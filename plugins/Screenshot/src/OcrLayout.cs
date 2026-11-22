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

    /// <summary>Height of the letters (median of the words – one tall word does not count).</summary>
    public double Height => Words.Count == 0 ? 0 : OcrLayout.Median(Words.Select(w => w.Height));

    public double Center => (Left + Right) / 2;
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

    [GeneratedRegex(@"\S\s+(?:[-–—=]|->|→|=>)\s+\S|\S:\s+\S")]
    private static partial Regex SeparatorPattern();

    /// <summary>A line with a separator in the middle or a wide gap between two words (a table column).</summary>
    internal static bool IsStructured(OcrLineBox line, double letter)
    {
        if (SeparatorPattern().IsMatch(line.Text))
        {
            return true;
        }

        for (var i = 1; i < line.Words.Count; i++)
        {
            var gap = line.Words[i].X - (line.Words[i - 1].X + line.Words[i - 1].Width);
            if (gap > 1.5 * letter)
            {
                return true;
            }
        }

        return false;
    }

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
        var usable = lines.Where(l => l.Words.Count > 0 && !string.IsNullOrWhiteSpace(l.Text)).ToList();
        var result = new List<List<OcrLineBox>>();
        foreach (var column in Columns(usable))
        {
            result.AddRange(ParagraphsOf(column));
        }

        return result;
    }

    /// <summary>
    /// Line breaks inside a paragraph that were made on purpose: the first word of the next line would still
    /// have fitted into the line above (a list of short lines, vocabulary, an address). If that is so for most
    /// breaks of the paragraph, every line stays a line; otherwise only those breaks stay. Text that only
    /// wrapped is joined. Index i = the break before line i.
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

        // Lines built like a table or a vocabulary list ("to achieve - erreichen", "Name:  Value", columns) stay
        // lines when most of the paragraph looks like that – they are as long as wrapped text, so the fit test
        // below cannot tell them apart.
        var structured = paragraph.Count(l => IsStructured(l, letter));
        if (structured * 3 >= paragraph.Count * 2)
        {
            Array.Fill(wanted, true);
            return wanted;
        }

        var count = 0;
        var breaks = 0;
        for (var i = 1; i < paragraph.Count; i++)
        {
            if (IsBullet(paragraph[i].Text))
            {
                continue; // a new line anyway
            }

            breaks++;
            var first = paragraph[i].Words[0];
            var space = Math.Max(first.Height * 0.3, letter * 0.3);
            wanted[i] = paragraph[i - 1].Right + space + first.Width < right - (letter * 0.5);
            count += wanted[i] ? 1 : 0;
        }

        if (breaks > 0 && count * 2 > breaks)
        {
            Array.Fill(wanted, true);
        }

        return wanted;
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
        var current = new List<OcrLineBox> { lines[0] };
        for (var i = 1; i < lines.Count; i++)
        {
            var above = lines[i - 1];
            var line = lines[i];
            var gap = line.Top - above.Top;
            var letter = Math.Max(Math.Min(line.Height, above.Height), 1);
            var otherSize = Math.Abs(line.Height - above.Height) > 0.25 * letter;
            var otherIndent = Math.Abs(line.Left - above.Left) > 2 * letter && Math.Abs(line.Center - above.Center) > 2 * letter;
            if (gap > 1.4 * basePitch || otherSize || otherIndent)
            {
                paragraphs.Add(current);
                current = [];
            }

            current.Add(line);
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
