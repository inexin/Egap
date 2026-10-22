namespace Egap.Documents;

/// <summary>What a grid track size is given in.</summary>
public enum GridTrackUnit
{
    /// <summary>Its content's size: between its narrowest and widest (CSS auto).</summary>
    Auto,

    /// <summary>Points.</summary>
    Points,

    /// <summary>Per cent of the container's content width.</summary>
    Percent,

    /// <summary>A share of the space left over (CSS fr).</summary>
    Fraction,

    /// <summary>Its content's narrowest size, wrapped wherever it can be (CSS min-content).</summary>
    MinContent,

    /// <summary>Its content's widest size, not wrapped (CSS max-content).</summary>
    MaxContent,
}

/// <summary>One limit of a grid track's size: a length, a share of the space, or a size from the content.</summary>
/// <param name="Unit">How <paramref name="Value"/> is read.</param>
/// <param name="Value">Points, per cent or fraction; 0 for the content sizes.</param>
public readonly record struct GridTrackSize(GridTrackUnit Unit, double Value)
{
    /// <summary>The content's size (CSS auto).</summary>
    public static GridTrackSize Auto => default;

    /// <summary>The content's narrowest size.</summary>
    public static GridTrackSize MinContent => new(GridTrackUnit.MinContent, 0);

    /// <summary>The content's widest size.</summary>
    public static GridTrackSize MaxContent => new(GridTrackUnit.MaxContent, 0);

    /// <summary>A size in points.</summary>
    public static GridTrackSize Points(double points) => new(GridTrackUnit.Points, points);

    /// <summary>A size in per cent of the container's content width.</summary>
    public static GridTrackSize Percent(double percent) => new(GridTrackUnit.Percent, percent);

    /// <summary>A share of the space left over.</summary>
    public static GridTrackSize Fraction(double fraction) => new(GridTrackUnit.Fraction, fraction);
}

/// <summary>
/// A grid column or row: its least and greatest size (CSS minmax). A single size is both, except a fraction, which grows from
/// its content's size as CSS's 1fr does.
/// </summary>
/// <param name="Min">The least size.</param>
/// <param name="Max">The greatest size, or a fraction to share the space left over.</param>
public readonly record struct GridTrack(GridTrackSize Min, GridTrackSize Max)
{
    /// <summary>A track sized to its content.</summary>
    public static GridTrack Auto => default;

    /// <summary>A track of a size in points.</summary>
    public static GridTrack Points(double points) => Of(GridTrackSize.Points(points));

    /// <summary>A track of a size in per cent of the container's content width.</summary>
    public static GridTrack Percent(double percent) => Of(GridTrackSize.Percent(percent));

    /// <summary>A track that takes a share of the space left over, and never less than its content's narrowest size.</summary>
    public static GridTrack Fraction(double fraction) => new(GridTrackSize.Auto, GridTrackSize.Fraction(fraction));

    /// <summary>A track between two sizes.</summary>
    public static GridTrack MinMax(GridTrackSize min, GridTrackSize max) => new(min, max);

    /// <summary>A track of one size; a fraction grows from its content's size.</summary>
    public static GridTrack Of(GridTrackSize size)
    {
        return size.Unit == GridTrackUnit.Fraction ? new(GridTrackSize.Auto, size) : new(size, size);
    }
}

/// <summary>Which way a grid's automatic placement fills cells, and whether it goes back to fill holes.</summary>
public enum GridAutoFlow
{
    /// <summary>Along each row, adding rows as needed.</summary>
    Row,

    /// <summary>Down each column, adding columns as needed.</summary>
    Column,

    /// <summary>Along rows, filling earlier holes with later items that fit.</summary>
    RowDense,

    /// <summary>Down columns, filling earlier holes with later items that fit.</summary>
    ColumnDense,
}

/// <summary>
/// How a grid container lays out its items (CSS grid): the column and row tracks, the size of tracks it adds, named areas,
/// automatic placement, gaps and alignment. Immutable; set a new one to change it.
/// </summary>
public sealed record GridLayout
{
    /// <summary>The columns, left to right.</summary>
    public IReadOnlyList<GridTrack> Columns { get; init; } = [];

    /// <summary>The rows, top to bottom; rows past these get <see cref="AutoRows"/>.</summary>
    public IReadOnlyList<GridTrack> Rows { get; init; } = [];

    /// <summary>The size of columns the placement adds past <see cref="Columns"/>.</summary>
    public GridTrack AutoColumns { get; init; }

    /// <summary>The size of rows the placement adds past <see cref="Rows"/>.</summary>
    public GridTrack AutoRows { get; init; }

    /// <summary>How items without a place are placed.</summary>
    public GridAutoFlow AutoFlow { get; init; }

    /// <summary>
    /// Named areas (CSS grid-template-areas): one string per row with a name per column, such as "head head" and "side main";
    /// a dot leaves a cell unnamed. Each name must make a rectangle. Empty for none.
    /// </summary>
    public IReadOnlyList<string> Areas { get; init; } = [];

    /// <summary>The gap between rows, in points.</summary>
    public double RowGap { get; init; }

    /// <summary>The gap between columns, in points.</summary>
    public double ColumnGap { get; init; }

    /// <summary>
    /// How items line up across their cells (CSS justify-items); an item's <see cref="GridItem.JustifySelf"/> wins over it.
    /// Takes Start, End, Center and Stretch; any other value lines up as Start.
    /// </summary>
    public LayoutAlignment JustifyItems { get; init; } = LayoutAlignment.Stretch;

    /// <summary>
    /// How items line up down their cells (CSS align-items); an item's <see cref="GridItem.AlignSelf"/> wins over it.
    /// Takes Start, End, Center and Stretch; any other value lines up as Start.
    /// </summary>
    public LayoutAlignment AlignItems { get; init; } = LayoutAlignment.Stretch;

    /// <summary>
    /// How the columns share the width left over when they do not fill the container (CSS justify-content); stretch, as CSS's
    /// normal, widens the columns sized to their content. Takes Start, End, Center, Stretch, SpaceBetween, SpaceAround and
    /// SpaceEvenly; any other value lines up as Start.
    /// </summary>
    public LayoutAlignment JustifyContent { get; init; } = LayoutAlignment.Stretch;
}

/// <summary>
/// A grid container (CSS grid): items placed in cells of column and row tracks, by line, by named area or automatically, and
/// spanning several. The page flow breaks between rows; DOCX writes the container as a borderless table.
/// </summary>
public sealed class GridContainer : Block
{
    private GridLayout layout = new();
    private BoxStyle? box;

    /// <summary>An empty grid container of <paramref name="document"/>, not yet in the tree.</summary>
    public GridContainer(Document document)
        : base(document)
    {
        Items = new ChildCollection<GridItem>(this);
    }

    /// <summary>The tracks, areas and placement rules.</summary>
    public GridLayout Layout
    {
        get => layout;
        set => SetProperty(ref layout, Check(value ?? throw new ArgumentNullException(nameof(value))));
    }

    /// <summary>The box drawn around the grid, or null for none.</summary>
    public BoxStyle? Box
    {
        get => box;
        set => SetProperty(ref box, value);
    }

    /// <summary>The items in document order, the order automatic placement takes them in.</summary>
    public NodeCollection<GridItem> Items { get; }

    internal override int ChildCount => Items.Count;

    /// <summary>Adds an item at the end, holding a paragraph of <paramref name="text"/> when it is given.</summary>
    public GridItem AddItem(string? text = null)
    {
        GridItem item = Items.AddTrusted(new GridItem(Document));
        if (!string.IsNullOrEmpty(text))
        {
            item.AddParagraph(text);
        }

        return item;
    }

    /// <inheritdoc/>
    public override GridContainer Clone(bool deep = true)
    {
        return (GridContainer)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return Items[index];
    }

    private static GridLayout Check(GridLayout value)
    {
        ArgumentNullException.ThrowIfNull(value.Columns, nameof(value));
        ArgumentNullException.ThrowIfNull(value.Rows, nameof(value));
        ArgumentNullException.ThrowIfNull(value.Areas, nameof(value));
        foreach (GridTrack track in value.Columns.Concat(value.Rows).Append(value.AutoColumns).Append(value.AutoRows))
        {
            if (!Valid(track.Min) || !Valid(track.Max) || track.Min.Unit == GridTrackUnit.Fraction)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "A track size must be finite and not negative, and a fraction can only be a track's greatest size.");
            }
        }

        if (!Enum.IsDefined(value.AutoFlow) || !Enum.IsDefined(value.JustifyItems) || !Enum.IsDefined(value.AlignItems) || !Enum.IsDefined(value.JustifyContent))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A grid layout setting is not one of its enum's values.");
        }

        if (!(value.RowGap >= 0 && double.IsFinite(value.RowGap)) || !(value.ColumnGap >= 0 && double.IsFinite(value.ColumnGap)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Gaps must be finite and not negative.");
        }

        return value with { Columns = [.. value.Columns], Rows = [.. value.Rows], Areas = [.. value.Areas] };

        static bool Valid(GridTrackSize size) => Enum.IsDefined(size.Unit) && size.Value >= 0 && double.IsFinite(size.Value);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitGridContainerStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitGridContainerEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new GridContainer(context.Target) { layout = layout, box = box };
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        NodeCollection<GridItem> items = ((GridContainer)copy).Items;
        foreach (GridItem item in Items)
        {
            items.AddTrusted(CloneChild(item, context, pending));
        }
    }
}

/// <summary>
/// An item of a <see cref="GridContainer"/>: blocks in a box placed in one cell or spanning several. A line number counts
/// from 1 at the grid's start, and from -1 at the end of the explicit grid; null leaves the item to automatic placement.
/// </summary>
public sealed class GridItem : BlockContainer
{
    private int? column;
    private int columnSpan = 1;
    private int? row;
    private int rowSpan = 1;
    private string? area;
    private LayoutAlignment? justifySelf;
    private LayoutAlignment? alignSelf;
    private BoxStyle? box;

    /// <summary>An empty item of <paramref name="document"/>, not yet in a container.</summary>
    public GridItem(Document document)
        : base(document)
    {
    }

    /// <summary>The column line the item starts at, or null to place it automatically.</summary>
    public int? Column
    {
        get => column;
        set => SetProperty(ref column, value != 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Grid lines count from 1, or from -1 at the end."));
    }

    /// <summary>How many columns the item spans.</summary>
    public int ColumnSpan
    {
        get => columnSpan;
        set => SetProperty(ref columnSpan, value is >= 1 and <= 1000 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The row line the item starts at, or null to place it automatically.</summary>
    public int? Row
    {
        get => row;
        set => SetProperty(ref row, value != 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Grid lines count from 1, or from -1 at the end."));
    }

    /// <summary>How many rows the item spans.</summary>
    public int RowSpan
    {
        get => rowSpan;
        set => SetProperty(ref rowSpan, value is >= 1 and <= 1000 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The named area of <see cref="GridLayout.Areas"/> the item fills, or null; an area wins over lines and spans.</summary>
    public string? Area
    {
        get => area;
        set => SetProperty(ref area, value);
    }

    /// <summary>How the item lines up across its cells, or null for the container's <see cref="GridLayout.JustifyItems"/>. Takes Start, End, Center and Stretch; any other value lines up as Start.</summary>
    public LayoutAlignment? JustifySelf
    {
        get => justifySelf;
        set => SetProperty(ref justifySelf, value is null || Enum.IsDefined(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>How the item lines up down its cells, or null for the container's <see cref="GridLayout.AlignItems"/>. Takes Start, End, Center and Stretch; any other value lines up as Start.</summary>
    public LayoutAlignment? AlignSelf
    {
        get => alignSelf;
        set => SetProperty(ref alignSelf, value is null || Enum.IsDefined(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The box drawn around the item's blocks, or null for none.</summary>
    public BoxStyle? Box
    {
        get => box;
        set => SetProperty(ref box, value);
    }

    /// <summary>The container this item is in, or null.</summary>
    public GridContainer? Container => Parent as GridContainer;

    /// <inheritdoc/>
    public override GridItem Clone(bool deep = true)
    {
        return (GridItem)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitGridItemStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitGridItemEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new GridItem(context.Target)
        {
            column = column,
            columnSpan = columnSpan,
            row = row,
            rowSpan = rowSpan,
            area = area,
            justifySelf = justifySelf,
            alignSelf = alignSelf,
            box = box,
        };
    }
}
