namespace Egap.Documents.Rtf;

/// <summary>
/// Tables. RTF has no table group: a row is its paragraphs marked <c>\intbl</c> (and <c>\itap</c> for nesting), each cell ended
/// by <c>\cell</c> and the row by <c>\row</c>, with the row's definition (<c>\trowd</c>, cell definitions ending at
/// <c>\cellx</c>) before the row, after it, or both. Each nesting level of a story builds its table here; the grid comes from
/// the cells' right edges.
/// </summary>
internal sealed partial class RtfReader
{
    private bool inNestTableProps;

    /// <summary>A row definition: the row and table formatting and the cells, each up to its <c>\cellx</c>.</summary>
    private sealed class RowDefinition
    {
        public RowFormat Row { get; } = new();

        public TableFormat Table { get; } = new();

        public List<CellDefinition> Cells { get; } = [];

        public CellDefinition Pending { get; set; } = new();

        public int Left { get; set; }

        public int? Gap { get; set; }

        public bool AutoFit { get; set; }

        public int WidthUnit { get; set; }

        public int Width { get; set; }

        public int Style { get; set; } = -1;

        public TablePosition? Position { get; set; }
    }

    /// <summary>A cell definition: its formatting, its right edge in twips, and how it merges.</summary>
    private sealed class CellDefinition
    {
        public CellFormat Format { get; } = new();

        public int Right { get; set; }

        public bool MergeFirst { get; set; }

        public bool Merged { get; set; }

        public int WidthUnit { get; set; }

        public int Width { get; set; }
    }

    /// <summary>A built cell with its left and right edges in twips, for the grid.</summary>
    private readonly record struct PlacedCell(Cell Cell, int Left, int Right);

    /// <summary>The table being built at one nesting level of a story.</summary>
    private sealed class TableLevel
    {
        public RowDefinition Definition { get; set; } = new();

        public Table? Table { get; set; }

        public Cell? Current { get; set; }

        public List<Cell> Cells { get; } = [];

        public List<List<PlacedCell>> Rows { get; } = [];
    }

    private TableLevel LevelAt(int level)
    {
        while (story.Tables.Count < level)
        {
            story.Tables.Add(new TableLevel());
        }

        return story.Tables[level - 1];
    }

    /// <summary>The level the row definition words that follow describe.</summary>
    private int DefinitionLevel => inNestTableProps ? Math.Max(2, state.Itap) : 1;

    private RowDefinition Definition => LevelAt(DefinitionLevel).Definition;

    /// <summary>Where blocks at table level <paramref name="level"/> go: the story, or the current cell of that level's table.</summary>
    private NodeCollection<Block> Container(int level)
    {
        level = Math.Min(level, Math.Max(1, options.Limits.MaxDepth / 4));
        CloseTablesAbove(level);
        if (level == 0)
        {
            return story.Blocks;
        }

        TableLevel table = LevelAt(level);
        if (table.Table is null)
        {
            NodeCollection<Block> parent = Container(level - 1);
            table.Table = parent.AddTrusted(new Table(document));
        }

        if (table.Current is null)
        {
            table.Current = new Cell(document);
        }

        return table.Current.Blocks;
    }

    private void EndCell(int level)
    {
        FlushText();
        // A cell ends with a paragraph, as in Word: the cell mark after a nested table is an empty one.
        EndParagraph(level, force: story.Paragraph is not null || LevelAt(level).Current is not { Blocks: [.., Paragraph] });
        TableLevel table = LevelAt(level);
        if (table.Current is not null)
        {
            table.Cells.Add(table.Current);
            table.Current = null;
        }
    }

    private void EndRow(int level)
    {
        FlushText();
        if (story.Paragraph is { } paragraph && paragraph.Inlines.Count > 0)
        {
            EndParagraph(level);
        }

        story.Paragraph = null;
        CloseTablesAbove(level);
        TableLevel table = LevelAt(level);
        if (table.Current is { } current)
        {
            // Content after the last \cell: a cell of its own.
            table.Cells.Add(current);
            table.Current = null;
        }

        if (table.Cells.Count == 0)
        {
            return;
        }

        if (table.Table is null)
        {
            NodeCollection<Block> parent = Container(level - 1);
            table.Table = parent.AddTrusted(new Table(document));
        }

        AddRow(table);
    }

    /// <summary>Makes a row from the cells read and the row definition, merging cells across where the definition says so.</summary>
    private void AddRow(TableLevel level)
    {
        Table table = level.Table!;
        RowDefinition definition = level.Definition;
        if (table.Rows.Count == 0)
        {
            ApplyTableFormat(table, definition);
        }

        Row row = table.Rows.AddTrusted(new Row(document));
        row.Format.CopyRaw(definition.Row);
        List<PlacedCell> placed = [];
        int left = definition.Left;
        for (int i = 0; i < level.Cells.Count; i++)
        {
            Cell cell = level.Cells[i];
            CellDefinition? cellDefinition = i < definition.Cells.Count ? definition.Cells[i] : null;
            int right = cellDefinition?.Right ?? left;
            if (cellDefinition is { Merged: true } && placed.Count > 0)
            {
                // A cell merged into the one before it: Word keeps its text in the first.
                PlacedCell previous = placed[^1];
                placed[^1] = previous with { Right = Math.Max(previous.Right, right) };
                left = Math.Max(left, right);
                continue;
            }

            if (cellDefinition is not null)
            {
                cell.Format.CopyRaw(cellDefinition.Format);
                if (cellDefinition.WidthUnit is 2 or 3 && cellDefinition.Width > 0)
                {
                    cell.Format.Width = cellDefinition.WidthUnit == 2 ? TableWidth.Percent(cellDefinition.Width / 50.0) : TableWidth.Points(Twips(cellDefinition.Width));
                }
                else if (right > left)
                {
                    cell.Format.Width = TableWidth.Points(((long)right - left) / 20.0);
                }
            }

            row.Cells.AddTrusted(cell);
            placed.Add(new PlacedCell(cell, left, Math.Max(left, right)));
            left = Math.Max(left, right);
        }

        level.Rows.Add(placed);
        level.Cells.Clear();
    }

    private void ApplyTableFormat(Table table, RowDefinition definition)
    {
        table.Format.CopyRaw(definition.Table);
        table.Format.Layout = definition.AutoFit ? TableLayoutKind.Autofit : TableLayoutKind.Fixed;
        table.Format.Indent ??= Twips(definition.Left + (definition.Gap ?? 0));
        if (definition.Gap is int gap)
        {
            table.Format.CellMarginLeft ??= Math.Max(0, Twips(gap));
            table.Format.CellMarginRight ??= Math.Max(0, Twips(gap));
        }

        if (definition.WidthUnit is 2 or 3 && definition.Width > 0)
        {
            table.Format.Width = definition.WidthUnit == 2 ? TableWidth.Percent(definition.Width / 50.0) : TableWidth.Points(Twips(definition.Width));
        }

        if (definition.Position is { } position)
        {
            table.Format.Position = position;
        }

        if (styles.GetValueOrDefault(definition.Style) is TableStyle style)
        {
            table.Style = style;
        }
    }

    /// <summary>Ends the tables nested deeper than <paramref name="level"/>.</summary>
    private void CloseTablesAbove(int level)
    {
        for (int i = story.Tables.Count; i > level; i--)
        {
            TableLevel table = story.Tables[i - 1];
            if (table.Table is null)
            {
                continue;
            }

            if (table.Current is not null || table.Cells.Count > 0)
            {
                // A row the file never ended.
                if (table.Current is { } current)
                {
                    table.Cells.Add(current);
                    table.Current = null;
                }

                AddRow(table);
            }

            FinishTable(table);
        }
    }

    /// <summary>Sets the table's grid from its cells' edges and the span of each cell over it.</summary>
    private static void FinishTable(TableLevel level)
    {
        Table table = level.Table!;
        level.Table = null;
        if (table.Rows.Count == 0)
        {
            table.Remove();
            level.Rows.Clear();
            return;
        }

        SortedSet<int> edges = [];
        foreach (List<PlacedCell> row in level.Rows)
        {
            foreach (PlacedCell cell in row)
            {
                edges.Add(cell.Left);
                edges.Add(cell.Right);
            }
        }

        int[] grid = [.. edges];
        if (grid.Length >= 2 && grid.Length <= 64)
        {
            double[] widths = new double[grid.Length - 1];
            for (int i = 0; i < widths.Length; i++)
            {
                // In long: edges far apart, such as a huge negative \trleft, overflow an int.
                widths[i] = ((long)grid[i + 1] - grid[i]) / 20.0;
            }

            table.Grid = widths;
            foreach (List<PlacedCell> row in level.Rows)
            {
                foreach (PlacedCell cell in row)
                {
                    int span = Array.BinarySearch(grid, cell.Right) - Array.BinarySearch(grid, cell.Left);
                    if (span > 1)
                    {
                        cell.Cell.Format.GridSpan = span;
                    }
                }
            }
        }

        level.Rows.Clear();
    }

    // ---- Row and cell definition words ----

    private bool TableWord(string word, bool has, int p)
    {
        if (!(word.StartsWith("tr", StringComparison.Ordinal) || word.StartsWith("cl", StringComparison.Ordinal)
            || word.StartsWith("tbl", StringComparison.Ordinal) || word.StartsWith("tp", StringComparison.Ordinal)
            || word.StartsWith("tpos", StringComparison.Ordinal) || word.StartsWith("tdfrmtxt", StringComparison.Ordinal)
            || word is "cellx" or "ts" or "rtlrow" or "ltrrow" or "lastrow"))
        {
            return false;
        }

        if (word == "trowd")
        {
            LevelAt(DefinitionLevel).Definition = new RowDefinition();
            state.Border = BorderTarget.None;
            return true;
        }

        RowDefinition definition = Definition;
        CellDefinition cell = definition.Pending;
        bool on = !has || p != 0;
        switch (word)
        {
            case "trgaph" when has:
                definition.Gap = p;
                return true;
            case "trleft" when has:
                definition.Left = p;
                return true;
            case "trrh" when has:
                definition.Row.Height = p == 0 ? null : Twips(Math.Abs(p));
                definition.Row.HeightRule = p == 0 ? RowHeightRule.Auto : p > 0 ? RowHeightRule.AtLeast : RowHeightRule.Exactly;
                return true;
            case "trhdr":
                definition.Row.IsHeader = on;
                return true;
            case "trkeep":
                definition.Row.CantSplit = on;
                return true;
            case "trql" or "trqc" or "trqr":
                TableAlignment alignment = word switch { "trqc" => TableAlignment.Center, "trqr" => TableAlignment.Right, _ => TableAlignment.Left };
                definition.Row.Alignment = alignment;
                definition.Table.Alignment = alignment;
                return true;
            case "trautofit":
                definition.AutoFit = on;
                return true;
            case "trftsWidth" when has:
                definition.WidthUnit = p;
                return true;
            case "trwWidth" when has:
                definition.Width = p;
                return true;
            case "trpaddl" or "trpaddr" or "trpaddt" or "trpaddb" when has:
                SetCellMargin(definition.Table, word[^1], p);
                return true;
            case "trspdl" when has && p > 0:
                definition.Table.CellSpacing = Twips(p);
                definition.Row.CellSpacing = Twips(p);
                return true;
            case "tblind" when has:
                definition.Table.Indent = Twips(p);
                return true;
            case "trbrdrt":
                state.Border = BorderTarget.TableTop;
                return true;
            case "trbrdrb":
                state.Border = BorderTarget.TableBottom;
                return true;
            case "trbrdrl":
                state.Border = BorderTarget.TableLeft;
                return true;
            case "trbrdrr":
                state.Border = BorderTarget.TableRight;
                return true;
            case "trbrdrh":
                state.Border = BorderTarget.TableInsideHorizontal;
                return true;
            case "trbrdrv":
                state.Border = BorderTarget.TableInsideVertical;
                return true;
            case "trcbpat" when has:
                definition.Table.Shading = UpdateShading(definition.Table.Shading, fill: Color(p));
                return true;
            case "trcfpat" when has:
                definition.Table.Shading = UpdateShading(definition.Table.Shading, patternColor: Color(p));
                return true;
            case "trshdng" when has:
                definition.Table.Shading = UpdateShading(definition.Table.Shading, percent: p);
                return true;
            case "ts" when has:
                definition.Style = p;
                return true;
            case "rtlrow":
                definition.Table.Bidi = true;
                return true;
            case "ltrrow":
                definition.Table.Bidi = null;
                return true;
            case "tbllkhdrrows":
                definition.Table.LookFirstRow = on;
                return true;
            case "tbllklastrow":
                definition.Table.LookLastRow = on;
                return true;
            case "tbllkhdrcols":
                definition.Table.LookFirstColumn = on;
                return true;
            case "tbllklastcol":
                definition.Table.LookLastColumn = on;
                return true;
            case "tbllknorowband":
                definition.Table.LookNoRowBands = on;
                return true;
            case "tbllknocolband":
                definition.Table.LookNoColumnBands = on;
                return true;

            // Floating tables.
            case "tphcol" or "tphmrg" or "tphpg":
                definition.Position = (definition.Position ?? new TablePosition()) with
                {
                    HorizontalAnchor = word switch { "tphpg" => HorizontalAnchor.Page, "tphmrg" => HorizontalAnchor.Margin, _ => HorizontalAnchor.Column },
                };
                return true;
            case "tpvpara" or "tpvmrg" or "tpvpg":
                definition.Position = (definition.Position ?? new TablePosition()) with
                {
                    VerticalAnchor = word switch { "tpvpg" => VerticalAnchor.Page, "tpvmrg" => VerticalAnchor.Margin, _ => VerticalAnchor.Paragraph },
                };
                return true;
            case "tposx" or "tposnegx" when has:
                definition.Position = (definition.Position ?? new TablePosition()) with { HorizontalOffset = Twips(p), HorizontalAlignment = FloatingAlignment.None };
                return true;
            case "tposy" or "tposnegy" when has:
                definition.Position = (definition.Position ?? new TablePosition()) with { VerticalOffset = Twips(p), VerticalAlignment = FloatingAlignment.None };
                return true;
            case "tposxl" or "tposxc" or "tposxr" or "tposxi" or "tposxo":
                definition.Position = (definition.Position ?? new TablePosition()) with { HorizontalAlignment = FloatingAlignmentOf(word[^1]) };
                return true;
            case "tposyt" or "tposyc" or "tposyb" or "tposyin" or "tposyout" or "tposyil":
                definition.Position = (definition.Position ?? new TablePosition()) with
                {
                    VerticalAlignment = word switch
                    {
                        "tposyt" => FloatingAlignment.Start,
                        "tposyc" => FloatingAlignment.Center,
                        "tposyb" => FloatingAlignment.End,
                        "tposyin" => FloatingAlignment.Inside,
                        "tposyout" => FloatingAlignment.Outside,
                        _ => FloatingAlignment.None,
                    },
                };
                return true;
            case "tdfrmtxtLeft" when has:
                definition.Position = (definition.Position ?? new TablePosition()) with { DistanceLeft = Twips(p) };
                return true;
            case "tdfrmtxtRight" when has:
                definition.Position = (definition.Position ?? new TablePosition()) with { DistanceRight = Twips(p) };
                return true;
            case "tdfrmtxtTop" when has:
                definition.Position = (definition.Position ?? new TablePosition()) with { DistanceTop = Twips(p) };
                return true;
            case "tdfrmtxtBottom" when has:
                definition.Position = (definition.Position ?? new TablePosition()) with { DistanceBottom = Twips(p) };
                return true;

            // Cells.
            case "clmgf":
                cell.MergeFirst = true;
                return true;
            case "clmrg":
                cell.Merged = true;
                return true;
            case "clvmgf":
                cell.Format.VerticalMerge = VerticalMerge.Restart;
                return true;
            case "clvmrg":
                cell.Format.VerticalMerge = VerticalMerge.Continue;
                return true;
            case "clvertalt" or "clvertalc" or "clvertalb":
                cell.Format.VerticalAlignment = word switch { "clvertalc" => VerticalAlignment.Center, "clvertalb" => VerticalAlignment.Bottom, _ => VerticalAlignment.Top };
                return true;
            case "clbrdrt":
                state.Border = BorderTarget.CellTop;
                return true;
            case "clbrdrb":
                state.Border = BorderTarget.CellBottom;
                return true;
            case "clbrdrl":
                state.Border = BorderTarget.CellLeft;
                return true;
            case "clbrdrr":
                state.Border = BorderTarget.CellRight;
                return true;
            case "cldglu":
                state.Border = BorderTarget.CellDiagonalUp;
                return true;
            case "cldgll":
                state.Border = BorderTarget.CellDiagonalDown;
                return true;
            case "clcbpat" when has:
                cell.Format.Shading = UpdateShading(cell.Format.Shading, fill: Color(p));
                return true;
            case "clcfpat" when has:
                cell.Format.Shading = UpdateShading(cell.Format.Shading, patternColor: Color(p));
                return true;
            case "clshdng" when has:
                cell.Format.Shading = UpdateShading(cell.Format.Shading, percent: p);
                return true;
            case "clftsWidth" when has:
                cell.WidthUnit = p;
                return true;
            case "clwWidth" when has:
                cell.Width = p;
                return true;

            // Word writes the left margin as \clpadt and the top as \clpadl, against the specification's names.
            case "clpadl" when has:
                cell.Format.MarginTop = Twips(Math.Max(0, p));
                return true;
            case "clpadt" when has:
                cell.Format.MarginLeft = Twips(Math.Max(0, p));
                return true;
            case "clpadr" when has:
                cell.Format.MarginRight = Twips(Math.Max(0, p));
                return true;
            case "clpadb" when has:
                cell.Format.MarginBottom = Twips(Math.Max(0, p));
                return true;
            case "clNoWrap":
                cell.Format.NoWrap = on;
                return true;
            case "clFitText":
                cell.Format.FitText = on;
                return true;
            case "clhidemark":
                cell.Format.HideMark = on;
                return true;
            case "cltxlrtb":
                cell.Format.TextDirection = TextDirection.Horizontal;
                return true;
            case "cltxtbrl" or "cltxtbrlv":
                cell.Format.TextDirection = TextDirection.TopToBottom;
                return true;
            case "cltxbtlr":
                cell.Format.TextDirection = TextDirection.BottomToTop;
                return true;
            case "cellx" when has:
                cell.Right = p;
                if (definition.Cells.Count < 63)
                {
                    definition.Cells.Add(cell);
                }

                definition.Pending = new CellDefinition();
                if (state.Border is >= BorderTarget.CellTop and <= BorderTarget.CellDiagonalUp)
                {
                    state.Border = BorderTarget.None;
                }

                return true;
        }

        // Other row and cell words (revision ids, padding units, the flat look) carry nothing the model keeps.
        return word.StartsWith("tr", StringComparison.Ordinal) || word.StartsWith("cl", StringComparison.Ordinal)
            || word.StartsWith("tbl", StringComparison.Ordinal) || word.StartsWith("tpos", StringComparison.Ordinal)
            || word.StartsWith("tdfrmtxt", StringComparison.Ordinal);
    }

    private static FloatingAlignment FloatingAlignmentOf(char c) => c switch
    {
        'l' => FloatingAlignment.Start,
        'c' => FloatingAlignment.Center,
        'r' => FloatingAlignment.End,
        'i' => FloatingAlignment.Inside,
        'o' => FloatingAlignment.Outside,
        _ => FloatingAlignment.None,
    };

    private void UpdateTableBorder(BorderTarget target, Func<Border, Border> change)
    {
        static Border Start(Border? border) => border ?? new Border(BorderStyle.Single);
        RowDefinition definition = Definition;
        TableFormat table = definition.Table;
        CellFormat cell = definition.Pending.Format;
        switch (target)
        {
            case BorderTarget.TableTop:
                table.BorderTop = change(Start(table.BorderTop));
                break;
            case BorderTarget.TableBottom:
                table.BorderBottom = change(Start(table.BorderBottom));
                break;
            case BorderTarget.TableLeft:
                table.BorderLeft = change(Start(table.BorderLeft));
                break;
            case BorderTarget.TableRight:
                table.BorderRight = change(Start(table.BorderRight));
                break;
            case BorderTarget.TableInsideHorizontal:
                table.BorderInsideHorizontal = change(Start(table.BorderInsideHorizontal));
                break;
            case BorderTarget.TableInsideVertical:
                table.BorderInsideVertical = change(Start(table.BorderInsideVertical));
                break;
            case BorderTarget.CellTop:
                cell.BorderTop = change(Start(cell.BorderTop));
                break;
            case BorderTarget.CellBottom:
                cell.BorderBottom = change(Start(cell.BorderBottom));
                break;
            case BorderTarget.CellLeft:
                cell.BorderLeft = change(Start(cell.BorderLeft));
                break;
            case BorderTarget.CellRight:
                cell.BorderRight = change(Start(cell.BorderRight));
                break;
            case BorderTarget.CellDiagonalDown:
                cell.BorderDiagonalDown = change(Start(cell.BorderDiagonalDown));
                break;
            case BorderTarget.CellDiagonalUp:
                cell.BorderDiagonalUp = change(Start(cell.BorderDiagonalUp));
                break;
        }
    }

    private void UpdateCellShading(Func<Shading?, Shading> change)
    {
        CellFormat cell = Definition.Pending.Format;
        cell.Shading = change(cell.Shading);
    }
}
