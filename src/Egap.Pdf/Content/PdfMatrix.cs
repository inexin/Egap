using System.Globalization;

namespace Egap.Pdf;

/// <summary>
/// A transformation matrix as PDF writes it (8.3.3): <c>[a b c d e f]</c>, mapping a point (x, y) to
/// (a·x + c·y + e, b·x + d·y + f).
/// </summary>
/// <remarks>
/// <c>first * second</c> is the matrix that applies <c>first</c> and then <c>second</c>, which is PDF's order:
/// after <c>cm</c> the current transformation matrix is <c>cm * ctm</c>.
/// </remarks>
/// <param name="A">The horizontal scale and rotation component.</param>
/// <param name="B">The rotation and skew component that turns x into y.</param>
/// <param name="C">The rotation and skew component that turns y into x.</param>
/// <param name="D">The vertical scale and rotation component.</param>
/// <param name="E">The horizontal translation.</param>
/// <param name="F">The vertical translation.</param>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity matrix, <c>[1 0 0 1 0 0]</c>.</summary>
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>Whether this is the identity matrix.</summary>
    public bool IsIdentity => this == Identity;

    /// <summary>The determinant, <c>a·d − b·c</c>; zero for a matrix that cannot be inverted.</summary>
    public double Determinant => (A * D) - (B * C);

    /// <summary>A translation.</summary>
    public static PdfMatrix Translation(double x, double y)
    {
        return new PdfMatrix(1, 0, 0, 1, x, y);
    }

    /// <summary>A scale about the origin.</summary>
    public static PdfMatrix Scale(double x, double y)
    {
        return new PdfMatrix(x, 0, 0, y, 0, 0);
    }

    /// <summary>A rotation about the origin, counterclockwise in PDF's y-up space.</summary>
    public static PdfMatrix Rotation(double degrees)
    {
        // Multiples of 90 degrees are exact, so turned pages keep whole-number coordinates.
        (double sin, double cos) = (((degrees % 360) + 360) % 360) switch
        {
            0 => (0.0, 1.0),
            90 => (1.0, 0.0),
            180 => (0.0, -1.0),
            270 => (-1.0, 0.0),
            _ => Math.SinCos(degrees * Math.PI / 180),
        };
        return new PdfMatrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>The matrix that applies <paramref name="first"/> and then <paramref name="second"/>.</summary>
    public static PdfMatrix operator *(PdfMatrix first, PdfMatrix second)
    {
        return new PdfMatrix(
            (first.A * second.A) + (first.B * second.C),
            (first.A * second.B) + (first.B * second.D),
            (first.C * second.A) + (first.D * second.C),
            (first.C * second.B) + (first.D * second.D),
            (first.E * second.A) + (first.F * second.C) + second.E,
            (first.E * second.B) + (first.F * second.D) + second.F);
    }

    /// <summary>The matrix that applies this one and then <paramref name="second"/>; the named form of <c>*</c>.</summary>
    public PdfMatrix Multiply(PdfMatrix second)
    {
        return this * second;
    }

    /// <summary>Maps a point.</summary>
    public PdfPoint Transform(PdfPoint point)
    {
        return new PdfPoint((A * point.X) + (C * point.Y) + E, (B * point.X) + (D * point.Y) + F);
    }

    /// <summary>Maps a distance: like <see cref="Transform(PdfPoint)"/> without the translation.</summary>
    public PdfPoint TransformVector(PdfPoint vector)
    {
        return new PdfPoint((A * vector.X) + (C * vector.Y), (B * vector.X) + (D * vector.Y));
    }

    /// <summary>The smallest upright rectangle holding the rectangle's four corners once mapped.</summary>
    public PdfRectangle Transform(PdfRectangle rectangle)
    {
        PdfPoint p1 = Transform(new PdfPoint(rectangle.Left, rectangle.Bottom));
        PdfPoint p2 = Transform(new PdfPoint(rectangle.Right, rectangle.Bottom));
        PdfPoint p3 = Transform(new PdfPoint(rectangle.Left, rectangle.Top));
        PdfPoint p4 = Transform(new PdfPoint(rectangle.Right, rectangle.Top));
        return new PdfRectangle(
            Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X)),
            Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y)),
            Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X)),
            Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y)));
    }

    /// <summary>The inverse, or <see langword="false"/> when the matrix cannot be inverted (its determinant is zero or not finite).</summary>
    public bool TryInvert(out PdfMatrix inverse)
    {
        double determinant = Determinant;
        if (determinant == 0 || !double.IsFinite(determinant))
        {
            inverse = Identity;
            return false;
        }

        inverse = new PdfMatrix(
            D / determinant,
            -B / determinant,
            -C / determinant,
            A / determinant,
            ((C * F) - (D * E)) / determinant,
            ((B * E) - (A * F)) / determinant);
        return true;
    }

    /// <summary>The matrix as PDF writes it: <c>[a b c d e f]</c>.</summary>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"[{A} {B} {C} {D} {E} {F}]");
    }
}
