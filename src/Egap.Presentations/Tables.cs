using System.Xml.Linq;

using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// A table in a <see cref="GraphicFrame"/> (<c>a:tbl</c>): its grid of columns and rows, the cells' text and how cells are
/// merged. Cell, row and table formatting the model does not hold (borders, fills, margins) are kept as they are. A row,
/// column or cell object stays valid until a row or column is added or removed; read them again from the table then.
/// </summary>
public sealed class SlideTable
{
    /// <summary>PowerPoint's built-in table style for a new table: Medium Style 2, Accent 1.</summary>
    public const string DefaultStyleId = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}";

    private readonly List<SlideTableRow> rows = [];
    private readonly List<SlideTableColumn> columns = [];

    internal SlideTable(GraphicFrame frame, XElement element)
    {
        Frame = frame;
        Element = element;
        Read();
    }

    /// <summary>The frame the table is in.</summary>
    public GraphicFrame Frame { get; }

    /// <summary>The rows, from the top.</summary>
    public IReadOnlyList<SlideTableRow> Rows => rows;

    /// <summary>The columns of the grid (<c>a:tblGrid</c>), from the left.</summary>
    public IReadOnlyList<SlideTableColumn> Columns => columns;

    /// <summary>
    /// The id of the table style (<c>a:tableStyleId</c>), a GUID in braces naming one of PowerPoint's built-in styles or one
    /// in the presentation's table styles part; null when the table has none. Setting null removes it.
    /// </summary>
    public string? StyleId
    {
        get => (string?)Properties?.Element(PptxNames.A + "tableStyleId");
        set
        {
            if (value == StyleId)
            {
                return;
            }

            XElement properties = Properties ?? AddProperties();
            properties.Element(PptxNames.A + "tableStyleId")?.Remove();
            if (value is not null)
            {
                // CT_TableProperties: the fill and effect choices, then tableStyleId, then extLst.
                XElement made = new(PptxNames.A + "tableStyleId", value);
                if (properties.Element(PptxNames.A + "extLst") is { } extensions)
                {
                    extensions.AddBeforeSelf(made);
                }
                else
                {
                    properties.Add(made);
                }
            }

            Changed();
        }
    }

    internal XElement Element { get; }

    private XElement? Properties => Element.Element(PptxNames.A + "tblPr");

    private XElement Grid => Element.Element(PptxNames.A + "tblGrid") ?? AddGrid();

    /// <summary>The cell at <paramref name="row"/> and <paramref name="column"/>, from 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no cell there.</exception>
    public SlideTableCell Cell(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, rows.Count);
        IReadOnlyList<SlideTableCell> cells = rows[row].Cells;
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, cells.Count);
        return cells[column];
    }

    /// <summary>
    /// Adds a row at <paramref name="index"/> (at the end when it is <see cref="Rows"/>' count), as tall as the row it
    /// follows, with one empty cell per column formatted as that row's. A row added inside a vertical merge widens it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative or past the number of rows.</exception>
    public SlideTableRow InsertRow(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, rows.Count);
        SlideTableRow? model = rows.Count == 0 ? null : rows[Math.Max(0, index - 1)];
        XElement row = new(PptxNames.A + "tr", new XAttribute("h", PptxNames.Text(model?.Height ?? 370840)));
        List<XElement> cells = [];
        for (int column = 0; column < columns.Count; column++)
        {
            XElement? like = model?.Cells.ElementAtOrDefault(column)?.Element;
            cells.Add(NewCell(like?.Element(PptxNames.A + "tcPr")));
        }

        // A merge the new row falls inside grows by one row, once, and covers the new row's cells under it.
        IEnumerable<SlideTableCell> crossing = index > 0 && index < rows.Count ? rows[index].Cells.Where(c => c.IsCoveredVertically) : [];
        foreach (SlideTableCell origin in DistinctOrigins(crossing))
        {
            origin.Element.SetAttributeValue("rowSpan", PptxNames.Text(origin.RowSpan + 1));
            for (int column = origin.ColumnIndex; column < Math.Min(cells.Count, origin.ColumnIndex + origin.ColumnSpan); column++)
            {
                cells[column].SetAttributeValue("vMerge", "1");
                cells[column].SetAttributeValue("hMerge", column > origin.ColumnIndex ? "1" : null);
            }
        }

        row.Add(cells);

        if (index < rows.Count)
        {
            rows[index].Element.AddBeforeSelf(row);
        }
        else if (rows.Count > 0)
        {
            rows[^1].Element.AddAfterSelf(row);
        }
        else
        {
            Element.Add(row);
        }

        Changed();
        Read();
        return rows[index];
    }

    /// <summary>Adds a row at the end, as <see cref="InsertRow(int)"/> does.</summary>
    public SlideTableRow AddRow() => InsertRow(rows.Count);

    /// <summary>
    /// Removes the row at <paramref name="index"/>. A vertical merge it starts passes to the row below, keeping its text; one
    /// it is inside loses a row.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no row at that position.</exception>
    public void RemoveRow(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, rows.Count);
        IReadOnlyList<SlideTableCell> removed = rows[index].Cells;
        foreach (SlideTableCell origin in DistinctOrigins(removed.Where(c => c.IsCoveredVertically)))
        {
            // A merge from above loses a row, once.
            origin.Element.SetAttributeValue("rowSpan", origin.RowSpan > 2 ? PptxNames.Text(origin.RowSpan - 1) : null);
        }

        foreach (SlideTableCell origin in removed.Where(c => !c.IsCoveredVertically && !c.IsCoveredHorizontally && c.RowSpan > 1))
        {
            // A merge that starts in this row starts in the row below now, with the origin's content and the rest of its span.
            if (index + 1 >= rows.Count)
            {
                continue;
            }

            IReadOnlyList<SlideTableCell> next = rows[index + 1].Cells;
            for (int column = origin.ColumnIndex; column < Math.Min(next.Count, origin.ColumnIndex + origin.ColumnSpan); column++)
            {
                if (column == origin.ColumnIndex)
                {
                    XElement replacement = new(origin.Element);
                    replacement.SetAttributeValue("rowSpan", origin.RowSpan > 2 ? PptxNames.Text(origin.RowSpan - 1) : null);
                    next[column].Element.ReplaceWith(replacement);
                }
                else
                {
                    next[column].Element.Attribute("vMerge")?.Remove();
                }
            }
        }

        rows[index].Element.Remove();
        Changed();
        Read();
    }

    /// <summary>
    /// Adds a column at <paramref name="index"/> (at the end when it is <see cref="Columns"/>' count) <paramref name="width"/>
    /// EMUs wide, with an empty cell in each row formatted as its neighbour's. The frame widens by as much. A column added
    /// inside a horizontal merge widens it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative or past the number of columns, or the width is negative.</exception>
    public SlideTableColumn InsertColumn(int index, long width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, columns.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        XElement column = new(PptxNames.A + "gridCol", new XAttribute("w", PptxNames.Text(width)));
        if (index < columns.Count)
        {
            columns[index].Element.AddBeforeSelf(column);
        }
        else
        {
            Grid.Add(column);
        }

        // A merge the new column falls inside grows by one column, once, and covers the new column's cells beside it.
        Dictionary<int, bool> covered = [];
        IEnumerable<SlideTableCell> crossing = index > 0
            ? rows.Select(r => r.Cells.ElementAtOrDefault(index)).OfType<SlideTableCell>().Where(c => c.IsCoveredHorizontally)
            : [];
        foreach (SlideTableCell origin in DistinctOrigins(crossing))
        {
            origin.Element.SetAttributeValue("gridSpan", PptxNames.Text(origin.ColumnSpan + 1));
            for (int row = origin.RowIndex; row < Math.Min(rows.Count, origin.RowIndex + origin.RowSpan); row++)
            {
                covered[row] = row > origin.RowIndex;
            }
        }

        foreach (SlideTableRow row in rows)
        {
            IReadOnlyList<SlideTableCell> cells = row.Cells;
            SlideTableCell? like = cells.Count == 0 ? null : cells[Math.Clamp(index - 1, 0, cells.Count - 1)];
            XElement cell = NewCell(like?.Element.Element(PptxNames.A + "tcPr"));
            if (covered.TryGetValue(row.Index, out bool below))
            {
                cell.SetAttributeValue("hMerge", "1");
                cell.SetAttributeValue("vMerge", below ? "1" : null);
            }

            if (index < cells.Count)
            {
                cells[index].Element.AddBeforeSelf(cell);
            }
            else if (row.Element.Elements(PptxNames.A + "tc").LastOrDefault() is { } last)
            {
                last.AddAfterSelf(cell);
            }
            else
            {
                row.Element.AddFirst(cell);
            }
        }

        if (Frame.Transform is { } frame)
        {
            Frame.Transform = frame with { Width = frame.Width + width };
        }

        Changed();
        Read();
        return columns[index];
    }

    /// <summary>Adds a column at the right, as <see cref="InsertColumn(int, long)"/> does.</summary>
    public SlideTableColumn AddColumn(long width) => InsertColumn(columns.Count, width);

    /// <summary>
    /// Removes the column at <paramref name="index"/>, and its width from the frame's. A horizontal merge it starts passes
    /// to the column at its right, keeping its text; one it is inside loses a column.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no column at that position.</exception>
    public void RemoveColumn(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, columns.Count);
        long width = columns[index].Width;
        List<SlideTableCell> removed = [.. rows.Select(r => r.Cells.ElementAtOrDefault(index)).OfType<SlideTableCell>()];
        foreach (SlideTableCell origin in DistinctOrigins(removed.Where(c => c.IsCoveredHorizontally)))
        {
            // A merge from the left loses a column, once.
            origin.Element.SetAttributeValue("gridSpan", origin.ColumnSpan > 2 ? PptxNames.Text(origin.ColumnSpan - 1) : null);
        }

        foreach (SlideTableCell origin in removed.Where(c => !c.IsCoveredVertically && !c.IsCoveredHorizontally && c.ColumnSpan > 1))
        {
            // A merge that starts in this column starts in the column at its right now, with the origin's content.
            for (int row = origin.RowIndex; row < Math.Min(rows.Count, origin.RowIndex + origin.RowSpan); row++)
            {
                if (rows[row].Cells.ElementAtOrDefault(index + 1) is not { } next)
                {
                    continue;
                }

                if (row == origin.RowIndex)
                {
                    XElement replacement = new(origin.Element);
                    replacement.SetAttributeValue("gridSpan", origin.ColumnSpan > 2 ? PptxNames.Text(origin.ColumnSpan - 1) : null);
                    next.Element.ReplaceWith(replacement);
                }
                else
                {
                    next.Element.Attribute("hMerge")?.Remove();
                }
            }
        }

        removed.ForEach(c => c.Element.Remove());

        columns[index].Element.Remove();
        if (Frame.Transform is { } frame)
        {
            Frame.Transform = frame with { Width = Math.Max(0, frame.Width - width) };
        }

        Changed();
        Read();
    }

    /// <summary>
    /// Merges the <paramref name="rowSpan"/> by <paramref name="columnSpan"/> cells from <paramref name="row"/> and
    /// <paramref name="column"/> into one, which shows the top left cell's text; the others keep theirs, hidden, as
    /// PowerPoint does. Cells already merged in the block are split first.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The block does not fit in the table, or a span is below 1.</exception>
    public SlideTableCell Merge(int row, int column, int rowSpan, int columnSpan)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rowSpan, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnSpan, 1);
        _ = Cell(row, column);
        _ = Cell(row + rowSpan - 1, column + columnSpan - 1);
        for (int r = row; r < row + rowSpan; r++)
        {
            for (int c = column; c < column + columnSpan; c++)
            {
                XElement cell = rows[r].Cells[c].Element;
                cell.SetAttributeValue("rowSpan", r == row && c == column && rowSpan > 1 ? PptxNames.Text(rowSpan) : null);
                cell.SetAttributeValue("gridSpan", r == row && c == column && columnSpan > 1 ? PptxNames.Text(columnSpan) : null);
                cell.SetAttributeValue("hMerge", c > column ? "1" : null);
                cell.SetAttributeValue("vMerge", r > row ? "1" : null);
            }
        }

        Changed();
        return rows[row].Cells[column];
    }

    /// <summary>Splits a merged cell back into the cells it covers, each showing its own text again.</summary>
    /// <exception cref="ArgumentNullException">The cell is null.</exception>
    public void Split(SlideTableCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        SlideTableCell origin = cell.Origin;
        int rowSpan = origin.RowSpan, columnSpan = origin.ColumnSpan;
        for (int r = origin.RowIndex; r < Math.Min(rows.Count, origin.RowIndex + rowSpan); r++)
        {
            for (int c = origin.ColumnIndex; c < Math.Min(rows[r].Cells.Count, origin.ColumnIndex + columnSpan); c++)
            {
                XElement element = rows[r].Cells[c].Element;
                foreach (string name in (string[])["rowSpan", "gridSpan", "hMerge", "vMerge"])
                {
                    element.Attribute(name)?.Remove();
                }
            }
        }

        Changed();
    }

    /// <summary>A table's XML for a new frame: <paramref name="rows"/> by <paramref name="columns"/> empty cells.</summary>
    internal static XElement NewElement(int rows, int columns, long width, long height)
    {
        long columnWidth = width / columns, rowHeight = height / rows;
        return new XElement(PptxNames.A + "tbl",
            new XElement(PptxNames.A + "tblPr", new XAttribute("firstRow", "1"), new XAttribute("bandRow", "1"), new XElement(PptxNames.A + "tableStyleId", DefaultStyleId)),
            new XElement(PptxNames.A + "tblGrid", Enumerable.Range(0, columns).Select(_ => new XElement(PptxNames.A + "gridCol", new XAttribute("w", PptxNames.Text(columnWidth))))),
            Enumerable.Range(0, rows).Select(_ => new XElement(PptxNames.A + "tr", new XAttribute("h", PptxNames.Text(rowHeight)),
                Enumerable.Range(0, columns).Select(_ => NewCell(null)))));
    }

    internal void Changed() => Frame.Changed();

    /// <summary>The cells whose merges cover <paramref name="cells"/>, each once.</summary>
    private static List<SlideTableCell> DistinctOrigins(IEnumerable<SlideTableCell> cells)
    {
        HashSet<XElement> seen = [];
        return [.. cells.Select(c => c.Origin).Where(o => seen.Add(o.Element))];
    }

    private static XElement NewCell(XElement? properties) => new(PptxNames.A + "tc",
        TextBody.NewElement("", PptxNames.A + "txBody"),
        properties is null ? new XElement(PptxNames.A + "tcPr") : new XElement(properties));

    private XElement AddProperties()
    {
        XElement made = new(PptxNames.A + "tblPr");
        Element.AddFirst(made);
        return made;
    }

    private XElement AddGrid()
    {
        XElement made = new(PptxNames.A + "tblGrid");
        if (Properties is { } properties)
        {
            properties.AddAfterSelf(made);
        }
        else
        {
            Element.AddFirst(made);
        }

        return made;
    }

    private void Read()
    {
        columns.Clear();
        rows.Clear();
        foreach (XElement column in Element.Element(PptxNames.A + "tblGrid")?.Elements(PptxNames.A + "gridCol") ?? [])
        {
            columns.Add(new SlideTableColumn(this, column, columns.Count));
        }

        foreach (XElement row in Element.Elements(PptxNames.A + "tr"))
        {
            rows.Add(new SlideTableRow(this, row, rows.Count));
        }
    }
}

/// <summary>A row of a table (<c>a:tr</c>).</summary>
public sealed class SlideTableRow
{
    private readonly List<SlideTableCell> cells = [];

    internal SlideTableRow(SlideTable table, XElement element, int index)
    {
        Table = table;
        Element = element;
        Index = index;
        foreach (XElement cell in element.Elements(PptxNames.A + "tc"))
        {
            cells.Add(new SlideTableCell(this, cell, cells.Count));
        }
    }

    /// <summary>The table the row is in.</summary>
    public SlideTable Table { get; }

    /// <summary>The row's position, from 0 at the top.</summary>
    public int Index { get; }

    /// <summary>The row's cells, one per grid column, merged ones included.</summary>
    public IReadOnlyList<SlideTableCell> Cells => cells;

    /// <summary>The row's height in EMUs (<c>a:tr/@h</c>): its least height, as PowerPoint makes a row taller to fit its text.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The height set is negative.</exception>
    public long Height
    {
        get => PptxNames.Long(Element, "h") ?? 0;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value != Height)
            {
                Element.SetAttributeValue("h", PptxNames.Text(value));
                Table.Changed();
            }
        }
    }

    internal XElement Element { get; }
}

/// <summary>A column of a table's grid (<c>a:gridCol</c>).</summary>
public sealed class SlideTableColumn
{
    internal SlideTableColumn(SlideTable table, XElement element, int index)
    {
        Table = table;
        Element = element;
        Index = index;
    }

    /// <summary>The table the column is in.</summary>
    public SlideTable Table { get; }

    /// <summary>The column's position, from 0 at the left.</summary>
    public int Index { get; }

    /// <summary>The column's width in EMUs (<c>a:gridCol/@w</c>). Setting it leaves the frame's width as it is.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width set is negative.</exception>
    public long Width
    {
        get => PptxNames.Long(Element, "w") ?? 0;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value != Width)
            {
                Element.SetAttributeValue("w", PptxNames.Text(value));
                Table.Changed();
            }
        }
    }

    internal XElement Element { get; }
}

/// <summary>
/// A cell of a table (<c>a:tc</c>): its text, and how it is merged. A merged block shows the text of its top left cell
/// (<see cref="Origin"/>); the cells it covers keep their own, hidden.
/// </summary>
public sealed class SlideTableCell
{
    private TextBody? text;

    internal SlideTableCell(SlideTableRow row, XElement element, int index)
    {
        Row = row;
        Element = element;
        ColumnIndex = index;
    }

    /// <summary>The row the cell is in.</summary>
    public SlideTableRow Row { get; }

    /// <summary>The cell's row, from 0 at the top.</summary>
    public int RowIndex => Row.Index;

    /// <summary>The cell's grid column, from 0 at the left.</summary>
    public int ColumnIndex { get; }

    /// <summary>The number of rows the cell spans (<c>rowSpan</c>): 1 unless it starts a vertical merge.</summary>
    public int RowSpan => (int)Math.Max(1, PptxNames.Long(Element, "rowSpan") ?? 1);

    /// <summary>The number of grid columns the cell spans (<c>gridSpan</c>): 1 unless it starts a horizontal merge.</summary>
    public int ColumnSpan => (int)Math.Max(1, PptxNames.Long(Element, "gridSpan") ?? 1);

    /// <summary>Whether a merge from a cell to the left covers this one (<c>hMerge</c>).</summary>
    public bool IsCoveredHorizontally => PptxNames.Bool(Element, "hMerge");

    /// <summary>Whether a merge from a cell above covers this one (<c>vMerge</c>).</summary>
    public bool IsCoveredVertically => PptxNames.Bool(Element, "vMerge");

    /// <summary>The cell whose text the merged block shows: this one unless it is covered.</summary>
    public SlideTableCell Origin
    {
        get
        {
            SlideTableCell cell = this;
            while (cell.IsCoveredVertically && cell.RowIndex > 0 && cell.Row.Table.Rows[cell.RowIndex - 1].Cells.ElementAtOrDefault(cell.ColumnIndex) is { } above)
            {
                cell = above;
            }

            while (cell.IsCoveredHorizontally && cell.ColumnIndex > 0)
            {
                cell = cell.Row.Cells[cell.ColumnIndex - 1];
            }

            return cell;
        }
    }

    /// <summary>The cell's text (<c>a:txBody</c>), made when the cell has none.</summary>
    public TextBody TextBody
    {
        get
        {
            XElement? body = Element.Element(PptxNames.A + "txBody");
            if (body is null)
            {
                body = TextBody.NewElement("", PptxNames.A + "txBody");
                Element.AddFirst(body);
            }

            return text is not null && text.Element == body ? text : text = new TextBody(Row.Table.Frame, body);
        }
    }

    /// <summary>The cell's text, as <see cref="Presentations.TextBody.Text"/> gives and takes it.</summary>
    /// <exception cref="ArgumentNullException">The text set is null.</exception>
    public string Text
    {
        get => Element.Element(PptxNames.A + "txBody") is null ? "" : TextBody.Text;
        set => TextBody.Text = value;
    }

    internal XElement Element { get; }
}
