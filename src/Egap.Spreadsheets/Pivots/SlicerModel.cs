using System.Globalization;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Pivots;

/// <summary>
/// The slicers of a workbook and their caches, read from an .xlsx file when first needed, and those made in memory. A slicer
/// cache holds a field's items and which are selected; Excel keeps the selection in the pivot tables too, as hidden items,
/// and Egap reads and changes it there (MS-XLSX 2.3.x, slicers and slicer caches).
/// </summary>
internal sealed class SlicerModel
{
    public const string CacheRelationship = "http://schemas.microsoft.com/office/2007/relationships/slicerCache";
    public const string SlicerRelationship = "http://schemas.microsoft.com/office/2007/relationships/slicer";
    public const string CacheType = "application/vnd.ms-excel.slicerCache+xml";
    public const string SlicerType = "application/vnd.ms-excel.slicer+xml";

    /// <summary>The extension of a workbook part that lists its slicer caches.</summary>
    public const string WorkbookExtension = "{BBE1A952-AA13-448e-AADC-164F8A28A991}";

    /// <summary>The extension of a worksheet part that lists its slicers.</summary>
    public const string SheetExtension = "{A8765BA9-456A-4dab-B4F3-ACF838C121DE}";

    /// <summary>The extension of a pivot cache definition that gives the id slicer caches know it by.</summary>
    public const string PivotCacheExtension = "{725AE2AE-9491-48be-B2B4-4EB974FC3084}";

    public static readonly XNamespace X14 = "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main";

    private readonly Workbook book;
    private readonly Dictionary<Worksheet, SheetSlicers> sheets = [];

    private SlicerModel(Workbook book) => this.book = book;

    public List<SlicerCache> Caches { get; } = [];

    public static SlicerModel Load(Workbook book)
    {
        SlicerModel model = new(book);
        if (book.Source is not { } source)
        {
            return model;
        }

        Dictionary<string, SlicerCache> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (OpcRelationship relationship in source.WorkbookPart.Relationships)
        {
            if (relationship.External || !XlsxNames.SameType(relationship.Type, CacheRelationship)
                || source.Package.GetPart(relationship.TargetName) is not { } part || SafeXml.TryLoad(part) is not { Root: { } root })
            {
                continue;
            }

            SlicerCache cache = SlicerCache.Read(book, root, part, relationship.Id);
            model.Caches.Add(cache);
            byName.TryAdd(cache.Name, cache);
        }

        foreach (Worksheet sheet in book.Worksheets)
        {
            if (sheet.Source is not { } sheetSource)
            {
                continue;
            }

            foreach (OpcRelationship relationship in sheetSource.Part.Relationships)
            {
                if (relationship.External || !XlsxNames.SameType(relationship.Type, SlicerRelationship)
                    || source.Package.GetPart(relationship.TargetName) is not { } part || SafeXml.TryLoad(part) is not { Root: { } root })
                {
                    continue;
                }

                SheetSlicers list = model.For(sheet);
                list.Part ??= (part, relationship.Id, root);
                foreach (XElement element in root.Elements(X14 + "slicer"))
                {
                    string name = (string?)element.Attribute("name") ?? "";
                    if ((string?)element.Attribute("cache") is not { } cacheName || !byName.TryGetValue(cacheName, out SlicerCache? cache))
                    {
                        continue;
                    }

                    Slicer slicer = new(cache, sheet, name, XlsxNames.Decode((string?)element.Attribute("caption") ?? name), element)
                    {
                        Drawing = sheet.Drawings.FirstOrDefault(d => d.Kind == SheetDrawingKind.Slicer && string.Equals(d.SlicerName, name, StringComparison.Ordinal)),
                    };
                    list.Slicers.Add(slicer);
                    cache.Slicers.Add(slicer);
                }
            }
        }

        return model;
    }

    /// <summary>The slicers of a sheet, made or read.</summary>
    public IReadOnlyList<Slicer> Slicers(Worksheet sheet) => sheets.TryGetValue(sheet, out SheetSlicers? list) ? list.Slicers : [];

    /// <summary>The slicer a drawing object of a sheet shows.</summary>
    public Slicer? Find(Worksheet sheet, SheetDrawing drawing) =>
        Slicers(sheet).FirstOrDefault(s => s.Drawing == drawing) ?? Slicers(sheet).FirstOrDefault(s => s.Drawing is null && s.Name == drawing.SlicerName);

    private SheetSlicers For(Worksheet sheet)
    {
        if (!sheets.TryGetValue(sheet, out SheetSlicers? list))
        {
            sheets[sheet] = list = new SheetSlicers();
        }

        return list;
    }

    /// <summary>
    /// Makes a slicer of a pivot table's field, its cache, the defined name Excel gives the cache and the drawing object that
    /// shows it, and refreshes the pivot table so the field has items. Nothing stays when a part of it fails.
    /// </summary>
    public SheetDrawing Create(Worksheet sheet, PivotTable pivotTable, string field, RangeAddress cells)
    {
        ArgumentNullException.ThrowIfNull(pivotTable);
        ArgumentNullException.ThrowIfNull(field);
        if (pivotTable.Worksheet.Workbook != book || !book.Worksheets.Contains(pivotTable.Worksheet))
        {
            throw new ArgumentException("The pivot table is not one of this workbook's.", nameof(pivotTable));
        }

        if (pivotTable.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        PivotCache pivotCache = pivotTable.Cache!;
        int index = pivotCache.Fields.FindIndex(f => f.Database && f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = pivotCache.Fields.FindIndex(f => f.Database && f.Name.Trim().Equals(field.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (index < 0)
        {
            throw new ArgumentException($"The pivot table has no field named {field}.", nameof(field));
        }

        string sourceName = pivotCache.Fields[index].Name;
        string name = FreeSlicerName(sourceName);

        // A second slicer of a field shares the field's slicer cache, as Excel's are: Excel keeps one cache to a field.
        if (Caches.FirstOrDefault(c => c.PivotCache == pivotCache && c.Field == index && c.Slicers.Count > 0) is { } shared)
        {
            if (!shared.Tables.Contains(pivotTable))
            {
                shared.Connect(pivotTable);
            }

            return AddSlicer(sheet, shared, name, sourceName, cells);
        }

        string cacheName = FreeCacheName(sourceName);
        SlicerCache cache = new(book, cacheName, sourceName, pivotCache, null, null, null) { Made = true };
        cache.Tables.Add(pivotTable);

        // The pivot table gets the field's items, which a slicer filters by, from a refresh.
        XDocument before = new(pivotTable.Definition.Document);
        try
        {
            SlicerCache.EnsureItems(pivotTable, index);
            book.Pivots.Refresh(pivotCache);
        }
        catch
        {
            pivotTable.Definition = new PivotDefinition(before);
            throw;
        }

        cache.EnsurePivotCacheId(Caches);
        Caches.Add(cache);
        if (!book.Names.Any(n => n.Name.Equals(cacheName, StringComparison.OrdinalIgnoreCase)))
        {
            book.Names.Add(cacheName, "#N/A");
        }

        return AddSlicer(sheet, cache, name, sourceName, cells);
    }

    private SheetDrawing AddSlicer(Worksheet sheet, SlicerCache cache, string name, string caption, RangeAddress cells)
    {
        Slicer slicer = new(cache, sheet, name, caption, null);
        cache.Slicers.Add(slicer);
        For(sheet).Slicers.Add(slicer);
        SheetDrawing drawing = sheet.Drawings.AddMade(SheetDrawingKind.Slicer, cells, name);
        drawing.SlicerName = name;
        slicer.Drawing = drawing;
        return drawing;
    }

    /// <summary>The name of a new slicer cache: <c>Slicer_</c> and the field's name with only letters, digits and underscores.</summary>
    private string FreeCacheName(string field)
    {
        string stem = "Slicer_" + new string([.. field.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
        HashSet<string> taken = new(Caches.Select(c => c.Name).Concat(book.Names.Select(n => n.Name)), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem))
        {
            return stem;
        }

        for (int n = 1; ; n++)
        {
            string name = stem + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>The name of a new slicer: the field's, then with 1, 2 and so on after a space, as Excel names them.</summary>
    private string FreeSlicerName(string field)
    {
        HashSet<string> taken = new(sheets.Values.SelectMany(s => s.Slicers).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(field))
        {
            return field;
        }

        for (int n = 1; ; n++)
        {
            string name = field + " " + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>Whether a save writes a sheet's slicers part again: a slicer was made, changed or removed.</summary>
    public bool SheetChanged(Worksheet sheet) =>
        sheets.TryGetValue(sheet, out SheetSlicers? list) && list.Slicers.Any(s => s.Source is null || s.Changed || !Shown(s));

    /// <summary>Whether a slicer still has its drawing object on its sheet.</summary>
    public static bool Shown(Slicer slicer) => slicer.Drawing is { } drawing && slicer.Worksheet.Drawings.Contains(drawing);

    /// <summary>
    /// Writes a sheet's slicers part when it changed and gives the sheet's extension list with the part in its slicer list;
    /// null when the sheet's own extension list stays.
    /// </summary>
    public XElement? WriteSheet(Worksheet sheet, string sheetPart, List<OpcRelationship> relationships, Func<string> nextId, Func<string, string, string> freeName, Action<string, string, byte[], List<OpcRelationship>> add)
    {
        if (!SheetChanged(sheet))
        {
            return null;
        }

        SheetSlicers list = sheets[sheet];
        List<Slicer> shown = [.. list.Slicers.Where(Shown)];
        XElement? oldList = sheet.Source?.Kept.Select(k => k.Element).FirstOrDefault(e => e.Name == XlsxNames.Main + "extLst");
        XElement extensions = oldList is null ? new XElement(XlsxNames.Main + "extLst") : new XElement(oldList);
        XElement? extension = extensions.Elements(XlsxNames.Main + "ext").FirstOrDefault(e => (string?)e.Attribute("uri") == SheetExtension);
        string? id = list.Part?.RelationshipId;
        if (shown.Count == 0)
        {
            extension?.Remove();
            relationships.RemoveAll(r => r.Id == id);
            return extensions.HasElements ? extensions : new XElement(XlsxNames.Main + "extLst");
        }

        string partName = list.Part?.Part.Name ?? freeName("/xl/slicers/slicer", "xml");
        XElement root = list.Part?.Root is { } oldRoot ? new XElement(oldRoot.Name, oldRoot.Attributes()) : NewRoot("slicers");
        foreach (Slicer slicer in shown)
        {
            root.Add(SlicerXml(slicer));
        }

        add(partName, SlicerType, OpcPackageWriter.Serialize(new XDocument(root)), list.Part is { } kept ? [.. kept.Part.Relationships] : []);
        if (id is null || !relationships.Any(r => r.Id == id))
        {
            id = nextId();
            relationships.Add(new OpcRelationship(id, SlicerRelationship, Xlsx.SheetObjectWriter.Relative(sheetPart, partName), false, partName));
        }

        if (extension is null)
        {
            extension = new XElement(XlsxNames.Main + "ext", new XAttribute("uri", SheetExtension), new XAttribute(XNamespace.Xmlns + "x14", X14.NamespaceName));
            extensions.Add(extension);
        }

        XElement slicerList = extension.Element(X14 + "slicerList") ?? new XElement(X14 + "slicerList");
        if (slicerList.Parent is null)
        {
            extension.Add(slicerList);
        }

        if (!slicerList.Elements(X14 + "slicer").Any(e => (string?)e.Attribute(XlsxNames.R + "id") == id))
        {
            slicerList.Add(new XElement(X14 + "slicer", new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri), new XAttribute(XlsxNames.R + "id", id)));
        }

        return extensions;
    }

    private static XElement SlicerXml(Slicer slicer)
    {
        XElement element = slicer.Source is { } source ? new XElement(source) : new XElement(X14 + "slicer");
        if (slicer.Source is null || slicer.Changed)
        {
            element.SetAttributeValue("name", slicer.Name);
            element.SetAttributeValue("cache", slicer.Cache.Name);
            element.SetAttributeValue("caption", XlsxNames.Encode(slicer.Caption));
            element.SetAttributeValue("columnCount", slicer.ColumnCount == 1 ? null : XlsxNames.Integer(slicer.ColumnCount));
            element.SetAttributeValue("showCaption", slicer.ShowHeader ? null : "0");
            element.SetAttributeValue("style", slicer.StyleName);
            element.SetAttributeValue("rowHeight", slicer.RowHeight.ToString(CultureInfo.InvariantCulture));
        }

        return element;
    }

    /// <summary>A root element of an x14 part, with the namespaces Excel declares on it.</summary>
    private static XElement NewRoot(string name) => new(
        X14 + name,
        new XAttribute(XNamespace.Xmlns + "mc", "http://schemas.openxmlformats.org/markup-compatibility/2006"),
        new XAttribute(XName.Get("Ignorable", "http://schemas.openxmlformats.org/markup-compatibility/2006"), "x"),
        new XAttribute(XNamespace.Xmlns + "x", XlsxNames.Main.NamespaceName));

    /// <summary>
    /// Writes the slicer caches that were made or changed, drops those no slicer shows any more, and lists the caches in the
    /// workbook part's extension; <paramref name="sheetIds"/> gives each worksheet's tab id.
    /// </summary>
    public void WriteWorkbook(XElement workbook, List<OpcRelationship> relationships, Func<string> nextId, string workbookName, Func<string, string> freeName, Action<string, string, byte[], List<OpcRelationship>> add, IReadOnlyDictionary<Worksheet, int> sheetIds)
    {
        if (!Caches.Any(c => c.Made || c.Changed || !c.Slicers.Any(Shown)))
        {
            return;
        }

        XNamespace ns = workbook.Name.Namespace;
        XElement extensions = workbook.Element(ns + "extLst") ?? new XElement(ns + "extLst");
        XElement? extension = extensions.Elements(ns + "ext").FirstOrDefault(e => (string?)e.Attribute("uri") == WorkbookExtension);
        if (extension is null)
        {
            extension = new XElement(ns + "ext", new XAttribute("uri", WorkbookExtension), new XAttribute(XNamespace.Xmlns + "x14", X14.NamespaceName));
            extensions.AddFirst(extension);
        }

        XElement list = extension.Element(X14 + "slicerCaches") ?? new XElement(X14 + "slicerCaches");
        if (list.Parent is null)
        {
            extension.Add(list);
        }

        foreach (SlicerCache cache in Caches)
        {
            if (!cache.Slicers.Any(Shown))
            {
                // A cache with no slicer left goes, with its defined name.
                list.Elements(X14 + "slicerCache").Where(e => (string?)e.Attribute(XlsxNames.R + "id") == cache.RelationshipId).Remove();
                relationships.RemoveAll(r => r.Id == cache.RelationshipId);
                workbook.Element(ns + "definedNames")?.Elements(ns + "definedName").Where(e => (string?)e.Attribute("name") == cache.Name).Remove();
                continue;
            }

            if (!cache.Made && !cache.Changed)
            {
                continue;
            }

            string partName = cache.Part?.Name ?? freeName("/xl/slicerCaches/slicerCache");
            add(partName, CacheType, OpcPackageWriter.Serialize(new XDocument(cache.Xml(sheetIds))), cache.Part is { } part ? [.. part.Relationships] : []);
            if (cache.RelationshipId is null || !relationships.Any(r => r.Id == cache.RelationshipId))
            {
                string id = nextId();
                relationships.Add(new OpcRelationship(id, CacheRelationship, Xlsx.SheetObjectWriter.Relative(workbookName, partName), false, partName));
                list.Add(new XElement(X14 + "slicerCache", new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri), new XAttribute(XlsxNames.R + "id", id)));
            }
        }

        if (!list.HasElements)
        {
            extension.Remove();
        }

        if (extensions.Parent is null && extensions.HasElements)
        {
            WorkbookOrder.Place(workbook, extensions);
        }
        else if (extensions.Parent is not null && !extensions.HasElements)
        {
            extensions.Remove();
        }
    }

    /// <summary>A sheet's slicers and the part the file kept them in.</summary>
    private sealed class SheetSlicers
    {
        public List<Slicer> Slicers { get; } = [];

        public (OpcPart Part, string RelationshipId, XElement Root)? Part { get; set; }
    }
}

/// <summary>
/// A slicer cache: the field of a pivot cache whose items its slicers show, the pivot tables they filter and, as Excel
/// saved it, which items were selected and which had no data.
/// </summary>
internal sealed class SlicerCache
{
    private readonly Workbook book;
    private readonly Dictionary<int, bool> noData = [];
    private readonly Dictionary<int, bool> selected = [];

    public SlicerCache(Workbook book, string name, string sourceName, PivotCache? pivotCache, XElement? source, OpcPart? part, string? relationshipId)
    {
        this.book = book;
        Name = name;
        SourceName = sourceName;
        PivotCache = pivotCache;
        Source = source;
        Part = part;
        RelationshipId = relationshipId;
    }

    public string Name { get; }

    /// <summary>The name of the field, as the pivot cache names it.</summary>
    public string SourceName { get; }

    public PivotCache? PivotCache { get; private set; }

    public List<PivotTable> Tables { get; } = [];

    public List<Slicer> Slicers { get; } = [];

    /// <summary>The cache's element as the file had it, or null for a cache made in memory.</summary>
    public XElement? Source { get; }

    public OpcPart? Part { get; }

    public string? RelationshipId { get; }

    public bool Made { get; init; }

    /// <summary>Whether its selection or its pivot tables changed, so a save writes it again.</summary>
    public bool Changed { get; private set; }

    /// <summary>Whether Egap can change the cache: it is a pivot table field's, and Egap found its pivot tables and field.</summary>
    public bool CanChange => PivotCache is not null && Tables.Count > 0 && Field >= 0 && (Source is null || Source.Element(SlicerModel.X14 + "data")?.Element(SlicerModel.X14 + "tabular") is not null);

    /// <summary>The field's number in the pivot cache, or -1.</summary>
    public int Field => PivotCache?.Fields.FindIndex(f => f.Name == SourceName) ?? -1;

    public static SlicerCache Read(Workbook book, XElement root, OpcPart part, string relationshipId)
    {
        XNamespace x14 = SlicerModel.X14;
        SlicerCache cache = new(book, (string?)root.Attribute("name") ?? "", XlsxNames.Decode((string?)root.Attribute("sourceName") ?? ""), null, root, part, relationshipId);
        foreach (XElement table in root.Element(x14 + "pivotTables")?.Elements(x14 + "pivotTable") ?? [])
        {
            int tabId = XlsxNames.Int((string?)table.Attribute("tabId"), -1);
            string tableName = (string?)table.Attribute("name") ?? "";
            if (book.Worksheets.FirstOrDefault(s => s.Source?.SheetId == tabId) is { } sheet
                && book.Pivots.Tables(sheet).FirstOrDefault(t => t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                cache.Tables.Add(found);
            }
        }

        cache.PivotCache = cache.Tables.FirstOrDefault()?.Cache;
        if (cache.Tables.Any(t => t.Cache != cache.PivotCache))
        {
            cache.Tables.Clear();
        }

        foreach (XElement item in root.Element(x14 + "data")?.Element(x14 + "tabular")?.Element(x14 + "items")?.Elements(x14 + "i") ?? [])
        {
            int x = XlsxNames.Int((string?)item.Attribute("x"), -1);
            if (x >= 0)
            {
                cache.selected[x] = XlsxNames.Bool((string?)item.Attribute("s"), false);
                cache.noData[x] = XlsxNames.Bool((string?)item.Attribute("nd"), false);
            }
        }

        return cache;
    }

    /// <summary>
    /// The items the slicers show, in their order: the pivot cache's items for the field, sorted as Excel sorts them, each
    /// selected unless the first pivot table hides it. A cache Egap cannot change gives the items the file listed.
    /// </summary>
    public List<SlicerItem> Items()
    {
        List<SlicerItem> items = [];
        int field = Field;
        if (PivotCache is null || field < 0)
        {
            return items;
        }

        PivotCacheField cacheField = PivotCache.Fields[field];
        PivotFieldDefinition? pivotField = Tables.Count > 0 && field < Tables[0].Definition.Fields.Count ? Tables[0].Definition.Fields[field] : null;
        Dictionary<int, PivotItemDefinition> shown = [];
        foreach (PivotItemDefinition item in pivotField?.DataItems ?? [])
        {
            if (item.Shared >= 0 && !item.Missing)
            {
                shown.TryAdd(item.Shared, item);
            }
        }

        CompareInfo compare = book.Culture.CompareInfo;
        List<int> order = pivotField is not null && shown.Count > 0
            ? [.. shown.Keys.Where(s => s < cacheField.Items.Count)]
            : [.. Enumerable.Range(0, cacheField.Items.Count).Where(s => selected.ContainsKey(s) || selected.Count == 0)];
        order.Sort((a, b) =>
        {
            int c = cacheField.Items[a].CompareTo(cacheField.Items[b], compare);
            return c != 0 ? c : a.CompareTo(b);
        });

        string? format = FormatOf(pivotField, cacheField);
        foreach (int shared in order)
        {
            PivotItemDefinition? item = shown.GetValueOrDefault(shared);
            bool isSelected = item is not null ? !item.Hidden : selected.GetValueOrDefault(shared, true);
            items.Add(new SlicerItem(shared, item?.Caption ?? Label(cacheField.Items[shared], format), isSelected, noData.GetValueOrDefault(shared)));
        }

        return items;
    }

    private string? FormatOf(PivotFieldDefinition? pivotField, PivotCacheField cacheField)
    {
        int id = pivotField?.NumberFormatId ?? cacheField.NumberFormatId;
        return id == 0 ? null : book.Styles.NumberFormats.Code(id);
    }

    private string Label(PivotValue value, string? format) => value.Kind switch
    {
        PivotValueKind.Blank => "(blank)",
        PivotValueKind.Number or PivotValueKind.Date => NumberFormatCode.Get(format ?? "General").Format(value.Number, book.Culture, book.Date1904),
        _ => value.ToString(),
    };

    /// <summary>
    /// Selects the items of the given labels, or all of them for null, by hiding the others in every pivot table, and
    /// refreshes the pivot tables; nothing changes when a refresh fails.
    /// </summary>
    public void Select(IReadOnlyCollection<string>? labels)
    {
        if (!CanChange)
        {
            throw new InvalidOperationException("Egap changes only the slicers of pivot tables.");
        }

        if (Tables.Select(t => t.Problem).FirstOrDefault(p => p is not null) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        List<SlicerItem> items = Items();
        HashSet<int> chosen;
        if (labels is null)
        {
            chosen = [.. items.Select(i => i.Shared)];
        }
        else
        {
            chosen = [];
            foreach (string label in labels)
            {
                SlicerItem? item = items.Find(i => string.Equals(i.Label, label, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    throw new ArgumentException($"The slicer has no item {label}.", nameof(labels));
                }

                chosen.Add(item.Shared);
            }

            if (chosen.Count == 0)
            {
                throw new ArgumentException("A slicer needs at least one item selected.", nameof(labels));
            }
        }

        Apply(Tables, chosen);
        Changed = true;
    }

    /// <summary>Connects another pivot table of the pivot cache: it takes the cache's selection and is refreshed.</summary>
    public void Connect(PivotTable table)
    {
        if (!CanChange)
        {
            throw new InvalidOperationException("Egap changes only the slicers of pivot tables.");
        }

        if (table.Cache != PivotCache)
        {
            throw new ArgumentException("The pivot table does not share the slicer's pivot cache.", nameof(table));
        }

        if (Tables.Contains(table))
        {
            return;
        }

        if (table.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        HashSet<int> chosen = [.. Items().Where(i => i.Selected).Select(i => i.Shared)];
        Apply([table], chosen);
        Tables.Add(table);
        Changed = true;
    }

    /// <summary>Hides the items not chosen in the tables' field and refreshes the cache; the tables stay as they were when it fails.</summary>
    private void Apply(IReadOnlyList<PivotTable> tables, HashSet<int> chosen)
    {
        int field = Field;
        if (field < 0 || tables.Any(t => field >= t.Definition.Fields.Count))
        {
            throw new InvalidOperationException("A pivot table has no field for the slicer.");
        }

        List<(PivotTable Table, XDocument Before)> saved = [.. tables.Select(t => (t, new XDocument(t.Definition.Document)))];
        try
        {
            foreach (PivotTable table in tables)
            {
                EnsureItems(table, field);
                PivotFieldDefinition pivotField = table.Definition.Fields[field];
                foreach (PivotItemDefinition item in pivotField.DataItems)
                {
                    item.Element.SetAttributeValue("h", item.Shared >= 0 && !chosen.Contains(item.Shared) ? "1" : null);
                }

                // A page field the slicer filters shows several items.
                if (table.Definition.PageFields.FirstOrDefault(p => p.Field == field) is { } page)
                {
                    pivotField.Element.SetAttributeValue("multipleItemSelectionAllowed", "1");
                    page.Element.SetAttributeValue("item", null);
                }

                table.Definition = new PivotDefinition(table.Definition.Document);
            }

            book.Pivots.Refresh(PivotCache!);
        }
        catch
        {
            foreach ((PivotTable table, XDocument before) in saved)
            {
                table.Definition = new PivotDefinition(before);
            }

            throw;
        }
    }

    /// <summary>Gives a pivot table's field an items list, which the next refresh fills, so it can hide items.</summary>
    public static void EnsureItems(PivotTable table, int field)
    {
        PivotFieldDefinition pivotField = table.Definition.Fields[field];
        if (pivotField.ItemsElement is null)
        {
            XNamespace ns = table.Definition.Ns;
            pivotField.Element.AddFirst(new XElement(ns + "items", new XAttribute("count", "0")));
            table.Definition = new PivotDefinition(table.Definition.Document);
        }
    }

    /// <summary>Gives the pivot cache the id slicer caches know it by, when it has none.</summary>
    public void EnsurePivotCacheId(IEnumerable<SlicerCache> others)
    {
        if (PivotCacheId(PivotCache!) is not null)
        {
            return;
        }

        long next = 1 + book.Pivots.Caches.Select(PivotCacheId).Where(id => id is not null).Select(id => id!.Value).DefaultIfEmpty(0).Max();
        XElement root = PivotCache!.Definition.Root!;
        XNamespace ns = root.Name.Namespace;
        XElement extensions = root.Element(ns + "extLst") ?? new XElement(ns + "extLst");
        if (extensions.Parent is null)
        {
            root.Add(extensions);
        }

        extensions.Add(new XElement(ns + "ext", new XAttribute("uri", SlicerModel.PivotCacheExtension), new XAttribute(XNamespace.Xmlns + "x14", SlicerModel.X14.NamespaceName),
            new XElement(SlicerModel.X14 + "pivotCacheDefinition", new XAttribute("pivotCacheId", next.ToString(CultureInfo.InvariantCulture)))));
        PivotCache.Changed = true;
    }

    /// <summary>The id a pivot cache's definition gives it for slicer caches, or null.</summary>
    public static long? PivotCacheId(PivotCache cache)
    {
        XElement? root = cache.Definition.Root;
        XNamespace ns = root?.Name.Namespace ?? XNamespace.None;
        XElement? definition = root?.Element(ns + "extLst")?.Elements(ns + "ext")
            .FirstOrDefault(e => (string?)e.Attribute("uri") == SlicerModel.PivotCacheExtension)?.Element(SlicerModel.X14 + "pivotCacheDefinition");
        return long.TryParse((string?)definition?.Attribute("pivotCacheId"), NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : null;
    }

    /// <summary>The cache's part: its pivot tables by tab id and name, and its items with their selection.</summary>
    public XElement Xml(IReadOnlyDictionary<Worksheet, int> sheetIds)
    {
        XNamespace x14 = SlicerModel.X14;
        XElement root = Source is { } source ? new XElement(source.Name, source.Attributes()) : new XElement(
            x14 + "slicerCacheDefinition",
            new XAttribute(XNamespace.Xmlns + "mc", "http://schemas.openxmlformats.org/markup-compatibility/2006"),
            new XAttribute(XName.Get("Ignorable", "http://schemas.openxmlformats.org/markup-compatibility/2006"), "x"),
            new XAttribute(XNamespace.Xmlns + "x", XlsxNames.Main.NamespaceName),
            new XAttribute("name", Name),
            new XAttribute("sourceName", XlsxNames.Encode(SourceName)));
        root.Add(new XElement(x14 + "pivotTables", Tables.Where(t => sheetIds.ContainsKey(t.Worksheet)).Select(t =>
            new XElement(x14 + "pivotTable", new XAttribute("tabId", XlsxNames.Integer(sheetIds[t.Worksheet])), new XAttribute("name", t.Name)))));

        XElement? oldTabular = Source?.Element(x14 + "data")?.Element(x14 + "tabular");
        XElement tabular = oldTabular is null ? new XElement(x14 + "tabular") : new XElement(oldTabular.Name, oldTabular.Attributes());
        tabular.SetAttributeValue("pivotCacheId", PivotCacheId(PivotCache!)?.ToString(CultureInfo.InvariantCulture));
        List<SlicerItem> items = Items();
        tabular.Add(new XElement(x14 + "items", new XAttribute("count", XlsxNames.Integer(items.Count)), items.Select(i => new XElement(
            x14 + "i",
            new XAttribute("x", XlsxNames.Integer(i.Shared)),
            i.Selected ? new XAttribute("s", "1") : null,
            i.NoData ? new XAttribute("nd", "1") : null))));
        tabular.Add(oldTabular?.Elements().Where(e => e.Name != x14 + "items").Select(e => new XElement(e)));
        root.Add(new XElement(x14 + "data", tabular));
        root.Add(Source?.Elements().Where(e => e.Name != x14 + "pivotTables" && e.Name != x14 + "data").Select(e => new XElement(e)));
        return root;
    }
}

/// <summary>An item of a slicer: its pivot cache item, its label, and whether it is selected and has data.</summary>
internal sealed record SlicerItem(int Shared, string Label, bool Selected, bool NoData);
