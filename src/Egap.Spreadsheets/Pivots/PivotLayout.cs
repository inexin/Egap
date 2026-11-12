using System.Globalization;
using System.Xml.Linq;

using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Pivots;

/// <summary>
/// Lays a pivot table out from its cache's values, as Excel does on a refresh: the items each field shows, the row and
/// column lines with their subtotals and grand totals, the captions above and beside them, and every value. The layout
/// keeps the table's top left cell; the page fields go above it with a row between.
/// </summary>
internal sealed class PivotLayout
{
    private readonly PivotDefinition definition;
    private readonly PivotCacheData data;
    private readonly IReadOnlyList<string> fieldNames;
    private readonly IReadOnlyList<int> numberFormats;
    private readonly Workbook book;
    private readonly int dataCount;
    private readonly int[][] positions;

    private PivotLayout(PivotDefinition definition, PivotCacheData data, IReadOnlyList<string> fieldNames, IReadOnlyList<int> numberFormats, Workbook book)
    {
        this.definition = definition;
        this.data = data;
        this.fieldNames = fieldNames;
        this.numberFormats = numberFormats;
        this.book = book;
        dataCount = definition.DataFields.Count;
        positions = new int[definition.Fields.Count][];
        for (int f = 0; f < definition.Fields.Count; f++)
        {
            int[] map = new int[f < data.Fields.Count ? data.Fields[f].Items.Count : 0];
            Array.Fill(map, -1);
            int position = 0;
            foreach (PivotItemDefinition item in definition.Fields[f].DataItems)
            {
                if ((uint)item.Shared < (uint)map.Length && map[item.Shared] < 0)
                {
                    map[item.Shared] = position;
                }

                position++;
            }

            positions[f] = map;
        }
    }

    /// <summary>The cells to write, by row and column from the table's top left cell; page fields have rows above it.</summary>
    public List<(int Row, int Column, CellValue Value, string? Format)> Cells { get; } = [];

    /// <summary>Where the table goes.</summary>
    public RangeAddress Range { get; private set; }

    /// <summary>Where the page fields go, or null when there are none.</summary>
    public RangeAddress? PageArea { get; private set; }

    /// <summary>The cells, from the table's top left, that show a field's button: beside its caption, or a page field's item.</summary>
    public List<(int Row, int Column)> Buttons { get; } = [];

    /// <summary>The row labels, from the table's top left, of a compact column, which Excel aligns left with an indent for each level.</summary>
    public List<(int Row, int Column, int Indent)> LeftLabels { get; } = [];

    /// <summary>The cells, from the table's top left, of outer items that show a button to expand and collapse them.</summary>
    public List<(int Row, int Column)> ExpandButtons { get; } = [];

    /// <summary>
    /// Brings the fields' items up to date with the cache's new items, then lays the table out and writes its location and
    /// row and column items into <paramref name="definition"/>, which should be a copy until the refresh is sure to go on.
    /// </summary>
    public static PivotLayout Build(PivotDefinition definition, PivotCacheData data, IReadOnlyList<PivotCacheField> cacheFields, Workbook book)
    {
        if (definition.Fields.Count != data.Fields.Count)
        {
            throw new InvalidOperationException("The pivot table's fields are not the cache's.");
        }

        CompareInfo compare = book.Culture.CompareInfo;
        for (int f = 0; f < definition.Fields.Count; f++)
        {
            UpdateItems(definition, f, data.Fields[f], compare);
        }

        // The fields after the refresh: those the cache had keep their number formats; new ones have none.
        int[] formats = [.. Enumerable.Range(0, data.Names.Length).Select(f => data.OldFields is { } old
            ? (old[f] >= 0 ? cacheFields[old[f]].NumberFormatId : 0)
            : cacheFields[f].NumberFormatId)];
        PivotLayout layout = new(definition, data, data.Names, formats, book);
        layout.Lay();
        return layout;
    }

    /// <summary>
    /// A field's items after the cache's items change: each keeps its place and settings and follows its value; one whose
    /// value the source no longer has is marked missing; values new to the field come at the end, hidden when the field
    /// hides items and does not take new ones into its filter; a sorted field sorts again. Subtotal items stay last.
    /// </summary>
    private static void UpdateItems(PivotDefinition definition, int f, PivotFieldData field, CompareInfo compare)
    {
        PivotFieldDefinition pivotField = definition.Fields[f];
        XNamespace ns = definition.Ns;
        bool onAxis = pivotField.Axis is "axisRow" or "axisCol" or "axisPage"
            || definition.RowFields.Contains(f) || definition.ColumnFields.Contains(f) || definition.PageFields.Any(p => p.Field == f);
        if (!field.Shared || (pivotField.ItemsElement is null && !onAxis))
        {
            return;
        }

        // The page fields' items, by value, to find them again.
        Dictionary<PageFieldDefinition, int> pages = [];
        List<PivotItemDefinition> before = [.. pivotField.DataItems];
        foreach (PageFieldDefinition page in definition.PageFields.Where(p => p.Field == f && p.Item is not null))
        {
            if ((uint)page.Item!.Value < (uint)before.Count && before[page.Item.Value].Shared is int shared && (uint)shared < (uint)field.Remap.Length)
            {
                pages[page] = field.Remap[shared];
            }
        }

        List<PivotItemDefinition> items = [];
        HashSet<int> taken = [];
        bool hides = before.Any(i => i.Hidden);
        // A field with no items yet, such as one a slicer was just put on, gets them sorted.
        bool created = pivotField.ItemsElement is null || pivotField.Items.Count == 0;
        foreach (PivotItemDefinition item in before)
        {
            int moved = (uint)item.Shared < (uint)field.Remap.Length ? field.Remap[item.Shared] : -1;
            if (moved < 0 || !taken.Add(moved))
            {
                continue;
            }

            item.Shared = moved;
            item.Element.SetAttributeValue("x", XlsxNames.Integer(moved));
            item.Element.SetAttributeValue("m", field.Unused[moved] ? "1" : null);
            InSchemaOrder(item.Element);
            items.Add(item);
        }

        List<PivotItemDefinition> added = [];
        for (int s = 0; s < field.Items.Count; s++)
        {
            if (!taken.Contains(s) && !field.Unused[s])
            {
                XElement element = new(ns + "item");
                if (hides && !pivotField.IncludeNewItemsInFilter)
                {
                    element.SetAttributeValue("h", "1");
                }

                element.SetAttributeValue("x", XlsxNames.Integer(s));
                added.Add(new PivotItemDefinition(element) { Shared = s });
            }
        }

        // A field Excel makes items for starts sorted; one that had them takes new values at the end.
        if (created)
        {
            added.Sort((a, b) => field.Items[a.Shared].CompareTo(field.Items[b.Shared], compare));
        }

        items.AddRange(added);
        if (pivotField.SortType is "ascending" or "descending")
        {
            int sign = pivotField.SortType == "ascending" ? 1 : -1;
            List<PivotItemDefinition> sorted = [.. items.Select((item, i) => (item, i))
                .OrderBy(p => p, Comparer<(PivotItemDefinition Item, int Index)>.Create((a, b) =>
                {
                    int c = sign * field.Items[a.Item.Shared].CompareTo(field.Items[b.Item.Shared], compare);
                    return c != 0 ? c : a.Index.CompareTo(b.Index);
                }))
                .Select(p => p.item)];
            items = sorted;
        }

        List<PivotItemDefinition> subtotals = [.. pivotField.Items.Where(i => i.Type != "data")];
        if (created)
        {
            subtotals = [.. pivotField.Subtotals.Select(t => new PivotItemDefinition(new XElement(ns + "item", new XAttribute("t", t))))];
            if (pivotField.ItemsElement is null)
            {
                pivotField.ItemsElement = new XElement(ns + "items");
                pivotField.Element.AddFirst(pivotField.ItemsElement);
            }
        }

        pivotField.Items.Clear();
        pivotField.Items.AddRange(items);
        pivotField.Items.AddRange(subtotals);
        XElement itemsElement = pivotField.ItemsElement!;
        itemsElement.RemoveNodes();
        itemsElement.Add(pivotField.Items.Select(i => i.Element));
        itemsElement.SetAttributeValue("count", XlsxNames.Integer(pivotField.Items.Count));

        // A page field whose item the source no longer has shows all items again.
        foreach ((PageFieldDefinition page, int shared) in pages)
        {
            int at = shared < 0 || field.Unused[shared] ? -1 : items.FindIndex(i => i.Shared == shared);
            page.Item = at < 0 ? null : at;
            page.Element.SetAttributeValue("item", at < 0 ? null : XlsxNames.Integer(at));
        }
    }

    private static readonly string[] ItemAttributeOrder = ["n", "t", "h", "s", "sd", "f", "m", "c", "x", "d", "e"];

    /// <summary>Puts an item's attributes in the order the schema lists them, as Excel writes them.</summary>
    private static void InSchemaOrder(XElement item)
    {
        List<XAttribute> attributes = [.. item.Attributes()];
        item.RemoveAttributes();
        item.Add(attributes.OrderBy(a => a.Name.Namespace == XNamespace.None && Array.IndexOf(ItemAttributeOrder, a.Name.LocalName) is int i and >= 0 ? i : ItemAttributeOrder.Length));
    }

    private enum LineKind
    {
        Data,
        Subtotal,
        Grand,
        Blank,
    }

    /// <summary>A row or column of the table: the items it is for, by level, and what it shows.</summary>
    private sealed class Line
    {
        public LineKind Kind { get; init; }

        /// <summary>The item's position in its field for each level the line is for; for the data fields' level, the data field.</summary>
        public int[] Path { get; init; } = [];

        /// <summary>The data field the line is for, or -1 when its axis does not have the data fields.</summary>
        public int Data { get; init; } = -1;

        /// <summary>The subtotal's item type, for a subtotal line.</summary>
        public string Type { get; init; } = "default";

        /// <summary>Whether the line heads its items in outline form, and so shows values only for a subtotal at the top.</summary>
        public bool Header { get; init; }

        /// <summary>Whether a header line shows its item's subtotal.</summary>
        public bool ShowsTotal { get; init; }

        /// <summary>The records the line is for.</summary>
        public List<int> Records { get; init; } = [];

        /// <summary>How many leading levels the line shares with the line before it.</summary>
        public int Repeated { get; set; }
    }

    private void Lay()
    {
        List<int> included = FilterRecords();
        List<int> rowFields = definition.RowFields;
        List<int> columnFields = definition.ColumnFields;
        // Excel sums a table that hides no record in the order of its items, and one a filter or slicer thins in the source's
        // order, which a total of many decimals shows in its last bits.
        if (included.Count == data.Records.Length)
        {
            SortByItems(included, [.. rowFields.Concat(columnFields).Where(f => f >= 0)]);
        }
        List<Line> rows = Axis(rowFields, rows: true, included);
        List<Line> columns = Axis(columnFields, rows: false, included);

        bool classic = definition.GridDropZones;
        bool columnField = columnFields.Any(f => f != PivotDefinition.DataField);
        int columnLevels = columnFields.Count;

        // The row label columns: a field in compact outline form shares its column with the field after it.
        int[] labelColumn = new int[rowFields.Count];
        for (int l = 1; l < rowFields.Count; l++)
        {
            labelColumn[l] = labelColumn[l - 1] + (OutlineForm(rowFields, l - 1) && Compact(rowFields[l - 1]) ? 0 : 1);
        }

        int firstDataColumn = rowFields.Count == 0 ? 0 : labelColumn[^1] + 1;
        bool topRow = classic || columnField;
        if (topRow && firstDataColumn == 0)
        {
            firstDataColumn = 1;
        }

        int firstHeaderRow, firstDataRow;
        if (classic)
        {
            firstDataRow = 1 + Math.Max(columnLevels, 1);
            firstHeaderRow = columnLevels == 0 ? firstDataRow : 1;
        }
        else if (columnLevels == 0)
        {
            firstHeaderRow = firstDataRow = 1;
        }
        else
        {
            firstHeaderRow = columnField ? 1 : 0;
            firstDataRow = firstHeaderRow + columnLevels;
        }

        int headerRow = firstDataRow - 1;
        CellAddress anchor = definition.Range.First;
        int height = firstDataRow + rows.Count;
        int width = firstDataColumn + columns.Count;
        if (anchor.Row + height > CellAddress.MaxRows || anchor.Column + width > CellAddress.MaxColumns)
        {
            throw new InvalidOperationException($"The pivot table {definition.Name} would not fit on the sheet.");
        }

        Range = new RangeAddress(anchor.Row, anchor.Column, anchor.Row + height - 1, anchor.Column + width - 1);

        // The top row: the data field's name in the corner, then the column fields' buttons or one caption for them.
        if (topRow)
        {
            // With no row fields Excel's newer layouts name the one data field on its row, not in the corner.
            if (dataCount == 1 && (classic || rowFields.Count > 0))
            {
                Put(0, 0, DataName(0));
            }

            if (!classic && columnFields.FirstOrDefault(f => f != PivotDefinition.DataField) is int first && Compact(first))
            {
                PutButton(0, firstDataColumn, definition.ColumnHeaderCaption ?? "Column Labels");
            }
            else
            {
                for (int j = 0; j < columnFields.Count; j++)
                {
                    PutButton(0, firstDataColumn + j, FieldCaption(columnFields[j]));
                }
            }
        }

        // The row fields' captions, over their columns.
        for (int l = 0; l < rowFields.Count; l++)
        {
            if (l == 0 || labelColumn[l] != labelColumn[l - 1])
            {
                int field = rowFields[l];
                // Values alone on the rows show the data caption, as Excel does, not Row Labels.
                bool valuesOnly = field == PivotDefinition.DataField && rowFields.Count == 1;
                PutButton(headerRow, labelColumn[l], !classic && !valuesOnly && Compact(field) ? definition.RowHeaderCaption ?? "Row Labels" : FieldCaption(field));
            }
        }

        // Column labels.
        for (int j = 0; j < columns.Count; j++)
        {
            Line line = columns[j];
            int column = firstDataColumn + j;
            switch (line.Kind)
            {
                case LineKind.Data when line.Path.Length == 0:
                    if (classic || dataCount > 1)
                    {
                        Put(headerRow, column, "Total");
                    }
                    else if (dataCount == 1)
                    {
                        Put(headerRow, column, DataName(0));
                    }

                    break;
                case LineKind.Data:
                    for (int l = line.Repeated; l < line.Path.Length; l++)
                    {
                        PutLabel(firstHeaderRow + l, column, columnFields[l], line.Path[l]);

                        // An item of an outer field shows the button that expands and collapses it.
                        if (columnFields[l] != PivotDefinition.DataField && columnFields.Skip(l + 1).Any(f => f != PivotDefinition.DataField))
                        {
                            ExpandButtons.Add((firstHeaderRow + l, column));
                        }
                    }

                    break;
                case LineKind.Subtotal:
                    Put(firstHeaderRow + line.Path.Length - 1, column, SubtotalLabel(columnFields, line));
                    break;
                case LineKind.Grand:
                    Put(firstHeaderRow, column, GrandLabel(columnFields, line));
                    break;
            }
        }

        // Row labels.
        for (int i = 0; i < rows.Count; i++)
        {
            Line line = rows[i];
            int row = firstDataRow + i;
            switch (line.Kind)
            {
                case LineKind.Data when line.Path.Length == 0:
                    if (firstDataColumn > 0)
                    {
                        Put(row, 0, !classic && dataCount == 1 && rowFields.Count == 0 ? DataName(0) : "Total");
                    }

                    break;
                case LineKind.Data:
                    for (int l = line.Repeated; l < line.Path.Length; l++)
                    {
                        PutLabel(row, labelColumn[l], rowFields[l], line.Path[l]);
                        CompactLabel(row, l);
                        if (rowFields[l] != PivotDefinition.DataField && rowFields.Skip(l + 1).Any(f => f != PivotDefinition.DataField))
                        {
                            ExpandButtons.Add((row, labelColumn[l]));
                        }
                    }

                    break;
                case LineKind.Subtotal:
                    Put(row, labelColumn[line.Path.Length - 1], SubtotalLabel(rowFields, line));
                    CompactLabel(row, line.Path.Length - 1);
                    break;
                case LineKind.Grand:
                    Put(row, 0, GrandLabel(rowFields, line));
                    CompactLabel(row, 0);
                    break;
            }
        }

        // Excel writes a compact column's labels at the left, each field inside the first indented a step more.
        void CompactLabel(int row, int level)
        {
            if (classic || level >= rowFields.Count || !Compact(rowFields[level]))
            {
                return;
            }

            int first = level;
            while (first > 0 && labelColumn[first - 1] == labelColumn[level])
            {
                first--;
            }

            LeftLabels.Add((row, labelColumn[level], level - first));
        }

        Values(rows, columns, firstDataRow, firstDataColumn);
        Pages();

        // The definition.
        XElement location = definition.Location!;
        location.SetAttributeValue("ref", Range.ToString());
        location.SetAttributeValue("firstHeaderRow", XlsxNames.Integer(firstHeaderRow));
        location.SetAttributeValue("firstDataRow", XlsxNames.Integer(firstDataRow));
        location.SetAttributeValue("firstDataCol", XlsxNames.Integer(firstDataColumn));
        WriteItems("rowItems", rows, ["rowFields", "pivotFields"]);
        WriteItems("colItems", columns, ["colFields", "rowItems", "rowFields", "pivotFields"]);
    }

    /// <summary>
    /// The records the table shows: those of no hidden item of a field it uses or a slicer filters, and of each page field's
    /// item.
    /// </summary>
    private List<int> FilterRecords()
    {
        List<(int Field, int? Page)> filters = [];
        HashSet<int> used = [.. definition.RowFields.Concat(definition.ColumnFields).Where(f => f >= 0)];

        // A slicer hides the items of its field whether the table shows the field or not.
        for (int f = 0; f < definition.Fields.Count; f++)
        {
            if (definition.Fields[f].DataItems.Any(i => i.Hidden) && !definition.PageFields.Any(p => p.Field == f))
            {
                used.Add(f);
            }
        }
        foreach (int field in used)
        {
            filters.Add((field, null));
        }

        foreach (PageFieldDefinition page in definition.PageFields)
        {
            filters.Add((page.Field, page.Item));
        }

        List<int> included = [];
        for (int r = 0; r < data.Records.Length; r++)
        {
            bool keep = true;
            foreach ((int field, int? page) in filters)
            {
                int position = Position(field, r);
                if (position < 0 || definition.Fields[field].Items[position].Hidden || (page is { } item && position != item))
                {
                    keep = false;
                    break;
                }
            }

            if (keep)
            {
                included.Add(r);
            }
        }

        return included;
    }

    /// <summary>
    /// Puts the records in the order of their items, row fields first, keeping the source's order among equals, as Excel
    /// sums them when no record is filtered out.
    /// </summary>
    private void SortByItems(List<int> records, int[] fields)
    {
        if (fields.Length == 0 || records.Count < 2)
        {
            return;
        }

        // Each record's positions packed into one number, its index last so equals keep their order: one sort of numbers.
        long radix = records.Count;
        int[] sizes = new int[fields.Length];
        for (int f = 0; f < fields.Length; f++)
        {
            int size = sizes[f] = data.Fields[fields[f]].Shared ? positions[fields[f]].DefaultIfEmpty(-1).Max() + 2 : 1;
            if (radix > long.MaxValue / size)
            {
                SortByItemsSlowly(records, fields);
                return;
            }

            radix *= size;
        }

        long[] keys = new long[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            long key = 0;
            for (int f = 0; f < fields.Length; f++)
            {
                key = (key * sizes[f]) + Position(fields[f], records[i]) + 1;
            }

            keys[i] = (key * records.Count) + i;
        }

        Array.Sort(keys);
        int[] sorted = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            sorted[i] = records[(int)(keys[i] % records.Count)];
        }

        sorted.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(records));
    }

    /// <summary>The same order, for fields with too many items to pack into one number.</summary>
    private void SortByItemsSlowly(List<int> records, int[] fields)
    {
        int[] keys = new int[records.Count * fields.Length];
        for (int i = 0; i < records.Count; i++)
        {
            for (int f = 0; f < fields.Length; f++)
            {
                keys[(i * fields.Length) + f] = Position(fields[f], records[i]);
            }
        }

        int[] order = new int[records.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            for (int f = 0; f < fields.Length; f++)
            {
                int c = keys[(a * fields.Length) + f].CompareTo(keys[(b * fields.Length) + f]);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.CompareTo(b);
        });

        int[] sorted = new int[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            sorted[i] = records[order[i]];
        }

        for (int i = 0; i < sorted.Length; i++)
        {
            records[i] = sorted[i];
        }
    }

    /// <summary>A record's item's position in a field, or -1.</summary>
    private int Position(int field, int record)
    {
        PivotFieldData fieldData = data.Fields[field];
        return fieldData.Shared ? positions[field][fieldData.Index[record]] : -1;
    }

    private bool OutlineForm(List<int> fields, int level) =>
        fields[level] == PivotDefinition.DataField ? definition.OutlineData : definition.Fields[fields[level]].Outline;

    private bool Compact(int field) => field == PivotDefinition.DataField ? definition.CompactData : definition.Fields[field].Compact;

    /// <summary>The lines of the rows or the columns.</summary>
    private List<Line> Axis(List<int> fields, bool rows, List<int> records)
    {
        List<Line> lines = [];
        if (fields.Count == 0)
        {
            lines.Add(new Line { Records = records });
            return lines;
        }

        Lines(fields, rows, 0, [], records, -1, lines);
        bool grand = rows ? definition.RowGrandTotals : definition.ColumnGrandTotals;
        if (grand && fields.Any(f => f != PivotDefinition.DataField))
        {
            bool data = fields.Contains(PivotDefinition.DataField);
            for (int d = 0; d < (data ? dataCount : 1); d++)
            {
                lines.Add(new Line { Kind = LineKind.Grand, Records = records, Data = data ? d : -1 });
            }
        }

        // How many leading items each line shares with the line for items before it.
        int[]? previous = null;
        foreach (Line line in lines)
        {
            if (line.Kind != LineKind.Data)
            {
                continue;
            }

            int shared = 0;
            if (previous is not null)
            {
                while (shared < previous.Length && shared < line.Path.Length - 1 && previous[shared] == line.Path[shared])
                {
                    shared++;
                }
            }

            line.Repeated = shared;
            previous = line.Path;
        }

        return lines;
    }

    private void Lines(List<int> fields, bool rows, int level, int[] path, List<int> records, int data, List<Line> lines)
    {
        int field = fields[level];
        bool last = level == fields.Count - 1;
        if (field == PivotDefinition.DataField)
        {
            for (int d = 0; d < dataCount; d++)
            {
                int[] next = [.. path, d];
                if (last)
                {
                    lines.Add(new Line { Path = next, Records = records, Data = d });
                    continue;
                }

                if (rows && OutlineForm(fields, level))
                {
                    lines.Add(new Line { Path = next, Records = records, Data = d, Header = true });
                }

                Lines(fields, rows, level + 1, next, records, d, lines);
            }

            return;
        }

        PivotFieldDefinition pivotField = definition.Fields[field];
        int count = pivotField.DataItems.Count();
        List<int>?[] groups = new List<int>?[count];
        foreach (int record in records)
        {
            int position = Position(field, record);
            if (position >= 0)
            {
                (groups[position] ??= []).Add(record);
            }
        }

        bool fieldBelow = fields.Skip(level + 1).Any(f => f != PivotDefinition.DataField);
        bool dataBelow = dataCount > 1 && fields.Skip(level + 1).Contains(PivotDefinition.DataField);
        List<string> subtotals = fieldBelow ? pivotField.Subtotals : [];
        bool outline = rows && !last && OutlineForm(fields, level);
        bool top = outline && pivotField.SubtotalTop && subtotals is ["default"] && !dataBelow;
        for (int position = 0; position < count; position++)
        {
            PivotItemDefinition item = pivotField.Items[position];
            List<int>? group = groups[position];
            if (group is null && !(pivotField.ShowAll && !item.Hidden && !item.Missing))
            {
                continue;
            }

            group ??= [];
            int[] next = [.. path, position];
            if (last)
            {
                lines.Add(new Line { Path = next, Records = group, Data = data });
                continue;
            }

            if (outline)
            {
                lines.Add(new Line { Path = next, Records = group, Data = data, Header = true, ShowsTotal = top });
            }

            Lines(fields, rows, level + 1, next, group, data, lines);
            if (!top)
            {
                foreach (string type in subtotals)
                {
                    for (int d = dataBelow ? 0 : data; d < (dataBelow ? dataCount : data + 1); d++)
                    {
                        lines.Add(new Line { Kind = LineKind.Subtotal, Path = next, Records = group, Data = d, Type = type });
                    }
                }
            }

            if (rows && pivotField.InsertBlankRow)
            {
                lines.Add(new Line { Kind = LineKind.Blank, Path = next });
            }
        }
    }

    /// <summary>Every value: for each row line, the records it shares with each column line, summarized.</summary>
    private void Values(List<Line> rows, List<Line> columns, int firstDataRow, int firstDataColumn)
    {
        if (dataCount == 0)
        {
            return;
        }

        // For each record, the column lines it is in.
        int[] start = new int[data.Records.Length + 1];
        foreach (Line column in columns)
        {
            foreach (int record in column.Records)
            {
                start[record + 1]++;
            }
        }

        for (int r = 0; r < data.Records.Length; r++)
        {
            start[r + 1] += start[r];
        }

        int[] next = (int[])start.Clone();
        int[] membership = new int[start[^1]];
        for (int j = 0; j < columns.Count; j++)
        {
            foreach (int record in columns[j].Records)
            {
                membership[next[record]++] = j;
            }
        }

        Summary[] summaries = new Summary[columns.Count];
        bool[] touched = new bool[columns.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            Line row = rows[i];
            if (row.Kind == LineKind.Blank || (row.Header && !row.ShowsTotal))
            {
                continue;
            }

            Array.Clear(touched);
            foreach (int record in row.Records)
            {
                for (int k = start[record]; k < start[record + 1]; k++)
                {
                    int j = membership[k];
                    if (!touched[j])
                    {
                        touched[j] = true;
                        summaries[j] = new Summary();
                    }

                    int d = DataOf(row, columns[j]);
                    summaries[j].Add(data.Records[record][definition.DataFields[d].Field]);
                }
            }

            for (int j = 0; j < columns.Count; j++)
            {
                Line column = columns[j];
                if (column.Kind == LineKind.Blank)
                {
                    continue;
                }

                int d = DataOf(row, column);
                DataFieldDefinition dataField = definition.DataFields[d];
                CellValue value = touched[j] ? summaries[j].Result(FunctionOf(row, column, dataField)) : CellValue.Empty;
                if (value.IsEmpty)
                {
                    if (definition.MissingCaption is { Length: > 0 } missing)
                    {
                        Put(firstDataRow + i, firstDataColumn + j, missing);
                    }

                    continue;
                }

                if (value.Kind == CellValueKind.Error && definition.ErrorCaption is { } caption)
                {
                    value = caption;
                }

                Put(firstDataRow + i, firstDataColumn + j, value, dataField.NumberFormatId > 0 ? book.Styles.NumberFormats.Code(dataField.NumberFormatId) : null);
            }
        }
    }

    private static int DataOf(Line row, Line column) => row.Data >= 0 ? row.Data : column.Data >= 0 ? column.Data : 0;

    private static PivotFunction FunctionOf(Line row, Line column, DataFieldDefinition dataField)
    {
        if (row.Kind == LineKind.Subtotal && PivotDefinition.SubtotalFunction(row.Type) is { } rowFunction)
        {
            return rowFunction;
        }

        if (column.Kind == LineKind.Subtotal && PivotDefinition.SubtotalFunction(column.Type) is { } columnFunction)
        {
            return columnFunction;
        }

        return PivotDefinition.Function(dataField.Subtotal) ?? PivotFunction.Sum;
    }

    /// <summary>The page fields above the table, one to a row, each with the item it shows.</summary>
    private void Pages()
    {
        int count = definition.PageFields.Count;
        if (count == 0)
        {
            return;
        }

        CellAddress anchor = definition.Range.First;
        int top = -1 - count;
        if (anchor.Row + top < 0)
        {
            throw new InvalidOperationException($"The pivot table {definition.Name} has no room above it for its page fields.");
        }

        for (int k = 0; k < count; k++)
        {
            PageFieldDefinition page = definition.PageFields[k];
            Put(top + k, 0, FieldCaption(page.Field));

            // A page field's button sits beside its item, not its caption.
            Buttons.Add((top + k, 1));
            if (page.Item is { } item && item < definition.Fields[page.Field].DataItems.Count())
            {
                PutLabel(top + k, 1, page.Field, item);
            }
            else
            {
                PivotFieldDefinition pageField = definition.Fields[page.Field];
                Put(top + k, 1, pageField.MultipleItemSelectionAllowed && pageField.DataItems.Any(i => i.Hidden) ? "(Multiple Items)" : "(All)");
            }
        }

        PageArea = new RangeAddress(anchor.Row + top, anchor.Column, anchor.Row - 2, anchor.Column + 1);
    }

    private void WriteItems(string name, List<Line> lines, string[] after)
    {
        XNamespace ns = definition.Ns;
        XElement element = new(ns + name, new XAttribute("count", XlsxNames.Integer(lines.Count)));
        foreach (Line line in lines)
        {
            XElement i = new(ns + "i");
            switch (line.Kind)
            {
                case LineKind.Data when line.Path.Length == 0:
                    break;
                case LineKind.Data:
                    if (line.Repeated > 0)
                    {
                        i.SetAttributeValue("r", XlsxNames.Integer(line.Repeated));
                    }

                    if (line.Data > 0)
                    {
                        i.SetAttributeValue("i", XlsxNames.Integer(line.Data));
                    }

                    i.Add(line.Path.Skip(line.Repeated).Select(v => X(ns, v)));
                    break;
                case LineKind.Grand:
                case LineKind.Subtotal:
                case LineKind.Blank:
                    i.SetAttributeValue("t", line.Kind switch { LineKind.Grand => "grand", LineKind.Blank => "blank", _ => line.Type });
                    if (line.Data > 0)
                    {
                        i.SetAttributeValue("i", XlsxNames.Integer(line.Data));
                    }

                    i.Add(line.Kind == LineKind.Grand ? [X(ns, 0)] : line.Path.Select(v => X(ns, v)));
                    break;
            }

            element.Add(i);
        }

        XElement root = definition.Root;
        if (root.Element(ns + name) is { } old)
        {
            old.ReplaceWith(element);
            return;
        }

        foreach (string before in after)
        {
            if (root.Element(ns + before) is { } anchor)
            {
                anchor.AddAfterSelf(element);
                return;
            }
        }

        if (definition.Location is { } location)
        {
            location.AddAfterSelf(element);
        }
    }

    private static XElement X(XNamespace ns, int value) => value == 0 ? new XElement(ns + "x") : new XElement(ns + "x", new XAttribute("v", XlsxNames.Integer(value)));

    private void Put(int row, int column, CellValue value, string? format = null) => Cells.Add((row, column, value, format));

    /// <summary>A field's caption, which Excel shows with the field's button beside it.</summary>
    private void PutButton(int row, int column, CellValue value)
    {
        Put(row, column, value);
        Buttons.Add((row, column));
    }

    /// <summary>An item's label: its caption, its value, or <c>(blank)</c>; a number or date in the field's number format.</summary>
    private void PutLabel(int row, int column, int field, int position)
    {
        if (field == PivotDefinition.DataField)
        {
            Put(row, column, DataName(position));
            return;
        }

        PivotItemDefinition item = definition.Fields[field].Items[position];
        if (item.Caption is { } caption)
        {
            Put(row, column, caption);
            return;
        }

        PivotValue value = data.Fields[field].Items[item.Shared];
        switch (value.Kind)
        {
            case PivotValueKind.Blank:
                Put(row, column, "(blank)");
                break;
            case PivotValueKind.Number or PivotValueKind.Date:
                Put(row, column, value.Number, FieldFormat(field) is { } code && code != "General" ? code : null);
                break;
            case PivotValueKind.Text when value.Text.Length == 0:
                // An empty text item leaves its label cell empty.
                break;
            case PivotValueKind.Boolean:
                // Excel writes a true or false item's label as text.
                Put(row, column, value.ToString());
                break;
            default:
                Put(row, column, value.ToCell());
                break;
        }
    }

    /// <summary>An item's label as text, as a subtotal shows it: <c>East</c>, <c>2019</c>.</summary>
    private string LabelText(int field, int position)
    {
        if (field == PivotDefinition.DataField)
        {
            return DataName(position);
        }

        PivotItemDefinition item = definition.Fields[field].Items[position];
        if (item.Caption is { } caption)
        {
            return caption;
        }

        PivotValue value = data.Fields[field].Items[item.Shared];
        return value.Kind switch
        {
            PivotValueKind.Blank => "(blank)",
            PivotValueKind.Number or PivotValueKind.Date => NumberFormatCode.Get(FieldFormat(field) ?? "General").Format(value.Number, book.Culture, book.Date1904),
            _ => value.ToString(),
        };
    }

    private string SubtotalLabel(List<int> fields, Line line)
    {
        int level = line.Path.Length - 1;
        int field = fields[level];
        PivotFieldDefinition pivotField = definition.Fields[field];
        if (pivotField.SubtotalCaption is { } caption)
        {
            return caption;
        }

        string item = LabelText(field, line.Path[level]);
        bool dataBelow = dataCount > 1 && fields.Skip(level + 1).Contains(PivotDefinition.DataField);
        if (dataBelow)
        {
            return line.Type == "default" ? $"{item} {DataName(line.Data)}" : $"{item} {PivotDefinition.SubtotalWord(line.Type)} {DataName(line.Data)}";
        }

        return $"{item} {PivotDefinition.SubtotalWord(line.Type)}";
    }

    private string GrandLabel(List<int> fields, Line line) =>
        dataCount > 1 && fields.Contains(PivotDefinition.DataField) && line.Data >= 0
            ? (definition.GrandTotalCaption is { } total ? total + " " : "Total ") + DataName(line.Data)
            : definition.GrandTotalCaption ?? "Grand Total";

    private string FieldCaption(int field)
    {
        if (field == PivotDefinition.DataField)
        {
            return definition.DataCaption;
        }

        return definition.Fields[field].Caption ?? (field < fieldNames.Count ? fieldNames[field] : "");
    }

    private string DataName(int index)
    {
        DataFieldDefinition dataField = definition.DataFields[index];
        return dataField.Name ?? $"{PivotDefinition.FunctionWord(dataField.Subtotal)} of {FieldCaption(dataField.Field)}";
    }

    /// <summary>The number format a field's items show in: the pivot field's, else the cache field's.</summary>
    private string? FieldFormat(int field)
    {
        int id = definition.Fields[field].NumberFormatId ?? (field < numberFormats.Count ? numberFormats[field] : 0);
        return id == 0 ? null : book.Styles.NumberFormats.Code(id);
    }

    /// <summary>The values of one cell of a pivot table, summarized as each function needs them.</summary>
    private struct Summary
    {
        private int values;
        private int numbers;
        private double sum;
        private double product;
        private double min;
        private double max;
        private PivotValue? error;
        private double squares;

        public void Add(PivotValue value)
        {
            if (value.Kind == PivotValueKind.Blank)
            {
                return;
            }

            values++;
            if (value.Kind == PivotValueKind.Error)
            {
                error ??= value;
                return;
            }

            if (!value.IsNumeric)
            {
                return;
            }

            double x = value.Number;
            if (numbers == 0)
            {
                product = 1;
                min = max = x;
            }

            numbers++;
            sum += x;
            product *= x;
            min = Math.Min(min, x);
            max = Math.Max(max, x);
            squares += x * x;
        }

        public readonly CellValue Result(PivotFunction function)
        {
            if (values == 0)
            {
                return CellValue.Empty;
            }

            if (function == PivotFunction.Count)
            {
                return values;
            }

            if (function == PivotFunction.CountNumbers)
            {
                return numbers;
            }

            if (error is { } e)
            {
                return e.ToCell();
            }

            double result = function switch
            {
                PivotFunction.Sum => sum,
                PivotFunction.Average => numbers == 0 ? double.NaN : sum / numbers,
                PivotFunction.Max => numbers == 0 ? 0 : max,
                PivotFunction.Min => numbers == 0 ? 0 : min,
                PivotFunction.Product => numbers == 0 ? 0 : product,
                PivotFunction.StandardDeviation => Math.Sqrt(Variance(1)),
                PivotFunction.StandardDeviationPopulation => Math.Sqrt(Variance(0)),
                PivotFunction.Variance => Variance(1),
                PivotFunction.VariancePopulation => Variance(0),
                _ => sum,
            };

            if (double.IsNaN(result))
            {
                return CellError.DivisionByZero;
            }

            return double.IsFinite(result) ? result : CellError.Number;
        }

        private readonly double Variance(int correction)
        {
            if (numbers - correction <= 0)
            {
                return double.NaN;
            }

            // Excel's pivot tables use the one-pass sums, to the last bit: the squares less the square of the sum over the count,
            // and none below zero, which rounding can give for equal values.
            return Math.Max(0, (squares - (sum * sum / numbers)) / (numbers - correction));
        }
    }
}
