namespace Egap.Spreadsheets;

/// <summary>
/// How a cell looks: its font, fill, borders, alignment, protection and number format. A style is an immutable value; change
/// one with <c>with</c>, as in <c>cell.Style = cell.Style with { Font = cell.Style.Font with { Bold = true } }</c>, or for
/// many cells at once with <see cref="CellRange.UpdateStyle"/>. A workbook keeps each distinct style once, as Excel's cell
/// formats table does, however many cells use it.
/// </summary>
public sealed record CellStyle
{
    /// <summary>The style of a new cell: Calibri 11, no fill, no borders, general alignment, locked, the General number format.</summary>
    public static CellStyle Default { get; } = new();

    /// <summary>The font.</summary>
    public CellFont Font { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } = CellFont.Default;

    /// <summary>The fill behind the cell.</summary>
    public CellFill Fill { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } = CellFill.None;

    /// <summary>The borders.</summary>
    public CellBorders Borders { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } = CellBorders.None;

    /// <summary>The alignment, wrapping, indent and rotation of the cell's text.</summary>
    public CellAlignment Alignment { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } = CellAlignment.Default;

    /// <summary>Whether the cell is locked and its formula hidden when the sheet is protected.</summary>
    public CellProtection Protection { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } = CellProtection.Default;

    /// <summary>
    /// The number format code, as Excel writes it: <c>General</c>, <c>0.00</c>, <c>#,##0</c>, <c>yyyy-mm-dd</c>,
    /// <c>0%</c> and so on. Excel's built-in formats are kept by their number in the file.
    /// </summary>
    public string NumberFormat
    {
        get;
        init => field = string.IsNullOrEmpty(value) ? "General" : value.Length <= 255
            ? value
            : throw new ArgumentException("A number format code is at most 255 characters.", nameof(value));
    } = "General";
}

/// <summary>How text is underlined in a cell.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Excel's own names for these values.")]
public enum CellUnderline
{
    /// <summary>No underline.</summary>
    None,

    /// <summary>One line.</summary>
    Single,

    /// <summary>Two lines.</summary>
    Double,

    /// <summary>One line, drawn lower and across the whole cell as accounting formats do.</summary>
    SingleAccounting,

    /// <summary>Two lines, drawn lower and across the whole cell as accounting formats do.</summary>
    DoubleAccounting,
}

/// <summary>Superscript or subscript text.</summary>
public enum CellVerticalTextPosition
{
    /// <summary>On the baseline.</summary>
    Baseline,

    /// <summary>Raised and smaller.</summary>
    Superscript,

    /// <summary>Lowered and smaller.</summary>
    Subscript,
}

/// <summary>Which of the theme's fonts a cell font follows.</summary>
public enum CellFontScheme
{
    /// <summary>None: the font is named directly.</summary>
    None,

    /// <summary>The theme's heading font.</summary>
    Major,

    /// <summary>The theme's body font, which new cells use.</summary>
    Minor,
}

/// <summary>A cell's font.</summary>
public sealed record CellFont
{
    /// <summary>Excel's default font: Calibri 11 in the theme's text colour. It names its font and follows no theme font: a workbook made here has no theme part, and Excel would take the font from its own default theme.</summary>
    public static CellFont Default { get; } = new();

    /// <summary>The font name.</summary>
    public string Name
    {
        get;
        init => field = !string.IsNullOrEmpty(value) && value.Length <= 31
            ? value
            : throw new ArgumentException("A font name is 1 to 31 characters.", nameof(value));
    } = "Calibri";

    /// <summary>The size in points, from 1 to 409.</summary>
    public double Size
    {
        get;
        init => field = value is >= 1 and <= 409 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A font size is from 1 to 409 points.");
    } = 11;

    /// <summary>Bold.</summary>
    public bool Bold { get; init; }

    /// <summary>Italic.</summary>
    public bool Italic { get; init; }

    /// <summary>The underline.</summary>
    public CellUnderline Underline { get; init; }

    /// <summary>A line through the text.</summary>
    public bool Strikethrough { get; init; }

    /// <summary>Superscript or subscript.</summary>
    public CellVerticalTextPosition VerticalPosition { get; init; }

    /// <summary>The text colour; new cells use the theme's dark 1 (text) colour.</summary>
    public CellColor Color { get; init; } = CellColor.FromTheme(1);

    /// <summary>The font family class, as the file gives it: 0 none, 1 roman, 2 swiss, 3 modern, 4 script, 5 decorative.</summary>
    public int Family { get; init; } = 2;

    /// <summary>The character set the font is meant for, or null when not given.</summary>
    public int? CharacterSet { get; init; }

    /// <summary>Which theme font this font follows; <see cref="CellFontScheme.None"/> for new cells.</summary>
    public CellFontScheme Scheme { get; init; }
}

/// <summary>The pattern of a cell's fill.</summary>
public enum CellFillPattern
{
    /// <summary>No fill.</summary>
    None,

    /// <summary>A solid fill in the foreground colour.</summary>
    Solid,

    /// <summary>50 % gray dots.</summary>
    MediumGray,

    /// <summary>75 % gray dots.</summary>
    DarkGray,

    /// <summary>25 % gray dots.</summary>
    LightGray,

    /// <summary>Thick horizontal stripes.</summary>
    DarkHorizontal,

    /// <summary>Thick vertical stripes.</summary>
    DarkVertical,

    /// <summary>Thick reverse diagonal stripes.</summary>
    DarkDown,

    /// <summary>Thick diagonal stripes.</summary>
    DarkUp,

    /// <summary>A thick grid.</summary>
    DarkGrid,

    /// <summary>A thick diagonal crosshatch.</summary>
    DarkTrellis,

    /// <summary>Thin horizontal stripes.</summary>
    LightHorizontal,

    /// <summary>Thin vertical stripes.</summary>
    LightVertical,

    /// <summary>Thin reverse diagonal stripes.</summary>
    LightDown,

    /// <summary>Thin diagonal stripes.</summary>
    LightUp,

    /// <summary>A thin grid.</summary>
    LightGrid,

    /// <summary>A thin diagonal crosshatch.</summary>
    LightTrellis,

    /// <summary>12.5 % gray dots.</summary>
    Gray125,

    /// <summary>6.25 % gray dots.</summary>
    Gray0625,
}

/// <summary>The fill behind a cell: a pattern in a foreground colour over a background colour.</summary>
public sealed record CellFill
{
    /// <summary>No fill.</summary>
    public static CellFill None { get; } = new();

    /// <summary>The pattern.</summary>
    public CellFillPattern Pattern { get; init; }

    /// <summary>The pattern's colour; a solid fill is all this colour.</summary>
    public CellColor Foreground { get; init; }

    /// <summary>The colour between the pattern's dots or lines.</summary>
    public CellColor Background { get; init; }

    /// <summary>A solid fill in one colour.</summary>
    public static CellFill Solid(CellColor color) => new() { Pattern = CellFillPattern.Solid, Foreground = color };
}

/// <summary>The line style of a cell border.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Excel's own names for these values.")]
public enum CellBorderStyle
{
    /// <summary>No line.</summary>
    None,

    /// <summary>A thin line.</summary>
    Thin,

    /// <summary>A medium line.</summary>
    Medium,

    /// <summary>A thin dashed line.</summary>
    Dashed,

    /// <summary>A thin dotted line.</summary>
    Dotted,

    /// <summary>A thick line.</summary>
    Thick,

    /// <summary>Two thin lines.</summary>
    Double,

    /// <summary>A hairline.</summary>
    Hair,

    /// <summary>A medium dashed line.</summary>
    MediumDashed,

    /// <summary>A thin dash-dot line.</summary>
    DashDot,

    /// <summary>A medium dash-dot line.</summary>
    MediumDashDot,

    /// <summary>A thin dash-dot-dot line.</summary>
    DashDotDot,

    /// <summary>A medium dash-dot-dot line.</summary>
    MediumDashDotDot,

    /// <summary>A medium slanted dash-dot line.</summary>
    SlantDashDot,
}

/// <summary>One edge of a cell's borders.</summary>
/// <param name="Style">The line style.</param>
/// <param name="Color">The line colour; automatic is black.</param>
public readonly record struct CellBorder(CellBorderStyle Style, CellColor Color = default)
{
    /// <summary>No line.</summary>
    public static CellBorder None => default;

    /// <summary>A thin automatic line.</summary>
    public static CellBorder Thin => new(CellBorderStyle.Thin);
}

/// <summary>A cell's borders: four edges and the diagonals.</summary>
public sealed record CellBorders
{
    /// <summary>No borders.</summary>
    public static CellBorders None { get; } = new();

    /// <summary>The left edge.</summary>
    public CellBorder Left { get; init; }

    /// <summary>The right edge.</summary>
    public CellBorder Right { get; init; }

    /// <summary>The top edge.</summary>
    public CellBorder Top { get; init; }

    /// <summary>The bottom edge.</summary>
    public CellBorder Bottom { get; init; }

    /// <summary>The diagonal line, drawn where <see cref="DiagonalUp"/> or <see cref="DiagonalDown"/> asks.</summary>
    public CellBorder Diagonal { get; init; }

    /// <summary>Whether the diagonal runs from bottom left to top right.</summary>
    public bool DiagonalUp { get; init; }

    /// <summary>Whether the diagonal runs from top left to bottom right.</summary>
    public bool DiagonalDown { get; init; }

    /// <summary>The same line on all four edges.</summary>
    public static CellBorders Box(CellBorder edge) => new() { Left = edge, Right = edge, Top = edge, Bottom = edge };
}

/// <summary>How a cell's text sits across the cell.</summary>
public enum CellHorizontalAlignment
{
    /// <summary>Text to the left, numbers to the right, booleans and errors centred, as Excel does by default.</summary>
    General,

    /// <summary>To the left, after any indent.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>To the right, before any indent.</summary>
    Right,

    /// <summary>The text repeated to fill the cell.</summary>
    Fill,

    /// <summary>Wrapped and spread to both edges.</summary>
    Justify,

    /// <summary>Centred across this and the empty cells to its right that share the alignment.</summary>
    CenterContinuous,

    /// <summary>Each word spread evenly across the cell.</summary>
    Distributed,
}

/// <summary>How a cell's text sits up and down in the cell.</summary>
public enum CellVerticalAlignment
{
    /// <summary>At the bottom, as Excel does by default.</summary>
    Bottom,

    /// <summary>At the top.</summary>
    Top,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Lines spread to fill the height.</summary>
    Justify,

    /// <summary>Lines spread evenly across the height.</summary>
    Distributed,
}

/// <summary>The direction of a cell's text.</summary>
public enum CellReadingOrder
{
    /// <summary>From the text's first strong character.</summary>
    Context,

    /// <summary>Left to right.</summary>
    LeftToRight,

    /// <summary>Right to left.</summary>
    RightToLeft,
}

/// <summary>The alignment, wrapping, indent and rotation of a cell's text.</summary>
public sealed record CellAlignment
{
    /// <summary>General alignment at the bottom, no wrap, indent or rotation.</summary>
    public static CellAlignment Default { get; } = new();

    /// <summary>Across the cell.</summary>
    public CellHorizontalAlignment Horizontal { get; init; }

    /// <summary>Up and down in the cell.</summary>
    public CellVerticalAlignment Vertical { get; init; }

    /// <summary>Whether text wraps onto more lines within the cell's width.</summary>
    public bool WrapText { get; init; }

    /// <summary>Whether text is shrunk to fit the cell's width.</summary>
    public bool ShrinkToFit { get; init; }

    /// <summary>The indent in steps of three spaces of the default font, from 0 to 250.</summary>
    public int Indent
    {
        get;
        init => field = value is >= 0 and <= 250 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "An indent is from 0 to 250.");
    }

    /// <summary>
    /// The text's rotation as SpreadsheetML gives it: 0 to 90 degrees up, 91 to 180 for 1 to 90 degrees down, and 255 for
    /// letters stacked top to bottom.
    /// </summary>
    public int TextRotation
    {
        get;
        init => field = value is (>= 0 and <= 180) or 255 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A text rotation is from 0 to 180, or 255 for stacked letters.");
    }

    /// <summary>The direction of the text.</summary>
    public CellReadingOrder ReadingOrder { get; init; }
}

/// <summary>What sheet protection does to a cell.</summary>
public sealed record CellProtection
{
    /// <summary>Locked, formula shown: Excel's default.</summary>
    public static CellProtection Default { get; } = new();

    /// <summary>Whether the cell cannot be changed while the sheet is protected.</summary>
    public bool Locked { get; init; } = true;

    /// <summary>Whether the cell's formula is hidden while the sheet is protected.</summary>
    public bool Hidden { get; init; }
}
