using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// A box on the displayed page in points: origin at the top left of the crop box as a viewer shows it (the page's
/// <c>/Rotate</c> applied), y down, as <see cref="PdfCanvas"/> draws by default. Every stage of layout analysis works in
/// this space, so a rotated page reads like an upright one.
/// </summary>
internal readonly record struct PageBox(double Left, double Top, double Right, double Bottom)
{
    public static PageBox Empty { get; } = new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public double CenterX => (Left + Right) / 2;

    public double CenterY => (Top + Bottom) / 2;

    public bool IsEmpty => Right < Left || Bottom < Top;

    public double Area => IsEmpty ? 0 : Width * Height;

    public PageBox Union(PageBox other)
    {
        return new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public PageBox Intersect(PageBox other)
    {
        return new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
    }

    /// <summary>The area two boxes share.</summary>
    public double Overlap(PageBox other)
    {
        return Intersect(other).Area;
    }

    public bool Contains(double x, double y, double tolerance = 0)
    {
        return x >= Left - tolerance && x <= Right + tolerance && y >= Top - tolerance && y <= Bottom + tolerance;
    }

    /// <summary>The box around <paramref name="boxes"/>, or <see cref="Empty"/> when there are none.</summary>
    public static PageBox Around(IEnumerable<PageBox> boxes)
    {
        PageBox all = Empty;
        foreach (PageBox box in boxes)
        {
            all = all.Union(box);
        }

        return all;
    }

    /// <summary>The box around points.</summary>
    public static PageBox Around(params ReadOnlySpan<PdfPoint> points)
    {
        PageBox all = Empty;
        foreach (PdfPoint p in points)
        {
            all = all.Union(new(p.X, p.Y, p.X, p.Y));
        }

        return all;
    }
}
