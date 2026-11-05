using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Formulas;

/// <summary>Lookup and reference functions.</summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> LookupFunctions() =>
    [
        new("VLOOKUP", 3, 4, [ArgumentKind.Value, ArgumentKind.Any, ArgumentKind.Value], (a, c) => TableLookup(a, c, vertical: true)),
        new("HLOOKUP", 3, 4, [ArgumentKind.Value, ArgumentKind.Any, ArgumentKind.Value], (a, c) => TableLookup(a, c, vertical: false)),
        new("LOOKUP", 2, 3, Kinds.ValueThenArrays, Lookup),
        new("MATCH", 2, 3, [ArgumentKind.Value, ArgumentKind.Any, ArgumentKind.Value], Match),
        new("INDEX", 2, 4, Kinds.ArrayThenValues, Index),
        new("OFFSET", 3, 5, Kinds.AnyThenValues, Offset),
        new("INDIRECT", 1, 2, Kinds.Value, Indirect),
        new("ADDRESS", 2, 5, Kinds.Value, Address),
        new("AREAS", 1, 1, Kinds.Any, static (a, _) => a[0].Kind == EvalKind.Reference ? Number(a[0].Areas.Length) : a[0].IsError ? a[0] : Error(CellError.Value)),
        new("ROWS", 1, 1, Kinds.Array, (a, c) => Dimension(a[0], rows: true)),
        new("COLUMNS", 1, 1, Kinds.Array, (a, c) => Dimension(a[0], rows: false)),
        new("TRANSPOSE", 1, 1, Kinds.Any, Transpose),
        new("HYPERLINK", 1, 2, Kinds.Value, static (a, _) => Result(a.Length > 1 ? a[1] : a[0])),
        new("FORMULATEXT", 1, 1, Kinds.Any, FormulaText),
    ];

    /// <summary>
    /// Whether a lookup value equals an item: of the same kind, text without case and with wildcards when asked, numbers to
    /// 15 digits.
    /// </summary>
    private static bool LookupEquals(in EvalValue item, in EvalValue wanted, bool wildcards)
    {
        if (item.Kind != wanted.Kind)
        {
            return false;
        }

        return item.Kind switch
        {
            EvalKind.Text => wildcards ? Wildcard.IsMatch(wanted.Text, item.Text) : string.Equals(item.Text, wanted.Text, StringComparison.OrdinalIgnoreCase),
            EvalKind.Number => ExcelValues.CompareNumbers(item.Number, wanted.Number) == 0,
            EvalKind.Boolean => item.Number == wanted.Number,
            _ => false,
        };
    }

    /// <summary>The position of the first item equal to the value, or -1.</summary>
    private static int ExactIndex(Func<int, EvalValue> item, int count, in EvalValue wanted)
    {
        bool wildcards = wanted.Kind == EvalKind.Text && Wildcard.Has(wanted.Text);
        for (int i = 0; i < count; i++)
        {
            if (LookupEquals(item(i), wanted, wildcards))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A sorted lookup by bisection, as Excel does it: in ascending order the last item not greater than the value, in
    /// descending order the last item not less than it. Only items of the value's kind count; blanks are passed over.
    /// Gives -1 when there is none.
    /// </summary>
    private static int SortedIndex(Func<int, EvalValue> item, int count, in EvalValue wanted, bool descending, EvalContext context)
    {
        int low = 0, high = count - 1, found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int probe = middle;
            EvalValue value = item(probe);

            // Step back to the nearest item of the value's kind, so mixed and blank cells do not stop the search.
            while (probe >= low && value.Kind != wanted.Kind)
            {
                probe--;
                if (probe >= low)
                {
                    value = item(probe);
                }
            }

            if (probe < low)
            {
                low = middle + 1;
                continue;
            }

            int order = ExcelValues.Compare(value, wanted, context);
            if (descending ? order >= 0 : order <= 0)
            {
                found = probe;
                low = middle + 1;
            }
            else
            {
                high = probe - 1;
            }
        }

        return found;
    }

    /// <summary>VLOOKUP and HLOOKUP: the value in a column (row) of a table, on the row (column) the first column (row) matches.</summary>
    private static EvalValue TableLookup(EvalValue[] arguments, EvalContext context, bool vertical)
    {
        EvalValue wanted = arguments[0];
        if (wanted.IsError)
        {
            return wanted;
        }

        if (arguments[1].IsError)
        {
            return arguments[1];
        }

        if (Grid.Of(arguments[1]) is not { } table)
        {
            return Error(CellError.Value);
        }

        if (!Num(arguments[2], context, out double indexValue, out EvalValue error) || !OptionalFlag(arguments, 3, true, out bool sorted, out error))
        {
            return error;
        }

        int index = (int)Math.Truncate(Math.Clamp(indexValue, -1, 1 << 20));
        int lines = vertical ? table.Rows : table.Columns;
        int width = vertical ? table.Columns : table.Rows;
        if (index < 1)
        {
            return Error(CellError.Value);
        }

        if (index > width)
        {
            return Error(CellError.Reference);
        }

        if (wanted.Kind is EvalKind.Blank or EvalKind.Missing)
        {
            wanted = Number(0);
        }

        Func<int, EvalValue> key = vertical ? i => table[i, 0] : i => table[0, i];
        int found = sorted ? SortedIndex(key, LimitLines(table, lines, vertical), wanted, false, context) : ExactIndex(key, LimitLines(table, lines, vertical), wanted);
        if (found < 0)
        {
            return Error(CellError.NotAvailable);
        }

        return Result(vertical ? table[found, index - 1] : table[index - 1, found]);
    }

    /// <summary>
    /// How many rows (columns) of a table a lookup needs to walk: a whole-column reference stops after the last cell in use,
    /// since the rest are blank.
    /// </summary>
    private static int LimitLines(Grid table, int lines, bool vertical)
    {
        if (table.Area is not { } area || lines < 4096)
        {
            return lines;
        }

        int last = -1;
        foreach ((int row, int column) in table.Occupied())
        {
            last = Math.Max(last, vertical ? row : column);
        }

        return last + 1;
    }

    /// <summary>LOOKUP: a sorted lookup in a vector, giving the matching item of a result vector; or in an array's first row or column.</summary>
    private static EvalValue Lookup(EvalValue[] arguments, EvalContext context)
    {
        EvalValue wanted = arguments[0];
        if (wanted.IsError)
        {
            return wanted;
        }

        if (arguments[1].IsError)
        {
            return arguments[1];
        }

        if (Grid.Of(arguments[1]) is not { } source)
        {
            return Error(CellError.Value);
        }

        if (wanted.Kind is EvalKind.Blank or EvalKind.Missing)
        {
            wanted = Number(0);
        }

        bool alongRow = source.Rows == 1 || (arguments.Length == 2 && source.Columns > source.Rows);
        int count = LimitLines(source, alongRow ? source.Columns : source.Rows, !alongRow);
        Func<int, EvalValue> key = alongRow ? i => source[0, i] : i => source[i, 0];
        int found = SortedIndex(key, count, wanted, false, context);
        if (found < 0)
        {
            return Error(CellError.NotAvailable);
        }

        if (arguments.Length == 3)
        {
            if (arguments[2].IsError)
            {
                return arguments[2];
            }

            if (Grid.Of(arguments[2]) is not { } result)
            {
                return Error(CellError.Value);
            }

            return Result(result.Rows == 1 ? result[0, found] : result[found, 0]);
        }

        return Result(alongRow ? source[source.Rows - 1, found] : source[found, source.Columns - 1]);
    }

    /// <summary>MATCH: the 1-based position of a value in a row or column, exactly (0) or in ascending (1) or descending (-1) order.</summary>
    private static EvalValue Match(EvalValue[] arguments, EvalContext context)
    {
        EvalValue wanted = arguments[0];
        if (wanted.IsError)
        {
            return wanted;
        }

        if (arguments[1].IsError)
        {
            return arguments[1];
        }

        if (Grid.Of(arguments[1]) is not { } source || (source.Rows > 1 && source.Columns > 1))
        {
            return Error(CellError.NotAvailable);
        }

        if (!Optional(arguments, 2, 1, context, out double typeValue, out EvalValue error))
        {
            return error;
        }

        if (wanted.Kind is EvalKind.Blank or EvalKind.Missing)
        {
            return Error(CellError.NotAvailable);
        }

        bool row = source.Rows == 1;
        int count = LimitLines(source, row ? source.Columns : source.Rows, !row);
        Func<int, EvalValue> item = row ? i => source[0, i] : i => source[i, 0];
        int type = Math.Sign(Math.Truncate(typeValue));
        int found = type == 0 ? ExactIndex(item, count, wanted) : SortedIndex(item, count, wanted, type < 0, context);
        return found < 0 ? Error(CellError.NotAvailable) : Number(found + 1);
    }

    /// <summary>
    /// INDEX: the cell (or the whole row or column, for 0) at a row and column of a reference, kept as a reference; or the
    /// element of an array.
    /// </summary>
    private static EvalValue Index(EvalValue[] arguments, EvalContext context)
    {
        EvalValue source = arguments[0];
        if (source.IsError)
        {
            return source;
        }

        if (!Num(arguments[1], context, out double rowValue, out EvalValue error)
            || !Optional(arguments, 2, 0, context, out double columnValue, out error)
            || !Optional(arguments, 3, 1, context, out double areaValue, out error))
        {
            return error;
        }

        int row = (int)Math.Truncate(Math.Clamp(rowValue, -1, 1 << 21));
        int column = (int)Math.Truncate(Math.Clamp(columnValue, -1, 1 << 21));
        int areaIndex = (int)Math.Truncate(Math.Clamp(areaValue, -1, 1 << 16));
        if (row < 0 || column < 0 || areaIndex < 1)
        {
            return Error(CellError.Value);
        }

        if (source.Kind == EvalKind.Reference)
        {
            if (areaIndex > source.Areas.Length)
            {
                return Error(CellError.Reference);
            }

            SheetArea area = source.Areas[areaIndex - 1];
            RangeAddress range = area.Range;
            if (arguments.Length == 2 && range.RowCount == 1 && range.ColumnCount > 1)
            {
                (row, column) = (1, row);
            }
            else if (arguments.Length == 2 && range.ColumnCount > 1 && !context.ArrayMode)
            {
                return Error(CellError.Reference); // In an ordinary formula a range of rows and columns needs both numbers.
            }

            if (row > range.RowCount || column > range.ColumnCount)
            {
                return Error(CellError.Reference);
            }

            int firstRow = row == 0 ? range.First.Row : range.First.Row + row - 1;
            int lastRow = row == 0 ? range.Last.Row : firstRow;
            int firstColumn = column == 0 ? range.First.Column : range.First.Column + column - 1;
            int lastColumn = column == 0 ? range.Last.Column : firstColumn;
            return EvalValue.FromArea(area.Sheet, new RangeAddress(firstRow, firstColumn, lastRow, lastColumn));
        }

        if (Grid.Of(source) is not { } grid)
        {
            return Error(CellError.Value);
        }

        if (arguments.Length == 2 && grid.Rows == 1)
        {
            (row, column) = (1, row);
        }
        else if (arguments.Length == 2 && grid.Columns == 1)
        {
            column = 1;
        }

        if (row > grid.Rows || column > grid.Columns)
        {
            return Error(CellError.Reference);
        }

        if (row > 0 && column > 0)
        {
            return Result(grid[row - 1, column - 1]);
        }

        // A whole row or column of an array.
        int rows = row == 0 ? grid.Rows : 1, columns = column == 0 ? grid.Columns : 1;
        EvalValue[] items = new EvalValue[rows * columns];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                items[(r * columns) + c] = grid[row == 0 ? r : row - 1, column == 0 ? c : column - 1];
            }
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }

    /// <summary>OFFSET: a reference moved by some rows and columns from another, of its size or a size given.</summary>
    private static EvalValue Offset(EvalValue[] arguments, EvalContext context)
    {
        EvalValue source = arguments[0];
        if (source.IsError)
        {
            return source;
        }

        if (source.Kind != EvalKind.Reference || source.Areas.Length != 1)
        {
            return Error(CellError.Value);
        }

        RangeAddress range = source.Areas[0].Range;
        if (!Num(arguments[1], context, out double rows, out EvalValue error)
            || !Num(arguments[2], context, out double columns, out error)
            || !Optional(arguments, 3, range.RowCount, context, out double height, out error)
            || !Optional(arguments, 4, range.ColumnCount, context, out double width, out error))
        {
            return error;
        }

        height = Math.Truncate(height);
        width = Math.Truncate(width);
        if (height == 0 || width == 0)
        {
            return Error(CellError.Reference);
        }

        double top = range.First.Row + Math.Truncate(rows), left = range.First.Column + Math.Truncate(columns);
        double bottom = top + height + (height > 0 ? -1 : 1), right = left + width + (width > 0 ? -1 : 1);
        (top, bottom) = (Math.Min(top, bottom), Math.Max(top, bottom));
        (left, right) = (Math.Min(left, right), Math.Max(left, right));
        if (top < 0 || left < 0 || bottom >= CellAddress.MaxRows || right >= CellAddress.MaxColumns)
        {
            return Error(CellError.Reference);
        }

        return EvalValue.FromArea(source.Areas[0].Sheet, new RangeAddress((int)top, (int)left, (int)bottom, (int)right));
    }

    /// <summary>INDIRECT: the reference written in text, in A1 or R1C1 style, or a defined name; #REF! for anything else.</summary>
    private static EvalValue Indirect(EvalValue[] arguments, EvalContext context)
    {
        if (!Str(arguments[0], context, out string text, out EvalValue error) || !OptionalFlag(arguments, 1, true, out bool a1, out error))
        {
            return error;
        }

        if (context.ForText() is not { } inner
            || FormulaParser.TryParse(text.Trim(), r1c1: !a1, new CellAddress(context.Row, context.Column)) is not { } parsed
            || !IsReferenceText(parsed.Root))
        {
            return Error(CellError.Reference);
        }

        EvalValue result = FormulaEvaluator.Run(FormulaCompiler.Compile(parsed), inner);
        return result.Kind == EvalKind.Reference ? result : Error(CellError.Reference);
    }

    /// <summary>Whether a parsed formula is only references: a cell or range, a name or a table's area, and ranges built of them.</summary>
    private static bool IsReferenceText(FormulaNode node) => node switch
    {
        ReferenceNode or NameNode or StructuredNode => true,
        BinaryNode { Operator: BinaryOperator.Range or BinaryOperator.Intersection } binary => IsReferenceText(binary.Left) && IsReferenceText(binary.Right),
        _ => false,
    };

    /// <summary>ADDRESS: a cell's reference as text, absolute or relative, in A1 or R1C1 style, with a sheet name when given.</summary>
    private static EvalValue Address(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double rowValue, out EvalValue error)
            || !Num(arguments[1], context, out double columnValue, out error))
        {
            return error;
        }

        // Unlike most functions' optional arguments, these two take their defaults when empty too: ADDRESS(1,1,,) is $A$1.
        double absolute = 1;
        bool a1 = true;
        if ((arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing && !Num(arguments[2], context, out absolute, out error))
            || (arguments.Length > 3 && arguments[3].Kind != EvalKind.Missing && !ExcelValues.TryBoolean(arguments[3], out a1, out error)))
        {
            return error;
        }

        string? sheet = null;
        if (arguments.Length > 4 && arguments[4].Kind != EvalKind.Missing)
        {
            if (!Str(arguments[4], context, out string name, out error))
            {
                return error;
            }

            sheet = name;
        }

        int row = (int)Math.Truncate(Math.Clamp(rowValue, 0, int.MaxValue));
        int column = (int)Math.Truncate(Math.Clamp(columnValue, 0, int.MaxValue));
        int mode = (int)Math.Truncate(absolute);
        if (row < 1 || column < 1 || row > CellAddress.MaxRows || column > CellAddress.MaxColumns || mode is < 1 or > 4)
        {
            return Error(CellError.Value);
        }

        bool absoluteRow = mode is 1 or 2, absoluteColumn = mode is 1 or 3;
        StringBuilder text = new();
        if (sheet is not null)
        {
            bool plain = sheet.Length > 0 && !char.IsDigit(sheet[0]) && sheet.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');
            text.Append(plain || sheet.Length == 0 ? sheet : "'" + sheet.Replace("'", "''", StringComparison.Ordinal) + "'").Append('!');
        }

        string r = row.ToString(CultureInfo.InvariantCulture), c = column.ToString(CultureInfo.InvariantCulture);
        if (a1)
        {
            text.Append(absoluteColumn ? "$" : "").Append(CellAddress.ColumnName(column - 1)).Append(absoluteRow ? "$" : "").Append(r);
        }
        else
        {
            text.Append('R').Append(absoluteRow ? r : "[" + r + "]").Append('C').Append(absoluteColumn ? c : "[" + c + "]");
        }

        return EvalValue.FromText(text.ToString());
    }

    private static EvalValue Dimension(in EvalValue value, bool rows)
    {
        if (value.IsError)
        {
            return value;
        }

        if (value.Kind == EvalKind.Reference && value.Areas.Length > 1)
        {
            return Error(CellError.Reference);
        }

        Grid grid = Grid.Of(value)!.Value;
        return Number(rows ? grid.Rows : grid.Columns);
    }

    private static EvalValue Transpose(EvalValue[] arguments, EvalContext context)
    {
        // In an ordinary formula TRANSPOSE's range meets the cell, as Excel has it; in an array formula it turns whole.
        EvalValue value = context.ArrayMode || arguments[0].Kind != EvalKind.Reference
            ? arguments[0]
            : FormulaEvaluator.Operand(arguments[0], context);
        if (value.Kind == EvalKind.Reference)
        {
            value = FormulaEvaluator.ToArray(value);
        }

        if (value.Kind != EvalKind.Array)
        {
            return Result(value);
        }

        EvalArray array = value.Array;
        EvalValue[] items = new EvalValue[array.Items.Length];
        for (int r = 0; r < array.Rows; r++)
        {
            for (int c = 0; c < array.Columns; c++)
            {
                items[(c * array.Rows) + r] = Result(array[r, c]);
            }
        }

        return EvalValue.FromArray(new EvalArray(array.Columns, array.Rows, items));
    }

    private static EvalValue FormulaText(EvalValue[] arguments, EvalContext context)
    {
        EvalValue value = arguments[0];
        if (value.IsError)
        {
            return value;
        }

        if (value.Kind != EvalKind.Reference)
        {
            return Error(CellError.Value);
        }

        SheetArea area = value.Areas[0];
        return area.Sheet.GetFormula(area.Range.First.Row, area.Range.First.Column) is { } formula
            ? EvalValue.FromText("=" + formula)
            : Error(CellError.NotAvailable);
    }
}
