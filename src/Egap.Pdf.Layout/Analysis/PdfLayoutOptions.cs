using Egami.Drawing;

namespace Egap.Pdf.Layout;

/// <summary>How layout analysis runs (design Q2, Q5, Q10): whether tags are trusted, which tables are looked for, the stages a caller replaces, and every threshold.</summary>
public sealed record PdfLayoutOptions
{
    /// <summary>The defaults: tags first, ruled and aligned tables, Egap's own stages.</summary>
    public static PdfLayoutOptions Default { get; } = new();

    /// <summary>Whether a tagged page's structure tree gives its roles and order when the tags pass the sanity check (design Q2). True by default.</summary>
    public bool UseTags { get; init; } = true;

    /// <summary>Which tables are looked for (design Q5).</summary>
    public TableDetection Tables { get; init; } = TableDetection.RuledAndAligned;

    /// <summary>Areas, per page index, that are tables whatever they look like: the caller's fix for a layout the detectors miss.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<RectF>>? TableAreas { get; init; }

    /// <summary>Regions and roles from outside Egap's rules, such as a layout model (design Q1); null for none.</summary>
    public ILayoutRegionDetector? RegionDetector { get; init; }

    /// <summary>A replacement for Egap's table detection: the areas it returns are tables, and Egap finds their cells; null for Egap's own.</summary>
    public ITableDetector? TableDetector { get; init; }

    /// <summary>A replacement for Egap's reading order (XY cut checked against content order); null for Egap's own.</summary>
    public IReadingOrderDetector? ReadingOrderDetector { get; init; }

    /// <summary>The thresholds of the geometric stages.</summary>
    public LayoutThresholds Thresholds { get; init; } = LayoutThresholds.Default;
}

/// <summary>Which tables layout analysis looks for (design Q5).</summary>
public enum TableDetection
{
    /// <summary>None: table text is read as text.</summary>
    None = 0,

    /// <summary>Tables drawn with ruling lines only.</summary>
    Ruled = 1,

    /// <summary>Ruled tables, partly ruled tables and borderless tables found by text alignment.</summary>
    RuledAndAligned = 2,
}

/// <summary>Finds where a page's tables are, in place of Egap's own detection; Egap then finds the cells inside each area.</summary>
public interface ITableDetector
{
    /// <summary>The page's table areas, in points on the displayed page (y down from the top left).</summary>
    IReadOnlyList<RectF> Detect(PdfPage page);
}

/// <summary>Orders a page's blocks, tables and figures, in place of Egap's XY cut.</summary>
public interface IReadingOrderDetector
{
    /// <summary>The indices of <paramref name="bounds"/> in reading order.</summary>
    /// <param name="bounds">Each item's box on the displayed page, in points.</param>
    /// <param name="contentOrder">Each item's place in the page's content stream, which generators mostly write in reading order.</param>
    IReadOnlyList<int> Order(IReadOnlyList<RectF> bounds, IReadOnlyList<int> contentOrder);
}
