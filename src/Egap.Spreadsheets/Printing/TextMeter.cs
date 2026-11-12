using Egap.Documents.Layout;

using EgamiFont = Egami.Fonts.Font;

namespace Egap.Spreadsheets.Printing;

/// <summary>
/// Measures cell text as Excel does on screen at 96 dpi, for AutoFit: each character a whole number of pixels, from
/// <see cref="CharacterWidths"/> where Excel was measured and from the font hinted to whole pixels otherwise; each line as
/// high as <see cref="PrintedRows"/> gives the font's row, or as the font's own line otherwise. The font chain loads only
/// when the tables lack something.
/// </summary>
internal sealed class TextMeter(Workbook book)
{
    /// <summary>Excel fits a column to its text's pixels times 29/28, then adds <see cref="Padding"/>.</summary>
    public const double Scale = 29.0 / 28;

    /// <summary>The pixels a column adds to its widest text: two of margin on the left, three on the right.</summary>
    public const int Padding = 5;

    /// <summary>A character no font has, which fonts draw with their missing glyph.</summary>
    private const char MissingGlyph = '\uFFFF';

    private readonly Dictionary<(string Name, double Size, bool Bold, bool Italic), CharacterWidths.Widths?> tables = new(CharacterWidths.KeyComparer.Instance);
    private readonly Dictionary<(string Name, double Size, bool Bold, bool Italic, char C), int> measured = [];
    private readonly Dictionary<(string Name, double Size, bool Bold, bool Italic), EgamiFont> faces = [];
    private SheetFonts? fonts;
    private int? digit;

    /// <summary>The widest digit of the normal font in pixels: what a column's width counts in.</summary>
    public int MaxDigitWidth => digit ??= Normal(book) is var normal && Table(normal) is { } widths && widths.MaxDigit > 0
        ? widths.MaxDigit
        : Fonts.MaxDigitWidth;

    /// <summary>
    /// The width of one line of text in <paramref name="font"/>, in pixels as Excel lays it out on screen, a line break in it
    /// as wide as the font's missing glyph and a carriage return as nothing. A number in the
    /// General format passes its <paramref name="decimalSeparator"/>: Excel gives its digits and that separator each the
    /// width of the font's widest digit.
    /// </summary>
    public int Width(ReadOnlySpan<char> text, CellFont font, char decimalSeparator = '\0')
    {
        CharacterWidths.Widths? table = Table(font);
        int width = 0;
        foreach (char c in text)
        {
            if (c == '\r')
            {
                continue;
            }

            if (c == '\n')
            {
                // Excel measures a line break in a line as a character the font lacks: its missing glyph's box.
                width += Measure(font, MissingGlyph);
                continue;
            }

            if (decimalSeparator != '\0' && (char.IsAsciiDigit(c) || c == decimalSeparator))
            {
                width += DigitWidth(font);
                continue;
            }

            // A minus sign takes the hyphen's width: Excel shows negative numbers with a hyphen in every culture.
            int pixels = table?.Of(c == '\u2212' ? '-' : c) ?? -1;
            width += pixels >= 0 ? pixels : Measure(font, c);
        }

        return width;
    }

    /// <summary>
    /// The width of one line of text as Excel measures it to fit a column, with fractions of a pixel: from the table where
    /// Excel was measured, and the whole pixels of <see cref="Width"/> widened by <see cref="Scale"/> otherwise.
    /// </summary>
    public double Exact(ReadOnlySpan<char> text, CellFont font, char decimalSeparator = '\0')
    {
        CharacterWidths.Widths? table = Table(font);
        double width = 0;
        foreach (char c in text)
        {
            if (c == '\r')
            {
                continue;
            }

            if (c == '\n')
            {
                width += Measure(font, MissingGlyph) * Scale;
                continue;
            }

            if (decimalSeparator != '\0' && (char.IsAsciiDigit(c) || c == decimalSeparator))
            {
                width += table is { } digits && digits.ExactMaxDigit > 0 ? digits.ExactMaxDigit : DigitWidth(font) * Scale;
                continue;
            }

            double pixels = table?.Exact(c == '\u2212' ? '-' : c) ?? -1;
            width += pixels >= 0 ? pixels : Measure(font, c) * Scale;
        }

        return width;
    }

    /// <summary>
    /// The pixels a short text takes beyond its whole pixels, which Excel keeps when its measure gives less: two, and two
    /// more for each whole four pixels the font's widest digit has over five, as Excel measured from 8 to 20 points, and the
    /// font's <see cref="Overhang"/>.
    /// </summary>
    public int Lead(CellFont font) => 2 + (2 * ((Math.Max(5, DigitWidth(font)) - 5) / 4)) + Overhang(font);

    /// <summary>
    /// The pixels a slanted or emboldened face reaches past its last character, which Excel adds to a text: for italics four,
    /// and two more for each whole eight pixels of the font's height over sixteen, and two for MS Sans Serif drawn bold, as
    /// Excel measured from 8 to 20 points.
    /// </summary>
    public static int Overhang(CellFont font)
    {
        if (font.Italic)
        {
            int pixels = (int)Math.Round(font.Size * 96 / 72);
            return 4 + (2 * (Math.Max(0, pixels - 8) / 8));
        }

        return font.Bold && string.Equals(font.Name, "MS Sans Serif", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }

    /// <summary>The widest digit of <paramref name="font"/> in pixels.</summary>
    public int DigitWidth(CellFont font)
    {
        if (Table(font) is { } widths && widths.MaxDigit > 0)
        {
            return widths.MaxDigit;
        }

        int widest = 0;
        for (char d = '0'; d <= '9'; d++)
        {
            widest = Math.Max(widest, Measure(font, d));
        }

        return widest;
    }

    /// <summary>The height of a line of <paramref name="font"/> in pixels: the row Excel gives that font.</summary>
    public int LineHeight(CellFont font)
    {
        // An italic Excel wasn't measured for keeps the upright face's row, which is nearer than its outlines.
        if ((CharacterWidths.LineHeight(font.Name, font.Size, font.Bold, font.Italic)
            ?? (font.Italic ? CharacterWidths.LineHeight(font.Name, font.Size, font.Bold, false) : null)) is { } pixels)
        {
            return pixels;
        }

        if (PrintedRows.Find(font.Name, font.Size) is { } row)
        {
            return row.Pixels;
        }

        // A font Excel wasn't measured for: its own line at the size in whole pixels, with the three pixels Excel's rows of
        // larger fonts keep over their lines (Arial 24 and Calibri 36).
        return (int)Math.Ceiling(Face(font).LineHeight) + 3;
    }

    private static CellFont Normal(Workbook book) => book.Styles.Count > 0 ? book.Styles[0].Font : CellFont.Default;

    private SheetFonts Fonts => fonts ??= new SheetFonts(book, FontSettings.Default);

    private CharacterWidths.Widths? Table(CellFont font)
    {
        (string, double, bool, bool) key = (font.Name, font.Size, font.Bold, font.Italic);
        if (!tables.TryGetValue(key, out CharacterWidths.Widths? widths))
        {
            // An italic Excel wasn't measured for: Calibri's keeps its upright widths, other fonts' italics don't.
            widths = CharacterWidths.Find(font.Name, font.Size, font.Bold, font.Italic);
            if (widths is null && font.Italic && string.Equals(font.Name, "Calibri", StringComparison.OrdinalIgnoreCase))
            {
                widths = CharacterWidths.Find(font.Name, font.Size, font.Bold, false);
            }

            tables[key] = widths;
        }

        return widths;
    }

    private int Measure(CellFont font, char c)
    {
        (string, double, bool, bool, char) key = (font.Name, font.Size, font.Bold, font.Italic, c);
        if (!measured.TryGetValue(key, out int pixels))
        {
            // Hinting snaps each glyph to whole pixels at the font's size in whole pixels, as GDI sizes it.
            measured[key] = pixels = char.IsSurrogate(c) ? 0 : (int)Math.Round(Face(font).MeasureText(c.ToString()).Advance, MidpointRounding.AwayFromZero);
        }

        return pixels;
    }

    private EgamiFont Face(CellFont font)
    {
        (string, double, bool, bool) key = (font.Name, font.Size, font.Bold, font.Italic);
        if (!faces.TryGetValue(key, out EgamiFont face))
        {
            faces[key] = face = Fonts.Font(font.Name, font.Bold, font.Italic, Math.Round(font.Size * 96 / 72));
        }

        return face;
    }
}
