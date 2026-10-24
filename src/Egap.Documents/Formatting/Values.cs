using System.Globalization;

using Egami.Pixels;

namespace Egap.Documents;

/// <summary>Which of a theme's twelve colours a <see cref="DocumentColor"/> names, as Word's themes have them.</summary>
public enum ThemeColor
{
    /// <summary>Dark 1, usually black text.</summary>
    Dark1,

    /// <summary>Light 1, usually the white background.</summary>
    Light1,

    /// <summary>Dark 2.</summary>
    Dark2,

    /// <summary>Light 2.</summary>
    Light2,

    /// <summary>Accent 1.</summary>
    Accent1,

    /// <summary>Accent 2.</summary>
    Accent2,

    /// <summary>Accent 3.</summary>
    Accent3,

    /// <summary>Accent 4.</summary>
    Accent4,

    /// <summary>Accent 5.</summary>
    Accent5,

    /// <summary>Accent 6.</summary>
    Accent6,

    /// <summary>Hyperlink.</summary>
    Hyperlink,

    /// <summary>Followed hyperlink.</summary>
    FollowedHyperlink,
}

/// <summary>How a <see cref="DocumentColor"/> is given.</summary>
public enum DocumentColorKind
{
    /// <summary>Automatic: the application picks, black text on a light background as Word does.</summary>
    Auto,

    /// <summary>A fixed colour.</summary>
    Rgb,

    /// <summary>A colour of the document's theme, lightened or darkened.</summary>
    Theme,
}

/// <summary>
/// A colour as documents give it: automatic, a fixed colour, or a theme colour with Word's tint (towards white) or shade
/// (towards black), so a theme change recolours the text that uses it. The default value is automatic.
/// </summary>
public readonly record struct DocumentColor
{
    private DocumentColor(DocumentColorKind kind, Rgba8 rgb, ThemeColor theme, double tint, double shade)
    {
        Kind = kind;
        Rgb = rgb;
        Theme = theme;
        Tint = tint;
        Shade = shade;
    }

    /// <summary>Automatic colour.</summary>
    public static DocumentColor Auto => default;

    /// <summary>How the colour is given.</summary>
    public DocumentColorKind Kind { get; }

    /// <summary>For <see cref="DocumentColorKind.Rgb"/>, the colour; for a theme colour, the value the file stored beside it, if any.</summary>
    public Rgba8 Rgb { get; }

    /// <summary>For <see cref="DocumentColorKind.Theme"/>, which theme colour.</summary>
    public ThemeColor Theme { get; }

    /// <summary>For a theme colour, how far its luminance moves towards white, 0 to 1 (1 minus Word's themeTint over 255).</summary>
    public double Tint { get; }

    /// <summary>For a theme colour, how far its luminance moves towards black, 0 to 1 (1 minus Word's themeShade over 255).</summary>
    public double Shade { get; }

    /// <summary>A fixed colour.</summary>
    public static DocumentColor FromRgb(Rgba8 rgb)
    {
        return new(DocumentColorKind.Rgb, rgb, default, 0, 0);
    }

    /// <summary>A fixed colour from hex, such as "#C00000" or "C00000".</summary>
    public static DocumentColor FromHex(string hex)
    {
        return FromRgb(Rgba8.FromHex(hex));
    }

    /// <summary>A theme colour, optionally moved towards white (<paramref name="tint"/>) or black (<paramref name="shade"/>).</summary>
    public static DocumentColor FromTheme(ThemeColor theme, double tint = 0, double shade = 0, Rgba8 stored = default)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(tint, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tint, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(shade, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shade, 1);
        return new(DocumentColorKind.Theme, stored, theme, tint, shade);
    }

    /// <summary>The colour to draw with: a theme colour through <paramref name="theme"/>, automatic as <paramref name="auto"/>.</summary>
    public Rgba8 Resolve(Theme? theme, Rgba8 auto)
    {
        switch (Kind)
        {
            case DocumentColorKind.Rgb:
                return Rgb;
            case DocumentColorKind.Theme:
                Rgba8 baseColor = theme?.Colors[Theme] ?? (Rgb.A != 0 ? Rgb : auto);
                return Adjust(baseColor, Tint, Shade);
            default:
                return auto;
        }
    }

    /// <summary>The colour as text: "auto", "#RRGGBB" or the theme colour with its tint and shade.</summary>
    public override string ToString()
    {
        return Kind switch
        {
            DocumentColorKind.Rgb => Rgb.ToHex()[..7],
            DocumentColorKind.Theme => Theme + (Tint > 0 ? " tint " + Tint.ToString("0.###", CultureInfo.InvariantCulture) : "")
                + (Shade > 0 ? " shade " + Shade.ToString("0.###", CultureInfo.InvariantCulture) : ""),
            _ => "auto",
        };
    }

    private static Rgba8 Adjust(Rgba8 color, double tint, double shade)
    {
        if (tint == 0 && shade == 0)
        {
            return new Rgba8(color.R, color.G, color.B, 255);
        }

        // Word moves the luminance in HSL: a shade scales it towards black, a tint towards white (so Office's accent 1,
        // #4472C4, with Heading 1's shade of 25 % gives Word's #2F5496).
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
            h /= 6;
        }

        l *= 1 - shade;
        l += (1 - l) * tint;
        double q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        double p = (2 * l) - q;
        return new Rgba8(Channel(p, q, h + (1 / 3.0)), Channel(p, q, h), Channel(p, q, h - (1 / 3.0)), 255);

        static byte Channel(double p, double q, double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            double v = t < 1 / 6.0 ? p + ((q - p) * 6 * t) : t < 0.5 ? q : t < 2 / 3.0 ? p + ((q - p) * ((2 / 3.0) - t) * 6) : p;
            return (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        }
    }
}

/// <summary>Which theme font a run's font slot names.</summary>
public enum ThemeFont
{
    /// <summary>The theme's major (headings) Latin font.</summary>
    MajorLatin,

    /// <summary>The theme's major East Asian font.</summary>
    MajorEastAsia,

    /// <summary>The theme's major complex script font.</summary>
    MajorComplexScript,

    /// <summary>The theme's minor (body) Latin font.</summary>
    MinorLatin,

    /// <summary>The theme's minor East Asian font.</summary>
    MinorEastAsia,

    /// <summary>The theme's minor complex script font.</summary>
    MinorComplexScript,
}

/// <summary>A line style for borders, as Word's border types without the art borders (kept by the DOCX reader as extension data).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Word's own names for these values.")]
public enum BorderStyle
{
    /// <summary>No border.</summary>
    None,

    /// <summary>A single line.</summary>
    Single,

    /// <summary>A thick single line.</summary>
    Thick,

    /// <summary>Two lines.</summary>
    Double,

    /// <summary>Dots.</summary>
    Dotted,

    /// <summary>Dashes.</summary>
    Dashed,

    /// <summary>Dash, dot.</summary>
    DotDash,

    /// <summary>Dash, dot, dot.</summary>
    DotDotDash,

    /// <summary>Three lines.</summary>
    Triple,

    /// <summary>Thin, thick, with a small gap.</summary>
    ThinThickSmallGap,

    /// <summary>Thick, thin, with a small gap.</summary>
    ThickThinSmallGap,

    /// <summary>Thin, thick, thin, with a small gap.</summary>
    ThinThickThinSmallGap,

    /// <summary>Thin, thick, with a medium gap.</summary>
    ThinThickMediumGap,

    /// <summary>Thick, thin, with a medium gap.</summary>
    ThickThinMediumGap,

    /// <summary>Thin, thick, thin, with a medium gap.</summary>
    ThinThickThinMediumGap,

    /// <summary>Thin, thick, with a large gap.</summary>
    ThinThickLargeGap,

    /// <summary>Thick, thin, with a large gap.</summary>
    ThickThinLargeGap,

    /// <summary>Thin, thick, thin, with a large gap.</summary>
    ThinThickThinLargeGap,

    /// <summary>A wavy line.</summary>
    Wave,

    /// <summary>Two wavy lines.</summary>
    DoubleWave,

    /// <summary>Short dashes with small gaps.</summary>
    DashSmallGap,

    /// <summary>Dash, dot, stroked.</summary>
    DashDotStroked,

    /// <summary>Embossed (3D).</summary>
    ThreeDEmboss,

    /// <summary>Engraved (3D).</summary>
    ThreeDEngrave,

    /// <summary>Outset.</summary>
    Outset,

    /// <summary>Inset.</summary>
    Inset,
}

/// <summary>One border line: style, width and colour, the space between it and the text, and Word's shadow and frame flags.</summary>
/// <param name="Style">The line style.</param>
/// <param name="Width">The line width in points.</param>
/// <param name="Color">The colour.</param>
/// <param name="Space">The space between the border and the text, in points.</param>
/// <param name="Shadow">Whether the border has a shadow.</param>
/// <param name="Frame">Whether the border looks raised (Word's frame effect).</param>
public sealed record Border(BorderStyle Style, double Width = 0.5, DocumentColor Color = default, double Space = 0, bool Shadow = false, bool Frame = false)
{
    /// <summary>No border; set on a side to remove a border a style gives it.</summary>
    public static Border None { get; } = new(BorderStyle.None, 0);

    /// <summary>Whether a line is drawn.</summary>
    public bool IsVisible => Style != BorderStyle.None && Width > 0;
}

/// <summary>A shading pattern: Word's set, from clear (fill only) to solid (pattern colour only) by percentages and hatches.</summary>
public enum ShadingPattern
{
    /// <summary>The fill colour only.</summary>
    Clear,

    /// <summary>The pattern colour only.</summary>
    Solid,

    /// <summary>No shading.</summary>
    Nil,

    /// <summary>Horizontal stripes.</summary>
    HorizontalStripe,

    /// <summary>Vertical stripes.</summary>
    VerticalStripe,

    /// <summary>Reverse diagonal stripes.</summary>
    ReverseDiagonalStripe,

    /// <summary>Diagonal stripes.</summary>
    DiagonalStripe,

    /// <summary>A horizontal cross.</summary>
    HorizontalCross,

    /// <summary>A diagonal cross.</summary>
    DiagonalCross,

    /// <summary>Thin horizontal stripes.</summary>
    ThinHorizontalStripe,

    /// <summary>Thin vertical stripes.</summary>
    ThinVerticalStripe,

    /// <summary>Thin reverse diagonal stripes.</summary>
    ThinReverseDiagonalStripe,

    /// <summary>Thin diagonal stripes.</summary>
    ThinDiagonalStripe,

    /// <summary>A thin horizontal cross.</summary>
    ThinHorizontalCross,

    /// <summary>A thin diagonal cross.</summary>
    ThinDiagonalCross,

    /// <summary>A percentage of the pattern colour over the fill, given by <see cref="Shading.Percent"/>.</summary>
    Percent,
}

/// <summary>Shading behind text, a paragraph or a cell: a fill colour, a pattern and the pattern's colour.</summary>
/// <param name="Pattern">The pattern.</param>
/// <param name="Fill">The background colour.</param>
/// <param name="PatternColor">The colour of the pattern drawn over the fill.</param>
/// <param name="Percent">For <see cref="ShadingPattern.Percent"/>, how much of the pattern colour, 0 to 100.</param>
public sealed record Shading(ShadingPattern Pattern, DocumentColor Fill = default, DocumentColor PatternColor = default, double Percent = 0)
{
    /// <summary>A plain fill of <paramref name="color"/>.</summary>
    public static Shading Solid(DocumentColor color)
    {
        return new(ShadingPattern.Clear, color);
    }
}

/// <summary>How text lines up at a tab stop.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Word's own names for these values.")]
public enum TabAlignment
{
    /// <summary>Text starts at the stop.</summary>
    Left,

    /// <summary>Text is centred on the stop.</summary>
    Center,

    /// <summary>Text ends at the stop.</summary>
    Right,

    /// <summary>The decimal separator sits at the stop.</summary>
    Decimal,

    /// <summary>A vertical bar is drawn at the stop; text does not stop there.</summary>
    Bar,

    /// <summary>Removes a stop at this position that a style gives the paragraph.</summary>
    Clear,
}

/// <summary>What fills the space before a tab stop.</summary>
public enum TabLeader
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Dots.</summary>
    Dot,

    /// <summary>Hyphens.</summary>
    Hyphen,

    /// <summary>An underline.</summary>
    Underscore,

    /// <summary>A heavy line.</summary>
    Heavy,

    /// <summary>Middle dots.</summary>
    MiddleDot,
}

/// <summary>A tab stop: where, how text lines up, and the leader.</summary>
/// <param name="Position">The position in points from the paragraph's left indent.</param>
/// <param name="Alignment">How text lines up at the stop, or Clear to remove an inherited stop.</param>
/// <param name="Leader">What fills the space before the stop.</param>
public sealed record TabStop(double Position, TabAlignment Alignment = TabAlignment.Left, TabLeader Leader = TabLeader.None);

/// <summary>How text is underlined, as Word's underline types.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Word's own names for these values.")]
public enum UnderlineStyle
{
    /// <summary>No underline.</summary>
    None,

    /// <summary>A single line.</summary>
    Single,

    /// <summary>A single line under words, not spaces.</summary>
    Words,

    /// <summary>Two lines.</summary>
    Double,

    /// <summary>A thick line.</summary>
    Thick,

    /// <summary>Dots.</summary>
    Dotted,

    /// <summary>Heavy dots.</summary>
    DottedHeavy,

    /// <summary>Dashes.</summary>
    Dash,

    /// <summary>Heavy dashes.</summary>
    DashedHeavy,

    /// <summary>Long dashes.</summary>
    DashLong,

    /// <summary>Heavy long dashes.</summary>
    DashLongHeavy,

    /// <summary>Dash, dot.</summary>
    DotDash,

    /// <summary>Heavy dash, dot.</summary>
    DashDotHeavy,

    /// <summary>Dash, dot, dot.</summary>
    DotDotDash,

    /// <summary>Heavy dash, dot, dot.</summary>
    DashDotDotHeavy,

    /// <summary>A wavy line.</summary>
    Wave,

    /// <summary>A heavy wavy line.</summary>
    WavyHeavy,

    /// <summary>Two wavy lines.</summary>
    WavyDouble,
}

/// <summary>Raised or lowered text.</summary>
public enum VerticalTextPosition
{
    /// <summary>On the baseline.</summary>
    Baseline,

    /// <summary>Superscript.</summary>
    Superscript,

    /// <summary>Subscript.</summary>
    Subscript,
}

/// <summary>Text highlighting colours, the sixteen Word offers.</summary>
public enum HighlightColor
{
    /// <summary>No highlight.</summary>
    None,

    /// <summary>Black.</summary>
    Black,

    /// <summary>Blue.</summary>
    Blue,

    /// <summary>Cyan.</summary>
    Cyan,

    /// <summary>Green.</summary>
    Green,

    /// <summary>Magenta.</summary>
    Magenta,

    /// <summary>Red.</summary>
    Red,

    /// <summary>Yellow.</summary>
    Yellow,

    /// <summary>White.</summary>
    White,

    /// <summary>Dark blue.</summary>
    DarkBlue,

    /// <summary>Dark cyan.</summary>
    DarkCyan,

    /// <summary>Dark green.</summary>
    DarkGreen,

    /// <summary>Dark magenta.</summary>
    DarkMagenta,

    /// <summary>Dark red.</summary>
    DarkRed,

    /// <summary>Dark yellow.</summary>
    DarkYellow,

    /// <summary>Dark grey.</summary>
    DarkGray,

    /// <summary>Light grey.</summary>
    LightGray,
}

/// <summary>How a paragraph's lines line up between its indents.</summary>
public enum ParagraphAlignment
{
    /// <summary>Against the start (left in left-to-right text).</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Against the end.</summary>
    Right,

    /// <summary>Spread to both indents, except the last line.</summary>
    Justify,

    /// <summary>Spread to both indents, the last line too, by widening spaces and letters.</summary>
    Distribute,
}

/// <summary>
/// How far a justified paragraph's Arabic lines are stretched with tatweels (kashida) between letters, as Word's low, medium
/// and high kashida alignments; the rest of a line's free width goes to word spaces. Scripts without kashida justify at spaces.
/// </summary>
public enum KashidaLevel
{
    /// <summary>No kashida: lines are justified at word spaces only.</summary>
    None,

    /// <summary>Short kashidas, most of the free width at spaces (Word's lowKashida).</summary>
    Low,

    /// <summary>Medium kashidas (Word's mediumKashida).</summary>
    Medium,

    /// <summary>Long kashidas, most of the free width in the words (Word's highKashida).</summary>
    High,
}

/// <summary>How <see cref="ParagraphFormat.LineSpacing"/> is read.</summary>
public enum LineSpacingRule
{
    /// <summary>A multiple of single spacing (1 is single, 1.5, 2 double).</summary>
    Multiple,

    /// <summary>At least this many points; taller lines grow.</summary>
    AtLeast,

    /// <summary>Exactly this many points; taller content is clipped.</summary>
    Exactly,

    /// <summary>
    /// At least this many points, the extra space shared above and below the text as CSS's line-height shares it (half
    /// leading); taller lines grow. DOCX, which has no such rule, saves it as <see cref="AtLeast"/>.
    /// </summary>
    AtLeastCentered,
}

/// <summary>How a table, cell or row width or height is given.</summary>
public enum WidthUnit
{
    /// <summary>Decided by the layout from the content.</summary>
    Auto,

    /// <summary>In points.</summary>
    Points,

    /// <summary>In per cent of the space available.</summary>
    Percent,
}

/// <summary>A table or cell width: automatic, in points or in per cent.</summary>
/// <param name="Unit">How <paramref name="Value"/> is read.</param>
/// <param name="Value">The width in points or per cent; 0 for automatic.</param>
public readonly record struct TableWidth(WidthUnit Unit, double Value)
{
    /// <summary>Automatic width.</summary>
    public static TableWidth Auto => new(WidthUnit.Auto, 0);

    /// <summary>A width in points.</summary>
    public static TableWidth Points(double points)
    {
        return new(WidthUnit.Points, points);
    }

    /// <summary>A width in per cent.</summary>
    public static TableWidth Percent(double percent)
    {
        return new(WidthUnit.Percent, percent);
    }
}

/// <summary>How a table's column widths are found.</summary>
public enum TableLayoutKind
{
    /// <summary>From the content, within the widths given.</summary>
    Autofit,

    /// <summary>From the widths given, whatever the content.</summary>
    Fixed,
}

/// <summary>Where a table sits between the margins.</summary>
public enum TableAlignment
{
    /// <summary>At the start, moved by <see cref="TableFormat.Indent"/>.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>At the end.</summary>
    Right,
}

/// <summary>How a row's height is read.</summary>
public enum RowHeightRule
{
    /// <summary>From the content.</summary>
    Auto,

    /// <summary>At least the height given.</summary>
    AtLeast,

    /// <summary>Exactly the height given.</summary>
    Exactly,
}

/// <summary>Where content sits vertically in a cell, or a page's text in a section.</summary>
public enum VerticalAlignment
{
    /// <summary>At the top.</summary>
    Top,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>At the bottom.</summary>
    Bottom,

    /// <summary>Spread over the height (sections only).</summary>
    Justify,
}

/// <summary>A cell's part in a vertical merge.</summary>
public enum VerticalMerge
{
    /// <summary>Not merged.</summary>
    None,

    /// <summary>The first cell of a vertically merged range.</summary>
    Restart,

    /// <summary>A cell merged into the one above.</summary>
    Continue,
}

/// <summary>Which way text runs in a cell or section.</summary>
public enum TextDirection
{
    /// <summary>Left to right, top to bottom.</summary>
    Horizontal,

    /// <summary>Top to bottom, right to left (rotated 90 degrees).</summary>
    TopToBottom,

    /// <summary>Bottom to top, left to right (rotated 270 degrees).</summary>
    BottomToTop,
}

/// <summary>Page orientation.</summary>
public enum PageOrientation
{
    /// <summary>Taller than wide.</summary>
    Portrait,

    /// <summary>Wider than tall.</summary>
    Landscape,
}

/// <summary>Where a section starts.</summary>
public enum SectionStart
{
    /// <summary>On a new page.</summary>
    NewPage,

    /// <summary>On the same page, after the previous section.</summary>
    Continuous,

    /// <summary>On the next odd page.</summary>
    OddPage,

    /// <summary>On the next even page.</summary>
    EvenPage,

    /// <summary>In the next column.</summary>
    NewColumn,
}

/// <summary>How page numbers are written.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Word's own names for these values.")]
public enum NumberStyle
{
    /// <summary>1, 2, 3.</summary>
    Decimal,

    /// <summary>I, II, III.</summary>
    UpperRoman,

    /// <summary>i, ii, iii.</summary>
    LowerRoman,

    /// <summary>A, B, C.</summary>
    UpperLetter,

    /// <summary>a, b, c.</summary>
    LowerLetter,

    /// <summary>1st, 2nd, 3rd.</summary>
    Ordinal,

    /// <summary>One, Two, Three.</summary>
    CardinalText,

    /// <summary>First, Second, Third.</summary>
    OrdinalText,

    /// <summary>01, 02, 03.</summary>
    DecimalZero,

    /// <summary>A bullet: the level text is shown as it is.</summary>
    Bullet,

    /// <summary>No number.</summary>
    None,
}
