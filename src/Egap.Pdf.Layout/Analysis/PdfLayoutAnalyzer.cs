using Egami.Drawing;

namespace Egap.Pdf.Layout;

/// <summary>
/// Layout analysis of PDF pages (design Q10, Build Plan task 10): blocks with roles, tables and figures in reading order,
/// without building a document model. Tagged pages whose tags pass a sanity check are read from their tags (design Q2);
/// others by geometric rules (design Q1, Q3 to Q7), every threshold an option and the main stages replaceable through
/// <see cref="PdfLayoutOptions"/>.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("report.pdf");
/// foreach (PdfPageLayout page in PdfLayoutAnalyzer.Analyze(pdf))
/// {
///     foreach (LayoutBlock block in page.Blocks)
///     {
///         Console.WriteLine($"{block.Role} {block.Bounds} {block.Confidence:P0}: {block.Text}");
///     }
/// }
/// </code>
/// </example>
public static class PdfLayoutAnalyzer
{
    /// <summary>
    /// Analyses every page of a document. Roles that need the whole document come out right: headers and footers repeat
    /// across pages, heading levels rank across the document, and tables and paragraphs that run on to the next page say so.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="options">How to analyse; <see cref="PdfLayoutOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Stops between pages.</param>
    public static IReadOnlyList<PdfPageLayout> Analyze(PdfDocument document, PdfLayoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Analyze(document, Enumerable.Range(0, document.Pages.Count), options, cancellationToken);
    }

    /// <summary>
    /// Analyses one page. Roles are judged from that page alone (with the document's outline and tags), so a running header
    /// that only repetition would show stays text.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="pageIndex">The page's index, from 0.</param>
    /// <param name="options">How to analyse; <see cref="PdfLayoutOptions.Default"/> when null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    public static PdfPageLayout Analyze(PdfDocument document, int pageIndex, PdfLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.Pages.Count);
        return Analyze(document, [pageIndex], options, default)[0];
    }

    private static List<PdfPageLayout> Analyze(PdfDocument document, IEnumerable<int> indices, PdfLayoutOptions? options, CancellationToken cancellationToken)
    {
        (List<PageDraft> drafts, _) = Drafts(document, indices, options ?? PdfLayoutOptions.Default, cancellationToken);
        return [.. drafts.Select(d => Publish(document.Pages[d.Content.Index], d))];
    }

    /// <summary>Every stage up to the public result: the pages' drafts with their roles, and the footnotes linked to their markers.</summary>
    internal static (List<PageDraft> Drafts, List<Roles.NoteLink> Notes) Drafts(PdfDocument document, IEnumerable<int> indices, PdfLayoutOptions options,
        CancellationToken cancellationToken)
    {
        List<OutlineEntry> outline = PageReader.ReadOutline(document);
        TagIndex? tags = options.UseTags ? TagIndex.Read(document) : null;
        List<PageDraft> drafts = [];
        foreach (int index in indices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            drafts.Add(PageAnalysis.Analyze(document, index, tags, outline, options));
        }

        return (drafts, Finish(drafts, outline));
    }

    /// <summary>
    /// The document-level stages: roles over all pages, artifact blocks that are not furniture dropped, tables and paragraphs
    /// carried on across pages.
    /// </summary>
    internal static List<Roles.NoteLink> Finish(List<PageDraft> drafts, IReadOnlyList<OutlineEntry> outline)
    {
        List<Roles.NoteLink> links = Roles.Assign(drafts, outline);
        foreach (PageDraft draft in drafts)
        {
            draft.Blocks.RemoveAll(b => b.Artifact && b.Role is not (LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber));
        }

        TableJoins.AcrossPages(drafts);
        for (int i = 1; i < drafts.Count; i++)
        {
            BlockDraft? before = drafts[i - 1].Blocks.Where(Body).MaxBy(b => b.Order);
            BlockDraft? next = drafts[i].Blocks.Where(Body).MinBy(b => b.Order);
            // The last body paragraph of one page and the first of the next, with no table or figure after or before them.
            if (before is { Role: LayoutRole.Text } && next is { Role: LayoutRole.Text, Tag: null }
                && drafts[i - 1].Tables.All(t => t.Order < before.Order) && drafts[i - 1].Figures.All(f => f.Order < before.Order)
                && drafts[i].Tables.All(t => t.Order > next.Order) && drafts[i].Figures.All(f => f.Order > next.Order)
                && Paragraphs.Carries(before, next, Paragraphs.IndentStyle(drafts[i].Blocks)))
            {
                next.Continues = true;
            }
        }

        return links;
    }

    private static bool Body(BlockDraft block)
    {
        return block.Role is not (LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber or LayoutRole.Footnote);
    }

    /// <summary>The public layout of a page: headers first, then the body in reading order, then notes, footers and page numbers.</summary>
    internal static PdfPageLayout Publish(PdfPage page, PageDraft draft)
    {
        List<object> items =
        [
            .. draft.Blocks.Where(b => b.Role == LayoutRole.PageHeader).OrderBy(b => b.Box.Top),
            .. draft.Blocks.Where(Body).Cast<object>().Concat(draft.Tables).Concat(draft.Figures).OrderBy(OrderOf),
            .. draft.Blocks.Where(b => b.Role == LayoutRole.Footnote).OrderBy(b => b.Box.Top),
            .. draft.Blocks.Where(b => b.Role is LayoutRole.PageFooter or LayoutRole.PageNumber).OrderBy(b => b.Box.Top),
        ];

        IReadOnlyList<PdfPlacedImage>? placed = null;
        List<LayoutElement> elements = [];
        foreach (object item in items)
        {
            int order = elements.Count;
            switch (item)
            {
                case BlockDraft b:
                    elements.Add(new LayoutBlock(PageAnalysis.Rect(b.Box), order, b.Confidence, b.Role, b.Text, b.Level, b.Marker, b.Continues, [.. b.Lines.Select(Line)]));
                    break;
                case TableDraft t:
                    elements.Add(new DetectedTable(PageAnalysis.Rect(t.Box), order, t.Confidence, t.Rows, t.Columns, t.IsRuled, t.Continues, t.RepeatsHeader,
                        [.. t.Cells.Select(c => new TableCell(c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Header, Paragraphs.Text(c.Lines), PageAnalysis.Rect(c.Box), [.. c.Lines.Select(Line)]))]));
                    break;
                case FigureDraft f:
                    placed ??= f.Images.Count > 0 ? Placed(page) : null;
                    List<PageLine> text = [.. draft.Content.Lines];
                    elements.Add(new LayoutFigure(PageAnalysis.Rect(f.Box), order, f.Tag is null ? 0.7 : 1, page,
                        [.. f.Images.Select(i => new LayoutImage(Match(i, placed, draft.Content), PageAnalysis.Rect(i.Bounds), [.. i.Corners.Select(PageAnalysis.Point)]))],
                        [.. f.Lines.Select(Line)], f.AltText, f.Decorative, FigureFinder.InLine(f, text), FigureFinder.Wrapped(f, text)));
                    break;
            }
        }

        return new PdfPageLayout(draft.Content.Index, (float)draft.Content.Width, (float)draft.Content.Height, elements, draft.FromTags, draft.Content.Warnings);
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

    private static LayoutLine Line(PageLine line)
    {
        return new LayoutLine(line.Text, PageAnalysis.Rect(line.Box), (float)line.Baseline, [.. line.Words.Select(Word)]);
    }

    private static LayoutWord Word(PageWord word)
    {
        PageGlyph glyph = word.Glyphs[word.Glyphs.Count / 2];
        return new LayoutWord(word.Text, PageAnalysis.Rect(word.Box), FontTraits.Family(glyph.Font), (float)glyph.Size, FontTraits.IsBold(glyph),
            FontTraits.IsItalic(glyph.Font), glyph.Color);
    }

    internal static IReadOnlyList<PdfPlacedImage>? Placed(PdfPage page)
    {
        try
        {
            return page.GetImages();
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>The stored image a placed image is: the one drawn with the same matrix.</summary>
    internal static PdfImage? Match(PageImage image, IReadOnlyList<PdfPlacedImage>? placed, PageContent content)
    {
        foreach (PdfPlacedImage candidate in placed ?? [])
        {
            PdfMatrix m = candidate.Matrix * content.ToDisplayed;
            if (Math.Abs(m.A - image.Matrix.A) < 0.01 && Math.Abs(m.B - image.Matrix.B) < 0.01 && Math.Abs(m.C - image.Matrix.C) < 0.01
                && Math.Abs(m.D - image.Matrix.D) < 0.01 && Math.Abs(m.E - image.Matrix.E) < 0.01 && Math.Abs(m.F - image.Matrix.F) < 0.01)
            {
                return candidate.Image;
            }
        }

        return null;
    }
}
