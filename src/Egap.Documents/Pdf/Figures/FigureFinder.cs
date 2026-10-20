using Egami.Drawing;

using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// Figures (design Q7, Build Plan task 9). Images are figures with their original bytes kept (the XObject stream or inline
/// image, its filters, colour space, soft mask and placement, all in <see cref="PageImage"/>). Vector graphics that are not
/// table rules, underlines, separators or backgrounds are grouped by overlap and nearness, and a group big enough is a
/// figure; images and drawings that touch are one figure. Short text inside a figure (axis labels, callouts) belongs to it.
/// A figure whose content is all artifacts is decorative. <see cref="Record"/> draws a figure's region as an Egami Picture
/// through Phase 4's renderer, which a writer turns into a PNG or SVG.
/// </summary>
internal static class FigureFinder
{
    /// <summary>How near, in points, two pieces of a drawing must be to belong to one figure.</summary>
    private const double Near = 6;

    /// <summary>The least width and height, in points, of a vector figure.</summary>
    private const double Least = 24;

    /// <summary>
    /// The figures of a page whose tables are already found (<paramref name="tables"/>): their images and paths, and the
    /// glyphs inside them; <paramref name="glyphs"/> are the content glyphs not yet placed, and <paramref name="found"/> the
    /// figures earlier stages found.
    /// </summary>
    public static List<FigureDraft> Find(PageContent content, IReadOnlyList<PageGlyph> glyphs, IReadOnlyList<TableDraft> tables, IReadOnlyList<FigureDraft>? found = null)
    {
        // Images and drawings an earlier stage placed (tags, a region detector) are not looked at again.
        HashSet<PageImage> usedImages = new((found ?? []).SelectMany(f => f.Images), ReferenceEqualityComparer.Instance);
        HashSet<PagePath> usedPaths = new((found ?? []).SelectMany(f => f.Paths), ReferenceEqualityComparer.Instance);
        List<PageLine> lines = PageReader.Lines(glyphs);
        List<(PageBox Box, PageImage? Image, PagePath? Path)> parts = [];
        foreach (PageImage image in content.Images.Where(i => !usedImages.Contains(i)))
        {
            if (image.Bounds.Width >= 4 && image.Bounds.Height >= 4 && !Background(image.Bounds, content, lines))
            {
                parts.Add((image.Bounds.Intersect(content.Bounds), image, null));
            }
        }

        List<PageBox> rules = BlockBuilder.Rules(content.Paths);
        foreach (PagePath path in content.Paths.Where(p => !usedPaths.Contains(p)))
        {
            // Paths in tables are the tables'; backgrounds and page frames of half the page or more join nothing.
            if (tables.Any(t => !Grow(t.Box, 6).Intersect(path.Bounds).IsEmpty && Grow(t.Box, 6).Contains(path.Bounds.CenterX, path.Bounds.CenterY)) || Background(path.Bounds, content, lines)
                || path.Bounds.Area >= content.Bounds.Area * 0.5)
            {
                continue;
            }

            // Only what lands on the page counts: a path drawn far past its edge is cut to it.
            PageBox onPage = path.Bounds.Intersect(content.Bounds);
            if (!onPage.IsEmpty)
            {
                parts.Add((onPage, null, path));
            }
        }

        // Parts that touch or come within a few points of each other are one drawing.
        int[] parent = [.. Enumerable.Range(0, parts.Count)];
        List<int> byLeft = [.. Enumerable.Range(0, parts.Count).OrderBy(i => parts[i].Box.Left)];
        for (int k = 0; k < byLeft.Count; k++)
        {
            PageBox a = Grow(parts[byLeft[k]].Box, Near);
            for (int m = k + 1; m < byLeft.Count && parts[byLeft[m]].Box.Left <= a.Right; m++)
            {
                if (!a.Intersect(parts[byLeft[m]].Box).IsEmpty)
                {
                    parent[Find(parent, byLeft[m])] = Find(parent, byLeft[k]);
                }
            }
        }

        List<FigureDraft> figures = [];
        foreach (IGrouping<int, int> group in Enumerable.Range(0, parts.Count).GroupBy(i => Find(parent, i)))
        {
            List<(PageBox Box, PageImage? Image, PagePath? Path)> members = [.. group.Select(i => parts[i])];
            PageBox box = PageBox.Around(members.Select(m => m.Box));
            bool hasImage = members.Any(m => m.Image is not null);

            // Rules on their own (underlines, separators, borders, the grid of a table) are left to text and tables; a lone shaded box is a
            // background; anything else drawn needs some size.
            bool rulesOnly = members.All(m => m.Path is PagePath p && (Furniture(p, lines, content) || Thin(p.Bounds)));
            bool shadedBox = members.Count == 1 && members[0].Path is { Filled: true } lone && !lone.Segments.Any(x => x.Verb == PdfPathVerb.CurveTo);
            // Drawings around most of a table are its shading and frame.
            bool aroundTable = tables.Any(t => t.Box.Intersect(box) is { IsEmpty: false } common && common.Area >= t.Box.Area * 0.5 && common.Area >= box.Area * 0.5);
            if (!hasImage && (rulesOnly || shadedBox || aroundTable || box.Width < Least || box.Height < Least))
            {
                continue;
            }

            FigureDraft figure = new(box);
            figure.Images.AddRange(members.Select(m => m.Image).OfType<PageImage>());
            figure.Paths.AddRange(members.Select(m => m.Path).OfType<PagePath>());
            figure.Decorative = figure.Images.All(i => i.IsArtifact) && figure.Paths.All(p => p.IsArtifact);
            figures.Add(figure);
        }

        // Lines inside a figure are its labels, and so are short ones, or runs of short words, just outside it (axis labels, a
        // legend); the figure's box
        // grows to take them in.
        HashSet<PageLine> taken = new(ReferenceEqualityComparer.Instance);
        foreach (FigureDraft figure in figures)
        {
            PageBox inner = Grow(figure.Box, 2);
            foreach (PageLine line in lines.Where(l => !taken.Contains(l)))
            {
                bool inside = inner.Contains(line.Box.CenterX, line.Box.CenterY) && line.Box.Width <= figure.Box.Width * 0.9 + 1;
                bool labels = line.Words.Count <= 4 || line.Words.Average(w => w.Text.Length) <= 6;
                bool beside = labels && Grow(figure.Box, line.Size * 1.5).Contains(line.Box.CenterX, line.Box.CenterY)
                    && line.Box.Left >= figure.Box.Left - line.Size && line.Box.Right <= figure.Box.Right + line.Size;
                if (inside || beside)
                {
                    figure.Lines.Add(line);
                    taken.Add(line);
                }
            }

            figure.Box = figure.Lines.Aggregate(figure.Box, (box, line) => box.Union(line.Box));
        }

        figures.RemoveAll(TextBox);
        return figures;
    }

    /// <summary>
    /// Whether a "figure" is really text in a box: no picture and no curves, only boxes and rules, around text that fills it
    /// (a framed note, a shaded banner, a screen drawn as text): its lines cover a quarter of the box, or three lines of four
    /// words or more cover an eighth of it.
    /// </summary>
    private static bool TextBox(FigureDraft figure)
    {
        if (figure.Images.Count > 0 || figure.Paths.Any(p => p.Segments.Any(s => s.Verb == PdfPathVerb.CurveTo)) || figure.Lines.Count == 0)
        {
            return false;
        }

        double share = figure.Lines.Sum(l => l.Box.Area) / Math.Max(figure.Box.Area, 1);
        return share >= 0.25 || (figure.Lines.Count(l => l.Words.Count >= 4) >= 3 && share >= 0.12);
    }

    /// <summary>
    /// Whether an image or a drawing sits in a text line, as an icon or a symbol does: no taller than one and a half lines and
    /// on the same baseline band as text beside it.
    /// </summary>
    public static bool InLine(FigureDraft figure, IEnumerable<PageLine> lines)
    {
        return lines.Any(l => figure.Box.Height <= l.Size * 1.5 && Math.Min(l.Box.Bottom, figure.Box.Bottom) - Math.Max(l.Box.Top, figure.Box.Top) > figure.Box.Height * 0.5
            && l.Words.Any(w => w.Box.Right <= figure.Box.Left + 1 || w.Box.Left >= figure.Box.Right - 1));
    }

    /// <summary>Whether text runs beside a figure on the same band, so the figure floats with text wrapped around it.</summary>
    public static bool Wrapped(FigureDraft figure, IEnumerable<PageLine> lines)
    {
        return lines.Count(l => l.Box.CenterY > figure.Box.Top && l.Box.CenterY < figure.Box.Bottom && (l.Box.Right < figure.Box.Left || l.Box.Left > figure.Box.Right)) >= 2;
    }

    /// <summary>A figure's region of the page as an Egami Picture, drawn by Phase 4's renderer: vectors stay vectors.</summary>
    public static Egami.Drawing.Picture Record(PdfPage page, FigureDraft figure)
    {
        RectF region = new((float)figure.Box.Left, (float)figure.Box.Top, (float)Math.Max(figure.Box.Width, 1), (float)Math.Max(figure.Box.Height, 1));
        return page.ToPicture(new PdfRenderOptions { Region = region, Annotations = PdfAnnotationRendering.None });
    }

    /// <summary>
    /// Whether a path is a rule rather than drawing: a thin line under or through text (an underline, a strike) or a long one
    /// (a separator, a border). A group of nothing but such rules is no figure; joined to other drawing, they are its axes.
    /// </summary>
    private static bool Furniture(PagePath path, List<PageLine> lines, PageContent content)
    {
        PageBox b = path.Bounds;
        bool thin = (b.Height < 2.5 && b.Width > 4) || (b.Width < 2.5 && b.Height > 4);
        if (!thin)
        {
            return false;
        }

        // An underline or strike sits under or through a line of text and is no wider than it by much.
        bool underText = lines.Any(l => b.Height < 2.5 && b.CenterY >= l.Box.Top && b.CenterY <= l.Box.Bottom + (l.Size * 0.4)
            && b.Left >= l.Box.Left - 2 && b.Right <= l.Box.Right + 2);

        // A long separator or page border.
        bool separator = b.Width > content.Width * 0.3 || b.Height > content.Height * 0.3;
        return underText || separator;
    }

    /// <summary>Whether a box is a background: a filled area behind text lines (a shaded box, a page colour) with text inside it.</summary>
    private static bool Background(PageBox box, PageContent content, List<PageLine> lines)
    {
        if (box.Area >= content.Bounds.Area * 0.9)
        {
            return true;
        }

        int inside = lines.Count(l => box.Contains(l.Box.CenterX, l.Box.CenterY));
        return inside >= 3 && lines.Where(l => box.Contains(l.Box.CenterX, l.Box.CenterY)).Average(l => l.Box.Width) > box.Width * 0.5;
    }

    /// <summary>Whether a box is a thin horizontal or vertical line: a rule of a table, a frame or a separator.</summary>
    private static bool Thin(PageBox box)
    {
        return (box.Height < 2.5 && box.Width > box.Height) || (box.Width < 2.5 && box.Height > box.Width);
    }

    private static PageBox Grow(PageBox box, double by)
    {
        return new(box.Left - by, box.Top - by, box.Right + by, box.Bottom + by);
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }
}
