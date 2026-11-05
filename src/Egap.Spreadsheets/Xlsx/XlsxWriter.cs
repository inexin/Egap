using System.Text;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>
/// Writes a <see cref="Workbook"/> as an XLSX, XLSM, XLTX or XLTM package. The workbook, worksheet, style and shared string
/// parts are written from the model; a loaded workbook's other parts are copied as they were, with their relationships, and
/// parts nothing points at any more (a removed sheet's, the calculation chain, macros in a file without them) are left out.
/// </summary>
internal static class XlsxWriter
{
    private const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string VbaProject = "http://schemas.microsoft.com/office/2006/relationships/vbaProject";

    public static void Write(Workbook book, Stream stream, WorkbookFormat format)
    {
        WorkbookSource? source = book.Source;
        if (book.Worksheets.Count == 0 && (source is null || source.KeptSheets.Count == 0))
        {
            throw new InvalidOperationException("A workbook file needs at least one sheet; add one before saving.");
        }

        Dictionary<string, OutPart> parts = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        void Add(OutPart part)
        {
            if (!parts.ContainsKey(part.Name))
            {
                order.Add(part.Name);
            }

            parts[part.Name] = part;
        }

        List<OpcRelationship> packageRels;
        string workbookName;
        List<OpcRelationship> workbookRels;
        if (source is not null)
        {
            foreach (OpcPart part in source.Package.Parts)
            {
                if (!part.IsRelationships && !part.Name.Equals("/[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                {
                    Add(new OutPart(part.Name, part.ContentType, part.Data, [.. part.Relationships]));
                }
            }

            packageRels = [.. source.Package.Relationships];
            workbookName = source.WorkbookPart.Name;
            bool macros = format is WorkbookFormat.Xlsm or WorkbookFormat.Xltm;
            workbookRels = [.. source.WorkbookPart.Relationships.Where(r =>
                !XlsxNames.SameType(r.Type, XlsxNames.WorksheetRelationship)
                && !XlsxNames.SameType(r.Type, XlsxNames.SharedStringsRelationship)
                && !XlsxNames.SameType(r.Type, XlsxNames.CalcChainRelationship)
                && !XlsxNames.SameType(r.Type, XlsxNames.StylesRelationship)
                && (macros || !XlsxNames.SameType(r.Type, VbaProject)))];
        }
        else
        {
            workbookName = "/xl/workbook.xml";
            packageRels = [new OpcRelationship("rId1", OfficeDocument, "xl/workbook.xml", false, workbookName)];
            workbookRels = [];
        }

        // The styles and shared strings parts keep their names; new ones go where Excel puts them.
        string stylesName = RelatedName(source, XlsxNames.StylesRelationship) ?? FreeName(parts, "/xl/styles.xml");
        string stringsName = RelatedName(source, XlsxNames.SharedStringsRelationship) ?? FreeName(parts, "/xl/sharedStrings.xml");
        HashSet<string> usedIds = [.. workbookRels.Select(r => r.Id)];
        foreach (Worksheet sheet in book.Worksheets)
        {
            if (sheet.Source is { } kept)
            {
                usedIds.Add(kept.RelationshipId);
            }
        }

        string NextId()
        {
            for (int i = 1; ; i++)
            {
                string id = "rId" + XlsxNames.Integer(i);
                if (usedIds.Add(id))
                {
                    return id;
                }
            }
        }

        // Worksheets.
        StringMap strings = new(book.SharedStrings);
        Dictionary<Worksheet, string> sheetIds = [];
        SheetObjectState objectState = new(book);
        SheetObjectParts objectParts = new(parts.ContainsKey, (name, type, data, rels) => Add(new OutPart(name, type, data, rels)));
        int sheetNumber = 0;
        foreach (Worksheet sheet in book.Worksheets)
        {
            string name;
            string id;
            List<OpcRelationship> rels;
            if (sheet.Source is { } kept)
            {
                name = kept.Part.Name;
                id = kept.RelationshipId;
                rels = [.. kept.Part.Relationships];
            }
            else
            {
                name = FreeSheetName(parts);
                id = NextId();
                rels = [];
            }

            sheetIds[sheet] = id;
            List<(string Name, XElement Element)> objects = SheetObjectWriter.Write(sheet, ++sheetNumber, name, rels, objectParts, objectState);
            Add(new OutPart(name, XlsxNames.WorksheetType, WorksheetPartWriter.Write(sheet, strings, objects), rels));
            workbookRels.Add(new OpcRelationship(id, XlsxNames.WorksheetRelationship, RelativeTarget(workbookName, name), false, name));
        }

        Add(new OutPart(stylesName, XlsxNames.StylesType, OpcPackageWriter.Serialize(XlsxStyles.Write(book.Styles, objectState.NewDifferentials)), []));
        workbookRels.Add(new OpcRelationship(NextId(), XlsxNames.StylesRelationship, RelativeTarget(workbookName, stylesName), false, stylesName));
        if (strings.Count > 0)
        {
            List<OpcRelationship> stringRels = parts.TryGetValue(stringsName, out OutPart? oldStrings) ? oldStrings.Relationships : [];
            Add(new OutPart(stringsName, XlsxNames.SharedStringsType, strings.Write(), stringRels));
            workbookRels.Add(new OpcRelationship(NextId(), XlsxNames.SharedStringsRelationship, RelativeTarget(workbookName, stringsName), false, stringsName));
        }

        Add(new OutPart(workbookName, XlsxNames.WorkbookType(format), OpcPackageWriter.Serialize(WorkbookPart(book, sheetIds)), workbookRels));

        // Everything the package's relationships still reach, in the order the parts came.
        HashSet<string> reached = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> next = new();
        foreach (OpcRelationship relationship in packageRels)
        {
            Reach(relationship, "/");
        }

        while (next.TryDequeue(out string? name))
        {
            foreach (OpcRelationship relationship in parts[name].Relationships)
            {
                Reach(relationship, name);
            }
        }

        void Reach(OpcRelationship relationship, string from)
        {
            if (relationship.External)
            {
                return;
            }

            string target = OpcPackage.ResolveTarget(from, relationship.Target);
            if (parts.ContainsKey(target) && reached.Add(target))
            {
                next.Enqueue(target);
            }
        }

        OpcPackageWriter writer = new();
        foreach (OpcRelationship relationship in packageRels)
        {
            writer.AddRelationship("/", relationship.Id, relationship.Type, relationship.Target, relationship.External);
        }

        foreach (string name in order)
        {
            if (!reached.Contains(name))
            {
                continue;
            }

            OutPart part = parts[name];
            writer.AddPart(part.Name, part.ContentType, part.Data);
            foreach (OpcRelationship relationship in part.Relationships)
            {
                if (relationship.External || reached.Contains(OpcPackage.ResolveTarget(part.Name, relationship.Target)))
                {
                    writer.AddRelationship(part.Name, relationship.Id, relationship.Type, relationship.Target, relationship.External);
                }
            }
        }

        writer.Save(stream);
    }

    private static string? RelatedName(WorkbookSource? source, string type) =>
        source?.WorkbookPart.Relationships.FirstOrDefault(r => !r.External && XlsxNames.SameType(r.Type, type))?.TargetName;

    private static string FreeName(Dictionary<string, OutPart> parts, string name) => parts.ContainsKey(name) ? FreeSheetName(parts, name[..^4]) : name;

    private static string FreeSheetName(Dictionary<string, OutPart> parts, string stem = "/xl/worksheets/sheet")
    {
        for (int i = 1; ; i++)
        {
            string name = stem + XlsxNames.Integer(i) + ".xml";
            if (!parts.ContainsKey(name))
            {
                return name;
            }
        }
    }

    /// <summary>A part name relative to the folder of <paramref name="from"/>, as relationships give targets.</summary>
    private static string RelativeTarget(string from, string to)
    {
        string folder = from[..(from.LastIndexOf('/') + 1)];
        return to.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? to[folder.Length..] : to;
    }

    private static XDocument WorkbookPart(Workbook book, Dictionary<Worksheet, string> sheetIds)
    {
        XNamespace ns = XlsxNames.Main;
        WorkbookSource? source = book.Source;
        XElement root = source is not null
            ? new XElement(source.WorkbookXml.Root!)
            : new XElement(ns + "workbook", new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri),
                new XElement(ns + "bookViews", new XElement(ns + "workbookView")),
                new XElement(ns + "calcPr", new XAttribute("calcId", "191029")));

        // The date system.
        XElement? workbookPr = root.Element(ns + "workbookPr");
        if (book.Date1904 || workbookPr?.Attribute("date1904") is not null)
        {
            if (workbookPr is null)
            {
                workbookPr = new XElement(ns + "workbookPr");
                WorkbookOrder.Place(root, workbookPr);
            }

            workbookPr.SetAttributeValue("date1904", book.Date1904 ? "1" : null);
        }

        // The sheets in tab order, kept sheets after the worksheet they came after.
        List<KeptSheet> keptSheets = source?.KeptSheets ?? [];
        Dictionary<Worksheet, int> positions = [];
        Dictionary<KeptSheet, int> keptPositions = [];
        XElement sheets = new(ns + "sheets");
        int sheetId = Math.Max(
            book.Worksheets.Select(s => s.Source?.SheetId ?? 0).DefaultIfEmpty(0).Max(),
            keptSheets.Select(k => XlsxNames.Int((string?)k.Element.Attribute("sheetId"), 0)).DefaultIfEmpty(0).Max());
        HashSet<int> usedSheetIds = [];
        void AddKept(Func<KeptSheet, bool> match)
        {
            foreach (KeptSheet kept in keptSheets)
            {
                if (!keptPositions.ContainsKey(kept) && match(kept))
                {
                    keptPositions[kept] = sheets.Elements().Count();
                    sheets.Add(new XElement(kept.Element));
                }
            }
        }

        AddKept(k => k.After is null);
        foreach (Worksheet sheet in book.Worksheets)
        {
            int id = sheet.Source?.SheetId ?? 0;
            if (id <= 0 || !usedSheetIds.Add(id))
            {
                id = ++sheetId;
                usedSheetIds.Add(id);
            }

            positions[sheet] = sheets.Elements().Count();
            XElement element = new(ns + "sheet", new XAttribute("name", sheet.Name), new XAttribute("sheetId", XlsxNames.Integer(id)));
            if (sheet.Visibility != SheetVisibility.Visible)
            {
                element.Add(new XAttribute("state", sheet.Visibility == SheetVisibility.Hidden ? "hidden" : "veryHidden"));
            }

            element.Add(new XAttribute(XlsxNames.R + "id", sheetIds[sheet]));
            sheets.Add(element);
            AddKept(k => k.After == sheet);
        }

        AddKept(_ => true);
        WorkbookOrder.Place(root, sheets);

        // The defined names, local ones pointing at the sheets' new positions.
        XElement names = new(ns + "definedNames");
        foreach (DefinedName name in book.Names)
        {
            XElement element = new(ns + "definedName", new XAttribute("name", name.Name));
            if (name.Sheet is { } sheet && positions.TryGetValue(sheet, out int position))
            {
                element.Add(new XAttribute("localSheetId", XlsxNames.Integer(position)));
            }
            else if (name.KeptScope >= 0 && name.KeptScope < keptSheets.Count)
            {
                element.Add(new XAttribute("localSheetId", XlsxNames.Integer(keptPositions[keptSheets[name.KeptScope]])));
            }
            else if (name.Sheet is not null)
            {
                continue;
            }

            if (name.Hidden)
            {
                element.Add(new XAttribute("hidden", "1"));
            }

            if (name.Comment is not null)
            {
                element.Add(new XAttribute("comment", name.Comment));
            }

            if (name.Extra is not null)
            {
                element.Add(name.Extra);
            }

            element.Add(new XText(name.Formula));
            names.Add(element);
        }

        if (names.HasElements)
        {
            WorkbookOrder.Place(root, names);
        }
        else
        {
            root.Element(ns + "definedNames")?.Remove();
        }

        // The tab Excel opens on must be a sheet that is there.
        int count = sheets.Elements().Count();
        foreach (XElement view in root.Element(ns + "bookViews")?.Elements(ns + "workbookView") ?? [])
        {
            foreach (string attribute in (ReadOnlySpan<string>)["activeTab", "firstSheet"])
            {
                if (view.Attribute(attribute) is { } tab && XlsxNames.Int(tab.Value, 0) >= count)
                {
                    tab.Value = XlsxNames.Integer(count - 1);
                }
            }
        }

        WriteCalculation(book, root);

        return new XDocument(root);
    }

    /// <summary>
    /// The calculation settings into <c>calcPr</c>: an attribute is written when its value differs from the default or the
    /// file had it. Changed formulas or values make the results Excel saved stale, so it is told to calculate again on open.
    /// </summary>
    private static void WriteCalculation(Workbook book, XElement root)
    {
        XNamespace ns = XlsxNames.Main;
        CalculationSettings settings = book.Calculation;
        XElement? calcPr = root.Element(ns + "calcPr");
        bool stale = book.ChangeCount > 0 && book.Worksheets.Any(s => s.Formulas.Count > 0);
        bool custom = settings.Mode != CalculationMode.Automatic || settings.Iterate || settings.MaxIterations != 100
            || settings.MaxChange != 0.001 || !settings.FullPrecision || !settings.Concurrent;
        if (calcPr is null)
        {
            if (!stale && !custom)
            {
                return;
            }

            calcPr = new XElement(ns + "calcPr");
            WorkbookOrder.Place(root, calcPr);
        }

        void Set(string name, string? value, bool isDefault)
        {
            if (!isDefault || calcPr.Attribute(name) is not null)
            {
                calcPr.SetAttributeValue(name, value);
            }
        }

        Set("calcMode", settings.Mode switch
        {
            CalculationMode.Manual => "manual",
            CalculationMode.AutomaticExceptTables => "autoNoTable",
            _ => "auto",
        }, settings.Mode == CalculationMode.Automatic);
        Set("iterate", settings.Iterate ? "1" : "0", !settings.Iterate);
        Set("iterateCount", XlsxNames.Integer(settings.MaxIterations), settings.MaxIterations == 100);
        Set("iterateDelta", XlsxNames.Number(settings.MaxChange), settings.MaxChange == 0.001);
        Set("fullPrecision", settings.FullPrecision ? "1" : "0", settings.FullPrecision);
        Set("concurrentCalc", settings.Concurrent ? "1" : "0", settings.Concurrent);
        if (stale)
        {
            calcPr.SetAttributeValue("fullCalcOnLoad", "1");
        }
    }

    /// <summary>A part to write: its name, content type, bytes and relationships (targets as written).</summary>
    private sealed record OutPart(string Name, string ContentType, byte[] Data, List<OpcRelationship> Relationships);
}

/// <summary>
/// The shared strings a save writes: only those cells use, numbered in the order the sheets first use them (18.4.9).
/// </summary>
internal sealed class StringMap(SharedStringTable table)
{
    private int[] map = new int[table.Count];
    private readonly List<int> used = [];
    private int references;

    public int Count => used.Count;

    /// <summary>The index a cell's string has in the part written.</summary>
    public int Map(int index)
    {
        references++;
        if (index >= map.Length)
        {
            // A string added while saving, such as a table's header written from its column name.
            Array.Resize(ref map, Math.Max(index + 1, table.Count));
        }

        int mapped = map[index] - 1;
        if (mapped < 0)
        {
            mapped = used.Count;
            used.Add(index);
            map[index] = mapped + 1;
        }

        return mapped;
    }

    public byte[] Write()
    {
        using MemoryStream output = new();
        using (XmlWriter writer = XmlWriter.Create(output, WorksheetPartWriter.Settings))
        {
            writer.WriteStartDocument(standalone: true);
            writer.WriteStartElement("sst", XlsxNames.MainUri);
            writer.WriteAttributeString("count", XlsxNames.Integer(references));
            writer.WriteAttributeString("uniqueCount", XlsxNames.Integer(used.Count));
            foreach (int index in used)
            {
                if (table.Rich(index) is { } rich)
                {
                    rich.WriteTo(writer);
                    continue;
                }

                writer.WriteStartElement("si", XlsxNames.MainUri);
                WorksheetPartWriter.WriteText(writer, "t", table[index]);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        return output.ToArray();
    }
}

/// <summary>
/// Writes a worksheet part (18.3.1.99): the elements the model holds, from the file's own where it had them, the file's other
/// elements in their places, and the cells streamed row by row.
/// </summary>
internal static class WorksheetPartWriter
{
    public static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.Entitize };

    private static readonly XNamespace Ns = XlsxNames.Main;

    public static byte[] Write(Worksheet sheet, StringMap strings, List<(string Name, XElement Element)>? objects = null)
    {
        SheetSource? source = sheet.Source;
        List<(double Rank, XElement? Element)> elements = [];
        void Add(string name, XElement? element)
        {
            if (element is not null)
            {
                elements.Add((WorksheetOrder.Rank(name), element));
            }
        }

        Add("sheetPr", SheetProperties(sheet));
        Add("dimension", new XElement(Ns + "dimension", new XAttribute("ref", sheet.UsedRange?.Address.ToString() ?? "A1")));
        Add("sheetViews", SheetViews(sheet));
        Add("sheetFormatPr", SheetFormat(sheet));
        Add("cols", Columns(sheet));
        elements.Add((WorksheetOrder.Rank("sheetData"), null));
        if (sheet.MergedCells.Count > 0)
        {
            Add("mergeCells", new XElement(Ns + "mergeCells", new XAttribute("count", XlsxNames.Integer(sheet.MergedCells.Count)),
                sheet.MergedCells.Select(r => new XElement(Ns + "mergeCell", new XAttribute("ref", r.ToString())))));
        }

        PageSetupElements(sheet.PageSetup, Add);
        foreach ((string name, XElement element) in objects ?? [])
        {
            Add(name, element);
        }

        if (source is not null)
        {
            // A kept element the objects write again (a drawing the reader could not read, when a picture is added) gives way.
            HashSet<string> written = [.. (objects ?? []).Select(o => o.Name)];
            elements.AddRange(source.Kept.Where(k => !(XlsxNames.IsMain(k.Element.Name.NamespaceName) && written.Contains(k.Element.Name.LocalName)))
                .Select(k => (k.Rank, (XElement?)k.Element)));
        }

        using MemoryStream output = new();
        using (XmlWriter writer = XmlWriter.Create(output, Settings))
        {
            writer.WriteStartDocument(standalone: true);
            writer.WriteStartElement("worksheet", XlsxNames.MainUri);
            if (source is null)
            {
                writer.WriteAttributeString("xmlns", "r", null, XlsxNames.RUri);
            }
            else
            {
                foreach (XAttribute attribute in source.RootAttributes)
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        if (attribute.Name.Namespace == XNamespace.Xmlns)
                        {
                            writer.WriteAttributeString("xmlns", attribute.Name.LocalName, null, attribute.Value);
                        }
                    }
                    else
                    {
                        writer.WriteAttributeString(attribute.Name.LocalName, attribute.Name.NamespaceName, attribute.Value);
                    }
                }
            }

            foreach ((double _, XElement? element) in elements.OrderBy(e => e.Rank))
            {
                if (element is null)
                {
                    WriteSheetData(writer, sheet, strings);
                }
                else
                {
                    element.WriteTo(writer);
                }
            }

            writer.WriteEndElement();
        }

        return output.ToArray();
    }

    internal static void WriteText(XmlWriter writer, string name, string text)
    {
        writer.WriteStartElement(name, XlsxNames.MainUri);
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n', StringComparison.Ordinal)))
        {
            writer.WriteAttributeString("xml", "space", null, "preserve");
        }

        writer.WriteString(XlsxNames.Encode(text));
        writer.WriteEndElement();
    }

    private static XElement Patch(XElement? source, string name) => source is null ? new XElement(Ns + name) : new XElement(source);

    private static void Set(XElement element, string name, string? value) => element.SetAttributeValue(name, value);

    private static string? Flag(bool value, bool fallback) => value == fallback ? null : value ? "1" : "0";

    private static XElement? SheetProperties(Worksheet sheet)
    {
        XElement element = Patch(sheet.Source?.SheetProperties, "sheetPr");
        element.Element(Ns + "tabColor")?.Remove();
        if (sheet.TabColor is { } color)
        {
            element.AddFirst(XlsxStyles.WriteColor("tabColor", color));
        }

        XElement? setup = element.Element(Ns + "pageSetUpPr");
        if (sheet.PageSetup.FitToPage || setup is not null)
        {
            if (setup is null)
            {
                setup = new XElement(Ns + "pageSetUpPr");
                element.Add(setup);
            }

            Set(setup, "fitToPage", sheet.PageSetup.FitToPage ? "1" : null);
            if (!setup.HasAttributes && !setup.HasElements)
            {
                setup.Remove();
            }
        }

        return element.HasAttributes || element.HasElements ? element : null;
    }

    private static XElement SheetViews(Worksheet sheet)
    {
        SheetView model = sheet.View;
        XElement views = Patch(sheet.Source?.SheetViews, "sheetViews");
        XElement? view = views.Element(Ns + "sheetView");
        if (view is null)
        {
            view = new XElement(Ns + "sheetView", new XAttribute("workbookViewId", "0"));
            views.AddFirst(view);
        }

        Set(view, "showGridLines", Flag(model.ShowGridLines, true));
        Set(view, "showRowColHeaders", Flag(model.ShowHeadings, true));
        Set(view, "showZeros", Flag(model.ShowZeros, true));
        Set(view, "rightToLeft", Flag(model.RightToLeft, false));
        Set(view, "tabSelected", Flag(model.Selected, false));
        Set(view, "zoomScale", model.ZoomScale == 100 ? null : XlsxNames.Integer(model.ZoomScale));
        if (sheet.Source?.SheetViews is null || (model.FrozenRows, model.FrozenColumns) != model.SourceFrozen)
        {
            view.Elements(Ns + "pane").Remove();
            view.Elements(Ns + "selection").Remove();
            if (model.FrozenRows > 0 || model.FrozenColumns > 0)
            {
                string pane = (model.FrozenRows > 0, model.FrozenColumns > 0) switch
                {
                    (true, true) => "bottomRight",
                    (true, false) => "bottomLeft",
                    _ => "topRight",
                };
                XElement element = new(Ns + "pane");
                if (model.FrozenColumns > 0)
                {
                    element.Add(new XAttribute("xSplit", XlsxNames.Integer(model.FrozenColumns)));
                }

                if (model.FrozenRows > 0)
                {
                    element.Add(new XAttribute("ySplit", XlsxNames.Integer(model.FrozenRows)));
                }

                element.Add(
                    new XAttribute("topLeftCell", new CellAddress(model.FrozenRows, model.FrozenColumns).ToString()),
                    new XAttribute("activePane", pane),
                    new XAttribute("state", "frozen"));
                view.AddFirst(element, new XElement(Ns + "selection", new XAttribute("pane", pane)));
            }
        }

        return views;
    }

    private static XElement SheetFormat(Worksheet sheet)
    {
        XElement element = Patch(sheet.Source?.SheetFormat, "sheetFormatPr");
        double height = sheet.DefaultRowHeight ?? XlsxNamesDefaults.RowHeight(element);
        Set(element, "defaultRowHeight", XlsxNames.Number(height));
        if (sheet.Source?.SheetFormat is null && sheet.DefaultRowHeight is not null)
        {
            Set(element, "customHeight", "1");
        }

        Set(element, "defaultColWidth", sheet.DefaultColumnWidth is { } width ? XlsxNames.Number(width) : null);
        int rowOutline = sheet.RowRecords.Values.Select(r => r.OutlineLevel).DefaultIfEmpty(0).Max();
        int columnOutline = sheet.ColumnSpans.Spans.Select(s => s.OutlineLevel).DefaultIfEmpty(0).Max();
        Set(element, "outlineLevelRow", rowOutline > 0 ? XlsxNames.Integer(rowOutline) : null);
        Set(element, "outlineLevelCol", columnOutline > 0 ? XlsxNames.Integer(columnOutline) : null);
        return element;
    }

    private static XElement? Columns(Worksheet sheet)
    {
        if (sheet.ColumnSpans.Spans.Count == 0)
        {
            return null;
        }

        XElement cols = new(Ns + "cols");
        foreach (ColumnSpan span in sheet.ColumnSpans.Spans)
        {
            XElement col = new(Ns + "col", new XAttribute("min", XlsxNames.Integer(span.First + 1)), new XAttribute("max", XlsxNames.Integer(span.Last + 1)));
            bool custom = !double.IsNaN(span.Width);
            col.Add(new XAttribute("width", XlsxNames.Number(custom ? span.Width : sheet.DefaultColumnWidth ?? 9.140625)));
            if (span.Style >= 0)
            {
                col.Add(new XAttribute("style", XlsxNames.Integer(span.Style)));
            }

            if (span.Hidden)
            {
                col.Add(new XAttribute("hidden", "1"));
            }

            if (span.Extra is not null)
            {
                col.Add(span.Extra.Select(a => new XAttribute(a)));
            }

            if (custom && col.Attribute("customWidth") is null)
            {
                col.Add(new XAttribute("customWidth", "1"));
            }

            if (span.OutlineLevel > 0)
            {
                col.Add(new XAttribute("outlineLevel", XlsxNames.Integer(span.OutlineLevel)));
            }

            if (span.Collapsed)
            {
                col.Add(new XAttribute("collapsed", "1"));
            }

            cols.Add(col);
        }

        return cols;
    }

    private static void PageSetupElements(PageSetup page, Action<string, XElement?> add)
    {
        XElement options = Patch(page.SourcePrintOptions, "printOptions");
        Set(options, "horizontalCentered", Flag(page.CenterHorizontally, false));
        Set(options, "verticalCentered", Flag(page.CenterVertically, false));
        Set(options, "headings", Flag(page.PrintHeadings, false));
        Set(options, "gridLines", Flag(page.PrintGridLines, false));
        add("printOptions", options.HasAttributes ? options : null);

        XElement margins = Patch(page.SourceMargins, "pageMargins");
        Set(margins, "left", XlsxNames.Number(page.LeftMargin));
        Set(margins, "right", XlsxNames.Number(page.RightMargin));
        Set(margins, "top", XlsxNames.Number(page.TopMargin));
        Set(margins, "bottom", XlsxNames.Number(page.BottomMargin));
        Set(margins, "header", XlsxNames.Number(page.HeaderMargin));
        Set(margins, "footer", XlsxNames.Number(page.FooterMargin));
        add("pageMargins", margins);

        XElement setup = Patch(page.SourcePageSetup, "pageSetup");
        Set(setup, "paperSize", page.PaperSize is { } paper ? XlsxNames.Integer(paper) : null);
        Set(setup, "scale", page.Scale is { } scale ? XlsxNames.Integer(scale) : null);
        Set(setup, "firstPageNumber", page.FirstPageNumber is { } first ? XlsxNames.Integer(first) : null);
        Set(setup, "fitToWidth", page.FitToWidth == 1 ? null : XlsxNames.Integer(page.FitToWidth));
        Set(setup, "fitToHeight", page.FitToHeight == 1 ? null : XlsxNames.Integer(page.FitToHeight));
        Set(setup, "pageOrder", page.PageOrder == PrintPageOrder.OverThenDown ? "overThenDown" : null);
        Set(setup, "orientation", page.Orientation switch
        {
            PrintOrientation.Portrait => "portrait",
            PrintOrientation.Landscape => "landscape",
            _ => null,
        });
        Set(setup, "useFirstPageNumber", page.FirstPageNumber is null ? null : "1");
        add("pageSetup", setup.HasAttributes ? setup : null);

        XElement headerFooter = Patch(page.SourceHeaderFooter, "headerFooter");
        SetChild(headerFooter, "oddHeader", page.Header);
        SetChild(headerFooter, "oddFooter", page.Footer);
        add("headerFooter", headerFooter.HasAttributes || headerFooter.HasElements ? headerFooter : null);
        add("rowBreaks", Breaks("rowBreaks", page.RowBreaks, page.SourceRowBreaks, CellAddress.MaxColumns - 1));
        add("colBreaks", Breaks("colBreaks", page.ColumnBreaks, page.SourceColumnBreaks, CellAddress.MaxRows - 1));
    }

    /// <summary>
    /// rowBreaks or colBreaks for the manual breaks: the file's element as it was while its manual breaks are the same, else
    /// one brk per break reaching across the sheet (ECMA-376 Part 1, 18.3.1.73 and 18.3.1.14).
    /// </summary>
    private static XElement? Breaks(string name, PageBreakCollection breaks, XElement? source, int max)
    {
        if (source is not null)
        {
            IEnumerable<int> read = source.Elements(Ns + "brk")
                .Where(b => XlsxNames.Bool((string?)b.Attribute("man"), false))
                .Select(b => XlsxNames.Int((string?)b.Attribute("id"), 0))
                .Where(id => id > 0)
                .Distinct()
                .Order();
            if (read.SequenceEqual(breaks))
            {
                return source;
            }
        }

        if (breaks.Count == 0)
        {
            return null;
        }

        string count = XlsxNames.Integer(breaks.Count);
        return new XElement(Ns + name, new XAttribute("count", count), new XAttribute("manualBreakCount", count),
            breaks.Select(id => new XElement(Ns + "brk", new XAttribute("id", XlsxNames.Integer(id)), new XAttribute("max", XlsxNames.Integer(max)), new XAttribute("man", "1"))));
    }

    /// <summary>The children of headerFooter in CT_HeaderFooter's order (ECMA-376 Part 1, 18.3.1.46).</summary>
    private static readonly string[] HeaderFooterOrder = ["oddHeader", "oddFooter", "evenHeader", "evenFooter", "firstHeader", "firstFooter"];

    /// <summary>Sets, adds in its schema place, or removes a child of headerFooter.</summary>
    private static void SetChild(XElement parent, string name, string? value)
    {
        XElement? child = parent.Element(Ns + name);
        if (value is null)
        {
            child?.Remove();
        }
        else if (child is not null)
        {
            child.Value = value;
        }
        else
        {
            int rank = Array.IndexOf(HeaderFooterOrder, name);
            XElement added = new(Ns + name, value);
            XElement? next = parent.Elements().FirstOrDefault(e => e.Name.Namespace == Ns && Array.IndexOf(HeaderFooterOrder, e.Name.LocalName) > rank);
            if (next is null)
            {
                parent.Add(added);
            }
            else
            {
                next.AddBeforeSelf(added);
            }
        }
    }

    private static void WriteSheetData(XmlWriter writer, Worksheet sheet, StringMap strings)
    {
        writer.WriteStartElement("sheetData", XlsxNames.MainUri);
        HashSet<string> masters = SharedMasters(sheet);
        int[] recordRows = [.. sheet.RowRecords.Keys.Order()];
        int nextRecord = 0;
        int openRow = -1;
        char[] reference = new char[16];
        if (sheet.Store.Bounds() is { } bounds)
        {
            foreach ((int row, int column) in sheet.Store.Occupied(bounds))
            {
                if (row != openRow)
                {
                    if (openRow >= 0)
                    {
                        writer.WriteEndElement();
                    }

                    while (nextRecord < recordRows.Length && recordRows[nextRecord] < row)
                    {
                        WriteRowStart(writer, sheet, recordRows[nextRecord++]);
                        writer.WriteEndElement();
                    }

                    if (nextRecord < recordRows.Length && recordRows[nextRecord] == row)
                    {
                        nextRecord++;
                    }

                    WriteRowStart(writer, sheet, row);
                    openRow = row;
                }

                WriteCell(writer, sheet, row, column, strings, masters, reference);
            }
        }

        if (openRow >= 0)
        {
            writer.WriteEndElement();
        }

        while (nextRecord < recordRows.Length)
        {
            WriteRowStart(writer, sheet, recordRows[nextRecord++]);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>The shared indexes of the shared formulas whose first cell still holds them; cells sharing any other are written as values.</summary>
    private static HashSet<string> SharedMasters(Worksheet sheet)
    {
        HashSet<string> masters = [];
        foreach (FormulaEntry entry in sheet.Formulas.Entries)
        {
            if (entry.Text.Length > 0 && entry.Attributes is { } attributes && IsShared(attributes) && Attribute(attributes, "si") is { } si)
            {
                masters.Add(si);
            }
        }

        return masters;
    }

    private static bool IsShared(XAttribute[] attributes) => Attribute(attributes, "t") == "shared";

    private static string? Attribute(XAttribute[] attributes, string name)
    {
        foreach (XAttribute attribute in attributes)
        {
            if (attribute.Name.LocalName == name && attribute.Name.Namespace == XNamespace.None)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private static void WriteRowStart(XmlWriter writer, Worksheet sheet, int row)
    {
        writer.WriteStartElement("row", XlsxNames.MainUri);
        writer.WriteAttributeString("r", XlsxNames.Integer(row + 1));
        if (!sheet.RowRecords.TryGetValue(row, out RowRecord? record))
        {
            return;
        }

        bool height = !double.IsNaN(record.Height);
        if (record.Style >= 0)
        {
            writer.WriteAttributeString("s", XlsxNames.Integer(record.Style));
            writer.WriteAttributeString("customFormat", "1");
        }

        if (height)
        {
            writer.WriteAttributeString("ht", XlsxNames.Number(record.Height));
        }

        if (record.Hidden)
        {
            writer.WriteAttributeString("hidden", "1");
        }

        if (height)
        {
            writer.WriteAttributeString("customHeight", "1");
        }

        if (record.OutlineLevel > 0)
        {
            writer.WriteAttributeString("outlineLevel", XlsxNames.Integer(record.OutlineLevel));
        }

        if (record.Collapsed)
        {
            writer.WriteAttributeString("collapsed", "1");
        }

        foreach (XAttribute attribute in record.Extra ?? [])
        {
            if (height && attribute.Name.LocalName is "ht" or "customHeight" && attribute.Name.Namespace == XNamespace.None)
            {
                continue;
            }

            writer.WriteAttributeString(attribute.Name.LocalName, attribute.Name.NamespaceName, attribute.Value);
        }
    }

    private static void WriteCell(XmlWriter writer, Worksheet sheet, int row, int column, StringMap strings, HashSet<string> masters, char[] reference)
    {
        ref readonly CellRecord record = ref sheet.Store.Get(row, column);
        writer.WriteStartElement("c", XlsxNames.MainUri);
        int length = CellAddress.WriteColumnName(column, reference);
        (row + 1).TryFormat(reference.AsSpan(length), out int digits, default, System.Globalization.CultureInfo.InvariantCulture);
        writer.WriteStartAttribute("r");
        writer.WriteChars(reference, 0, length + digits);
        writer.WriteEndAttribute();
        if (record.Style != 0)
        {
            writer.WriteAttributeString("s", XlsxNames.Integer(record.Style));
        }

        FormulaEntry? formula = record.Kind == CellKind.Formula ? sheet.Formulas[(int)record.Payload] : null;
        if (formula is { Text.Length: 0, Attributes: { } shared } && IsShared(shared) && (Attribute(shared, "si") is not { } si || !masters.Contains(si)))
        {
            // A cell that shares a formula whose first cell no longer holds it: its result is all that is left.
            formula = new FormulaEntry("", formula.Result) { Attributes = null };
            if (formula.Result.IsEmpty)
            {
                formula = null;
                WriteExtras(writer, sheet, row, column, attributes: true);
                WriteExtras(writer, sheet, row, column, attributes: false);
                writer.WriteEndElement();
                return;
            }
        }

        CellValue value = formula is not null ? formula.Result : default;
        string? type = record.Kind switch
        {
            CellKind.SharedString => "s",
            CellKind.Boolean => "b",
            CellKind.Error => "e",
            CellKind.Formula => value.Kind switch
            {
                CellValueKind.Text => "str",
                CellValueKind.Boolean => "b",
                CellValueKind.Error => "e",
                _ => null,
            },
            _ => null,
        };
        if (type is not null)
        {
            writer.WriteAttributeString("t", type);
        }

        WriteExtras(writer, sheet, row, column, attributes: true);
        if (formula is { Text.Length: > 0 } || formula?.Attributes is not null)
        {
            writer.WriteStartElement("f", XlsxNames.MainUri);
            foreach (XAttribute attribute in formula.Attributes ?? [])
            {
                writer.WriteAttributeString(attribute.Name.LocalName, attribute.Name.NamespaceName, attribute.Value);
            }

            writer.WriteString(formula.Text);
            writer.WriteEndElement();
        }

        switch (record.Kind)
        {
            case CellKind.Number:
                writer.WriteElementString("v", XlsxNames.MainUri, XlsxNames.Number(BitConverter.Int64BitsToDouble(record.Payload)));
                break;
            case CellKind.SharedString:
                writer.WriteElementString("v", XlsxNames.MainUri, XlsxNames.Integer(strings.Map((int)record.Payload)));
                break;
            case CellKind.Boolean:
                writer.WriteElementString("v", XlsxNames.MainUri, record.Payload != 0 ? "1" : "0");
                break;
            case CellKind.Error:
                writer.WriteElementString("v", XlsxNames.MainUri, CellValue.ErrorText((CellError)record.Payload));
                break;
            case CellKind.Formula:
                switch (value.Kind)
                {
                    case CellValueKind.Number:
                        writer.WriteElementString("v", XlsxNames.MainUri, XlsxNames.Number(value.Number));
                        break;
                    case CellValueKind.Text:
                        writer.WriteElementString("v", XlsxNames.MainUri, XlsxNames.Encode(value.Text));
                        break;
                    case CellValueKind.Boolean:
                        writer.WriteElementString("v", XlsxNames.MainUri, value.Boolean ? "1" : "0");
                        break;
                    case CellValueKind.Error:
                        writer.WriteElementString("v", XlsxNames.MainUri, CellValue.ErrorText(value.Error));
                        break;
                }

                break;
        }

        WriteExtras(writer, sheet, row, column, attributes: false);
        writer.WriteEndElement();
    }

    private static void WriteExtras(XmlWriter writer, Worksheet sheet, int row, int column, bool attributes)
    {
        if (sheet.CellExtras.Count == 0 || !sheet.CellExtras.TryGetValue(Worksheet.Key(row, column), out XElement? extra))
        {
            return;
        }

        if (attributes)
        {
            foreach (XAttribute attribute in extra.Attributes())
            {
                writer.WriteAttributeString(attribute.Name.LocalName, attribute.Name.NamespaceName, attribute.Value);
            }
        }
        else
        {
            foreach (XNode node in extra.Nodes())
            {
                node.WriteTo(writer);
            }
        }
    }
}

/// <summary>Defaults Excel writes when a sheet gives none.</summary>
internal static class XlsxNamesDefaults
{
    /// <summary>The default row height a sheet format element gives, or 15 points, Calibri 11's row.</summary>
    public static double RowHeight(XElement sheetFormat) =>
        XlsxNames.TryNumber((string?)sheetFormat.Attribute("defaultRowHeight"), out double height) ? height : 15;
}
