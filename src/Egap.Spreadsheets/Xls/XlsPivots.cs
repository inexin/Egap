using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Documents.Office;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Xls;

/// <summary>
/// The pivot tables of an XLS file ([MS-XLS] 2.1.7.12 and 2.1.7.20.5): the pivot cache definitions of the globals substream,
/// each cache's own stream in the <c>_SX_DB_CUR</c> storage and the pivot views of the sheets. They become the parts an
/// .xlsx file holds them in (ECMA-376 Part 1, 18.10), as Excel saves an XLS file's pivot tables, so the pivot model reads,
/// refreshes and saves them as it does an .xlsx file's. The records are kept as the file is read and turned into parts only
/// when the pivot tables are first needed.
/// </summary>
internal sealed class XlsPivots
{
    private const string PivotTableType = "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotTable+xml";
    private const string CacheDefinitionType = "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotCacheDefinition+xml";
    private const string CacheRecordsType = "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotCacheRecords+xml";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private static readonly XNamespace Ns = XlsxNames.Main;
    private static readonly XNamespace R = XlsxNames.R;

    private readonly List<CacheSource> caches = [];
    private readonly List<(Worksheet Sheet, List<(int Type, byte[] Body)> Records)> views = [];

    /// <summary>Whether a record of a sheet belongs to a pivot view: what <see cref="View"/> keeps.</summary>
    public static bool IsViewRecord(int type) =>
        type is 0x00B0 or 0x00B1 or 0x00B2 or 0x00B4 or 0x00B5 or 0x00B6 or 0x00C5 or 0x0100 or 0x00F1 or 0x0802 or 0x0810 or 0x0864;

    /// <summary>Whether a globals record belongs to a pivot cache definition: what <see cref="Global"/> reads.</summary>
    public static bool IsGlobalRecord(int type) => type is 0x00D5 or 0x00E3 or 0x0051 or 0x0052 or 0x01B5 or 0x00D0 or 0x00D1 or 0x00D2 or 0x00CD;

    /// <summary>A record of the globals substream: SXStreamID, SXVS and the source a range cache reads.</summary>
    public void Global(int type, ReadOnlySpan<byte> body)
    {
        switch (type)
        {
            case 0x00D5 when body.Length >= 2:
                caches.Add(new CacheSource { StreamId = BinaryPrimitives.ReadUInt16LittleEndian(body) });
                break;
            case 0x00E3 when body.Length >= 2 && caches.Count > 0:
                caches[^1].Kind = BinaryPrimitives.ReadUInt16LittleEndian(body);
                break;
            case 0x0051 or 0x0052 or 0x01B5 when caches.Count > 0 && caches[^1] is { Kind: 1, Read: false } cache:
                cache.Read = true;
                try
                {
                    ReadSource(cache, type, body);
                }
                catch (InvalidDataException)
                {
                    cache.Range = null;
                    cache.Name = null;
                }

                break;

            // Multiple consolidation ranges ([MS-XLS] 2.2.5.3.2.1): SXTbl, a DREF for each range, an SxTbpg for each range
            // with its page items, then each page field's items as SXTBRGIITM and SXString records.
            case 0x00D0 when body.Length >= 6 && caches.Count > 0 && caches[^1] is { Kind: 4, Read: false } table:
                table.Read = true;
                table.Consolidation = new Consolidation(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) & 0x7FFF, (BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) & 0x8000) != 0);
                break;
            case 0x0051 or 0x0052 or 0x01B5 when caches.Count > 0 && caches[^1].Consolidation is { } consolidation && consolidation.Ranges.Count < consolidation.RangeCount:
                {
                    DataRef range = new();
                    try
                    {
                        ReadSource(range, type, body);
                    }
                    catch (InvalidDataException)
                    {
                        range.Range = null;
                    }

                    consolidation.Ranges.Add(range);
                    break;
                }

            case 0x00D2 when caches.Count > 0 && caches[^1].Consolidation is { } consolidation && consolidation.Items.Count < consolidation.RangeCount:
                {
                    int[] items = new int[consolidation.PageCount];
                    for (int i = 0; i < items.Length; i++)
                    {
                        items[i] = (2 * i) + 2 <= body.Length ? BinaryPrimitives.ReadInt16LittleEndian(body[(2 * i)..]) : -1;
                    }

                    consolidation.Items.Add(items);
                    break;
                }

            case 0x00D1 when body.Length >= 2 && caches.Count > 0 && caches[^1].Consolidation is { } consolidation && consolidation.Pages.Count < consolidation.PageCount:
                consolidation.Pages.Add(([], BinaryPrimitives.ReadUInt16LittleEndian(body)));
                break;
            case 0x00CD when body.Length >= 2 && caches.Count > 0 && caches[^1].Consolidation is { Pages: [.., var page] } && page.Names.Count < page.Count:
                {
                    Reader reader = new(body);
                    int count = reader.UInt16();
                    try
                    {
                        page.Names.Add(count == 0xFFFF ? "" : reader.String(count));
                    }
                    catch (InvalidDataException)
                    {
                        page.Names.Add("");
                    }

                    break;
                }
        }
    }

    /// <summary>A pivot view record of a sheet, with its CONTINUE records' data after it.</summary>
    public void View(Worksheet sheet, int type, byte[] body)
    {
        if (views.Count == 0 || views[^1].Sheet != sheet)
        {
            views.Add((sheet, []));
        }

        views[^1].Records.Add((type, body));
    }

    /// <summary>
    /// Reads the caches' streams and leaves the rest for when the workbook's pivot tables are first needed. An encrypted
    /// file's cache streams are encrypted too, and Egap does not read its pivot tables.
    /// </summary>
    public void Finish(Workbook book, CompoundFile file, bool encrypted, Func<int, int> numberFormat)
    {
        if (views.Count == 0 || encrypted)
        {
            return;
        }

        Dictionary<int, byte[]> streams = [];
        foreach (CacheSource cache in caches)
        {
            if (!streams.ContainsKey(cache.StreamId) && file.ReadStream("_SX_DB_CUR/" + cache.StreamId.ToString("X4", CultureInfo.InvariantCulture)) is { } data)
            {
                streams[cache.StreamId] = data;
            }
        }

        List<CacheSource> sources = caches;
        List<(Worksheet Sheet, List<(int Type, byte[] Body)> Records)> found = views;
        DocumentLimits limits = new();
        book.MadePivots = () => Build(sources, streams, found, numberFormat, limits);
    }

    private static void ReadSource(DataRef cache, int type, ReadOnlySpan<byte> body)
    {
        Reader reader = new(body);
        int fileLength;
        if (type == 0x0051)
        {
            int rowFirst = reader.UInt16(), rowLast = reader.UInt16(), columnFirst = reader.Byte(), columnLast = reader.Byte();
            cache.Range = new RangeAddress(Math.Min(rowFirst, CellAddress.MaxRows - 1), columnFirst, Math.Min(rowLast, CellAddress.MaxRows - 1), columnLast).ToString();
            fileLength = reader.UInt16();
        }
        else if (type == 0x0052)
        {
            cache.Name = reader.String(reader.UInt16());
            fileLength = reader.UInt16();
        }
        else
        {
            cache.Name = reader.Byte() switch
            {
                0x00 => "Consolidate_Area",
                0x04 => "Database",
                0x05 => "Criteria",
                0x06 => "Print_Area",
                0x07 => "Print_Titles",
                0x0D => "_FilterDatabase",
                _ => null,
            };
            reader.Skip(3);
            fileLength = reader.UInt16();
        }

        if (fileLength == 0)
        {
            return;
        }

        // DConFile: 0x02 and a sheet of this workbook, or 0x01 and an encoded path ([MS-XLS] 2.5.277), [file]sheet.
        string file = reader.String(fileLength);
        if (file.Length > 0 && file[0] == '\u0002')
        {
            cache.Sheet = file[1..];
        }
        else if (file.Length > 0 && file[0] == '\u0001')
        {
            int open = file.LastIndexOf('[');
            int close = file.LastIndexOf(']');
            string path = open >= 0 && close > open ? file[1..open] + file[(open + 1)..close] : file[1..];
            cache.Sheet = close >= 0 ? file[(close + 1)..] : null;
            cache.File = DecodePath(path);
        }
    }

    /// <summary>An encoded path as a relationship target: a drive or the root, then folders, as Excel writes the link.</summary>
    private static string DecodePath(string path)
    {
        StringBuilder text = new();
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            switch (c)
            {
                case '\u0001' when i + 1 < path.Length:
                    text.Append(path[++i] == '@' ? "//" : path[i] + ":/");
                    break;
                case '\u0002' or '\u0003':
                    text.Append('/');
                    break;
                case '\u0004':
                    text.Append("../");
                    break;
                case '\u0005' when i + 1 < path.Length:
                    i++;
                    break;
                case < ' ':
                    break;
                default:
                    text.Append(c is ' ' or '%' or '#' ? Uri.HexEscape(c) : c.ToString());
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>The parts of the pivot tables Egap can turn into .xlsx parts, in a package of their own.</summary>
    private static PivotParts? Build(List<CacheSource> sources, Dictionary<int, byte[]> streams, List<(Worksheet Sheet, List<(int Type, byte[] Body)> Records)> views, Func<int, int> numberFormat, DocumentLimits limits)
    {
        Dictionary<int, (XDocument Definition, XDocument? Records, string? File, List<string> Fields)?> made = [];
        List<(Worksheet Sheet, XDocument Table, int Cache)> tables = [];
        foreach ((Worksheet sheet, List<(int Type, byte[] Body)> records) in views)
        {
            for (int start = 0; start < records.Count; start++)
            {
                if (records[start].Type != 0x00B0)
                {
                    continue;
                }

                int end = start + 1;
                while (end < records.Count && records[end].Type != 0x00B0)
                {
                    end++;
                }

                try
                {
                    PivotView view = PivotView.Read(records.GetRange(start, end - start), numberFormat);
                    if ((uint)view.Cache >= (uint)sources.Count)
                    {
                        continue;
                    }

                    if (!made.TryGetValue(view.Cache, out var cache))
                    {
                        CacheSource source = sources[view.Cache];
                        cache = streams.TryGetValue(source.StreamId, out byte[]? stream) ? CacheParts(source, stream) : null;
                        made[view.Cache] = cache;
                    }

                    if (cache is { } parts && view.Fields.Count == parts.Fields.Count)
                    {
                        tables.Add((sheet, view.Table(view.Cache, parts.Fields), view.Cache));
                    }
                }
                catch (InvalidDataException)
                {
                    // A damaged view is left out, as Excel leaves out a pivot table it cannot read.
                }
            }
        }

        if (tables.Count == 0)
        {
            return null;
        }

        // The parts as a package, so the pivot model reads them as it reads an .xlsx file's.
        using MemoryStream zip = new();
        Dictionary<int, string> cacheNames = [];
        List<(string Name, string Type)> types = [];
        using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, string type, byte[] data)
            {
                using Stream stream = archive.CreateEntry(name.TrimStart('/'), CompressionLevel.Fastest).Open();
                stream.Write(data);
                types.Add((name, type));
            }

            foreach ((int index, var cache) in made)
            {
                if (cache is not { } parts || !tables.Any(t => t.Cache == index))
                {
                    continue;
                }

                string number = XlsxNames.Integer(cacheNames.Count + 1);
                string name = "/xl/pivotCache/pivotCacheDefinition" + number + ".xml";
                cacheNames[index] = name;
                List<(string Id, string Type, string Target, bool External)> rels = [];
                if (parts.Records is { } recordsDocument)
                {
                    Entry("/xl/pivotCache/pivotCacheRecords" + number + ".xml", CacheRecordsType, OpcPackageWriter.Serialize(recordsDocument));
                    rels.Add(("rId1", Relationships + "pivotCacheRecords", "pivotCacheRecords" + number + ".xml", false));
                }

                if (parts.File is { } file)
                {
                    rels.Add(("rId2", Relationships + "externalLinkPath", file, true));
                }

                Entry(name, CacheDefinitionType, OpcPackageWriter.Serialize(parts.Definition));
                RelationshipsEntry(archive, "/xl/pivotCache/_rels/pivotCacheDefinition" + number + ".xml.rels", rels);
            }

            for (int i = 0; i < tables.Count; i++)
            {
                string number = XlsxNames.Integer(i + 1);
                Entry("/xl/pivotTables/pivotTable" + number + ".xml", PivotTableType, OpcPackageWriter.Serialize(tables[i].Table));
                RelationshipsEntry(archive, "/xl/pivotTables/_rels/pivotTable" + number + ".xml.rels",
                    [("rId1", Relationships + "pivotCacheDefinition", "../pivotCache/" + cacheNames[tables[i].Cache][("/xl/pivotCache/".Length)..], false)]);
            }

            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            XDocument contentTypes = new(new XElement(ct + "Types",
                new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                types.Select(t => new XElement(ct + "Override", new XAttribute("PartName", t.Name), new XAttribute("ContentType", t.Type)))));
            using Stream typesStream = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Fastest).Open();
            typesStream.Write(OpcPackageWriter.Serialize(contentTypes));
        }

        zip.Position = 0;
        OpcPackage package = OpcPackage.Read(zip, limits);
        return new PivotParts(package, [.. tables.Select((t, i) => (t.Sheet, package.GetPart("/xl/pivotTables/pivotTable" + XlsxNames.Integer(i + 1) + ".xml")!))]);
    }

    private static void RelationshipsEntry(ZipArchive archive, string name, List<(string Id, string Type, string Target, bool External)> rels)
    {
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        XDocument document = new(new XElement(pr + "Relationships", rels.Select(r => new XElement(pr + "Relationship",
            new XAttribute("Id", r.Id), new XAttribute("Type", r.Type), new XAttribute("Target", r.Target), r.External ? new XAttribute("TargetMode", "External") : null))));
        using Stream stream = archive.CreateEntry(name.TrimStart('/'), CompressionLevel.Fastest).Open();
        stream.Write(OpcPackageWriter.Serialize(document));
    }

    /// <summary>
    /// A cache stream as a pivot cache definition and its records. Null for a cache Egap does not turn into parts: one that
    /// does not read a range or name of a worksheet, or has calculated or grouped fields.
    /// </summary>
    private static (XDocument Definition, XDocument? Records, string? File, List<string> Fields)? CacheParts(CacheSource source, byte[] stream)
    {
        bool consolidated = source.Kind == 4 && source.Consolidation is { } c && c.RangeCount > 0 && c.Ranges.Count == c.RangeCount
            && c.Ranges.All(r => r.Range is not null && r.Sheet is not null && r.File is null) && c.Items.Count == c.RangeCount && c.Pages.Count == c.PageCount;
        if (!consolidated && (source.Kind != 1 || (source.Range is null && source.Name is null) || (source.Range is not null && source.Sheet is null)))
        {
            return null;
        }

        BiffStream biff = new(stream);
        if (!biff.Read() || biff.Type != 0x00C6 || biff.Length < 20)
        {
            return null;
        }

        Reader header = new(biff.Body);
        int recordCount = Math.Max(0, header.Int32());
        header.Skip(2);
        int flags = header.UInt16();
        header.Skip(2);
        int sourceFields = header.Int16();
        int allFields = header.Int16();
        header.Skip(4);
        int who = header.UInt16();
        string? refreshedBy = who is not 0xFFFF and > 0 ? header.String(who) : null;
        if (sourceFields <= 0 || sourceFields != allFields || sourceFields > 16384)
        {
            return null;
        }

        double? refreshed = null;
        List<CacheField> fields = [];
        List<CacheValue[]> rows = [];
        CacheValue[]? row = null;
        int next = 0;
        while (biff.Read() && biff.Type != BiffType.Eof)
        {
            ReadOnlySpan<byte> body = biff.Body;
            int type = biff.Type;
            if (type == 0x0122 && body.Length >= 12)
            {
                refreshed = BinaryPrimitives.ReadDoubleLittleEndian(body);
                if (BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) != 0)
                {
                    return null;
                }

                continue;
            }

            if (type == 0x00C7)
            {
                Reader field = new(body);
                int fieldFlags = field.UInt16();
                field.Skip(6);
                int groupItems = field.UInt16();
                field.Skip(2);
                int items = field.UInt16();
                string name = field.String(field.UInt16());
                if ((fieldFlags & 0x8018) != 0 || groupItems != 0 || fields.Count >= sourceFields)
                {
                    return null;
                }

                fields.Add(new CacheField(name, (fieldFlags & 0x0001) != 0, (fieldFlags & 0x0200) != 0, items));
                continue;
            }

            if (type is 0x00D8 or 0x00D9 or 0x00F9)
            {
                return null;
            }

            bool isValue = type is 0x00C9 or 0x00CA or 0x00CB or 0x00CD or 0x00CE or 0x00CF;
            if (type != 0x00C8 && !isValue)
            {
                continue;
            }

            // A field's shared items come after it; the records start once every field has its items.
            if (isValue && fields.Count > 0 && rows.Count == 0 && row is null && fields[^1].Items.Count < fields[^1].ItemCount)
            {
                fields[^1].Items.Add(Value(type, body));
                continue;
            }

            if (fields.Count != sourceFields)
            {
                return null;
            }

            // A record: SXDBB with the indexes of the fields with shared items, then a value for each other field.
            bool inline = fields.Any(f => !f.Shared);
            if (type == 0x00C8)
            {
                if (row is not null)
                {
                    rows.Add(row);
                }

                row = new CacheValue[sourceFields];
                next = 0;
                int at = 0;
                for (int f = 0; f < sourceFields; f++)
                {
                    if (!fields[f].Shared)
                    {
                        continue;
                    }

                    int size = fields[f].Wide ? 2 : 1;
                    int index = at + size <= body.Length ? (size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(body[at..]) : body[at]) : 0;
                    at += size;
                    row[f] = new CacheValue(CacheKind.Index, index, null);
                    if (index < fields[f].Items.Count)
                    {
                        fields[f].Used.Add(index);
                    }
                }

                if (!inline)
                {
                    rows.Add(row);
                    row = null;
                }

                continue;
            }

            if (row is null)
            {
                if (fields.Any(f => f.Shared))
                {
                    continue;
                }

                row = new CacheValue[sourceFields];
                next = 0;
            }

            while (next < sourceFields && fields[next].Shared)
            {
                next++;
            }

            if (next < sourceFields)
            {
                row[next++] = Value(type, body);
            }

            while (next < sourceFields && fields[next].Shared)
            {
                next++;
            }

            if (next >= sourceFields)
            {
                rows.Add(row);
                row = null;
            }
        }

        if (row is not null)
        {
            rows.Add(row);
        }

        if (fields.Count != sourceFields)
        {
            return null;
        }

        bool saved = (flags & 0x0001) != 0;
        XElement root = new(Ns + "pivotCacheDefinition", new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri),
            saved ? new XAttribute(R + "id", "rId1") : null,
            refreshedBy is null ? null : new XAttribute("refreshedBy", XlsxNames.Encode(refreshedBy)),
            refreshed is { } date && double.IsFinite(date) ? new XAttribute("refreshedDate", XlsxNames.Number(date)) : null,
            new XAttribute("createdVersion", "1"), new XAttribute("refreshedVersion", "1"),
            new XAttribute("recordCount", XlsxNames.Integer(saved ? rows.Count : recordCount)),
            saved ? null : new XAttribute("saveData", "0"),
            (flags & 0x0004) != 0 ? new XAttribute("refreshOnLoad", "1") : null,
            new XAttribute("upgradeOnRefresh", "1"));
        if (consolidated)
        {
            Consolidation table = source.Consolidation!;
            root.Add(new XElement(Ns + "cacheSource", new XAttribute("type", "consolidation"),
                new XElement(Ns + "consolidation", table.AutoPage ? null : new XAttribute("autoPage", "0"),
                    table.Pages.Count == 0 ? null : new XElement(Ns + "pages", new XAttribute("count", XlsxNames.Integer(table.Pages.Count)),
                        table.Pages.Select(p => new XElement(Ns + "page", new XAttribute("count", XlsxNames.Integer(p.Names.Count)),
                            p.Names.Select(n => new XElement(Ns + "pageItem", new XAttribute("name", XlsxNames.Encode(n))))))),
                    new XElement(Ns + "rangeSets", new XAttribute("count", XlsxNames.Integer(table.Ranges.Count)),
                        table.Ranges.Select((r, i) => new XElement(Ns + "rangeSet",
                            table.Items[i].Select((item, p) => item >= 0 ? new XAttribute("i" + XlsxNames.Integer(p + 1), XlsxNames.Integer(item)) : null),
                            new XAttribute("ref", r.Range!), new XAttribute("sheet", XlsxNames.Encode(r.Sheet!))))))));
        }
        else
        {
            XElement worksheetSource = source.Range is { } range
                ? new XElement(Ns + "worksheetSource", new XAttribute("ref", range), new XAttribute("sheet", XlsxNames.Encode(source.Sheet!)))
                : new XElement(Ns + "worksheetSource", new XAttribute("name", XlsxNames.Encode(source.Name!)), source.Sheet is { } named ? new XAttribute("sheet", XlsxNames.Encode(named)) : null);
            if (source.File is not null)
            {
                worksheetSource.Add(new XAttribute(R + "id", "rId2"));
            }

            root.Add(new XElement(Ns + "cacheSource", new XAttribute("type", "worksheet"), worksheetSource));
        }
        XElement list = new(Ns + "cacheFields", new XAttribute("count", XlsxNames.Integer(fields.Count)));
        for (int f = 0; f < fields.Count; f++)
        {
            CacheField field = fields[f];
            IEnumerable<CacheValue> values = field.Shared ? field.Items : rows.Select(r => r[f]);
            XElement shared = SharedItems(values);
            if (field.Shared)
            {
                shared.Add(new XAttribute("count", XlsxNames.Integer(field.Items.Count)));
                for (int i = 0; i < field.Items.Count; i++)
                {
                    XElement item = Item(field.Items[i]);
                    if (saved && !field.Used.Contains(i))
                    {
                        item.Add(new XAttribute("u", "1"));
                    }

                    shared.Add(item);
                }
            }

            list.Add(new XElement(Ns + "cacheField", new XAttribute("name", XlsxNames.Encode(field.Name)), new XAttribute("numFmtId", "0"), shared));
        }

        root.Add(list);
        XDocument? records = null;
        if (saved)
        {
            XElement recordsRoot = new(Ns + "pivotCacheRecords", new XAttribute(XNamespace.Xmlns + "r", XlsxNames.RUri), new XAttribute("count", XlsxNames.Integer(rows.Count)));
            foreach (CacheValue[] values in rows)
            {
                XElement r = new(Ns + "r");
                foreach (CacheValue value in values)
                {
                    r.Add(value.Kind == CacheKind.Index
                        ? new XElement(Ns + "x", new XAttribute("v", XlsxNames.Number(value.Number)))
                        : Item(value));
                }

                recordsRoot.Add(r);
            }

            records = new XDocument(recordsRoot);
        }

        return (new XDocument(root), records, source.File, [.. fields.Select(f => f.Name)]);
    }

    private static CacheValue Value(int type, ReadOnlySpan<byte> body)
    {
        switch (type)
        {
            case 0x00C9 when body.Length >= 8:
                return new CacheValue(CacheKind.Number, BinaryPrimitives.ReadDoubleLittleEndian(body), null);
            case 0x00CA when body.Length >= 2:
                return new CacheValue(CacheKind.Boolean, BinaryPrimitives.ReadUInt16LittleEndian(body) != 0 ? 1 : 0, null);
            case 0x00CB when body.Length >= 2:
                return new CacheValue(CacheKind.Error, 0, BinaryPrimitives.ReadUInt16LittleEndian(body) switch
                {
                    0x00 => "#NULL!",
                    0x07 => "#DIV/0!",
                    0x0F => "#VALUE!",
                    0x17 => "#REF!",
                    0x1D => "#NAME?",
                    0x24 => "#NUM!",
                    _ => "#N/A",
                });
            case 0x00CD when body.Length >= 2:
                {
                    Reader reader = new(body);
                    int count = reader.UInt16();
                    return new CacheValue(CacheKind.Text, 0, count == 0xFFFF ? "" : reader.String(count));
                }

            case 0x00CE when body.Length >= 8:
                {
                    int year = BinaryPrimitives.ReadUInt16LittleEndian(body), month = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                    int day = body[4], hour = body[5], minute = body[6], second = body[7];
                    if (day == 0)
                    {
                        (year, month, day) = (1899, 12, 30);
                    }

                    try
                    {
                        DateTime at = new(year, month, day, Math.Min(hour, 23), Math.Min(minute, 59), Math.Min(second, 59));

                        // Excel counts 1900 as a leap year, so the dates before March 1900 are a day earlier in an .xlsx file's
                        // pivot cache, as Excel writes them from its serial numbers.
                        if (at.Year == 1900 && at.Month < 3)
                        {
                            at = at.AddDays(-1);
                        }

                        return new CacheValue(CacheKind.Date, 0, at.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        return new CacheValue(CacheKind.Missing, 0, null);
                    }
                }

            default:
                return new CacheValue(CacheKind.Missing, 0, null);
        }
    }

    private static XElement Item(CacheValue value) => value.Kind switch
    {
        CacheKind.Number => new XElement(Ns + "n", new XAttribute("v", XlsxNames.Number(value.Number))),
        CacheKind.Boolean => new XElement(Ns + "b", new XAttribute("v", value.Number != 0 ? "1" : "0")),
        CacheKind.Error => new XElement(Ns + "e", new XAttribute("v", value.Text!)),
        CacheKind.Text => new XElement(Ns + "s", new XAttribute("v", XlsxNames.Encode(value.Text!))),
        CacheKind.Date => new XElement(Ns + "d", new XAttribute("v", value.Text!)),
        _ => new XElement(Ns + "m"),
    };

    /// <summary>A field's <c>sharedItems</c> with what its values hold, as Excel describes them (ECMA-376 Part 1, 18.10.1.90).</summary>
    private static XElement SharedItems(IEnumerable<CacheValue> values)
    {
        bool text = false, number = false, integer = true, date = false, blank = false, other = false, any = false;
        double min = double.MaxValue, max = double.MinValue;
        string? minDate = null, maxDate = null;
        foreach (CacheValue value in values)
        {
            any = true;
            switch (value.Kind)
            {
                case CacheKind.Text:
                    text = true;
                    break;
                case CacheKind.Number:
                    number = true;
                    integer &= Math.Floor(value.Number) == value.Number && Math.Abs(value.Number) < 1e15;
                    min = Math.Min(min, value.Number);
                    max = Math.Max(max, value.Number);
                    break;
                case CacheKind.Date:
                    date = true;
                    minDate = minDate is null || string.CompareOrdinal(value.Text, minDate) < 0 ? value.Text : minDate;
                    maxDate = maxDate is null || string.CompareOrdinal(value.Text, maxDate) > 0 ? value.Text : maxDate;
                    break;
                case CacheKind.Missing:
                    blank = true;
                    break;
                default:
                    // Excel counts errors and logical values as text in these flags.
                    other = true;
                    break;
            }
        }

        XElement shared = new(Ns + "sharedItems");
        if (!any || ((text || other) && !number && !date))
        {
            if (blank)
            {
                shared.Add(new XAttribute("containsBlank", "1"));
            }

            return shared;
        }

        int kinds = (text || other ? 1 : 0) + (number ? 1 : 0) + (date ? 1 : 0);
        if (!text && !blank && !other)
        {
            shared.Add(new XAttribute("containsSemiMixedTypes", "0"));
        }

        if (date && !text && !number && !other && !blank)
        {
            shared.Add(new XAttribute("containsNonDate", "0"));
        }

        if (date)
        {
            shared.Add(new XAttribute("containsDate", "1"));
        }

        if (!text && !other)
        {
            shared.Add(new XAttribute("containsString", "0"));
        }

        if (blank)
        {
            shared.Add(new XAttribute("containsBlank", "1"));
        }

        if (kinds > 1)
        {
            shared.Add(new XAttribute("containsMixedTypes", "1"));
        }

        // Excel describes the numbers of a field that has dates too only as mixed types: it refuses a cache that gives their
        // range as well.
        if (number && !date)
        {
            shared.Add(new XAttribute("containsNumber", "1"));
            if (integer)
            {
                shared.Add(new XAttribute("containsInteger", "1"));
            }

            shared.Add(new XAttribute("minValue", XlsxNames.Number(min)), new XAttribute("maxValue", XlsxNames.Number(max)));
        }

        if (date)
        {
            shared.Add(new XAttribute("minDate", minDate!), new XAttribute("maxDate", maxDate!));
        }

        return shared;
    }

    private class DataRef
    {
        public string? Range;
        public string? Name;
        public string? Sheet;
        public string? File;
    }

    private sealed class CacheSource : DataRef
    {
        public int StreamId;
        public int Kind;
        public bool Read;
        public Consolidation? Consolidation;
    }

    /// <summary>A cache of multiple consolidation ranges: its ranges, each range's page items and the page fields' items.</summary>
    private sealed class Consolidation(int rangeCount, int pageCount, bool autoPage)
    {
        public int RangeCount { get; } = rangeCount;

        public int PageCount { get; } = Math.Min(pageCount, 4);

        public bool AutoPage { get; } = autoPage;

        public List<DataRef> Ranges { get; } = [];

        public List<int[]> Items { get; } = [];

        public List<(List<string> Names, int Count)> Pages { get; } = [];
    }

    private sealed class CacheField(string name, bool shared, bool wide, int itemCount)
    {
        public string Name { get; } = name;

        public bool Shared { get; } = shared;

        public bool Wide { get; } = wide;

        public int ItemCount { get; } = shared ? itemCount : 0;

        public List<CacheValue> Items { get; } = [];

        public HashSet<int> Used { get; } = [];
    }

    private enum CacheKind
    {
        Missing,
        Number,
        Boolean,
        Error,
        Text,
        Date,
        Index,
    }

    private readonly record struct CacheValue(CacheKind Kind, double Number, string? Text);

    /// <summary>A pivot view's records: SxView and what follows it up to the next one.</summary>
    private sealed class PivotView
    {
        private static readonly string[] ItemTypes = ["", "default", "sum", "countA", "avg", "max", "min", "product", "count", "stdDev", "stdDevP", "var", "varP"];
        private static readonly string[] LineTypes = ["", "default", "sum", "countA", "count", "avg", "max", "min", "product", "stdDev", "stdDevP", "var", "varP", "grand", "blank"];
        private static readonly string[] Functions = ["", "count", "average", "max", "min", "product", "countNums", "stdDev", "stdDevp", "var", "varp"];
        private static readonly string[] ShowAs = ["", "difference", "percent", "percentDiff", "runTotal", "percentOfRow", "percentOfCol", "percentOfTotal", "index"];
        private static readonly string[] Subtotals = ["defaultSubtotal", "sumSubtotal", "countASubtotal", "avgSubtotal", "maxSubtotal", "minSubtotal", "productSubtotal", "countSubtotal", "stdDevSubtotal", "stdDevPSubtotal", "varSubtotal", "varPSubtotal"];

        private RangeAddress range;
        private int headRow, dataRow, dataColumn, dataAxis, dataPosition, rowCount, columnCount, pageCount, dataCount, lineRows, lineColumns, flags, autoFormat;
        private string name = "", dataName = "";
        private readonly List<int> rows = [], columns = [];
        private readonly List<(int Field, int Item)> pages = [];
        private readonly List<XElement> data = [];
        private readonly List<XElement> rowItems = [], columnItems = [];
        private int pageRows, pageColumns, pageWrap;
        private bool pagesAcross;
        private string? errorText, missingText, grandCaption;
        private uint? viewInfo;
        private readonly Dictionary<string, uint> fieldInfo = new(StringComparer.OrdinalIgnoreCase);

        public int Cache { get; private set; }

        public List<Field> Fields { get; } = [];

        public static PivotView Read(List<(int Type, byte[] Body)> records, Func<int, int> numberFormat)
        {
            PivotView view = new();
            Reader head = new(records[0].Body);
            int rowFirst = head.UInt16(), rowLast = head.UInt16(), columnFirst = head.UInt16(), columnLast = head.UInt16();
            view.range = new RangeAddress(Math.Min(rowFirst, CellAddress.MaxRows - 1), Math.Min(columnFirst, CellAddress.MaxColumns - 1),
                Math.Clamp(rowLast, rowFirst, CellAddress.MaxRows - 1), Math.Clamp(columnLast, columnFirst, CellAddress.MaxColumns - 1));
            view.headRow = head.UInt16() - rowFirst;
            view.dataRow = head.UInt16() - rowFirst;
            view.dataColumn = head.UInt16() - columnFirst;
            view.Cache = head.Int16();
            head.Skip(2);
            view.dataAxis = head.UInt16();
            view.dataPosition = head.Int16();
            int fieldCount = head.Int16();
            view.rowCount = head.UInt16();
            view.columnCount = head.UInt16();
            view.pageCount = head.UInt16();
            view.dataCount = head.Int16();
            view.lineRows = head.UInt16();
            view.lineColumns = head.UInt16();
            view.flags = head.UInt16();
            view.autoFormat = head.UInt16();
            int tableLength = head.UInt16(), dataLength = head.UInt16();
            view.name = head.String(tableLength);
            view.dataName = head.String(dataLength);
            if (fieldCount < 0 || fieldCount > 16384)
            {
                throw new InvalidDataException("A pivot view has too many fields.");
            }

            int axisRecords = 0, lineRecords = 0;
            string? addlField = null;
            foreach ((int type, byte[] body) in records.Skip(1))
            {
                Reader reader = new(body);
                switch (type)
                {
                    case 0x00B1:
                        {
                            int axis = reader.UInt16();
                            reader.Skip(2);
                            int subtotals = reader.UInt16();
                            reader.Skip(2);
                            int length = reader.UInt16();
                            view.Fields.Add(new Field(axis, subtotals, length == 0xFFFF ? null : reader.String(length)));
                            break;
                        }

                    case 0x00B2 when view.Fields.Count > 0:
                        {
                            int itemType = reader.Int16();
                            int itemFlags = reader.UInt16();
                            int cache = reader.Int16();
                            int length = reader.UInt16();
                            view.Fields[^1].Items.Add((itemType, itemFlags, cache, length == 0xFFFF ? null : reader.String(length)));
                            break;
                        }

                    case 0x0100 when view.Fields.Count > 0:
                        {
                            Field field = view.Fields[^1];
                            field.Extra = reader.UInt32();
                            reader.Skip(4);
                            field.NumberFormat = reader.UInt16() is > 0 and int format ? numberFormat(format) : 0;
                            if (reader.Left >= 10)
                            {
                                int length = reader.UInt16();
                                reader.Skip(8);
                                field.SubtotalCaption = length == 0xFFFF ? null : reader.String(length);
                            }

                            break;
                        }

                    case 0x00B4:
                        {
                            List<int> axis = axisRecords == 0 && view.rowCount > 0 ? view.rows : view.columns;
                            int count = axis == view.rows ? view.rowCount : view.columnCount;
                            for (int i = 0; i < count; i++)
                            {
                                axis.Add(reader.Int16());
                            }

                            axisRecords++;
                            break;
                        }

                    case 0x00B6:
                        for (int i = 0; i < view.pageCount; i++)
                        {
                            int field = reader.Int16(), item = reader.Int16();
                            reader.Skip(2);
                            view.pages.Add((field, item));
                        }

                        break;
                    case 0x00C5:
                        view.data.Add(DataField(reader, numberFormat));
                        break;
                    case 0x00B5:
                        {
                            bool rowLines = lineRecords == 0;
                            lineRecords++;
                            Lines(reader, rowLines ? view.lineRows : view.lineColumns, rowLines ? view.rowCount : view.columnCount, rowLines ? view.rowItems : view.columnItems);
                            break;
                        }

                    case 0x00F1:
                        {
                            reader.Skip(2);
                            int error = reader.UInt16(), missing = reader.UInt16(), tag = reader.UInt16();
                            reader.Skip(2);
                            view.pageRows = reader.UInt16();
                            view.pageColumns = reader.UInt16();
                            uint exFlags = reader.UInt32();
                            view.pagesAcross = (exFlags & 0x1) != 0;
                            view.pageWrap = (int)((exFlags >> 1) & 0xFF);
                            reader.Skip(6);
                            string? errorText = error == 0xFFFF ? null : reader.String(error);
                            string? missingText = missing == 0xFFFF ? null : reader.String(missing);
                            view.errorText = (exFlags & 0x200000) != 0 ? errorText ?? "" : null;
                            view.missingText = (exFlags & 0x400000) != 0 ? missingText : null;
                            _ = tag;
                            break;
                        }

                    case 0x0810 when body.Length >= 16:
                        {
                            reader.Skip(14);
                            int length = reader.UInt16();
                            string caption = length > 0 ? reader.String(length) : "";
                            view.grandCaption = caption.Length > 0 ? caption : null;
                            break;
                        }

                    case 0x0864 when body.Length >= 6:
                        {
                            reader.Skip(4);
                            int kind = reader.Byte(), part = reader.Byte();
                            if (kind == 0x00 && part == 0x19 && reader.Left >= 4)
                            {
                                view.viewInfo = reader.UInt32();
                            }
                            else if (kind == 0x17 && part == 0x00 && reader.Left >= 9)
                            {
                                reader.Skip(6);
                                addlField = reader.String(reader.UInt16());
                            }
                            else if (kind == 0x17 && part == 0x19 && reader.Left >= 4 && addlField is not null)
                            {
                                view.fieldInfo[addlField] = reader.UInt32();
                            }

                            break;
                        }
                }
            }

            if (view.Fields.Count != fieldCount)
            {
                throw new InvalidDataException("A pivot view does not have its fields.");
            }

            return view;
        }

        private static XElement DataField(Reader reader, Func<int, int> numberFormat)
        {
            int field = reader.Int16(), function = reader.Int16(), show = reader.Int16(), baseField = reader.Int16(), baseItem = reader.Int16();
            int format = reader.UInt16();
            int length = reader.UInt16();
            string? caption = length == 0xFFFF ? null : reader.String(length);
            return new XElement(Ns + "dataField",
                caption is null ? null : new XAttribute("name", XlsxNames.Encode(caption)),
                new XAttribute("fld", XlsxNames.Integer(field)),
                function > 0 && function < Functions.Length ? new XAttribute("subtotal", Functions[function]) : null,
                show > 0 && show < ShowAs.Length ? new XAttribute("showDataAs", ShowAs[show]) : null,
                new XAttribute("baseField", XlsxNames.Integer(Math.Max(0, (int)baseField))),
                new XAttribute("baseItem", XlsxNames.Integer(baseItem switch { 0x7FFB => 1048828, 0x7FFC => 1048829, < 0 => 0, _ => baseItem })),
                format != 0 ? new XAttribute("numFmtId", XlsxNames.Integer(numberFormat(format))) : null);
        }

        /// <summary>SXLI's lines as row or column items: each with as many entries as the axis has fields.</summary>
        private static void Lines(Reader reader, int count, int axisFields, List<XElement> items)
        {
            for (int i = 0; i < count; i++)
            {
                int repeated = reader.Int16();
                int itemType = reader.UInt16() & 0x7FFF;
                int shown = reader.Int16();
                int lineFlags = reader.UInt16();
                int[] entries = new int[axisFields];
                for (int j = 0; j < axisFields; j++)
                {
                    entries[j] = reader.Int16();
                }

                XElement item = new(Ns + "i",
                    itemType > 0 && itemType < LineTypes.Length ? new XAttribute("t", LineTypes[itemType]) : null,
                    repeated > 0 ? new XAttribute("r", XlsxNames.Integer(repeated)) : null,
                    ((lineFlags >> 1) & 0xFF) is > 0 and int data ? new XAttribute("i", XlsxNames.Integer(data)) : null);
                for (int j = Math.Max(0, repeated); j < Math.Min(shown, axisFields); j++)
                {
                    int value = entries[j] is < 0 or 0x7FFF ? 0 : entries[j];
                    item.Add(new XElement(Ns + "x", value == 0 ? null : new XAttribute("v", XlsxNames.Integer(value))));
                }

                items.Add(item);
            }
        }

        /// <summary>The view as a pivot table definition part, as Excel saves an XLS file's pivot table to .xlsx.</summary>
        public XDocument Table(int cacheId, List<string> cacheFields)
        {
            // Excel 2007 and later keep the compact and outline switches in SXAddl records; older views are classic.
            bool legacy = viewInfo is null;
            uint info = viewInfo ?? 0;
            XElement root = new(Ns + "pivotTableDefinition",
                new XAttribute("name", XlsxNames.Encode(name)),
                new XAttribute("cacheId", XlsxNames.Integer(cacheId)),
                dataAxis == 1 ? new XAttribute("dataOnRows", "1") : null,
                dataPosition >= 0 ? new XAttribute("dataPosition", XlsxNames.Integer(dataPosition)) : null,
                (flags & 0x0001) == 0 ? new XAttribute("rowGrandTotals", "0") : null,
                (flags & 0x0002) == 0 ? new XAttribute("colGrandTotals", "0") : null,
                new XAttribute("applyNumberFormats", (flags & 0x0010) != 0 ? "1" : "0"),
                new XAttribute("applyBorderFormats", (flags & 0x0080) != 0 ? "1" : "0"),
                new XAttribute("applyFontFormats", (flags & 0x0020) != 0 ? "1" : "0"),
                new XAttribute("applyPatternFormats", (flags & 0x0100) != 0 ? "1" : "0"),
                new XAttribute("applyAlignmentFormats", (flags & 0x0040) != 0 ? "1" : "0"),
                new XAttribute("applyWidthHeightFormats", (flags & 0x0200) != 0 ? "1" : "0"),
                new XAttribute("dataCaption", XlsxNames.Encode(dataName)),
                grandCaption is null ? null : new XAttribute("grandTotalCaption", XlsxNames.Encode(grandCaption)),
                errorText is null ? null : new XAttribute("showError", "1"),
                errorText is null ? null : new XAttribute("errorCaption", XlsxNames.Encode(errorText)),
                missingText is null ? null : new XAttribute("missingCaption", XlsxNames.Encode(missingText)),
                new XAttribute("updatedVersion", "1"),
                (flags & 0x0008) != 0 ? new XAttribute("useAutoFormatting", "1") : null,
                (flags & 0x0008) != 0 && autoFormat is > 0 and not 1 ? new XAttribute("autoFormatId", XlsxNames.Integer(autoFormat)) : null,
                new XAttribute("itemPrintTitles", "1"),
                new XAttribute("createdVersion", "1"),
                new XAttribute("indent", "0"),
                pagesAcross ? new XAttribute("pageOverThenDown", "1") : null,
                pageWrap > 0 ? new XAttribute("pageWrap", XlsxNames.Integer(pageWrap)) : null,
                legacy || (info & 0x1) == 0 ? new XAttribute("compact", "0") : null,
                legacy || (info & 0x8) == 0 ? new XAttribute("compactData", "0") : null,
                !legacy && (info & 0x2) != 0 ? new XAttribute("outline", "1") : null,
                !legacy && (info & 0x4) != 0 ? new XAttribute("outlineData", "1") : null,
                legacy || (info & 0x10) == 0 ? new XAttribute("gridDropZones", "1") : null);
            root.Add(new XElement(Ns + "location",
                new XAttribute("ref", range.ToString()),
                new XAttribute("firstHeaderRow", XlsxNames.Integer(Math.Max(0, headRow))),
                new XAttribute("firstDataRow", XlsxNames.Integer(Math.Max(0, dataRow))),
                new XAttribute("firstDataCol", XlsxNames.Integer(Math.Max(0, dataColumn))),
                pages.Count > 0 && pageRows > 0 ? new XAttribute("rowPageCount", XlsxNames.Integer(pageRows)) : null,
                pages.Count > 0 && pageColumns > 0 ? new XAttribute("colPageCount", XlsxNames.Integer(pageColumns)) : null));

            XElement fields = new(Ns + "pivotFields", new XAttribute("count", XlsxNames.Integer(Fields.Count)));
            for (int f = 0; f < Fields.Count; f++)
            {
                Field field = Fields[f];
                uint extra = field.Extra;
                bool compact = !legacy && (fieldInfo.TryGetValue(cacheFields[f], out uint fieldFlags) ? (fieldFlags & 0x8) != 0 : (info & 0x1) != 0);
                XElement element = new(Ns + "pivotField",
                    field.Name is null ? null : new XAttribute("name", XlsxNames.Encode(field.Name)),
                    (field.Axis & 0x7) switch
                    {
                        1 => new XAttribute("axis", "axisRow"),
                        2 => new XAttribute("axis", "axisCol"),
                        4 => new XAttribute("axis", "axisPage"),
                        _ => null,
                    },
                    (field.Axis & 0x8) != 0 ? new XAttribute("dataField", "1") : null,
                    compact ? null : new XAttribute("compact", "0"),
                    field.NumberFormat != 0 ? new XAttribute("numFmtId", XlsxNames.Integer(field.NumberFormat)) : null,
                    (extra & 0x200000) == 0 ? new XAttribute("outline", "0") : null,
                    (extra & 0x800000) == 0 ? new XAttribute("subtotalTop", "0") : null,
                    (extra & 0x2) == 0 ? new XAttribute("dragToRow", "0") : null,
                    (extra & 0x4) == 0 ? new XAttribute("dragToCol", "0") : null,
                    (extra & 0x8) == 0 ? new XAttribute("dragToPage", "0") : null,
                    (extra & 0x10) == 0 ? new XAttribute("dragOff", "0") : null,
                    (extra & 0x20) != 0 ? new XAttribute("dragToData", "0") : null,
                    (extra & 0x1) == 0 ? new XAttribute("showAll", "0") : null,
                    (extra & 0x400000) != 0 ? new XAttribute("insertBlankRow", "1") : null,
                    (extra & 0x4000) != 0 ? new XAttribute("insertPageBreak", "1") : null,
                    (extra & 0x8000) != 0 ? new XAttribute("hideNewItems", "1") : null,
                    (extra & 0x200) != 0 ? new XAttribute("sortType", (extra & 0x400) != 0 ? "ascending" : "descending") : null,
                    (extra & 0x800) != 0 ? new XAttribute("autoShow", "1") : null,
                    (extra & 0x800) != 0 && (extra & 0x1000) == 0 ? new XAttribute("topAutoShow", "0") : null,
                    field.SubtotalCaption is null ? null : new XAttribute("subtotalCaption", XlsxNames.Encode(field.SubtotalCaption)),
                    legacy ? new XAttribute("includeNewItemsInFilter", "1") : null);
                for (int s = 0; s < Subtotals.Length; s++)
                {
                    bool on = (field.Subtotals & (1 << s)) != 0;
                    if (s == 0 ? !on : on)
                    {
                        element.Add(new XAttribute(Subtotals[s], s == 0 ? "0" : "1"));
                    }
                }

                if (field.Items.Count > 0)
                {
                    XElement items = new(Ns + "items", new XAttribute("count", XlsxNames.Integer(field.Items.Count)));
                    foreach ((int itemType, int itemFlags, int cache, string? caption) in field.Items)
                    {
                        items.Add(new XElement(Ns + "item",
                            caption is null ? null : new XAttribute("n", XlsxNames.Encode(caption)),
                            itemType > 0 && itemType < ItemTypes.Length ? new XAttribute("t", ItemTypes[itemType]) : null,
                            (itemFlags & 0x01) != 0 ? new XAttribute("h", "1") : null,
                            (itemFlags & 0x02) != 0 ? new XAttribute("sd", "0") : null,
                            (itemFlags & 0x10) != 0 ? new XAttribute("m", "1") : null,
                            itemType == 0 ? new XAttribute("x", XlsxNames.Integer(Math.Max(0, cache))) : null));
                    }

                    element.Add(items);
                }

                fields.Add(element);
            }

            root.Add(fields);
            Axis(root, "rowFields", rows, "rowItems", rowItems);
            Axis(root, "colFields", columns, "colItems", columnItems);
            if (pages.Count > 0)
            {
                root.Add(new XElement(Ns + "pageFields", new XAttribute("count", XlsxNames.Integer(pages.Count)),
                    pages.Select(p => new XElement(Ns + "pageField", new XAttribute("fld", XlsxNames.Integer(p.Field)),
                        p.Item is >= 0 and < 0x7FFD ? new XAttribute("item", XlsxNames.Integer(p.Item)) : null, new XAttribute("hier", "-1")))));
            }

            if (data.Count > 0)
            {
                root.Add(new XElement(Ns + "dataFields", new XAttribute("count", XlsxNames.Integer(data.Count)), data));
            }

            root.Add(new XElement(Ns + "pivotTableStyleInfo", new XAttribute("showRowHeaders", "1"), new XAttribute("showColHeaders", "1"),
                new XAttribute("showRowStripes", "0"), new XAttribute("showColStripes", "0"), new XAttribute("showLastColumn", "1")));
            return new XDocument(root);
        }

        private static void Axis(XElement root, string fieldsName, List<int> fields, string itemsName, List<XElement> items)
        {
            if (fields.Count > 0)
            {
                root.Add(new XElement(Ns + fieldsName, new XAttribute("count", XlsxNames.Integer(fields.Count)),
                    fields.Select(f => new XElement(Ns + "field", new XAttribute("x", XlsxNames.Integer(f))))));
            }

            if (items.Count > 0)
            {
                root.Add(new XElement(Ns + itemsName, new XAttribute("count", XlsxNames.Integer(items.Count)), items));
            }
        }
    }

    /// <summary>A pivot field of a view: Sxvd, its SXVI items and SXVDEx.</summary>
    private sealed class Field(int axis, int subtotals, string? name)
    {
        public int Axis { get; } = axis;

        public int Subtotals { get; } = subtotals;

        public string? Name { get; } = name;

        public uint Extra { get; set; } = 0x00A0141E;

        public int NumberFormat { get; set; }

        public string? SubtotalCaption { get; set; }

        public List<(int Type, int Flags, int Cache, string? Caption)> Items { get; } = [];
    }

    /// <summary>Reads a record's data, failing with <see cref="InvalidDataException"/> past its end.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position;

        public readonly int Left => data.Length - position;

        public int Byte() => Take(1)[0];

        public int UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int Int16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public void Skip(int count) => Take(count);

        /// <summary>XLUnicodeStringNoCch: an option byte, then <paramref name="count"/> one- or two-byte characters.</summary>
        public string String(int count)
        {
            bool wide = (Byte() & 0x01) != 0;
            ReadOnlySpan<byte> bytes = Take(wide ? count * 2 : count);
            return wide ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - position)
            {
                throw new InvalidDataException("A pivot record ends before its data does.");
            }

            ReadOnlySpan<byte> span = data.Slice(position, count);
            position += count;
            return span;
        }
    }
}

/// <summary>The pivot table parts made from an XLS file: a package of their own, and each table's sheet and part.</summary>
internal sealed record PivotParts(OpcPackage Package, List<(Worksheet Sheet, OpcPart Table)> Tables);
