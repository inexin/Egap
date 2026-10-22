using System.Globalization;

using Egami.Pixels;

using Egap.Pdf;

namespace Egap.Cli.Host;

/// <summary>Reads the values options take: lengths (12mm, 1in, 72pt), page sizes, colors and numbers.</summary>
internal static class Values
{
    /// <summary>A length in points: Egap's Length syntax, such as 12mm, 1.5cm, 1in or 72pt; a bare number is points.</summary>
    public static double Points(string text, string option)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double points))
        {
            return points;
        }

        return Length.TryParse(text, out Length length) ? length.Points
            : throw new UsageException($"{option} '{text}' is not a length such as 12mm, 1in or 72pt.");
    }

    /// <summary>Two lengths joined by a comma, such as an offset 10mm,-5mm.</summary>
    public static (double X, double Y) Pair(string text, string option)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (Points(parts[0], option), Points(parts[1], option))
            : throw new UsageException($"{option} '{text}' is not two lengths joined by a comma, such as 10mm,-5mm.");
    }

    /// <summary>A page size: A3, A4, A5, Letter, Legal, Tabloid, or WxH in lengths.</summary>
    public static PdfPageSize Size(string text, string option)
    {
        try
        {
            return Commands.ConvertCommand.PageSize(text, landscape: false)!.Value;
        }
        catch (UsageException)
        {
            throw new UsageException($"{option} '{text}' is not A3, A4, A5, Letter, Legal, Tabloid or WxH such as 210mmx297mm.");
        }
    }

    /// <summary>A color as #rgb, #rrggbb or #rrggbbaa.</summary>
    public static Rgba8 Color(string text, string option)
    {
        try
        {
            return Rgba8.FromHex(text);
        }
        catch (FormatException)
        {
            throw new UsageException($"{option} '{text}' is not a color such as #c00 or #336699.");
        }
    }

    /// <summary>A number between <paramref name="min"/> and <paramref name="max"/>.</summary>
    public static double Number(string text, string option, double min = double.MinValue, double max = double.MaxValue)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= min && value <= max ? value
            : throw new UsageException($"{option} '{text}' is not a number{(min > double.MinValue ? $" from {min.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}{(max < double.MaxValue ? $" to {max.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}.");
    }
}
