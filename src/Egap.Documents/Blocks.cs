namespace Egap.Documents;

/// <summary>A node that goes into a story or a table cell: a paragraph or a table. Content controls and kept islands join in Build Plan task 5.</summary>
public abstract class Block : Node
{
    private protected Block(Document document)
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(document);
    }
}

/// <summary>A paragraph: a list of inlines (runs of text, tabs, breaks and the rest) ended by a paragraph mark.</summary>
public sealed class Paragraph : Block
{
    /// <summary>An empty paragraph of <paramref name="document"/>, not yet in the tree.</summary>
    public Paragraph(Document document)
        : base(document)
    {
        Inlines = new ChildCollection<Inline>(this);
    }

    /// <summary>The paragraph's content in order.</summary>
    public NodeCollection<Inline> Inlines { get; }

    /// <summary>The runs of text in the paragraph, leaving out tabs, breaks and other inlines.</summary>
    public IEnumerable<Run> Runs => Inlines.OfType<Run>();

    internal override int ChildCount => Inlines.Count;

    /// <summary>Adds a run of <paramref name="text"/> at the end.</summary>
    public Run AddRun(string text)
    {
        return Inlines.AddTrusted(new Run(Document, text));
    }

    /// <summary>Adds a tab at the end.</summary>
    public Tab AddTab()
    {
        return Inlines.AddTrusted(new Tab(Document));
    }

    /// <summary>Adds a break of <paramref name="kind"/> at the end.</summary>
    public Break AddBreak(BreakKind kind = BreakKind.Line)
    {
        return Inlines.AddTrusted(new Break(Document, kind));
    }

    /// <inheritdoc/>
    public override Paragraph Clone(bool deep = true)
    {
        return (Paragraph)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return Inlines[index];
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitParagraphStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitParagraphEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new Paragraph(context.Target);
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        NodeCollection<Inline> inlines = ((Paragraph)copy).Inlines;
        foreach (Inline inline in Inlines)
        {
            inlines.AddTrusted(CloneChild(inline, context, pending));
        }
    }
}

/// <summary>
/// A table: rows of cells, each cell holding blocks, so tables nest. Rows need not have the same number of cells; merged
/// cells and widths come with the table format in Build Plan task 3.
/// </summary>
public sealed class Table : Block
{
    /// <summary>An empty table of <paramref name="document"/>, not yet in the tree.</summary>
    public Table(Document document)
        : base(document)
    {
        Rows = new ChildCollection<Row>(this);
    }

    /// <summary>The rows in order.</summary>
    public NodeCollection<Row> Rows { get; }

    internal override int ChildCount => Rows.Count;

    /// <summary>Adds a row at the end with <paramref name="cells"/> cells, each holding an empty paragraph.</summary>
    public Row AddRow(int cells = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cells);
        Row row = new(Document);
        for (int c = 0; c < cells; c++)
        {
            row.AddCell();
        }

        return Rows.AddTrusted(row);
    }

    /// <inheritdoc/>
    public override Table Clone(bool deep = true)
    {
        return (Table)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return Rows[index];
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitTableStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitTableEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new Table(context.Target);
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        NodeCollection<Row> rows = ((Table)copy).Rows;
        foreach (Row row in Rows)
        {
            rows.AddTrusted(CloneChild(row, context, pending));
        }
    }
}

/// <summary>A table row: its cells in order.</summary>
public sealed class Row : Node
{
    /// <summary>An empty row of <paramref name="document"/>, not yet in a table.</summary>
    public Row(Document document)
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Cells = new ChildCollection<Cell>(this);
    }

    /// <summary>The cells in order.</summary>
    public NodeCollection<Cell> Cells { get; }

    /// <summary>The table this row is in, or null.</summary>
    public Table? Table => Parent as Table;

    internal override int ChildCount => Cells.Count;

    /// <summary>Adds a cell at the end, holding an empty paragraph.</summary>
    public Cell AddCell()
    {
        Cell cell = new(Document);
        cell.AddParagraph();
        return Cells.AddTrusted(cell);
    }

    /// <inheritdoc/>
    public override Row Clone(bool deep = true)
    {
        return (Row)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return Cells[index];
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitRowStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitRowEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new Row(context.Target);
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        NodeCollection<Cell> cells = ((Row)copy).Cells;
        foreach (Cell cell in Cells)
        {
            cells.AddTrusted(CloneChild(cell, context, pending));
        }
    }
}

/// <summary>
/// A table cell: blocks, nested tables included. Word wants at least one paragraph in every cell; the model allows none, and
/// writers add the paragraph where the format needs it.
/// </summary>
public sealed class Cell : BlockContainer
{
    /// <summary>An empty cell of <paramref name="document"/>, not yet in a row.</summary>
    public Cell(Document document)
        : base(document)
    {
    }

    /// <summary>The row this cell is in, or null.</summary>
    public Row? Row => Parent as Row;

    /// <inheritdoc/>
    public override Cell Clone(bool deep = true)
    {
        return (Cell)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitCellStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitCellEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return new Cell(context.Target);
    }
}
