
using Egap.Pdf;

using Picture = Egap.Documents.Picture;

namespace Egap.Documents.Pdf;

internal sealed partial class ModelBuilder
{
    /// <summary>The space Word leaves inside a text box by default: a tenth of an inch at the sides, a twentieth at top and bottom.</summary>
    private const double InsetX = 7.2, InsetY = 3.6;

    private readonly HashSet<PageDraft> scattered = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether a block leaves the flow for a positioned frame (design Q8): its confidence is under the threshold, it lies
    /// over a figure, or its page is a scatter of short labels (a diagram or a form) that reflowing would scramble.
    /// </summary>
    private bool Positioned(BlockDraft block, PageDraft page, out WrapMode wrap)
    {
        wrap = WrapMode.Square;
        if (page.Figures.Any(f => Overlap(block.Box, f.Box) > block.Box.Width * block.Box.Height * 0.6))
        {
            wrap = WrapMode.InFrontOfText;
            return true;
        }

        return block.Confidence < options.ConfidenceThreshold || scattered.Contains(page);
    }

    /// <summary>Pages of scattered labels: many blocks, nearly all one short line, starting at many different lefts.</summary>
    private void FindScattered()
    {
        foreach (PageDraft page in pages)
        {
            List<BlockDraft> body = [.. page.Blocks.Where(b => !Furniture(b) && b.Role != LayoutRole.Footnote)];
            if (body.Count < 12)
            {
                continue;
            }

            double width = page.Content.Width;
            List<double> widths = [.. body.Select(b => b.Box.Width).Order()];
            // Labels sit anywhere; short lines stacked in columns (a list, an index) start at a few lefts and flow.
            int lefts = body.Select(b => (int)Math.Round(b.Box.Left / 4)).Distinct().Count();
            if (body.Count(b => b.Lines.Count == 1) >= body.Count * 0.75 && widths[widths.Count / 2] < width * 0.2 && lefts >= body.Count * 0.4)
            {
                scattered.Add(page);
            }
        }
    }

    /// <summary>Writes a block as a positioned text frame on its page, anchored at the next paragraph of the flow.</summary>
    private void WriteFrame(BlockDraft block, WrapMode wrap)
    {
        if (!Placeable(block.Box))
        {
            return;
        }

        StyleEntry entry = styles.For(block);
        Shape frame = Frame(block.Box, wrap);
        Paragraph paragraph = Framed(entry);
        writer.Write(paragraph, block.Lines, entry.Font, breaks: true);
        frame.TextBox!.Blocks.Add(paragraph);
        floats.Add(frame);
    }

    /// <summary>A text frame over <paramref name="box"/>: no line, no fill, placed from the page's top left so its text lands where the box was.</summary>
    private Shape Frame(PageBox box, WrapMode wrap)
    {
        // A little room on the right keeps a line from wrapping in a font a touch wider than the PDF's.
        Shape frame = new(document, ShapeKind.Rectangle, Math.Round(box.Width + (2 * InsetX) + Math.Max(4, box.Width * 0.06), 2), Math.Round(box.Height + (2 * InsetY), 2))
        {
            Fill = null,
            Outline = null,
            Floating = new FloatingPlacement
            {
                HorizontalAnchor = HorizontalAnchor.Page,
                HorizontalOffset = Math.Round(box.Left - InsetX, 2),
                VerticalAnchor = VerticalAnchor.Page,
                VerticalOffset = Math.Round(box.Top - InsetY, 2),
                Wrap = wrap,
                DistanceLeft = 0,
                DistanceRight = 0,
            },
        };
        frame.AddTextBox();
        return frame;
    }

    /// <summary>A paragraph for a frame: its style's look, with no space around it.</summary>
    private Paragraph Framed(StyleEntry entry)
    {
        Paragraph paragraph = new(document) { Style = entry.Style };
        paragraph.Format.SpaceBefore = 0;
        paragraph.Format.SpaceAfter = 0;
        paragraph.Format.LeftIndent = 0;
        paragraph.Format.FirstLineIndent = 0;
        paragraph.Format.Alignment = ParagraphAlignment.Left;
        paragraph.Format.LineSpacing = 1;
        paragraph.Format.LineSpacingRule = LineSpacingRule.Multiple;
        return paragraph;
    }

    /// <summary>
    /// Exact mode (design Q8): every page is an empty paragraph starting a new page, holding a text frame for each line the
    /// page draws where the line sits, each table in a frame of its own and each figure as a picture placed on the page, its
    /// text drawn in it. It looks like the PDF and edits poorly.
    /// </summary>
    private void BuildExact(CancellationToken cancellationToken)
    {
        Section? section = null;
        PageDraft? previous = null;
        foreach (PageDraft page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (section is null || previous is null || Math.Abs(page.Content.Width - previous.Content.Width) >= 1 || Math.Abs(page.Content.Height - previous.Content.Height) >= 1)
            {
                Section next = document.AddSection();
                next.PageSetup.PageWidth = page.Content.Width;
                next.PageSetup.PageHeight = page.Content.Height;
                next.PageSetup.Orientation = page.Content.Width > page.Content.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
                next.PageSetup.MarginLeft = next.PageSetup.MarginRight = next.PageSetup.MarginTop = next.PageSetup.MarginBottom = 0;
                next.PageSetup.HeaderDistance = next.PageSetup.FooterDistance = 0;
                if (section is not null)
                {
                    next.PageSetup.SectionStart = SectionStart.NewPage;
                }

                section = next;
            }

            writer.SetPage(page.Content);
            Paragraph anchor = new(document) { Style = styles.Normal.Style };
            anchor.Format.SpaceBefore = 0;
            anchor.Format.SpaceAfter = 0;
            anchor.Format.PageBreakBefore = previous is not null && section.Body.Blocks.Count > 0;
            anchor.MarkFormat.Size = 1;
            // Every line the page draws, outside tables (framed whole below) and figures (drawn as pictures with their text).
            Dictionary<PageGlyph, BlockDraft> owner = new(ReferenceEqualityComparer.Instance);
            foreach (BlockDraft block in page.Blocks)
            {
                foreach (PageGlyph glyph in block.Lines.SelectMany(l => l.Words).SelectMany(w => w.Glyphs))
                {
                    owner.TryAdd(glyph, block);
                }
            }

            List<PageBox> covered = [.. page.Tables.Select(t => t.Box), .. page.Figures.Select(f => f.Box)];
            foreach (PageLine line in page.Content.Lines.Where(l => Placeable(l.Box)).OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left))
            {
                double x = (line.Box.Left + line.Box.Right) / 2, y = (line.Box.Top + line.Box.Bottom) / 2;
                if (covered.Any(b => b.Contains(x, y)))
                {
                    continue;
                }

                StyleEntry entry = owner.TryGetValue(line.Words[0].Glyphs[0], out BlockDraft? block) ? styles.For(block) : styles.Normal;
                Shape frame = Frame(line.Box, WrapMode.InFrontOfText);
                Paragraph text = Framed(entry);
                writer.Write(text, [line], entry.Font);
                frame.TextBox!.Blocks.Add(text);
                anchor.Inlines.Add(frame);
            }

            foreach (TableDraft table in page.Tables.Where(t => Placeable(t.Box) && !page.Figures.Any(f => f.Box.Contains(t.Box.CenterX, t.Box.CenterY))))
            {
                Shape frame = Frame(table.Box, WrapMode.InFrontOfText);
                lastTable = null;
                WriteTable(frame.TextBox!, table, new Frame(table.Box.Left, table.Box.Right));
                frame.TextBox!.Blocks.Add(new Paragraph(document) { Style = styles.Normal.Style });
                anchor.Inlines.Add(frame);
            }

            foreach (FigureDraft figure in page.Figures.Where(f => Placeable(f.Box) && f.Box.Width >= 1 && f.Box.Height >= 1))
            {
                Picture picture = new(document, Image(figure, page), Math.Round(figure.Box.Width, 2), Math.Round(figure.Box.Height, 2))
                {
                    Floating = new FloatingPlacement
                    {
                        HorizontalAnchor = HorizontalAnchor.Page,
                        HorizontalOffset = Math.Round(figure.Box.Left, 2),
                        VerticalAnchor = VerticalAnchor.Page,
                        VerticalOffset = Math.Round(figure.Box.Top, 2),
                        Wrap = WrapMode.BehindText,
                    },
                };
                if (figure.AltText is { Length: > 0 } alt)
                {
                    picture.Description = Clean(alt);
                }

                anchor.Inlines.Insert(0, picture);
            }

            if (Scan(page) is PageImage scan)
            {
                PdfPage pdfPage = pdf.Pages[page.Content.Index];
                PageBox box = scan.Bounds.Intersect(page.Content.Bounds);
                options.OnWarning?.Invoke(new PdfToDocumentWarning(PdfToDocumentWarningKind.ScannedPage, page.Content.Index,
                    $"Page {page.Content.Index + 1} is a scanned picture with no text; it is kept as a picture."));
                ImageData image = Stored(PdfLayoutAnalyzer.Match(scan, PdfLayoutAnalyzer.Placed(pdfPage), page.Content)) ?? Rendered(pdfPage, box);
                anchor.Inlines.Insert(0, new Picture(document, image, Math.Round(box.Width, 2), Math.Round(box.Height, 2))
                {
                    Floating = new FloatingPlacement
                    {
                        HorizontalAnchor = HorizontalAnchor.Page,
                        HorizontalOffset = Math.Round(box.Left, 2),
                        VerticalAnchor = VerticalAnchor.Page,
                        VerticalOffset = Math.Round(box.Top, 2),
                        Wrap = WrapMode.BehindText,
                    },
                });
            }

            section.Body.Blocks.Add(anchor);
            written.Add((page.Content.Index, page.Content.Bounds, anchor));
            previous = page;
        }
    }

    private static double Overlap(PageBox a, PageBox b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>Whether a box can hold a frame: finite, with some size, on or near the page.</summary>
    private static bool Placeable(PageBox box)
    {
        return double.IsFinite(box.Left) && double.IsFinite(box.Top) && double.IsFinite(box.Right) && double.IsFinite(box.Bottom)
            && box.Width > 0 && box.Height > 0 && box.Width < 20000 && box.Height < 20000;
    }
}
