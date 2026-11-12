using System.Xml.Linq;

using Egap.Spreadsheets.Pivots;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Printing;

/// <summary>
/// The formats the pivot tables of a sheet put on their cells from their styles, as Excel draws them: the header rows, the
/// row labels, subheadings, subtotals, stripes, grand totals and filters, each part as the table's style formats it. A cell's
/// own fill, font colour and borders win over the style's; the style's bold adds to the cell's.
/// </summary>
internal sealed class PivotFormats
{
    private readonly Dictionary<CellAddress, Format> cells = [];

    /// <summary>The cells of outer column items, whose text Excel draws to the right of the button that expands and collapses them.</summary>
    private readonly HashSet<CellAddress> expanders = [];

    /// <summary>The row label cells in a column with expand and collapse buttons, whose text Excel draws right of the buttons.</summary>
    private readonly HashSet<CellAddress> labels = [];

    /// <summary>
    /// The pixels Excel keeps left of an outer item's text for its expand and collapse button; in a column with buttons it
    /// steps each level of indent by as much.
    /// </summary>
    public const int ExpandButton = 13;

    private PivotFormats()
    {
    }

    private static readonly IReadOnlyDictionary<PivotStyleElement, PivotStyleFormat> NoStyle = new Dictionary<PivotStyleElement, PivotStyleFormat>();

    /// <summary>The formats and buttons of a sheet's pivot tables; null when it has none.</summary>
    public static PivotFormats? For(Worksheet sheet)
    {
        PivotFormats? formats = null;
        foreach (PivotTable table in sheet.Workbook.Pivots.Tables(sheet))
        {
            XElement root = table.Definition.Root;
            // A table with no style, or a style of the workbook's own, still has Excel's buttons and label indents.
            XElement info = root.Element(table.Definition.Ns + "pivotTableStyleInfo") ?? new XElement(table.Definition.Ns + "pivotTableStyleInfo");
            IReadOnlyDictionary<PivotStyleElement, PivotStyleFormat> style = (string?)info.Attribute("name") is { } name && PivotStyles.Get(name) is { } named ? named : NoStyle;
            formats ??= new PivotFormats();
            formats.Add(table.Definition, info, style);
        }

        return formats;
    }

    /// <summary>
    /// The pixels left of a row label's text with an indent of <paramref name="indent"/>: past the expand and collapse
    /// buttons of its column and a button's width for each level of indent; null for a cell Excel indents as any other.
    /// </summary>
    public int? LabelIndent(int row, int column, int indent) =>
        labels.Contains(new CellAddress(row, column)) ? ExpandButton * (indent + 1) : null;

    /// <summary>The pixels left of a column item's text that its expand and collapse button takes; 0 for a cell without one.</summary>
    public int Button(int row, int column) => expanders.Contains(new CellAddress(row, column)) ? ExpandButton : 0;

    /// <summary>A cell's style with its pivot table's formats under its own.</summary>
    public CellStyle Style(int row, int column, CellStyle style)
    {
        if (!cells.TryGetValue(new CellAddress(row, column), out Format format))
        {
            return style;
        }

        CellFont font = style.Font;
        if (format.Bold && !font.Bold)
        {
            font = font with { Bold = true };
        }

        if (format.FontColor is { } color && font.Color == CellFont.Default.Color)
        {
            font = font with { Color = color };
        }

        CellFill fill = format.Fill is { } background && style.Fill.Pattern == CellFillPattern.None ? CellFill.Solid(background) : style.Fill;
        CellBorders borders = style.Borders;
        if (format.HasBorders)
        {
            borders = borders with
            {
                Left = borders.Left.Style == CellBorderStyle.None && format.Left is { } left ? left : borders.Left,
                Right = borders.Right.Style == CellBorderStyle.None && format.Right is { } right ? right : borders.Right,
                Top = borders.Top.Style == CellBorderStyle.None && format.Top is { } top ? top : borders.Top,
                Bottom = borders.Bottom.Style == CellBorderStyle.None && format.Bottom is { } bottom ? bottom : borders.Bottom,
            };
        }

        return style with { Font = font, Fill = fill, Borders = borders };
    }

    private void Add(PivotDefinition definition, XElement info, IReadOnlyDictionary<PivotStyleElement, PivotStyleFormat> style)
    {
        bool Flag(string name) => XlsxNames.Bool((string?)info.Attribute(name), false);
        bool rowHeaders = Flag("showRowHeaders"), columnHeaders = Flag("showColHeaders");
        bool rowStripes = Flag("showRowStripes"), columnStripes = Flag("showColStripes"), lastColumn = Flag("showLastColumn");

        XNamespace ns = definition.Ns;
        RangeAddress table = definition.Range;
        XElement? location = definition.Location;
        int firstHeaderRow = XlsxNames.Int((string?)location?.Attribute("firstHeaderRow"), 1);
        int firstDataRow = XlsxNames.Int((string?)location?.Attribute("firstDataRow"), 1);
        int firstDataColumn = XlsxNames.Int((string?)location?.Attribute("firstDataCol"), 1);
        List<(string Type, int Repeated, int Level)> rowItems = Items(definition.Root.Element(ns + "rowItems"), ns);
        List<(string Type, int Repeated, int Level)> columnItems = Items(definition.Root.Element(ns + "colItems"), ns);
        int top = table.First.Row, left = table.First.Column, bottom = table.Last.Row, right = table.Last.Column;
        int dataTop = top + firstDataRow, dataLeft = left + firstDataColumn;
        int rowFields = definition.RowFields.Count;

        List<(PivotStyleElement Element, RangeAddress Area)> parts = [];
        void Part(PivotStyleElement element, int r1, int c1, int r2, int c2)
        {
            if (r1 <= r2 && c1 <= c2 && style.ContainsKey(element))
            {
                parts.Add((element, new RangeAddress(r1, c1, r2, c2)));
            }
        }

        Part(PivotStyleElement.WholeTable, top, left, bottom, right);

        int pages = definition.PageFields.Count;
        for (int p = 0; p < pages; p++)
        {
            int row = top - 1 - pages + p;
            if (row >= 0)
            {
                Part(PivotStyleElement.PageFieldLabels, row, left, row, left);
                Part(PivotStyleElement.PageFieldValues, row, left + 1, row, left + 1);
            }
        }

        if (columnHeaders)
        {
            Part(PivotStyleElement.HeaderRow, top, left, dataTop - 1, right);
            Part(PivotStyleElement.FirstHeaderCell, top, left, top, left);
            for (int level = 0; top + firstHeaderRow + level < dataTop - 1 && level < 3; level++)
            {
                int row = top + firstHeaderRow + level;
                Part(PivotStyleElement.FirstColumnSubheading + level, row, dataLeft, row, right);
            }
        }

        if (rowHeaders)
        {
            Part(PivotStyleElement.FirstColumn, dataTop, left, bottom, dataLeft - 1);
        }

        // In tabular form an outer field's item heads the rows of its inner items in its own column.
        List<int> fields = definition.RowFields;
        bool tabular = fields.Count > 1 && fields.All(f => f == PivotDefinition.DataField || !(definition.Fields[f].Compact || definition.Fields[f].Outline));
        for (int k = 0; k < rowItems.Count; k++)
        {
            int row = dataTop + k;
            (string type, int repeated, int level) = rowItems[k];
            if (tabular && rowHeaders && type == "data")
            {
                for (int l = repeated; l <= level && l < fields.Count - 1 && l < 3; l++)
                {
                    int end = k + 1;
                    while (end < rowItems.Count && rowItems[end].Type == "data" && rowItems[end].Repeated > l)
                    {
                        end++;
                    }

                    Part(PivotStyleElement.FirstRowSubheading + l, row, left + l, dataTop + end - 1, left + l);
                }
            }

            switch (type)
            {
                case "grand":
                    Part(PivotStyleElement.TotalRow, row, left, row, right);
                    break;
                case "blank":
                    Part(PivotStyleElement.BlankRow, row, left, row, right);
                    break;
                case "data":
                    if (rowStripes)
                    {
                        Part(k % 2 == 0 ? PivotStyleElement.FirstRowStripe : PivotStyleElement.SecondRowStripe, row, left, row, right);
                    }

                    // A line that ends at an outer field heads the items of the fields inside it.
                    if (!tabular && rowHeaders && level < rowFields - 1 && level >= 0)
                    {
                        Part(PivotStyleElement.FirstRowSubheading + Math.Min(level, 2), row, left, row, right);
                    }

                    break;
                default:
                    Part(PivotStyleElement.FirstSubtotalRow + Math.Clamp(level, 0, 2), row, left, row, right);
                    break;
            }
        }

        for (int k = 0; k < columnItems.Count; k++)
        {
            int column = dataLeft + k;
            (string type, _, int level) = columnItems[k];
            switch (type)
            {
                case "grand":
                    if (lastColumn)
                    {
                        Part(PivotStyleElement.LastColumn, top + firstHeaderRow, column, bottom, column);
                    }

                    break;
                case "data":
                case "blank":
                    if (columnStripes)
                    {
                        Part(k % 2 == 0 ? PivotStyleElement.FirstColumnStripe : PivotStyleElement.SecondColumnStripe, dataTop, column, bottom, column);
                    }

                    break;
                default:
                    Part(PivotStyleElement.FirstSubtotalColumn + Math.Clamp(level, 0, 2), top + firstHeaderRow, column, bottom, column);
                    break;
            }
        }

        // Outer items, row and column, show a button to expand and collapse them; inner fields' columns as the layout puts them.
        int[] labelColumn = new int[fields.Count];
        for (int l = 1; l < fields.Count; l++)
        {
            int previous = fields[l - 1];
            bool shared = previous == PivotDefinition.DataField
                ? definition.OutlineData && definition.CompactData
                : definition.Fields[previous].Outline && definition.Fields[previous].Compact;
            labelColumn[l] = labelColumn[l - 1] + (shared ? 0 : 1);
        }

        // A row label column with any button draws all its items, outer and inner, right of the buttons; not the totals.
        int lastRowField = fields.FindLastIndex(f => f != PivotDefinition.DataField);
        HashSet<int> buttonColumns = [];
        for (int k = 0; k < rowItems.Count; k++)
        {
            (string type, int repeated, int level) = rowItems[k];
            for (int l = repeated; type == "data" && l <= level && l < lastRowField; l++)
            {
                if (fields[l] != PivotDefinition.DataField)
                {
                    buttonColumns.Add(left + labelColumn[l]);
                }
            }
        }

        for (int k = 0; k < rowItems.Count; k++)
        {
            if (rowItems[k].Type == "data")
            {
                foreach (int column in buttonColumns)
                {
                    labels.Add(new CellAddress(dataTop + k, column));
                }
            }
        }

        List<int> columnFieldList = definition.ColumnFields;
        int lastColumnField = columnFieldList.FindLastIndex(f => f != PivotDefinition.DataField);
        for (int k = 0; k < columnItems.Count; k++)
        {
            (string type, int repeated, int level) = columnItems[k];
            for (int l = repeated; type == "data" && l <= level && l < lastColumnField; l++)
            {
                if (columnFieldList[l] != PivotDefinition.DataField)
                {
                    expanders.Add(new CellAddress(top + firstHeaderRow + l, dataLeft + k));
                }
            }
        }

        // The parts go on in the order of their elements, a later one over an earlier one.
        foreach ((PivotStyleElement element, RangeAddress area) in parts.OrderBy(p => p.Element))
        {
            PivotStyleFormat format = style[element];
            for (int row = area.First.Row; row <= area.Last.Row; row++)
            {
                for (int column = area.First.Column; column <= area.Last.Column; column++)
                {
                    CellAddress address = new(row, column);
                    cells.TryGetValue(address, out Format cell);
                    cell.Put(format, row == area.First.Row, row == area.Last.Row, column == area.First.Column, column == area.Last.Column);
                    cells[address] = cell;
                }
            }
        }
    }

    /// <summary>The kind of each line of rowItems or colItems, the fields it repeats from the line before, and the level of the field it ends at.</summary>
    private static List<(string Type, int Repeated, int Level)> Items(XElement? items, XNamespace ns)
    {
        List<(string, int, int)> list = [];
        if (items is null)
        {
            return list;
        }

        foreach (XElement i in items.Elements(ns + "i"))
        {
            int repeated = XlsxNames.Int((string?)i.Attribute("r"), 0);
            int count = i.Elements(ns + "x").Count();
            list.Add(((string?)i.Attribute("t") ?? "data", repeated, repeated + Math.Max(count, 1) - 1));
        }

        return list;
    }

    /// <summary>What the style's parts have put on one cell so far.</summary>
    private struct Format
    {
        public bool Bold;
        public CellColor? FontColor;
        public CellColor? Fill;
        public CellBorder? Left;
        public CellBorder? Right;
        public CellBorder? Top;
        public CellBorder? Bottom;

        public readonly bool HasBorders => Left is not null || Right is not null || Top is not null || Bottom is not null;

        public void Put(PivotStyleFormat format, bool first, bool last, bool start, bool end)
        {
            Bold |= format.Bold;
            FontColor = format.FontColor ?? FontColor;
            Fill = format.Fill ?? Fill;
            Top = (first ? format.Top : format.Horizontal) ?? Top;
            Bottom = (last ? format.Bottom : format.Horizontal) ?? Bottom;
            Left = (start ? format.Left : format.Vertical) ?? Left;
            Right = (end ? format.Right : format.Vertical) ?? Right;
        }
    }
}
