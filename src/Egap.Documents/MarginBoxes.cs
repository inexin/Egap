namespace Egap.Documents;

/// <summary>
/// Where a <see cref="MarginBox"/> sits in the page margins: the sixteen boxes of CSS Paged Media, three along each edge
/// and one in each corner.
/// </summary>
public enum MarginBoxPosition
{
    /// <summary>The top left corner, left of the top margin and above the left margin.</summary>
    TopLeftCorner,

    /// <summary>The left part of the top margin.</summary>
    TopLeft,

    /// <summary>The middle of the top margin.</summary>
    TopCenter,

    /// <summary>The right part of the top margin.</summary>
    TopRight,

    /// <summary>The top right corner.</summary>
    TopRightCorner,

    /// <summary>The top part of the right margin.</summary>
    RightTop,

    /// <summary>The middle of the right margin.</summary>
    RightMiddle,

    /// <summary>The bottom part of the right margin.</summary>
    RightBottom,

    /// <summary>The bottom right corner.</summary>
    BottomRightCorner,

    /// <summary>The right part of the bottom margin.</summary>
    BottomRight,

    /// <summary>The middle of the bottom margin.</summary>
    BottomCenter,

    /// <summary>The left part of the bottom margin.</summary>
    BottomLeft,

    /// <summary>The bottom left corner.</summary>
    BottomLeftCorner,

    /// <summary>The bottom part of the left margin.</summary>
    LeftBottom,

    /// <summary>The middle of the left margin.</summary>
    LeftMiddle,

    /// <summary>The top part of the left margin.</summary>
    LeftTop,
}

/// <summary>
/// Content in one of the page margins, drawn on every page of its section (or only on the first): text such as a title
/// or "Page 3 of 9" with PAGE and NUMPAGES fields, as CSS's @page margin boxes hold. Headers and footers still apply;
/// margin boxes sit beside them and do not move the body.
/// </summary>
public sealed class MarginBox : Story
{
    /// <summary>An empty margin box of <paramref name="document"/> at <paramref name="position"/>, not yet in a section.</summary>
    /// <param name="document">The document the box belongs to.</param>
    /// <param name="position">Where the box sits.</param>
    /// <param name="firstPage">Whether the box is only for the section's first page.</param>
    public MarginBox(Document document, MarginBoxPosition position, bool firstPage = false)
        : base(document)
    {
        if (!Enum.IsDefined(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        Position = position;
        FirstPage = firstPage;
    }

    /// <summary>Where the box sits; fixed once made.</summary>
    public MarginBoxPosition Position { get; }

    /// <summary>
    /// Whether the box is only for the section's first page, where it takes the place of the box at the same position
    /// (an empty one leaves that place blank); fixed once made.
    /// </summary>
    public bool FirstPage { get; }

    /// <summary>The section the box belongs to, or null when it is not in one.</summary>
    public Section? Section => Parent as Section;

    /// <summary>Whether the box is along the top edge (a top corner included).</summary>
    internal bool IsTop => Position is MarginBoxPosition.TopLeftCorner or MarginBoxPosition.TopLeft or MarginBoxPosition.TopCenter or MarginBoxPosition.TopRight or MarginBoxPosition.TopRightCorner;

    /// <summary>Whether the box is along the bottom edge (a bottom corner included).</summary>
    internal bool IsBottom => Position is MarginBoxPosition.BottomLeftCorner or MarginBoxPosition.BottomLeft or MarginBoxPosition.BottomCenter or MarginBoxPosition.BottomRight or MarginBoxPosition.BottomRightCorner;

    /// <summary>The text alignment CSS gives a box at <paramref name="position"/> when it sets none.</summary>
    internal static ParagraphAlignment DefaultAlignment(MarginBoxPosition position) => position switch
    {
        MarginBoxPosition.TopLeftCorner or MarginBoxPosition.BottomLeftCorner or MarginBoxPosition.TopRight or MarginBoxPosition.BottomRight => ParagraphAlignment.Right,
        MarginBoxPosition.TopRightCorner or MarginBoxPosition.BottomRightCorner or MarginBoxPosition.TopLeft or MarginBoxPosition.BottomLeft => ParagraphAlignment.Left,
        _ => ParagraphAlignment.Center,
    };

    /// <inheritdoc/>
    public override MarginBox Clone(bool deep = true)
    {
        return (MarginBox)base.Clone(deep);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new MarginBox(context.Target, Position, FirstPage);
    }
}

/// <summary>A section's margin boxes: at most one for each position, and one more for each on the first page.</summary>
public sealed class MarginBoxCollection : NodeCollection<MarginBox>
{
    internal MarginBoxCollection(Section owner)
        : base(owner)
    {
    }

    /// <summary>The box at <paramref name="position"/> (for every page, or only the first), or null when there is none.</summary>
    /// <param name="position">Where the box sits.</param>
    /// <param name="firstPage">Whether to look for the first page's box.</param>
    public MarginBox? this[MarginBoxPosition position, bool firstPage = false]
    {
        get
        {
            foreach (MarginBox box in this)
            {
                if (box.Position == position && box.FirstPage == firstPage)
                {
                    return box;
                }
            }

            return null;
        }
    }

    /// <summary>The box at <paramref name="position"/>, added empty when the section has none.</summary>
    /// <param name="position">Where the box sits.</param>
    /// <param name="firstPage">Whether the box is only for the section's first page.</param>
    /// <returns>The box.</returns>
    public MarginBox GetOrAdd(MarginBoxPosition position, bool firstPage = false)
    {
        return this[position, firstPage] ?? AddTrusted(new MarginBox(Owner.Document, position, firstPage));
    }

    /// <summary>The box a page shows at <paramref name="position"/>: the first page's own box when it has one, else the box for every page.</summary>
    internal MarginBox? For(MarginBoxPosition position, bool firstPage)
    {
        return (firstPage ? this[position, true] : null) ?? this[position, false];
    }

    private protected override void Validate(MarginBox node)
    {
        if (this[node.Position, node.FirstPage] is not null)
        {
            throw new ArgumentException($"The section already has a {node.Position} margin box{(node.FirstPage ? " for its first page" : "")}.", nameof(node));
        }
    }
}
