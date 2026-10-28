namespace Egap.Documents;

/// <summary>
/// Where a floating table sits and how text keeps clear of it (Word's <c>w:tblpPr</c> and <c>w:tblOverlap</c>). A table with
/// a position is laid out as a float that text wraps around on the side with more room, and continues on the next page when
/// it does not fit. Immutable; set a new one to move the table.
/// </summary>
public sealed record TablePosition
{
    /// <summary>What the horizontal position is measured from: <see cref="HorizontalAnchor.Column"/> (the text), the margins or the page.</summary>
    public HorizontalAnchor HorizontalAnchor { get; init; } = HorizontalAnchor.Column;

    /// <summary>How it lines up horizontally, or None for <see cref="HorizontalOffset"/>.</summary>
    public FloatingAlignment HorizontalAlignment { get; init; }

    /// <summary>The horizontal offset in points, when not aligned.</summary>
    public double HorizontalOffset { get; init; }

    /// <summary>What the vertical position is measured from: <see cref="VerticalAnchor.Paragraph"/> (the text), the margins or the page.</summary>
    public VerticalAnchor VerticalAnchor { get; init; } = VerticalAnchor.Paragraph;

    /// <summary>How it lines up vertically, or None for <see cref="VerticalOffset"/>.</summary>
    public FloatingAlignment VerticalAlignment { get; init; }

    /// <summary>The vertical offset in points, when not aligned.</summary>
    public double VerticalOffset { get; init; }

    /// <summary>The space text keeps on the left, in points.</summary>
    public double DistanceLeft { get; init; }

    /// <summary>The space text keeps on the right, in points.</summary>
    public double DistanceRight { get; init; }

    /// <summary>The space text keeps above, in points.</summary>
    public double DistanceTop { get; init; }

    /// <summary>The space text keeps below, in points.</summary>
    public double DistanceBottom { get; init; }

    /// <summary>Other floating tables may overlap this one; when false, a later one moves down until it clears this.</summary>
    public bool AllowOverlap { get; init; } = true;
}

/// <summary>How text wraps around a paragraph frame (Word's <c>w:framePr w:wrap</c>).</summary>
public enum FrameWrap
{
    /// <summary>Word's default: text wraps around the frame.</summary>
    Auto,

    /// <summary>Text wraps around the frame.</summary>
    Around,

    /// <summary>No text beside the frame: text goes on below it.</summary>
    NotBeside,

    /// <summary>No wrapping: text flows as if the frame were not there.</summary>
    None,

    /// <summary>Text wraps tightly around the frame's contents.</summary>
    Tight,

    /// <summary>Text wraps through the frame's contents.</summary>
    Through,
}

/// <summary>A paragraph frame's drop cap kind (Word's <c>w:dropCap</c>).</summary>
public enum DropCap
{
    /// <summary>No drop cap: an ordinary frame.</summary>
    None,

    /// <summary>A letter dropped into the text, at the start of the next paragraph.</summary>
    Drop,

    /// <summary>A letter dropped into the margin beside the next paragraph.</summary>
    Margin,
}

/// <summary>
/// An old-style text frame (Word's <c>w:framePr</c>): the paragraph, with the paragraphs after it that have the same frame,
/// is placed as a float by its anchors and alignment, and is never split across pages. A frame with a <see cref="DropCap"/>
/// is a drop cap: its text sits at the start of the next paragraph, which wraps beside it. Immutable.
/// </summary>
public sealed record FramePosition
{
    /// <summary>The frame's width in points, or null for the width of its text.</summary>
    public double? Width { get; init; }

    /// <summary>The frame's height in points, read as <see cref="HeightRule"/> says; null for the height of its text.</summary>
    public double? Height { get; init; }

    /// <summary>How <see cref="Height"/> is read.</summary>
    public RowHeightRule HeightRule { get; init; }

    /// <summary>What the horizontal position is measured from: <see cref="HorizontalAnchor.Column"/> (the text), the margins or the page.</summary>
    public HorizontalAnchor HorizontalAnchor { get; init; } = HorizontalAnchor.Column;

    /// <summary>How it lines up horizontally, or None for <see cref="HorizontalOffset"/>.</summary>
    public FloatingAlignment HorizontalAlignment { get; init; }

    /// <summary>The horizontal offset in points, when not aligned.</summary>
    public double HorizontalOffset { get; init; }

    /// <summary>What the vertical position is measured from: <see cref="VerticalAnchor.Paragraph"/> (the text), the margins or the page.</summary>
    public VerticalAnchor VerticalAnchor { get; init; } = VerticalAnchor.Paragraph;

    /// <summary>How it lines up vertically, or None for <see cref="VerticalOffset"/>.</summary>
    public FloatingAlignment VerticalAlignment { get; init; }

    /// <summary>The vertical offset in points, when not aligned.</summary>
    public double VerticalOffset { get; init; }

    /// <summary>The space text keeps on the left and right, in points.</summary>
    public double DistanceHorizontal { get; init; }

    /// <summary>The space text keeps above and below, in points.</summary>
    public double DistanceVertical { get; init; }

    /// <summary>How text wraps around the frame.</summary>
    public FrameWrap Wrap { get; init; }

    /// <summary>Whether the frame is a drop cap, and which kind.</summary>
    public DropCap DropCap { get; init; }

    /// <summary>How many lines a drop cap is tall.</summary>
    public int DropCapLines { get; init; } = 1;

    /// <summary>The anchor stays with its paragraph when the frame is moved in an editor (Word's <c>w:anchorLock</c>).</summary>
    public bool AnchorLock { get; init; }
}
