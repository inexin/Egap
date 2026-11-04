using System.Globalization;

using Egami.Pixels;

namespace Egap.Documents.Odf;

/// <summary>ODF's value syntaxes (ODF 1.3 Part 3, 18): lengths, percentages, colours, borders and the like.</summary>
internal static class OdfUnits
{
    /// <summary>A length such as "2.5cm", "12pt" or "0.5in" in points, or null when it is not one.</summary>
    public static double? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        ReadOnlySpan<char> s = value.AsSpan().Trim();
        int end = 0;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] is '.' or '-' or '+' or 'e' or 'E'))
        {
            // An exponent's "e" is part of the number only when a digit or sign follows; "em" is a unit.
            if (s[end] is 'e' or 'E' && (end + 1 >= s.Length || !(char.IsAsciiDigit(s[end + 1]) || s[end + 1] is '-' or '+')))
            {
                break;
            }

            end++;
        }

        if (end == 0 || !double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
        {
            return null;
        }

        double scale = s[end..].Trim().ToString().ToLowerInvariant() switch
        {
            "pt" => 1,
            "in" or "inch" => 72,
            "cm" => 72 / 2.54,
            "mm" => 72 / 25.4,
            "pc" => 12,
            "px" => 0.75,
            "" => 1,
            _ => double.NaN,
        };

        return double.IsNaN(scale) ? null : number * scale;
    }

    /// <summary>A percentage such as "150%" as a number (150), or null when it is not one.</summary>
    public static double? Percent(string? value)
    {
        if (value is null)
        {
            return null;
        }

        ReadOnlySpan<char> s = value.AsSpan().Trim();
        return s.EndsWith('%') && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number
            : null;
    }

    /// <summary>A whole number, or null.</summary>
    public static int? Int(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
    }

    /// <summary>A boolean "true" or "false", or null.</summary>
    public static bool? Bool(string? value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>A colour "#RRGGBB" (or "#RGB"), or null; "transparent" and others are null too.</summary>
    public static Rgba8? Color(string? value)
    {
        if (value is null)
        {
            return null;
        }

        ReadOnlySpan<char> s = value.AsSpan().Trim();
        if (s.Length == 4 && s[0] == '#')
        {
            s = string.Concat(new string(s[1], 2), new string(s[2], 2), new string(s[3], 2));
        }
        else if (s.Length == 7 && s[0] == '#')
        {
            s = s[1..];
        }
        else
        {
            return null;
        }

        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? new Rgba8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255)
            : null;
    }

    /// <summary>
    /// A border as <c>fo:border</c> gives it, "0.5pt solid #000000" in any order, or <see cref="Border.None"/> for "none";
    /// null when it cannot be read. <paramref name="lineWidths"/> is <c>style:border-line-width</c>, the inner line, gap and
    /// outer line of a double border, whose sum is the width.
    /// </summary>
    public static Border? Border(string? value, string? lineWidths = null, double space = 0, bool shadow = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        double? width = null;
        BorderStyle? style = null;
        Rgba8? color = null;
        foreach (string part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Color(part) is { } c)
            {
                color = c;
            }
            else if (BorderStyleOf(part) is { } s)
            {
                style = s;
            }
            else if (Length(part) is { } w)
            {
                width = w;
            }
            else
            {
                width ??= part switch
                {
                    "thin" => 0.5,
                    "medium" => 1.5,
                    "thick" => 3,
                    _ => null,
                };
            }
        }

        if (style is null or BorderStyle.None || width is <= 0)
        {
            return Documents.Border.None;
        }

        if (style == BorderStyle.Double && lineWidths is not null)
        {
            string[] parts = lineWidths.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && Length(parts[0]) is { } inner && Length(parts[2]) is { } outer)
            {
                // Word's double borders give one line's width; ODF gives all three. The thicker line decides the look.
                style = Math.Abs(inner - outer) < 0.01 ? BorderStyle.Double : inner < outer ? BorderStyle.ThinThickSmallGap : BorderStyle.ThickThinSmallGap;
                width = Math.Max(inner, outer);
            }
        }

        return new Border(style.Value, width ?? 0.5, color is { } rgb ? DocumentColor.FromRgb(rgb) : default, space, shadow);
    }

    private static BorderStyle? BorderStyleOf(string value) => value switch
    {
        "none" or "hidden" => BorderStyle.None,
        "solid" => BorderStyle.Single,
        "double" or "double-thin" => BorderStyle.Double,
        "dotted" => BorderStyle.Dotted,
        "dashed" => BorderStyle.Dashed,
        "fine-dashed" => BorderStyle.DashSmallGap,
        "dash-dot" => BorderStyle.DotDash,
        "dash-dot-dot" => BorderStyle.DotDotDash,
        "groove" => BorderStyle.ThreeDEngrave,
        "ridge" => BorderStyle.ThreeDEmboss,
        "inset" => BorderStyle.Inset,
        "outset" => BorderStyle.Outset,
        _ => null,
    };

    /// <summary>A number format of <c>style:num-format</c> as a model number style.</summary>
    public static NumberStyle NumberStyleOf(string? format) => format switch
    {
        "1" => NumberStyle.Decimal,
        "a" => NumberStyle.LowerLetter,
        "A" => NumberStyle.UpperLetter,
        "i" => NumberStyle.LowerRoman,
        "I" => NumberStyle.UpperRoman,
        "1st" => NumberStyle.Ordinal,
        "One" or "one" or "ONE" => NumberStyle.CardinalText,
        "First" or "first" or "FIRST" => NumberStyle.OrdinalText,
        "01, 02, 03, ..." or "001, 002, 003, ..." => NumberStyle.DecimalZero,
        null or "" => NumberStyle.None,
        _ => NumberStyle.Decimal,
    };

    /// <summary>A date and time as ODF writes them (xsd:dateTime, often without a zone), or null.</summary>
    public static DateTimeOffset? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
        {
            return date;
        }

        return null;
    }
}
