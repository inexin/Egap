using System.Globalization;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Storage;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Pivots;

/// <summary>
/// A pivot cache: its definition part (the source, the fields and their shared items, ECMA-376 Part 1, 18.10.1.67) and its
/// records part. A refresh reads the source range again, as Excel does: the shared items in the order the source first
/// has them, then those it no longer has, kept and marked unused; then the records again.
/// </summary>
internal sealed class PivotCache
{
    private readonly XNamespace ns;

    public PivotCache(OpcPackage package, OpcPart part, XDocument definition, bool date1904)
    {
        DefinitionPart = part;
        Definition = definition;
        XElement root = definition.Root!;
        ns = root.Name.Namespace;
        RecordsPart = (string?)root.Attribute(XlsxNames.R + "id") is { } id && part.GetRelationship(id) is { External: false } rel ? package.GetPart(rel.TargetName) : null;

        XElement? source = root.Element(ns + "cacheSource");
        WorksheetSource = source?.Element(ns + "worksheetSource");
        string type = (string?)source?.Attribute("type") ?? "";
        if (type != "worksheet" || WorksheetSource is null)
        {
            Problem = "The pivot cache's source is not a range of a worksheet.";
        }
        else if ((string?)WorksheetSource.Attribute(XlsxNames.R + "id") is not null)
        {
            Problem = "The pivot cache's source is in another workbook.";
        }

        foreach (XElement element in root.Element(ns + "cacheFields")?.Elements(ns + "cacheField") ?? [])
        {
            Fields.Add(new PivotCacheField(element, date1904));
        }

        if (Problem is null && Fields.Any(f => !f.Database))
        {
            Problem = "The pivot cache has calculated or grouped fields.";
        }

        if (Problem is null && Fields.Count == 0)
        {
            Problem = "The pivot cache has no fields.";
        }

        MissingItemsLimit = XlsxNames.Int((string?)root.Attribute("missingItemsLimit"), -1);
    }

    public OpcPart DefinitionPart { get; }

    public XDocument Definition { get; }

    public OpcPart? RecordsPart { get; }

    /// <summary>The records part as the last refresh wrote it, or null when no refresh changed it.</summary>
    public XDocument? NewRecords { get; private set; }

    /// <summary>Whether a refresh or an edit changed the definition, so a save writes it again.</summary>
    public bool Changed { get; set; }

    public XElement? WorksheetSource { get; }

    public List<PivotCacheField> Fields { get; } = [];

    /// <summary>The pivot tables that read the cache.</summary>
    public List<PivotTable> Tables { get; } = [];

    /// <summary>Why the cache cannot be refreshed, or null when it can.</summary>
    public string? Problem { get; }

    /// <summary>How many items no longer in the source a field keeps; -1 for Excel's default, all of them.</summary>
    private int MissingItemsLimit { get; }

    /// <summary>The source as a reference, such as <c>Data!A1:G61</c>, or the name of a table or defined name.</summary>
    public string? SourceText
    {
        get
        {
            if (WorksheetSource is null)
            {
                return null;
            }

            if ((string?)WorksheetSource.Attribute("name") is { } name)
            {
                return name;
            }

            string? range = (string?)WorksheetSource.Attribute("ref");
            string? sheet = (string?)WorksheetSource.Attribute("sheet");
            return sheet is null ? range : SheetNames.Quoted(sheet) + "!" + range;
        }
    }

    /// <summary>Moves the source range as rows or columns go into or out of its sheet, as Excel does.</summary>
    public void SheetEdited(Worksheet edited, SheetEdit edit)
    {
        if (WorksheetSource is null || (string?)WorksheetSource.Attribute("name") is not null
            || !string.Equals((string?)WorksheetSource.Attribute("sheet"), edited.Name, StringComparison.OrdinalIgnoreCase)
            || !RangeAddress.TryParse((string?)WorksheetSource.Attribute("ref"), out RangeAddress range))
        {
            return;
        }

        RangeAddress? moved = edit.Moved(range);
        string text = moved?.ToString() ?? "#REF!";
        if (text != (string?)WorksheetSource.Attribute("ref"))
        {
            WorksheetSource.SetAttributeValue("ref", text);
            Changed = true;
        }
    }

    /// <summary>Follows a sheet's new name.</summary>
    public void SheetRenamed(string from, string to)
    {
        if (WorksheetSource is not null && string.Equals((string?)WorksheetSource.Attribute("sheet"), from, StringComparison.OrdinalIgnoreCase))
        {
            WorksheetSource.SetAttributeValue("sheet", to);
            Changed = true;
        }
    }

    /// <summary>The sheet and range the source names, header row included.</summary>
    /// <exception cref="InvalidOperationException">The source cannot be found.</exception>
    public (Worksheet Sheet, RangeAddress Range) ResolveSource(Workbook book)
    {
        if (Problem is not null)
        {
            throw new InvalidOperationException(Problem);
        }

        XElement source = WorksheetSource!;
        string? sheetName = (string?)source.Attribute("sheet");
        if ((string?)source.Attribute("name") is { } name)
        {
            foreach (Worksheet sheet in book.Worksheets)
            {
                foreach (SheetTable table in sheet.Tables)
                {
                    if (string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        RangeAddress range = table.Range;
                        return (sheet, table.ShowTotalsRow && range.RowCount > 1 ? new RangeAddress(range.First.Row, range.First.Column, range.Last.Row - 1, range.Last.Column) : range);
                    }
                }
            }

            // A name of the source's sheet comes before one of the workbook.
            DefinedName? defined = book.Names
                .Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
                    && (n.Sheet is null || (sheetName is not null && string.Equals(n.Sheet.Name, sheetName, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(n => n.Sheet is null ? 1 : 0)
                .FirstOrDefault();
            if (defined is not null && NameRange(book, defined) is { } named)
            {
                return named;
            }

            throw new InvalidOperationException($"The pivot cache's source {name} is not a table or a name of a range in the workbook.");
        }

        if (sheetName is null || !book.Worksheets.TryGet(sheetName, out Worksheet? found))
        {
            throw new InvalidOperationException($"The pivot cache's source sheet {sheetName} is not in the workbook.");
        }

        string? reference = (string?)source.Attribute("ref");
        if (!RangeAddress.TryParse(reference?.Replace("$", "", StringComparison.Ordinal), out RangeAddress area))
        {
            throw new InvalidOperationException($"The pivot cache's source {reference} is not a range.");
        }

        return (found, area);
    }

    /// <summary>
    /// The one range a defined name refers to, worked out as a formula would, so a name made with OFFSET or INDEX follows
    /// the data; null when it is not one range.
    /// </summary>
    private static (Worksheet Sheet, RangeAddress Range)? NameRange(Workbook book, DefinedName name)
    {
        Worksheet? home = name.Sheet ?? (book.Worksheets.Count > 0 ? book.Worksheets[0] : null);
        if (home is null || FormulaParser.TryParse(name.Formula, r1c1: false, default) is not { } parsed)
        {
            return null;
        }

        try
        {
            EvalValue value = FormulaEvaluator.Run(FormulaCompiler.Compile(parsed), new EvalContext(home, 0, 0));
            return value.Kind == EvalKind.Reference && value.Areas is [SheetArea area] ? (area.Sheet, area.Range) : null;
        }
        catch (FormulaException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the source again: the fields' values, record by record, and each field's items, without changing the cache.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source cannot be found, or its header row does not name the cache's fields.</exception>
    public PivotCacheData Read(Workbook book, ISet<int> axisFields)
    {
        (Worksheet sheet, RangeAddress range) = ResolveSource(book);
        if (range.RowCount < 2)
        {
            throw new InvalidOperationException("The pivot cache's source has no rows under its header row.");
        }

        // Excel names a field from its header cell, without spaces around it, and finds the cache's fields by name: a
        // column the source gains becomes a new field, and a field whose column is gone leaves the pivot tables.
        Dictionary<string, int> oldFields = new(StringComparer.OrdinalIgnoreCase);
        for (int f = 0; f < Fields.Count; f++)
        {
            oldFields.TryAdd(Fields[f].Name.Trim(), f);
        }

        int columns = range.ColumnCount;
        string[] names = new string[columns];
        int[] oldOf = new int[columns];
        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < columns; c++)
        {
            Cell header = sheet[range.First.Row, range.First.Column + c];
            string text = header.Value.IsEmpty ? "" : header.Text.Trim();
            if (text.Length == 0)
            {
                throw new InvalidOperationException("The pivot table field name is not valid: a header cell of the source is empty.");
            }

            string name = text;
            for (int n = 2; !seen.TryAdd(name, c); n++)
            {
                name = text + n.ToString(CultureInfo.InvariantCulture);
            }

            // A field found again keeps the name the cache gave it.
            oldOf[c] = oldFields.TryGetValue(name, out int old) ? old : -1;
            names[c] = oldOf[c] >= 0 ? Fields[oldOf[c]].Name : name;
        }

        bool same = columns == Fields.Count && oldOf.Select((old, c) => old == c).All(b => b);

        // Which style indexes are date formats, as a cell's number format makes its number a date.
        StyleTable styles = book.Styles;
        Dictionary<int, bool> dates = [];
        bool IsDate(int row, int column)
        {
            int style = sheet.GetStyleIndex(row, column);
            if (!dates.TryGetValue(style, out bool date))
            {
                date = NumberFormatCode.Get(styles[style].NumberFormat).IsDateTime;
                dates[style] = date;
            }

            return date;
        }

        int count = range.RowCount - 1;
        PivotValue[][] records = new PivotValue[count][];
        for (int r = 0; r < count; r++)
        {
            int row = range.First.Row + 1 + r;
            PivotValue[] record = new PivotValue[columns];
            for (int c = 0; c < columns; c++)
            {
                int column = range.First.Column + c;
                CellValue value = sheet.GetValue(row, column);
                record[c] = PivotValue.FromCell(value, value.Kind == CellValueKind.Number && IsDate(row, column));
            }

            records[r] = record;
        }

        PivotCacheData data = new(records, names, same ? null : oldOf);
        for (int f = 0; f < columns; f++)
        {
            List<PivotValue> oldItems = oldOf[f] >= 0 ? Fields[oldOf[f]].Items : [];
            bool shared = oldOf[f] >= 0 && (Fields[oldOf[f]].HasItems || axisFields.Contains(oldOf[f]));
            List<PivotValue> items = [];
            List<bool> unused = [];
            int[] index = new int[count];
            int[] remap = new int[oldItems.Count];
            if (shared)
            {
                Dictionary<PivotValue, int> positions = [];
                for (int r = 0; r < count; r++)
                {
                    PivotValue value = records[r][f];
                    if (!positions.TryGetValue(value, out int at))
                    {
                        at = items.Count;
                        positions.Add(value, at);
                        items.Add(value);
                        unused.Add(false);
                    }

                    index[r] = at;
                }

                // Items the source no longer has stay, unused, so pivot tables keep their place and settings.
                int kept = 0;
                for (int i = 0; i < oldItems.Count; i++)
                {
                    PivotValue old = oldItems[i];
                    if (positions.TryGetValue(old, out int at))
                    {
                        remap[i] = at;
                    }
                    else if (MissingItemsLimit < 0 || kept < MissingItemsLimit)
                    {
                        kept++;
                        remap[i] = items.Count;
                        positions.Add(old, items.Count);
                        items.Add(old);
                        unused.Add(true);
                    }
                    else
                    {
                        remap[i] = -1;
                    }
                }
            }
            else
            {
                Array.Fill(remap, -1);
            }

            data.Fields.Add(new PivotFieldData(shared, items, unused, index, remap));
        }

        return data;
    }

    /// <summary>Writes what <see cref="Read"/> found into the definition and the records.</summary>
    public void Apply(PivotCacheData data, Workbook book)
    {
        XElement root = Definition.Root!;
        bool date1904 = book.Date1904;
        if (data.OldFields is { } oldOf)
        {
            XElement list = root.Element(ns + "cacheFields")!;
            List<PivotCacheField> fields = [];
            for (int f = 0; f < oldOf.Length; f++)
            {
                fields.Add(oldOf[f] >= 0
                    ? Fields[oldOf[f]]
                    : new PivotCacheField(new XElement(ns + "cacheField", new XAttribute("name", XlsxNames.Encode(data.Names[f])), new XAttribute("numFmtId", "0")), date1904));
            }

            list.RemoveNodes();
            list.Add(fields.Select(f => f.Element));
            list.SetAttributeValue("count", XlsxNames.Integer(fields.Count));
            Fields.Clear();
            Fields.AddRange(fields);
        }

        for (int f = 0; f < Fields.Count; f++)
        {
            PivotCacheField field = Fields[f];
            PivotFieldData fieldData = data.Fields[f];
            XElement shared = field.Element.Element(ns + "sharedItems") ?? new XElement(ns + "sharedItems");
            if (shared.Parent is null)
            {
                field.Element.AddFirst(shared);
            }

            WriteSharedItems(shared, data.Records, f, fieldData, date1904);
            field.Items.Clear();
            field.Items.AddRange(fieldData.Items);
            field.HasItems = fieldData.Shared;
        }

        root.SetAttributeValue("recordCount", XlsxNames.Integer(data.Records.Length));
        double now = ExcelValues.Serial(Clock.Now, false) ?? 0;
        root.SetAttributeValue("refreshedDate", XlsxNames.Number(Math.Round(now, 9)));
        root.SetAttributeValue("invalid", null);
        Changed = true;

        if (RecordsPart is not null && (string?)root.Attribute("saveData") is not ("0" or "false"))
        {
            XElement records = new(ns + "pivotCacheRecords", new XAttribute("count", XlsxNames.Integer(data.Records.Length)));
            for (int r = 0; r < data.Records.Length; r++)
            {
                XElement record = new(ns + "r");
                for (int f = 0; f < Fields.Count; f++)
                {
                    PivotFieldData fieldData = data.Fields[f];
                    record.Add(fieldData.Shared
                        ? new XElement(ns + "x", new XAttribute("v", XlsxNames.Integer(fieldData.Index[r])))
                        : data.Records[r][f].ToXml(ns, date1904));
                }

                records.Add(record);
            }

            NewRecords = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), records);
        }
    }

    /// <summary>
    /// A field's shared items element again: what kinds of value the source has, the smallest and largest number and date,
    /// and the items themselves when the field shares them.
    /// </summary>
    private void WriteSharedItems(XElement shared, PivotValue[][] records, int f, PivotFieldData data, bool date1904)
    {
        bool text = false, blank = false, number = false, date = false, other = false, integer = true, longText = false;
        double min = double.MaxValue, max = double.MinValue, minDate = double.MaxValue, maxDate = double.MinValue;
        foreach (PivotValue[] record in records)
        {
            PivotValue value = record[f];
            switch (value.Kind)
            {
                case PivotValueKind.Blank:
                    blank = true;
                    break;
                case PivotValueKind.Number:
                    number = true;
                    integer &= Math.Floor(value.Number) == value.Number;
                    min = Math.Min(min, value.Number);
                    max = Math.Max(max, value.Number);
                    break;
                case PivotValueKind.Date:
                    date = true;
                    minDate = Math.Min(minDate, value.Number);
                    maxDate = Math.Max(maxDate, value.Number);
                    break;
                case PivotValueKind.Text:
                    text = true;
                    longText |= value.Text.Length > 255;
                    break;
                default:
                    other = true;
                    break;
            }
        }

        // An unused item still counts for the kinds, as Excel keeps them.
        foreach (PivotValue value in data.Items.Where((_, i) => data.Unused[i]))
        {
            text |= value.Kind == PivotValueKind.Text;
            blank |= value.Kind == PivotValueKind.Blank;
        }

        bool stringish = text || other;
        int kinds = (stringish ? 1 : 0) + (number ? 1 : 0) + (date ? 1 : 0);
        shared.RemoveAttributes();
        shared.RemoveNodes();
        void Flag(string name, bool value, bool fallback)
        {
            if (value != fallback)
            {
                shared.SetAttributeValue(name, value ? "1" : "0");
            }
        }

        Flag("containsSemiMixedTypes", stringish || blank, true);
        Flag("containsNonDate", stringish || number || blank || records.Length == 0, true);
        Flag("containsDate", date, false);
        Flag("containsString", stringish || records.Length == 0, true);
        Flag("containsBlank", blank, false);
        Flag("containsMixedTypes", kinds > 1, false);
        Flag("containsNumber", number, false);
        Flag("containsInteger", number && integer, false);
        if (number)
        {
            shared.SetAttributeValue("minValue", XlsxNames.Number(min));
            shared.SetAttributeValue("maxValue", XlsxNames.Number(max));
        }

        if (date)
        {
            shared.SetAttributeValue("minDate", PivotValue.DateText(minDate, date1904));
            shared.SetAttributeValue("maxDate", PivotValue.DateText(maxDate + 1, date1904));
        }

        if (data.Shared)
        {
            shared.SetAttributeValue("count", XlsxNames.Integer(data.Items.Count));
        }

        Flag("longText", longText, false);
        if (data.Shared)
        {
            for (int i = 0; i < data.Items.Count; i++)
            {
                XElement item = data.Items[i].ToXml(ns, date1904);
                if (data.Unused[i])
                {
                    item.SetAttributeValue("u", "1");
                }

                shared.Add(item);
            }
        }
    }
}

/// <summary>A pivot cache field: its element in the definition, its name and its shared items.</summary>
internal sealed class PivotCacheField
{
    public PivotCacheField(XElement element, bool date1904)
    {
        Element = element;
        Name = XlsxNames.Decode((string?)element.Attribute("name") ?? "");
        Database = XlsxNames.Bool((string?)element.Attribute("databaseField"), true)
            && element.Attribute("formula") is null && element.Element(element.Name.Namespace + "fieldGroup") is null;
        NumberFormatId = XlsxNames.Int((string?)element.Attribute("numFmtId"), 0);
        XElement? shared = element.Element(element.Name.Namespace + "sharedItems");
        foreach (XElement item in shared?.Elements() ?? [])
        {
            if (PivotValue.Read(item, date1904) is { } value)
            {
                Items.Add(value);
            }
        }

        HasItems = Items.Count > 0;
    }

    public XElement Element { get; }

    public string Name { get; }

    /// <summary>Whether the source has the field; a calculated or grouped field does not.</summary>
    public bool Database { get; }

    public int NumberFormatId { get; }

    /// <summary>The shared items, which pivot fields and records number from 0.</summary>
    public List<PivotValue> Items { get; } = [];

    /// <summary>Whether the field shares its items, so records give an item's number.</summary>
    public bool HasItems { get; set; }
}

/// <summary>What a refresh read from the source: the values, record by record, and each field's items.</summary>
internal sealed class PivotCacheData(PivotValue[][] records, string[] names, int[]? oldFields)
{
    public PivotValue[][] Records { get; } = records;

    /// <summary>The fields' names, from the source's header row.</summary>
    public string[] Names { get; } = names;

    /// <summary>
    /// For each field, the cache's field it was before, or -1 for a new one; null when the fields are the cache's own.
    /// </summary>
    public int[]? OldFields { get; } = oldFields;

    public List<PivotFieldData> Fields { get; } = [];
}

/// <summary>
/// A field's items after a refresh: <see cref="Index"/> gives each record's item; <see cref="Remap"/> where each item
/// before the refresh went, -1 when it was dropped.
/// </summary>
internal sealed record PivotFieldData(bool Shared, List<PivotValue> Items, List<bool> Unused, int[] Index, int[] Remap);
