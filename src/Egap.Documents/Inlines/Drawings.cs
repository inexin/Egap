namespace Egap.Documents;

/// <summary>What a floating object's horizontal position is measured from.</summary>
public enum HorizontalAnchor
{
    /// <summary>The column the anchor paragraph is in.</summary>
    Column,

    /// <summary>The page edge.</summary>
    Page,

    /// <summary>The margins.</summary>
    Margin,

    /// <summary>The character the object is anchored to.</summary>
    Character,

    /// <summary>The left margin area.</summary>
    LeftMargin,

    /// <summary>The right margin area.</summary>
    RightMargin,

    /// <summary>The inside margin (left on odd pages).</summary>
    InsideMargin,

    /// <summary>The outside margin.</summary>
    OutsideMargin,
}

/// <summary>What a floating object's vertical position is measured from.</summary>
public enum VerticalAnchor
{
    /// <summary>The anchor paragraph.</summary>
    Paragraph,

    /// <summary>The page edge.</summary>
    Page,

    /// <summary>The margins.</summary>
    Margin,

    /// <summary>The line the object is anchored to.</summary>
    Line,

    /// <summary>The top margin area.</summary>
    TopMargin,

    /// <summary>The bottom margin area.</summary>
    BottomMargin,

    /// <summary>The inside margin.</summary>
    InsideMargin,

    /// <summary>The outside margin.</summary>
    OutsideMargin,
}

/// <summary>Where a floating object lines up within what it is measured from, instead of an offset.</summary>
public enum FloatingAlignment
{
    /// <summary>No alignment: the offset applies.</summary>
    None,

    /// <summary>At the start (left or top).</summary>
    Start,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>At the end (right or bottom).</summary>
    End,

    /// <summary>Inside (left on odd pages, right on even).</summary>
    Inside,

    /// <summary>Outside.</summary>
    Outside,
}

/// <summary>How text flows around a floating object (Phase 6 design Q11: all six of Word's wrap modes).</summary>
public enum WrapMode
{
    /// <summary>Text flows around the object's bounding box.</summary>
    Square,

    /// <summary>Text flows around the object's outline (its wrap polygon).</summary>
    Tight,

    /// <summary>Text flows around the outline and into its open parts.</summary>
    Through,

    /// <summary>Text stops above the object and goes on below it.</summary>
    TopAndBottom,

    /// <summary>The object is behind the text, which flows over it.</summary>
    BehindText,

    /// <summary>The object is in front of the text, which flows under it.</summary>
    InFrontOfText,
}

/// <summary>Which sides of a floating object text flows on.</summary>
public enum WrapSide
{
    /// <summary>Both sides.</summary>
    Both,

    /// <summary>Only the left.</summary>
    Left,

    /// <summary>Only the right.</summary>
    Right,

    /// <summary>The side with more room.</summary>
    Largest,
}

/// <summary>
/// Where a floating picture, shape or text box sits and how text wraps around it (Word's anchor): position by alignment or
/// offset from a reference area, wrap mode and side, the distances text keeps, and the stacking order. Immutable; set a new
/// one to move the object.
/// </summary>
public sealed record FloatingPlacement
{
    /// <summary>What the horizontal position is measured from.</summary>
    public HorizontalAnchor HorizontalAnchor { get; init; } = HorizontalAnchor.Column;

    /// <summary>How it lines up horizontally, or None for <see cref="HorizontalOffset"/>.</summary>
    public FloatingAlignment HorizontalAlignment { get; init; }

    /// <summary>The horizontal offset in points, when not aligned.</summary>
    public double HorizontalOffset { get; init; }

    /// <summary>What the vertical position is measured from.</summary>
    public VerticalAnchor VerticalAnchor { get; init; } = VerticalAnchor.Paragraph;

    /// <summary>How it lines up vertically, or None for <see cref="VerticalOffset"/>.</summary>
    public FloatingAlignment VerticalAlignment { get; init; }

    /// <summary>The vertical offset in points, when not aligned.</summary>
    public double VerticalOffset { get; init; }

    /// <summary>How text flows around it.</summary>
    public WrapMode Wrap { get; init; } = WrapMode.Square;

    /// <summary>Which sides text flows on.</summary>
    public WrapSide WrapSide { get; init; } = WrapSide.Both;

    /// <summary>The outline text wraps to, for tight and through wrapping: points in the object's own space, 0 to 21600 as Word stores them.</summary>
    public IReadOnlyList<(double X, double Y)>? WrapPolygon { get; init; }

    /// <summary>The space text keeps above, in points.</summary>
    public double DistanceTop { get; init; }

    /// <summary>The space text keeps below, in points.</summary>
    public double DistanceBottom { get; init; }

    /// <summary>The space text keeps on the left, in points.</summary>
    public double DistanceLeft { get; init; } = 9;

    /// <summary>The space text keeps on the right, in points.</summary>
    public double DistanceRight { get; init; } = 9;

    /// <summary>Its order in the stack of floating objects; higher is in front.</summary>
    public long ZOrder { get; init; }

    /// <summary>It may overlap other floating objects.</summary>
    public bool AllowOverlap { get; init; } = true;

    /// <summary>When anchored in a table cell, it is laid out inside the cell.</summary>
    public bool LayoutInCell { get; init; } = true;

    /// <summary>It stays where its offsets say even when that is off the page (Word's locked anchor is kept as is).</summary>
    public bool Locked { get; init; }
}

/// <summary>A picture in the text: an image of given size, in the line or floating.</summary>
public sealed class Picture : Inline
{
    private ImageData image;
    private double width;
    private double height;
    private string? title;
    private string? description;
    private FloatingPlacement? floating;
    private string? sourceUrl;
    private (double Left, double Top, double Right, double Bottom) crop;
    private ImageData? fallback;

    /// <summary>A picture of <paramref name="image"/> shown at <paramref name="width"/> by <paramref name="height"/> points, not yet in a paragraph.</summary>
    public Picture(Document document, ImageData image, double width, double height)
        : base(document)
    {
        this.image = image ?? throw new ArgumentNullException(nameof(image));
        this.width = CheckSize(width);
        this.height = CheckSize(height);
    }

    /// <summary>The image as stored.</summary>
    public ImageData Image
    {
        get => image;
        set => SetProperty(ref image, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>The width shown, in points.</summary>
    public double Width
    {
        get => width;
        set => SetProperty(ref width, CheckSize(value));
    }

    /// <summary>The height shown, in points.</summary>
    public double Height
    {
        get => height;
        set => SetProperty(ref height, CheckSize(value));
    }

    /// <summary>The picture's title (alternative text title), or null.</summary>
    public string? Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    /// <summary>The alternative text, or null.</summary>
    public string? Description
    {
        get => description;
        set => SetProperty(ref description, value);
    }

    /// <summary>Where the picture floats, or null for a picture in the line.</summary>
    public FloatingPlacement? Floating
    {
        get => floating;
        set => SetProperty(ref floating, value);
    }

    /// <summary>
    /// The address of a linked image (one the file names by URL instead of holding), or null; Egap keeps it and never fetches
    /// it. A linked picture may have empty <see cref="Image"/> bytes and a size of 0, meaning its own size.
    /// </summary>
    public string? SourceUrl
    {
        get => sourceUrl;
        set => SetProperty(ref sourceUrl, value);
    }

    /// <summary>How much of the image is cut off on each side, as fractions of its size (negative adds space).</summary>
    public (double Left, double Top, double Right, double Bottom) Crop
    {
        get => crop;
        set => SetProperty(ref crop, value);
    }

    /// <summary>
    /// A raster stand-in for <see cref="Image"/> where it cannot be shown, or null: Word keeps a PNG beside an SVG picture for
    /// readers without SVG, and DOCX writes this one there.
    /// </summary>
    public ImageData? Fallback
    {
        get => fallback;
        set => SetProperty(ref fallback, value);
    }

    /// <inheritdoc/>
    public override Picture Clone(bool deep = true)
    {
        return (Picture)base.Clone(deep);
    }

    internal static double CheckSize(double value)
    {
        return double.IsFinite(value) && value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A size must be finite and not negative.");
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitPicture(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new Picture(context.Target, image, width, height) { title = title, description = description, floating = floating, crop = crop, sourceUrl = sourceUrl, fallback = fallback });
    }
}

/// <summary>The shape a <see cref="Shape"/> draws (Word's preset geometry), from the common ones.</summary>
public enum ShapeKind
{
    /// <summary>A rectangle.</summary>
    Rectangle,

    /// <summary>A rectangle with rounded corners.</summary>
    RoundedRectangle,

    /// <summary>An ellipse.</summary>
    Ellipse,

    /// <summary>A straight line from the top left to the bottom right of its box.</summary>
    Line,

    /// <summary>A triangle.</summary>
    Triangle,

    /// <summary>A right-pointing arrow.</summary>
    RightArrow,

    /// <summary>Another preset or a custom outline, kept by the reader as extension data and drawn as its box.</summary>
    Other,
}

/// <summary>The text inside a text box.</summary>
public sealed class TextBoxStory : Story
{
    internal TextBoxStory(Document document)
        : base(document)
    {
    }

    /// <summary>The shape this text belongs to, or null.</summary>
    public Shape? Shape => Parent as Shape;

    /// <inheritdoc/>
    public override TextBoxStory Clone(bool deep = true)
    {
        return (TextBoxStory)base.Clone(deep);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new TextBoxStory(context.Target);
    }
}

/// <summary>
/// A shape in the text, in the line or floating, with a fill and an outline, and optionally text inside it (a text box,
/// Phase 6 design Q11).
/// </summary>
public sealed class Shape : Inline
{
    private ShapeKind kind;
    private double width;
    private double height;
    private DocumentColor? fill;
    private Border? outline;
    private FloatingPlacement? floating;
    private double rotation;
    private string? title;
    private string? description;

    /// <summary>A shape of <paramref name="kind"/> and size in points, not yet in a paragraph.</summary>
    public Shape(Document document, ShapeKind kind, double width, double height)
        : base(document)
    {
        this.kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        this.width = Picture.CheckSize(width);
        this.height = Picture.CheckSize(height);
    }

    /// <summary>What the shape draws.</summary>
    public ShapeKind Kind
    {
        get => kind;
        set => SetProperty(ref kind, Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The width in points.</summary>
    public double Width
    {
        get => width;
        set => SetProperty(ref width, Picture.CheckSize(value));
    }

    /// <summary>The height in points.</summary>
    public double Height
    {
        get => height;
        set => SetProperty(ref height, Picture.CheckSize(value));
    }

    /// <summary>The fill colour, or null for no fill.</summary>
    public DocumentColor? Fill
    {
        get => fill;
        set => SetProperty(ref fill, value);
    }

    /// <summary>The outline, or null for none.</summary>
    public Border? Outline
    {
        get => outline;
        set => SetProperty(ref outline, value);
    }

    /// <summary>Where the shape floats, or null for a shape in the line.</summary>
    public FloatingPlacement? Floating
    {
        get => floating;
        set => SetProperty(ref floating, value);
    }

    /// <summary>The rotation in degrees, clockwise.</summary>
    public double Rotation
    {
        get => rotation;
        set => SetProperty(ref rotation, double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The shape's title (alternative text title), or null.</summary>
    public string? Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    /// <summary>The alternative text, or null.</summary>
    public string? Description
    {
        get => description;
        set => SetProperty(ref description, value);
    }

    /// <summary>The text inside, for a text box; null for a shape without text.</summary>
    public TextBoxStory? TextBox { get; private set; }

    internal override int ChildCount => TextBox is null ? 0 : 1;

    /// <summary>Makes the shape a text box: gives it an empty story for its text, or returns the one it has.</summary>
    public TextBoxStory AddTextBox()
    {
        if (TextBox is null)
        {
            TextBox = new TextBoxStory(Document);
            TextBox.Attach(this, null, 0);
            Document.NotifyChanged(TextBox, DocumentChangeKind.NodeInserted, this, 0);
        }

        return TextBox;
    }

    /// <inheritdoc/>
    public override Shape Clone(bool deep = true)
    {
        return (Shape)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return index == 0 && TextBox is not null ? TextBox : throw new ArgumentOutOfRangeException(nameof(index));
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitShapeStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitShapeEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new Shape(context.Target, kind, width, height)
        {
            fill = fill,
            outline = outline,
            floating = floating,
            rotation = rotation,
            title = title,
            description = description,
        });
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        if (TextBox is not null)
        {
            Shape shape = (Shape)copy;
            shape.TextBox = new TextBoxStory(context.Target);
            shape.TextBox.Attach(shape, null, 0);
            pending.Push((TextBox, shape.TextBox));
        }
    }
}
