namespace Egap.Pdf.Conversion;

/// <summary>
/// Every threshold the geometric stages use (design Q1: every threshold an option). Distances are in multiples of the text
/// size unless they say otherwise, so they hold for any font size. The defaults are tuned against the comparison data sets
/// and may change in a minor version; the properties themselves stay.
/// </summary>
public sealed record LayoutThresholds
{
    /// <summary>The thresholds Egap uses unless told otherwise; their values may change in a minor version as the analysis is tuned.</summary>
    public static LayoutThresholds Default { get; } = new();

    /// <summary>A gap inside a line wider than this many times the page's usual word gap, and than <see cref="GutterSize"/>, cuts the line (a column gutter or a table's column gap).</summary>
    public double GutterWordGaps { get; init; } = 3;

    /// <summary>The least gap, in text sizes, that cuts a line.</summary>
    public double GutterSize { get; init; } = 1.5;

    /// <summary>Two lines join one block when the step from one baseline to the next is at most this many times the page's usual line pitch for their size.</summary>
    public double LinePitch { get; init; } = 1.3;

    /// <summary>Lines whose sizes differ by more than this ratio do not join one block.</summary>
    public double SizeRatio { get; init; } = 1.15;

    /// <summary>The share of the narrower line's width two lines must overlap by, side to side, to join one block.</summary>
    public double Overlap { get; init; } = 0.3;

    /// <summary>The least gap between blocks, in points, that the XY cut treats as a column gutter.</summary>
    public double ColumnGap { get; init; } = 6;

    /// <summary>The least gap between blocks, in points, that the XY cut treats as a break between bands down the page.</summary>
    public double BandGap { get; init; } = 2;

    /// <summary>
    /// The content order wins on a page when the geometric order agrees with it on fewer than this share of block pairs and
    /// the content order itself reads down the page and across columns.
    /// </summary>
    public double OrderAgreement { get; init; } = 0.6;
}
