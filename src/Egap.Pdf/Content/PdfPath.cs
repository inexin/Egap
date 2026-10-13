namespace Egap.Pdf;

/// <summary>What a path segment does (8.5.2).</summary>
public enum PdfPathVerb
{
    /// <summary>Starts a subpath at <see cref="PdfPathSegment.Point"/> (<c>m</c>).</summary>
    MoveTo,

    /// <summary>A straight line to <see cref="PdfPathSegment.Point"/> (<c>l</c>).</summary>
    LineTo,

    /// <summary>A cubic Bézier curve through two control points to <see cref="PdfPathSegment.Point"/> (<c>c</c>, <c>v</c>, <c>y</c>).</summary>
    CurveTo,

    /// <summary>Closes the subpath with a line back to its start (<c>h</c>).</summary>
    Close,
}

/// <summary>One segment of a path. For a curve, <see cref="Control1"/> and <see cref="Control2"/> are its control points.</summary>
/// <param name="Verb">What the segment does.</param>
/// <param name="Point">Where it ends; the subpath's start for <see cref="PdfPathVerb.Close"/>.</param>
/// <param name="Control1">A curve's first control point.</param>
/// <param name="Control2">A curve's second control point.</param>
public readonly record struct PdfPathSegment(PdfPathVerb Verb, PdfPoint Point, PdfPoint Control1 = default, PdfPoint Control2 = default);

/// <summary>
/// A path as content builds it (8.5.2), in the user space it was built in: subpaths of lines and cubic curves. <c>re</c>
/// becomes a closed subpath of four lines, and <c>v</c> and <c>y</c> become curves with both control points given.
/// </summary>
public sealed class PdfPath
{
    private readonly List<PdfPathSegment> segments = [];
    private PdfPoint start;

    /// <summary>The segments, in order.</summary>
    public IReadOnlyList<PdfPathSegment> Segments => segments;

    /// <summary>Whether the path has no segments.</summary>
    public bool IsEmpty => segments.Count == 0;

    /// <summary>The current point: where the last segment ended; the origin when there is none.</summary>
    public PdfPoint CurrentPoint { get; private set; }

    /// <summary>Starts a subpath.</summary>
    public PdfPath MoveTo(double x, double y)
    {
        start = new PdfPoint(x, y);
        CurrentPoint = start;
        segments.Add(new PdfPathSegment(PdfPathVerb.MoveTo, start));
        return this;
    }

    /// <summary>A line to a point; a subpath is started at the current point when none is open.</summary>
    public PdfPath LineTo(double x, double y)
    {
        EnsureStarted();
        CurrentPoint = new PdfPoint(x, y);
        segments.Add(new PdfPathSegment(PdfPathVerb.LineTo, CurrentPoint));
        return this;
    }

    /// <summary>A cubic Bézier curve to (<paramref name="x3"/>, <paramref name="y3"/>).</summary>
    public PdfPath CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        EnsureStarted();
        CurrentPoint = new PdfPoint(x3, y3);
        segments.Add(new PdfPathSegment(PdfPathVerb.CurveTo, CurrentPoint, new PdfPoint(x1, y1), new PdfPoint(x2, y2)));
        return this;
    }

    /// <summary>Closes the subpath; the current point goes back to its start.</summary>
    public PdfPath Close()
    {
        if (segments.Count > 0 && segments[^1].Verb != PdfPathVerb.Close)
        {
            segments.Add(new PdfPathSegment(PdfPathVerb.Close, start));
            CurrentPoint = start;
        }

        return this;
    }

    /// <summary>A rectangle as a closed subpath (<c>re</c>).</summary>
    public PdfPath Rectangle(double x, double y, double width, double height)
    {
        return MoveTo(x, y).LineTo(x + width, y).LineTo(x + width, y + height).LineTo(x, y + height).Close();
    }

    /// <summary>The smallest upright rectangle around every point and control point once mapped, or <see langword="null"/> for an empty path.</summary>
    public PdfRectangle? GetBounds(PdfMatrix matrix)
    {
        if (segments.Count == 0)
        {
            return null;
        }

        double left = double.PositiveInfinity, bottom = double.PositiveInfinity, right = double.NegativeInfinity, top = double.NegativeInfinity;
        void Add(PdfPoint point)
        {
            PdfPoint p = matrix.Transform(point);
            left = Math.Min(left, p.X);
            right = Math.Max(right, p.X);
            bottom = Math.Min(bottom, p.Y);
            top = Math.Max(top, p.Y);
        }

        foreach (PdfPathSegment segment in segments)
        {
            Add(segment.Point);
            if (segment.Verb == PdfPathVerb.CurveTo)
            {
                Add(segment.Control1);
                Add(segment.Control2);
            }
        }

        return new PdfRectangle(left, bottom, right, top);
    }

    private void EnsureStarted()
    {
        if (segments.Count == 0 || segments[^1].Verb == PdfPathVerb.Close)
        {
            MoveTo(CurrentPoint.X, CurrentPoint.Y);
        }
    }
}
