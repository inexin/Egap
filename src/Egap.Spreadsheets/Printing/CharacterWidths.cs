namespace Egap.Spreadsheets.Printing;

/// <summary>
/// The widths Excel gives characters on screen, in whole pixels at 96 dpi, for the fonts and sizes sheets use most. Excel
/// measures text with the font hinted to whole pixels, which no font's outlines give, so the table is what Excel measured
/// (Oracle/character-widths.excel.tsv in the tests, from tools/excel-character-width-oracle.ps1) and needs no font
/// installed. Fonts, sizes and characters it lacks are measured from the font itself.
/// </summary>
internal static partial class CharacterWidths
{
    // Built on first use: the table is a field of the other part of the class, which may initialize later.
    private static readonly Lazy<Dictionary<(string Font, double Size, bool Bold), string>> Fonts = new(Build);

    private static readonly Dictionary<char, int> Places = Measured.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);

    /// <summary>The widths of one font, size and weight, or null when Excel wasn't measured for it.</summary>
    public static Widths? Find(string font, double size, bool bold) =>
        Fonts.Value.TryGetValue((font, size, bold), out string? widths) ? new Widths(widths) : null;

    private static Dictionary<(string, double, bool), string> Build()
    {
        Dictionary<(string, double, bool), string> fonts = new(KeyComparer.Instance);
        foreach ((string font, double size, bool bold, string widths) in Table)
        {
            fonts[(font, size, bold)] = widths;
        }

        return fonts;
    }

    /// <summary>One font's measured widths.</summary>
    internal readonly struct Widths(string widths)
    {
        /// <summary>A character's width in pixels, or -1 when the table doesn't have the character.</summary>
        public int Of(char c) => Places.TryGetValue(c, out int place) ? widths[place] - '0' : -1;

        /// <summary>The widest of the digits 0 to 9, which column widths count in.</summary>
        public int MaxDigit
        {
            get
            {
                int widest = 0;
                for (char digit = '0'; digit <= '9'; digit++)
                {
                    widest = Math.Max(widest, Of(digit));
                }

                return widest;
            }
        }
    }

    /// <summary>Compares a font's name, ignoring case, size and weight.</summary>
    internal sealed class KeyComparer : IEqualityComparer<(string Font, double Size, bool Bold)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Font, double Size, bool Bold) x, (string Font, double Size, bool Bold) y) =>
            string.Equals(x.Font, y.Font, StringComparison.OrdinalIgnoreCase) && x.Size == y.Size && x.Bold == y.Bold;

        public int GetHashCode((string Font, double Size, bool Bold) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Font), key.Size, key.Bold);
    }
}
