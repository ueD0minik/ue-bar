// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text;
using System.Text.RegularExpressions;

namespace UeBar.Modules.Screenshot;

/// <summary>How recognized text is written out (setting "Format of the recognized text").</summary>
internal enum OcrTextFormat
{
    /// <summary>The lines of a paragraph joined to one paragraph, hyphens at the line end removed.</summary>
    Flow,

    /// <summary>Every recognized line stays a line.</summary>
    Lines,
}

/// <summary>A word as Windows found it: text and box in pixels of the recognized image.</summary>
internal readonly record struct OcrWordBox(string Text, double X, double Y, double Width, double Height);

/// <summary>A line as Windows found it: its text and its words with their boxes.</summary>
internal sealed record OcrLineBox(string Text, IReadOnlyList<OcrWordBox> Words)
{
    public double Left => Words.Count == 0 ? 0 : Words.Min(w => w.X);

    public double Right => Words.Count == 0 ? 0 : Words.Max(w => w.X + w.Width);

    public double Top => Words.Count == 0 ? 0 : Words.Min(w => w.Y);

    public double Bottom => Words.Count == 0 ? 0 : Words.Max(w => w.Y + w.Height);

    /// <summary>
    /// Height of the letters: the tallest word box (P2-5-Fix 3). Windows fits each box to its letters, so "in s."
    /// is much lower than "Daten" – the median made a short line look like smaller type and broke paragraphs.
    /// </summary>
    public double Height => Words.Count == 0 ? 0 : Words.Max(w => w.Height);

    public double Center => (Left + Right) / 2;

    /// <summary>
    /// Width per character (median of the words) – a measure of the type size that does not depend on which
    /// letters reach up or down (P2-5-Fix 3: a line without descenders looked like smaller type).
    /// </summary>
    public double CharWidth
    {
        get
        {
            var words = Words.Where(w => w.Text.Length >= 2).ToList();
            return OcrLayout.Median((words.Count > 0 ? words : Words).Select(w => w.Width / Math.Max(1, w.Text.Length)));
        }
    }
}

/// <summary>
/// Gets the structure of a page back from the recognized lines and their positions (P2-5-Fix 2): Windows OCR
/// gives lines, not paragraphs. A new paragraph starts where the gap to the line above is clearly larger
/// than the usual line pitch (median, not a fixed number of pixels), where the letter size changes (a
/// heading) or where the indent changes (unless the text is centered). Lines starting with a bullet or a
/// number ("•", "-", "1.", "a)") always start a new line. As running text, lines are joined only where the
/// text wrapped: lines built like a table or vocabulary list ("to achieve - erreichen", wide gaps) and lines
/// that end although the next word would have fitted stay lines. Columns side by side are read one after the other
/// when a clear vertical gap separates them; more complex layouts (tables, text flowing around pictures)
/// are not reconstructed. WinRT-free, so it can be tested with made-up boxes.
/// </summary>
internal static partial class OcrLayout
{
    /// <summary>Conjunctions after a hyphen ("in- and output", German and English) – there the hyphen stays.</summary>
    private static readonly HashSet<string> KeepHyphenBefore = new(StringComparer.OrdinalIgnoreCase) { "und", "oder", "bis", "sowie", "bzw.", "and", "or", "to", "nor" };

    [GeneratedRegex(@"^(?:[•‣◦▪▫●○■□➢➤►▶✓✔·]|[-–—*](?=\s)|\(?\d{1,3}[.)](?=\s)|\(?[a-zA-Z][)](?=\s))")]
    private static partial Regex BulletPattern();

    public static bool IsBullet(string line) => BulletPattern().IsMatch(line.TrimStart());

    [GeneratedRegex(@"\S\s+(?<sep>[-–—=]|->|→|=>)\s+\S|\S(?<sep>:)\s+\S")]
    private static partial Regex SeparatorPattern();

    /// <summary>
    /// What makes a line look like a table or vocabulary row (P2-5-Fix 3): its separator ("-" for every dash,
    /// ":", "=", "→") or a wide gap between two words with its x position ("gap@…"). Null = plain text. Only
    /// several rows in a row with the same kind count as a table – one " - " in running text never does.
    /// </summary>
    internal static string? StructureOf(OcrLineBox line, double letter)
    {
        if (SeparatorPattern().Match(line.Text) is { Success: true } match)
        {
            var sep = match.Groups["sep"].Value;
            return sep is "-" or "–" or "—" ? "-" : sep is "->" or "=>" ? "→" : sep;
        }

        for (var i = 1; i < line.Words.Count; i++)
        {
            var end = line.Words[i - 1].X + line.Words[i - 1].Width;
            if (line.Words[i].X - end > 1.5 * letter)
            {
                return $"gap@{line.Words[i].X}";
            }
        }

        return null;
    }

    /// <summary>Two structure kinds belong to one table: same separator, or a column gap at about the same x.</summary>
    private static bool SameStructure(string? a, string? b, double letter)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (a.StartsWith("gap@", StringComparison.Ordinal) && b.StartsWith("gap@", StringComparison.Ordinal))
        {
            return Math.Abs(double.Parse(a[4..], System.Globalization.CultureInfo.InvariantCulture) - double.Parse(b[4..], System.Globalization.CultureInfo.InvariantCulture)) <= 1.5 * letter;
        }

        return a == b;
    }

    /// <summary>Where the text of a list item starts: after the bullet ("•" as its own word) – the hanging indent.</summary>
    internal static double ItemTextLeft(OcrLineBox line) =>
        line.Words.Count > 1 && IsBullet(line.Words[0].Text + " x") ? line.Words[1].X : line.Left;

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0 && !double.IsNaN(v)).Order().ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The page as text in the chosen format.</summary>
    public static string Format(IReadOnlyList<OcrLineBox> lines, OcrTextFormat format)
    {
        var text = new StringBuilder();
        List<string>? previous = null;
        foreach (var boxes in ParagraphBoxes(lines))
        {
            var paragraph = boxes.Select(l => l.Text.Trim()).ToList();
            if (previous is not null)
            {
                // Items of one list stay together; everything else gets an empty line in between.
                var listGoesOn = IsBullet(previous[^1]) && IsBullet(paragraph[0]);
                text.Append(listGoesOn ? "\n" : "\n\n");
            }

            text.Append(format == OcrTextFormat.Lines ? string.Join("\n", paragraph) : Flow(paragraph, WantedBreaks(boxes)));
            previous = paragraph;
        }

        return text.ToString().Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    /// <summary>The lines grouped to paragraphs, in reading order (column by column, top to bottom).</summary>
    public static IReadOnlyList<List<string>> Paragraphs(IReadOnlyList<OcrLineBox> lines) =>
        [.. ParagraphBoxes(lines).Select(p => p.Select(l => l.Text.Trim()).ToList())];

    private static List<List<OcrLineBox>> ParagraphBoxes(IReadOnlyList<OcrLineBox> lines)
    {
        var usable = Rows(lines.Where(l => l.Words.Count > 0 && !string.IsNullOrWhiteSpace(l.Text)).ToList());
        var result = new List<List<OcrLineBox>>();
        foreach (var column in Columns(usable))
        {
            result.AddRange(ParagraphsOf(column));
        }

        return result;
    }

    /// <summary>
    /// Line breaks inside a paragraph that stay in running text (P2-5-Fix 3). Index i = the break before line i.
    /// <list type="bullet">
    /// <item>A line that reaches the right edge of the paragraph – the next word would not have fitted – wrapped:
    /// joined, whatever characters it holds.</item>
    /// <item>A line that ends clearly earlier (the next word would have fitted) ended on purpose: the break stays.</item>
    /// <item>A table or vocabulary list – at least two rows in a row with the same separator or column gap, two
    /// of them short – keeps all its breaks, also after its longest row.</item>
    /// </list>
    /// </summary>
    internal static bool[] WantedBreaks(IReadOnlyList<OcrLineBox> paragraph)
    {
        var wanted = new bool[paragraph.Count];
        if (paragraph.Count < 2)
        {
            return wanted;
        }

        var right = paragraph.Max(l => l.Right);
        var letter = Math.Max(Median(paragraph.Select(l => l.Height)), 1);
        var full = new bool[paragraph.Count];
        for (var i = 1; i < paragraph.Count; i++)
        {
            var first = paragraph[i].Words[0];
            var space = Math.Max(first.Height * 0.3, letter * 0.3);
            full[i - 1] = paragraph[i - 1].Right + space + first.Width >= right - (letter * 0.5);
        }

        full[^1] = paragraph[^1].Right >= right - letter;
        double? itemLeft = null;
        for (var i = 0; i < paragraph.Count; i++)
        {
            var bullet = IsBullet(paragraph[i].Text);

            // A line at the text edge of the list item above (hanging indent) is that item going on – always joined.
            var continuesItem = !bullet && itemLeft is { } edge && Math.Abs(paragraph[i].Left - edge) <= letter;
            if (i > 0)
            {
                wanted[i] = !bullet && !continuesItem && !full[i - 1];
            }

            itemLeft = bullet ? ItemTextLeft(paragraph[i]) : continuesItem ? itemLeft : null;
        }

        // Tables and vocabulary: runs of rows with the same structure.
        var kinds = paragraph.Select(l => StructureOf(l, letter)).ToArray();
        var start = 0;
        while (start < paragraph.Count)
        {
            var end = start;
            while (end + 1 < paragraph.Count && SameStructure(kinds[start], kinds[end + 1], letter))
            {
                end++;
            }

            // Two rows in a row that end clearly before the right edge – wrapped text reaches it.
            var shortPair = false;
            for (var i = start; i < end; i++)
            {
                shortPair |= paragraph[i].Right < right - letter && paragraph[i + 1].Right < right - letter;
            }

            // A column gap at the same x in several rows never comes from wrapping – that alone is a table.
            var aligned = kinds[start]?.StartsWith("gap@", StringComparison.Ordinal) == true;
            if (end > start && (shortPair || aligned))
            {
                for (var i = start + 1; i <= end; i++)
                {
                    wanted[i] = true;
                }
            }

            start = end + 1;
        }

        return wanted;
    }

    /// <summary>
    /// Puts back together what is one line on the screen (P2-5-Fix 3): Windows sometimes returns a line in pieces
    /// – a bullet "•" as its own line (listed after its text, because its box starts lower), or a line split at
    /// a dash. Pieces whose heights overlap by at least half and that lie close side by side (gap below two
    /// letter heights – a column gap is wider) become one line, left to right. Nothing is dropped.
    /// </summary>
    internal static List<OcrLineBox> Rows(List<OcrLineBox> lines)
    {
        var rows = Merged(lines);
        return [.. rows.Select(r => r with { Text = Compose(r) })];
    }

    /// <summary>
    /// The text of a line from its words (P2-5-Fix 3): Windows puts a space between all word boxes, also where
    /// they touch – "hast .", "Netzwerk- ,", "Vokabel liste". A space only where there is a gap of more than a
    /// fifth of the letter height. Only for lines whose words make up the whole text (Latin script).
    /// </summary>
    /// <summary>Punctuation that is never preceded by a space: , . ; : ! ? ) ] } and closing quotes.</summary>
    private static readonly HashSet<char> ClosingPunctuation = [',', '.', ';', ':', '!', '?', ')', ']', '}', '"', '\u201C', '\u201D', '\u00BB', '\''];

    /// <summary>Punctuation never followed by a space: ( [ { and opening quotes (German low-9, English, guillemet).</summary>
    private static readonly HashSet<char> OpeningPunctuation = ['(', '[', '{', '\u201E', '\u201C', '\u00AB'];

    internal static string Compose(OcrLineBox line)
    {
        if (line.Words.Count < 2 || string.Concat(line.Words.Select(w => w.Text)) != line.Text.Replace(" ", "", StringComparison.Ordinal))
        {
            return line.Text.Trim();
        }

        var letter = Math.Max(line.Height, 1);
        var gaps = new double[line.Words.Count];
        for (var i = 1; i < line.Words.Count; i++)
        {
            gaps[i] = line.Words[i].X - (line.Words[i - 1].X + line.Words[i - 1].Width);
        }

        // "Touching" = far below the usual word gap of this line and below a fifth of the letter height.
        var usual = Median(gaps.Skip(1).Where(g => g > 0));
        var text = new StringBuilder(line.Words[0].Text);
        for (var i = 1; i < line.Words.Count; i++)
        {
            var touching = gaps[i] < 0.2 * letter && (usual <= 0 || gaps[i] < 0.35 * usual);

            // P2-5-Fix 4: a word that is only closing punctuation ("genannt ,") belongs to the word before, an
            // opening bracket or quote alone to the word after – a spacing rule, no text is changed.
            var closes = line.Words[i].Text.All(ClosingPunctuation.Contains);
            var opens = line.Words[i - 1].Text.All(OpeningPunctuation.Contains);
            if (!touching && !closes && !opens)
            {
                text.Append(' ');
            }

            text.Append(line.Words[i].Text);
        }

        return text.ToString();
    }

    private static List<OcrLineBox> Merged(List<OcrLineBox> lines)
    {
        var pending = lines.OrderBy(l => l.Left).ToList();
        var rows = new List<OcrLineBox>();
        while (pending.Count > 0)
        {
            var row = pending[0];
            pending.RemoveAt(0);
            for (var merged = true; merged;)
            {
                merged = false;
                for (var i = 0; i < pending.Count; i++)
                {
                    if (SameRow(row, pending[i]))
                    {
                        var words = row.Words.Concat(pending[i].Words).OrderBy(w => w.X).ToList();
                        var pieces = new[] { row, pending[i] }.OrderBy(l => l.Left).Select(l => l.Text.Trim());
                        row = new OcrLineBox(string.Join(" ", pieces), words);
                        pending.RemoveAt(i);
                        merged = true;
                        break;
                    }
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    private static bool SameRow(OcrLineBox a, OcrLineBox b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var smaller = Math.Max(Math.Min(a.Bottom - a.Top, b.Bottom - b.Top), 1);
        if (overlap < smaller / 2)
        {
            return false;
        }

        var letter = Math.Max(a.Height, b.Height);
        var gap = a.Left <= b.Left ? b.Left - a.Right : a.Left - b.Right;
        return gap < 2 * letter;
    }

    /// <summary>
    /// Columns: a vertical gap no line crosses, at least as wide as two letters, with at least two lines on
    /// each side – then left before right (recursively). Otherwise one column.
    /// </summary>
    internal static IEnumerable<List<OcrLineBox>> Columns(List<OcrLineBox> lines)
    {
        if (lines.Count < 4)
        {
            yield return lines;
            yield break;
        }

        var letter = Median(lines.Select(l => l.Height));
        var ordered = lines.OrderBy(l => l.Left).ToList();
        var reach = ordered[0].Right;
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Left - reach >= 2 * letter && i >= 2 && ordered.Count - i >= 2)
            {
                var gutter = (reach + ordered[i].Left) / 2;
                foreach (var column in Columns([.. lines.Where(l => l.Center < gutter)]).Concat(Columns([.. lines.Where(l => l.Center >= gutter)])))
                {
                    yield return column;
                }

                yield break;
            }

            reach = Math.Max(reach, ordered[i].Right);
        }

        yield return lines;
    }

    private static List<List<OcrLineBox>> ParagraphsOf(List<OcrLineBox> column)
    {
        var lines = column.OrderBy(l => l.Top).ThenBy(l => l.Left).ToList();
        var paragraphs = new List<List<OcrLineBox>>();
        if (lines.Count == 0)
        {
            return paragraphs;
        }

        var height = Median(lines.Select(l => l.Height));
        var pitch = Median(lines.Zip(lines.Skip(1), (a, b) => b.Top - a.Top));

        // Usual pitch: the median – but never more than 1.6 letter heights, otherwise text whose paragraphs are
        // all one line long (pitch = paragraph gap) would never break.
        var basePitch = pitch > 0 ? Math.Min(pitch, 1.6 * height) : 1.6 * height;

        // P2-5-Fix 3: with few lines the median already holds paragraph gaps – the tightest real line pitch
        // (lines not overlapping) is the measure then.
        var tightest = lines.Zip(lines.Skip(1), (a, b) => b.Top - a.Top).Where(p => p >= 0.8 * height).DefaultIfEmpty(0).Min();
        var current = new List<OcrLineBox> { lines[0] };
        double? itemLeft = IsBullet(lines[0].Text) ? ItemTextLeft(lines[0]) : null;
        for (var i = 1; i < lines.Count; i++)
        {
            var above = lines[i - 1];
            var line = lines[i];
            var gap = line.Top - above.Top;
            var letter = Math.Max(Math.Min(line.Height, above.Height), 1);
            var bullet = IsBullet(line.Text);

            // P2-5-Fix 3: a line at the text edge of the list item above (hanging indent) belongs to that item.
            var continuesItem = !bullet && itemLeft is { } edge && Math.Abs(line.Left - edge) <= letter;
            // Another type size (a heading): clearly wider letters, or very different heights.
            var widths = (Small: Math.Min(line.CharWidth, above.CharWidth), Large: Math.Max(line.CharWidth, above.CharWidth));
            var heights = (Small: Math.Min(line.Height, above.Height), Large: Math.Max(line.Height, above.Height));
            var otherSize = (widths.Small > 0 && widths.Large > 1.3 * widths.Small) || heights.Large > 1.6 * Math.Max(heights.Small, 1);
            var otherIndent = !continuesItem && Math.Abs(line.Left - above.Left) > 2 * letter && Math.Abs(line.Center - above.Center) > 2 * letter;
            var widerThanTightest = tightest > 0 && gap > 1.35 * tightest;
            if (gap > 1.4 * basePitch || widerThanTightest || otherSize || otherIndent)
            {
                paragraphs.Add(current);
                current = [];
            }

            current.Add(line);
            itemLeft = bullet ? ItemTextLeft(line) : continuesItem ? itemLeft : null;
        }

        paragraphs.Add(current);
        return paragraphs;
    }

    /// <summary>One paragraph as running text; a bullet starts a new line, hyphens at line ends go.</summary>
    internal static string Flow(IReadOnlyList<string> lines, IReadOnlyList<bool>? wantedBreaks = null)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (text.Length == 0)
            {
                text.Append(line);
            }
            else if (IsBullet(line) || (wantedBreaks is not null && i < wantedBreaks.Count && wantedBreaks[i]))
            {
                text.Append('\n').Append(line);
            }
            else
            {
                Join(text, line);
            }
        }

        return text.ToString();
    }

    /// <summary>"devel-" + "opment" → "development"; "in-" + "and output" and "E-" + "Mail" keep the hyphen.</summary>
    private static void Join(StringBuilder text, string next)
    {
        var last = text[^1];
        var beforeLast = text.Length > 1 ? text[^2] : ' ';
        if ((last is '-' or '­' or '¬') && char.IsLetter(beforeLast) && next.Length > 0)
        {
            var firstWord = next.Split(' ', 2)[0];
            if (char.IsLower(next[0]) && !KeepHyphenBefore.Contains(firstWord))
            {
                text.Length--; // a word broken at the line end
                text.Append(next);
                return;
            }

            if (char.IsUpper(next[0]) && last == '-')
            {
                text.Append(next); // "E-" + "Mail", "Daten-" + "Bank": a real hyphen, no space
                return;
            }
        }

        text.Append(' ').Append(next);
    }
}
