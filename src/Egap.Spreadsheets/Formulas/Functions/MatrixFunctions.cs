using System.Text;

namespace Egap.Spreadsheets.Formulas;

/// <summary>Matrices and the remaining math functions.</summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> MatrixFunctions() =>
    [
        Unary("ACOTH", x => Math.Abs(x) <= 1 ? double.NaN : Math.Abs(x) < 2 ? 0.5 * Math.Log((x + 1) / (x - 1)) : Math.Atanh(1 / x)),
        new("COTH", 1, 1, Kinds.Value, (a, c) => Reciprocal(a, c, Math.Tanh)),
        new("CSCH", 1, 1, Kinds.Value, (a, c) => Reciprocal(a, c, Math.Sinh)),
        new("SECH", 1, 1, Kinds.Value, (a, c) => Reciprocal(a, c, Math.Cosh)),
        new("AGGREGATE", 3, 255, [ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Array], AggregateFunction),
        new("ARABIC", 1, 1, Kinds.Value, Arabic),
        new("BASE", 2, 3, Kinds.Value, BaseFunction),
        new("DECIMAL", 2, 2, Kinds.Value, DecimalFunction),
        new("MDETERM", 1, 1, Kinds.Array, Determinant),
        new("MINVERSE", 1, 1, Kinds.Array, Inverse),
        new("MMULT", 2, 2, Kinds.Array, MatrixProduct),
        new("MUNIT", 1, 1, Kinds.Any, Identity),
        new("MULTINOMIAL", 1, 255, Kinds.Any, Multinomial),
        new("RANDARRAY", 0, 5, Kinds.Any, RandomArray),
        new("ROMAN", 1, 2, Kinds.Value, Roman),
        new("SEQUENCE", 1, 4, Kinds.Any, Sequence),
        new("SERIESSUM", 4, 4, [ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Array], SeriesSum),
        new("SUMX2MY2", 2, 2, Kinds.Array, (a, c) => SumOfPairs(a, static (x, y) => (x * x) - (y * y))),
        new("SUMX2PY2", 2, 2, Kinds.Array, (a, c) => SumOfPairs(a, static (x, y) => (x * x) + (y * y))),
        new("SUMXMY2", 2, 2, Kinds.Array, (a, c) => SumOfPairs(a, static (x, y) => (x - y) * (x - y))),
    ];

    /// <summary>1 over a function: #DIV/0! where that is 0, and 0 where the result is too small for a double's full precision.</summary>
    private static EvalValue Reciprocal(EvalValue[] arguments, EvalContext context, Func<double, double> function)
    {
        if (!Num(arguments[0], context, out double x, out EvalValue error))
        {
            return error;
        }

        double value = function(x);
        return value == 0 ? Error(CellError.DivisionByZero) : Number(EngFlush(1 / value));
    }

    // ---- Matrices ----

    /// <summary>An argument as a matrix of numbers: an empty cell, text or a boolean in it is #VALUE!.</summary>
    private static double[,]? ReadMatrix(in EvalValue argument, out EvalValue error)
    {
        error = Error(CellError.Value);
        if (argument.IsError)
        {
            error = argument;
            return null;
        }

        if (Grid.Of(argument) is not { } grid || grid.Count > EvalContext.MaxArrayCells)
        {
            return null;
        }

        double[,] matrix = new double[grid.Rows, grid.Columns];
        for (int row = 0; row < grid.Rows; row++)
        {
            for (int column = 0; column < grid.Columns; column++)
            {
                EvalValue value = grid[row, column];
                if (value.Kind != EvalKind.Number)
                {
                    error = value.IsError ? value : error;
                    return null;
                }

                matrix[row, column] = value.Number;
            }
        }

        return matrix;
    }

    private static EvalValue FromMatrix(double[,] matrix)
    {
        int rows = matrix.GetLength(0), columns = matrix.GetLength(1);
        EvalValue[] items = new EvalValue[rows * columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                items[(row * columns) + column] = Number(matrix[row, column]);
            }
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }

    /// <summary>LU decomposition with partial pivoting in place; the sign of the row swaps, or 0 when a pivot is 0.</summary>
    private static int Decompose(double[,] matrix, int[] order)
    {
        int size = matrix.GetLength(0), sign = 1;
        for (int i = 0; i < size; i++)
        {
            order[i] = i;
        }

        for (int k = 0; k < size; k++)
        {
            int pivot = k;
            for (int row = k + 1; row < size; row++)
            {
                if (Math.Abs(matrix[row, k]) > Math.Abs(matrix[pivot, k]))
                {
                    pivot = row;
                }
            }

            if (matrix[pivot, k] == 0)
            {
                return 0;
            }

            if (pivot != k)
            {
                for (int column = 0; column < size; column++)
                {
                    (matrix[pivot, column], matrix[k, column]) = (matrix[k, column], matrix[pivot, column]);
                }

                (order[pivot], order[k]) = (order[k], order[pivot]);
                sign = -sign;
            }

            for (int row = k + 1; row < size; row++)
            {
                double factor = matrix[row, k] / matrix[k, k];
                matrix[row, k] = factor;
                for (int column = k + 1; column < size; column++)
                {
                    matrix[row, column] -= factor * matrix[k, column];
                }
            }
        }

        return sign;
    }

    private static EvalValue Determinant(EvalValue[] arguments, EvalContext context)
    {
        double[,]? matrix = ReadMatrix(arguments[0], out EvalValue error);
        if (matrix is null)
        {
            return error;
        }

        int size = matrix.GetLength(0);
        if (size != matrix.GetLength(1))
        {
            return Error(CellError.Value);
        }

        int sign = Decompose(matrix, new int[size]);
        double result = sign;
        for (int i = 0; i < size && sign != 0; i++)
        {
            result *= matrix[i, i];
        }

        return Number(result);
    }

    private static EvalValue Inverse(EvalValue[] arguments, EvalContext context)
    {
        double[,]? matrix = ReadMatrix(arguments[0], out EvalValue error);
        if (matrix is null)
        {
            return error;
        }

        int size = matrix.GetLength(0);
        if (size != matrix.GetLength(1))
        {
            return Error(CellError.Value);
        }

        int[] order = new int[size];
        if (Decompose(matrix, order) == 0)
        {
            return Error(CellError.Number);
        }

        double[,] inverse = new double[size, size];
        double[] column = new double[size];
        for (int c = 0; c < size; c++)
        {
            for (int row = 0; row < size; row++)
            {
                double value = order[row] == c ? 1 : 0;
                for (int k = 0; k < row; k++)
                {
                    value -= matrix[row, k] * column[k];
                }

                column[row] = value;
            }

            for (int row = size - 1; row >= 0; row--)
            {
                double value = column[row];
                for (int k = row + 1; k < size; k++)
                {
                    value -= matrix[row, k] * column[k];
                }

                column[row] = value / matrix[row, row];
            }

            for (int row = 0; row < size; row++)
            {
                inverse[row, c] = column[row];
            }
        }

        return FromMatrix(inverse);
    }

    private static EvalValue MatrixProduct(EvalValue[] arguments, EvalContext context)
    {
        double[,]? a = ReadMatrix(arguments[0], out EvalValue error);
        if (a is null)
        {
            return error;
        }

        double[,]? b = ReadMatrix(arguments[1], out error);
        if (b is null)
        {
            return error;
        }

        int rows = a.GetLength(0), columns = b.GetLength(1), inner = a.GetLength(1);
        if (inner != b.GetLength(0) || (long)rows * columns > EvalContext.MaxArrayCells)
        {
            return Error(CellError.Value);
        }

        double[,] product = new double[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                double total = 0;
                for (int k = 0; k < inner; k++)
                {
                    total += a[row, k] * b[k, column];
                }

                product[row, column] = total;
            }
        }

        return FromMatrix(product);
    }

    /// <summary>Arguments that say how big an array is, or what is in it: a range meets the formula's cell, an array gives its first element.</summary>
    private static EvalValue[] ArrayArguments(EvalValue[] arguments, EvalContext context)
    {
        EvalValue[] values = new EvalValue[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            values[i] = arguments[i].Kind switch
            {
                EvalKind.Reference => FormulaEvaluator.Deref(FormulaEvaluator.IntersectReference(arguments[i], context)),
                EvalKind.Array => arguments[i].Array.Items[0],
                _ => arguments[i],
            };
        }

        return values;
    }

    private static EvalValue Identity(EvalValue[] arguments, EvalContext context)
    {
        arguments = ArrayArguments(arguments, context);
        if (!Num(arguments[0], context, out double size, out EvalValue error))
        {
            return error;
        }

        size = Math.Truncate(size);
        if (size < 1 || size * size > EvalContext.MaxArrayCells)
        {
            return Error(CellError.Value);
        }

        int n = (int)size;
        EvalValue[] items = new EvalValue[n * n];
        Array.Fill(items, Number(0));
        for (int i = 0; i < n; i++)
        {
            items[(i * n) + i] = Number(1);
        }

        return EvalValue.FromArray(new EvalArray(n, n, items));
    }

    // ---- Arrays made from nothing ----

    private static bool ArraySize(EvalValue[] arguments, int index, EvalContext context, out int size, out EvalValue error)
    {
        size = 1;
        error = default;
        if (index >= arguments.Length || arguments[index].Kind == EvalKind.Missing)
        {
            return true;
        }

        if (!Num(arguments[index], context, out double number, out error))
        {
            return false;
        }

        number = Math.Truncate(number);
        if (number < 0)
        {
            error = Error(CellError.Value);
            return false;
        }

        if (number == 0)
        {
            error = Error(CellError.Calc);
            return false;
        }

        // A sheet's rows are the limit for rows; Excel lets columns go past the sheet's.
        if (index == 0 && number > CellAddress.MaxRows)
        {
            error = Error(CellError.Value);
            return false;
        }

        size = number > int.MaxValue ? int.MaxValue : (int)number;
        return true;
    }

    private static EvalValue Sequence(EvalValue[] arguments, EvalContext context)
    {
        arguments = ArrayArguments(arguments, context);
        if (!ArraySize(arguments, 0, context, out int rows, out EvalValue error) || !ArraySize(arguments, 1, context, out int columns, out error))
        {
            return error;
        }

        double start = 1, step = 1;
        if ((arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing && !Num(arguments[2], context, out start, out error))
            || (arguments.Length > 3 && arguments[3].Kind != EvalKind.Missing && !Num(arguments[3], context, out step, out error)))
        {
            return error;
        }

        if ((long)rows * columns > EvalContext.MaxArrayCells)
        {
            return Error(CellError.Value);
        }

        EvalValue[] items = new EvalValue[rows * columns];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = Number(start + (i * step));
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }

    private static EvalValue RandomArray(EvalValue[] arguments, EvalContext context)
    {
        arguments = ArrayArguments(arguments, context);
        if (!ArraySize(arguments, 0, context, out int rows, out EvalValue error) || !ArraySize(arguments, 1, context, out int columns, out error))
        {
            return error;
        }

        double low = 0, high = 1;
        bool whole = false;
        if ((arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing && !Num(arguments[2], context, out low, out error))
            || (arguments.Length > 3 && arguments[3].Kind != EvalKind.Missing && !Num(arguments[3], context, out high, out error))
            || (arguments.Length > 4 && arguments[4].Kind != EvalKind.Missing && !ExcelValues.TryBoolean(arguments[4], out whole, out error)))
        {
            return error;
        }

        if (low > high || (whole && (low != Math.Floor(low) || high != Math.Floor(high))))
        {
            return Error(CellError.Value);
        }

        if ((long)rows * columns > EvalContext.MaxArrayCells)
        {
            return Error(CellError.Value);
        }

        EvalValue[] items = new EvalValue[rows * columns];
        for (int i = 0; i < items.Length; i++)
        {
            double value = Random.Shared.NextDouble();
            items[i] = Number(whole ? Math.Min(high, Math.Floor(low + (value * (high - low + 1)))) : low + (value * (high - low)));
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }

    // ---- AGGREGATE ----

    private static readonly string[] AggregateNames =
    [
        "AVERAGE", "COUNT", "COUNTA", "MAX", "MIN", "PRODUCT", "STDEV.S", "STDEV.P", "SUM", "VAR.S", "VAR.P", "MEDIAN", "MODE.SNGL",
        "LARGE", "SMALL", "PERCENTILE.INC", "QUARTILE.INC", "PERCENTILE.EXC", "QUARTILE.EXC",
    ];

    /// <summary>AGGREGATE: one of nineteen aggregates, leaving out what its options say: nested totals, hidden rows, errors.</summary>
    private static EvalValue AggregateFunction(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double code, out EvalValue error) || !Num(arguments[1], context, out double optionValue, out error))
        {
            return error;
        }

        int function = (int)Math.Truncate(code), option = (int)Math.Truncate(optionValue);
        if (function is < 1 or > 19 || option is < 0 or > 7)
        {
            return Error(CellError.Value);
        }

        bool skipNested = option < 4;
        bool skipHidden = option % 2 == 1;
        bool skipErrors = option is 2 or 3 or 6 or 7;
        TryGet(AggregateNames[function - 1], out FormulaFunction? aggregate);
        if (function >= 14)
        {
            if (arguments.Length != 4)
            {
                return Error(CellError.Value);
            }

            bool cutK = function <= 15;
            EvalValue? gathered = AggregateValues(arguments[2], skipNested, skipHidden, skipErrors, out List<EvalValue> items);
            if (gathered is { } failed)
            {
                return failed;
            }

            EvalValue array = EvalValue.FromArray(new EvalArray(1, Math.Max(items.Count, 1), items.Count == 0 ? [EvalValue.Blank] : [.. items]));
            EvalValue k = arguments[3];
            if (k.Kind == EvalKind.Reference)
            {
                k = FormulaEvaluator.Operand(k, context);
            }

            if (k.Kind != EvalKind.Array)
            {
                return aggregate!.Body([array, cutK ? AggregateK(k, function, context) : k], context);
            }

            EvalValue[] results = new EvalValue[k.Array.Items.Length];
            for (int i = 0; i < results.Length; i++)
            {
                EvalValue item = k.Array.Items[i];
                results[i] = aggregate!.Body([array, cutK ? AggregateK(item, function, context) : item], context);
            }

            return EvalValue.FromArray(new EvalArray(k.Array.Rows, k.Array.Columns, results));
        }

        List<EvalValue> values = [];
        for (int i = 2; i < arguments.Length; i++)
        {
            if (arguments[i].Kind != EvalKind.Reference)
            {
                return arguments[i].IsError ? arguments[i] : Error(CellError.Value);
            }

            EvalValue? failed = AggregateValues(arguments[i], skipNested, skipHidden, skipErrors, out List<EvalValue> items);
            if (failed is { } stop)
            {
                return stop;
            }

            values.AddRange(items);
        }

        EvalValue[] cells = values.Count == 0 ? [EvalValue.Blank] : [.. values];
        return aggregate!.Body([EvalValue.FromArray(new EvalArray(1, cells.Length, cells))], context);
    }

    /// <summary>The k of LARGE and SMALL as AGGREGATE has it: under 1 is no place at all, and past that LARGE rounds it up and SMALL down.</summary>
    private static EvalValue AggregateK(in EvalValue k, int function, EvalContext context)
    {
        if (!Num(k, context, out double number, out EvalValue error))
        {
            return error;
        }

        return Number(number < 1 ? 0 : function == 14 ? Math.Ceiling(number) : Math.Truncate(number));
    }

    /// <summary>The values of one argument after the options; an error when one stays in and the aggregate cannot go on.</summary>
    private static EvalValue? AggregateValues(in EvalValue argument, bool skipNested, bool skipHidden, bool skipErrors, out List<EvalValue> values)
    {
        values = [];
        switch (argument.Kind)
        {
            case EvalKind.Reference:
                foreach (SheetArea area in argument.Areas)
                {
                    foreach ((int row, int column) in area.Sheet.Store.Occupied(area.Range))
                    {
                        if ((skipHidden || area.Sheet.HasFilterCriteria) && area.Sheet.Rows[row].Hidden)
                        {
                            continue;
                        }

                        if (skipNested && area.Sheet.GetFormula(row, column) is { } formula
                            && (formula.Contains("SUBTOTAL(", StringComparison.OrdinalIgnoreCase) || formula.Contains("AGGREGATE(", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        CellValue value = area.Sheet.GetValue(row, column);
                        if (value.IsEmpty)
                        {
                            continue;
                        }

                        if (value.Kind == CellValueKind.Error && skipErrors)
                        {
                            continue;
                        }

                        values.Add(EvalValue.FromCell(value));
                    }
                }

                return null;
            case EvalKind.Array:
                foreach (EvalValue item in argument.Array.Items)
                {
                    if (!(item.IsError && skipErrors))
                    {
                        values.Add(item);
                    }
                }

                return null;
            case EvalKind.Error:
                if (!skipErrors)
                {
                    return argument;
                }

                return null;
            case EvalKind.Number:
                values.Add(argument);
                return null;
            default:
                return Error(CellError.Value);
        }
    }

    // ---- Number names ----

    private static readonly (int Value, string Text)[][] RomanForms = BuildRomanForms();

    private static (int Value, string Text)[][] BuildRomanForms()
    {
        (int, string)[] classic = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        (int, string)[] first = [(950, "LM"), (450, "LD"), (95, "VC"), (45, "VL")];
        (int, string)[] second = [(990, "XM"), (490, "XD"), (99, "IC"), (49, "IL")];
        (int, string)[] third = [(995, "VM"), (495, "VD")];
        (int, string)[] fourth = [(999, "IM"), (499, "ID")];
        static (int, string)[] Sorted(params (int, string)[][] parts) => [.. parts.SelectMany(p => p).OrderByDescending(p => p.Item1)];
        return [classic, Sorted(classic, first), Sorted(classic, first, second), Sorted(classic, first, second, third), Sorted(classic, first, second, third, fourth)];
    }

    private static EvalValue Roman(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double number, out EvalValue error))
        {
            return error;
        }

        double form = 0;
        if (arguments.Length > 1 && arguments[1].Kind != EvalKind.Missing)
        {
            if (arguments[1].Kind == EvalKind.Boolean)
            {
                form = arguments[1].Boolean ? 0 : 4;
            }
            else if (!Num(arguments[1], context, out form, out error))
            {
                return error;
            }
        }

        number = Math.Truncate(number);
        form = Math.Truncate(form);
        if (number is < 0 or > 3999 || form is < 0 or > 4)
        {
            return Error(CellError.Value);
        }

        StringBuilder text = new();
        int left = (int)number;
        foreach ((int value, string name) in RomanForms[(int)form])
        {
            while (left >= value)
            {
                text.Append(name);
                left -= value;
            }
        }

        return EvalValue.FromText(text.ToString());
    }

    private static EvalValue Arabic(EvalValue[] arguments, EvalContext context)
    {
        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        if (arguments[0].Kind is not (EvalKind.Text or EvalKind.Blank))
        {
            return Error(CellError.Value);
        }

        string text = (arguments[0].Kind == EvalKind.Text ? arguments[0].Text : "").Trim();
        if (text.Length > 255)
        {
            return Error(CellError.Value);
        }

        double sign = 1;
        if (text.StartsWith('-'))
        {
            sign = -1;
            text = text[1..];
        }

        if (sign < 0 && text.Length == 0)
        {
            return Error(CellError.Number);
        }

        double total = 0;
        int previous = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            int value = char.ToUpperInvariant(text[i]) switch
            {
                'I' => 1,
                'V' => 5,
                'X' => 10,
                'L' => 50,
                'C' => 100,
                'D' => 500,
                'M' => 1000,
                _ => 0,
            };
            if (value == 0)
            {
                return Error(CellError.Value);
            }

            total += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }

        return Number(sign * total);
    }

    private static EvalValue BaseFunction(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double number, out EvalValue error) || !Num(arguments[1], context, out double radixValue, out error)
            || !Optional(arguments, 2, 0, context, out double lengthValue, out error))
        {
            return error;
        }

        radixValue = Math.Truncate(radixValue);
        lengthValue = Math.Truncate(lengthValue);
        if (number is < 0 or >= 9007199254740992 || radixValue is < 2 or > 36 || lengthValue is < 0 or > 255)
        {
            return Error(CellError.Number);
        }

        long left = (long)Math.Truncate(number), radix = (long)radixValue;
        StringBuilder digits = new();
        do
        {
            digits.Insert(0, "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"[(int)(left % radix)]);
            left /= radix;
        }
        while (left > 0);
        return EvalValue.FromText(digits.ToString().PadLeft((int)lengthValue, '0'));
    }

    private static EvalValue DecimalFunction(EvalValue[] arguments, EvalContext context)
    {
        if (!Str(arguments[0], context, out string text, out EvalValue error) || !Num(arguments[1], context, out double radixValue, out error))
        {
            return error;
        }

        radixValue = Math.Truncate(radixValue);
        if (radixValue is < 2 or > 36)
        {
            return Error(CellError.Number);
        }

        if (text.Length > 255)
        {
            return Error(CellError.Value);
        }

        double total = 0;
        foreach (char c in text.TrimStart())
        {
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => 99,
            };
            if (digit >= radixValue)
            {
                return Error(CellError.Number);
            }

            total = (total * radixValue) + digit;
        }

        return Number(total);
    }

    // ---- Sums ----

    /// <summary>MULTINOMIAL: the factorial of the sum over the factorials of the parts. Only numbers are parts: anything else in a range is #VALUE!, but for empty cells among several; past 170! is #NUM!.</summary>
    private static EvalValue Multinomial(EvalValue[] arguments, EvalContext context)
    {
        double total = 0, result = 1;
        for (int a = 0; a < arguments.Length; a++)
        {
            EvalValue argument = arguments[a];
            if (argument.Kind == EvalKind.Missing)
            {
                if (a == 0)
                {
                    return Error(CellError.NotAvailable);
                }

                continue;
            }

            if (arguments.Length == 1 && argument.Kind == EvalKind.Reference && argument.Areas is [{ Range.CellCount: 1 } single] && single.Sheet.GetValue(single.Range.First.Row, single.Range.First.Column).IsEmpty)
            {
                return Error(CellError.Value);
            }

            foreach ((EvalValue value, bool direct) in Scalars(argument))
            {
                if (!PlainNumber(value, direct, context, out double number, out EvalValue error))
                {
                    return error;
                }

                double n = Math.Truncate(number);
                if (n < 0 || total + n > 170)
                {
                    return Error(CellError.Number);
                }

                // Multiplying by (total + i) / i for i up the part keeps each step whole, with the fewest digits lost.
                for (double i = 1; i <= n; i++)
                {
                    total++;
                    result = result * total / i;
                }
            }
        }

        return Number(result);
    }

    /// <summary>A value that must be a number: only text given directly may be read as one.</summary>
    private static bool PlainNumber(in EvalValue value, bool direct, EvalContext context, out double number, out EvalValue error)
    {
        number = 0;
        error = default;
        if (value.IsError)
        {
            error = value;
            return false;
        }

        if (value.Kind == EvalKind.Text && direct)
        {
            return Num(value, context, out number, out error);
        }

        if (value.Kind != EvalKind.Number)
        {
            error = Error(CellError.Value);
            return false;
        }

        number = value.Number;
        return true;
    }

    private static EvalValue SeriesSum(EvalValue[] arguments, EvalContext context)
    {
        if (!EngNumber(arguments[0], context, out double x, out EvalValue error) || !EngNumber(arguments[1], context, out double n, out error)
            || !EngNumber(arguments[2], context, out double m, out error))
        {
            return error;
        }

        if (arguments[3].Kind == EvalKind.Missing)
        {
            return Error(CellError.NotAvailable);
        }

        double total = 0, power = n;
        foreach ((EvalValue value, bool direct) in Scalars(arguments[3]))
        {
            if (!PlainNumber(value, direct, context, out double coefficient, out error))
            {
                return error;
            }

            if (x == 0 && power <= 0)
            {
                return Error(CellError.Number);
            }

            // A negative x to a fractional power has no real value, and adds nothing.
            double term = Math.Pow(x, power);
            total += double.IsNaN(term) ? 0 : coefficient * term;
            power += m;
        }

        return Number(total);
    }

    private static EvalValue SumOfPairs(EvalValue[] arguments, Func<double, double, double> term)
    {
        for (int i = 0; i < 2; i++)
        {
            if (arguments[i].IsError)
            {
                return arguments[i];
            }
        }

        if (Grid.Of(arguments[0]) is not { } x || Grid.Of(arguments[1]) is not { } y)
        {
            return Error(CellError.Value);
        }

        if (x.Count != y.Count)
        {
            return Error(CellError.NotAvailable);
        }

        if (x.Count == 1 && (x[0, 0].Kind == EvalKind.Blank || y[0, 0].Kind == EvalKind.Blank))
        {
            return Error(CellError.Value);
        }

        double total = 0;
        bool any = false;
        for (int row = 0; row < x.Rows; row++)
        {
            for (int column = 0; column < x.Columns; column++)
            {
                // Two shapes of one size pair up cell by cell, row by row.
                int place = (row * x.Columns) + column;
                EvalValue a = x[row, column], b = y[place / y.Columns, place % y.Columns];
                if (a.IsError)
                {
                    return a;
                }

                if (b.IsError)
                {
                    return b;
                }

                if (a.Kind == EvalKind.Number && b.Kind == EvalKind.Number)
                {
                    total += term(a.Number, b.Number);
                    any = true;
                }
            }
        }

        return any ? Number(total) : Error(CellError.DivisionByZero);
    }
}
