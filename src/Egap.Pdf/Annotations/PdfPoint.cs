namespace Egap.Pdf;

/// <summary>A point in default user space, as annotations give vertices, lines and quadrilaterals.</summary>
/// <param name="X">The horizontal position.</param>
/// <param name="Y">The vertical position.</param>
public readonly record struct PdfPoint(double X, double Y)
{
    /// <summary>Pairs of numbers from an array as points; a trailing odd number or a pair with a non-number is left out.</summary>
    internal static IReadOnlyList<PdfPoint> FromArray(PdfArray? array)
    {
        if (array is null)
        {
            return [];
        }

        List<PdfPoint> points = new(array.Count / 2);
        for (int i = 0; i + 1 < array.Count; i += 2)
        {
            if (array.GetNumber(i) is double x && array.GetNumber(i + 1) is double y && double.IsFinite(x) && double.IsFinite(y))
            {
                points.Add(new PdfPoint(x, y));
            }
        }

        return points;
    }

    /// <summary>Points as a flat array of numbers <c>[x1 y1 x2 y2 ...]</c>.</summary>
    internal static PdfArray ToArray(IEnumerable<PdfPoint> points)
    {
        PdfArray array = [];
        foreach (PdfPoint point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(points), point, "Points must have finite coordinates.");
            }

            array.Add(new PdfReal(point.X));
            array.Add(new PdfReal(point.Y));
        }

        return array;
    }
}
