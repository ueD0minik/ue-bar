// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace UeBar.Modules.Screenshot;

/// <summary>Why text recognition gave nothing.</summary>
internal enum OcrProblem
{
    None,

    /// <summary>No recognition language of Windows fits (none installed, or the chosen one is missing).</summary>
    NoLanguage,

    /// <summary>The image could not be read or recognition failed.</summary>
    Failed,
}

/// <summary>Result of the text recognition: the lines as text, the language used.</summary>
internal sealed record OcrOutcome(string Text, string? Language, OcrProblem Problem = OcrProblem.None, string? Detail = null)
{
    public bool HasText => Problem == OcrProblem.None && !string.IsNullOrWhiteSpace(Text);

    /// <summary>The lines with the boxes of their words (P2-5-Fix 2) – for <see cref="OcrLayout"/>; empty = only <see cref="Text"/>.</summary>
    public IReadOnlyList<OcrLineBox> Lines { get; init; } = [];

    /// <summary>The text in the wanted format: paragraphs rebuilt from the boxes when there are any.</summary>
    public string Formatted(OcrTextFormat format) => Lines.Count > 0 ? OcrLayout.Format(Lines, format) : Text;

    /// <summary>How much the image was enlarged for recognition (P2-5-Fix 3, for the log).</summary>
    public double Scale { get; init; } = 1;

    /// <summary>The image was dark and was inverted before recognition.</summary>
    public bool Inverted { get; init; }
}

/// <summary>Text recognition (tests use a fake).</summary>
internal interface IShotOcr
{
    /// <summary>Recognition languages installed in Windows (tag, display name).</summary>
    IReadOnlyList<(string Tag, string Name)> Languages { get; }

    /// <summary><paramref name="language"/> null = the user's Windows languages (the first one Windows can recognize).</summary>
    Task<OcrOutcome> RecognizeAsync(byte[] image, string? language, CancellationToken cancellationToken);
}

/// <summary>
/// The text recognition built into Windows (Windows.Media.Ocr): offline, languages = the recognition
/// packages of the installed Windows languages. Small images are enlarged first – the engine reads text
/// from about 20 pixels height reliably. Callable from the thread pool (the WinRT objects are agile).
/// </summary>
internal sealed class WindowsOcr : IShotOcr
{
    public IReadOnlyList<(string Tag, string Name)> Languages
    {
        get
        {
            try
            {
                return [.. OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName))];
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or TypeLoadException)
            {
                return [];
            }
        }
    }

    /// <summary>How the image is prepared before recognition (P2-5-Fix 3; tests measure the variants).</summary>
    public OcrPreparation Preparation { get; init; } = OcrPreparation.Best;

    public async Task<OcrOutcome> RecognizeAsync(byte[] image, string? language, CancellationToken cancellationToken)
    {
        OcrEngine? engine;
        try
        {
            engine = language is { Length: > 0 } tag
                ? OcrEngine.IsLanguageSupported(new Windows.Globalization.Language(tag)) ? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag)) : null
                : OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (ArgumentException ex)
        {
            return new OcrOutcome("", language, OcrProblem.NoLanguage, ex.Message);
        }

        if (engine is null)
        {
            return new OcrOutcome("", language, OcrProblem.NoLanguage);
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(image);
                await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
                writer.DetachStream();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            var max = (int)OcrEngine.MaxImageDimension;
            var width = (int)decoder.PixelWidth;
            var height = (int)decoder.PixelHeight;
            var factor = Preparation.Factor ?? OcrScale.Factor(width, height, max);
            var (result, dark) = await RecognizeScaledAsync(engine, decoder, factor, null, cancellationToken).ConfigureAwait(false);

            // P2-5-Fix 3: small letters are read badly – then once more, enlarged until they are about
            // OcrScale.TargetLetter pixels high (within the engine's limit). Everything stays in memory.
            if (Preparation.RescaleSmallText && OcrScale.Rescale(factor, LetterHeight(result), width, height, max) is { } better)
            {
                (result, _) = await RecognizeScaledAsync(engine, decoder, better, dark, cancellationToken).ConfigureAwait(false);
                factor = better;
            }

            var text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
            var lines = result.Lines
                .Select(l => new OcrLineBox(l.Text, [.. l.Words.Select(w => new OcrWordBox(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))]))
                .ToList();
            return new OcrOutcome(text, engine.RecognizerLanguage?.LanguageTag) { Lines = lines, Scale = factor, Inverted = dark };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new OcrOutcome("", engine.RecognizerLanguage?.LanguageTag, OcrProblem.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Decodes at <paramref name="factor"/> (good quality), turns a dark image into dark text on light (if
    /// prepared so; <paramref name="dark"/> = decided before, null = look at the image) and recognizes.
    /// </summary>
    private async Task<(OcrResult Result, bool Dark)> RecognizeScaledAsync(OcrEngine engine, BitmapDecoder decoder, double factor, bool? dark, CancellationToken cancellationToken)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * factor)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * factor)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (!Preparation.InvertDark)
        {
            return (await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false), false);
        }

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        var isDark = dark ?? OcrImage.IsDark(pixels);
        if (!isDark)
        {
            return (await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false), false);
        }

        OcrImage.InvertToGray(pixels, Preparation.Stretch);
        using var prepared = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, bitmap.PixelWidth, bitmap.PixelHeight, BitmapAlphaMode.Premultiplied);
        return (await engine.RecognizeAsync(prepared).AsTask(cancellationToken).ConfigureAwait(false), true);
    }

    /// <summary>Typical letter height of a result: the median of the tallest word of every line.</summary>
    private static double LetterHeight(OcrResult result) =>
        OcrLayout.Median(result.Lines.Where(l => l.Words.Count > 0).Select(l => l.Words.Max(w => w.BoundingRect.Height)));
}

/// <summary>
/// Pixel work before recognition (P2-5-Fix 3), on BGRA bytes in memory – WinRT-free, testable. Windows OCR
/// reads dark text on a light background much better than light text on a dark design.
/// </summary>
internal static class OcrImage
{
    /// <summary>Mostly dark: the median brightness (sampled) is below 45 %.</summary>
    public static bool IsDark(ReadOnlySpan<byte> bgra)
    {
        var histogram = new int[256];
        var samples = 0;
        var step = Math.Max(4, (bgra.Length / 4 / 50_000) * 4); // at most ~50 000 samples
        for (var i = 0; i + 3 < bgra.Length; i += step)
        {
            histogram[Luma(bgra[i + 2], bgra[i + 1], bgra[i])]++;
            samples++;
        }

        if (samples == 0)
        {
            return false;
        }

        var half = samples / 2;
        var seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen > half)
            {
                return v < 115;
            }
        }

        return false;
    }

    /// <summary>
    /// Grey, inverted (light text on dark → dark text on light) and the contrast stretched so the darkest 1 %
    /// becomes black and the lightest 1 % white. In place.
    /// </summary>
    public static void InvertToGray(Span<byte> bgra, double stretch = 1)
    {
        var histogram = new int[256];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var v = (byte)(255 - Luma(bgra[i + 2], bgra[i + 1], bgra[i]));
            bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
            bgra[i + 3] = 255;
            histogram[v]++;
        }

        var total = bgra.Length / 4;
        var (low, high) = (Percentile(histogram, total, 0.01), Percentile(histogram, total, 0.99));
        if (stretch <= 0 || high - low < 32)
        {
            return; // nearly flat: leave it
        }

        var map = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            var full = Math.Clamp((v - low) * 255.0 / (high - low), 0, 255);
            map[v] = (byte)Math.Round(v + ((full - v) * Math.Min(stretch, 1)));
        }

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            bgra[i] = bgra[i + 1] = bgra[i + 2] = map[bgra[i]];
        }
    }

    private static int Percentile(int[] histogram, int total, double share)
    {
        var target = total * share;
        var seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen >= target)
            {
                return v;
            }
        }

        return 255;
    }

    private static byte Luma(byte r, byte g, byte b) => (byte)(((r * 299) + (g * 587) + (b * 114)) / 1000);
}

/// <summary>What is done to the image before recognition (P2-5-Fix 3).</summary>
/// <param name="InvertDark">A mostly dark image (dark design) becomes dark text on light grey, contrast stretched.</param>
/// <param name="RescaleSmallText">Small letters: a second pass, enlarged.</param>
internal sealed record OcrPreparation(bool InvertDark, bool RescaleSmallText)
{
    /// <summary>Contrast after inverting: 0 = none, 1 = darkest/lightest 1 % to black/white (tests compare).</summary>
    public double Stretch { get; init; } = 1;

    /// <summary>Tests only: decode at this factor instead of the size-based one.</summary>
    public double? Factor { get; init; }

    public static OcrPreparation None { get; } = new(false, false);

    /// <summary>
    /// What ships: measured on 11 test screenshots (OcrQualityTests), neither inverting dark images nor a second,
    /// enlarged pass read better overall (inverting 4.8 → 5.2, enlarging → 5.2 wrong words per 100; enlarging
    /// made whole-window captures worse). Both stay switchable for measuring new screenshots.
    /// </summary>
    public static OcrPreparation Best { get; } = new(false, false);
}

/// <summary>How much an image is enlarged before recognition (WinRT-free, testable).</summary>
internal static class OcrScale
{
    /// <summary>Letter height (pixels) the engine reads best at.</summary>
    public const double TargetLetter = 30;

    /// <summary>Most an image is ever enlarged.</summary>
    public const double MaxFactor = 4;

    /// <summary>
    /// A second pass is worth it when the letters came out smaller than 2/3 of the target: the factor that
    /// brings them to the target, capped by <see cref="MaxFactor"/> and the engine's limit. Null = keep.
    /// </summary>
    public static double? Rescale(double factor, double letter, int width, int height, int maxDimension)
    {
        if (letter <= 0 || letter >= TargetLetter * 2 / 3 || width <= 0 || height <= 0)
        {
            return null;
        }

        var wanted = Math.Min(factor * TargetLetter / letter, MaxFactor);
        var limit = maxDimension / (double)Math.Max(width, height);
        var next = Math.Min(wanted, limit);
        return next > factor * 1.2 ? next : null;
    }
    /// <summary>Small images twice (thrice when tiny), never beyond the engine's limit; too large ones shrink to it.</summary>
    public static double Factor(int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0 || maxDimension <= 0)
        {
            return 1;
        }

        var largest = Math.Max(width, height);
        var wanted = largest < 400 ? 3.0 : largest < 1600 ? 2.0 : 1.0;
        return Math.Min(wanted, maxDimension / (double)largest);
    }
}
