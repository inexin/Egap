using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// One glyph as layout analysis sees it: its Unicode text and box on the displayed page, where its advance starts and ends,
/// the direction of writing as a unit vector, its font, size and fill colour, its text render mode, and the marked content
/// it sits in: its MCID for the structure tree (-1 for none), and for an artifact its <c>/Subtype</c> or <c>/Type</c>
/// (Header, Footer, Watermark, Pagination, Layout...; empty when it says neither), null when it is not one.
/// </summary>
internal sealed record PageGlyph(string Text, PageBox Box, PdfPoint Origin, PdfPoint End, double Dx, double Dy, PdfFont? Font, double Size,
    PdfColor Color, int RenderMode, int Mcid, string? Artifact, double SpaceWidth)
{
    /// <summary>Whether the glyph is marked as an artifact (page furniture, not content).</summary>
    public bool IsArtifact => Artifact is not null;

    /// <summary>A space glyph: it separates words and is not part of one.</summary>
    public bool IsSpace => Text.Length > 0 && Text.All(char.IsWhiteSpace);

    /// <summary>Render mode 3 or 7: text that is not drawn, such as an OCR layer over a scan; it still counts as text.</summary>
    public bool IsInvisible => RenderMode is 3 or 7;
}

/// <summary>A word: glyphs between spaces or wide gaps, in reading order within the line.</summary>
internal sealed class PageWord(IReadOnlyList<PageGlyph> glyphs)
{
    public IReadOnlyList<PageGlyph> Glyphs { get; } = glyphs;

    public string Text { get; } = Join(glyphs);

    public PageBox Box { get; } = Around(glyphs);

    /// <summary>The size of most of the word's glyphs.</summary>
    public double Size => Glyphs[Glyphs.Count / 2].Size;

    public PdfFont? Font => Glyphs[Glyphs.Count / 2].Font;

    public override string ToString()
    {
        return Text;
    }

    private static string Join(IReadOnlyList<PageGlyph> glyphs)
    {
        if (glyphs.Count == 1)
        {
            return glyphs[0].Text;
        }

        System.Text.StringBuilder text = new(glyphs.Count);
        for (int i = 0; i < glyphs.Count; i++)
        {
            text.Append(glyphs[i].Text);
        }

        return text.ToString();
    }

    private static PageBox Around(IReadOnlyList<PageGlyph> glyphs)
    {
        PageBox all = PageBox.Empty;
        for (int i = 0; i < glyphs.Count; i++)
        {
            all = all.Union(glyphs[i].Box);
        }

        return all;
    }
}

/// <summary>A line: words on one baseline in one direction, as Phase 3's text extraction builds them (content order).</summary>
internal sealed class PageLine(IReadOnlyList<PageWord> words)
{
    private double? size;
    private double? baseline;

    public IReadOnlyList<PageWord> Words { get; } = words;

    private string? text;

    public string Text => text ??= Join(Words);

    public PageBox Box { get; } = Around(words);

    /// <summary>The first glyph's direction of writing: (1, 0) for ordinary horizontal text.</summary>
    public (double Dx, double Dy) Direction => (Words[0].Glyphs[0].Dx, Words[0].Glyphs[0].Dy);

    /// <summary>Whether the line runs left to right along the page (within a degree or so).</summary>
    public bool IsHorizontal => Direction.Dx > 0.9998;

    /// <summary>
    /// Where the line sits down the page: the baseline most of its glyphs share for horizontal text, so a taller glyph (a
    /// symbol from another font, a superscript) does not move it; the centre of the box otherwise.
    /// </summary>
    public double Baseline => baseline ??= IsHorizontal ? Most(g => Math.Round(g.Origin.Y, 1)) : Box.CenterY;

    /// <summary>The most common glyph size on the line.</summary>
    public double Size => size ??= Most(g => Math.Round(g.Size, 1));

    private static string Join(IReadOnlyList<PageWord> words)
    {
        if (words.Count == 1)
        {
            return words[0].Text;
        }

        System.Text.StringBuilder text = new();
        for (int i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                text.Append(' ');
            }

            text.Append(words[i].Text);
        }

        return text.ToString();
    }

    private static PageBox Around(IReadOnlyList<PageWord> words)
    {
        PageBox all = PageBox.Empty;
        for (int i = 0; i < words.Count; i++)
        {
            all = all.Union(words[i].Box);
        }

        return all;
    }

    /// <summary>The value most of the line's glyphs have; the first seen wins a tie, as grouping in order would give.</summary>
    private double Most(Func<PageGlyph, double> key)
    {
        Dictionary<double, int> counts = [];
        foreach (PageWord word in Words)
        {
            foreach (PageGlyph glyph in word.Glyphs)
            {
                double value = key(glyph);
                counts[value] = counts.GetValueOrDefault(value) + 1;
            }
        }

        return counts.MaxBy(c => c.Value).Key;
    }

    public override string ToString()
    {
        return Text;
    }
}

/// <summary>
/// A painted path on the displayed page: its segments with every point in page space, its bounds, whether it is stroked
/// (with the line width in page points) and filled, its colours and the marked content it sits in.
/// </summary>
internal sealed record PagePath(IReadOnlyList<PdfPathSegment> Segments, PageBox Bounds, bool Stroked, bool Filled, double LineWidth,
    PdfColor StrokeColor, PdfColor FillColor, int Mcid, string? Artifact)
{
    public bool IsArtifact => Artifact is not null;
}

/// <summary>
/// An image placed on the page: the corners of its unit square on the displayed page (top left, top right, bottom right,
/// bottom left of the image as stored), their bounds, the XObject stream or inline image it comes from, and its marked content.
/// </summary>
internal sealed record PageImage(IReadOnlyList<PdfPoint> Corners, PageBox Bounds, PdfStream? Stream, PdfInlineImage? Inline, string? Name,
    PdfMatrix Matrix, int Mcid, string? Artifact)
{
    public bool IsArtifact => Artifact is not null;
}

/// <summary>A shading painted with <c>sh</c> over the clip; its bounds are the clip's.</summary>
internal sealed record PageShading(PageBox Bounds, int Mcid, string? Artifact);

/// <summary>
/// A visible annotation on the page: its box, subtype and text (<c>/Contents</c>), author for markup annotations, and for
/// links the URI or the target page and its displayed top.
/// </summary>
internal sealed record PageAnnotation(PdfAnnotation Annotation, PageBox Box, string Subtype, string? Contents, string? Author, string? Uri,
    int? TargetPage, double? TargetTop)
{
    /// <summary>A text markup annotation's quadrilaterals on the displayed page, one box each; empty for other annotations.</summary>
    public IReadOnlyList<PageBox> Quads { get; init; } = [];
}

/// <summary>An outline entry: its title, its depth (0 for top-level entries) and where it goes, if anywhere.</summary>
internal sealed record OutlineEntry(string Title, int Level, int? Page, double? Top);

/// <summary>
/// Everything layout analysis reads from one page (Build Plan task 2): the displayed page's size, glyphs in content order
/// with their words and lines, painted paths, images, shadings, annotations and the outline entries that point here.
/// </summary>
internal sealed class PageContent
{
    public required int Index { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    /// <summary>The page's <c>/Rotate</c> in quarter turns clockwise, 0 to 3.</summary>
    public required int QuarterTurns { get; init; }

    /// <summary>The matrix from the page's user space to the displayed page.</summary>
    public required PdfMatrix ToDisplayed { get; init; }

    public required IReadOnlyList<PageGlyph> Glyphs { get; init; }

    public required IReadOnlyList<PageLine> Lines { get; init; }

    public required IReadOnlyList<PagePath> Paths { get; init; }

    public required IReadOnlyList<PageImage> Images { get; init; }

    public required IReadOnlyList<PageShading> Shadings { get; init; }

    public required IReadOnlyList<PageAnnotation> Annotations { get; init; }

    /// <summary>The outline entries whose destination is this page, in outline order.</summary>
    public required IReadOnlyList<OutlineEntry> Outline { get; init; }

    public required IReadOnlyList<PdfWarning> Warnings { get; init; }

    /// <summary>For each glyph, path and image, the index of the page's own content operation that draws it (a form's <c>Do</c> for what the form draws).</summary>
    public IReadOnlyDictionary<object, int> Operations { get; init; } = new Dictionary<object, int>();

    public IEnumerable<PageWord> Words => Lines.SelectMany(l => l.Words);

    public PageBox Bounds => new(0, 0, Width, Height);
}
