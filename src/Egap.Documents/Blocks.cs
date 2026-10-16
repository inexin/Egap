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
    private ParagraphStyle? style;

    /// <summary>An empty paragraph of <paramref name="document"/>, not yet in the tree.</summary>
    public Paragraph(Document document)
        : base(document)
    {
        Inlines = new ChildCollection<Inline>(this);
        Format = Own(new ParagraphFormat());
        MarkFormat = Own(new RunFormat());
    }

    /// <summary>The paragraph's direct formatting, over its style's.</summary>
    public ParagraphFormat Format { get; }

    /// <summary>The formatting of the paragraph mark, which Word uses for an empty paragraph's height and the mark itself.</summary>
    public RunFormat MarkFormat { get; }

    /// <summary>The paragraph style, or null for the document's default paragraph style.</summary>
    public ParagraphStyle? Style
    {
        get => style;
        set => SetStyle(ref style, value);
    }

    /// <summary>
    /// The formatting the paragraph is laid out with: document defaults, then the table style, paragraph style and direct
    /// formatting (Phase 6 design Q5). Cached until the paragraph, its styles or its table change.
    /// </summary>
    public ResolvedParagraphFormat GetEffectiveFormat()
    {
        return FormatResolver.Resolve(this);
    }

    /// <summary>Puts the paragraph in <paramref name="list"/> at <paramref name="level"/> (direct formatting).</summary>
    public void SetList(List list, int level = 0)
    {
        ArgumentNullException.ThrowIfNull(list);
        Format.List = list;
        Format.ListLevel = level;
    }

    /// <summary>
    /// Takes the paragraph out of any list: clears the direct list settings and, when the paragraph's style puts it in a list,
    /// sets <see cref="List.None"/> to override it.
    /// </summary>
    public void RemoveList()
    {
        Format.List = null;
        Format.ListLevel = null;
        if (GetEffectiveFormat().List is not null)
        {
            Format.List = List.None;
        }
    }

    /// <summary>The paragraph's computed list label, such as "3.2.a.", or null when it is not in a list.</summary>
    public ListLabel? ListLabel => Numbering.GetLabel(this);

    /// <summary>The resolver's cache for this paragraph.</summary>
    internal FormatResolver.ParagraphCache? FormatCache { get; set; }

    /// <summary>
    /// The <see cref="Document.Version"/> of the last change inside this paragraph (its content or settings), or 0 when it has
    /// not changed since it was made. Layout reuses a paragraph whose version it has seen (Build Plan task 21b).
    /// </summary>
    public long Version { get; internal set; }

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

    /// <summary>Adds a run of <paramref name="text"/> at the end with a copy of <paramref name="format"/> as its direct formatting.</summary>
    public Run AddRun(string text, RunFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        Run run = new(Document, text);
        run.Format.CopyRaw(format);
        return Inlines.AddTrusted(run);
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

    private protected override string FormatPropertyPrefix(FormatBase format)
    {
        return format == MarkFormat ? "MarkFormat." : "Format.";
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        Paragraph copy = new(context.Target) { style = context.Map(style) };
        copy.Format.CopyRaw(Format);
        copy.MarkFormat.CopyRaw(MarkFormat);
        context.FixLists(copy.Format);
        return copy;
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
    private TableStyle? style;
    private double[]? grid;

    /// <summary>An empty table of <paramref name="document"/>, not yet in the tree.</summary>
    public Table(Document document)
        : base(document)
    {
        Rows = new ChildCollection<Row>(this);
        Format = Own(new TableFormat());
    }

    /// <summary>The table's direct formatting, over its style's.</summary>
    public TableFormat Format { get; }

    /// <summary>The table style, or null for the document's default table style.</summary>
    public TableStyle? Style
    {
        get => style;
        set => SetStyle(ref style, value);
    }

    /// <summary>
    /// The widths of the table's grid columns in points (Word's tblGrid), which cells span with <see cref="CellFormat.GridSpan"/>;
    /// null when not given, and the layout finds them from the cells.
    /// </summary>
    public IReadOnlyList<double>? Grid
    {
        get => grid;
        set
        {
            if (value is not null && value.Any(w => !double.IsFinite(w) || w < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Grid widths must be finite and not negative.");
            }

            grid = value is null ? null : [.. value];
            Document.NotifyChanged(this, DocumentChangeKind.PropertyChanged, this, property: nameof(Grid));
        }
    }

    /// <summary>The table's effective formatting: the default table style, its own style chain, then direct formatting.</summary>
    public TableFormat GetEffectiveFormat()
    {
        return FormatResolver.Resolve(this);
    }

    /// <summary>The <see cref="Document.Version"/> of the last change to this table's structure or formats, for format caches.</summary>
    internal long Stamp { get; set; }

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
        Table copy = new(context.Target) { style = context.Map(style), grid = grid };
        copy.Format.CopyRaw(Format);
        return copy;
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
        Format = Own(new RowFormat());
    }

    /// <summary>The row's direct formatting.</summary>
    public RowFormat Format { get; }

    /// <summary>The row's effective formatting: its table's style (with the regions that apply to it), then direct formatting.</summary>
    public RowFormat GetEffectiveFormat()
    {
        return FormatResolver.Resolve(this);
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
        Row copy = new(context.Target);
        copy.Format.CopyRaw(Format);
        return copy;
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
        Format = Own(new CellFormat());
    }

    /// <summary>The cell's direct formatting.</summary>
    public CellFormat Format { get; }

    /// <summary>
    /// The cell's effective formatting: its table's style with the regions that apply to the cell (first row, bands, corners
    /// and the rest), then direct formatting.
    /// </summary>
    public CellFormat GetEffectiveFormat()
    {
        return FormatResolver.Resolve(this);
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
        Cell copy = new(context.Target);
        copy.Format.CopyRaw(Format);
        return copy;
    }
}
