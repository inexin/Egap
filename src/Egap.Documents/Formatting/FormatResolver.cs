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

    /// <summary>A cell's table style levels, with and without the regions about columns, and what they depend on.</summary>
    internal sealed class LayerCache(CacheKey key)
    {
        public CacheKey Key { get; } = key;

        public List<TableLayer>? All { get; set; }

        public List<TableLayer>? RowOnly { get; set; }
    }

    private static readonly List<TableLayer> NoLayers = [];
    private static readonly object True = true;
    private static readonly object False = false;

    /// <summary>One level of a table style for a cell: the whole table or one region, of one style in the BasedOn chain.</summary>
    internal readonly record struct TableLayer(TableFormat Table, RowFormat Row, CellFormat Cell, ParagraphFormat Paragraph, RunFormat Run);

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

    /// <summary>A run's effective formatting as if it stood in <paramref name="paragraph"/>, for runs not in the tree (a paragraph mark, a list label).</summary>
    public static ResolvedRunFormat ResolveRunIn(Run run, Paragraph paragraph)
    {
        return ResolveRun(run, paragraph);
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
        ParagraphFormat? keepTableAlignment = null;
        if (paragraph.Parent is Cell cell)
        {
            List<TableLayer> layers = Layers(cell, rowOnly: false);
            levels.AddRange(layers.Select(l => l.Paragraph));

            // Without Word's overrideTableStyleFontSizeAndJustification, a left-aligned default paragraph style leaves the
            // table style's alignment in place.
            if (!document.Settings.WordCompatibility.OverrideTableStyleFontSizeAndJustification
                && document.Styles.DefaultParagraphStyle?.ParagraphFormat is { Alignment: ParagraphAlignment.Left } normal
                && layers.Exists(l => l.Paragraph.Alignment is not null))
            {
                keepTableAlignment = normal;
            }
        }

        levels.AddRange(Chain(StyleOf(paragraph)).Select(s => s.ParagraphFormat));

        // The list level's indents go between the styles and direct formatting (Word's order), once the list is known. A list
        // a style gives goes just below that style: its own indents (and its descendants') win over the level's, as in Word.
        List? list = paragraph.Format.List ?? levels.Select(l => l.List).LastOrDefault(l => l is not null);
        if (list is not null && list != List.None && paragraph.Document.Lists.Contains(list))
        {
            int level = paragraph.Format.ListLevel ?? levels.Select(l => l.ListLevel).LastOrDefault(l => l is not null) ?? 0;
            int at = paragraph.Format.List is null ? levels.FindLastIndex(l => l.List is not null) : levels.Count;
            levels.Insert(at < 0 ? levels.Count : at, list.GetLevel(level).ParagraphFormat);
        }

        levels.Add(paragraph.Format);
        object?[] values = new object?[ParagraphFormat.Count];
        List<TabStop> tabs = [];
        foreach (ParagraphFormat level in levels)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (level.GetRaw(i) is { } value && !(i == ParagraphFormat.AlignmentId && ReferenceEquals(level, keepTableAlignment)))
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
        List<TableLayer> table = paragraph?.Parent is Cell cell ? Layers(cell, rowOnly: false) : NoLayers;
        List<ParagraphStyle> paragraphStyle = paragraph is null ? Chain<ParagraphStyle>(null) : Chain(StyleOf(paragraph));
        List<CharacterStyle> characterStyle = Chain(StyleOf(run));

        // Without Word's overrideTableStyleFontSizeAndJustification, an 11 or 12 point default paragraph style leaves the
        // table style's font size in place.
        RunFormat? keepTableSize = table.Count > 0 && !document.Settings.WordCompatibility.OverrideTableStyleFontSizeAndJustification
            && document.Styles.DefaultParagraphStyle?.RunFormat is { Size: 11 or 12 } normal && table.Exists(l => l.Run.Size is not null) ? normal : null;

        object?[] values = new object?[RunFormat.Count];
        Apply(defaults);
        foreach (TableLayer level in table)
        {
            Apply(level.Run);
        }

        foreach (ParagraphStyle level in paragraphStyle)
        {
            Apply(level.RunFormat);
        }

        foreach (CharacterStyle level in characterStyle)
        {
            Apply(level.RunFormat);
        }

        Apply(run.Format);
        foreach (int id in RunFormat.Toggles)
        {
            values[id] = Toggle(id, defaults, table, paragraphStyle, characterStyle, run.Format) ? True : False;
        }

        return new ResolvedRunFormat(values, document.Theme);

        void Apply(RunFormat level)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (level.GetRaw(i) is { } value && !IsFontSlot(i) && !(i == RunFormat.SizeId && ReferenceEquals(level, keepTableSize)))
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
    }

    /// <summary>
    /// A toggle property: direct formatting sets it outright; otherwise each kind of style (table, paragraph, character) gives
    /// the value nearest in its own chain, and those combine by exclusive or; with no style setting it, the default holds.
    /// </summary>
    private static bool Toggle(int id, RunFormat defaults, List<TableLayer> table, List<ParagraphStyle> paragraph, List<CharacterStyle> character, RunFormat direct)
    {
        if (direct.GetRaw(id) is bool set)
        {
            return set;
        }

        bool? result = null;
        bool? value = null;
        foreach (TableLayer level in table)
        {
            value = level.Run.GetRaw(id) as bool? ?? value;
        }

        result = Combine(result, value);
        value = null;
        foreach (ParagraphStyle level in paragraph)
        {
            value = level.RunFormat.GetRaw(id) as bool? ?? value;
        }

        result = Combine(result, value);
        value = null;
        foreach (CharacterStyle level in character)
        {
            value = level.RunFormat.GetRaw(id) as bool? ?? value;
        }

        result = Combine(result, value);
        return result ?? (defaults.GetRaw(id) as bool? ?? false);

        static bool? Combine(bool? result, bool? value)
        {
            return value is bool b ? (result ?? false) ^ b : result;
        }
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
        if (cell.Row is not { Table: { } table } row)
        {
            return [];
        }

        // Cached on the cell: every paragraph and run in it asks for the same levels.
        CacheKey key = new(0, cell.Document.FormattingVersion, table.Stamp, row.IndexInParent, cell.IndexInParent, table.Rows.Count, row.Cells.Count);
        if (cell.LayerCache is not { } cache || cache.Key != key)
        {
            cache = new LayerCache(key);
            cell.LayerCache = cache;
        }

        return rowOnly ? cache.RowOnly ??= MakeLayers(cell, row, table, rowOnly) : cache.All ??= MakeLayers(cell, row, table, rowOnly);
    }

    private static List<TableLayer> MakeLayers(Cell cell, Row row, Table table, bool rowOnly)
    {
        List<TableLayer> layers = [];

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

    /// <summary>A style's BasedOn chain, base first, leaving out styles removed from the document; cached on the style, not to be changed.</summary>
    private static List<T> Chain<T>(T? style)
        where T : Style
    {
        if (style is null)
        {
            return EmptyChain<T>.Value;
        }

        long version = style.Document.FormattingVersion;
        if (style.ChainCache is { } cached && cached.FormattingVersion == version)
        {
            return (List<T>)cached.Chain;
        }

        List<T> chain = MakeChain(style);
        style.ChainCache = (version, chain);
        return chain;
    }

    private static List<T> MakeChain<T>(T style)
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

    private static class EmptyChain<T>
    {
        public static readonly List<T> Value = [];
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
