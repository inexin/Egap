namespace Egap.Documents;

/// <summary>
/// Effective formatting (Phase 6 design Q5): what a run, paragraph, table, row or cell looks like after document defaults,
/// the table style with its conditional regions, the paragraph style, the character style and direct formatting, in that
/// order, each style through its BasedOn chain. Toggle properties (bold, italic, caps and the like) from styles combine by
/// exclusive or as ECMA-376 17.7.3 says. Paragraph results, and the runs of the paragraph, are cached per paragraph.
/// </summary>
internal static class FormatResolver
{
    /// <summary>What a cached paragraph result depends on.</summary>
    internal readonly record struct CacheKey(long ParagraphVersion, long FormattingVersion, long TableStamp, int Row, int Cell, int RowCount, int CellCount);

    /// <summary>A paragraph's cached results.</summary>
    internal sealed class ParagraphCache(CacheKey key)
    {
        public CacheKey Key { get; } = key;

        public ResolvedParagraphFormat? Paragraph { get; set; }

        public Dictionary<Run, ResolvedRunFormat> Runs { get; } = [];
    }

    /// <summary>One level of a table style for a cell: the whole table or one region, of one style in the BasedOn chain.</summary>
    private readonly record struct TableLayer(TableFormat Table, RowFormat Row, CellFormat Cell, ParagraphFormat Paragraph, RunFormat Run);

    /// <summary>Regions in increasing precedence (ECMA-376 17.7.6): later ones win.</summary>
    private static readonly TableStyleRegion[] RegionOrder =
    [
        TableStyleRegion.OddColumnBand, TableStyleRegion.EvenColumnBand, TableStyleRegion.OddRowBand, TableStyleRegion.EvenRowBand,
        TableStyleRegion.LastColumn, TableStyleRegion.FirstColumn, TableStyleRegion.LastRow, TableStyleRegion.FirstRow,
        TableStyleRegion.TopRightCell, TableStyleRegion.TopLeftCell, TableStyleRegion.BottomRightCell, TableStyleRegion.BottomLeftCell,
    ];

    public static ResolvedParagraphFormat Resolve(Paragraph paragraph)
    {
        ParagraphCache cache = CacheFor(paragraph);
        return cache.Paragraph ??= ResolveParagraph(paragraph);
    }

    public static ResolvedRunFormat Resolve(Run run)
    {
        if (run.Paragraph is not { } paragraph)
        {
            return ResolveRun(run, null);
        }

        ParagraphCache cache = CacheFor(paragraph);
        if (!cache.Runs.TryGetValue(run, out ResolvedRunFormat? resolved))
        {
            resolved = ResolveRun(run, paragraph);
            cache.Runs[run] = resolved;
        }

        return resolved;
    }

    public static TableFormat Resolve(Table table)
    {
        TableFormat result = new();
        foreach (TableStyle style in Chain(StyleOf(table)))
        {
            Merge(result, style.TableFormat);
        }

        Merge(result, table.Format);
        return result;
    }

    public static RowFormat Resolve(Row row)
    {
        RowFormat result = new();
        if (row.Table is { } table && row.Cells.Count > 0)
        {
            foreach (TableLayer layer in Layers(row.Cells[0], rowOnly: true))
            {
                Merge(result, layer.Row);
            }
        }

        Merge(result, row.Format);
        return result;
    }

    public static CellFormat Resolve(Cell cell)
    {
        CellFormat result = new();
        foreach (TableLayer layer in Layers(cell, rowOnly: false))
        {
            Merge(result, layer.Cell);
        }

        Merge(result, cell.Format);
        return result;
    }

    private static ParagraphCache CacheFor(Paragraph paragraph)
    {
        Cell? cell = paragraph.Parent as Cell;
        Row? row = cell?.Row;
        Table? table = row?.Table;
        CacheKey key = new(
            paragraph.Version,
            paragraph.Document.FormattingVersion,
            table?.Stamp ?? 0,
            row is null ? -1 : row.IndexInParent,
            cell is null ? -1 : cell.IndexInParent,
            table?.Rows.Count ?? 0,
            row?.Cells.Count ?? 0);
        if (paragraph.FormatCache is not { } cache || cache.Key != key)
        {
            cache = new ParagraphCache(key);
            paragraph.FormatCache = cache;
        }

        return cache;
    }

    private static ResolvedParagraphFormat ResolveParagraph(Paragraph paragraph)
    {
        Document document = paragraph.Document;
        List<ParagraphFormat> levels = [document.Styles.DefaultParagraphFormat];
        if (paragraph.Parent is Cell cell)
        {
            levels.AddRange(Layers(cell, rowOnly: false).Select(l => l.Paragraph));
        }

        levels.AddRange(Chain(StyleOf(paragraph)).Select(s => s.ParagraphFormat));
        levels.Add(paragraph.Format);
        object?[] values = new object?[ParagraphFormat.Count];
        List<TabStop> tabs = [];
        foreach (ParagraphFormat level in levels)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (level.GetRaw(i) is { } value)
                {
                    values[i] = value;
                }
            }

            // Tab stops add up through the levels; a Clear stop removes the one at its position.
            if (level.TabStops is { } stops)
            {
                foreach (TabStop stop in stops)
                {
                    tabs.RemoveAll(t => Math.Abs(t.Position - stop.Position) < 0.01);
                    if (stop.Alignment != TabAlignment.Clear)
                    {
                        tabs.Add(stop);
                    }
                }
            }
        }

        tabs.Sort((a, b) => a.Position.CompareTo(b.Position));
        values[ParagraphFormat.TabStopsId] = tabs.ToArray();
        return new ResolvedParagraphFormat(values);
    }

    private static ResolvedRunFormat ResolveRun(Run run, Paragraph? paragraph)
    {
        Document document = run.Document;
        RunFormat defaults = document.Styles.DefaultRunFormat;
        List<RunFormat> table = paragraph?.Parent is Cell cell ? [.. Layers(cell, rowOnly: false).Select(l => l.Run)] : [];
        List<RunFormat> paragraphStyle = paragraph is null ? [] : [.. Chain(StyleOf(paragraph)).Select(s => s.RunFormat)];
        List<RunFormat> characterStyle = [.. Chain(StyleOf(run)).Select(s => s.RunFormat)];
        IEnumerable<RunFormat> all = [defaults, .. table, .. paragraphStyle, .. characterStyle, run.Format];

        object?[] values = new object?[RunFormat.Count];
        foreach (RunFormat level in all)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (level.GetRaw(i) is { } value && !IsFontSlot(i))
                {
                    values[i] = value;
                }
            }

            // A font slot's name and theme font come from the same level: the last level that sets either.
            for (int slot = 0; slot < 4; slot++)
            {
                int nameId = RunFormat.FontAsciiId + slot;
                int themeId = RunFormat.ThemeFontAsciiId + slot;
                if (level.GetRaw(nameId) is not null || level.GetRaw(themeId) is not null)
                {
                    values[nameId] = level.GetRaw(nameId);
                    values[themeId] = level.GetRaw(themeId);
                }
            }
        }

        foreach (int id in RunFormat.Toggles)
        {
            values[id] = Toggle(id, defaults, table, paragraphStyle, characterStyle, run.Format);
        }

        return new ResolvedRunFormat(values, document.Theme);
    }

    /// <summary>
    /// A toggle property: direct formatting sets it outright; otherwise each kind of style (table, paragraph, character) gives
    /// the value nearest in its own chain, and those combine by exclusive or; with no style setting it, the default holds.
    /// </summary>
    private static bool Toggle(int id, RunFormat defaults, List<RunFormat> table, List<RunFormat> paragraph, List<RunFormat> character, RunFormat direct)
    {
        if (direct.GetRaw(id) is bool set)
        {
            return set;
        }

        bool? result = null;
        foreach (List<RunFormat> group in (List<RunFormat>[])[table, paragraph, character])
        {
            bool? value = null;
            foreach (RunFormat level in group)
            {
                if (level.GetRaw(id) is bool v)
                {
                    value = v;
                }
            }

            if (value is bool b)
            {
                result = (result ?? false) ^ b;
            }
        }

        return result ?? (defaults.GetRaw(id) as bool? ?? false);
    }

    private static bool IsFontSlot(int id)
    {
        return id is >= RunFormat.FontAsciiId and <= RunFormat.FontComplexScriptId
            or >= RunFormat.ThemeFontAsciiId and <= RunFormat.ThemeFontComplexScriptId;
    }

    /// <summary>
    /// The table style levels that apply to <paramref name="cell"/>: the whole table for each style in the chain (base first),
    /// then each region that applies, in increasing precedence. With <paramref name="rowOnly"/>, only regions about rows.
    /// </summary>
    private static List<TableLayer> Layers(Cell cell, bool rowOnly)
    {
        List<TableLayer> layers = [];
        if (cell.Row is not { Table: { } table } row)
        {
            return layers;
        }

        List<TableStyle> chain = Chain(StyleOf(table));
        if (chain.Count == 0)
        {
            return layers;
        }

        foreach (TableStyle style in chain)
        {
            layers.Add(new TableLayer(style.TableFormat, style.RowFormat, style.CellFormat, style.ParagraphFormat, style.RunFormat));
        }

        TableFormat look = Resolve(table);
        bool firstRow = look.LookFirstRow ?? true;
        bool lastRow = look.LookLastRow ?? false;
        bool firstColumn = look.LookFirstColumn ?? true;
        bool lastColumn = look.LookLastColumn ?? false;
        bool rowBands = !(look.LookNoRowBands ?? false);
        bool columnBands = !(look.LookNoColumnBands ?? true);
        int rowIndex = row.IndexInParent;
        int rowCount = table.Rows.Count;
        int column = cell.IndexInParent;
        int columnCount = row.Cells.Count;
        bool isFirstRow = firstRow && rowIndex == 0;
        bool isLastRow = lastRow && rowIndex == rowCount - 1;
        bool isFirstColumn = !rowOnly && firstColumn && column == 0;
        bool isLastColumn = !rowOnly && lastColumn && column == columnCount - 1;
        int rowBand = (rowIndex - (firstRow ? 1 : 0)) / Math.Max(1, look.RowBandSize ?? 1);
        int columnBand = (column - (firstColumn ? 1 : 0)) / Math.Max(1, look.ColumnBandSize ?? 1);

        foreach (TableStyleRegion region in RegionOrder)
        {
            bool applies = region switch
            {
                TableStyleRegion.OddColumnBand => !rowOnly && columnBands && !isFirstColumn && !isLastColumn && columnBand >= 0 && columnBand % 2 == 0,
                TableStyleRegion.EvenColumnBand => !rowOnly && columnBands && !isFirstColumn && !isLastColumn && columnBand >= 0 && columnBand % 2 == 1,
                TableStyleRegion.OddRowBand => rowBands && !isFirstRow && !isLastRow && rowBand >= 0 && rowBand % 2 == 0,
                TableStyleRegion.EvenRowBand => rowBands && !isFirstRow && !isLastRow && rowBand >= 0 && rowBand % 2 == 1,
                TableStyleRegion.LastColumn => isLastColumn,
                TableStyleRegion.FirstColumn => isFirstColumn,
                TableStyleRegion.LastRow => isLastRow,
                TableStyleRegion.FirstRow => isFirstRow,
                TableStyleRegion.TopRightCell => isFirstRow && isLastColumn,
                TableStyleRegion.TopLeftCell => isFirstRow && isFirstColumn,
                TableStyleRegion.BottomRightCell => isLastRow && isLastColumn,
                TableStyleRegion.BottomLeftCell => isLastRow && isFirstColumn,
                _ => false,
            };
            if (!applies)
            {
                continue;
            }

            foreach (TableStyle style in chain)
            {
                TableStyleConditional part = style[region];
                layers.Add(new TableLayer(part.TableFormat, part.RowFormat, part.CellFormat, part.ParagraphFormat, part.RunFormat));
            }
        }

        return layers;
    }

    private static ParagraphStyle? StyleOf(Paragraph paragraph)
    {
        return paragraph.Style is { IsInDocument: true } s ? s : paragraph.Document.Styles.DefaultParagraphStyle;
    }

    private static CharacterStyle? StyleOf(Run run)
    {
        return run.Style is { IsInDocument: true } s ? s : run.Document.Styles.DefaultCharacterStyle;
    }

    private static TableStyle? StyleOf(Table table)
    {
        return table.Style is { IsInDocument: true } s ? s : table.Document.Styles.DefaultTableStyle;
    }

    /// <summary>A style's BasedOn chain, base first, leaving out styles removed from the document.</summary>
    private static List<T> Chain<T>(T? style)
        where T : Style
    {
        List<T> chain = [];
        for (Style? s = style; s is not null && chain.Count < 100; s = s.BasedOn)
        {
            if (s.IsInDocument)
            {
                chain.Add((T)s);
            }
        }

        chain.Reverse();
        return chain;
    }

    private static void Merge(FormatBase target, FormatBase level)
    {
        for (int i = 0; i < target.PropertyCount; i++)
        {
            if (level.GetRaw(i) is { } value)
            {
                target.SetRaw(i, value);
            }
        }
    }
}
