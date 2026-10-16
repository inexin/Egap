namespace Egap.Documents;

/// <summary>A node that holds blocks: a story (body, header, footer) or a table cell.</summary>
public abstract class BlockContainer : Node
{
    private protected BlockContainer(Document document)
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Blocks = new ChildCollection<Block>(this);
    }

    /// <summary>The paragraphs, tables and other blocks, in order.</summary>
    public NodeCollection<Block> Blocks { get; }

    /// <summary>The paragraphs directly in this container, leaving out those inside its tables.</summary>
    public IEnumerable<Paragraph> Paragraphs => Blocks.OfType<Paragraph>();

    /// <summary>The tables directly in this container, leaving out tables nested in their cells.</summary>
    public IEnumerable<Table> Tables => Blocks.OfType<Table>();

    internal override int ChildCount => Blocks.Count;

    /// <summary>Adds a paragraph at the end, holding <paramref name="text"/> as one run when it is given.</summary>
    public Paragraph AddParagraph(string? text = null)
    {
        Paragraph paragraph = Blocks.AddTrusted(new Paragraph(Document));
        if (!string.IsNullOrEmpty(text))
        {
            paragraph.AddRun(text);
        }

        return paragraph;
    }

    /// <summary>
    /// Adds a table at the end with <paramref name="rows"/> rows of <paramref name="columns"/> cells, each cell holding an
    /// empty paragraph.
    /// </summary>
    public Table AddTable(int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(columns);
        Table table = new(Document);
        for (int r = 0; r < rows; r++)
        {
            table.AddRow(columns);
        }

        return Blocks.AddTrusted(table);
    }

    internal override Node GetChild(int index)
    {
        return Blocks[index];
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        NodeCollection<Block> blocks = ((BlockContainer)copy).Blocks;
        foreach (Block block in Blocks)
        {
            blocks.AddTrusted(CloneChild(block, context, pending));
        }
    }
}

/// <summary>
/// Content that flows on its own: a section's body or a header or footer. Footnotes, comments and text boxes join in Build
/// Plan task 5.
/// </summary>
public abstract class Story : BlockContainer
{
    private protected Story(Document document)
        : base(document)
    {
        InitOwnerStory();
    }

    /// <summary>
    /// The <see cref="Document.Version"/> of the last change anywhere inside this story, nested tables included, or 0 when it
    /// has not changed since it was made.
    /// </summary>
    public long Version { get; internal set; }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitStoryStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitStoryEnd(this);
    }
}

/// <summary>A section's main text.</summary>
public sealed class Body : Story
{
    internal Body(Document document)
        : base(document)
    {
    }

    /// <summary>The section this body belongs to, or null for a copy not yet put back in place of another body's content.</summary>
    public Section? Section => Parent as Section;

    /// <inheritdoc/>
    public override Body Clone(bool deep = true)
    {
        return (Body)base.Clone(deep);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new Body(context.Target);
    }
}

/// <summary>A section's header or footer of one <see cref="HeaderFooterKind"/>.</summary>
public sealed class HeaderFooter : Story
{
    /// <summary>An empty header or footer of <paramref name="document"/>, not yet in a section.</summary>
    public HeaderFooter(Document document, HeaderFooterKind kind)
        : base(document)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
    }

    /// <summary>Which pages it is for; fixed once made.</summary>
    public HeaderFooterKind Kind { get; }

    /// <summary>Whether it is a header (and not a footer).</summary>
    public bool IsHeader => Kind is HeaderFooterKind.HeaderPrimary or HeaderFooterKind.HeaderFirst or HeaderFooterKind.HeaderEven;

    /// <inheritdoc/>
    public override HeaderFooter Clone(bool deep = true)
    {
        return (HeaderFooter)base.Clone(deep);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new HeaderFooter(context.Target, Kind);
    }
}
