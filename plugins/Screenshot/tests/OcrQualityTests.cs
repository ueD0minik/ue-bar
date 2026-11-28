// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace UeBar.Modules.Screenshot.Tests;

/// <summary>
/// P2-5-Fix 3: how well the real Windows OCR reads typical screenshots (demo content, drawn with ClearType like
/// a real window: dark terminal, dark editor, light web page, slide, small text at 100 %, vocabulary list) –
/// with and without preparing the image. Error rate = wrong words per 100 (word edit distance to the expected
/// text). The recognized text must not be worse than Windows' own and must stay under 1 s. Which preparation ships is decided on the user's real screenshots (RealScreenshotTests).
/// </summary>
[Trait("Category", "Slow")]
public sealed partial class OcrQualityTests(ITestOutputHelper output)
{
    /// <summary>Picture, language it is written in.</summary>
    public static TheoryData<string, string> Pictures { get; } = new()
    {
        { "dark-terminal", "de" },
        { "dark-editor", "en" },
        { "light-web", "en" },
        { "slide", "en" },
        { "small-text", "de" },
        { "vocabulary", "de" },
        { "dark-terminal-16", "de" },
        { "dark-chat", "de" },
        { "small-list", "de" },
        { "dark-wide", "de" },
        { "light-wide", "de" },
    };

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "images", "quality");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    /// <summary>Words, with typographic quotes and dashes unified (drawing and recognition may differ there).</summary>
    internal static string[] Words(string text) =>
        Space().Split(text.Replace('„', '"').Replace('“', '"').Replace('”', '"').Replace('–', '-').Replace('—', '-').Trim())
            .Where(w => w.Length > 0).ToArray();

    /// <summary>Wrong words per 100: word edit distance (substituted, missing, extra) / expected words.</summary>
    internal static double ErrorRate(string expected, string actual)
    {
        var a = Words(expected);
        var b = Words(actual);
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1];
            next[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                next[j] = Math.Min(Math.Min(next[j - 1] + 1, row[j] + 1), row[j - 1] + (string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal) ? 0 : 1));
            }

            row = next;
        }

        return a.Length == 0 ? 0 : 100.0 * row[b.Length] / a.Length;
    }

    private static async Task<(double Errors, long Ms, OcrOutcome Outcome)?> MeasureAsync(string picture, string language, OcrPreparation preparation, bool exactLanguage = false)
    {
        var ocr = new WindowsOcr { Preparation = preparation };
        // The picture's language, else another Latin-script one (German reads English text and the other way round).
        var tag = ocr.Languages.FirstOrDefault(l => l.Tag.StartsWith(language, StringComparison.OrdinalIgnoreCase)).Tag
            ?? (exactLanguage ? null : ocr.Languages.FirstOrDefault(l => l.Tag.StartsWith("de", StringComparison.OrdinalIgnoreCase) || l.Tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Tag);
        if (tag is null)
        {
            return null; // that recognition language is not installed here (the build server has English only)
        }

        var image = await File.ReadAllBytesAsync(Path.Combine(Folder, picture + ".png"));
        var expected = await File.ReadAllTextAsync(Path.Combine(Folder, picture + ".txt"));
        await ocr.RecognizeAsync(image, tag, default); // warm up the engine – the first call loads it
        var clock = Stopwatch.StartNew();
        var outcome = await ocr.RecognizeAsync(image, tag, default);
        clock.Stop();
        Assert.True(outcome.Problem == OcrProblem.None, $"{outcome.Problem}: {outcome.Detail}");
        return (ErrorRate(expected, outcome.Text), clock.ElapsedMilliseconds, outcome);
    }

    /// <summary>The page structure of an expected text: lines per paragraph (paragraphs end at an empty line).</summary>
    internal static int[] Structure(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n").Select(p => p.Split('\n').Length)];

    [Theory]
    [MemberData(nameof(Pictures))]
    public async Task Recognized_text_is_not_worse_than_before_and_stays_under_a_second(string picture, string language)
    {
        if (await MeasureAsync(picture, language, OcrPreparation.Best) is not { } m)
        {
            return;
        }

        var expected = await File.ReadAllTextAsync(Path.Combine(Folder, picture + ".txt"));
        var before = ErrorRate(expected, m.Outcome.Text); // what Windows gives – the words of 1.4.1
        var after = ErrorRate(expected, m.Outcome.Formatted(OcrTextFormat.Lines));
        output.WriteLine($"{picture,-16} before {before,5:0.0} | after {after,5:0.0} wrong words per 100 | {m.Ms} ms");
        Assert.True(after <= before, $"{picture}: {after:0.0} now, {before:0.0} before");
        Assert.True(m.Ms < 1000, $"{picture}: {m.Ms} ms");
    }

    /// <summary>
    /// Running text only: code (dark-editor) and a list of short UI lines (small-list) are no running text – there
    /// "Lines as in the image" is the right format (whether text wrapped after its longest line cannot be seen).
    /// </summary>
    public static TheoryData<string, string> RunningText { get; } = new()
    {
        { "dark-terminal", "de" },
        { "dark-terminal-16", "de" },
        { "dark-chat", "de" },
        { "dark-wide", "de" },
        { "light-web", "en" },
        { "light-wide", "de" },
        { "slide", "en" },
        { "small-text", "de" },
        { "vocabulary", "de" },
    };

    [Theory]
    [MemberData(nameof(RunningText))]
    public async Task Paragraphs_lists_and_tables_come_out_as_in_the_picture(string picture, string language)
    {
        // Only with the picture's own recognition language – another one splits words and lines differently.
        if (await MeasureAsync(picture, language, OcrPreparation.Best, exactLanguage: true) is not { } m)
        {
            return;
        }

        var expected = await File.ReadAllTextAsync(Path.Combine(Folder, picture + ".txt"));
        var flow = m.Outcome.Formatted(OcrTextFormat.Flow);
        Assert.True(Structure(expected).SequenceEqual(Structure(flow)), $"{picture}: expected lines per paragraph {string.Join(",", Structure(expected))}, got {string.Join(",", Structure(flow))}:\n{flow}");
    }

    [Fact]
    public void Dark_images_are_found_and_turned_into_dark_text_on_light()
    {
        static byte[] Image(byte background, byte text, int textPixels, int total)
        {
            var bgra = new byte[total * 4];
            for (var i = 0; i < total; i++)
            {
                var v = i < textPixels ? text : background;
                (bgra[(i * 4) + 0], bgra[(i * 4) + 1], bgra[(i * 4) + 2], bgra[(i * 4) + 3]) = (v, v, v, 255);
            }

            return bgra;
        }

        var dark = Image(background: 30, text: 230, textPixels: 100, total: 1000);
        Assert.True(OcrImage.IsDark(dark));
        Assert.False(OcrImage.IsDark(Image(background: 250, text: 20, textPixels: 100, total: 1000)));

        OcrImage.InvertToGray(dark);
        Assert.Equal(0, dark[0]); // the light text is black now
        Assert.Equal(255, dark[999 * 4]); // the dark background white
    }

    [Fact]
    public void A_second_enlarged_pass_only_for_small_letters_and_within_the_limit()
    {
        Assert.Equal(3, OcrScale.Rescale(1, letter: 10, 1000, 500, 10_000));
        Assert.Null(OcrScale.Rescale(1, letter: 25, 1000, 500, 10_000)); // big enough
        Assert.Equal(OcrScale.MaxFactor, OcrScale.Rescale(1, letter: 2, 1000, 500, 10_000));
        Assert.Null(OcrScale.Rescale(2, letter: 15, 2600, 500, 5_200)); // the engine's limit allows no more
    }

    [Fact]
    public void The_error_rate_counts_wrong_missing_and_extra_words()
    {
        Assert.Equal(0, ErrorRate("ein Absatz hier", "ein  Absatz\nhier"));
        Assert.Equal(100.0 / 3, ErrorRate("ein Absatz hier", "ein Absau hier"), 3);
        Assert.Equal(100.0 / 3, ErrorRate("in 1,1 s", "in s"), 3);
        Assert.Equal(0, ErrorRate("„to achieve – erreichen“", "\"to achieve - erreichen\""));
    }
}
