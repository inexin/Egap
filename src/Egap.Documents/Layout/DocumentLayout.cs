namespace Egap.Documents.Layout;

/// <summary>How <see cref="Document.Layout(LayoutOptions?)"/> lays a document out.</summary>
public sealed class LayoutOptions
{
    /// <summary>Where fonts come from; <see cref="FontSettings.Default"/> when null.</summary>
    public FontSettings? Fonts { get; init; }
}

/// <summary>
/// A document laid out into pages (Phase 6 design Q11 and Q13): an immutable snapshot, each page a tree of boxes linked to
/// the model nodes they show. An edit to the document needs a new layout.
/// </summary>
public sealed class DocumentLayout
{
    private Dictionary<Node, List<(PageLayout Page, LayoutBox Box)>>? index;

    internal DocumentLayout(Document document, IReadOnlyList<PageLayout> pages, IReadOnlyList<FontSubstitution> substitutions)
    {
        Document = document;
        Pages = pages;
        FontSubstitutions = substitutions;
    }

    /// <summary>The document laid out.</summary>
    public Document Document { get; }

    /// <summary>The pages in order.</summary>
    public IReadOnlyList<PageLayout> Pages { get; }

    /// <summary>The fonts used in place of the ones the document asked for, each once.</summary>
    public IReadOnlyList<FontSubstitution> FontSubstitutions { get; }

    /// <summary>The boxes that show <paramref name="node"/>, with their pages, in page order; empty when it is not laid out.</summary>
    public IReadOnlyList<(PageLayout Page, LayoutBox Box)> FindBoxes(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (index is null)
        {
            Dictionary<Node, List<(PageLayout, LayoutBox)>> built = [];
            foreach (PageLayout page in Pages)
            {
                foreach (LayoutBox box in page.Boxes.SelectMany(b => b.Descendants()))
                {
                    if (box.Node is { } shown)
                    {
                        if (!built.TryGetValue(shown, out List<(PageLayout, LayoutBox)>? list))
                        {
                            list = [];
                            built[shown] = list;
                        }

                        list.Add((page, box));
                    }
                }
            }

            index = built;
        }

        return index.TryGetValue(node, out List<(PageLayout, LayoutBox)>? found) ? found : [];
    }

    /// <summary>The text position nearest the point (in points from the page's top left), or null on a page without text.</summary>
    public DocumentPosition? HitTest(PageLayout page, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!Pages.Contains(page))
        {
            throw new ArgumentException("The page is not one of this layout's.", nameof(page));
        }

        return LayoutInteraction.HitTest(page, x, y);
    }

    /// <summary>Where the caret for <paramref name="position"/> is drawn: its page and a rectangle a point wide, the line's height.</summary>
    public PageRect? GetCaret(DocumentPosition position)
    {
        return LayoutInteraction.Caret(this, position);
    }

    /// <summary>The rectangles that show the text from <paramref name="start"/> to <paramref name="end"/> selected, line by line across columns and pages.</summary>
    public IReadOnlyList<PageRect> GetSelectionRects(DocumentPosition start, DocumentPosition end)
    {
        return LayoutInteraction.Selection(this, start, end);
    }

    /// <summary>The model inline and the offset in its text a position falls in, or null at a paragraph's end or in its list label.</summary>
    public (Inline Node, int Offset)? Locate(DocumentPosition position)
    {
        return LayoutInteraction.Locate(this, position);
    }

    /// <summary>The page <paramref name="node"/> starts on, or null when it is not laid out.</summary>
    public PageLayout? PageOf(Node node)
    {
        IReadOnlyList<(PageLayout Page, LayoutBox Box)> boxes = FindBoxes(node);
        return boxes.Count == 0 ? null : boxes[0].Page;
    }
}

/// <summary>One laid-out page: its size, its number as printed, and its areas.</summary>
public sealed class PageLayout
{
    internal PageLayout(int index, string number, Section section, double width, double height, IReadOnlyList<LayoutBox> boxes)
    {
        Index = index;
        Number = number;
        Section = section;
        Width = width;
        Height = height;
        Boxes = boxes;
    }

    /// <summary>The page's place in the document, from 0.</summary>
    public int Index { get; }

    /// <summary>The page number as the document prints it (with the section's restart and number style).</summary>
    public string Number { get; }

    /// <summary>The section the page belongs to.</summary>
    public Section Section { get; }

    /// <summary>The page width in points.</summary>
    public double Width { get; }

    /// <summary>The page height in points.</summary>
    public double Height { get; }

    /// <summary>The page's areas: header, body columns, notes and footer, then floating objects.</summary>
    public IReadOnlyList<LayoutBox> Boxes { get; }

    /// <summary>
    /// Draws the page onto <paramref name="canvas"/> with its top left corner at the canvas's origin, one canvas unit to the
    /// point: any Egami canvas, such as a raster, an SVG or, through Egap.Pdf.Layout, a PDF page.
    /// </summary>
    public void Draw(Egami.Drawing.Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        PageDrawing.Draw(this, canvas);
    }

    /// <summary>The page drawn on white at <paramref name="dpi"/> dots per inch.</summary>
    public Egami.Raster<Egami.Pixels.Rgba8> Render(double dpi = 96)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        double scale = dpi / 72;
        Egami.Raster<Egami.Pixels.Rgba8> raster = Egami.Raster.Create(Math.Max(1, (int)Math.Ceiling(Width * scale)), Math.Max(1, (int)Math.Ceiling(Height * scale)), new Egami.Pixels.Rgba8(255, 255, 255, 255));
        using Egami.Drawing.RasterCanvas<Egami.Pixels.Rgba8> canvas = new(raster);
        canvas.Scale((float)scale, (float)scale);
        Draw(canvas);
        return raster;
    }

    /// <summary>The body text's lines on this page, in reading order.</summary>
    public IEnumerable<LineBox> BodyLines => Boxes.OfType<AreaBox>().Where(a => a.Area == PageArea.Body).SelectMany(a => a.Descendants()).OfType<LineBox>();
}
