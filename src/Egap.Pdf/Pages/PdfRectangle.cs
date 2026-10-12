using System.Globalization;

namespace Egap.Pdf;

/// <summary>
/// A rectangle in PDF user space (7.9.5), as page boxes are given: the lower-left and upper-right corners, with y going up.
/// </summary>
/// <param name="Left">The x of the left edge.</param>
/// <param name="Bottom">The y of the bottom edge.</param>
/// <param name="Right">The x of the right edge.</param>
/// <param name="Top">The y of the top edge.</param>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary>The width, <see cref="Right"/> less <see cref="Left"/>.</summary>
    public double Width => Right - Left;

    /// <summary>The height, <see cref="Top"/> less <see cref="Bottom"/>.</summary>
    public double Height => Top - Bottom;

    /// <summary>Whether the rectangle has no area.</summary>
    public bool IsEmpty => !(Width > 0 && Height > 0);

    /// <summary>The rectangle with its corners ordered, so <see cref="Left"/> is at most <see cref="Right"/> and <see cref="Bottom"/> at most <see cref="Top"/>.</summary>
    public PdfRectangle Normalize()
    {
        return new(Math.Min(Left, Right), Math.Min(Bottom, Top), Math.Max(Left, Right), Math.Max(Bottom, Top));
    }

    /// <summary>The part both rectangles cover, which may be empty.</summary>
    public PdfRectangle Intersect(PdfRectangle other)
    {
        return new(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom), Math.Min(Right, other.Right), Math.Min(Top, other.Top));
    }

    /// <summary>The rectangle as a PDF array <c>[left bottom right top]</c>.</summary>
    public PdfArray ToArray()
    {
        return [Number(Left), Number(Bottom), Number(Right), Number(Top)];
    }

    /// <summary>A rectangle from a PDF array of four numbers, corners ordered; null when the array is not one.</summary>
    public static PdfRectangle? FromArray(PdfArray? array)
    {
        if (array is not { Count: 4 } || array.GetNumber(0) is not double a || array.GetNumber(1) is not double b
            || array.GetNumber(2) is not double c || array.GetNumber(3) is not double d
            || !double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(d))
        {
            return null;
        }

        return new PdfRectangle(a, b, c, d).Normalize();
    }

    /// <summary>The rectangle as <c>[left bottom right top]</c>.</summary>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"[{Left} {Bottom} {Right} {Top}]");
    }

    private static PdfObject Number(double value)
    {
        return value == Math.Floor(value) && Math.Abs(value) < 1e15 ? new PdfInteger((long)value) : new PdfReal(value);
    }
}
