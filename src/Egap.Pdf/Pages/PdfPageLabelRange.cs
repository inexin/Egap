namespace Egap.Pdf;

/// <summary>How the number in a page label is written (12.4.2).</summary>
public enum PdfPageLabelStyle
{
    /// <summary>No number: the label is the prefix alone.</summary>
    None,

    /// <summary>Decimal arabic numerals: 1, 2, 3.</summary>
    Arabic,

    /// <summary>Uppercase roman numerals: I, II, III.</summary>
    UpperRoman,

    /// <summary>Lowercase roman numerals: i, ii, iii.</summary>
    LowerRoman,

    /// <summary>Uppercase letters: A to Z, then AA to ZZ, and so on.</summary>
    UpperLetters,

    /// <summary>Lowercase letters: a to z, then aa to zz, and so on.</summary>
    LowerLetters,
}

/// <summary>
/// A range of page labels (12.4.2): from the page at <see cref="StartIndex"/> (from 0) up to the next range, labels are the
/// prefix followed by a number in the given style, counting from <see cref="Start"/>.
/// </summary>
/// <param name="StartIndex">The index of the range's first page, from 0.</param>
/// <param name="Style">How the number is written.</param>
/// <param name="Prefix">Text before the number, or <see langword="null"/> for none.</param>
/// <param name="Start">The number of the range's first page; at least 1.</param>
public sealed record PdfPageLabelRange(int StartIndex, PdfPageLabelStyle Style, string? Prefix = null, int Start = 1)
{
    /// <summary>The label of the page <paramref name="offset"/> pages into the range.</summary>
    public string Format(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        long number = (long)Start + offset;
        return (Prefix ?? "") + Style switch
        {
            PdfPageLabelStyle.Arabic => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PdfPageLabelStyle.UpperRoman => Roman(number),
            PdfPageLabelStyle.LowerRoman => Roman(number).ToLowerInvariant(),
            PdfPageLabelStyle.UpperLetters => Letters(number),
            PdfPageLabelStyle.LowerLetters => Letters(number).ToLowerInvariant(),
            _ => "",
        };
    }

    /// <summary>Roman numerals, with as many M as the thousands need, as pdf.js and Acrobat write them.</summary>
    private static string Roman(long number)
    {
        ReadOnlySpan<(int Value, string Digits)> numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];
        System.Text.StringBuilder text = new();
        foreach ((int value, string digits) in numerals)
        {
            for (; number >= value && text.Length < 10_000; number -= value)
            {
                text.Append(digits);
            }
        }

        return text.ToString();
    }

    /// <summary>A to Z for 1 to 26, then AA to ZZ for 27 to 52, and so on (12.4.2).</summary>
    private static string Letters(long number)
    {
        char letter = (char)('A' + ((number - 1) % 26));
        return new string(letter, (int)Math.Min(((number - 1) / 26) + 1, 10_000));
    }
}
