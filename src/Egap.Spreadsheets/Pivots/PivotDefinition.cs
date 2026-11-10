using System.Globalization;
using System.Xml.Linq;

using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Pivots;

/// <summary>
/// A pivot table definition part as the layout needs it (ECMA-376 Part 1, 18.10.1.73): the fields and their items, the
/// row, column, page and data fields, and the switches that place them. The XML stays the source of truth: a refresh
/// changes the items, the location and the row and column items in place and keeps the rest.
/// </summary>
internal sealed class PivotDefinition
{
    /// <summary>The data fields' place in <see cref="RowFields"/> or <see cref="ColumnFields"/>.</summary>
    public const int DataField = -2;

    public PivotDefinition(XDocument document)
    {
        Document = document;
        Root = document.Root!;
        Ns = Root.Name.Namespace;
        Name = XlsxNames.Decode((string?)Root.Attribute("name") ?? "");
        CacheId = XlsxNames.Int((string?)Root.Attribute("cacheId"), -1);
        Location = Root.Element(Ns + "location");
        if (Location is null || !RangeAddress.TryParse((string?)Location.Attribute("ref"), out RangeAddress range))
        {
            Problem = "The pivot table has no location.";
        }
        else
        {
            Range = range;
        }

        foreach (XElement field in Root.Element(Ns + "pivotFields")?.Elements(Ns + "pivotField") ?? [])
        {
            Fields.Add(new PivotFieldDefinition(field));
        }

        RowFields = FieldList("rowFields");
        ColumnFields = FieldList("colFields");
        foreach (XElement page in Root.Element(Ns + "pageFields")?.Elements(Ns + "pageField") ?? [])
        {
            int field = XlsxNames.Int((string?)page.Attribute("fld"), -1);
            int item = XlsxNames.Int((string?)page.Attribute("item"), -1);
            PageFields.Add(new PageFieldDefinition(page, field, item < 0 ? null : item));
        }

        foreach (XElement data in Root.Element(Ns + "dataFields")?.Elements(Ns + "dataField") ?? [])
        {
            DataFields.Add(new DataFieldDefinition(data));
        }

        DataOnRows = Bool("dataOnRows", false);
        RowGrandTotals = Bool("rowGrandTotals", true);
        ColumnGrandTotals = Bool("colGrandTotals", true);
        CompactData = Bool("compactData", true);
        OutlineData = Bool("outlineData", false);
        GridDropZones = Bool("gridDropZones", false);
        DataCaption = XlsxNames.Decode((string?)Root.Attribute("dataCaption") ?? "Values");
        GrandTotalCaption = Text("grandTotalCaption");
        RowHeaderCaption = Text("rowHeaderCaption");
        ColumnHeaderCaption = Text("colHeaderCaption");
        ErrorCaption = Bool("showError", false) ? Text("errorCaption") ?? "" : null;
        MissingCaption = Bool("showMissing", true) ? Text("missingCaption") : null;

        // Excel puts the data fields on an axis of their own once there are two or more.
        if (DataFields.Count > 1 && !RowFields.Contains(DataField) && !ColumnFields.Contains(DataField))
        {
            List<int> axis = DataOnRows ? RowFields : ColumnFields;
            int position = XlsxNames.Int((string?)Root.Attribute("dataPosition"), axis.Count);
            axis.Insert(Math.Clamp(position, 0, axis.Count), DataField);
        }

        Problem ??= Check();
    }

    public XDocument Document { get; }

    public XElement Root { get; }

    public XNamespace Ns { get; }

    public string Name { get; }

    public int CacheId { get; }

    public XElement? Location { get; }

    public RangeAddress Range { get; set; }

    public List<PivotFieldDefinition> Fields { get; } = [];

    public List<int> RowFields { get; }

    public List<int> ColumnFields { get; }

    public List<PageFieldDefinition> PageFields { get; } = [];

    public List<DataFieldDefinition> DataFields { get; } = [];

    public bool DataOnRows { get; }

    /// <summary>Whether the table has a grand total row at the bottom, as Excel reads <c>rowGrandTotals</c>.</summary>
    public bool RowGrandTotals { get; }

    /// <summary>Whether the table has a grand total column at the right, as Excel reads <c>colGrandTotals</c>.</summary>
    public bool ColumnGrandTotals { get; }

    public bool CompactData { get; }

    public bool OutlineData { get; }

    /// <summary>The classic layout, with the fields' buttons in the grid.</summary>
    public bool GridDropZones { get; }

    public string DataCaption { get; }

    public string? GrandTotalCaption { get; }

    public string? RowHeaderCaption { get; }

    public string? ColumnHeaderCaption { get; }

    /// <summary>What a cell with an error shows instead, or null to show the error.</summary>
    public string? ErrorCaption { get; }

    /// <summary>What an empty cell shows, or null for nothing.</summary>
    public string? MissingCaption { get; }

    /// <summary>Why the table cannot be laid out again, or null when it can.</summary>
    public string? Problem { get; }

    private string? Check()
    {
        foreach (int field in RowFields.Concat(ColumnFields))
        {
            if (field != DataField && (uint)field >= (uint)Fields.Count)
            {
                return "The pivot table names a field it does not have.";
            }
        }

        if (PageFields.Any(p => (uint)p.Field >= (uint)Fields.Count) || DataFields.Any(d => (uint)d.Field >= (uint)Fields.Count))
        {
            return "The pivot table names a field it does not have.";
        }

        if (DataFields.Any(d => d.ShowDataAs is not "normal"))
        {
            return "The pivot table shows values as a share or a difference, which Egap does not work out.";
        }

        if (DataFields.Any(d => Function(d.Subtotal) is null))
        {
            return "The pivot table summarizes with a function Egap does not know.";
        }

        if (Fields.Any(f => f.Items.Any(i => i.Calculated)))
        {
            return "The pivot table has calculated items.";
        }

        if (Root.Elements().Any(e => e.Name.LocalName is "pivotHierarchies" or "rowHierarchiesUsage" or "colHierarchiesUsage"))
        {
            return "The pivot table reads an OLAP cube.";
        }

        if (RowFields.Count(f => f == DataField) + ColumnFields.Count(f => f == DataField) > 1)
        {
            return "The pivot table has its data fields on two axes.";
        }

        return null;
    }

    /// <summary>
    /// Follows the cache's fields when the source gains or loses columns: the pivot fields move with their cache fields, a
    /// new field gets a pivot field on no axis, and a field that is gone leaves the rows, columns, pages, values and formats
    /// that used it, as in Excel.
    /// </summary>
    /// <param name="document">The pivot table definition, changed in place.</param>
    /// <param name="oldOf">For each new field, the field it was, or -1.</param>
    public static void RemapFields(XDocument document, int[] oldOf)
    {
        XElement root = document.Root!;
        XNamespace ns = root.Name.Namespace;
        XElement? list = root.Element(ns + "pivotFields");
        List<XElement> before = list?.Elements(ns + "pivotField").ToList() ?? [];
        int[] newOf = new int[before.Count];
        Array.Fill(newOf, -1);
        for (int f = 0; f < oldOf.Length; f++)
        {
            if ((uint)oldOf[f] < (uint)newOf.Length)
            {
                newOf[oldOf[f]] = f;
            }
        }

        int Map(int old) => old < 0 ? old : old < newOf.Length ? newOf[old] : -1;

        bool compact = XlsxNames.Bool((string?)root.Attribute("compact"), true);
        bool outline = XlsxNames.Bool((string?)root.Attribute("outline"), false);
        List<XElement> after = [];
        for (int f = 0; f < oldOf.Length; f++)
        {
            if ((uint)oldOf[f] < (uint)before.Count)
            {
                after.Add(before[oldOf[f]]);
                continue;
            }

            XElement field = new(ns + "pivotField");
            if (!compact)
            {
                field.SetAttributeValue("compact", "0");
            }

            if (!outline)
            {
                field.SetAttributeValue("outline", "0");
            }

            field.SetAttributeValue("showAll", "0");
            after.Add(field);
        }

        if (list is not null)
        {
            list.RemoveNodes();
            list.Add(after);
            list.SetAttributeValue("count", XlsxNames.Integer(after.Count));
        }

        void Fields(string name, string element, string attribute)
        {
            if (root.Element(ns + name) is not { } fields)
            {
                return;
            }

            foreach (XElement field in fields.Elements(ns + element).ToList())
            {
                int moved = Map(XlsxNames.Int((string?)field.Attribute(attribute), -1));
                if (moved == -1)
                {
                    field.Remove();
                }
                else
                {
                    field.SetAttributeValue(attribute, XlsxNames.Integer(moved));
                }
            }

            fields.SetAttributeValue("count", XlsxNames.Integer(fields.Elements(ns + element).Count()));
        }

        Fields("rowFields", "field", "x");
        Fields("colFields", "field", "x");
        Fields("pageFields", "pageField", "fld");
        Fields("dataFields", "dataField", "fld");
        foreach (XElement data in root.Element(ns + "dataFields")?.Elements(ns + "dataField") ?? [])
        {
            int moved = Map(XlsxNames.Int((string?)data.Attribute("baseField"), 0));
            data.SetAttributeValue("baseField", XlsxNames.Integer(Math.Max(moved, 0)));
        }

        // With one value left, the values need no place of their own.
        if ((root.Element(ns + "dataFields")?.Elements(ns + "dataField").Count() ?? 0) < 2)
        {
            foreach (string axis in new[] { "rowFields", "colFields" })
            {
                if (root.Element(ns + axis) is { } fields)
                {
                    fields.Elements(ns + "field").Where(f => (string?)f.Attribute("x") == "-2").Remove();
                    fields.SetAttributeValue("count", XlsxNames.Integer(fields.Elements(ns + "field").Count()));
                }
            }
        }

        foreach (string name in new[] { "rowFields", "colFields", "pageFields", "dataFields" })
        {
            if (root.Element(ns + name) is { HasElements: false } empty)
            {
                empty.Remove();
            }
        }

        // Formats name fields by number too; one for a field that is gone goes.
        foreach (XElement element in root.Elements().Where(e => e.Name.LocalName is "formats" or "conditionalFormats" or "chartFormats").ToList())
        {
            foreach (XElement format in element.Elements().ToList())
            {
                bool gone = false;
                foreach (XElement reference in format.Descendants().Where(d => d.Name.LocalName is "pivotArea" or "reference"))
                {
                    if (reference.Attribute("field") is { } attribute
                        && int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int old) && old >= 0)
                    {
                        int moved = Map(old);
                        gone |= moved < 0;
                        attribute.Value = XlsxNames.Integer(Math.Max(moved, 0));
                    }
                }

                if (gone)
                {
                    format.Remove();
                }
            }

            element.SetAttributeValue("count", XlsxNames.Integer(element.Elements().Count()));
        }
    }

    private List<int> FieldList(string name) =>
        [.. Root.Element(Ns + name)?.Elements(Ns + "field").Select(f => XlsxNames.Int((string?)f.Attribute("x"), int.MinValue)) ?? []];

    private bool Bool(string name, bool fallback) => XlsxNames.Bool((string?)Root.Attribute(name), fallback);

    private string? Text(string name) => (string?)Root.Attribute(name) is { } text ? XlsxNames.Decode(text) : null;

    /// <summary>
    /// The summary function a data field or subtotal item names, or null for one Excel does not have. A subtotal item's
    /// <c>count</c> counts numbers and its <c>countA</c> counts values, as a data field's <c>countNums</c> and <c>count</c> do.
    /// </summary>
    public static PivotFunction? Function(string name) => name switch
    {
        "sum" => PivotFunction.Sum,
        "count" => PivotFunction.Count,
        "average" => PivotFunction.Average,
        "max" => PivotFunction.Max,
        "min" => PivotFunction.Min,
        "product" => PivotFunction.Product,
        "countNums" => PivotFunction.CountNumbers,
        "stdDev" => PivotFunction.StdDev,
        "stdDevp" => PivotFunction.StdDevP,
        "var" => PivotFunction.Var,
        "varp" => PivotFunction.VarP,
        _ => null,
    };

    /// <summary>The function a subtotal item's type names; null for <c>default</c>, which is the data field's own.</summary>
    public static PivotFunction? SubtotalFunction(string type) => type switch
    {
        "sum" => PivotFunction.Sum,
        "countA" => PivotFunction.Count,
        "avg" => PivotFunction.Average,
        "max" => PivotFunction.Max,
        "min" => PivotFunction.Min,
        "product" => PivotFunction.Product,
        "count" => PivotFunction.CountNumbers,
        "stdDev" => PivotFunction.StdDev,
        "stdDevP" => PivotFunction.StdDevP,
        "var" => PivotFunction.Var,
        "varP" => PivotFunction.VarP,
        _ => null,
    };

    /// <summary>The word Excel puts after an item in a subtotal of a function: <c>East Sum</c>, <c>East Count</c>.</summary>
    public static string SubtotalWord(string type) => type switch
    {
        "sum" => "Sum",
        "countA" => "Count",
        "avg" => "Average",
        "max" => "Max",
        "min" => "Min",
        "product" => "Product",
        "count" => "Count",
        "stdDev" => "StdDev",
        "stdDevP" => "StdDevp",
        "var" => "Var",
        "varP" => "Varp",
        _ => "Total",
    };

    /// <summary>The word a data field's default name starts with: <c>Sum</c> of Units.</summary>
    public static string FunctionWord(string subtotal) => subtotal switch
    {
        "count" => "Count",
        "average" => "Average",
        "max" => "Max",
        "min" => "Min",
        "product" => "Product",
        "countNums" => "Count",
        "stdDev" => "StdDev",
        "stdDevp" => "StdDevp",
        "var" => "Var",
        "varp" => "Varp",
        _ => "Sum",
    };
}

/// <summary>The summary functions of pivot table values (ECMA-376 Part 1, 18.18.67).</summary>
internal enum PivotFunction
{
    Sum,
    Count,
    Average,
    Max,
    Min,
    Product,
    CountNumbers,
    StdDev,
    StdDevP,
    Var,
    VarP,
}

/// <summary>A pivot field: where it is, its items and how it shows them.</summary>
internal sealed class PivotFieldDefinition
{
    private static readonly (string Attribute, string Type)[] CustomSubtotals =
    [
        ("sumSubtotal", "sum"), ("countASubtotal", "countA"), ("avgSubtotal", "avg"), ("maxSubtotal", "max"), ("minSubtotal", "min"),
        ("productSubtotal", "product"), ("countSubtotal", "count"), ("stdDevSubtotal", "stdDev"), ("stdDevPSubtotal", "stdDevP"),
        ("varSubtotal", "var"), ("varPSubtotal", "varP"),
    ];

    public PivotFieldDefinition(XElement element)
    {
        Element = element;
        XNamespace ns = element.Name.Namespace;
        Axis = (string?)element.Attribute("axis");
        Caption = (string?)element.Attribute("name") is { } name ? XlsxNames.Decode(name) : null;
        Compact = Bool("compact", true);
        Outline = Bool("outline", true);
        SubtotalTop = Bool("subtotalTop", true);
        ShowAll = Bool("showAll", true);
        InsertBlankRow = Bool("insertBlankRow", false);
        IncludeNewItemsInFilter = Bool("includeNewItemsInFilter", false);
        MultipleItemSelectionAllowed = Bool("multipleItemSelectionAllowed", false);
        SortType = (string?)element.Attribute("sortType") ?? "manual";
        NumberFormatId = element.Attribute("numFmtId") is { } id ? XlsxNames.Int((string?)id, 0) : null;
        SubtotalCaption = (string?)element.Attribute("subtotalCaption") is { } caption ? XlsxNames.Decode(caption) : null;
        // Custom subtotals take the place of the default one.
        foreach ((string attribute, string type) in CustomSubtotals)
        {
            if (Bool(attribute, false))
            {
                Subtotals.Add(type);
            }
        }

        if (Subtotals.Count == 0 && Bool("defaultSubtotal", true))
        {
            Subtotals.Add("default");
        }

        ItemsElement = element.Element(ns + "items");
        foreach (XElement item in ItemsElement?.Elements(ns + "item") ?? [])
        {
            Items.Add(new PivotItemDefinition(item));
        }
    }

    public XElement Element { get; }

    /// <summary><c>axisRow</c>, <c>axisCol</c>, <c>axisPage</c>, <c>axisValues</c> or null.</summary>
    public string? Axis { get; }

    public string? Caption { get; }

    public bool Compact { get; }

    public bool Outline { get; }

    public bool SubtotalTop { get; }

    public bool ShowAll { get; }

    public bool InsertBlankRow { get; }

    public bool IncludeNewItemsInFilter { get; }

    /// <summary>Whether a page field may show more than one item, and so says <c>(Multiple Items)</c> when it hides some.</summary>
    public bool MultipleItemSelectionAllowed { get; }

    public string SortType { get; }

    public int? NumberFormatId { get; }

    public string? SubtotalCaption { get; }

    /// <summary>The subtotals, by item type: <c>default</c> for the data field's own function, then custom ones.</summary>
    public List<string> Subtotals { get; } = [];

    public XElement? ItemsElement { get; set; }

    /// <summary>The items, those for values first and the subtotal items after, as the part lists them.</summary>
    public List<PivotItemDefinition> Items { get; } = [];

    /// <summary>The items for values, in the field's order.</summary>
    public IEnumerable<PivotItemDefinition> DataItems => Items.Where(i => i.Type == "data");

    private bool Bool(string name, bool fallback) => XlsxNames.Bool((string?)Element.Attribute(name), fallback);
}

/// <summary>An item of a pivot field: a shared item it shows, or a subtotal.</summary>
internal sealed class PivotItemDefinition(XElement element)
{
    public XElement Element { get; } = element;

    /// <summary><c>data</c> for an item of a value, or the subtotal or other type it is.</summary>
    public string Type { get; } = (string?)element.Attribute("t") ?? "data";

    /// <summary>The shared item, or -1.</summary>
    public int Shared { get; set; } = XlsxNames.Int((string?)element.Attribute("x"), -1);

    public bool Hidden => XlsxNames.Bool((string?)Element.Attribute("h"), false);

    public bool Missing => XlsxNames.Bool((string?)Element.Attribute("m"), false);

    public bool Calculated => XlsxNames.Bool((string?)Element.Attribute("f"), false);

    /// <summary>The caption the user gave the item, or null.</summary>
    public string? Caption => (string?)Element.Attribute("n") is { } n ? XlsxNames.Decode(n) : null;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Type} {Shared}");
}

/// <summary>A page field: the field and the item it shows, or null for all.</summary>
internal sealed class PageFieldDefinition(XElement element, int field, int? item)
{
    public XElement Element { get; } = element;

    public int Field { get; } = field;

    public int? Item { get; set; } = item;
}

/// <summary>A data field: the field it sums, how and its name.</summary>
internal sealed class DataFieldDefinition(XElement element)
{
    public XElement Element { get; } = element;

    public int Field { get; } = XlsxNames.Int((string?)element.Attribute("fld"), -1);

    public string? Name { get; } = (string?)element.Attribute("name") is { } name ? XlsxNames.Decode(name) : null;

    public string Subtotal { get; } = (string?)element.Attribute("subtotal") ?? "sum";

    public string ShowDataAs { get; } = (string?)element.Attribute("showDataAs") ?? "normal";

    public int NumberFormatId { get; } = XlsxNames.Int((string?)element.Attribute("numFmtId"), 0);
}
