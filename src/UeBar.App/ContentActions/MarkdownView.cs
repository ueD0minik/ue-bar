// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using UeBar.Bar;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.ContentActions;

/// <summary>Draws Markdown (<see cref="MarkdownLite"/>) as a FlowDocument in the bar's colours – selectable, links open in the browser.</summary>
internal static class MarkdownView
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    /// <summary>A read-only, selectable view of the text.</summary>
    public static RichTextBox Create()
    {
        var box = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Res("TextBrush"),
            Padding = new Thickness(0),
            FontSize = 14,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = Res("AccentBrush"),
            CaretBrush = Brushes.Transparent,
        };
        box.Document = new FlowDocument { PagePadding = new Thickness(0) };
        return box;
    }

    public static void Render(RichTextBox box, string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = (FontFamily)Application.Current.FindResource("BarFont"),
            FontSize = 14,
            Foreground = Res("TextBrush"),
            LineHeight = 21,
        };

        List? list = null;
        var listLevel = -1;
        foreach (var block in MarkdownLite.Parse(markdown))
        {
            if (block.Kind != MdBlockKind.ListItem)
            {
                list = null;
                listLevel = -1;
            }

            switch (block.Kind)
            {
                case MdBlockKind.Heading:
                    var heading = Paragraph(block.Inlines);
                    heading.FontSize = block.Level switch { 1 => 20, 2 => 17, _ => 15 };
                    heading.FontWeight = FontWeights.SemiBold;
                    heading.Margin = new Thickness(0, 10, 0, 4);
                    document.Blocks.Add(heading);
                    break;
                case MdBlockKind.ListItem:
                    if (list is null || block.Level != listLevel)
                    {
                        list = new List
                        {
                            MarkerStyle = block.Number is null ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal,
                            Margin = new Thickness(block.Level * 18, 2, 0, 2),
                            Padding = new Thickness(22, 0, 0, 0),
                        };
                        if (block.Number is { } start && start > 1)
                        {
                            list.StartIndex = start;
                        }

                        document.Blocks.Add(list);
                        listLevel = block.Level;
                    }

                    var itemParagraph = Paragraph(block.Inlines);
                    itemParagraph.Margin = new Thickness(0, 1, 0, 1);
                    if (block.Checked is { } done)
                    {
                        itemParagraph.Inlines.InsertBefore(itemParagraph.Inlines.FirstInline, new Run(done ? "☑ " : "☐ "));
                    }

                    list.ListItems.Add(new ListItem(itemParagraph));
                    break;
                case MdBlockKind.Code:
                    var code = new Paragraph(new Run(block.Code ?? ""))
                    {
                        FontFamily = Mono,
                        FontSize = 13,
                        Background = Res("TileOffBrush"),
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 6, 0, 6),
                        LineHeight = 18,
                    };
                    document.Blocks.Add(code);
                    break;
                case MdBlockKind.Quote:
                    var quote = Paragraph(block.Inlines);
                    quote.BorderBrush = Res("SubTextBrush");
                    quote.BorderThickness = new Thickness(3, 0, 0, 0);
                    quote.Padding = new Thickness(10, 2, 0, 2);
                    quote.Foreground = Res("SubTextBrush");
                    document.Blocks.Add(quote);
                    break;
                case MdBlockKind.Rule:
                    document.Blocks.Add(new Paragraph { BorderBrush = Res("GripBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8, 0, 8), FontSize = 1 });
                    break;
                case MdBlockKind.Table:
                    document.Blocks.Add(Table(block));
                    break;
                default:
                    var paragraph = Paragraph(block.Inlines);
                    paragraph.Margin = new Thickness(0, 0, 0, 8);
                    document.Blocks.Add(paragraph);
                    break;
            }
        }

        box.Document = document;
    }

    private static Table Table(MdBlock block)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8), BorderBrush = Res("GripBrush"), BorderThickness = new Thickness(1) };
        var columns = block.Rows!.Max(r => r.Count);
        for (var c = 0; c < columns; c++)
        {
            table.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        for (var r = 0; r < block.Rows!.Count; r++)
        {
            var row = new TableRow();
            foreach (var cell in block.Rows[r])
            {
                var paragraph = Paragraph(cell);
                paragraph.Margin = new Thickness(0);
                if (r == 0)
                {
                    paragraph.FontWeight = FontWeights.SemiBold;
                }

                row.Cells.Add(new TableCell(paragraph) { Padding = new Thickness(8, 4, 8, 4), BorderBrush = Res("GripBrush"), BorderThickness = new Thickness(0, 0, 1, 1) });
            }

            group.Rows.Add(row);
        }

        table.RowGroups.Add(group);
        return table;
    }

    private static Paragraph Paragraph(IReadOnlyList<MdInline> inlines)
    {
        var paragraph = new Paragraph();
        foreach (var piece in inlines)
        {
            var lines = piece.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                if (lines[i].Length > 0)
                {
                    paragraph.Inlines.Add(Styled(piece, lines[i]));
                }
            }
        }

        return paragraph;
    }

    private static Inline Styled(MdInline piece, string text)
    {
        Inline run = new Run(text);
        if (piece.Style.HasFlag(MdStyle.Code))
        {
            run.FontFamily = Mono;
            run.FontSize = 13;
            run.Background = Res("TileOffBrush");
        }

        if (piece.Style.HasFlag(MdStyle.Bold))
        {
            run.FontWeight = FontWeights.SemiBold;
        }

        if (piece.Style.HasFlag(MdStyle.Italic))
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (piece.Style.HasFlag(MdStyle.Strike))
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }

        if (piece.Link is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var link = new Hyperlink(run) { NavigateUri = uri, Foreground = Res("AccentBrush"), ToolTip = url };
            link.RequestNavigate += (_, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // no browser – nothing to do
                }
            };
            return link;
        }

        return run;
    }
}
