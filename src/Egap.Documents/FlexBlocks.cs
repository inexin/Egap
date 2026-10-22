namespace Egap.Documents;

/// <summary>Which way a flex container lays its items out.</summary>
public enum FlexDirection
{
    /// <summary>Left to right, in lines that stack down the page.</summary>
    Row,

    /// <summary>Right to left, in lines that stack down the page.</summary>
    RowReverse,

    /// <summary>Top to bottom, one item under the other.</summary>
    Column,

    /// <summary>Bottom to top: the last item first.</summary>
    ColumnReverse,
}

/// <summary>Whether a flex container's items go on more lines when they do not fit on one.</summary>
public enum FlexWrap
{
    /// <summary>One line; items shrink to fit it.</summary>
    NoWrap,

    /// <summary>As many lines as the items need, each under the one before.</summary>
    Wrap,

    /// <summary>As many lines as the items need, in reverse order.</summary>
    WrapReverse,
}

/// <summary>
/// How a flex container lays out its items (CSS flexbox): direction, wrapping, how the space left on a line is shared and
/// how items line up across it, and the gaps between items and lines. Immutable; set a new one to change it.
/// </summary>
public sealed record FlexLayout
{
    /// <summary>Which way the items go.</summary>
    public FlexDirection Direction { get; init; }

    /// <summary>Whether items go on more lines when they do not fit on one.</summary>
    public FlexWrap Wrap { get; init; }

    /// <summary>How the space left on a line is shared out along it (CSS justify-content). Takes Start, End, Center, SpaceBetween, SpaceAround and SpaceEvenly; any other value lines up as Start.</summary>
    public LayoutAlignment JustifyContent { get; init; } = LayoutAlignment.Start;

    /// <summary>
    /// How items line up across a line (CSS align-items); an item's <see cref="FlexItem.AlignSelf"/> wins over it. Takes Start, End, Center and Stretch; any other value lines up as Start.
    /// </summary>
    public LayoutAlignment AlignItems { get; init; } = LayoutAlignment.Stretch;

    /// <summary>
    /// How the lines share the container's height when it is taller than they are (CSS align-content). Takes Start, End,
    /// Center, Stretch, SpaceBetween, SpaceAround and SpaceEvenly; any other value lines up as Start.
    /// </summary>
    public LayoutAlignment AlignContent { get; init; } = LayoutAlignment.Stretch;

    /// <summary>The gap between lines in a row container, or between items in a column container, in points.</summary>
    public double RowGap { get; init; }

    /// <summary>The gap between items on a line in a row container, in points.</summary>
    public double ColumnGap { get; init; }

    /// <summary>Whether the items run across the page rather than down it.</summary>
    internal bool IsRow => Direction is FlexDirection.Row or FlexDirection.RowReverse;
}

/// <summary>
/// A flex container (CSS flexbox): items laid out in a row or a column, growing and shrinking to share the space, wrapped onto
/// more lines when they ask for it. The page flow breaks between lines; DOCX writes the container as a borderless table.
/// </summary>
public sealed class FlexContainer : Block
{
    private FlexLayout layout = new();
    private BoxStyle? box;

    /// <summary>An empty flex container of <paramref name="document"/>, not yet in the tree.</summary>
    public FlexContainer(Document document)
        : base(document)
    {
        Items = new ChildCollection<FlexItem>(this);
    }

    /// <summary>How the items are laid out.</summary>
    public FlexLayout Layout
    {
        get => layout;
        set => SetProperty(ref layout, Check(value ?? throw new ArgumentNullException(nameof(value))));
    }

    /// <summary>The box drawn around the items (padding, borders, background, corners, shadow), or null for none.</summary>
    public BoxStyle? Box
    {
        get => box;
        set => SetProperty(ref box, value);
    }

    /// <summary>The items in document order; <see cref="FlexItem.Order"/> can lay them out in another.</summary>
    public NodeCollection<FlexItem> Items { get; }

    internal override int ChildCount => Items.Count;

    /// <summary>Adds an item at the end, holding a paragraph of <paramref name="text"/> when it is given.</summary>
    public FlexItem AddItem(string? text = null)
    {
        FlexItem item = Items.AddTrusted(new FlexItem(Document));
        if (!string.IsNullOrEmpty(text))
        {
            item.AddParagraph(text);
        }

        return item;
    }

    /// <inheritdoc/>
    public override FlexContainer Clone(bool deep = true)
    {
        return (FlexContainer)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return Items[index];
    }

    private static FlexLayout Check(FlexLayout value)
    {
        if (!Enum.IsDefined(value.Direction) || !Enum.IsDefined(value.Wrap) || !Enum.IsDefined(value.JustifyContent)
            || !Enum.IsDefined(value.AlignItems) || !Enum.IsDefined(value.AlignContent))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A flex layout setting is not one of its enum's values.");
        }

        if (!(value.RowGap >= 0 && double.IsFinite(value.RowGap)) || !(value.ColumnGap >= 0 && double.IsFinite(value.ColumnGap)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Gaps must be finite and not negative.");
        }

        return value;
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitFlexContainerStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitFlexContainerEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new FlexContainer(context.Target) { layout = layout, box = box };
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        NodeCollection<FlexItem> items = ((FlexContainer)copy).Items;
        foreach (FlexItem item in Items)
        {
            items.AddTrusted(CloneChild(item, context, pending));
        }
    }
}

/// <summary>An item of a <see cref="FlexContainer"/>: blocks laid out in a box that grows and shrinks with the line.</summary>
public sealed class FlexItem : BlockContainer
{
    private double grow;
    private double shrink = 1;
    private TableWidth basis;
    private LayoutAlignment? alignSelf;
    private int order;
    private BoxStyle? box;

    /// <summary>An empty item of <paramref name="document"/>, not yet in a container.</summary>
    public FlexItem(Document document)
        : base(document)
    {
    }

    /// <summary>How much of a line's free space the item takes, against the other items' (CSS flex-grow); 0 to keep its size.</summary>
    public double Grow
    {
        get => grow;
        set => SetProperty(ref grow, Factor(value));
    }

    /// <summary>How much the item gives up when a line is too full, against the other items' (CSS flex-shrink); 0 never to shrink.</summary>
    public double Shrink
    {
        get => shrink;
        set => SetProperty(ref shrink, Factor(value));
    }

    /// <summary>
    /// The item's size along the line before growing or shrinking (CSS flex-basis), borders and padding included; automatic
    /// takes the box's width, or its content's.
    /// </summary>
    public TableWidth Basis
    {
        get => basis;
        set => SetProperty(ref basis, value.Value >= 0 && double.IsFinite(value.Value) && Enum.IsDefined(value.Unit) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>How the item lines up across its line, or null for the container's <see cref="FlexLayout.AlignItems"/>. Takes Start, End, Center and Stretch; any other value lines up as Start.</summary>
    public LayoutAlignment? AlignSelf
    {
        get => alignSelf;
        set => SetProperty(ref alignSelf, value is null || Enum.IsDefined(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>Where the item goes among its container's items: lower first, document order among equals (CSS order).</summary>
    public int Order
    {
        get => order;
        set => SetProperty(ref order, value);
    }

    /// <summary>The box drawn around the item's blocks, or null for none.</summary>
    public BoxStyle? Box
    {
        get => box;
        set => SetProperty(ref box, value);
    }

    /// <summary>The container this item is in, or null.</summary>
    public FlexContainer? Container => Parent as FlexContainer;

    /// <inheritdoc/>
    public override FlexItem Clone(bool deep = true)
    {
        return (FlexItem)base.Clone(deep);
    }

    private static double Factor(double value)
    {
        return value >= 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A flex factor must be finite and not negative.");
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitFlexItemStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitFlexItemEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new FlexItem(context.Target) { grow = grow, shrink = shrink, basis = basis, alignSelf = alignSelf, order = order, box = box };
    }
}
