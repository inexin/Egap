namespace Egap.Spreadsheets.Printing;

/// <summary>A normal font's default row: its height on screen in pixels at 96 dpi and on paper in dots at 600 dpi.</summary>
internal readonly record struct PrintedRow(int Pixels, int Dots);

/// <summary>
/// How Excel prints rows, per normal font and size. Excel's PDF export at 600 dpi scales a row by the font's printed row
/// over its row on screen, both of which come from the font's metrics on the device, rounded and hinted there, so a 15 point
/// row prints 14.52 points in Calibri 11 and 15.24 in Aptos Narrow 11. The table is what Excel measures
/// (Oracle/row-heights.excel.tsv in the tests, from tools/excel-row-height-oracle.ps1), so it needs no font installed.
/// </summary>
internal static class PrintedRows
{
    /// <summary>The usual dots a pixel of row prints in, for a font or size the table lacks: Calibri 11 and Arial 10's.</summary>
    public const double UsualDotsPerPixel = 6.05;

    private static readonly Dictionary<string, (double Size, int Pixels, int Dots)[]> Fonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Aptos Narrow"] = [(8, 15, 95), (9, 16, 106), (10, 18, 116), (11, 20, 127), (12, 21, 137), (14, 25, 159)],
        ["Arial Narrow"] = [(7, 12, 76), (8, 17, 86), (9, 18, 97), (10, 17, 106), (11, 22, 116), (12, 21, 126)],
        ["Arial"] = [(7, 12, 76), (8, 15, 85), (9, 16, 93), (10, 17, 103), (11, 19, 114), (12, 20, 123), (14, 24, 143), (16, 27, 161), (18, 31, 181), (20, 34, 200)],
        ["Book Antiqua"] = [(10, 18, 110), (11, 22, 120), (12, 21, 131)],
        ["Calibri"] = [(7, 12, 79), (8, 15, 90), (9, 16, 99), (10, 17, 109), (11, 20, 121), (12, 21, 130), (14, 25, 150), (16, 28, 171), (18, 31, 191), (20, 35, 212)],
        ["Cambria"] = [(8, 14, 87), (9, 16, 96), (10, 17, 105), (11, 19, 115), (12, 21, 125), (14, 24, 145)],
        ["Century Gothic"] = [(10, 18, 108), (11, 22, 119), (12, 23, 128)],
        ["Comic Sans MS"] = [(10, 20, 133), (11, 22, 147), (12, 26, 159)],
        ["Courier New"] = [(8, 15, 79), (9, 16, 88), (10, 18, 96), (11, 20, 107), (12, 21, 115), (14, 25, 133)],
        ["Courier"] = [(10, 16, 96), (12, 20, 115)],
        ["Garamond"] = [(10, 17, 102), (11, 20, 111), (12, 21, 120)],
        ["Geneva"] = [(9, 16, 93), (10, 17, 103), (12, 20, 123)],
        ["Helvetica"] = [(9, 16, 93), (10, 17, 103), (12, 20, 123)],
        ["MS Sans Serif"] = [(8, 14, 85), (10, 17, 103), (12, 21, 123)],
        ["Segoe UI"] = [(8, 14, 102), (9, 16, 114), (10, 19, 124), (11, 22, 138), (12, 23, 148), (14, 27, 171)],
        ["Tahoma"] = [(8, 14, 89), (9, 15, 98), (10, 17, 108), (11, 19, 119), (12, 20, 129), (14, 24, 149)],
        ["Times New Roman"] = [(7, 12, 77), (8, 15, 87), (9, 16, 96), (10, 17, 106), (11, 20, 116), (12, 21, 126), (14, 25, 145), (16, 27, 165), (18, 31, 184), (20, 35, 203)],
        ["Verdana"] = [(8, 14, 88), (9, 15, 100), (10, 17, 108), (11, 19, 122), (12, 20, 129), (14, 24, 150)],
    };

    /// <summary>The measured row of a normal font, or null when Excel wasn't measured for it.</summary>
    public static PrintedRow? Find(string font, double size)
    {
        if (Fonts.TryGetValue(font, out (double Size, int Pixels, int Dots)[]? sizes))
        {
            foreach ((double known, int pixels, int dots) in sizes)
            {
                if (known == size)
                {
                    return new PrintedRow(pixels, dots);
                }
            }
        }

        return null;
    }
}
