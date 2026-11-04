using System.Globalization;

namespace Egap.Spreadsheets;

/// <summary>How a <see cref="CellColor"/> is given.</summary>
public enum CellColorKind
{
    /// <summary>Automatic: Excel picks, black text and borders as a rule.</summary>
    Auto,

    /// <summary>A fixed ARGB colour.</summary>
    Rgb,

    /// <summary>One of the workbook theme's colours, lightened or darkened by a tint.</summary>
    Theme,

    /// <summary>An entry of the legacy 64-colour palette, as old files and Excel 97 give colours.</summary>
    Indexed,
}

/// <summary>
/// A colour as spreadsheets give it: automatic, a fixed ARGB colour, a theme colour with a tint, or a legacy palette index.
/// The default value is automatic.
/// </summary>
public readonly record struct CellColor
{
    private CellColor(CellColorKind kind, uint value, double tint)
    {
        Kind = kind;
        Value = value;
        Tint = tint;
    }

    /// <summary>Automatic colour.</summary>
    public static CellColor Auto => default;

    /// <summary>Black.</summary>
    public static CellColor Black => FromRgb(0, 0, 0);

    /// <summary>White.</summary>
    public static CellColor White => FromRgb(255, 255, 255);

    /// <summary>How the colour is given.</summary>
    public CellColorKind Kind { get; }

    /// <summary>
    /// The ARGB value for <see cref="CellColorKind.Rgb"/> (<c>0xFF0000FF</c> is opaque blue), the theme index for
    /// <see cref="CellColorKind.Theme"/>, or the palette index for <see cref="CellColorKind.Indexed"/>; 0 for automatic.
    /// </summary>
    public uint Value { get; }

    /// <summary>
    /// How much the colour is lightened (towards 1, white) or darkened (towards -1, black); 0 leaves it as it is. Any kind
    /// of colour but automatic may carry one.
    /// </summary>
    public double Tint { get; }

    /// <summary>An opaque colour from red, green and blue.</summary>
    public static CellColor FromRgb(byte red, byte green, byte blue) =>
        new(CellColorKind.Rgb, 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue, 0);

    /// <summary>A colour from its ARGB value, as SpreadsheetML writes it (<c>FF4472C4</c> is 0xFF4472C4).</summary>
    public static CellColor FromArgb(uint argb, double tint = 0) => new(CellColorKind.Rgb, argb, CheckTint(tint));

    /// <summary>
    /// A theme colour by its index as SpreadsheetML numbers them: 0 light 1 (the background), 1 dark 1 (the text), 2 light
    /// 2, 3 dark 2, 4 to 9 the six accents, 10 hyperlink and 11 followed hyperlink.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is past 11, or the tint is outside -1 to 1.</exception>
    public static CellColor FromTheme(int index, double tint = 0)
    {
        return (uint)index <= 11
            ? new CellColor(CellColorKind.Theme, (uint)index, CheckTint(tint))
            : throw new ArgumentOutOfRangeException(nameof(index), index, "A theme colour is from 0 to 11.");
    }

    /// <summary>
    /// An entry of the legacy palette: 0 to 63, 64 for the system foreground and 65 for the system background.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is past 65, or the tint is outside -1 to 1.</exception>
    public static CellColor FromIndex(int index, double tint = 0)
    {
        return (uint)index <= 65
            ? new CellColor(CellColorKind.Indexed, (uint)index, CheckTint(tint))
            : throw new ArgumentOutOfRangeException(nameof(index), index, "A palette index is from 0 to 65.");
    }

    /// <summary>The colour as text: <c>auto</c>, <c>#AARRGGBB</c>, <c>theme 4</c> or <c>indexed 10</c>, with any tint.</summary>
    public override string ToString()
    {
        string text = Kind switch
        {
            CellColorKind.Rgb => "#" + Value.ToString("X8", CultureInfo.InvariantCulture),
            CellColorKind.Theme => "theme " + Value.ToString(CultureInfo.InvariantCulture),
            CellColorKind.Indexed => "indexed " + Value.ToString(CultureInfo.InvariantCulture),
            _ => "auto",
        };
        return Tint == 0 ? text : text + " tint " + Tint.ToString("R", CultureInfo.InvariantCulture);
    }

    private static double CheckTint(double tint)
    {
        return tint is >= -1 and <= 1
            ? tint
            : throw new ArgumentOutOfRangeException(nameof(tint), tint, "A tint is from -1 to 1.");
    }
}
