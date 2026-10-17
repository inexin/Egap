using Egami.Drawing;

namespace Egap.Pdf.Layout;

/// <summary>
/// Tags an untagged PDF from its layout (design Q11, Build Plan task 14): a structure tree through Phase 5's model, with
/// headings, paragraphs, lists, tables, figures, captions, quotes, code and notes in reading order, page headers and footers
/// and decoration marked as artifacts, and the page content wrapped in marked content that points at its element. Figures
/// get no alternate text: the report lists them, and writing it stays with the caller.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("report.pdf");
/// PdfAutoTagReport report = PdfAutoTagger.Tag(pdf);
/// foreach (PdfAutoTagFigure figure in report.FiguresWithoutAltText)
/// {
///     Console.WriteLine($"page {figure.PageIndex + 1}: figure at {figure.Bounds} needs alternate text");
/// }
///
/// pdf.Save("report.pdf"); // an incremental update by default
/// </code>
/// </example>
public static class PdfAutoTagger
{
    /// <summary>The operators that paint a path.</summary>
    private static readonly HashSet<string> PathPaint = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*"];

    /// <summary>The operators that build a path or set the clip before it is painted.</summary>
    private static readonly HashSet<string> PathBuild = ["m", "l", "c", "v", "y", "h", "re", "W", "W*"];

    /// <summary>The operators that show text, draw an object, an inline image or a shading.</summary>
    private static readonly HashSet<string> Drawing = ["Tj", "TJ", "'", "\"", "Do", "BI", "sh"];

    /// <summary>
    /// Tags <paramref name="document"/> when it has no structure tree; a document that has one is left as it is. Save the
    /// document afterwards; by default that writes only the changes, as an incremental update.
    /// </summary>
    /// <param name="document">The document, open for writing.</param>
    /// <param name="options">How the pages are analysed; their tags are never used, since there are none.</param>
    /// <param name="cancellationToken">Stops between pages.</param>
    public static PdfAutoTagReport Tag(PdfDocument document, PdfLayoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfStructureTree tree = document.Structure;
        if (tree.Exists && tree.Children.Count > 0)
        {
            return new PdfAutoTagReport(false, 0, 0, []);
        }

        options = (options ?? PdfLayoutOptions.Default) with { UseTags = false, TrackOperations = true };
        (List<PageDraft> drafts, _) = PdfLayoutAnalyzer.Drafts(document, Enumerable.Range(0, document.Pages.Count), options, cancellationToken);
        PdfStructureElement root = tree.Add(PdfStructureType.Document);
        List<PdfAutoTagFigure> missing = [];
        int elements = 0;
        foreach (PageDraft draft in drafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            elements += TagPage(document.Pages[draft.Content.Index], draft, root, missing);
        }

        tree.IsMarked = true;
        return new PdfAutoTagReport(true, drafts.Count, elements, missing);
    }

    /// <summary>Builds one page's elements under <paramref name="root"/> and wraps its content in marked content; returns the elements made.</summary>
    private static int TagPage(PdfPage page, PageDraft draft, PdfStructureElement root, List<PdfAutoTagFigure> missing)
    {
        PageContent content = draft.Content;
        Dictionary<int, Dictionary<PdfStructureElement, int>> votes = [];
        int made = 0;

        void Vote(IEnumerable<object> items, PdfStructureElement? element)
        {
            foreach (object item in items)
            {
                if (!content.Operations.TryGetValue(item, out int op))
                {
                    continue;
                }

                // Artifacts need no vote: what no element claims is marked as one.
                if (element is null)
                {
                    continue;
                }

                Dictionary<PdfStructureElement, int> counts = votes.TryGetValue(op, out Dictionary<PdfStructureElement, int>? c) ? c : votes[op] = new(ReferenceEqualityComparer.Instance);
                counts[element] = counts.GetValueOrDefault(element) + 1;
            }
        }

        PdfStructureElement Add(PdfStructureElement parent, PdfStructureType type)
        {
            made++;
            return parent.Add(type);
        }

        static IEnumerable<object> Glyphs(IEnumerable<PageLine> lines)
        {
            return lines.SelectMany(l => l.Words).SelectMany(w => w.Glyphs);
        }

        // Furniture and footnotes come out of the body's order as the public layout gives them.
        bool Body(BlockDraft b) => b.Role is not (LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber or LayoutRole.Footnote);
        List<object> order = [.. draft.Blocks.Where(Body).Cast<object>().Concat(draft.Tables).Concat(draft.Figures).OrderBy(OrderOf),
            .. draft.Blocks.Where(b => b.Role == LayoutRole.Footnote).OrderBy(b => b.Box.Top)];
        foreach (BlockDraft furniture in draft.Blocks.Where(b => !Body(b) && b.Role != LayoutRole.Footnote))
        {
            Vote(Glyphs(furniture.Lines), null);
        }

        PdfStructureElement? list = null;
        foreach (object item in order)
        {
            if (item is not BlockDraft { Role: LayoutRole.ListItem })
            {
                list = null;
            }

            switch (item)
            {
                case BlockDraft { Role: LayoutRole.ListItem } block:
                    list ??= Add(root, PdfStructureType.L);
                    PdfStructureElement listItem = Add(list, PdfStructureType.LI);
                    List<PageLine> lines = block.Lines;
                    if (block.Marker is not null && lines[0].Words.Count > 1)
                    {
                        Vote(lines[0].Words[0].Glyphs, Add(listItem, PdfStructureType.Lbl));
                        Vote(lines[0].Words.Skip(1).SelectMany(w => w.Glyphs).Concat(Glyphs(lines.Skip(1))), Add(listItem, PdfStructureType.LBody));
                    }
                    else
                    {
                        Vote(Glyphs(lines), Add(listItem, PdfStructureType.LBody));
                    }

                    break;
                case BlockDraft block:
                    Vote(Glyphs(block.Lines), Add(root, TypeOf(block)));
                    break;
                case TableDraft table:
                    PdfStructureElement element = Add(root, PdfStructureType.Table);
                    foreach (IGrouping<int, CellDraft> row in table.Cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
                    {
                        PdfStructureElement tr = Add(element, PdfStructureType.TR);
                        foreach (CellDraft cell in row.OrderBy(c => c.Column))
                        {
                            PdfStructureElement td = Add(tr, cell.Header ? PdfStructureType.TH : PdfStructureType.TD);
                            if (cell.RowSpan > 1 || cell.ColumnSpan > 1)
                            {
                                PdfDictionary spans = [];
                                spans.Set("RowSpan", cell.RowSpan);
                                spans.Set("ColSpan", cell.ColumnSpan);
                                td.AddAttributes("Table", spans);
                            }

                            Vote(Glyphs(cell.Lines), td);
                        }
                    }

                    break;
                case FigureDraft figure when figure.Decorative:
                    Vote(figure.Images.Cast<object>().Concat(figure.Paths).Concat(Glyphs(figure.Lines)), null);
                    break;
                case FigureDraft figure:
                    PdfStructureElement picture = Add(root, PdfStructureType.Figure);
                    if (figure.AltText is { Length: > 0 } alt)
                    {
                        picture.AlternateText = alt;
                    }
                    else
                    {
                        missing.Add(new PdfAutoTagFigure(content.Index, PageAnalysis.Rect(figure.Box)));
                    }

                    PdfDictionary layout = [];
                    layout.Set("BBox", UserSpace(figure.Box, content).ToArray());
                    picture.AddAttributes("Layout", layout);
                    Vote(figure.Images.Cast<object>().Concat(figure.Paths).Concat(Glyphs(figure.Lines)), picture);
                    break;
            }
        }

        Rewrite(page, votes);
        page.Dictionary.Set("Tabs", (PdfName)"S");
        return made;
    }

    /// <summary>
    /// Wraps each drawing operation in marked content: <c>/Tag &lt;&lt;/MCID n&gt;&gt; BDC ... EMC</c> for the element most of
    /// what it draws belongs to, <c>/Artifact BMC ... EMC</c> for the rest. A painted path is wrapped from where it starts.
    /// </summary>
    private static void Rewrite(PdfPage page, Dictionary<int, Dictionary<PdfStructureElement, int>> votes)
    {
        PdfContent content = page.GetContent();
        List<PdfOperation> result = new(content.Count + (votes.Count * 2));
        int mcid = 0;
        for (int i = 0; i < content.Count; i++)
        {
            PdfOperation op = content[i];
            bool paints = Drawing.Contains(op.Operator) || PathPaint.Contains(op.Operator);
            if (!paints)
            {
                result.Add(op);
                continue;
            }

            // A path's construction joins the painting operator inside the marked content.
            int start = result.Count;
            if (PathPaint.Contains(op.Operator))
            {
                while (start > 0 && PathBuild.Contains(result[start - 1].Operator))
                {
                    start--;
                }
            }

            if (votes.TryGetValue(i, out Dictionary<PdfStructureElement, int>? counts) && counts.Count > 0)
            {
                PdfStructureElement owner = counts.MaxBy(c => c.Value).Key;
                PdfDictionary properties = [];
                properties.Set("MCID", mcid);
                result.Insert(start, new PdfOperation("BDC", (PdfName)owner.Type.Name, properties));
                owner.AddMarkedContent(page, mcid);
                mcid++;
            }
            else
            {
                result.Insert(start, new PdfOperation("BMC", (PdfName)"Artifact"));
            }

            result.Add(op);
            result.Add(new PdfOperation("EMC"));
        }

        page.SetContent(new PdfContent(result));
    }

    private static PdfStructureType TypeOf(BlockDraft block)
    {
        return block.Role switch
        {
            LayoutRole.Title => PdfStructureType.H1,
            LayoutRole.Heading => Math.Clamp(block.Level, 1, 6) switch
            {
                1 => PdfStructureType.H1,
                2 => PdfStructureType.H2,
                3 => PdfStructureType.H3,
                4 => PdfStructureType.H4,
                5 => PdfStructureType.H5,
                _ => PdfStructureType.H6,
            },
            LayoutRole.Caption => PdfStructureType.Caption,
            LayoutRole.Quote => PdfStructureType.BlockQuote,
            LayoutRole.Code => PdfStructureType.Code,
            LayoutRole.Footnote => PdfStructureType.Note,
            _ => PdfStructureType.P,
        };
    }

    /// <summary>A box on the displayed page in the page's default user space, for a figure's /BBox.</summary>
    private static PdfRectangle UserSpace(PageBox box, PageContent content)
    {
        if (!content.ToDisplayed.TryInvert(out PdfMatrix back))
        {
            return new PdfRectangle(box.Left, box.Top, box.Right, box.Bottom);
        }

        PdfPoint a = back.Transform(new PdfPoint(box.Left, box.Top)), b = back.Transform(new PdfPoint(box.Right, box.Bottom));
        return new PdfRectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    private static int OrderOf(object item)
    {
        return item switch
        {
            BlockDraft b => b.Order,
            TableDraft t => t.Order,
            FigureDraft f => f.Order,
            _ => 0,
        };
    }
}

/// <summary>What <see cref="PdfAutoTagger.Tag"/> did.</summary>
/// <param name="Tagged">Whether the document was tagged; false when it already had a structure tree.</param>
/// <param name="Pages">The pages tagged.</param>
/// <param name="Elements">The structure elements written.</param>
/// <param name="FiguresWithoutAltText">The figures tagged without alternate text, which a reader of the tagged file would skip or name only as a figure.</param>
public sealed record PdfAutoTagReport(bool Tagged, int Pages, int Elements, IReadOnlyList<PdfAutoTagFigure> FiguresWithoutAltText);

/// <summary>A figure tagged without alternate text.</summary>
/// <param name="PageIndex">Its page, from 0.</param>
/// <param name="Bounds">Where it is on the displayed page, in points.</param>
public sealed record PdfAutoTagFigure(int PageIndex, RectF Bounds);
