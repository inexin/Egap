namespace Egap.Spreadsheets.Printing;

/// <summary>
/// The widths Excel gives characters on screen at 96 dpi, and the height it gives a line, for the fonts and sizes sheets use
/// most. Excel measures text with the font hinted to whole pixels, which no font's outlines give, so the table is what Excel
/// measured (Oracle/character-widths.excel.tsv and line-heights.excel.tsv in the tests, from
/// tools/excel-character-width-oracle.ps1 and tools/excel-line-height-oracle.ps1) and needs no font installed. Fonts,
/// sizes and characters it lacks are measured from the font itself.
/// </summary>
internal static partial class CharacterWidths
{
    // Built on first use: the tables are fields of the other part of the class, which may initialize later.
    private static readonly Lazy<Dictionary<(string Font, double Size, bool Bold, bool Italic), Widths>> Fonts = new(() =>
    {
        Dictionary<(string, double, bool, bool), Widths> fonts = new(KeyComparer.Instance);
        foreach ((string font, double size, bool bold, bool italic, string widths, string exact) in Table)
        {
            fonts[(font, size, bold, italic)] = new Widths(widths, exact);
        }

        return fonts;
    });

    private static readonly Lazy<Dictionary<(string Font, double Size, bool Bold, bool Italic), int>> LineHeights = new(() =>
    {
        Dictionary<(string, double, bool, bool), int> heights = new(KeyComparer.Instance);
        foreach ((string font, double size, bool bold, bool italic, int pixels) in Lines)
        {
            heights[(font, size, bold, italic)] = pixels;
        }

        return heights;
    });

    /// <summary>Each character's place in the table, or -1, for the characters up to the last the table measures.</summary>
    private static readonly Lazy<short[]> Places = new(() =>
    {
        short[] places = new short[Measured.Max() + 1];
        Array.Fill(places, (short)-1);
        for (int i = 0; i < Measured.Length; i++)
        {
            places[Measured[i]] = (short)i;
        }

        return places;
    });

    /// <summary>The widths of one font, size and style, or null when Excel wasn't measured for it.</summary>
    public static Widths? Find(string font, double size, bool bold, bool italic) =>
        Fonts.Value.TryGetValue((font, size, bold, italic), out Widths? widths) ? widths : null;

    /// <summary>The height in pixels of the row Excel fits to one line of a font, size and style, or null when it wasn't measured.</summary>
    public static int? LineHeight(string font, double size, bool bold, bool italic) =>
        LineHeights.Value.TryGetValue((font, size, bold, italic), out int pixels) ? pixels : null;

    private static int Place(char c)
    {
        short[] places = Places.Value;
        return c < places.Length ? places[c] : -1;
    }

    /// <summary>One font's measured widths.</summary>
    internal sealed class Widths
    {
        private readonly byte[] whole;
        private readonly double[] exact;

        public Widths(string widths, string exact)
        {
            whole = new byte[widths.Length];
            this.exact = new double[widths.Length];
            for (int place = 0; place < widths.Length; place++)
            {
                whole[place] = (byte)(widths[place] - '0');
                int value = 0;
                for (int i = place * 3; i < (place * 3) + 3; i++)
                {
                    value = (value * 16) + (exact[i] <= '9' ? exact[i] - '0' : exact[i] - 'A' + 10);
                }

                this.exact[place] = value / 40.0;
            }

            for (char digit = '0'; digit <= '9'; digit++)
            {
                MaxDigit = Math.Max(MaxDigit, Of(digit));
                ExactMaxDigit = Math.Max(ExactMaxDigit, Exact(digit));
            }
        }

        /// <summary>The widest of the digits 0 to 9 in whole pixels, which column widths count in.</summary>
        public int MaxDigit { get; }

        /// <summary>The widest of the digits 0 to 9 as Excel measures them.</summary>
        public double ExactMaxDigit { get; }

        /// <summary>A character's width in whole pixels, or -1 when the table doesn't have the character.</summary>
        public int Of(char c) => Place(c) is >= 0 and var place ? whole[place] : -1;

        /// <summary>A character's width in pixels as Excel measures it, with fractions, or -1 when the table doesn't have it.</summary>
        public double Exact(char c) => Place(c) is >= 0 and var place ? exact[place] : -1;
    }

    /// <summary>Compares a font's name, ignoring case, size, weight and slant.</summary>
    internal sealed class KeyComparer : IEqualityComparer<(string Font, double Size, bool Bold, bool Italic)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Font, double Size, bool Bold, bool Italic) x, (string Font, double Size, bool Bold, bool Italic) y) =>
            string.Equals(x.Font, y.Font, StringComparison.OrdinalIgnoreCase) && x.Size == y.Size && x.Bold == y.Bold && x.Italic == y.Italic;

        public int GetHashCode((string Font, double Size, bool Bold, bool Italic) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Font), key.Size, key.Bold, key.Italic);
    }
}
