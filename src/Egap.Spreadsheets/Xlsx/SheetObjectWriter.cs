using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents.Charts;

using Egap.Documents.Docx;
using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>What the sheet object writer needs from the package writer: a way to add parts and to find free part names.</summary>
internal sealed class SheetObjectParts(Func<string, bool> exists, Action<string, string, byte[], List<OpcRelationship>> add)
{
    private readonly HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string name, string contentType, byte[] data, List<OpcRelationship> relationships)
    {
        taken.Add(name);
        add(name, contentType, data, relationships);
    }

    /// <summary>The first <c>stemN.extension</c> no part has.</summary>
    public string FreeName(string stem, string extension)
    {
        for (int i = 1; ; i++)
        {
            string name = stem + XlsxNames.Integer(i) + "." + extension;
            if (!exists(name) && !taken.Contains(name))
            {
                taken.Add(name);
                return name;
            }
        }
    }
}

/// <summary>What the writer shares between sheets: table ids and the differential formats conditional formatting adds.</summary>
internal sealed class SheetObjectState(Workbook book)
{
    private readonly Dictionary<CellStyle, int> added = [];
    private int nextTableId = book.Worksheets.SelectMany(s => s.Tables)
        .Select(t => t.Source is { } source ? XlsxNames.Int((string?)source.Element.Attribute("id"), 0) : 0).DefaultIfEmpty(0).Max() + 1;

    /// <summary>The differential formats to add after the style part's own.</summary>
    public List<XElement> NewDifferentials { get; } = [];

    public int NextTableId() => nextTableId++;

    /// <summary>The dxf index of a style: the file's own while it is unchanged, else a new one.</summary>
    public int Differential(ConditionalFormatRule rule)
    {
        StyleTable styles = book.Styles;
        if (rule.ReadStyle is { } read && Equals(read.Style, rule.Style) && styles.Source is not null && read.Index < styles.DifferentialStyles.Count)
        {
            return read.Index;
        }

        CellStyle style = rule.Style!;
        if (!added.TryGetValue(style, out int index))
        {
            index = (styles.Source is null ? 0 : styles.DifferentialStyles.Count) + NewDifferentials.Count;
            NewDifferentials.Add(XlsxStyles.WriteDifferential(style, styles));
            added.Add(style, index);
        }

        return index;
    }
}

/// <summary>
/// Writes what a worksheet holds besides its cells: the hyperlinks, data validation and conditional formatting elements,
/// and the table, drawing, comments and VML parts with the sheet's relationships to them. Parts whose objects did not
/// change since the file was read keep their bytes.
/// </summary>
internal static class SheetObjectWriter
{
    private static readonly XNamespace Ns = XlsxNames.Main;
    private static readonly XNamespace Xdr = SheetObjectReader.Xdr;
    private static readonly XNamespace A = SheetObjectReader.A;
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    private static readonly XNamespace X = "urn:schemas-microsoft-com:office:excel";

    private const string TableType = "application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml";
    private const string CommentsType = "application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml";
    private const string VmlType = "application/vnd.openxmlformats-officedocument.vmlDrawing";
    private const string DrawingType = "application/vnd.openxmlformats-officedocument.drawing+xml";

    /// <summary>A snapshot of what the model holds of an object, compared at save time to tell whether it changed.</summary>
    public static string State(SheetTable table)
    {
        StringBuilder text = new();
        text.Append(table.Name).Append('|').Append(table.Range).Append('|').Append(table.ShowHeaderRow).Append(table.ShowTotalsRow)
            .Append(table.ShowFilterButtons).Append(table.StyleName).Append('|').Append(table.ShowRowStripes).Append(table.ShowColumnStripes)
            .Append(table.ShowFirstColumn).Append(table.ShowLastColumn);
        foreach (SheetTableColumn column in table.Columns)
        {
            text.Append('|').Append(column.Name).Append('\u0001').Append(column.TotalsRowFunction).Append('\u0001').Append(column.TotalsRowLabel)
                .Append('\u0001').Append(column.TotalsRowFormula).Append('\u0001').Append(column.CalculatedFormula);
        }

        return text.ToString();
    }

    public static string State(CellComment comment) =>
        string.Join('\u0001', comment.Cell.ToString(), comment.Author, comment.Text, comment.Visible ? "1" : "0");

    public static string State(SheetDrawing drawing) => string.Join('\u0001',
        drawing.AnchorKind, drawing.From, drawing.To, drawing.X, drawing.Y, drawing.Width, drawing.Height, drawing.Name, drawing.Description, drawing.ImageChanged);

    /// <summary>
    /// Writes the sheet's objects: the parts go to <paramref name="parts"/>, the sheet's relationships are updated in
    /// <paramref name="relationships"/>, and the worksheet elements come back by name for the sheet writer to place.
    /// </summary>
    public static List<(string Name, XElement Element)> Write(Worksheet sheet, int sheetNumber, string sheetPart, List<OpcRelationship> relationships, SheetObjectParts parts, SheetObjectState state)
    {
        List<(string, XElement)> elements = [];
        HashSet<string> ids = [.. relationships.Select(r => r.Id)];
        string NextId()
        {
            for (int i = 1; ; i++)
            {
                string id = "rId" + XlsxNames.Integer(i);
                if (ids.Add(id))
                {
                    return id;
                }
            }
        }

        elements.AddRange(ConditionalFormatting(sheet, state).Select(e => ("conditionalFormatting", e)));
        if (DataValidations(sheet) is { } validations)
        {
            elements.Add(("dataValidations", validations));
        }

        if (Hyperlinks(sheet, relationships, NextId) is { } links)
        {
            elements.Add(("hyperlinks", links));
        }

        if (Drawing(sheet, sheetPart, relationships, parts, NextId) is { } drawing)
        {
            elements.Add(("drawing", drawing));
        }

        if (Comments(sheet, sheetNumber, sheetPart, relationships, parts, NextId) is { } legacy)
        {
            elements.Add(("legacyDrawing", legacy));
        }

        if (Tables(sheet, sheetPart, relationships, parts, state, NextId) is { } tables)
        {
            elements.Add(("tableParts", tables));
        }

        return elements;
    }

    /// <summary>A part name relative to the folder of <paramref name="from"/>, with <c>..</c> steps as needed.</summary>
    public static string Relative(string from, string to)
    {
        string[] source = from.Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1];
        string[] target = to.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < source.Length && common < target.Length - 1 && source[common].Equals(target[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        return string.Concat(Enumerable.Repeat("../", source.Length - common)) + string.Join('/', target[common..]);
    }

    private static string Attr(int value) => XlsxNames.Integer(value);

    private static string Flag(bool value) => value ? "1" : "0";

    private static XElement Patch(XElement? source, XName name) => source is null ? new XElement(name) : new XElement(source);

    /// <summary>
    /// Sets an attribute that holds an enum value, unless the model's value is still the one the file's attribute reads as:
    /// then the element, a copy of the file's, keeps the attribute as written, values the model does not know among them.
    /// </summary>
    private static void SetUnlessRead<T>(XElement element, XElement? source, string name, T value, Func<string?, T> read, string? written)
        where T : struct, Enum
    {
        if (source is not null && EqualityComparer<T>.Default.Equals(read((string?)source.Attribute(name)), value))
        {
            return;
        }

        element.SetAttributeValue(name, written);
    }

    private static string Sqref(IEnumerable<RangeAddress> ranges) => string.Join(' ', ranges.Select(r => r.ToString()));

    private static string Operator(ComparisonOperator value)
    {
        string name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    // Conditional formatting (18.3.1.18, 18.3.1.10).
    private static IEnumerable<XElement> ConditionalFormatting(Worksheet sheet, SheetObjectState state)
    {
        List<ConditionalFormatRule> rules = [.. sheet.ConditionalFormats.SelectMany(f => f.Rules)];
        HashSet<int> used = [];
        foreach (ConditionalFormatRule rule in rules)
        {
            if (rule.Priority > 0 && rules.Count(r => r.Priority == rule.Priority) == 1)
            {
                used.Add(rule.Priority);
            }
        }

        int next = used.Count == 0 ? 1 : used.Max() + 1;
        Dictionary<ConditionalFormatRule, int> priorities = [];
        foreach (ConditionalFormatRule rule in rules)
        {
            priorities[rule] = used.Contains(rule.Priority) && rule.Priority > 0 ? rule.Priority : next++;
        }

        foreach (ConditionalFormat format in sheet.ConditionalFormats)
        {
            if (format.Ranges.Count == 0 || format.Rules.Count == 0)
            {
                continue;
            }

            XElement element = format.Source is null ? new XElement(Ns + "conditionalFormatting") : new XElement(format.Source.Name, format.Source.Attributes());
            element.SetAttributeValue("sqref", Sqref(format.Ranges));
            foreach (ConditionalFormatRule rule in format.Rules)
            {
                element.Add(Rule(rule, priorities[rule], state));
            }

            if (format.Source is not null)
            {
                element.Add(format.Source.Elements().Where(e => e.Name.LocalName != "cfRule"));
            }

            yield return element;
        }
    }

    private static XElement Rule(ConditionalFormatRule rule, int priority, SheetObjectState state)
    {
        XElement element = Patch(rule.Source, Ns + "cfRule");
        if (rule.Source is null)
        {
            // A rule's type cannot change, so a file's rule keeps the type it was written with.
            element.SetAttributeValue("type", Array.Find(SheetObjectReader.RuleTypes, t => t.Type == rule.Type).Name);
        }

        element.SetAttributeValue("priority", Attr(priority));
        element.SetAttributeValue("stopIfTrue", rule.StopIfTrue ? "1" : null);
        element.SetAttributeValue("dxfId", rule.Style is null ? null : Attr(state.Differential(rule)));
        SetUnlessRead(element, rule.Source, "operator", rule.Operator, SheetObjectReader.Operator,
            rule.Type == ConditionalFormatType.CellIs || rule.Operator != ComparisonOperator.Between ? Operator(rule.Operator) : null);
        element.SetAttributeValue("text", rule.Text);
        XNamespace ns = element.Name.Namespace;
        element.Elements(ns + "formula").Remove();
        XElement[] formulas = [.. rule.Formulas.Take(3).Select(f => new XElement(ns + "formula", f))];
        if (element.HasElements)
        {
            element.AddFirst(formulas);
        }
        else
        {
            element.Add(formulas);
        }

        if (rule.Drawing is not null && rule.Source is null)
        {
            element.Add(new XElement(rule.Drawing));
        }

        return element;
    }

    // Data validation (18.3.1.32, 18.3.1.33).
    private static XElement? DataValidations(Worksheet sheet)
    {
        DataValidationCollection rules = sheet.DataValidations;
        List<DataValidation> written = [.. rules.Where(r => r.Ranges.Count > 0)];
        if (written.Count == 0)
        {
            return null;
        }

        XElement element = rules.Source is null ? new XElement(Ns + "dataValidations") : new XElement(rules.Source);
        element.SetAttributeValue("count", Attr(written.Count));
        foreach (DataValidation rule in written)
        {
            XElement item = Patch(rule.Source, Ns + "dataValidation");
            XNamespace ns = item.Name.Namespace;
            SetUnlessRead(item, rule.Source, "type", rule.Type, SheetObjectReader.ValidationType, rule.Type switch
            {
                DataValidationType.WholeNumber => "whole",
                DataValidationType.DecimalNumber => "decimal",
                DataValidationType.List => "list",
                DataValidationType.Date => "date",
                DataValidationType.Time => "time",
                DataValidationType.TextLength => "textLength",
                DataValidationType.Custom => "custom",
                _ => null,
            });
            SetUnlessRead(item, rule.Source, "errorStyle", rule.ErrorStyle, SheetObjectReader.ErrorStyle, rule.ErrorStyle switch
            {
                DataValidationErrorStyle.Warning => "warning",
                DataValidationErrorStyle.Information => "information",
                _ => null,
            });
            SetUnlessRead(item, rule.Source, "operator", rule.Operator, SheetObjectReader.Operator, rule.Operator == ComparisonOperator.Between ? null : Operator(rule.Operator));
            item.SetAttributeValue("allowBlank", rule.AllowBlank ? "1" : null);
            item.SetAttributeValue("showDropDown", rule.ShowDropDown ? null : "1");
            item.SetAttributeValue("showInputMessage", rule.ShowInputMessage ? "1" : null);
            item.SetAttributeValue("showErrorMessage", rule.ShowErrorMessage ? "1" : null);
            item.SetAttributeValue("errorTitle", rule.ErrorTitle);
            item.SetAttributeValue("error", rule.Error);
            item.SetAttributeValue("promptTitle", rule.PromptTitle);
            item.SetAttributeValue("prompt", rule.Prompt);
            item.SetAttributeValue("sqref", Sqref(rule.Ranges));
            item.Elements(ns + "formula1").Remove();
            item.Elements(ns + "formula2").Remove();
            XElement[] formulas = [.. new[] { ("formula1", rule.Formula1), ("formula2", rule.Formula2) }
                .Where(f => !string.IsNullOrEmpty(f.Item2)).Select(f => new XElement(ns + f.Item1, f.Item2))];
            item.AddFirst(formulas);
            element.Add(item);
        }

        return element;
    }

    // Hyperlinks (18.3.1.47).
    private static XElement? Hyperlinks(Worksheet sheet, List<OpcRelationship> relationships, Func<string> nextId)
    {
        HashSet<string> kept = [];
        XElement? element = null;
        if (sheet.Hyperlinks.Count > 0)
        {
            element = sheet.Source?.Hyperlinks is { } source ? new XElement(source) : new XElement(Ns + "hyperlinks");
            foreach (Hyperlink link in sheet.Hyperlinks)
            {
                XElement item = Patch(link.Source, Ns + "hyperlink");
                item.SetAttributeValue("ref", link.Range.ToString());
                string? id = null;
                if (link.Address is { Length: > 0 } address)
                {
                    if (link.Relationship is { } read && read.Target == address && kept.Add(read.Id))
                    {
                        id = read.Id;
                    }
                    else
                    {
                        id = nextId();
                        kept.Add(id);
                        relationships.Add(new OpcRelationship(id, SheetObjectReader.HyperlinkRelationship, address, true, address));
                    }
                }

                item.SetAttributeValue(XlsxNames.R + "id", id);
                item.SetAttributeValue("location", link.Location);
                item.SetAttributeValue("display", link.Display);
                item.SetAttributeValue("tooltip", link.ToolTip);
                element.Add(item);
            }
        }

        relationships.RemoveAll(r => XlsxNames.SameType(r.Type, SheetObjectReader.HyperlinkRelationship) && !kept.Contains(r.Id));
        return element;
    }

    // Tables (18.5).
    private static XElement? Tables(Worksheet sheet, string sheetPart, List<OpcRelationship> relationships, SheetObjectParts parts, SheetObjectState state, Func<string> nextId)
    {
        HashSet<string> kept = [];
        XElement? element = null;
        if (sheet.Tables.Count > 0)
        {
            element = sheet.Source?.TableParts is { } source ? new XElement(source) : new XElement(Ns + "tableParts");
            element.SetAttributeValue("count", Attr(sheet.Tables.Count));
            foreach (SheetTable table in sheet.Tables)
            {
                table.SyncColumns();
                string id;
                if (table.Source is { } read && table.RelationshipId is { } readId && relationships.Any(r => r.Id == readId))
                {
                    id = readId;
                    if (State(table) != table.ReadState)
                    {
                        parts.Add(read.PartName, TableType, OpcPackageWriter.Serialize(new XDocument(TableXml(table, null))), read.Relationships);
                    }
                }
                else
                {
                    id = nextId();
                    string name = parts.FreeName("/xl/tables/table", "xml");
                    parts.Add(name, TableType, OpcPackageWriter.Serialize(new XDocument(TableXml(table, state.NextTableId()))), table.Source?.Relationships ?? []);
                    relationships.Add(new OpcRelationship(id, SheetObjectReader.TableRelationship, Relative(sheetPart, name), false, name));
                }

                kept.Add(id);
                element.Add(new XElement(Ns + "tablePart", new XAttribute(XlsxNames.R + "id", id)));
            }
        }

        relationships.RemoveAll(r => XlsxNames.SameType(r.Type, SheetObjectReader.TableRelationship) && !kept.Contains(r.Id));
        return element;
    }

    private static XElement TableXml(SheetTable table, int? newId)
    {
        XElement? source = table.Source?.Element;
        XNamespace ns = source?.Name.Namespace ?? Ns;
        XElement root = source is null ? new XElement(Ns + "table") : new XElement(source.Name, source.Attributes());
        if (newId is { } id)
        {
            root.SetAttributeValue("id", Attr(id));
        }

        root.SetAttributeValue("name", table.Name);
        root.SetAttributeValue("displayName", table.Name);
        root.SetAttributeValue("ref", table.Range.ToString());
        root.SetAttributeValue("headerRowCount", table.ShowHeaderRow ? null : "0");
        root.SetAttributeValue("totalsRowCount", table.ShowTotalsRow ? "1" : null);
        root.SetAttributeValue("totalsRowShown", table.ShowTotalsRow ? null : (string?)source?.Attribute("totalsRowShown") ?? "0");

        RangeAddress range = table.Range;
        RangeAddress data = table.ShowTotalsRow && range.RowCount > 1 ? new RangeAddress(range.First, new CellAddress(range.Last.Row - 1, range.Last.Column)) : range;
        if (table.ShowHeaderRow && table.ShowFilterButtons)
        {
            XElement filter = source?.Element(ns + "autoFilter") is { } old ? new XElement(old) : new XElement(ns + "autoFilter");
            filter.SetAttributeValue("ref", data.ToString());
            root.Add(filter);
        }

        if (source?.Element(ns + "sortState") is { } sort)
        {
            root.Add(new XElement(sort));
        }

        XElement columns = new(ns + "tableColumns", new XAttribute("count", Attr(table.Columns.Count)));
        HashSet<int> usedIds = [.. table.Columns.Select(c => XlsxNames.Int((string?)c.Source?.Attribute("id"), 0)).Where(i => i > 0)];
        int nextColumn = 1;
        HashSet<int> written = [];
        foreach (SheetTableColumn column in table.Columns)
        {
            XElement item = column.Source is null ? new XElement(ns + "tableColumn") : new XElement(column.Source.Name, column.Source.Attributes());
            int columnId = XlsxNames.Int((string?)column.Source?.Attribute("id"), 0);
            if (columnId <= 0 || !written.Add(columnId))
            {
                while (usedIds.Contains(nextColumn) || written.Contains(nextColumn))
                {
                    nextColumn++;
                }

                columnId = nextColumn;
                written.Add(columnId);
            }

            item.SetAttributeValue("id", Attr(columnId));
            item.SetAttributeValue("name", XlsxNames.Encode(column.Name));
            SetUnlessRead(item, column.Source, "totalsRowFunction", column.TotalsRowFunction, SheetObjectReader.TotalsFunction, column.TotalsRowFunction switch
            {
                TableTotalsFunction.None => null,
                TableTotalsFunction.CountNumbers => "countNums",
                TableTotalsFunction.StandardDeviation => "stdDev",
                TableTotalsFunction.Variance => "var",
                _ => Operatorish(column.TotalsRowFunction),
            });
            item.SetAttributeValue("totalsRowLabel", column.TotalsRowLabel);
            if (column.CalculatedFormula is { Length: > 0 } calculated)
            {
                item.Add(new XElement(ns + "calculatedColumnFormula", calculated));
            }

            if (column.TotalsRowFunction == TableTotalsFunction.Custom && column.TotalsRowFormula is { Length: > 0 } totals)
            {
                item.Add(new XElement(ns + "totalsRowFormula", totals));
            }

            if (column.Source is not null)
            {
                item.Add(column.Source.Elements().Where(e => e.Name.LocalName is not ("calculatedColumnFormula" or "totalsRowFormula")).Select(e => new XElement(e)));
            }

            columns.Add(item);
        }

        root.Add(columns);
        if (table.StyleName is not null || source?.Element(ns + "tableStyleInfo") is not null)
        {
            XElement style = source?.Element(ns + "tableStyleInfo") is { } old ? new XElement(old) : new XElement(ns + "tableStyleInfo");
            style.SetAttributeValue("name", table.StyleName);
            style.SetAttributeValue("showFirstColumn", Flag(table.ShowFirstColumn));
            style.SetAttributeValue("showLastColumn", Flag(table.ShowLastColumn));
            style.SetAttributeValue("showRowStripes", Flag(table.ShowRowStripes));
            style.SetAttributeValue("showColumnStripes", Flag(table.ShowColumnStripes));
            root.Add(style);
        }

        if (source?.Element(ns + "extLst") is { } extensions)
        {
            root.Add(new XElement(extensions));
        }

        return root;
    }

    private static string Operatorish(TableTotalsFunction value)
    {
        string name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    // Notes: the comments part (18.7) and the VML drawing that shows them.
    private static XElement? Comments(Worksheet sheet, int sheetNumber, string sheetPart, List<OpcRelationship> relationships, SheetObjectParts parts, Func<string> nextId)
    {
        SheetSource? source = sheet.Source;
        CellCommentCollection comments = sheet.Comments;
        bool changed = comments.Changed || comments.Any(c => c.ReadState is null || State(c) != c.ReadState);
        if (!changed)
        {
            return source?.LegacyDrawing;
        }

        // The comments part.
        OpcRelationship? commentsRel = source?.CommentsPart is { } oldComments ? relationships.Find(r => !r.External && XlsxNames.SameType(r.Type, SheetObjectReader.CommentsRelationship)) : null;
        if (comments.Count == 0)
        {
            relationships.RemoveAll(r => XlsxNames.SameType(r.Type, SheetObjectReader.CommentsRelationship));
        }
        else
        {
            List<string> authors = [];
            XElement list = new(Ns + "commentList");
            foreach (CellComment comment in comments.OrderBy(c => c.Cell.Row).ThenBy(c => c.Cell.Column))
            {
                int author = authors.IndexOf(comment.Author);
                if (author < 0)
                {
                    author = authors.Count;
                    authors.Add(comment.Author);
                }

                XElement text = comment.RichText is { } rich ? new XElement(Ns + "text", rich.Elements().Select(e => Rename(e))) : new XElement(Ns + "text", TextRun(comment.Text));
                list.Add(new XElement(Ns + "comment", new XAttribute("ref", comment.Cell.ToString()), new XAttribute("authorId", Attr(author)), new XAttribute("shapeId", "0"), text));
            }

            XElement root = new(Ns + "comments", new XElement(Ns + "authors", authors.Select(a => new XElement(Ns + "author", a))), list);
            string name = commentsRel?.TargetName ?? parts.FreeName("/xl/comments", "xml");
            parts.Add(name, CommentsType, OpcPackageWriter.Serialize(new XDocument(root)), source?.CommentsPart is { } kept ? [.. kept.Relationships] : []);
            if (commentsRel is null)
            {
                relationships.Add(new OpcRelationship(nextId(), SheetObjectReader.CommentsRelationship, Relative(sheetPart, name), false, name));
            }
        }

        // The VML drawing: the file's other shapes (form controls) stay, its note shapes are written again.
        XDocument? vml = source?.VmlPart is { } oldVml ? SafeXml.TryLoad(oldVml) : null;
        vml?.Root?.Elements(V + "shape").Where(IsNote).Remove();

        bool otherShapes = vml?.Root?.Elements(V + "shape").Any() == true;
        if (comments.Count == 0 && !otherShapes)
        {
            relationships.RemoveAll(r => XlsxNames.SameType(r.Type, SheetObjectReader.VmlRelationship) && r.Id == (string?)source?.LegacyDrawing?.Attribute(XlsxNames.R + "id"));
            return null;
        }

        vml ??= new XDocument(new XElement("xml", new XAttribute(XNamespace.Xmlns + "v", V.NamespaceName), new XAttribute(XNamespace.Xmlns + "o", O.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
            new XElement(O + "shapelayout", new XAttribute(V + "ext", "edit"), new XElement(O + "idmap", new XAttribute(V + "ext", "edit"), new XAttribute("data", Attr(sheetNumber))))));
        XElement vmlRoot = vml.Root!;
        if (comments.Count > 0 && !vmlRoot.Elements(V + "shapetype").Any(t => (string?)t.Attribute("id") == "_x0000_t202"))
        {
            vmlRoot.Add(new XElement(V + "shapetype", new XAttribute("id", "_x0000_t202"), new XAttribute("coordsize", "21600,21600"), new XAttribute(O + "spt", "202"),
                new XAttribute("path", "m,l,21600r21600,l21600,xe"), new XElement(V + "stroke", new XAttribute("joinstyle", "miter")),
                new XElement(V + "path", new XAttribute("gradientshapeok", "t"), new XAttribute(O + "connecttype", "rect"))));
        }

        int shapeId = Math.Max(sheetNumber * 1024, vmlRoot.Elements(V + "shape").Select(s => ShapeNumber((string?)s.Attribute("id"))).DefaultIfEmpty(0).Max()) + 1;
        foreach (CellComment comment in comments)
        {
            vmlRoot.Add(NoteShape(comment, shapeId++));
        }

        OpcRelationship? vmlRel = source?.LegacyDrawing is { } legacy && (string?)legacy.Attribute(XlsxNames.R + "id") is { } legacyId ? relationships.Find(r => r.Id == legacyId) : null;
        string vmlName = vmlRel?.TargetName ?? parts.FreeName("/xl/drawings/vmlDrawing", "vml");
        using MemoryStream output = new();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true }))
        {
            vml.Save(writer);
        }

        parts.Add(vmlName, VmlType, output.ToArray(), source?.VmlPart is { } keptVml ? [.. keptVml.Relationships] : []);
        string id = vmlRel?.Id ?? nextId();
        if (vmlRel is null)
        {
            relationships.Add(new OpcRelationship(id, SheetObjectReader.VmlRelationship, Relative(sheetPart, vmlName), false, vmlName));
        }

        return new XElement(Ns + "legacyDrawing", new XAttribute(XlsxNames.R + "id", id));
    }

    private static bool IsNote(XElement shape) => (string?)shape.Element(X + "ClientData")?.Attribute("ObjectType") == "Note";

    private static int ShapeNumber(string? id) =>
        id is not null && id.StartsWith("_x0000_s", StringComparison.Ordinal) && int.TryParse(id.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// <summary>A rich text run's element in the main namespace, for text read from a strict file.</summary>
    private static XElement Rename(XElement element) =>
        new(element.Name.NamespaceName == XlsxNames.StrictMainUri ? Ns + element.Name.LocalName : element.Name, element.Attributes(), element.Nodes().Select(n => n is XElement e ? Rename(e) : n));

    private static XElement TextRun(string text)
    {
        XElement t = new(Ns + "t", XlsxNames.Encode(text));
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n', StringComparison.Ordinal)))
        {
            t.Add(new XAttribute(XNamespace.Xml + "space", "preserve"));
        }

        return t;
    }

    private static XElement NoteShape(CellComment comment, int id)
    {
        int row = comment.Cell.Row;
        int column = comment.Cell.Column;
        string anchor = string.Join(", ", new[] { column + 1, 15, Math.Max(row - 1, 0), 2, column + 3, 15, Math.Max(row - 1, 0) + 4, 16 }.Select(Attr));
        XElement data = new(X + "ClientData", new XAttribute("ObjectType", "Note"),
            new XElement(X + "MoveWithCells"), new XElement(X + "SizeWithCells"), new XElement(X + "Anchor", anchor),
            new XElement(X + "AutoFill", "False"), new XElement(X + "Row", Attr(row)), new XElement(X + "Column", Attr(column)));
        if (comment.Visible)
        {
            data.Add(new XElement(X + "Visible"));
        }

        return new XElement(V + "shape", new XAttribute("id", "_x0000_s" + Attr(id)), new XAttribute("type", "#_x0000_t202"),
            new XAttribute("style", "position:absolute;margin-left:59.25pt;margin-top:1.5pt;width:108pt;height:59.25pt;z-index:1;visibility:" + (comment.Visible ? "visible" : "hidden")),
            new XAttribute("fillcolor", "#ffffe1"), new XAttribute(O + "insetmode", "auto"),
            new XElement(V + "fill", new XAttribute("color2", "#ffffe1")),
            new XElement(V + "shadow", new XAttribute("on", "t"), new XAttribute("color", "black"), new XAttribute("obscured", "t")),
            new XElement(V + "path", new XAttribute(O + "connecttype", "none")),
            new XElement(V + "textbox", new XAttribute("style", "mso-direction-alt:auto"), new XElement("div", new XAttribute("style", "text-align:left"))),
            data);
    }

    // The drawing layer (20.5).
    private static XElement? Drawing(Worksheet sheet, string sheetPart, List<OpcRelationship> relationships, SheetObjectParts parts, Func<string> nextId)
    {
        SheetSource? source = sheet.Source;
        SheetDrawingCollection drawings = sheet.Drawings;
        foreach (SheetDrawing edited in drawings)
        {
            if (edited.LoadedChart is { IsChanged: true, IsNew: false } chart && edited.ChartPart is { } chartPart)
            {
                ChartCells.Fill(chart, sheet.Workbook);
                parts.Add(chartPart.Name, chartPart.ContentType, chart.ToBytes(), [.. chartPart.Relationships]);
            }
        }

        bool changed = drawings.Changed || drawings.Any(d => d.ReadState is null || State(d) != d.ReadState);
        if (!changed)
        {
            return source?.Drawing?.Element;
        }

        OpcRelationship? drawingRel = source?.Drawing is { } read && (string?)read.Element.Attribute(XlsxNames.R + "id") is { } readId ? relationships.Find(r => r.Id == readId) : null;
        if (drawings.Count == 0)
        {
            if (drawingRel is not null)
            {
                relationships.Remove(drawingRel);
            }

            return null;
        }

        XElement? oldRoot = source?.Drawing?.Part.LoadXml().Root;
        List<OpcRelationship> drawingRels = source?.Drawing is { } part ? [.. part.Part.Relationships] : [];
        HashSet<string> ids = [.. drawingRels.Select(r => r.Id)];
        string NextImageId()
        {
            for (int i = 1; ; i++)
            {
                string id = "rId" + XlsxNames.Integer(i);
                if (ids.Add(id))
                {
                    return id;
                }
            }
        }

        string drawingName = drawingRel?.TargetName ?? parts.FreeName("/xl/drawings/drawing", "xml");
        XElement root = oldRoot is null ? new XElement(Xdr + "wsDr", new XAttribute(XNamespace.Xmlns + "xdr", Xdr.NamespaceName), new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName))
            : new XElement(oldRoot.Name, oldRoot.Attributes());
        int shapeId = 1 + (oldRoot?.Descendants(Xdr + "cNvPr").Select(p => XlsxNames.Int((string?)p.Attribute("id"), 0)).DefaultIfEmpty(1).Max() ?? 1);
        foreach (SheetDrawing drawing in drawings)
        {
            string? imageId = drawing.ImageRelationshipId;
            if (drawing.Kind == SheetDrawingKind.Picture && (drawing.ImageChanged || imageId is null) && drawing.ImageData is { } image)
            {
                string extension = drawing.ImageContentType switch
                {
                    "image/png" => "png",
                    "image/jpeg" => "jpeg",
                    "image/gif" => "gif",
                    "image/bmp" => "bmp",
                    "image/tiff" => "tiff",
                    "image/x-emf" => "emf",
                    _ => "wmf",
                };
                string media = parts.FreeName("/xl/media/image", extension);
                parts.Add(media, drawing.ImageContentType!, image, []);
                imageId = NextImageId();
                drawingRels.Add(new OpcRelationship(imageId, SheetObjectReader.ImageRelationship, Relative(drawingName, media), false, media));
            }

            if (drawing.Kind == SheetDrawingKind.Chart && drawing.Source is null && drawing.Chart is { } madeChart)
            {
                ChartCells.Fill(madeChart, sheet.Workbook);
                string chartName = madeChart.IsExtended ? parts.FreeName("/xl/charts/chartEx", "xml") : parts.FreeName("/xl/charts/chart", "xml");
                List<OpcRelationship> chartRels = [];
                if (madeChart.IsExtended)
                {
                    // Excel opens no workbook with a cx: chart that lacks these.
                    string styleName = parts.FreeName("/xl/charts/style", "xml");
                    parts.Add(styleName, Chart.StyleContentType, Chart.ExtendedStylePart(), []);
                    string colorsName = parts.FreeName("/xl/charts/colors", "xml");
                    parts.Add(colorsName, Chart.ColorsContentType, Chart.ExtendedColorsPart(), []);
                    chartRels.Add(new OpcRelationship("rId1", Chart.StyleRelationship, Relative(chartName, styleName), false, styleName));
                    chartRels.Add(new OpcRelationship("rId2", Chart.ColorsRelationship, Relative(chartName, colorsName), false, colorsName));
                }

                parts.Add(chartName, madeChart.IsExtended ? Chart.ExtendedContentType : Chart.ContentType, madeChart.ToBytes(), chartRels);
                imageId = NextImageId();
                drawingRels.Add(new OpcRelationship(imageId, madeChart.IsExtended ? Chart.ExtendedRelationship : Chart.Relationship, Relative(drawingName, chartName), false, chartName));
            }

            root.Add(Anchor(drawing, imageId, shapeId++));
        }

        // What the drawing held besides the anchors the model reads (anchors in markup-compatibility blocks, extensions) stays.
        root.Add(oldRoot?.Elements().Where(e => !(e.Name.Namespace == Xdr && e.Name.LocalName is "twoCellAnchor" or "oneCellAnchor" or "absoluteAnchor")).Select(e => new XElement(e)));

        // Relationships no anchor names any more (a removed chart's, a replaced image's) are dropped.
        HashSet<string> named = [.. root.Descendants().Attributes().Where(a => a.Name.Namespace == XlsxNames.R || XlsxNames.IsRelationships(a.Name.NamespaceName)).Select(a => a.Value)];
        drawingRels.RemoveAll(r => !named.Contains(r.Id));
        parts.Add(drawingName, DrawingType, OpcPackageWriter.Serialize(new XDocument(root)), drawingRels);
        string id = drawingRel?.Id ?? nextId();
        if (drawingRel is null)
        {
            relationships.Add(new OpcRelationship(id, SheetObjectReader.DrawingRelationship, Relative(sheetPart, drawingName), false, drawingName));
        }

        return new XElement(Ns + "drawing", new XAttribute(XlsxNames.R + "id", id));
    }

    private static XElement Anchor(SheetDrawing drawing, string? imageId, int shapeId)
    {
        string name = drawing.AnchorKind switch
        {
            DrawingAnchorKind.OneCell => "oneCellAnchor",
            DrawingAnchorKind.Absolute => "absoluteAnchor",
            _ => "twoCellAnchor",
        };
        XElement anchor = new(Xdr + name);
        if (drawing.Source is { } source && source.Name.LocalName == name)
        {
            anchor.Add(source.Attributes());
        }

        if (drawing.AnchorKind == DrawingAnchorKind.Absolute)
        {
            anchor.Add(new XElement(Xdr + "pos", new XAttribute("x", Long(drawing.X)), new XAttribute("y", Long(drawing.Y))));
        }
        else
        {
            anchor.Add(Marker("from", drawing.From));
        }

        if (drawing.AnchorKind == DrawingAnchorKind.TwoCell)
        {
            anchor.Add(Marker("to", drawing.To));
        }
        else
        {
            anchor.Add(new XElement(Xdr + "ext", new XAttribute("cx", Long(drawing.Width)), new XAttribute("cy", Long(drawing.Height))));
        }

        XElement? shape;
        if (drawing.Source is { } kept)
        {
            shape = SheetObjectReader.ObjectOf(kept) is { } found ? new XElement(found) : null;
            if (shape?.Descendants(Xdr + "cNvPr").FirstOrDefault() is { } properties)
            {
                properties.SetAttributeValue("name", drawing.Name);
                properties.SetAttributeValue("descr", drawing.Description);
            }

            if (drawing.ImageChanged && imageId is not null && shape?.Descendants(A + "blip").FirstOrDefault() is { } blip)
            {
                blip.SetAttributeValue(XlsxNames.R + "embed", imageId);
            }
        }
        else if (drawing.Kind == SheetDrawingKind.Chart)
        {
            shape = ChartFrame(drawing, imageId, shapeId);
        }
        else
        {
            shape = new XElement(Xdr + "pic",
                new XElement(Xdr + "nvPicPr",
                    new XElement(Xdr + "cNvPr", new XAttribute("id", Attr(shapeId)), new XAttribute("name", drawing.Name), drawing.Description is null ? null : new XAttribute("descr", drawing.Description)),
                    new XElement(Xdr + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", "1")))),
                new XElement(Xdr + "blipFill",
                    new XElement(A + "blip", new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri), new XAttribute(XlsxNames.R + "embed", imageId ?? "")),
                    new XElement(A + "stretch", new XElement(A + "fillRect"))),
                new XElement(Xdr + "spPr", new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
        }

        anchor.Add(shape);
        anchor.Add(drawing.Source?.Element(Xdr + "clientData") is { } client ? new XElement(client) : new XElement(Xdr + "clientData"));
        return anchor;
    }

    /// <summary>A new chart's frame; one of the newer types goes in the markup-compatibility block Excel writes for them.</summary>
    private static XElement ChartFrame(SheetDrawing drawing, string? chartId, int shapeId)
    {
        bool extended = drawing.Chart!.IsExtended;
        XNamespace chartNs = extended ? SheetObjectReader.Cx : SheetObjectReader.C;
        XElement frame = new(
            Xdr + "graphicFrame",
            new XAttribute("macro", ""),
            new XElement(
                Xdr + "nvGraphicFramePr",
                new XElement(Xdr + "cNvPr", new XAttribute("id", Attr(shapeId)), new XAttribute("name", drawing.Name), drawing.Description is null ? null : new XAttribute("descr", drawing.Description)),
                new XElement(Xdr + "cNvGraphicFramePr")),
            new XElement(Xdr + "xfrm", new XElement(A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")), new XElement(A + "ext", new XAttribute("cx", "0"), new XAttribute("cy", "0"))),
            new XElement(
                A + "graphic",
                new XElement(
                    A + "graphicData",
                    new XAttribute("uri", chartNs.NamespaceName),
                    new XElement(chartNs + "chart", new XAttribute(XNamespace.Xmlns + (extended ? "cx" : "c"), chartNs.NamespaceName), new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri), new XAttribute(XlsxNames.R + "id", chartId ?? "")))));
        if (!extended)
        {
            return frame;
        }

        XNamespace cx1 = "http://schemas.microsoft.com/office/drawing/2015/9/8/chartex";
        return new XElement(
            SheetObjectReader.Mc + "AlternateContent",
            new XAttribute(XNamespace.Xmlns + "mc", SheetObjectReader.Mc.NamespaceName),
            new XElement(SheetObjectReader.Mc + "Choice", new XAttribute(XNamespace.Xmlns + "cx1", cx1.NamespaceName), new XAttribute("Requires", "cx1"), frame),
            new XElement(
                SheetObjectReader.Mc + "Fallback",
                new XElement(
                    Xdr + "sp",
                    new XAttribute("macro", ""),
                    new XAttribute("textlink", ""),
                    new XElement(Xdr + "nvSpPr", new XElement(Xdr + "cNvPr", new XAttribute("id", "0"), new XAttribute("name", "")), new XElement(Xdr + "cNvSpPr")),
                    new XElement(Xdr + "spPr", new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))),
                    new XElement(
                        Xdr + "txBody",
                        new XElement(A + "bodyPr"),
                        new XElement(A + "lstStyle"),
                        new XElement(A + "p", new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", "en-US")), new XElement(A + "t", "This chart isn't available in your version of Excel.")))))));
    }

    private static XElement Marker(string name, DrawingPosition position) => new(Xdr + name,
        new XElement(Xdr + "col", Attr(position.Cell.Column)), new XElement(Xdr + "colOff", Long(position.OffsetX)),
        new XElement(Xdr + "row", Attr(position.Cell.Row)), new XElement(Xdr + "rowOff", Long(position.OffsetY)));

    private static string Long(long value) => value.ToString(CultureInfo.InvariantCulture);
}
