using Egap.Documents.Charts;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>Fills a chart's kept values from the cells its references name, as Excel does when it saves.</summary>
internal static class ChartCells
{
    private const int MaxAreas = 256;
    private const int WholeColumn = 1048576;
    private const int WholeRow = 16384;

    /// <summary>What one chart may read before it keeps the rest of its values as they are: a file cannot make a save run for hours.</summary>
    private sealed class Budget
    {
        public long Cells { get; set; } = 1L << 24;
    }

    public static void Fill(Chart chart, Workbook book)
    {
        Budget budget = new();
        bool visibleOnly = chart.PlotVisibleOnly != false;
        foreach (ChartPlot plot in chart.Plots)
        {
            foreach (ChartSeries series in plot.Series)
            {
                if (series.NameReference is { } name && Read(book, name, visibleOnly, budget) is { } nameCells)
                {
                    series.Name = string.Join(' ', nameCells.SelectMany(a => a).Where(c => !c.Value.IsEmpty).Select(c => c.Text));
                    series.NameReference = name;
                }

                FillCategories(series.Categories, book, visibleOnly, budget);
                FillNumbers(series.Values, book, visibleOnly, budget);
                if (series.BubbleSizes.Reference is not null)
                {
                    FillNumbers(series.BubbleSizes, book, visibleOnly, budget);
                }

                foreach (ChartErrorBars bars in series.ErrorBars)
                {
                    FillNumbers(bars.Plus, book, visibleOnly, budget);
                    FillNumbers(bars.Minus, book, visibleOnly, budget);
                }
            }
        }

        if (chart.HasTitle && chart.Title.Reference is { } titleReference && Read(book, titleReference, visibleOnly, budget) is { } titleCells)
        {
            FillTitle(chart.Title, titleReference, titleCells);
        }

        foreach (ChartAxis axis in chart.Axes)
        {
            if (axis.HasTitle && axis.Title.Reference is { } axisReference && Read(book, axisReference, visibleOnly, budget) is { } axisCells)
            {
                FillTitle(axis.Title, axisReference, axisCells);
            }
        }
    }

    /// <summary>The references of a range as a chart writes them, such as <c>'My sheet'!$A$1:$B$2</c>.</summary>
    public static string Reference(Worksheet sheet, RangeAddress range)
    {
        string name = sheet.Name.All(c => char.IsLetterOrDigit(c) || c is '_') && !char.IsDigit(sheet.Name[0])
            ? sheet.Name
            : "'" + sheet.Name.Replace("'", "''", StringComparison.Ordinal) + "'";
        string first = Absolute(range.First);
        return range.First == range.Last ? name + "!" + first : name + "!" + first + ":" + Absolute(range.Last);
    }

    private static string Absolute(CellAddress cell)
    {
        string text = cell.ToString();
        int digits = text.IndexOfAny(['0', '1', '2', '3', '4', '5', '6', '7', '8', '9']);
        return "$" + text[..digits] + "$" + text[digits..];
    }

    private static void FillTitle(ChartTitle title, string reference, List<List<(CellValue Value, string Text, string Format)>> cells)
    {
        // A title's reference keeps no text of its own in this model's writing; the first cell's goes in its cache.
        title.Text = string.Join(' ', cells.SelectMany(a => a).Where(c => !c.Value.IsEmpty).Select(c => c.Text));
        title.Reference = reference;
    }

    private static void FillNumbers(ChartData data, Workbook book, bool visibleOnly, Budget budget)
    {
        if (data.Reference is { } reference && Read(book, reference, visibleOnly, budget) is { } cells)
        {
            List<(CellValue Value, string Text, string Format)> flat = [.. cells.SelectMany(a => a)];

            // Excel plots a cell of text as zero, and leaves a gap for an empty cell or an error.
            data.SetNumbers(
                flat.Select(c => c.Value.Kind switch
                {
                    CellValueKind.Number => c.Value.Number,
                    CellValueKind.Text => 0,
                    _ => (double?)null,
                }),
                flat.Count > 0 ? flat[0].Format : null);
        }
    }

    private static void FillCategories(ChartData data, Workbook book, bool visibleOnly, Budget budget)
    {
        if (data.Reference is not { } reference || Read(book, reference, visibleOnly, budget) is not { } cells)
        {
            return;
        }

        if (cells.Count > 1)
        {
            // Labels in levels, outermost first in the cells, innermost first in the file. Excel repeats an outer label only
            // where its cell has one.
            data.SetTextLevels(Enumerable.Reverse(cells).Select(level => level.Select(c => c.Value.IsEmpty ? null : c.Text)));
            return;
        }

        List<(CellValue Value, string Text, string Format)> flat = cells[0];
        if (flat.Any(c => c.Value.Kind == CellValueKind.Number) && flat.All(c => c.Value.Kind is CellValueKind.Number or CellValueKind.Empty))
        {
            data.SetNumbers(flat.Select(c => c.Value.IsEmpty ? null : (double?)c.Value.Number), flat[0].Format);
        }
        else
        {
            data.SetText(flat.Select(c => c.Value.IsEmpty ? null : c.Text));
        }
    }

    /// <summary>
    /// The cells of a reference in order, in levels when one area spans rows and columns (a level to each of the shorter side);
    /// null when it names nothing that can be read.
    /// </summary>
    private static List<List<(CellValue Value, string Text, string Format)>>? Read(Workbook book, string reference, bool visibleOnly, Budget budget)
    {
        if (budget.Cells <= 0)
        {
            return null;
        }

        List<(Worksheet Sheet, RangeAddress Range)> areas = [];
        if (!Resolve(book, reference, areas, 0) || areas.Count == 0)
        {
            return null;
        }

        // One reference gets as many cells as a chart can keep, however it reaches them.
        int left = ChartData.MaxPoints;
        List<List<(CellValue Value, string Text, string Format)>> levels = [];
        if (areas.Count == 1 && areas[0].Range is { RowCount: > 1, ColumnCount: > 1 } grid)
        {
            Worksheet sheet = areas[0].Sheet;
            bool byColumn = grid.RowCount >= grid.ColumnCount;
            int count = byColumn ? grid.ColumnCount : grid.RowCount;
            for (int i = 0; i < count && left > 0; i++)
            {
                RangeAddress line = byColumn
                    ? new RangeAddress(grid.First.Row, grid.First.Column + i, grid.Last.Row, grid.First.Column + i)
                    : new RangeAddress(grid.First.Row + i, grid.First.Column, grid.First.Row + i, grid.Last.Column);
                List<(CellValue Value, string Text, string Format)> level = [];
                Cells(sheet, line, visibleOnly, level, ref left);
                levels.Add(level);
            }
        }
        else
        {
            List<(CellValue Value, string Text, string Format)> level = [];
            foreach ((Worksheet sheet, RangeAddress range) in areas)
            {
                Cells(sheet, range, visibleOnly, level, ref left);
            }

            levels.Add(level);
        }

        budget.Cells -= ChartData.MaxPoints - left;
        return levels;
    }

    private static void Cells(Worksheet sheet, RangeAddress range, bool visibleOnly, List<(CellValue Value, string Text, string Format)> into, ref int left)
    {
        RangeAddress? used = sheet.UsedRange?.Address;
        int usedRow = used?.Last.Row ?? -1;
        int usedColumn = used?.Last.Column ?? -1;

        // Empty cells past the sheet's last are points too, as Excel counts them, but a whole column or row reaches only
        // as far as the sheet's cells.
        int lastRow = range.RowCount >= WholeColumn ? Math.Min(range.Last.Row, usedRow) : range.Last.Row;
        int lastColumn = range.ColumnCount >= WholeRow ? Math.Min(range.Last.Column, usedColumn) : range.Last.Column;
        for (int row = range.First.Row; row <= lastRow; row++)
        {
            for (int column = range.First.Column; column <= lastColumn; column++)
            {
                if (left-- <= 0)
                {
                    left = 0;
                    return;
                }

                if (row <= usedRow && column <= usedColumn)
                {
                    if (!visibleOnly || (!sheet.Rows[row].Hidden && !sheet.Columns[column].Hidden))
                    {
                        Cell cell = sheet[row, column];
                        into.Add((cell.Value, cell.Text, cell.NumberFormat));
                    }
                }
                else
                {
                    into.Add((default, "", "General"));
                }
            }
        }
    }

    private static bool Resolve(Workbook book, string reference, List<(Worksheet Sheet, RangeAddress Range)> areas, int depth)
    {
        string text = reference.Trim();
        if (text.StartsWith('='))
        {
            text = text[1..];
        }

        while (text.StartsWith('(') && text.EndsWith(')'))
        {
            text = text[1..^1];
        }

        foreach (string part in SplitAreas(text))
        {
            if (areas.Count >= MaxAreas || !ResolveOne(book, part.Trim(), areas, depth))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ResolveOne(Workbook book, string text, List<(Worksheet Sheet, RangeAddress Range)> areas, int depth)
    {
        int bang = text.LastIndexOf('!');
        if (bang > 0)
        {
            string sheetName = text[..bang];
            if (sheetName.StartsWith('\'') && sheetName.EndsWith('\'') && sheetName.Length >= 2)
            {
                sheetName = sheetName[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            Worksheet? sheet = book.Worksheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase));
            if (sheet is not null && RangeAddress.TryParse(text[(bang + 1)..].Replace("$", "", StringComparison.Ordinal), out RangeAddress range))
            {
                areas.Add((sheet, range));
                return true;
            }

            if (sheet is not null)
            {
                return Named(book, text[(bang + 1)..], sheet, areas, depth);
            }

            return sheetName is "[0]" && Named(book, text[(bang + 1)..], null, areas, depth);
        }

        return Named(book, text, null, areas, depth);
    }

    private static bool Named(Workbook book, string name, Worksheet? sheet, List<(Worksheet Sheet, RangeAddress Range)> areas, int depth) =>
        depth < 4 && book.Names.TryGet(name, sheet, out DefinedName? found) && Resolve(book, found.Formula, areas, depth + 1);

    private static List<string> SplitAreas(string text)
    {
        List<string> parts = [];
        bool quoted = false;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (text[i] == ',' && !quoted)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }
}
