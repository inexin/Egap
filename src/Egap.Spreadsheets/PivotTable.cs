using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Spreadsheets.Pivots;

namespace Egap.Spreadsheets;

/// <summary>
/// A pivot table of a worksheet, as an .xlsx file holds it: its definition, its cache of the source's values and the cells
/// it fills. <see cref="Refresh"/> reads the source again and lays the table out as Excel's Refresh does.
/// </summary>
/// <remarks>
/// Egap reads pivot tables from .xlsx files and keeps them when it saves one; it does not make new ones. A refresh works out
/// sums, counts, averages, maxima, minima, products, counts of numbers, standard deviations and variances with subtotals and
/// grand totals in the compact, outline, tabular and classic layouts, with page fields, hidden items and sorted fields. A
/// table that shows values as a share or difference, has calculated fields or items, groups items or reads another workbook
/// or a cube cannot be refreshed: see <see cref="CanRefresh"/>.
/// </remarks>
public sealed class PivotTable
{
    internal PivotTable(Worksheet worksheet, OpcPart part, PivotDefinition definition, PivotCache? cache)
    {
        Worksheet = worksheet;
        Part = part;
        Definition = definition;
        Cache = cache;
    }

    /// <summary>The pivot table's name, such as <c>PivotTable1</c>.</summary>
    public string Name => Definition.Name;

    /// <summary>The sheet the pivot table is on.</summary>
    public Worksheet Worksheet { get; }

    /// <summary>The cells the pivot table takes, without its page fields above it.</summary>
    public RangeAddress Range => Definition.Range;

    /// <summary>
    /// The range the pivot table's cache reads, such as <c>Data!A1:G61</c>, or the name of the table or defined name it
    /// reads; null when the source is not a range of the workbook.
    /// </summary>
    public string? Source => Cache?.SourceText;

    /// <summary>
    /// Whether <see cref="Refresh"/> can work the pivot table out again: its source is a range of the workbook and Egap knows
    /// everything it shows, as do the other pivot tables of its cache.
    /// </summary>
    public bool CanRefresh => Problem is null;

    internal OpcPart Part { get; }

    internal PivotDefinition Definition { get; set; }

    internal PivotCache? Cache { get; }

    /// <summary>Whether a refresh changed the definition, so a save writes it again.</summary>
    internal bool Changed { get; set; }

    /// <summary>Why the pivot table cannot be refreshed, or null.</summary>
    internal string? Problem => Cache is null
        ? "The pivot table's cache could not be read."
        : Cache.Problem ?? Cache.Tables.Select(t => t.Definition.Problem).FirstOrDefault(p => p is not null);

    /// <summary>
    /// Reads the pivot table's source again and lays out every pivot table that shares its cache, as Excel's Refresh does:
    /// new values become new items, the values the source no longer has stay as missing items, and the tables' cells are
    /// written again from their top left cells. Formulas in the source give the values they last calculated.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The pivot table cannot be refreshed (see <see cref="CanRefresh"/>), its source cannot be found or no longer has the
    /// cache's fields as its header row, or a table would grow over another pivot table or off the sheet. Nothing changes.
    /// </exception>
    public void Refresh()
    {
        if (Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        Worksheet.Workbook.Pivots.Refresh(Cache!);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Worksheet.Name}!{Range})";
}

/// <summary>The pivot tables and caches of a workbook read from an .xlsx file, read when first needed.</summary>
internal sealed class PivotModel
{
    private const string PivotTableType = "/pivotTable";
    private const string CacheDefinitionType = "/pivotCacheDefinition";

    private readonly Workbook book;
    private readonly Dictionary<Worksheet, List<PivotTable>> tables = [];

    private PivotModel(Workbook book) => this.book = book;

    public List<PivotCache> Caches { get; } = [];

    public static PivotModel Load(Workbook book)
    {
        PivotModel model = new(book);
        Dictionary<string, PivotCache?> caches = new(StringComparer.OrdinalIgnoreCase);
        if (book.Source is { } source)
        {
            foreach (Worksheet sheet in book.Worksheets)
            {
                if (sheet.Source is not { } sheetSource)
                {
                    continue;
                }

                foreach (OpcRelationship relationship in sheetSource.Part.Relationships)
                {
                    if (!relationship.External && relationship.Type.EndsWith(PivotTableType, StringComparison.Ordinal)
                        && source.Package.GetPart(relationship.TargetName) is { } part)
                    {
                        model.Add(sheet, source.Package, part, caches);
                    }
                }
            }
        }
        else if (book.MadePivots?.Invoke() is { } made)
        {
            // An XLS file's pivot tables, made into the parts an .xlsx file holds them in.
            foreach ((Worksheet sheet, OpcPart part) in made.Tables)
            {
                model.Add(sheet, made.Package, part, caches);
            }
        }

        return model;
    }

    private void Add(Worksheet sheet, OpcPackage package, OpcPart part, Dictionary<string, PivotCache?> caches)
    {
        if (Xlsx.SafeXml.TryLoad(part) is not { Root: not null } document)
        {
            return;
        }

        PivotDefinition definition = new(document);
        if (definition.Location is null)
        {
            return;
        }

        PivotCache? cache = null;
        OpcRelationship? cacheRelationship = part.Relationships.FirstOrDefault(r => !r.External && r.Type.EndsWith(CacheDefinitionType, StringComparison.Ordinal));
        if (cacheRelationship is not null && !caches.TryGetValue(cacheRelationship.TargetName, out cache))
        {
            if (package.GetPart(cacheRelationship.TargetName) is { } cachePart && Xlsx.SafeXml.TryLoad(cachePart) is { Root: not null } cacheDocument)
            {
                cache = new PivotCache(package, cachePart, cacheDocument, book.Date1904);
                Caches.Add(cache);
            }

            caches[cacheRelationship.TargetName] = cache;
        }

        PivotTable table = new(sheet, part, definition, cache);
        cache?.Tables.Add(table);
        if (!tables.TryGetValue(sheet, out List<PivotTable>? list))
        {
            tables[sheet] = list = [];
        }

        list.Add(table);
    }

    public IReadOnlyList<PivotTable> Tables(Worksheet sheet) => tables.TryGetValue(sheet, out List<PivotTable>? list) ? list : [];

    /// <summary>
    /// Refreshes a cache and lays out its tables: everything is worked out first, so a failure changes nothing.
    /// </summary>
    public void Refresh(PivotCache cache)
    {
        List<PivotTable> members = [.. cache.Tables.Where(t => book.Worksheets.Any(s => s == t.Worksheet))];
        if (members.Select(t => t.Definition.Problem).FirstOrDefault(p => p is not null) is { } invalid)
        {
            throw new InvalidOperationException(invalid);
        }

        HashSet<int> axisFields = [];
        foreach (PivotTable table in members)
        {
            axisFields.UnionWith(table.Definition.RowFields.Concat(table.Definition.ColumnFields).Where(f => f >= 0));
            axisFields.UnionWith(table.Definition.PageFields.Select(p => p.Field));
            axisFields.UnionWith(table.Definition.Fields.Select((f, i) => (f, i)).Where(p => p.f.ItemsElement is not null).Select(p => p.i));
        }

        PivotCacheData data = cache.Read(book, axisFields);
        List<(PivotTable Table, PivotDefinition Definition, PivotLayout Layout)> plans = [];
        foreach (PivotTable table in members)
        {
            XDocument document = new(table.Definition.Document);
            if (data.OldFields is { } oldFields)
            {
                PivotDefinition.RemapFields(document, oldFields);
            }

            PivotDefinition copy = new(document);
            if (copy.Problem is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            plans.Add((table, copy, PivotLayout.Build(copy, data, cache.Fields, book)));
        }

        // A table may not grow over another; Excel refuses such a refresh too.
        foreach ((PivotTable table, _, PivotLayout layout) in plans)
        {
            foreach (PivotTable other in Tables(table.Worksheet))
            {
                if (other == table)
                {
                    continue;
                }

                List<RangeAddress> taken = plans.FirstOrDefault(p => p.Table == other).Layout is { } planned
                    ? Areas(planned.Range, planned.PageArea)
                    : Areas(other.Range, PageArea(other.Definition));
                if (Areas(layout.Range, layout.PageArea).Any(a => taken.Any(a.Intersects)))
                {
                    throw new InvalidOperationException($"The pivot table {table.Name} would overlap the pivot table {other.Name}.");
                }
            }
        }

        cache.Apply(data, book);

        // Every old area is cleared before any table is written, as one table may grow into cells another no longer takes.
        foreach ((PivotTable table, _, _) in plans)
        {
            foreach (RangeAddress old in Areas(table.Range, PageArea(table.Definition)))
            {
                table.Worksheet.Range(old).ClearContents();
            }
        }

        foreach ((PivotTable table, PivotDefinition definition, PivotLayout layout) in plans)
        {
            Worksheet sheet = table.Worksheet;
            CellAddress anchor = table.Range.First;
            foreach ((int row, int column, CellValue value, string? format) in layout.Cells)
            {
                Cell cell = sheet[anchor.Row + row, anchor.Column + column];
                cell.Value = value;
                if (format is not null && cell.NumberFormat != format)
                {
                    cell.NumberFormat = format;
                }
            }

            definition.Range = layout.Range;
            table.Definition = definition;
            table.Changed = true;
        }
    }

    /// <summary>The parts a save writes again, with their new XML.</summary>
    public IEnumerable<(OpcPart Part, XDocument Document)> ChangedParts()
    {
        foreach (PivotCache cache in Caches)
        {
            if (cache.Changed)
            {
                yield return (cache.DefinitionPart, cache.Definition);
            }

            if (cache.NewRecords is { } records && cache.RecordsPart is { } part)
            {
                yield return (part, records);
            }
        }

        foreach (PivotTable table in tables.Values.SelectMany(t => t))
        {
            if (table.Changed)
            {
                yield return (table.Part, table.Definition.Document);
            }
        }
    }

    private static List<RangeAddress> Areas(RangeAddress range, RangeAddress? pages) => pages is { } p ? [range, p] : [range];

    /// <summary>Where a table's page fields are: a row each above it, with a row between.</summary>
    private static RangeAddress? PageArea(PivotDefinition definition)
    {
        int count = definition.PageFields.Count;
        CellAddress anchor = definition.Range.First;
        return count == 0 || anchor.Row - 1 - count < 0
            ? null
            : new RangeAddress(anchor.Row - 1 - count, anchor.Column, anchor.Row - 2, Math.Min(anchor.Column + 1, CellAddress.MaxColumns - 1));
    }
}
