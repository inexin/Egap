using System.Text.RegularExpressions;
using System.Xml.Linq;

using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Ods;

/// <summary>
/// What-if tables: OpenFormula's MULTIPLE.OPERATIONS in each cell of a table (ODF 1.3 Part 4, 6.15.48), which Excel keeps as
/// one data table over the cells, its input cells named on the first (ECMA-376 Part 1, 18.3.1.40).
/// </summary>
internal static partial class OdsDataTables
{
    /// <summary>A MULTIPLE.OPERATIONS cell: the formula's cell, then each input cell with the cell its values come from.</summary>
    internal sealed record Operation(string Formula, string Input1, string From1, string? Input2, string? From2, CellValue Result)
    {
        public string Key => Strip(Input1) + "," + (Input2 is null ? "" : Strip(Input2));
    }

    /// <summary>The cell's operation, or null when the formula is not MULTIPLE.OPERATIONS of plain cell references.</summary>
    public static Operation? Parse(string excel, CellValue result)
    {
        if (!excel.StartsWith("MULTIPLE.OPERATIONS(", StringComparison.OrdinalIgnoreCase) || !excel.EndsWith(')'))
        {
            return null;
        }

        string[] parts = excel["MULTIPLE.OPERATIONS(".Length..^1].Split(',');
        if (parts.Length is not (3 or 5) || !parts.All(p => CellReference().IsMatch(p)))
        {
            return null;
        }

        return parts.Length == 3
            ? new Operation(parts[0], parts[1], parts[2], null, null, result)
            : new Operation(parts[0], parts[1], parts[2], parts[3], parts[4], result);
    }

    /// <summary>
    /// Stores the operations of a sheet: each rectangle of cells with the same input cells as a data table on its first cell,
    /// the other cells keeping their values. Cells that make no data table keep MULTIPLE.OPERATIONS as their formula.
    /// </summary>
    public static void Place(Worksheet sheet, Dictionary<(int Row, int Column), Operation> operations)
    {
        HashSet<(int, int)> done = [];
        foreach (((int row, int column), Operation first) in operations.OrderBy(o => o.Key.Row).ThenBy(o => o.Key.Column))
        {
            if (done.Contains((row, column)))
            {
                continue;
            }

            int lastColumn = column;
            while (Same(operations, done, row, lastColumn + 1, first.Key))
            {
                lastColumn++;
            }

            int lastRow = row;
            while (Enumerable.Range(column, lastColumn - column + 1).All(c => Same(operations, done, lastRow + 1, c, first.Key)))
            {
                lastRow++;
            }

            XAttribute[]? attributes = Attributes(first, new RangeAddress(row, column, lastRow, lastColumn));
            for (int r = row; r <= lastRow; r++)
            {
                for (int c = column; c <= lastColumn; c++)
                {
                    done.Add((r, c));
                    Operation operation = operations[(r, c)];
                    if (attributes is null)
                    {
                        sheet.SetRead(r, c, new CellRecord { Style = sheet.GetStyleIndex(r, c) }, new FormulaEntry(MultipleOperations(operation), operation.Result));
                    }
                    else if (r == row && c == column)
                    {
                        sheet.SetRead(r, c, new CellRecord { Style = sheet.GetStyleIndex(r, c) }, new FormulaEntry("", operation.Result) { Attributes = attributes });
                    }
                }
            }
        }
    }

    private static bool Same(Dictionary<(int Row, int Column), Operation> operations, HashSet<(int, int)> done, int row, int column, string key) =>
        !done.Contains((row, column)) && operations.TryGetValue((row, column), out Operation? operation) && operation.Key == key;

    /// <summary>
    /// Excel's attributes for the table: a table of one input takes its values down a column or along a row; a table of two
    /// takes the row input's values along its top row and the column input's down its left column.
    /// </summary>
    private static XAttribute[]? Attributes(Operation first, RangeAddress range)
    {
        XAttribute type = new("t", "dataTable");
        XAttribute reference = new("ref", range.ToString());
        if (first.Input2 is null)
        {
            return Along(first.From1) switch
            {
                Direction.Down => [type, reference, new XAttribute("dt2D", "0"), new XAttribute("dtr", "0"), new XAttribute("r1", Strip(first.Input1))],
                Direction.Across => [type, reference, new XAttribute("dt2D", "0"), new XAttribute("dtr", "1"), new XAttribute("r1", Strip(first.Input1))],
                _ => null,
            };
        }

        return (Along(first.From1), Along(first.From2!)) switch
        {
            (Direction.Across, Direction.Down) => [type, reference, new XAttribute("dt2D", "1"), new XAttribute("dtr", "0"),
                new XAttribute("r1", Strip(first.Input1)), new XAttribute("r2", Strip(first.Input2))],
            (Direction.Down, Direction.Across) => [type, reference, new XAttribute("dt2D", "1"), new XAttribute("dtr", "0"),
                new XAttribute("r1", Strip(first.Input2)), new XAttribute("r2", Strip(first.Input1))],
            _ => null,
        };
    }

    private enum Direction
    {
        None,
        Down,
        Across,
    }

    /// <summary>Where a table's input values lie: down a column when the column is fixed, along a row when the row is.</summary>
    private static Direction Along(string from)
    {
        bool column = from.StartsWith('$');
        bool row = from.IndexOf('$', 1) > 0;
        return column == row ? Direction.None : column ? Direction.Down : Direction.Across;
    }

    private static string Strip(string reference) => reference.Replace("$", "", StringComparison.Ordinal);

    private static string MultipleOperations(Operation o) =>
        "MULTIPLE.OPERATIONS(" + string.Join(",", new[] { o.Formula, o.Input1, o.From1, o.Input2, o.From2 }.OfType<string>()) + ")";

    [GeneratedRegex(@"^\$?[A-Z]{1,3}\$?[0-9]{1,7}$")]
    private static partial Regex CellReference();
}
