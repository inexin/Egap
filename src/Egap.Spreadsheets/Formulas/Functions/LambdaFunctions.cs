namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// The functions that call a LAMBDA, or a function named as a value, for each part of an array. A range's parts stay
/// references, so ROW and ISREF see cells; a LAMBDA that gives more than one value for an element makes the whole result #CALC!.
/// </summary>
internal static partial class FormulaFunctions
{
    private static readonly ArgumentKind[] TakesMakeArray = [ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Any];

    private static IEnumerable<FormulaFunction> LambdaFunctions() =>
    [
        new("BYCOL", 2, 2, Kinds.Array, (a, c) => ByLine(a, c, rows: false), takesFunctions: true),
        new("BYROW", 2, 2, Kinds.Array, (a, c) => ByLine(a, c, rows: true), takesFunctions: true),
        new("ISOMITTED", 1, 1, Kinds.Any, static (a, _) => EvalValue.FromBoolean(a[0].Kind == EvalKind.Missing)),
        new("MAKEARRAY", 3, 3, TakesMakeArray, MakeArray, takesFunctions: true),
        new("MAP", 2, 254, Kinds.Array, Map, takesFunctions: true),
        new("REDUCE", 3, 3, Kinds.Array, Reduce, takesFunctions: true),
        new("SCAN", 3, 3, Kinds.Array, Scan, takesFunctions: true),

        // Compiled to what they do; here for what names them, as MAP(A1:A3,LET) gives #VALUE! for too few arguments.
        new("LAMBDA", 1, 254, Kinds.Any, static (_, _) => EvalValue.FromError(CellError.Value)),
        new("LET", 3, 253, Kinds.Any, static (_, _) => EvalValue.FromError(CellError.Value)),
    ];

    /// <summary>The rows and columns of a value handed to a LAMBDA a part at a time.</summary>
    private readonly struct Pieces
    {
        private readonly EvalValue value;
        private readonly RangeAddress range;
        private readonly Worksheet? sheet;

        public Pieces(in EvalValue given)
        {
            value = given;
            (Rows, Columns) = (1, 1);
            if (given.Kind == EvalKind.Reference)
            {
                Valid = given.Areas.Length == 1;
                (sheet, range) = given.Areas[0];
                (Rows, Columns) = (range.Last.Row - range.First.Row + 1, range.Last.Column - range.First.Column + 1);
            }
            else
            {
                Valid = true;
                if (given.Kind == EvalKind.Array)
                {
                    (Rows, Columns) = (given.Array.Rows, given.Array.Columns);
                }
            }
        }

        public bool Valid { get; }

        public int Rows { get; }

        public int Columns { get; }

        public long Count => (long)Rows * Columns;

        public bool Within(int row, int column) => row < Rows && column < Columns;

        public EvalValue this[int row, int column] => value.Kind switch
        {
            EvalKind.Reference => EvalValue.FromArea(sheet!, new RangeAddress(range.First.Row + row, range.First.Column + column, range.First.Row + row, range.First.Column + column)),
            EvalKind.Array => value.Array[row, column],
            _ => value,
        };

        /// <summary>One row or column as a value of its own.</summary>
        public EvalValue Line(int index, bool isRow)
        {
            if (value.Kind == EvalKind.Reference)
            {
                return EvalValue.FromArea(sheet!, isRow
                    ? new RangeAddress(range.First.Row + index, range.First.Column, range.First.Row + index, range.Last.Column)
                    : new RangeAddress(range.First.Row, range.First.Column + index, range.Last.Row, range.First.Column + index));
            }

            if (value.Kind != EvalKind.Array)
            {
                return value;
            }

            int count = isRow ? Columns : Rows;
            EvalValue[] items = new EvalValue[count];
            for (int i = 0; i < count; i++)
            {
                items[i] = isRow ? value.Array[index, i] : value.Array[i, index];
            }

            return EvalValue.FromArray(isRow ? new EvalArray(1, count, items) : new EvalArray(count, 1, items));
        }
    }

    /// <summary>The one value a call gave for an element; false when it gave more than one.</summary>
    private static bool Single(in EvalValue given, out EvalValue value)
    {
        value = given;
        switch (given.Kind)
        {
            case EvalKind.Reference:
                if (given.Areas.Length != 1 || given.Areas[0].Range.CellCount != 1)
                {
                    return false;
                }

                value = FormulaEvaluator.Deref(given);
                return true;
            case EvalKind.Array:
                if (given.Array.Items.Length != 1)
                {
                    return false;
                }

                value = given.Array.Items[0];
                return true;
            default:
                return true;
        }
    }

    private static EvalValue Map(EvalValue[] arguments, EvalContext context)
    {
        int count = arguments.Length - 1;
        EvalValue function = arguments[count];
        if (function.Kind != EvalKind.Lambda || !function.Lambda.Accepts(count))
        {
            return EvalValue.FromError(CellError.Value);
        }

        Pieces[] parts = new Pieces[count];
        int rows = 1, columns = 1;
        for (int i = 0; i < count; i++)
        {
            parts[i] = new Pieces(arguments[i]);

            // After the first, a lone value is no array, though a one-cell reference is one.
            if (!parts[i].Valid || (i > 0 && arguments[i].Kind is not (EvalKind.Reference or EvalKind.Array)))
            {
                return EvalValue.FromError(CellError.Value);
            }

            rows = Math.Max(rows, parts[i].Rows);
            columns = Math.Max(columns, parts[i].Columns);
        }

        if ((long)rows * columns > EvalContext.MaxArrayCells)
        {
            return EvalValue.FromError(CellError.Number);
        }

        EvalValue[] items = new EvalValue[rows * columns];
        EvalValue[] call = new EvalValue[count];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                bool all = true;
                for (int i = 0; i < count; i++)
                {
                    all &= parts[i].Within(r, c);
                    if (all)
                    {
                        call[i] = parts[i][r, c];
                    }
                }

                // Arrays of different sizes: #N/A where one of them has no element.
                if (!all)
                {
                    items[(r * columns) + c] = EvalValue.FromError(CellError.NotAvailable);
                }
                else if (!Single(Lambdas.Apply(function, call, context), out items[(r * columns) + c]))
                {
                    return EvalValue.FromError(CellError.Calc);
                }
            }
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }

    /// <summary>The arguments of REDUCE and SCAN: the start, which may be left out, the array and the function.</summary>
    private static bool Folding(EvalValue[] arguments, out EvalValue start, out Pieces parts, out EvalValue function)
    {
        start = arguments[0];
        parts = new Pieces(arguments[1]);
        function = arguments[2];
        return parts.Valid && parts.Count <= EvalContext.MaxArrayCells && function.Kind == EvalKind.Lambda && function.Lambda.Accepts(2);
    }

    /// <summary>REDUCE: what a call gives is the next call's first argument, and may be an array, so a result can grow.</summary>
    private static EvalValue Reduce(EvalValue[] arguments, EvalContext context)
    {
        if (!Folding(arguments, out EvalValue total, out Pieces parts, out EvalValue function))
        {
            return EvalValue.FromError(CellError.Value);
        }

        bool started = total.Kind != EvalKind.Missing;
        EvalValue[] call = new EvalValue[2];
        for (int r = 0; r < parts.Rows; r++)
        {
            for (int c = 0; c < parts.Columns; c++)
            {
                if (!started)
                {
                    total = parts[r, c];
                    started = true;
                    continue;
                }

                (call[0], call[1]) = (total, parts[r, c]);
                total = Lambdas.Apply(function, call, context);
            }
        }

        return total;
    }

    /// <summary>SCAN: as REDUCE, with each step's value in the result, which has the array's shape.</summary>
    private static EvalValue Scan(EvalValue[] arguments, EvalContext context)
    {
        if (!Folding(arguments, out EvalValue total, out Pieces parts, out EvalValue function))
        {
            return EvalValue.FromError(CellError.Value);
        }

        bool started = total.Kind != EvalKind.Missing;
        if (started && !Single(total, out total))
        {
            return EvalValue.FromError(CellError.Calc);
        }

        EvalValue[] items = new EvalValue[parts.Count];
        EvalValue[] call = new EvalValue[2];
        for (int r = 0; r < parts.Rows; r++)
        {
            for (int c = 0; c < parts.Columns; c++)
            {
                if (!started)
                {
                    Single(parts[r, c], out total);
                    started = true;
                }
                else
                {
                    (call[0], call[1]) = (total, parts[r, c]);
                    if (!Single(Lambdas.Apply(function, call, context), out total))
                    {
                        return EvalValue.FromError(CellError.Calc);
                    }
                }

                items[(r * parts.Columns) + c] = total;
            }
        }

        return EvalValue.FromArray(new EvalArray(parts.Rows, parts.Columns, items));
    }

    private static EvalValue ByLine(EvalValue[] arguments, EvalContext context, bool rows)
    {
        Pieces parts = new(arguments[0]);
        EvalValue function = arguments[1];
        if (!parts.Valid || function.Kind != EvalKind.Lambda || !function.Lambda.Accepts(1))
        {
            return EvalValue.FromError(CellError.Value);
        }

        int count = rows ? parts.Rows : parts.Columns;
        EvalValue[] items = new EvalValue[count];
        EvalValue[] call = new EvalValue[1];
        for (int i = 0; i < count; i++)
        {
            call[0] = parts.Line(i, rows);
            if (!Single(Lambdas.Apply(function, call, context), out items[i]))
            {
                return EvalValue.FromError(CellError.Calc);
            }
        }

        return EvalValue.FromArray(rows ? new EvalArray(count, 1, items) : new EvalArray(1, count, items));
    }

    /// <summary>MAKEARRAY: whole numbers of rows and columns of at least 1, and the function given each element's place from 1.</summary>
    private static EvalValue MakeArray(EvalValue[] arguments, EvalContext context)
    {
        EvalValue function = arguments[2];
        if (!ExcelValues.TryNumber(arguments[0], context, out double rows, out EvalValue error) || !ExcelValues.TryNumber(arguments[1], context, out double columns, out error))
        {
            return error;
        }

        if (function.Kind != EvalKind.Lambda || !function.Lambda.Accepts(2) || rows < 1 || columns < 1 || rows != Math.Floor(rows) || columns != Math.Floor(columns))
        {
            return EvalValue.FromError(CellError.Value);
        }

        if (rows * columns > EvalContext.MaxArrayCells)
        {
            return EvalValue.FromError(CellError.Number);
        }

        int height = (int)rows, width = (int)columns;
        EvalValue[] items = new EvalValue[height * width];
        EvalValue[] call = new EvalValue[2];
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++)
            {
                (call[0], call[1]) = (EvalValue.FromNumber(r + 1), EvalValue.FromNumber(c + 1));
                if (!Single(Lambdas.Apply(function, call, context), out items[(r * width) + c]))
                {
                    return EvalValue.FromError(CellError.Calc);
                }
            }
        }

        return EvalValue.FromArray(new EvalArray(height, width, items));
    }
}
