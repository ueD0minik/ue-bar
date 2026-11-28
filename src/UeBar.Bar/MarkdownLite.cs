// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text;

namespace UeBar.Bar;

/// <summary>Kind of a Markdown block (<see cref="MarkdownLite"/>).</summary>
public enum MdBlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Code,
    Quote,
    Rule,
    Table,
}

/// <summary>Kind of a piece of text inside a block.</summary>
[Flags]
public enum MdStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strike = 8,
}

/// <summary>A piece of text with one style; <see cref="Link"/> set = a link.</summary>
public sealed record MdInline(string Text, MdStyle Style = MdStyle.None, string? Link = null);

/// <summary>
/// One block. <see cref="Level"/>: heading 1–6, list nesting 0…; <see cref="Number"/> for numbered lists (null = bullet);
/// <see cref="Code"/> for code blocks (with <see cref="Language"/>); <see cref="Rows"/> for tables (first row = header).
/// </summary>
public sealed record MdBlock(MdBlockKind Kind, IReadOnlyList<MdInline> Inlines)
{
    public int Level { get; init; }

    public int? Number { get; init; }

    public string? Code { get; init; }

    public string? Language { get; init; }

    public IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>>? Rows { get; init; }

    /// <summary>A task list item: true = done, false = open, null = none.</summary>
    public bool? Checked { get; init; }
}

/// <summary>
/// The Markdown answers of assistants usually use (P3-1): headings, paragraphs, bullet and numbered lists (nested),
/// code blocks and inline code, bold/italic/strikethrough, links, quotes, rules and simple tables. Forgiving: an
/// answer that is still coming in (an open code block, a half-written "**") is shown as well as possible. No HTML.
/// </summary>
public static class MarkdownLite
{
    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(markdown))
        {
            return blocks;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new MdBlock(MdBlockKind.Paragraph, Inline(paragraph.ToString())));
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Code block: ``` (or ~~~) up to the closing fence – or the end, while the answer is still coming.
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                var indent = line.Length - trimmed.Length;
                for (i++; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (code.Length > 0)
                    {
                        code.Append('\n');
                    }

                    var codeLine = lines[i];
                    code.Append(indent > 0 && codeLine.Length >= indent && string.IsNullOrWhiteSpace(codeLine[..indent]) ? codeLine[indent..] : codeLine);
                }

                blocks.Add(new MdBlock(MdBlockKind.Code, []) { Code = code.ToString(), Language = language.Length > 0 ? language : null });
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (Heading(trimmed) is { } heading)
            {
                FlushParagraph();
                blocks.Add(heading);
                continue;
            }

            if (IsRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Rule, []));
                continue;
            }

            if (ListItem(line) is { } item)
            {
                FlushParagraph();
                blocks.Add(item);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new StringBuilder(trimmed[1..].TrimStart());
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('>'))
                {
                    quote.Append('\n').Append(lines[++i].TrimStart()[1..].TrimStart());
                }

                blocks.Add(new MdBlock(MdBlockKind.Quote, Inline(quote.ToString())));
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>> { Cells(trimmed) };
                i++; // separator
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('|'))
                {
                    rows.Add(Cells(lines[++i].Trim()));
                }

                blocks.Add(new MdBlock(MdBlockKind.Table, []) { Rows = rows });
                continue;
            }

            // A line of a paragraph: line breaks inside a paragraph stay (answers use them on purpose).
            if (paragraph.Length > 0)
            {
                paragraph.Append('\n');
            }

            paragraph.Append(trimmed.TrimEnd());
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>The text without Markdown marks – for copying a code block or a plain view.</summary>
    public static string PlainText(string? markdown)
    {
        var parts = new List<string>();
        foreach (var block in Parse(markdown))
        {
            parts.Add(block.Kind switch
            {
                MdBlockKind.Code => block.Code ?? "",
                MdBlockKind.Rule => "",
                MdBlockKind.Table => string.Join("\n", block.Rows!.Select(r => string.Join("\t", r.Select(c => string.Concat(c.Select(x => x.Text)))))),
                MdBlockKind.ListItem => new string(' ', block.Level * 2) + (block.Number is { } n ? $"{n}. " : "• ") + string.Concat(block.Inlines.Select(x => x.Text)),
                _ => string.Concat(block.Inlines.Select(x => x.Text)),
            });
        }

        return string.Join("\n", parts).Trim();
    }

    private static MdBlock? Heading(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && level < 7 && trimmed[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 6 && (trimmed.Length == level || trimmed[level] == ' ')
            ? new MdBlock(MdBlockKind.Heading, Inline(trimmed[level..].Trim().TrimEnd('#').TrimEnd())) { Level = level }
            : null;
    }

    private static bool IsRule(string trimmed)
    {
        var compact = trimmed.Replace(" ", "", StringComparison.Ordinal);
        return compact.Length >= 3 && (compact.All(c => c == '-') || compact.All(c => c == '*') || compact.All(c => c == '_'));
    }

    private static MdBlock? ListItem(string line)
    {
        var indent = 0;
        while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
        {
            indent += line[indent] == '\t' ? 4 : 1;
            if (indent > 40)
            {
                break;
            }
        }

        var rest = line.TrimStart();
        int? number = null;
        string text;
        if (rest.Length >= 2 && (rest[0] is '-' or '*' or '+') && rest[1] == ' ')
        {
            text = rest[2..];
        }
        else
        {
            var digits = 0;
            while (digits < rest.Length && digits < 9 && char.IsAsciiDigit(rest[digits]))
            {
                digits++;
            }

            if (digits == 0 || digits + 1 >= rest.Length || rest[digits] is not ('.' or ')') || rest[digits + 1] != ' ')
            {
                return null;
            }

            number = int.Parse(rest[..digits], System.Globalization.CultureInfo.InvariantCulture);
            text = rest[(digits + 2)..];
        }

        bool? done = null;
        if (text.Length >= 3 && text[0] == '[' && text[2] == ']' && (text.Length == 3 || text[3] == ' '))
        {
            done = text[1] is 'x' or 'X';
            text = text[3..].TrimStart();
        }

        return new MdBlock(MdBlockKind.ListItem, Inline(text.Trim())) { Level = Math.Min(indent / 2, 6), Number = number, Checked = done };
    }

    private static bool IsTableSeparator(string line)
    {
        var t = line.Trim();
        return t.StartsWith('|') && t.Contains('-', StringComparison.Ordinal) && t.All(c => c is '|' or '-' or ':' or ' ');
    }

    private static IReadOnlyList<IReadOnlyList<MdInline>> Cells(string row)
    {
        var inner = row.Trim();
        inner = inner.StartsWith('|') ? inner[1..] : inner;
        inner = inner.EndsWith('|') ? inner[..^1] : inner;
        return [.. inner.Split('|').Select(c => Inline(c.Trim()))];
    }

    /// <summary>Inline marks: `code`, **bold**, *italic* / _italic_, ~~strike~~, [text](url), and \-escapes.</summary>
    public static IReadOnlyList<MdInline> Inline(string text)
    {
        var result = new List<MdInline>();
        var buffer = new StringBuilder();
        var style = MdStyle.None;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                if (result.Count > 0 && result[^1].Style == style && result[^1].Link is null)
                {
                    result[^1] = result[^1] with { Text = result[^1].Text + buffer };
                }
                else
                {
                    result.Add(new MdInline(buffer.ToString(), style));
                }

                buffer.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_{}[]()#+-.!~|>".Contains(text[i + 1], StringComparison.Ordinal))
            {
                buffer.Append(text[++i]);
                continue;
            }

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    result.Add(new MdInline(text[(i + 1)..end], style | MdStyle.Code));
                    i = end;
                    continue;
                }
            }

            if (c == '[')
            {
                var close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                var end = close > i ? text.IndexOf(')', close + 2) : -1;
                if (close > i && end > close)
                {
                    var url = text[(close + 2)..end].Trim();
                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    {
                        Flush();
                        result.Add(new MdInline(text[(i + 1)..close], style, url));
                        i = end;
                        continue;
                    }
                }
            }

            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                Flush();
                style ^= MdStyle.Strike;
                i++;
                continue;
            }

            if (c is '*' or '_')
            {
                var run = 1;
                while (i + run < text.Length && text[i + run] == c && run < 3)
                {
                    run++;
                }

                // An underscore inside a word (file_name) is not a mark.
                var inWord = c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]) && i + run < text.Length && char.IsLetterOrDigit(text[i + run]);

                // "* " at the start of nothing, or a lone star between spaces, is a star.
                var lone = i + run < text.Length && text[i + run] == ' ' && (i == 0 || text[i - 1] == ' ') && (style & (MdStyle.Bold | MdStyle.Italic)) == 0;
                if (!inWord && !lone)
                {
                    Flush();
                    style ^= run switch
                    {
                        1 => MdStyle.Italic,
                        2 => MdStyle.Bold,
                        _ => MdStyle.Bold | MdStyle.Italic,
                    };
                    i += run - 1;
                    continue;
                }
            }

            buffer.Append(c);
        }

        Flush();
        return result;
    }
}
