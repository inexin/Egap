namespace Egap.Documents;

/// <summary>
/// How boxes line up along a container's axis: flex and grid containers and their items use the same values. Each property
/// says which values it takes, as CSS gives each its own set; a value a property does not take lines up as Start.
/// </summary>
public enum LayoutAlignment
{
    /// <summary>At the start (left, or top).</summary>
    Start,

    /// <summary>At the end (right, or bottom).</summary>
    End,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Stretched to fill the space; for content, the space is shared among the tracks or lines.</summary>
    Stretch,

    /// <summary>The space shared between the boxes, none before the first or after the last.</summary>
    SpaceBetween,

    /// <summary>The space shared around the boxes, half as much at the ends as between them.</summary>
    SpaceAround,

    /// <summary>The space shared equally before, between and after the boxes.</summary>
    SpaceEvenly,

    /// <summary>CSS baseline alignment, read from HTML and written back; layout lines it up as <see cref="Start"/>.</summary>
    Baseline,
}

/// <summary>The radius of each corner of a box, in points; 0 for a square corner.</summary>
/// <param name="TopLeft">The top left corner.</param>
/// <param name="TopRight">The top right corner.</param>
/// <param name="BottomRight">The bottom right corner.</param>
/// <param name="BottomLeft">The bottom left corner.</param>
public readonly record struct BoxCorners(double TopLeft, double TopRight, double BottomRight, double BottomLeft)
{
    /// <summary>The same radius on every corner.</summary>
    public static BoxCorners All(double radius)
    {
        return new(radius, radius, radius, radius);
    }

    /// <summary>Whether every corner is square.</summary>
    public bool IsSquare => TopLeft <= 0 && TopRight <= 0 && BottomRight <= 0 && BottomLeft <= 0;
}

/// <summary>
/// A shadow a box casts (CSS box-shadow): the box's shape moved by the offsets, grown by <see cref="Spread"/> and blurred by
/// <see cref="Blur"/>, drawn behind the box.
/// </summary>
/// <param name="OffsetX">How far right the shadow is moved, in points.</param>
/// <param name="OffsetY">How far down the shadow is moved, in points.</param>
/// <param name="Blur">The blur radius in points: the shadow fades out over this distance on each side of its edge.</param>
/// <param name="Spread">How much the shadow is grown on every side before it is blurred, in points (negative shrinks it).</param>
public sealed record BoxShadow(double OffsetX, double OffsetY, double Blur = 0, double Spread = 0)
{
    /// <summary>The shadow's colour; black unless set.</summary>
    public DocumentColor Color { get; init; } = DocumentColor.FromRgb(new Egami.Pixels.Rgba8(0, 0, 0, 255));

    /// <summary>The shadow's opacity, from 0 (invisible) to 1.</summary>
    public double Opacity { get; init; } = 1;
}

/// <summary>
/// How a box is drawn around its content: margins, padding, borders, background, rounded corners and a shadow, and the size
/// it asks for. Flex and grid containers and their items, and groups of blocks (<see cref="ContentControl.Box"/>), take one.
/// Immutable; set a new one to change the box.
/// </summary>
public sealed record BoxStyle
{
    /// <summary>The space kept clear above the box, in points; at the top of a page after the first it is left out, as browsers print it.</summary>
    public double MarginTop { get; init; }

    /// <summary>The space kept clear right of the box, in points.</summary>
    public double MarginRight { get; init; }

    /// <summary>The space kept clear below the box, in points.</summary>
    public double MarginBottom { get; init; }

    /// <summary>The space kept clear left of the box, in points.</summary>
    public double MarginLeft { get; init; }

    /// <summary>The space between the top border and the content, in points.</summary>
    public double PaddingTop { get; init; }

    /// <summary>The space between the right border and the content, in points.</summary>
    public double PaddingRight { get; init; }

    /// <summary>The space between the bottom border and the content, in points.</summary>
    public double PaddingBottom { get; init; }

    /// <summary>The space between the left border and the content, in points.</summary>
    public double PaddingLeft { get; init; }

    /// <summary>The top border, or null for none.</summary>
    public Border? BorderTop { get; init; }

    /// <summary>The right border, or null for none.</summary>
    public Border? BorderRight { get; init; }

    /// <summary>The bottom border, or null for none.</summary>
    public Border? BorderBottom { get; init; }

    /// <summary>The left border, or null for none.</summary>
    public Border? BorderLeft { get; init; }

    /// <summary>The background colour, or null for none.</summary>
    public DocumentColor? Background { get; init; }

    /// <summary>The corners' radii; square unless set.</summary>
    public BoxCorners CornerRadius { get; init; }

    /// <summary>The shadow the box casts, or null for none.</summary>
    public BoxShadow? Shadow { get; init; }

    /// <summary>
    /// The box's width, borders and padding included and margins not: automatic (from the layout), in points or in per cent
    /// of the space its container gives it.
    /// </summary>
    public TableWidth Width { get; init; }

    /// <summary>The least height of the box in points, borders and padding included; a taller content makes it taller.</summary>
    public double MinHeight { get; init; }

    /// <summary>The same padding on every side.</summary>
    public static BoxStyle Padded(double padding)
    {
        return new BoxStyle { PaddingTop = padding, PaddingRight = padding, PaddingBottom = padding, PaddingLeft = padding };
    }

    /// <summary>The same border on every side.</summary>
    public BoxStyle WithBorder(Border? border)
    {
        return this with { BorderTop = border, BorderRight = border, BorderBottom = border, BorderLeft = border };
    }

    /// <summary>The margins across: left and right.</summary>
    internal double MarginsX => MarginLeft + MarginRight;

    /// <summary>The margins down: top and bottom.</summary>
    internal double MarginsY => MarginTop + MarginBottom;

    /// <summary>The space the borders and padding take across: left and right.</summary>
    internal double Horizontal => PaddingLeft + PaddingRight + Visible(BorderLeft) + Visible(BorderRight);

    /// <summary>The space the borders and padding take down: top and bottom.</summary>
    internal double Vertical => PaddingTop + PaddingBottom + Visible(BorderTop) + Visible(BorderBottom);

    /// <summary>The left inset of the content: border and padding.</summary>
    internal double InsetLeft => PaddingLeft + Visible(BorderLeft);

    /// <summary>The top inset of the content: border and padding.</summary>
    internal double InsetTop => PaddingTop + Visible(BorderTop);

    /// <summary>The right inset of the content: border and padding.</summary>
    internal double InsetRight => PaddingRight + Visible(BorderRight);

    /// <summary>The bottom inset of the content: border and padding.</summary>
    internal double InsetBottom => PaddingBottom + Visible(BorderBottom);

    /// <summary>Whether the box draws anything of its own: a background, a border or a shadow.</summary>
    internal bool Draws => Background is not null || Shadow is not null || Visible(BorderTop) + Visible(BorderRight) + Visible(BorderBottom) + Visible(BorderLeft) > 0;

    private static double Visible(Border? border)
    {
        return border is { IsVisible: true } ? border.Width : 0;
    }
}
