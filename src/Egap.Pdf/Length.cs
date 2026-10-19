using System.Globalization;

namespace Egap.Pdf;

/// <summary>
/// A length on the page, held in points (1/72 inch) and made from points, millimetres, centimetres or inches, so sizes in
/// different units cannot be mixed up. It converts to and from a <see cref="double"/> in points without a cast, so it can be
/// passed wherever a size in points is asked for, and a plain number still works wherever a <see cref="Length"/> is.
/// </summary>
/// <example>
/// <code>
/// NUpOptions options = new() { Margin = Length.Mm(10), Gutter = Length.Mm(4) };
/// Length width = Length.Parse("21cm");
/// </code>
/// </example>
public readonly record struct Length : IComparable<Length>, IFormattable
{
    private const double PointsPerInch = 72;
    private const double MillimetresPerInch = 25.4;

    private Length(double points)
    {
        Points = points;
    }

    /// <summary>Gets a length of nothing.</summary>
    public static Length Zero => default;

    /// <summary>Gets the length in points.</summary>
    public double Points { get; }

    /// <summary>Gets the length in millimetres.</summary>
    public double Millimetres => Points / PointsPerInch * MillimetresPerInch;

    /// <summary>Gets the length in centimetres.</summary>
    public double Centimetres => Millimetres / 10;

    /// <summary>Gets the length in inches.</summary>
    public double Inches => Points / PointsPerInch;

    /// <summary>A length in points.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="points"/> is not a finite number.</exception>
    public static Length Pt(double points) => new(Finite(points, nameof(points)));

    /// <summary>A length in millimetres.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="millimetres"/> is not a finite number.</exception>
    public static Length Mm(double millimetres) => new(Finite(millimetres, nameof(millimetres)) / MillimetresPerInch * PointsPerInch);

    /// <summary>A length in centimetres.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="centimetres"/> is not a finite number.</exception>
    public static Length Cm(double centimetres) => new(Finite(centimetres, nameof(centimetres)) * 10 / MillimetresPerInch * PointsPerInch);

    /// <summary>A length in inches.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inches"/> is not a finite number.</exception>
    public static Length In(double inches) => new(Finite(inches, nameof(inches)) * PointsPerInch);

    /// <summary>
    /// Reads a length written as a number and a unit: <c>pt</c>, <c>mm</c>, <c>cm</c> or <c>in</c> (<c>"12pt"</c>,
    /// <c>"20 mm"</c>, <c>"8.5in"</c>). A number with no unit is in points. The number uses the invariant culture.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="FormatException">The text is not a length.</exception>
    public static Length Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out Length length) ? length : throw new FormatException($"'{text}' is not a length such as 12pt, 20mm, 2.5cm or 1in.");
    }

    /// <summary>Reads a length as <see cref="Parse"/> does; false when the text is not one.</summary>
    public static bool TryParse(string? text, out Length length)
    {
        length = default;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        int unit = span.Length;
        while (unit > 0 && char.IsAsciiLetter(span[unit - 1]))
        {
            unit--;
        }

        ReadOnlySpan<char> suffix = span[unit..];
        if (!double.TryParse(span[..unit].TrimEnd(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            return false;
        }

        double? points = suffix.Length == 0 || suffix.Equals("pt", StringComparison.OrdinalIgnoreCase) ? value
            : suffix.Equals("mm", StringComparison.OrdinalIgnoreCase) ? value / MillimetresPerInch * PointsPerInch
            : suffix.Equals("cm", StringComparison.OrdinalIgnoreCase) ? value * 10 / MillimetresPerInch * PointsPerInch
            : suffix.Equals("in", StringComparison.OrdinalIgnoreCase) ? value * PointsPerInch
            : null;
        if (points is null)
        {
            return false;
        }

        length = new Length(points.Value);
        return true;
    }

    /// <summary>A length in points from a plain number.</summary>
    public static implicit operator Length(double points) => new(points);

    /// <summary>The length in points.</summary>
    public static implicit operator double(Length length) => length.Points;

    /// <summary>The sum of two lengths.</summary>
    public static Length operator +(Length left, Length right) => new(left.Points + right.Points);

    /// <summary>The difference of two lengths.</summary>
    public static Length operator -(Length left, Length right) => new(left.Points - right.Points);

    /// <summary>The length turned the other way.</summary>
    public static Length operator -(Length length) => new(-length.Points);

    /// <summary>A length times a number.</summary>
    public static Length operator *(Length length, double factor) => new(length.Points * factor);

    /// <summary>A length times a number.</summary>
    public static Length operator *(double factor, Length length) => new(length.Points * factor);

    /// <summary>A length divided by a number.</summary>
    public static Length operator /(Length length, double divisor) => new(length.Points / divisor);

    /// <summary>Whether the first length is shorter.</summary>
    public static bool operator <(Length left, Length right) => left.Points < right.Points;

    /// <summary>Whether the first length is longer.</summary>
    public static bool operator >(Length left, Length right) => left.Points > right.Points;

    /// <summary>Whether the first length is shorter or the same.</summary>
    public static bool operator <=(Length left, Length right) => left.Points <= right.Points;

    /// <summary>Whether the first length is longer or the same.</summary>
    public static bool operator >=(Length left, Length right) => left.Points >= right.Points;

    /// <inheritdoc/>
    public int CompareTo(Length other) => Points.CompareTo(other.Points);

    /// <summary>The length in points, such as <c>56.69pt</c>.</summary>
    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    /// <summary>The length in points, the number written with <paramref name="format"/>.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => Points.ToString(format ?? "0.##", formatProvider) + "pt";

    private static double Finite(double value, string name)
    {
        return double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "A length must be a finite number.");
    }
}
