namespace Egap.Pdf.Layout;

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

    public string Text { get; } = string.Concat(glyphs.Select(g => g.Text));

    public PageBox Box { get; } = PageBox.Around(glyphs.Select(g => g.Box));

    /// <summary>The size of most of the word's glyphs.</summary>
    public double Size => Glyphs[Glyphs.Count / 2].Size;

    public PdfFont? Font => Glyphs[Glyphs.Count / 2].Font;

    public override string ToString()
    {
        return Text;
    }
}

/// <summary>A line: words on one baseline in one direction, as Phase 3's text extraction builds them (content order).</summary>
internal sealed class PageLine(IReadOnlyList<PageWord> words)
{
    private double? size;

    public IReadOnlyList<PageWord> Words { get; } = words;

    public string Text { get; } = string.Join(' ', words.Select(w => w.Text));

    public PageBox Box { get; } = PageBox.Around(words.Select(w => w.Box));

    /// <summary>The first glyph's direction of writing: (1, 0) for ordinary horizontal text.</summary>
    public (double Dx, double Dy) Direction => (Words[0].Glyphs[0].Dx, Words[0].Glyphs[0].Dy);

    /// <summary>Whether the line runs left to right along the page (within a degree or so).</summary>
    public bool IsHorizontal => Direction.Dx > 0.9998;

    /// <summary>The most common glyph size on the line.</summary>
    public double Size => size ??= Words.SelectMany(w => w.Glyphs).GroupBy(g => Math.Round(g.Size, 1)).MaxBy(g => g.Count())!.Key;

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
    int? TargetPage, double? TargetTop);

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

    public IEnumerable<PageWord> Words => Lines.SelectMany(l => l.Words);

    public PageBox Bounds => new(0, 0, Width, Height);
}
