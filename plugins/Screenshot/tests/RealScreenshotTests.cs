// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using Windows.Graphics.Imaging;
using Xunit.Abstractions;

namespace UeBar.Modules.Screenshot.Tests;

/// <summary>
/// P2-5-Fix 4: the text recognition measured on real screenshots of the user (Claude Code, Word, a web page, a
/// list). They stay in the private main folder (<c>docs/ocr-testbilder</c>, next to the app folder) and are never
/// part of this repository – without them (build server) these tests do nothing. Each picture has its expected
/// text as <c>&lt;name&gt;.txt</c>. Error rate = wrong words per 100, in any order (a page with columns may be read in
/// another order – that is not a recognition error); formatting marks and symbols (¶ · ¤ • ☐ ↗) do not count.
/// </summary>
public sealed class RealScreenshotTests(ITestOutputHelper output)
{
    /// <summary>The folder with the real screenshots, or null.</summary>
    internal static string? Folder()
    {
        if (Environment.GetEnvironmentVariable("UEBAR_OCR_SAMPLES") is { Length: > 0 } custom && Directory.Exists(custom))
        {
            return custom;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "ocr-testbilder");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.txt").Any())
            {
                return candidate;
            }
        }

        return null;
    }

    internal static IReadOnlyList<(string Name, byte[] Image, string Expected)> Pictures()
    {
        if (Folder() is not { } folder)
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "*.png")
            .Where(p => File.Exists(Path.ChangeExtension(p, ".txt")))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path.GetFileNameWithoutExtension(p), File.ReadAllBytes(p), File.ReadAllText(Path.ChangeExtension(p, ".txt"))))];
    }

    private static readonly string[] Symbols = ["¶", "¤", "•", "☐", "↗"];

    internal static string[] Words(string text)
    {
        var clean = text.Replace('·', ' ').Replace("…", "...", StringComparison.Ordinal); // the same on screen
        foreach (var symbol in Symbols)
        {
            clean = clean.Replace(symbol, " ", StringComparison.Ordinal);
        }

        return OcrQualityTests.Words(clean);
    }

    /// <summary>Wrong words per 100, order ignored: words of the expected text not found + extra words, counted like substitutions.</summary>
    internal static double ErrorRate(string expected, string actual)
    {
        var want = Words(expected);
        var got = Words(actual);
        var pool = got.GroupBy(w => w, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var matched = 0;
        foreach (var word in want)
        {
            if (pool.TryGetValue(word, out var n) && n > 0)
            {
                pool[word] = n - 1;
                matched++;
            }
        }

        return want.Length == 0 ? 0 : 100.0 * (Math.Max(want.Length, got.Length) - matched) / want.Length;
    }

    private static async Task<(double Errors, long Ms, OcrOutcome Outcome)> MeasureAsync(byte[] image, string expected, OcrPreparation preparation)
    {
        var ocr = new WindowsOcr { Preparation = preparation };
        var tag = ocr.Languages.FirstOrDefault(l => l.Tag.StartsWith("de", StringComparison.OrdinalIgnoreCase)).Tag;
        var clock = Stopwatch.StartNew();
        var outcome = await ocr.RecognizeAsync(image, tag, default);
        clock.Stop();
        return (ErrorRate(expected, outcome.Formatted(OcrTextFormat.Lines)), clock.ElapsedMilliseconds, outcome);
    }

    private static bool HasGerman() => new WindowsOcr().Languages.Any(l => l.Tag.StartsWith("de", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task The_shipped_preparation_reads_the_real_screenshots_best_and_under_a_second()
    {
        var pictures = Pictures();
        if (pictures.Count == 0 || !HasGerman())
        {
            return; // the private screenshots are not here (build server) – nothing to measure
        }

        await MeasureAsync(pictures[0].Image, pictures[0].Expected, OcrPreparation.Best); // warm up the engine
        (string Label, OcrPreparation Preparation)[] variants =
        [
            ("shipped (linear)", OcrPreparation.Best),
            ("Fant (1.4.2)", OcrPreparation.None),
            ("cubic", OcrPreparation.Best with { Interpolation = BitmapInterpolationMode.Cubic }),
            ("grey", OcrPreparation.Best with { Grayscale = true }),
            ("invert dark", OcrPreparation.Best with { InvertDark = true }),
            ("second pass", OcrPreparation.Best with { RescaleSmallText = true }),
        ];
        var totals = new List<(string Label, double PerHundred)>();
        foreach (var (label, preparation) in variants)
        {
            double wrong = 0, words = 0;
            var line = new System.Text.StringBuilder();
            foreach (var (name, image, expected) in pictures)
            {
                var m = await MeasureAsync(image, expected, preparation);
                var n = Words(expected).Length;
                wrong += m.Errors * n / 100;
                words += n;
                line.Append($" | {name} {m.Errors:0.0} ({m.Ms} ms)");
                if (label.StartsWith("shipped", StringComparison.Ordinal))
                {
                    Assert.True(m.Ms < 1000, $"{name}: {m.Ms} ms");
                }
            }

            totals.Add((label, 100 * wrong / words));
            output.WriteLine($"{label,-18} {100 * wrong / words,5:0.0} wrong words per 100{line}");
        }

        // Before = 1.4.2: Fant and the text as Windows gives it.
        foreach (var (name, image, expected) in pictures)
        {
            var before = await MeasureAsync(image, expected, OcrPreparation.None);
            var after = await MeasureAsync(image, expected, OcrPreparation.Best);
            output.WriteLine($"{name,-24} before {ErrorRate(expected, before.Outcome.Text),5:0.0} → after {after.Errors,5:0.0}");
        }

        Assert.All(totals, t => Assert.True(totals[0].PerHundred <= t.PerHundred, $"{t.Label} reads better ({t.PerHundred:0.0}) than what ships ({totals[0].PerHundred:0.0})"));
    }

    [Fact]
    public async Task Regression_a_bullet_in_front_of_a_quote_is_kept()
    {
        // Claude Code: "• „Absatz“ → „Absau“ …" – with Fant scaling Windows did not see the "•" at all.
        var picture = Pictures().FirstOrDefault(p => p.Expected.Contains("• „Absatz“", StringComparison.Ordinal));
        if (picture.Image is null || !HasGerman())
        {
            return;
        }

        var m = await MeasureAsync(picture.Image, picture.Expected, OcrPreparation.Best);
        var line = m.Outcome.Formatted(OcrTextFormat.Flow).Split(Environment.NewLine).First(l => l.Contains("Absatz", StringComparison.Ordinal));
        Assert.StartsWith("• ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_error_rate_ignores_order_formatting_marks_and_the_look_of_three_dots()
    {
        Assert.Equal(0, ErrorRate("Shop Produkte Support", "Support Shop Produkte"));
        Assert.Equal(0, ErrorRate("Ich·bin¶ • ☐ klar …", "Ich bin klar ..."));
        Assert.Equal(25, ErrorRate("Kern ist jetzt fertig", "Kem ist jetzt fertig"));
        Assert.Equal(100, ErrorRate("ist jetzt", "istjetzt")); // both words are wrong
    }
}