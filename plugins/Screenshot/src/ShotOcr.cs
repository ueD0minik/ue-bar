// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

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
            var factor = OcrScale.Factor((int)decoder.PixelWidth, (int)decoder.PixelHeight, max);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * factor)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * factor)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
                .AsTask(cancellationToken).ConfigureAwait(false);
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            var text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
            var lines = result.Lines
                .Select(l => new OcrLineBox(l.Text, [.. l.Words.Select(w => new OcrWordBox(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))]))
                .ToList();
            return new OcrOutcome(text, engine.RecognizerLanguage?.LanguageTag) { Lines = lines };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new OcrOutcome("", engine.RecognizerLanguage?.LanguageTag, OcrProblem.Failed, ex.Message);
        }
    }
}

/// <summary>How much an image is enlarged before recognition (WinRT-free, testable).</summary>
internal static class OcrScale
{
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
