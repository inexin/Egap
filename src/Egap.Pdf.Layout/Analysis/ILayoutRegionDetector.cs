using Egami.Drawing;

namespace Egap.Pdf.Layout;

/// <summary>
/// Supplies regions and roles for a page from outside Egap's own rules, such as a layout model the caller already runs
/// (design Q1): Egap ships no model, and this is the place one plugs in. A region the detector names takes precedence over
/// what the rules would find there; the rules still group the text inside it into lines and paragraphs.
/// </summary>
public interface ILayoutRegionDetector
{
    /// <summary>The regions of a page, in any order.</summary>
    /// <param name="page">The page; render it or read its content as the detector needs.</param>
    /// <returns>Regions with their bounds in points on the displayed page: y down from the top left of the crop box, the
    /// page's rotation applied, as <see cref="PdfCanvas"/> draws by default.</returns>
    IReadOnlyList<LayoutRegionHint> Detect(PdfPage page);
}

/// <summary>A region an <see cref="ILayoutRegionDetector"/> found: what it is, where it is and how sure the detector is.</summary>
/// <param name="Role">What the region is.</param>
/// <param name="Bounds">Where it is on the displayed page, in points, y down from the top left.</param>
/// <param name="Confidence">How sure the detector is, from 0 to 1.</param>
public sealed record LayoutRegionHint(LayoutRole Role, RectF Bounds, double Confidence = 1);
