using System.Collections.Concurrent;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Shaping;
using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Spreadsheets.Printing;

/// <summary>
/// The fonts one print of a workbook uses, through Egap.Documents' font chain (installed fonts, Office's cloud fonts, then the
/// standard substitutes such as Carlito for Calibri), and the units that follow from the workbook's normal font: Excel's
/// maximum digit width, which column widths count in.
/// </summary>
internal sealed class SheetFonts
{
    /// <summary>Excel lays out at 96 pixels to the inch; a point is three quarters of a pixel.</summary>
    public const double PointsPerPixel = 0.75;

    private readonly FontProvider provider;
    private readonly ConcurrentDictionary<(string Name, bool Bold, bool Italic), IReadOnlyList<FontFamily>?> families = new();

    public SheetFonts(Workbook book, FontSettings? settings)
    {
        provider = new FontProvider(new Document(), settings);
        NormalFont = book.Styles.Count > 0 ? book.Styles[0].Font : CellFont.Default;
        MaxDigitWidth = DigitWidth(NormalFont);
        PrinterDigitWidth = PrinterDigit(NormalFont);
    }

    /// <summary>The resolution Excel lays a print out at: a 600 dpi printer, the common laser printer and Excel's PDF export.</summary>
    public const int PrinterDpi = 600;

    /// <summary>
    /// The widest digit of the normal font in printer dots: the font is sized to whole dots and the digit rounded, so a
    /// column prints a little wider than on screen (Calibri 11: 47 dots, against 7 pixels at 96 dpi).
    /// </summary>
    public int PrinterDigitWidth { get; }

    /// <summary>The workbook's normal font: the font of the default cell format.</summary>
    public CellFont NormalFont { get; }

    /// <summary>The widest of the digits 0 to 9 in the normal font, in whole pixels at 96 dpi, as Excel measures it.</summary>
    public int MaxDigitWidth { get; }

    /// <summary>The fallback for characters the chosen fonts lack.</summary>
    public FontFallback Fallback => provider.Fallback;

    /// <summary>The fonts used in place of the ones asked for.</summary>
    public IReadOnlyList<FontSubstitution> Substitutions => provider.Substitutions;

    /// <summary>The families for a cell font, for Egami's text styles.</summary>
    public IReadOnlyList<FontFamily>? Families(string name, bool bold, bool italic) =>
        families.GetOrAdd((name, bold, italic), key =>
            provider.ResolveFamily(key.Name, Style(key.Bold, key.Italic)) is { } family ? [family] : null);

    /// <summary>The face a cell font draws with, at <paramref name="size"/> points.</summary>
    public Font Font(string name, bool bold, bool italic, double size)
    {
        FontMatch match = provider.Resolve(name, Style(bold, italic));
        return new Font(match.Typeface, (float)size) { Synthesis = match.Synthesis };
    }

    /// <summary>The text style of a run in <paramref name="font"/>, its size times <paramref name="scale"/>.</summary>
    public TextStyle TextStyle(CellFont font, Rgba8 color, double scale = 1)
    {
        TextDecorationLines lines = TextDecorationLines.None;
        if (font.Underline != CellUnderline.None)
        {
            lines |= TextDecorationLines.Underline;
        }

        if (font.Strikethrough)
        {
            lines |= TextDecorationLines.LineThrough;
        }

        double size = font.Size * scale;
        float? shift = null;
        if (font.VerticalPosition != CellVerticalTextPosition.Baseline)
        {
            // Superscript and subscript: two thirds of the size, raised or lowered by a third of it.
            shift = (float)(font.VerticalPosition == CellVerticalTextPosition.Superscript ? size / 3 : -size / 6);
            size *= 2.0 / 3;
        }

        return new TextStyle
        {
            FontFamilies = Families(font.Name, font.Bold, font.Italic),
            Size = (float)size,
            FontStyle = Style(font.Bold, font.Italic),
            Color = color,
            BaselineShift = shift,
            Decoration = lines == TextDecorationLines.None ? null : new TextDecoration
            {
                Lines = lines,
                Style = font.Underline is CellUnderline.Double or CellUnderline.DoubleAccounting ? TextDecorationStyle.Double : TextDecorationStyle.Solid,
            },
        };
    }

    /// <summary>A column of <paramref name="width"/> characters in pixels (ECMA-376 Part 1, 18.3.1.13): the width counts the cell padding.</summary>
    public int ColumnPixels(double width)
    {
        if (width <= 0)
        {
            return 0;
        }

        return (int)Math.Truncate(((256 * width) + Math.Truncate(128.0 / MaxDigitWidth)) / 256 * MaxDigitWidth);
    }

    /// <summary>
    /// A column of the sheet's default width in pixels: the sheet's default column width when it has one, else eight digits
    /// and five pixels of padding rounded up to a multiple of eight pixels, as Excel sizes a new sheet's columns.
    /// </summary>
    public int DefaultColumnPixels(Worksheet sheet) =>
        sheet.DefaultColumnWidth is { } width ? ColumnPixels(width) : (((8 * MaxDigitWidth) + 5 + 7) / 8) * 8;

    private static FontStyle Style(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => FontStyle.BoldItalic,
        (true, false) => FontStyle.Bold,
        (false, true) => FontStyle.Italic,
        _ => FontStyle.Normal,
    };

    private int PrinterDigit(CellFont font)
    {
        Font face = Font(font.Name, bold: false, italic: false, Math.Round(font.Size * PrinterDpi / 72));
        float widest = 0;
        for (char digit = '0'; digit <= '9'; digit++)
        {
            widest = Math.Max(widest, face.MeasureText(digit.ToString()).Advance);
        }

        return Math.Max(1, (int)Math.Round(widest));
    }

    private int DigitWidth(CellFont font)
    {
        Font face = Font(font.Name, bold: false, italic: false, font.Size * 96 / 72);
        float widest = 0;
        for (char digit = '0'; digit <= '9'; digit++)
        {
            widest = Math.Max(widest, face.MeasureText(digit.ToString()).Advance);
        }

        // Excel measures at whole pixels: Calibri 11 and Arial 10 give 7.
        return Math.Max(1, (int)Math.Truncate(widest + 0.01));
    }
}
