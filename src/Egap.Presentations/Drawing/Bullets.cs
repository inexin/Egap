using System.Globalization;
using System.Text;
using System.Xml.Linq;

using Egap.Documents.DrawingML;
using Egap.Presentations.Pptx;

namespace Egap.Presentations.Drawing;

/// <summary>
/// Paragraph bullets (ECMA-376 Part 1, 21.1.2.4): a character in its own font, colour and size, an automatic number in one of
/// DrawingML's numbering schemes, or a picture; each setting looked up down the paragraph's chain, with the <c>…Tx</c> forms
/// following the paragraph's first run.
/// </summary>
internal static class Bullets
{
    private static readonly XNamespace A = PptxNames.A;

    /// <summary>The kind of bullet the chain gives: the first of <c>a:buNone</c>, <c>a:buChar</c>, <c>a:buAutoNum</c> and <c>a:buBlip</c> it has.</summary>
    public static XElement? Kind(XElement?[] chain)
    {
        foreach (XElement? element in chain)
        {
            if (element?.Elements().FirstOrDefault(e => e.Name.Namespace == A && e.Name.LocalName is "buNone" or "buChar" or "buAutoNum" or "buBlip") is { } found)
            {
                return found.Name.LocalName == "buNone" ? null : found;
            }
        }

        return null;
    }

    /// <summary>The first of the named elements the chain has, for the paired settings (<c>buClr</c> or <c>buClrTx</c>, and the rest).</summary>
    public static XElement? First(XElement?[] chain, params string[] names)
    {
        foreach (XElement? element in chain)
        {
            if (element?.Elements().FirstOrDefault(e => e.Name.Namespace == A && Array.IndexOf(names, e.Name.LocalName) >= 0) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The bullet's look: the first run's, in the bullet font unless it follows the text, in the bullet colour unless it follows
    /// the text, and sized by the bullet size as a share of the text's or in points.
    /// </summary>
    public static RunLook Look(XElement?[] chain, RunLook first, TextFrame frame, float fontScale, bool character)
    {
        RunLook look = first with { Underline = "none", Strike = "noStrike", Caps = "none", Highlight = null, Outline = null, Spacing = 0 };
        if (First(chain, "buFont", "buFontTx") is { Name.LocalName: "buFont" } font && TextRuns.ThemeFont((string?)font.Attribute("typeface"), frame.Theme) is { Length: > 0 } typeface)
        {
            look = look with { Latin = typeface, EastAsian = typeface, Complex = typeface, Symbol = typeface };
            if (character)
            {
                // A bullet font's own weight and slant: Wingdings has no bold.
                look = look with { Bold = false, Italic = false };
            }
        }

        if (First(chain, "buClr", "buClrTx") is { Name.LocalName: "buClr" } color && DrawingColors.Resolve(color, frame.Theme, null) is { } rgb)
        {
            look = look with { Color = rgb, Fill = null };
        }

        XElement? size = First(chain, "buSzPct", "buSzPts", "buSzTx");
        if (size?.Name.LocalName == "buSzPct" && PptxNames.Long(size, "val") is long percent)
        {
            look = look with { Size = Math.Max(0.5f, first.Size * percent / 100000f) };
        }
        else if (size?.Name.LocalName == "buSzPts" && PptxNames.Long(size, "val") is long points)
        {
            look = look with { Size = Math.Max(0.5f, points / 100f * fontScale) };
        }

        return look;
    }

    /// <summary>
    /// The character a bullet draws: a symbol font's characters are in its private use range (U+F020 to U+F0FF), where a
    /// file gives them as the codes below 256 the font was made for.
    /// </summary>
    public static string Character(string text, string font)
    {
        if (!IsSymbolFont(font))
        {
            return text;
        }

        StringBuilder mapped = new(text.Length);
        foreach (char c in text)
        {
            mapped.Append(c is >= ' ' and <= 'ÿ' ? (char)(0xF000 + c) : c);
        }

        return mapped.ToString();
    }

    /// <summary>Whether a font is one of Windows' symbol fonts, whose character map is the symbol one.</summary>
    public static bool IsSymbolFont(string font) =>
        font.Equals("Wingdings", StringComparison.OrdinalIgnoreCase) || font.Equals("Wingdings 2", StringComparison.OrdinalIgnoreCase)
        || font.Equals("Wingdings 3", StringComparison.OrdinalIgnoreCase) || font.Equals("Webdings", StringComparison.OrdinalIgnoreCase)
        || font.Equals("Symbol", StringComparison.OrdinalIgnoreCase) || font.Equals("Marlett", StringComparison.OrdinalIgnoreCase)
        || font.Equals("Monotype Sorts", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An automatic number in a DrawingML numbering scheme (<c>ST_TextAutonumberScheme</c>), such as <c>arabicPeriod</c> for
    /// "1.", <c>romanLcParenR</c> for "i)" or <c>alphaUcParenBoth</c> for "(A)". Schemes for scripts this does not number in
    /// fall back to Arabic digits with the scheme's punctuation.
    /// </summary>
    public static string Number(string scheme, int number)
    {
        number = Math.Max(1, number);
        string digits;
        if (scheme.StartsWith("alphaLc", StringComparison.Ordinal))
        {
            digits = Alpha(number, 'a');
        }
        else if (scheme.StartsWith("alphaUc", StringComparison.Ordinal))
        {
            digits = Alpha(number, 'A');
        }
        else if (scheme.StartsWith("romanLc", StringComparison.Ordinal))
        {
            digits = Roman(number).ToLowerInvariant();
        }
        else if (scheme.StartsWith("romanUc", StringComparison.Ordinal))
        {
            digits = Roman(number);
        }
        else if (scheme.StartsWith("circleNum", StringComparison.Ordinal))
        {
            // Circled digits: ① to ⑳, then the plain number.
            return number <= 20 ? ((char)(0x2460 + number - 1)).ToString() : number.ToString(CultureInfo.InvariantCulture);
        }
        else if (scheme.StartsWith("ea1JpnChsDb", StringComparison.Ordinal) || scheme.StartsWith("ea1ChsPlain", StringComparison.Ordinal)
            || scheme.StartsWith("ea1ChtPlain", StringComparison.Ordinal) || scheme.StartsWith("ea1JpnKor", StringComparison.Ordinal))
        {
            digits = Chinese(number);
        }
        else if (scheme.StartsWith("arabicDb", StringComparison.Ordinal) || scheme.StartsWith("ea1ChsPeriod", StringComparison.Ordinal))
        {
            // Full-width digits.
            digits = string.Concat(number.ToString(CultureInfo.InvariantCulture).Select(d => (char)(d - '0' + 0xFF10)));
        }
        else
        {
            digits = number.ToString(CultureInfo.InvariantCulture);
        }

        if (scheme.EndsWith("ParenBoth", StringComparison.Ordinal))
        {
            return "(" + digits + ")";
        }

        if (scheme.EndsWith("ParenR", StringComparison.Ordinal))
        {
            return digits + ")";
        }

        if (scheme.EndsWith("Period", StringComparison.Ordinal))
        {
            return digits + (scheme.StartsWith("ea1", StringComparison.Ordinal) || scheme.StartsWith("arabicDb", StringComparison.Ordinal) ? "．" : ".");
        }

        if (scheme.EndsWith("Minus", StringComparison.Ordinal))
        {
            return "- " + digits + " -";
        }

        return digits;
    }

    /// <summary>Letters as PowerPoint counts with them: a to z, then aa to zz, then aaa.</summary>
    private static string Alpha(int number, char first)
    {
        int repeat = ((number - 1) / 26) + 1;
        return new string((char)(first + ((number - 1) % 26)), Math.Min(repeat, 50));
    }

    private static string Roman(int number)
    {
        if (number >= 4000)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        (int Value, string Text)[] numerals = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        StringBuilder text = new();
        foreach ((int value, string numeral) in numerals)
        {
            while (number >= value)
            {
                text.Append(numeral);
                number -= value;
            }
        }

        return text.ToString();
    }

    /// <summary>Chinese numerals up to 99, then digits.</summary>
    private static string Chinese(int number)
    {
        const string Ones = "〇一二三四五六七八九";
        if (number < 10)
        {
            return Ones[number].ToString();
        }

        if (number < 100)
        {
            int tens = number / 10;
            int ones = number % 10;
            return (tens > 1 ? Ones[tens].ToString() : "") + "十" + (ones > 0 ? Ones[ones].ToString() : "");
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>What text fields show (ECMA-376 Part 1, 21.1.2.2.4): the slide number and the date and time formats.</summary>
internal static class Fields
{
    /// <summary>
    /// The text of a field of <paramref name="type"/>: the slide number, or the date in the field's format and the run's
    /// language; any other field, and a slide number on a layout or master, keeps the text the file holds.
    /// </summary>
    public static string Text(string? type, string stored, FieldValues values, CultureInfo culture)
    {
        if (type == "slidenum")
        {
            return values.SlideNumber is int number ? number.ToString(culture) : stored;
        }

        if (type is null || !type.StartsWith("datetime", StringComparison.Ordinal))
        {
            return stored;
        }

        DateTime date = values.Date;
        DateTimeFormatInfo format = culture.DateTimeFormat;
        return type switch
        {
            "datetime" or "datetime1" or "datetimeFigureOut" => date.ToString(format.ShortDatePattern, culture),
            "datetime2" => date.ToString(format.LongDatePattern, culture),
            "datetime3" => date.ToString("d MMMM yyyy", culture),
            "datetime4" => date.ToString("MMMM d, yyyy", culture),
            "datetime5" => date.ToString("d-MMM-yy", culture),
            "datetime6" => date.ToString("MMMM yy", culture),
            "datetime7" => date.ToString("MMM-yy", culture),
            "datetime8" => date.ToString(format.ShortDatePattern + " " + format.ShortTimePattern, culture),
            "datetime9" => date.ToString(format.ShortDatePattern + " " + format.LongTimePattern, culture),
            "datetime10" => date.ToString("H:mm", culture),
            "datetime11" => date.ToString("H:mm:ss", culture),
            "datetime12" => date.ToString("h:mm tt", culture),
            "datetime13" => date.ToString("h:mm:ss tt", culture),
            _ => stored,
        };
    }
}
