// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Modules.Screenshot.Tests;

/// <summary>
/// P2-5-Fix 2: paragraphs of recognized text. Windows OCR gives lines; the structure comes back from the
/// positions – here with made-up boxes (exact) and with real test images (see <see cref="OcrLayoutImageTests"/>).
/// </summary>
public sealed class OcrLayoutTests
{
    private static readonly string N = Environment.NewLine;

    /// <summary>A line at (x, y) with letters <paramref name="height"/> pixels high; words 10 px per character.</summary>
    private static OcrLineBox Line(string text, double x, double y, double height = 20)
    {
        var words = new List<OcrWordBox>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var width = word.Length * height / 2;
            words.Add(new OcrWordBox(word, x, y, width, height));
            x += width + (height / 2);
        }

        return new OcrLineBox(text, words);
    }

    [Fact]
    public void A_clearly_larger_gap_starts_a_new_paragraph_and_lines_of_a_paragraph_are_joined()
    {
        OcrLineBox[] page =
        [
            Line("Sales grew in every region", 40, 100),
            Line("and the shop opened early.", 40, 128),
            Line("The team finished the", 40, 186),
            Line("orders in October.", 40, 214),
        ];

        Assert.Equal($"Sales grew in every region and the shop opened early.{N}{N}The team finished the orders in October.", OcrLayout.Format(page, OcrTextFormat.Flow));
        Assert.Equal($"Sales grew in every region{N}and the shop opened early.{N}{N}The team finished the{N}orders in October.", OcrLayout.Format(page, OcrTextFormat.Lines));
    }

    [Fact]
    public void Paragraphs_of_one_line_each_still_break_and_double_spaced_text_does_not()
    {
        OcrLineBox[] single = [Line("First point.", 40, 100), Line("Second point.", 40, 160), Line("Third point.", 40, 220)];
        Assert.Equal($"First point.{N}{N}Second point.{N}{N}Third point.", OcrLayout.Format(single, OcrTextFormat.Flow));

        OcrLineBox[] doubleSpaced = [Line("One sentence that goes", 40, 100), Line("on over two lines.", 40, 140), Line("And a third line.", 40, 180)];
        Assert.Equal("One sentence that goes on over two lines. And a third line.", OcrLayout.Format(doubleSpaced, OcrTextFormat.Flow));
    }

    [Fact]
    public void Short_lines_made_on_purpose_stay_lines_also_as_running_text()
    {
        // A vocabulary slide: no bullets, but every line ends long before the next word would have to wrap.
        OcrLineBox[] page =
        [
            Line("to achieve - erreichen", 40, 100),
            Line("to deserve - verdienen", 40, 128),
            Line("reliable - zuverlaessig", 40, 156),
            Line("the outcome - das Ergebnis", 40, 184),
            Line("to improve - verbessern", 40, 212),
        ];

        Assert.Equal(
            $"to achieve - erreichen{N}to deserve - verdienen{N}reliable - zuverlaessig{N}the outcome - das Ergebnis{N}to improve - verbessern",
            OcrLayout.Format(page, OcrTextFormat.Flow));
        Assert.All(OcrLayout.WantedBreaks(page).Skip(1), Assert.True);

        // Table rows: a wide gap between the columns.
        OcrLineBox[] table = [Table("Monday", "Maths", 100), Table("Tuesday", "English", 128), Table("Friday", "Sports", 156)];
        Assert.Equal($"Monday Maths{N}Tuesday English{N}Friday Sports", OcrLayout.Format(table, OcrTextFormat.Flow));

        static OcrLineBox Table(string left, string right, double y) => new(
            $"{left} {right}",
            [new OcrWordBox(left, 40, y, left.Length * 10, 20), new OcrWordBox(right, 300, y, right.Length * 10, 20)]);
    }

    [Fact]
    public void A_colon_now_and_then_does_not_turn_running_text_into_lines()
    {
        OcrLineBox[] page =
        [
            Line("Note: the meeting moves to", 40, 100),
            Line("the big room on the second", 40, 128),
            Line("floor and starts at nine.", 40, 156),
        ];

        Assert.Equal("Note: the meeting moves to the big room on the second floor and starts at nine.", OcrLayout.Format(page, OcrTextFormat.Flow));
    }

    [Fact]
    public void A_heading_in_larger_letters_is_its_own_paragraph()
    {
        OcrLineBox[] page = [Line("Quarterly review", 40, 30, 34), Line("Sales grew everywhere", 40, 76), Line("this quarter.", 40, 104)];

        Assert.Equal($"Quarterly review{N}{N}Sales grew everywhere this quarter.", OcrLayout.Format(page, OcrTextFormat.Flow));
    }

    [Fact]
    public void Hyphens_at_the_line_end_go_but_real_hyphens_stay()
    {
        Assert.Equal("Die Unternehmen wachsen", OcrLayout.Flow(["Die Unter-", "nehmen wachsen"]));
        Assert.Equal("the software development", OcrLayout.Flow(["the software devel-", "opment"]));
        Assert.Equal("Ein- und Ausgang", OcrLayout.Flow(["Ein-", "und Ausgang"]));
        Assert.Equal("eine E-Mail schreiben", OcrLayout.Flow(["eine E-", "Mail schreiben"]));
        Assert.Equal("Preis 10 - 20 Euro", OcrLayout.Flow(["Preis 10 -", "20 Euro"]));
    }

    [Fact]
    public void Bullets_and_numbers_always_start_a_new_line_and_a_list_stays_together()
    {
        OcrLineBox[] page =
        [
            Line("• Warm jacket", 40, 100),
            Line("• Charger for the phone, plus", 40, 128),
            Line("the long cable", 64, 156),
            Line("- Water bottle", 40, 184),
            Line("1. Check the train", 40, 242),
            Line("2. Lock the door", 40, 270),
            Line("a) first b) second", 40, 298),
        ];

        Assert.Equal(
            $"• Warm jacket{N}• Charger for the phone, plus the long cable{N}- Water bottle{N}1. Check the train{N}2. Lock the door{N}a) first b) second",
            OcrLayout.Format(page, OcrTextFormat.Flow));
        Assert.True(OcrLayout.IsBullet("* item"));
        Assert.True(OcrLayout.IsBullet("– item"));
        Assert.True(OcrLayout.IsBullet("12) item"));
        Assert.False(OcrLayout.IsBullet("-5 degrees"));
        Assert.False(OcrLayout.IsBullet("2026 was a good year"));
    }

    [Fact]
    public void Centered_lines_are_not_taken_for_indents_but_a_real_indent_starts_a_paragraph()
    {
        OcrLineBox[] centered = [Line("A short centered", 300, 100), Line("title over two lines that is wider", 220, 128)];
        Assert.Single(OcrLayout.Paragraphs(centered));

        OcrLineBox[] indented = [Line("Normal text at the margin", 40, 100), Line("Quote far inside", 140, 128)];
        Assert.Equal(2, OcrLayout.Paragraphs(indented).Count);
    }

    [Fact]
    public void Two_columns_are_read_one_after_the_other()
    {
        OcrLineBox[] page =
        [
            Line("The left column starts", 40, 40),
            Line("The right column is", 560, 40),
            Line("here and goes on.", 40, 68),
            Line("read after the left.", 560, 68),
        ];

        Assert.Equal($"The left column starts here and goes on.{N}{N}The right column is read after the left.", OcrLayout.Format(page, OcrTextFormat.Flow));
    }

    [Fact]
    public void Regression_a_quoted_list_in_running_text_does_not_turn_the_paragraph_into_lines()
    {
        // P2-5-Fix 3: one " - " (and a ": " in another line) made 1.4.1 keep every line break of the paragraph.
        OcrLineBox[] page =
        [
            Line("Im Praxistest ist dabei ein Fehler aufgefallen: Deine Vokabelfolie", 40, 100),
            Line("wurde im Fließtext zu einer einzigen Zeile.", 40, 128),
            Line("Das habe ich korrigiert. Zeilen, die wie eine Tabelle oder", 40, 186),
            Line("Vokabelliste aufgebaut sind („to achieve - erreichen“), bleiben", 40, 214),
            Line("jetzt Zeilen.", 40, 242),
        ];

        Assert.Equal(
            $"Im Praxistest ist dabei ein Fehler aufgefallen: Deine Vokabelfolie wurde im Fließtext zu einer einzigen Zeile.{N}{N}Das habe ich korrigiert. Zeilen, die wie eine Tabelle oder Vokabelliste aufgebaut sind („to achieve - erreichen“), bleiben jetzt Zeilen.",
            OcrLayout.Format(page, OcrTextFormat.Flow));
    }

    /// <summary>A bullet as Windows returns it: its own line, with a smaller box a little lower than the text.</summary>
    private static OcrLineBox Dot(double x, double y) => new("•", [new OcrWordBox("•", x, y + 6, 8, 8)]);

    [Fact]
    public void Regression_a_wrapped_list_item_stays_one_item_without_empty_lines_and_keeps_its_number()
    {
        // Windows' order: the text first, the bullet after it; the second row of item 1 comes in two pieces.
        OcrLineBox[] page =
        [
            Line("Deine Daten und die Plugins: Mit einer Kopie (auch mit HA an)", 64, 100),
            Dot(40, 100),
            Line("startet dieselbe 1.4.0 in", 64, 128),
            Line("1,1 s.", 324, 128),
            Line("Netzwerk-, Signatur- und Zertifikatsprüfungen: nichts.", 64, 156),
            Dot(40, 156),
            Line("Eine zweite laufende Instanz oder fremde Plugins.", 64, 184),
            Dot(40, 184),
        ];

        Assert.Equal(
            $"• Deine Daten und die Plugins: Mit einer Kopie (auch mit HA an) startet dieselbe 1.4.0 in 1,1 s.{N}• Netzwerk-, Signatur- und Zertifikatsprüfungen: nichts.{N}• Eine zweite laufende Instanz oder fremde Plugins.",
            OcrLayout.Format(page, OcrTextFormat.Flow));
        Assert.Equal(
            $"• Deine Daten und die Plugins: Mit einer Kopie (auch mit HA an){N}startet dieselbe 1.4.0 in 1,1 s.{N}• Netzwerk-, Signatur- und Zertifikatsprüfungen: nichts.{N}• Eine zweite laufende Instanz oder fremde Plugins.",
            OcrLayout.Format(page, OcrTextFormat.Lines));
    }

    [Fact]
    public void A_space_only_where_the_words_really_have_a_gap()
    {
        // Windows: "hast ." and "Vokabel liste" – the boxes touch.
        OcrWordBox[] words =
        [
            new("Du", 0, 0, 20, 20), new("hast", 30, 0, 40, 20), new(".", 71, 0, 4, 20),
            new("Vokabel", 85, 0, 70, 20), new("liste", 156, 0, 50, 20), new("gesehen", 216, 0, 70, 20),
        ];

        Assert.Equal("Du hast. Vokabelliste gesehen", OcrLayout.Compose(new OcrLineBox("Du hast . Vokabel liste gesehen", words)));
        Assert.Equal("text", OcrLayout.Compose(new OcrLineBox("text", [new OcrWordBox("text", 0, 0, 40, 20)])));
    }

    [Fact]
    public void Punctuation_on_its_own_sticks_to_its_word()
    {
        // P2-5-Fix 4, real screenshot: Windows returned "genannt" and "," as two words with a normal gap.
        OcrWordBox[] words =
        [
            new("wie", 0, 0, 30, 20), new("genannt", 40, 0, 70, 20), new(",", 118, 0, 4, 20),
            new("obwohl", 130, 0, 60, 20), new("(", 200, 0, 6, 20), new("Planung", 214, 0, 70, 20), new(")", 292, 0, 6, 20),
        ];

        Assert.Equal("wie genannt, obwohl (Planung)", OcrLayout.Compose(new OcrLineBox("wie genannt , obwohl ( Planung )", words)));
    }

    [Fact]
    public void Pieces_far_apart_on_one_height_stay_columns()
    {
        OcrLineBox[] page = [Line("left column", 40, 100), Line("right column", 600, 100)];
        Assert.Equal(2, OcrLayout.Rows([.. page]).Count);
    }

    [Fact]
    public void A_line_without_descenders_is_not_taken_for_smaller_type()
    {
        // Same size, but the second line has no letters below the baseline: its tallest box is lower.
        OcrLineBox[] page =
        [
            Line("Start early on Saturday and take the", 40, 100, 20),
            Line("connection to the lake", 40, 128, 15) with { Words = [.. Line("connection to the lake", 40, 128).Words.Select(w => w with { Height = 15 })] },
        ];

        Assert.Single(OcrLayout.Paragraphs(page));
    }

    [Fact]
    public void No_boxes_means_the_plain_text_of_windows()
    {
        var outcome = new OcrOutcome($"a{N}b", "en-US");
        Assert.Equal($"a{N}b", outcome.Formatted(OcrTextFormat.Flow));
        Assert.Equal("", OcrLayout.Format([], OcrTextFormat.Flow));
    }
}

/// <summary>P2-5-Fix 2: the real Windows OCR on test slides (English – the build server has English only).</summary>
public sealed class OcrLayoutImageTests
{
    private static readonly WindowsOcr Ocr = new();

    private static async Task<OcrOutcome?> RecognizeAsync(string image)
    {
        // English, else German (it reads these English slides just as well) – so the tests run on every PC.
        var english = Ocr.Languages.FirstOrDefault(l => l.Tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Tag
            ?? Ocr.Languages.FirstOrDefault(l => l.Tag.StartsWith("de", StringComparison.OrdinalIgnoreCase)).Tag;
        if (english is null)
        {
            return null; // no English recognition on this PC – covered by the made-up boxes above
        }

        var outcome = await Ocr.RecognizeAsync(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", image)), english, default);
        Assert.True(outcome.HasText, $"{outcome.Problem}: {outcome.Detail}");
        Assert.NotEmpty(outcome.Lines);
        return outcome;
    }

    private static string[] Paragraphs(string text) => text.Split(Environment.NewLine + Environment.NewLine);

    [Fact]
    public async Task A_slide_gives_heading_and_three_paragraphs_with_the_hyphen_removed()
    {
        if (await RecognizeAsync("ocr-paragraphs.png") is not { } outcome)
        {
            return;
        }

        var flow = Paragraphs(outcome.Formatted(OcrTextFormat.Flow));
        Assert.Equal(4, flow.Length);
        Assert.Equal("Quarterly review", flow[0]);
        Assert.DoesNotContain(Environment.NewLine, flow[1], StringComparison.Ordinal);
        Assert.Contains("development for the online orders", flow[2], StringComparison.Ordinal);
        Assert.EndsWith("We will report again in January.", flow[3], StringComparison.Ordinal);

        var lines = Paragraphs(outcome.Formatted(OcrTextFormat.Lines));
        Assert.Equal(4, lines.Length);
        Assert.Equal(3, lines[3].Split(Environment.NewLine).Length); // every line stays a line
        Assert.EndsWith("devel-", lines[2].Split(Environment.NewLine)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_keeps_one_item_per_line_and_joins_a_wrapped_item()
    {
        if (await RecognizeAsync("ocr-list.png") is not { } outcome)
        {
            return;
        }

        var flow = outcome.Formatted(OcrTextFormat.Flow);
        var parts = Paragraphs(flow);
        Assert.Equal("Packing list", parts[0]);
        var items = parts[1].Split(Environment.NewLine);
        Assert.Equal(6, items.Length);
        Assert.EndsWith("plus the long cable", items[1], StringComparison.Ordinal);
        Assert.StartsWith("1.", items[3], StringComparison.Ordinal);
        Assert.StartsWith("3.", items[5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_columns_are_not_mixed()
    {
        if (await RecognizeAsync("ocr-columns.png") is not { } outcome)
        {
            return;
        }

        var parts = Paragraphs(outcome.Formatted(OcrTextFormat.Flow));
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("The left column starts", parts[0], StringComparison.Ordinal);
        Assert.StartsWith("The right column is", parts[1], StringComparison.Ordinal);
    }
}
