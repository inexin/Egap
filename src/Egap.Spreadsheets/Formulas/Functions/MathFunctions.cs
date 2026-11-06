namespace Egap.Spreadsheets.Formulas;

/// <summary>Math and trigonometry functions.</summary>
internal static partial class FormulaFunctions
{
    /// <summary>The largest argument Excel's trigonometric functions take: 2^27.</summary>
    private const double TrigLimit = 134217728;

    private static IEnumerable<FormulaFunction> MathFunctions() =>
    [
        Unary("ABS", Math.Abs),
        Unary("ACOS", Math.Acos),
        Unary("ACOSH", Math.Acosh),
        Unary("ACOT", x => (Math.PI / 2) - Math.Atan(x)),
        Unary("ASIN", Math.Asin),
        Unary("ASINH", Math.Asinh),
        Unary("ATAN", Math.Atan),
        Unary("ATANH", Math.Atanh),
        Trig("COS", Math.Cos),
        Unary("COSH", Math.Cosh),
        Trig("COT", x => x == 0 ? double.NaN : 1 / Math.Tan(x), divides: true),
        Trig("CSC", x => x == 0 ? double.NaN : 1 / Math.Sin(x), divides: true),
        Trig("SEC", x => 1 / Math.Cos(x)),
        Trig("SIN", Math.Sin),
        Unary("SINH", Math.Sinh),
        Trig("TAN", Math.Tan),
        Unary("TANH", Math.Tanh),
        Unary("DEGREES", x => x * 180 / Math.PI),
        Unary("RADIANS", x => x * Math.PI / 180),
        Unary("EXP", Math.Exp),
        Unary("LN", x => x > 0 ? Math.Log(x) : double.NaN),
        Unary("LOG10", x => x > 0 ? Math.Log10(x) : double.NaN),
        Unary("SQRT", x => x >= 0 ? Math.Sqrt(x) : double.NaN),
        Unary("SQRTPI", x => x >= 0 ? Math.Sqrt(x * Math.PI) : double.NaN),
        Unary("INT", Math.Floor),
        Unary("SIGN", x => Math.Sign(x)),
        Unary("EVEN", x => Away(x, 2, 0)),
        Unary("ODD", x => Away(x, 2, 1)),
        Unary("FACT", Factorial),
        Unary("FACTDOUBLE", DoubleFactorial),
        new("PI", 0, 0, Kinds.Value, static (_, _) => Number(Math.PI)),
        new("ATAN2", 2, 2, Kinds.Value, Atan2),
        new("LOG", 1, 2, Kinds.Value, Log),
        new("POWER", 2, 2, Kinds.Value, (a, c) => Binary(a, c, Power)),
        new("MOD", 2, 2, Kinds.Value, (a, c) => Binary(a, c, Mod)),
        new("QUOTIENT", 2, 2, Kinds.Value, (a, c) => Binary(a, c, (n, d) => d == 0 ? Error(CellError.DivisionByZero) : Number(Math.Truncate(n / d)))),
        new("ROUND", 2, 2, Kinds.Value, (a, c) => Binary(a, c, (x, d) => Number(Round(x, d, Rounding.Nearest)))),
        new("ROUNDUP", 2, 2, Kinds.Value, (a, c) => Binary(a, c, (x, d) => Number(Round(x, d, Rounding.Up)))),
        new("ROUNDDOWN", 2, 2, Kinds.Value, (a, c) => Binary(a, c, (x, d) => Number(Round(x, d, Rounding.Down)))),
        new("TRUNC", 1, 2, Kinds.Value, Trunc),
        new("MROUND", 2, 2, Kinds.Value, (a, c) => Binary(a, c, MultipleRound)),
        new("CEILING", 2, 2, Kinds.Value, (a, c) => Binary(a, c, Ceiling)),
        new("FLOOR", 2, 2, Kinds.Value, (a, c) => Binary(a, c, Floor)),
        new("CEILING.MATH", 1, 3, Kinds.Value, (a, c) => MathRounding(a, c, up: true)),
        new("FLOOR.MATH", 1, 3, Kinds.Value, (a, c) => MathRounding(a, c, up: false)),
        new("CEILING.PRECISE", 1, 2, Kinds.Value, (a, c) => Precise(a, c, up: true)),
        new("ISO.CEILING", 1, 2, Kinds.Value, (a, c) => Precise(a, c, up: true)),
        new("FLOOR.PRECISE", 1, 2, Kinds.Value, (a, c) => Precise(a, c, up: false)),
        new("COMBIN", 2, 2, Kinds.Value, (a, c) => Binary(a, c, Combin)),
        new("COMBINA", 2, 2, Kinds.Value, (a, c) => Binary(a, c, (n, k) => Combin(Math.Truncate(n) + Math.Truncate(k) - 1, k))),
        new("PERMUT", 2, 2, Kinds.Value, (a, c) => Binary(a, c, Permut)),
        new("GCD", 1, 255, Kinds.Any, (a, c) => Divisors(a, c, lcm: false)),
        new("LCM", 1, 255, Kinds.Any, (a, c) => Divisors(a, c, lcm: true)),
        new("PRODUCT", 1, 255, Kinds.Any, Product),
        new("SUMSQ", 1, 255, Kinds.Any, SumSquares),
        new("SUMPRODUCT", 1, 255, Kinds.Array, SumProduct),
        new("RAND", 0, 0, Kinds.Value, static (_, _) => Number(Random.Shared.NextDouble())),
        new("RANDBETWEEN", 2, 2, Kinds.Value, (a, c) => Binary(a, c, RandomBetween)),
        new("SUBTOTAL", 2, 255, [ArgumentKind.Value, ArgumentKind.Any], Subtotal),
    ];

    private static FormulaFunction Unary(string name, Func<double, double> function) =>
        new(name, 1, 1, Kinds.Value, (a, c) => Num(a[0], c, out double x, out EvalValue e) ? Number(function(x)) : e);

    /// <summary>A trigonometric function: #NUM! past 2^27, and #DIV/0! where the function divides by zero.</summary>
    private static FormulaFunction Trig(string name, Func<double, double> function, bool divides = false) =>
        new(name, 1, 1, Kinds.Value, (a, c) =>
        {
            if (!Num(a[0], c, out double x, out EvalValue e))
            {
                return e;
            }

            if (Math.Abs(x) >= TrigLimit)
            {
                return Error(CellError.Number);
            }

            double result = function(x);
            return divides && double.IsNaN(result) ? Error(CellError.DivisionByZero) : Number(result);
        });

    private static EvalValue Binary(EvalValue[] arguments, EvalContext context, Func<double, double, EvalValue> function)
    {
        if (!Num(arguments[0], context, out double a, out EvalValue error) || !Num(arguments[1], context, out double b, out error))
        {
            return error;
        }

        return function(a, b);
    }

    private static EvalValue Atan2(EvalValue[] arguments, EvalContext context) => Binary(arguments, context, static (x, y) =>
        x == 0 && y == 0 ? Error(CellError.DivisionByZero) : Number(Math.Atan2(y, x)));

    private static EvalValue Log(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double x, out EvalValue error) || !Optional(arguments, 1, 10, context, out double b, out error))
        {
            return error;
        }

        if (x <= 0 || b <= 0)
        {
            return Error(CellError.Number);
        }

        return b == 1 ? Error(CellError.DivisionByZero) : Number(Math.Log(x) / Math.Log(b));
    }

    /// <summary>POWER and <c>^</c>: 0 to the power 0 is #NUM!, 0 to a negative power #DIV/0!.</summary>
    internal static EvalValue Power(double x, double y)
    {
        if (x == 0)
        {
            return y == 0 ? Error(CellError.Number) : y < 0 ? Error(CellError.DivisionByZero) : Number(0);
        }

        if (x < 0 && y != Math.Floor(y))
        {
            // A negative number has a real root when the power is 1 over an odd number, as in POWER(-8,1/3).
            double root = Math.Round(1 / y);
            return root % 2 != 0 && Math.Abs((1 / y) - root) < 1e-10 ? Number(-Math.Pow(-x, y)) : Error(CellError.Number);
        }

        return Number(Math.Pow(x, y));
    }

    private static EvalValue Mod(double n, double d)
    {
        if (d == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double result = n - (d * Math.Floor(n / d));
        // Rounding can leave the result a hair outside [0, d), or at d; Excel gives 0 there.
        return Number(Math.Abs(result) >= Math.Abs(d) || ExcelValues.CompareNumbers(result, d) == 0 ? 0 : result);
    }

    private enum Rounding
    {
        Nearest,
        Up,
        Down,
    }

    /// <summary>
    /// Rounds to a number of decimal places (to the left of the point when negative) as Excel does: on the number's 15-digit
    /// decimal form, so ROUND(2.675,2) is 2.68; halves away from zero, Up away from zero and Down toward it.
    /// </summary>
    private static double Round(double x, double places, Rounding mode)
    {
        int digits = (int)Math.Clamp(Math.Truncate(places), -400, 400);
        if (x == 0 || digits > 15 - Math.Floor(Math.Log10(Math.Abs(x))))
        {
            return x;
        }

        if (Math.Abs(x) < 7.9e27 && digits is >= -27 and <= 27)
        {
            decimal value = (decimal)x;
            decimal scale = Pow10(Math.Abs(digits));
            decimal scaled = digits >= 0 ? value * scale : value / scale;
            scaled = mode switch
            {
                Rounding.Nearest => Math.Round(scaled, 0, MidpointRounding.AwayFromZero),
                Rounding.Up => scaled > 0 ? Math.Ceiling(scaled) : Math.Floor(scaled),
                _ => Math.Truncate(scaled),
            };
            return (double)(digits >= 0 ? scaled / scale : scaled * scale);
        }

        double factor = Math.Pow(10, digits);
        double shifted = ExcelValues.Round15(x * factor);
        shifted = mode switch
        {
            Rounding.Nearest => Math.Round(shifted, MidpointRounding.AwayFromZero),
            Rounding.Up => shifted > 0 ? Math.Ceiling(shifted) : Math.Floor(shifted),
            _ => Math.Truncate(shifted),
        };
        return shifted / factor;
    }

    private static decimal Pow10(int digits)
    {
        decimal result = 1;
        for (int i = 0; i < digits; i++)
        {
            result *= 10;
        }

        return result;
    }

    private static EvalValue Trunc(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double x, out EvalValue error) || !Optional(arguments, 1, 0, context, out double digits, out error))
        {
            return error;
        }

        return Number(Round(x, digits, Rounding.Down));
    }

    /// <summary>Rounds away from zero to the next number that leaves <paramref name="remainder"/> when divided by <paramref name="step"/>.</summary>
    private static double Away(double x, double step, double remainder)
    {
        double size = Math.Ceiling(ExcelValues.Round15(Math.Abs(x)));
        if (size % step != remainder)
        {
            size++;
        }

        return x < 0 ? -size : size;
    }

    private static double Factorial(double x)
    {
        double n = Math.Truncate(x);
        if (n < 0 || n > 170)
        {
            return double.NaN;
        }

        double result = 1;
        for (int i = 2; i <= n; i++)
        {
            result *= i;
        }

        return result;
    }

    private static double DoubleFactorial(double x)
    {
        double n = Math.Truncate(x);
        if (n < -1 || n > 300)
        {
            return double.NaN;
        }

        double result = 1;
        for (double i = n; i > 1; i -= 2)
        {
            result *= i;
        }

        return result;
    }

    /// <summary>A quotient of a number and a significance, taken to 15 digits so 1.1/0.1 counts as 11.</summary>
    private static double Steps(double x, double step) => ExcelValues.Round15(x / step);

    private static EvalValue MultipleRound(double x, double multiple)
    {
        if (multiple == 0)
        {
            return Number(0);
        }

        if (Math.Sign(x) * Math.Sign(multiple) < 0)
        {
            return Error(CellError.Number);
        }

        return Number(Math.Round(Steps(x, multiple), MidpointRounding.AwayFromZero) * multiple);
    }

    private static EvalValue Ceiling(double x, double significance)
    {
        if (x > 0 && significance < 0)
        {
            return Error(CellError.Number);
        }

        return significance == 0 ? Number(0) : Number(Math.Ceiling(Steps(x, significance)) * significance);
    }

    private static EvalValue Floor(double x, double significance)
    {
        if (x > 0 && significance < 0)
        {
            return Error(CellError.Number);
        }

        if (significance == 0)
        {
            return x == 0 ? Number(0) : Error(CellError.DivisionByZero);
        }

        return Number(Math.Floor(Steps(x, significance)) * significance);
    }

    /// <summary>CEILING.MATH and FLOOR.MATH: the significance's sign ignored; a mode other than 0 turns negative numbers the other way.</summary>
    private static EvalValue MathRounding(EvalValue[] arguments, EvalContext context, bool up)
    {
        if (!Num(arguments[0], context, out double x, out EvalValue error)
            || !Optional(arguments, 1, 1, context, out double significance, out error)
            || !Optional(arguments, 2, 0, context, out double mode, out error))
        {
            return error;
        }

        significance = Math.Abs(significance);
        if (significance == 0)
        {
            return Number(0);
        }

        double steps = Steps(x, significance);
        bool towardZero = x < 0 && mode != 0;
        double rounded = up
            ? (towardZero ? Math.Floor(steps) : Math.Ceiling(steps))
            : (towardZero ? Math.Ceiling(steps) : Math.Floor(steps));
        return Number(rounded * significance);
    }

    private static EvalValue Precise(EvalValue[] arguments, EvalContext context, bool up)
    {
        if (!Num(arguments[0], context, out double x, out EvalValue error) || !Optional(arguments, 1, 1, context, out double significance, out error))
        {
            return error;
        }

        significance = Math.Abs(significance);
        if (significance == 0)
        {
            return Number(0);
        }

        double steps = Steps(x, significance);
        return Number((up ? Math.Ceiling(steps) : Math.Floor(steps)) * significance);
    }

    /// <summary>The largest count Excel takes in COMBIN, PERMUT and the counting distributions: one below 2^31 - 1, as measured.</summary>
    private const double MaxCount = 2147483646;

    private static EvalValue Combin(double n, double k)
    {
        n = Math.Truncate(n);
        k = Math.Truncate(k);
        if (n < 0 || k < 0 || n < k || n > MaxCount)
        {
            return Error(CellError.Number);
        }

        k = Math.Min(k, n - k);
        // A huge k ends early: past about a thousand steps the result has overflowed.
        double result = 1;
        for (double i = 1; i <= k && !double.IsInfinity(result); i++)
        {
            result = result * (n - k + i) / i;
        }

        return double.IsInfinity(result) ? Error(CellError.Number) : Number(result < 9007199254740992 ? Math.Round(result) : result);
    }

    private static EvalValue Permut(double n, double k)
    {
        n = Math.Truncate(n);
        k = Math.Truncate(k);
        if (n <= 0 || k < 0 || n < k || n > MaxCount)
        {
            return Error(CellError.Number);
        }

        // The product overflows within a few hundred steps of a large n.
        double result = 1;
        for (double i = 0; i < k && !double.IsInfinity(result); i++)
        {
            result *= n - i;
        }

        return double.IsInfinity(result) ? Error(CellError.Number) : Number(result);
    }

    private static EvalValue Divisors(EvalValue[] arguments, EvalContext context, bool lcm)
    {
        List<double> numbers = [];
        if (Gather(arguments, context, numbers) is { } error)
        {
            return error;
        }

        double result = lcm ? 1 : 0;
        foreach (double value in numbers)
        {
            double n = Math.Truncate(value);
            if (n < 0 || n >= 9007199254740992)
            {
                return Error(CellError.Number);
            }

            if (lcm)
            {
                if (n == 0)
                {
                    return Number(0);
                }

                result = result / Gcd(result, n) * n;
            }
            else
            {
                result = Gcd(result, n);
            }
        }

        return Number(result);
    }

    private static double Gcd(double a, double b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private static EvalValue Product(EvalValue[] arguments, EvalContext context)
    {
        List<double> numbers = [];
        if (Gather(arguments, context, numbers) is { } error)
        {
            return error;
        }

        if (numbers.Count == 0)
        {
            return Number(0);
        }

        double result = 1;
        foreach (double n in numbers)
        {
            result *= n;
        }

        return Number(result);
    }

    private static EvalValue SumSquares(EvalValue[] arguments, EvalContext context)
    {
        List<double> numbers = [];
        if (Gather(arguments, context, numbers) is { } error)
        {
            return error;
        }

        double total = 0;
        foreach (double n in numbers)
        {
            total += n * n;
        }

        return Number(total);
    }

    /// <summary>SUMPRODUCT: arrays of one size multiplied element by element and summed; what is not a number counts as 0.</summary>
    private static EvalValue SumProduct(EvalValue[] arguments, EvalContext context)
    {
        Grid[] grids = new Grid[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsError)
            {
                return arguments[i];
            }

            if (Grid.Of(arguments[i]) is not { } grid)
            {
                return Error(CellError.Value);
            }

            if (i > 0 && (grid.Rows != grids[0].Rows || grid.Columns != grids[0].Columns))
            {
                return Error(CellError.Value);
            }

            grids[i] = grid;
        }

        // Only the cells every argument fills can add anything; the first argument's cells in use say which.
        double total = 0;
        foreach ((int row, int column) in grids[0].Occupied())
        {
            double product = 1;
            for (int i = 0; i < grids.Length; i++)
            {
                EvalValue value = grids[i][row, column];
                if (value.IsError)
                {
                    return value;
                }

                product *= value.Kind == EvalKind.Number ? value.Number : 0;
            }

            total += product;
        }

        return Number(total);
    }

    private static EvalValue RandomBetween(double bottom, double top)
    {
        double low = Math.Ceiling(bottom), high = Math.Floor(top);
        return low > high ? Error(CellError.Number) : Number(Math.Floor(low + (Random.Shared.NextDouble() * (high - low + 1))));
    }

    /// <summary>
    /// SUBTOTAL: one of eleven aggregates over references, leaving out cells that hold SUBTOTAL or AGGREGATE formulas, and
    /// with the codes 101 to 111 the rows that are hidden too.
    /// </summary>
    private static EvalValue Subtotal(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double code, out EvalValue error))
        {
            return error;
        }

        int function = (int)Math.Truncate(code);
        bool skipHidden = function > 100;
        string? name = (function % 100) switch
        {
            1 => "AVERAGE",
            2 => "COUNT",
            3 => "COUNTA",
            4 => "MAX",
            5 => "MIN",
            6 => "PRODUCT",
            7 => "STDEV",
            8 => "STDEVP",
            9 => "SUM",
            10 => "VAR",
            11 => "VARP",
            _ => null,
        };
        if (name is null || function is > 111 or (> 11 and < 101))
        {
            return Error(CellError.Value);
        }

        List<EvalValue> values = [];
        for (int i = 1; i < arguments.Length; i++)
        {
            if (arguments[i].Kind != EvalKind.Reference)
            {
                return arguments[i].IsError ? arguments[i] : Error(CellError.Value);
            }

            foreach (SheetArea area in arguments[i].Areas)
            {
                foreach ((int row, int column) in area.Sheet.Store.Occupied(area.Range))
                {
                    // Codes above 100 leave out every hidden row, the others only rows a filter hides.
                    if ((skipHidden || area.Sheet.HasFilterCriteria) && area.Sheet.Rows[row].Hidden)
                    {
                        continue;
                    }

                    if (area.Sheet.GetFormula(row, column) is { } formula
                        && (formula.Contains("SUBTOTAL(", StringComparison.OrdinalIgnoreCase) || formula.Contains("AGGREGATE(", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    CellValue value = area.Sheet.GetValue(row, column);
                    if (!value.IsEmpty)
                    {
                        values.Add(EvalValue.FromCell(value));
                    }
                }
            }
        }

        // The cells left are handed to the aggregate as an array, which it reads as it reads a range.
        EvalValue[] items = values.Count == 0 ? [EvalValue.Blank] : [.. values];
        TryGet(name, out FormulaFunction? aggregate);
        return aggregate!.Body([EvalValue.FromArray(new EvalArray(1, items.Length, items))], context);
    }
}
