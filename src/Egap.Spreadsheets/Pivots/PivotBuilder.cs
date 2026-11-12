using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Pivots;

/// <summary>
/// Makes a new pivot table's parts as Excel makes them for a new pivot table: a cache definition that names the source and its
/// fields, an empty records part, and a table definition with the fields on their axes. A refresh then reads the source and
/// lays the table out (ECMA-376 Part 1, 18.10).
/// </summary>
internal static class PivotBuilder
{
    public const string TableType = "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotTable+xml";
    public const string CacheDefinitionType = "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotCacheDefinition+xml";
    public const string CacheRecordsType = "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotCacheRecords+xml";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string X14 = "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main";

    private static readonly XNamespace Ns = XlsxNames.Main;

    /// <summary>The names a source's header row gives its fields, as Excel names them: the header text, spaces kept, a repeated one numbered.</summary>
    /// <exception cref="ArgumentException">A header cell is empty, or the source has no rows under its header row.</exception>
    public static string[] FieldNames(Worksheet sheet, RangeAddress range)
    {
        if (range.RowCount < 2)
        {
            throw new ArgumentException("A pivot table's source needs a header row and at least one row under it.", nameof(range));
        }

        string[] names = new string[range.ColumnCount];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < names.Length; c++)
        {
            Cell header = sheet[range.First.Row, range.First.Column + c];
            string text = PivotCache.HeaderText(header);
            if (text.Trim().Length == 0)
            {
                throw new ArgumentException($"The source's header cell {header.Address} is empty; every column of a pivot table's source needs a name.", nameof(range));
            }

            string name = text;
            for (int n = 2; !seen.Add(name); n++)
            {
                name = text + n.ToString(CultureInfo.InvariantCulture);
            }

            names[c] = name;
        }

        return names;
    }

    /// <summary>The cache definition: the source, and a field for each column with the number format of its first value.</summary>
    public static XDocument CacheDefinition(XElement worksheetSource, string[] names, Worksheet sheet, RangeAddress range, Workbook book)
    {
        XElement fields = new(Ns + "cacheFields", new XAttribute("count", XlsxNames.Integer(names.Length)));
        for (int c = 0; c < names.Length; c++)
        {
            string code = book.Styles[sheet.GetStyleIndex(range.First.Row + 1, range.First.Column + c)].NumberFormat;
            int id = code == "General" ? 0 : book.Styles.NumberFormats.Add(code);
            fields.Add(new XElement(Ns + "cacheField", new XAttribute("name", XlsxNames.Encode(names[c])), new XAttribute("numFmtId", XlsxNames.Integer(id)), new XElement(Ns + "sharedItems")));
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(Ns + "pivotCacheDefinition",
            new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri),
            new XAttribute(XlsxNames.R + "id", "rId1"),
            new XAttribute("refreshedDate", "0"),
            new XAttribute("createdVersion", "8"),
            new XAttribute("refreshedVersion", "8"),
            new XAttribute("minRefreshableVersion", "3"),
            new XAttribute("recordCount", "0"),
            new XElement(Ns + "cacheSource", new XAttribute("type", "worksheet"), worksheetSource),
            fields));
    }

    /// <summary>
    /// The table definition: the fields on their axes, the value fields with their names, functions and number formats, the
    /// layout's switches and the style. The location is the top left cell alone until a refresh lays the table out.
    /// </summary>
    /// <exception cref="ArgumentException">The options name a field the source does not have, put a field on two axes, or give a value field a name that is taken.</exception>
    public static XDocument TableDefinition(string name, int cacheId, string[] names, PivotTableOptions options, CellAddress anchor, Workbook book)
    {
        int Field(string field, string list)
        {
            int index = Array.FindIndex(names, n => n.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                // A header with spaces around it is found by its text alone too.
                index = Array.FindIndex(names, n => n.Trim().Equals(field?.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return index >= 0 ? index : throw new ArgumentException($"The source has no field named {field} for {list}; its fields are {string.Join(", ", names)}.", nameof(options));
        }

        List<int> rows = [.. options.Rows.Select(f => Field(f, "Rows"))];
        List<int> columns = [.. options.Columns.Select(f => Field(f, "Columns"))];
        List<int> pages = [.. options.Filters.Select(f => Field(f, "Filters"))];
        List<int> axes = [.. rows, .. columns, .. pages];
        if (axes.Distinct().Count() != axes.Count)
        {
            throw new ArgumentException("A field can be in only one of Rows, Columns and Filters, once.", nameof(options));
        }

        if (options.Values.Any(v => v is null))
        {
            throw new ArgumentException("Values holds a null field.", nameof(options));
        }

        if (rows.Count + columns.Count + options.Values.Count == 0)
        {
            throw new ArgumentException("A pivot table needs a field in Rows, Columns or Values.", nameof(options));
        }

        // The values' names: Excel's Sum of Revenue, with a number after a name taken, and none may be a field's name.
        List<XElement> dataFields = [];
        HashSet<string> dataNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (PivotValueField value in options.Values)
        {
            int field = Field(value.Field, "Values");
            string subtotal = Subtotal(value.Function);
            string dataName;
            if (value.Name is { } given)
            {
                dataName = given;
                if (dataName.Trim().Length == 0 || names.Contains(dataName, StringComparer.OrdinalIgnoreCase) || !dataNames.Add(dataName))
                {
                    throw new ArgumentException($"The value name {given} is empty, a field's name or taken; Excel refuses it.", nameof(options));
                }
            }
            else
            {
                string stem = PivotDefinition.FunctionWord(subtotal) + " of " + names[field];
                dataName = stem;
                for (int n = 2; !dataNames.Add(dataName) || names.Contains(dataName, StringComparer.OrdinalIgnoreCase); n++)
                {
                    dataName = stem + n.ToString(CultureInfo.InvariantCulture);
                }
            }

            int format = value.NumberFormat is { } code ? CheckedFormat(book, code) : 0;
            dataFields.Add(new XElement(Ns + "dataField",
                new XAttribute("name", XlsxNames.Encode(dataName)),
                new XAttribute("fld", XlsxNames.Integer(field)),
                subtotal == "sum" ? null : new XAttribute("subtotal", subtotal),
                new XAttribute("baseField", "0"),
                new XAttribute("baseItem", "0"),
                format == 0 ? null : new XAttribute("numFmtId", XlsxNames.Integer(format))));
        }

        PivotTableLayout layout = options.Layout;
        bool compact = layout == PivotTableLayout.Compact;
        bool outline = layout != PivotTableLayout.Tabular;
        XElement root = new(Ns + "pivotTableDefinition",
            new XAttribute("name", XlsxNames.Encode(name)),
            new XAttribute("cacheId", XlsxNames.Integer(cacheId)),
            options.ValuesOnRows && dataFields.Count > 1 ? new XAttribute("dataOnRows", "1") : null,
            new XAttribute("applyNumberFormats", "0"),
            new XAttribute("applyBorderFormats", "0"),
            new XAttribute("applyFontFormats", "0"),
            new XAttribute("applyPatternFormats", "0"),
            new XAttribute("applyAlignmentFormats", "0"),
            new XAttribute("applyWidthHeightFormats", "1"),
            new XAttribute("dataCaption", "Values"),
            new XAttribute("updatedVersion", "8"),
            new XAttribute("minRefreshableVersion", "3"),
            new XAttribute("useAutoFormatting", "1"),
            options.GrandTotalRow ? null : new XAttribute("rowGrandTotals", "0"),
            options.GrandTotalColumn ? null : new XAttribute("colGrandTotals", "0"),
            new XAttribute("itemPrintTitles", "1"),
            new XAttribute("createdVersion", "8"),
            new XAttribute("indent", "0"),
            compact ? null : new XAttribute("compact", "0"),
            outline ? new XAttribute("outline", "1") : null,
            outline ? new XAttribute("outlineData", "1") : null,
            compact ? null : new XAttribute("compactData", "0"),
            new XAttribute("multipleFieldFilters", "0"));
        root.Add(new XElement(Ns + "location", new XAttribute("ref", anchor.ToString()),
            new XAttribute("firstHeaderRow", "1"), new XAttribute("firstDataRow", "1"), new XAttribute("firstDataCol", "1"),
            pages.Count > 0 ? new XAttribute("rowPageCount", XlsxNames.Integer(pages.Count)) : null,
            pages.Count > 0 ? new XAttribute("colPageCount", "1") : null));

        XElement pivotFields = new(Ns + "pivotFields", new XAttribute("count", XlsxNames.Integer(names.Length)));
        for (int f = 0; f < names.Length; f++)
        {
            string? axis = rows.Contains(f) ? "axisRow" : columns.Contains(f) ? "axisCol" : pages.Contains(f) ? "axisPage" : null;
            pivotFields.Add(new XElement(Ns + "pivotField",
                axis is null ? null : new XAttribute("axis", axis),
                options.Values.Any(v => Field(v.Field, "Values") == f) ? new XAttribute("dataField", "1") : null,
                compact ? null : new XAttribute("compact", "0"),
                outline ? null : new XAttribute("outline", "0"),
                new XAttribute("showAll", "0")));
        }

        root.Add(pivotFields);

        // Two or more values take a place of their own, after the column fields or the row fields.
        List<int> rowList = [.. rows];
        List<int> columnList = [.. columns];
        if (dataFields.Count > 1)
        {
            (options.ValuesOnRows ? rowList : columnList).Add(PivotDefinition.DataField);
        }

        if (rowList.Count > 0)
        {
            root.Add(new XElement(Ns + "rowFields", new XAttribute("count", XlsxNames.Integer(rowList.Count)),
                rowList.Select(f => new XElement(Ns + "field", new XAttribute("x", XlsxNames.Integer(f))))));
        }

        if (columnList.Count > 0)
        {
            root.Add(new XElement(Ns + "colFields", new XAttribute("count", XlsxNames.Integer(columnList.Count)),
                columnList.Select(f => new XElement(Ns + "field", new XAttribute("x", XlsxNames.Integer(f))))));
        }

        if (pages.Count > 0)
        {
            root.Add(new XElement(Ns + "pageFields", new XAttribute("count", XlsxNames.Integer(pages.Count)),
                pages.Select(f => new XElement(Ns + "pageField", new XAttribute("fld", XlsxNames.Integer(f)), new XAttribute("hier", "-1")))));
        }

        if (dataFields.Count > 0)
        {
            root.Add(new XElement(Ns + "dataFields", new XAttribute("count", XlsxNames.Integer(dataFields.Count)), dataFields));
        }

        root.Add(new XElement(Ns + "pivotTableStyleInfo",
            options.StyleName is { Length: > 0 } style ? new XAttribute("name", XlsxNames.Encode(style)) : null,
            new XAttribute("showRowHeaders", options.ShowRowHeaders ? "1" : "0"),
            new XAttribute("showColHeaders", options.ShowColumnHeaders ? "1" : "0"),
            new XAttribute("showRowStripes", options.ShowRowStripes ? "1" : "0"),
            new XAttribute("showColStripes", options.ShowColumnStripes ? "1" : "0"),
            new XAttribute("showLastColumn", "1")));

        // Excel 2010 and later hide the values row of a table without column fields.
        XNamespace x14 = X14;
        root.Add(new XElement(Ns + "extLst", new XElement(Ns + "ext", new XAttribute("uri", "{962EF5D1-5CA2-4c93-8EF4-DBF5C05439D2}"), new XAttribute(XNamespace.Xmlns + "x14", X14),
            new XElement(x14 + "pivotTableDefinition", new XAttribute("hideValuesRow", "1"), new XAttribute(XNamespace.Xmlns + "xm", "http://schemas.microsoft.com/office/excel/2006/main")))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    /// <summary>The <c>subtotal</c> a data field names its function with.</summary>
    public static string Subtotal(PivotFunction function) => function switch
    {
        PivotFunction.Count => "count",
        PivotFunction.Average => "average",
        PivotFunction.Max => "max",
        PivotFunction.Min => "min",
        PivotFunction.Product => "product",
        PivotFunction.CountNumbers => "countNums",
        PivotFunction.StandardDeviation => "stdDev",
        PivotFunction.StandardDeviationPopulation => "stdDevp",
        PivotFunction.Variance => "var",
        PivotFunction.VariancePopulation => "varp",
        _ => "sum",
    };

    private static int CheckedFormat(Workbook book, string code)
    {
        if (code.Length == 0 || code.Length > 255)
        {
            throw new ArgumentException($"The number format {code} is empty or longer than Excel allows.", nameof(code));
        }

        _ = NumberFormatCode.Get(code);
        return book.Styles.NumberFormats.Add(code);
    }

    /// <summary>The parts as a package of their own, so the pivot model reads them as it reads an .xlsx file's.</summary>
    public static (OpcPackage Package, OpcPart Table) Package(XDocument cache, XDocument records, XDocument table, DocumentLimits limits)
    {
        using MemoryStream zip = new();
        using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, byte[] data)
            {
                using Stream stream = archive.CreateEntry(name.TrimStart('/'), CompressionLevel.Fastest).Open();
                stream.Write(data);
            }

            Entry("/xl/pivotCache/pivotCacheRecords1.xml", OpcPackageWriter.Serialize(records));
            Entry("/xl/pivotCache/pivotCacheDefinition1.xml", OpcPackageWriter.Serialize(cache));
            Entry("/xl/pivotCache/_rels/pivotCacheDefinition1.xml.rels", OpcPackageWriter.Serialize(RelationshipsDocument("rId1", Relationships + "pivotCacheRecords", "pivotCacheRecords1.xml")));
            Entry("/xl/pivotTables/pivotTable1.xml", OpcPackageWriter.Serialize(table));
            Entry("/xl/pivotTables/_rels/pivotTable1.xml.rels", OpcPackageWriter.Serialize(RelationshipsDocument("rId1", Relationships + "pivotCacheDefinition", "../pivotCache/pivotCacheDefinition1.xml")));
            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            Entry("/[Content_Types].xml", OpcPackageWriter.Serialize(new XDocument(new XElement(ct + "Types",
                new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(ct + "Override", new XAttribute("PartName", "/xl/pivotCache/pivotCacheRecords1.xml"), new XAttribute("ContentType", CacheRecordsType)),
                new XElement(ct + "Override", new XAttribute("PartName", "/xl/pivotCache/pivotCacheDefinition1.xml"), new XAttribute("ContentType", CacheDefinitionType)),
                new XElement(ct + "Override", new XAttribute("PartName", "/xl/pivotTables/pivotTable1.xml"), new XAttribute("ContentType", TableType))))));
        }

        zip.Position = 0;
        OpcPackage package = OpcPackage.Read(zip, limits);
        return (package, package.GetPart("/xl/pivotTables/pivotTable1.xml")!);
    }

    /// <summary>An empty records part.</summary>
    public static XDocument EmptyRecords() =>
        new(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(Ns + "pivotCacheRecords", new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri), new XAttribute("count", "0")));

    private static XDocument RelationshipsDocument(string id, string type, string target)
    {
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        return new XDocument(new XElement(pr + "Relationships", new XElement(pr + "Relationship", new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", target))));
    }
}
