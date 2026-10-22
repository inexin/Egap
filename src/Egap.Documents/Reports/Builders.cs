using Egap.Pdf;

namespace Egap.Documents.Reports;

/// <summary>A paragraph of text a container made: its look set by a style chain.</summary>
public sealed class TextDescriptor
{
    private readonly ReportBuilder report;
    private readonly Paragraph paragraph;
    private readonly List<Run> runs;
    private TextStyle style;

    internal TextDescriptor(ReportBuilder report, Paragraph paragraph, List<Run> runs, TextStyle style)
    {
        this.report = report;
        this.paragraph = paragraph;
        this.runs = runs;
        this.style = style;
    }

    /// <summary>The paragraph.</summary>
    public Paragraph Paragraph => paragraph;

    /// <summary>Changes the style: <c>.Style(s =&gt; s.FontSize(20).Bold())</c>.</summary>
    public TextDescriptor Style(Func<TextStyle, TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Apply(change(style));
    }

    /// <summary>Takes a style kept in code.</summary>
    public TextDescriptor Style(TextStyle shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        return Apply(shared.Over(style));
    }

    /// <summary>Takes a named style the report defines (<see cref="ReportBuilder.Style"/>).</summary>
    public TextDescriptor Style(string name)
    {
        if (!report.Styles.TryGetValue(name, out TextStyle? named))
        {
            throw new ArgumentException($"The report defines no style named '{name}'.", nameof(name));
        }

        Apply(named.Over(style));
        CharacterStyle character = (CharacterStyle)report.Document.Styles[name]!;
        foreach (Run run in runs)
        {
            run.Style = character;
        }

        return this;
    }

    /// <summary>The size in points.</summary>
    public TextDescriptor FontSize(double size) => Apply(style.FontSize(size));

    /// <summary>The font family.</summary>
    public TextDescriptor FontFamily(string family) => Apply(style.FontFamily(family));

    /// <summary>Bold.</summary>
    public TextDescriptor Bold(bool bold = true) => Apply(style.Bold(bold));

    /// <summary>Italic.</summary>
    public TextDescriptor Italic(bool italic = true) => Apply(style.Italic(italic));

    /// <summary>Underlined.</summary>
    public TextDescriptor Underline(bool underline = true) => Apply(style.Underline(underline));

    /// <summary>The text colour.</summary>
    public TextDescriptor FontColor(DocumentColor color) => Apply(style.FontColor(color));

    /// <summary>The text colour from a hex string.</summary>
    public TextDescriptor FontColor(string hex) => Apply(style.FontColor(hex));

    /// <summary>Lines of <paramref name="multiple"/> times the font's height.</summary>
    public TextDescriptor LineHeight(double multiple) => Apply(style.LineHeightMultiple(multiple));

    /// <summary>At most <paramref name="lines"/> lines; the last ends in an ellipsis when the text is cut.</summary>
    public TextDescriptor ClampLines(int lines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lines, 1);
        paragraph.Format.MaxLines = lines;
        return this;
    }

    /// <summary>Centred.</summary>
    public TextDescriptor AlignCenter()
    {
        paragraph.Format.Alignment = ParagraphAlignment.Center;
        return this;
    }

    /// <summary>On the right.</summary>
    public TextDescriptor AlignRight()
    {
        paragraph.Format.Alignment = ParagraphAlignment.Right;
        return this;
    }

    private TextDescriptor Apply(TextStyle changed)
    {
        style = changed;
        foreach (Run run in runs)
        {
            style.ApplyTo(run.Format, TextStyle.Default);
        }

        style.ApplyTo(paragraph.Format);
        return this;
    }
}

/// <summary>The spans of one paragraph, each with its own style.</summary>
public sealed class TextBuilder
{
    private readonly ReportBuilder report;
    private readonly Paragraph paragraph;
    private readonly TextStyle style;

    internal TextBuilder(ReportBuilder report, Paragraph paragraph, TextStyle style)
    {
        this.report = report;
        this.paragraph = paragraph;
        this.style = style;
    }

    /// <summary>A span of text; page placeholders in it become fields.</summary>
    public TextDescriptor Span(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TextDescriptor(report, paragraph, Container.AddText(paragraph, text, style), style);
    }

    /// <summary>The page's number.</summary>
    public TextDescriptor CurrentPageNumber() => Span(Fields.Page);

    /// <summary>The number of pages.</summary>
    public TextDescriptor TotalPages() => Span(Fields.Pages);

    /// <summary>A line break.</summary>
    public void LineBreak() => paragraph.AddBreak(BreakKind.Line);

    /// <summary>A link to <paramref name="address"/> showing <paramref name="text"/>.</summary>
    public Hyperlink Hyperlink(string text, string address)
    {
        Hyperlink link = paragraph.AddHyperlink(text, address);
        foreach (Run run in link.Descendants<Run>())
        {
            Container.Styled(run, style.FontColor("0563C1").Underline().Over(style));
        }

        return link;
    }
}

/// <summary>Elements one under another.</summary>
public sealed class ColumnBuilder
{
    private readonly Slot slot;
    private readonly NodeCollection<Block> blocks;
    private double spacing;
    private bool first = true;

    internal ColumnBuilder(Slot slot, NodeCollection<Block> blocks)
    {
        this.slot = slot;
        this.blocks = blocks;
    }

    /// <summary>The space between elements.</summary>
    public ColumnBuilder Spacing(Length space)
    {
        spacing = space;
        return this;
    }

    /// <summary>The next element's container.</summary>
    public Container Item()
    {
        Slot child = slot.Child(blocks, slot.Inner);
        child.Gap = first ? 0 : spacing;
        first = false;
        return new Container(child);
    }
}

/// <summary>Elements side by side: each of a fixed width, its content's width, or a share of what is left.</summary>
public sealed class RowBuilder
{
    private readonly Slot slot;
    private readonly FlexContainer flex;

    internal RowBuilder(Slot slot, FlexContainer flex)
    {
        this.slot = slot;
        this.flex = flex;
    }

    /// <summary>The space between elements.</summary>
    public RowBuilder Spacing(Length space)
    {
        flex.Layout = flex.Layout with { ColumnGap = space };
        return this;
    }

    /// <summary>How the elements line up down the row: stretched (the default), at the top, middle or bottom.</summary>
    public RowBuilder AlignItems(LayoutAlignment alignment)
    {
        flex.Layout = flex.Layout with { AlignItems = alignment };
        return this;
    }

    /// <summary>Elements that do not fit go on another line.</summary>
    public RowBuilder Wrap()
    {
        flex.Layout = flex.Layout with { Wrap = FlexWrap.Wrap, RowGap = flex.Layout.ColumnGap };
        return this;
    }

    /// <summary>An element <paramref name="width"/> wide.</summary>
    public Container Fixed(Length width)
    {
        FlexItem item = Add();
        item.Basis = TableWidth.Points(width);
        item.Shrink = 0;
        return new Container(Slot.ForItem(slot.Report, item, slot.Style, width));
    }

    /// <summary>An element that takes <paramref name="weight"/> shares of the width the others leave.</summary>
    public Container Grow(double weight = 1)
    {
        FlexItem item = Add();
        item.Basis = TableWidth.Points(0);
        item.Grow = weight;
        return new Container(Slot.ForItem(slot.Report, item, slot.Style, slot.Inner));
    }

    /// <summary>An element as wide as its content.</summary>
    public Container Auto()
    {
        FlexItem item = Add();
        item.Shrink = 0;
        return new Container(Slot.ForItem(slot.Report, item, slot.Style, slot.Inner));
    }

    private FlexItem Add()
    {
        FlexItem item = new(slot.Document);
        flex.Items.Add(item);
        return item;
    }
}

/// <summary>Elements in a grid of tracks.</summary>
public sealed class GridBuilder
{
    private readonly Slot slot;
    private readonly GridContainer grid;

    internal GridBuilder(Slot slot, GridContainer grid)
    {
        this.slot = slot;
        this.grid = grid;
    }

    /// <summary>The columns.</summary>
    public GridBuilder Columns(params GridTrack[] tracks)
    {
        grid.Layout = grid.Layout with { Columns = tracks };
        return this;
    }

    /// <summary>The given rows; rows past them fit their content.</summary>
    public GridBuilder Rows(params GridTrack[] tracks)
    {
        grid.Layout = grid.Layout with { Rows = tracks };
        return this;
    }

    /// <summary>Named areas: a string per row with a name per column.</summary>
    public GridBuilder Areas(params string[] rows)
    {
        grid.Layout = grid.Layout with { Areas = rows };
        return this;
    }

    /// <summary>The space between rows and between columns.</summary>
    public GridBuilder Spacing(Length rows, Length columns)
    {
        grid.Layout = grid.Layout with { RowGap = rows, ColumnGap = columns };
        return this;
    }

    /// <summary>The next element, spanning <paramref name="columnSpan"/> columns and <paramref name="rowSpan"/> rows, placed in order.</summary>
    public Container Item(int columnSpan = 1, int rowSpan = 1)
    {
        GridItem item = Add();
        item.ColumnSpan = columnSpan;
        item.RowSpan = rowSpan;
        return new Container(Slot.ForItem(slot.Report, item, slot.Style, slot.Inner));
    }

    /// <summary>An element filling the named area.</summary>
    public Container Item(string area)
    {
        ArgumentException.ThrowIfNullOrEmpty(area);
        GridItem item = Add();
        item.Area = area;
        return new Container(Slot.ForItem(slot.Report, item, slot.Style, slot.Inner));
    }

    private GridItem Add()
    {
        GridItem item = new(slot.Document);
        grid.Items.Add(item);
        return item;
    }
}

/// <summary>The columns of a table: fixed widths and shares of what is left.</summary>
public sealed class TableColumns
{
    internal List<(double Value, bool Relative)> Definitions { get; } = [];

    /// <summary>A column <paramref name="width"/> wide.</summary>
    public TableColumns Fixed(Length width)
    {
        Definitions.Add((width, false));
        return this;
    }

    /// <summary>A column of <paramref name="weight"/> shares of the width the fixed ones leave.</summary>
    public TableColumns Relative(double weight = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);
        Definitions.Add((weight, true));
        return this;
    }
}

/// <summary>A table: columns, cells filled in order row by row, and header and footer rows that repeat on every page.</summary>
public sealed class TableBuilder
{
    private readonly Slot slot;
    private readonly Table table;
    private readonly TableColumns columns = new();
    private readonly List<(int Row, int Column, int Span)> placed = [];
    private Border? lines;
    private double padding = 3;
    private Row? row;
    private int filled;
    private RowKind kind = RowKind.Body;
    private DocumentColor? headerBackground;

    private enum RowKind
    {
        Header,
        Body,
        Footer,
    }

    internal TableBuilder(Slot slot, Table table)
    {
        this.slot = slot;
        this.table = table;
    }

    /// <summary><paramref name="count"/> columns sharing the width equally.</summary>
    public TableBuilder Columns(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        columns.Definitions.Clear();
        for (int i = 0; i < count; i++)
        {
            columns.Relative();
        }

        return this;
    }

    /// <summary>The columns: fixed widths and shares of what is left.</summary>
    public TableBuilder Columns(Action<TableColumns> define)
    {
        ArgumentNullException.ThrowIfNull(define);
        columns.Definitions.Clear();
        define(columns);
        return this;
    }

    /// <summary>Lines around and between the cells.</summary>
    public TableBuilder Lines(Length width, DocumentColor? color = null)
    {
        lines = new Border(BorderStyle.Single, width, color ?? DocumentColor.FromHex("000000"));
        return this;
    }

    /// <summary>The padding of every cell; 3 points unless set.</summary>
    public TableBuilder CellPadding(Length all)
    {
        padding = all;
        return this;
    }

    /// <summary>A background for the header cells.</summary>
    public TableBuilder HeaderBackground(DocumentColor color)
    {
        headerBackground = color;
        return this;
    }

    /// <summary>A header row of bold text, repeated on every page the table runs onto.</summary>
    public TableBuilder Header(params string[] texts)
    {
        return Header(h =>
        {
            foreach (string text in texts)
            {
                h.Cell().Text(text).Bold();
            }
        });
    }

    /// <summary>Header rows, filled by <paramref name="cells"/>, repeated on every page the table runs onto.</summary>
    public TableBuilder Header(Action<TableBuilder> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return In(RowKind.Header, cells);
    }

    /// <summary>Footer rows, filled by <paramref name="cells"/>, repeated at the foot of every page the table goes on from.</summary>
    public TableBuilder Footer(Action<TableBuilder> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return In(RowKind.Footer, cells);
    }

    /// <summary>A row of text for each item, its cells from <paramref name="cells"/>.</summary>
    public TableBuilder Rows<T>(IEnumerable<T> items, Func<T, object?[]> cells)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(cells);
        foreach (T item in items)
        {
            foreach (object? value in cells(item))
            {
                Cell().Text(Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) ?? "");
            }
        }

        return this;
    }

    /// <summary>The next cell, spanning <paramref name="columnSpan"/> columns; a row is started when the last one is full.</summary>
    public Container Cell(int columnSpan = 1)
    {
        int count = Math.Max(1, columns.Definitions.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnSpan, 1);
        columnSpan = Math.Min(columnSpan, count);
        if (row is null || filled + columnSpan > count)
        {
            CloseRow();
            row = table.AddRow();
            filled = 0;
            row.Format.IsHeader = kind == RowKind.Header ? true : null;
            row.Format.IsFooter = kind == RowKind.Footer ? true : null;
        }

        Cell cell = new(slot.Document);
        row.Cells.Add(cell);
        if (columnSpan > 1)
        {
            cell.Format.GridSpan = columnSpan;
        }

        placed.Add((table.Rows.Count - 1, filled, columnSpan));
        filled += columnSpan;
        if (kind == RowKind.Header && headerBackground is { } background)
        {
            cell.Format.Shading = Shading.Solid(background);
        }

        return new Container(Slot.ForCell(slot.Report, cell, slot.Style, Width(placed[^1].Column, columnSpan)));
    }

    private TableBuilder In(RowKind rows, Action<TableBuilder> cells)
    {
        CloseRow();
        RowKind before = kind;
        kind = rows;
        row = null;
        cells(this);
        CloseRow();
        row = null;
        kind = before;
        return this;
    }

    /// <summary>Ends the row being filled: a short row gets an empty cell for each column left, so Word sees a full row.</summary>
    private void CloseRow()
    {
        int count = Math.Max(1, columns.Definitions.Count);
        while (row is not null && filled < count)
        {
            Cell empty = new(slot.Document);
            row.Cells.Add(empty);
            filled++;
        }
    }

    private double[] Grid()
    {
        List<(double Value, bool Relative)> definitions = columns.Definitions.Count > 0 ? columns.Definitions : [(1, true)];
        double fixedWidth = definitions.Where(d => !d.Relative).Sum(d => d.Value);
        double shares = definitions.Where(d => d.Relative).Sum(d => d.Value);
        double left = Math.Max(0, slot.Inner - fixedWidth);
        return [.. definitions.Select(d => d.Relative ? left * d.Value / Math.Max(shares, 1e-9) : d.Value)];
    }

    private double Width(int column, int span) => Grid().Skip(column).Take(span).Sum();

    internal void Finish()
    {
        CloseRow();

        // The footer rows go last, where the layout looks for rows that repeat at the foot.
        foreach (Row footer in table.Rows.Where(r => r.Format.IsFooter == true).ToList())
        {
            footer.Remove();
            table.Rows.Add(footer);
        }

        double[] grid = Grid();
        table.Grid = grid;
        bool relative = columns.Definitions.Count == 0 || columns.Definitions.Any(d => d.Relative);
        table.Format.Width = relative ? TableWidth.Percent(100) : TableWidth.Points(grid.Sum());
        table.Format.Layout = TableLayoutKind.Fixed;
        table.Format.CellMarginLeft = table.Format.CellMarginRight = table.Format.CellMarginTop = table.Format.CellMarginBottom = padding;
        table.Format.Indent = 0;
        Border none = Border.None;
        table.Format.BorderTop = table.Format.BorderBottom = table.Format.BorderLeft = table.Format.BorderRight = lines ?? none;
        table.Format.BorderInsideHorizontal = table.Format.BorderInsideVertical = lines ?? none;
        foreach (Cell cell in table.Rows.SelectMany(r => r.Cells))
        {
            // A cell left empty still holds the paragraph Word wants there.
            if (cell.Blocks.Count == 0)
            {
                cell.AddParagraph();
            }
        }

        if (table.Rows.Count == 0)
        {
            table.Remove();
        }
    }
}
