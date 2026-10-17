using Egap.Documents;

namespace Egap.Pdf.Layout;

/// <summary>
/// A flow document from page layouts alone, without the PDF they came from (Build Plan task 18): what DoclingDocument JSON
/// holds is roles, text, tables and pictures, not fonts or line breaks, so paragraphs take the built-in styles of their roles.
/// </summary>
internal static class LayoutDocument
{
    public static Document Build(IReadOnlyList<PdfPageLayout> pages)
    {
        Document document = new();
        Section? section = null;
        PdfPageLayout? previous = null;
        List? list = null;
        Paragraph? last = null;
        foreach (PdfPageLayout page in pages)
        {
            if (section is null || previous is null || Math.Abs(page.Width - previous.Width) >= 1 || Math.Abs(page.Height - previous.Height) >= 1)
            {
                section = document.AddSection();
                section.PageSetup.PageWidth = page.Width;
                section.PageSetup.PageHeight = page.Height;
                section.PageSetup.Orientation = page.Width > page.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
                if (document.Sections.Count > 1)
                {
                    section.PageSetup.SectionStart = SectionStart.NewPage;
                }

                Furniture(document, section, page);
            }

            foreach (LayoutElement element in page.Elements)
            {
                switch (element)
                {
                    case LayoutBlock { Role: LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber }:
                        break;
                    case LayoutBlock { ContinuesPrevious: true } block when last is not null:
                        last.AddRun(" " + block.Text);
                        break;
                    case LayoutBlock block:
                        Paragraph paragraph = section.Body.AddParagraph();
                        paragraph.Style = Style(document, block);
                        string text = block.Text;
                        if (block.Role == LayoutRole.ListItem)
                        {
                            Marker? marker = block.ListMarker is string m ? ListBuilder.Parse(m, true) : null;
                            list ??= marker is { Style: not NumberStyle.Bullet } ? document.Lists.AddNumbered() : document.Lists.AddBulleted();
                            paragraph.SetList(list, Math.Clamp(block.Level - 1, 0, 8));
                            if (block.ListMarker is string written && text.StartsWith(written, StringComparison.Ordinal))
                            {
                                text = text[written.Length..].TrimStart();
                            }
                        }
                        else
                        {
                            list = null;
                        }

                        paragraph.AddRun(text);
                        last = paragraph;
                        break;
                    case DetectedTable table:
                        Table(section.Body, table);
                        list = null;
                        last = null;
                        break;
                    case LayoutFigure { Embedded: (ImageData image, _, _) } figure:
                        Paragraph holder = section.Body.AddParagraph();
                        Picture picture = holder.AddPicture(image, Math.Round(figure.Bounds.Width, 2), Math.Round(figure.Bounds.Height, 2));
                        if (figure.AltText is string alt)
                        {
                            picture.Description = alt;
                        }

                        list = null;
                        last = null;
                        break;
                }
            }

            previous = page;
        }

        if (document.Sections.Count == 0)
        {
            document.AddSection().Body.AddParagraph();
        }

        return document;
    }

    private static ParagraphStyle Style(Document document, LayoutBlock block)
    {
        BuiltInStyle style = block.Role switch
        {
            LayoutRole.Title => BuiltInStyle.Title,
            LayoutRole.Heading => BuiltInStyle.Heading1 + (Math.Clamp(block.Level, 1, 9) - 1),
            LayoutRole.Caption => BuiltInStyle.Caption,
            LayoutRole.Quote => BuiltInStyle.Quote,
            LayoutRole.ListItem => BuiltInStyle.ListParagraph,
            LayoutRole.Footnote => BuiltInStyle.FootnoteText,
            _ => BuiltInStyle.Normal,
        };
        return (ParagraphStyle)document.Styles.AddBuiltIn(style);
    }

    /// <summary>The first page's headers and footers as the section's, each block a paragraph.</summary>
    private static void Furniture(Document document, Section section, PdfPageLayout page)
    {
        foreach (LayoutBlock block in page.Blocks.Where(b => b.Role is LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber))
        {
            bool header = block.Role == LayoutRole.PageHeader || (block.Role == LayoutRole.PageNumber && block.Bounds.Y < page.Height / 2);
            HeaderFooter part = section.HeadersFooters.GetOrAdd(header ? HeaderFooterKind.HeaderPrimary : HeaderFooterKind.FooterPrimary);
            Paragraph paragraph = part.AddParagraph(block.Text);
            paragraph.Style = (ParagraphStyle)document.Styles.AddBuiltIn(header ? BuiltInStyle.Header : BuiltInStyle.Footer);
        }
    }

    private static void Table(Body body, DetectedTable source)
    {
        Table table = body.AddTable(0, 0);
        Dictionary<(int, int), TableCell> covering = [];
        foreach (TableCell cell in source.Cells)
        {
            for (int r = cell.Row; r < cell.Row + cell.RowSpan; r++)
            {
                for (int c = cell.Column; c < cell.Column + cell.ColumnSpan; c++)
                {
                    covering.TryAdd((r, c), cell);
                }
            }
        }

        for (int r = 0; r < source.RowCount; r++)
        {
            Row row = table.AddRow();
            for (int c = 0; c < source.ColumnCount;)
            {
                TableCell? cell = covering.GetValueOrDefault((r, c));
                if (cell is not null && cell.Column != c)
                {
                    c++;
                    continue;
                }

                Cell target = row.AddCell();
                int span = cell is null ? 1 : Math.Min(cell.ColumnSpan, source.ColumnCount - c);
                if (span > 1)
                {
                    target.Format.GridSpan = span;
                }

                if (cell is { RowSpan: > 1 })
                {
                    target.Format.VerticalMerge = cell.Row == r ? VerticalMerge.Restart : VerticalMerge.Continue;
                }

                if (cell is not null && cell.Row == r && cell.Text.Length > 0)
                {
                    target.Paragraphs.First().AddRun(cell.Text);
                }

                c += span;
            }

            if (r == 0 && source.Cells.Where(x => x.Row == 0).All(x => x.IsHeader) && source.Cells.Any(x => x.Row == 0))
            {
                row.Format.IsHeader = true;
            }
        }

    }
}
