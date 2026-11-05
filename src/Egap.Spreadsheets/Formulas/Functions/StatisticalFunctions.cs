namespace Egap.Spreadsheets.Formulas;

/// <summary>Counting, conditional aggregates and the core statistical functions.</summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> StatisticalFunctions() =>
    [
        new("COUNT", 1, 255, Kinds.Any, Count),
        new("COUNTA", 1, 255, Kinds.Any, CountAll),
        new("COUNTBLANK", 1, 1, Kinds.Any, CountBlank),
        Aggregate("AVERAGE", Average),
        Aggregate("AVERAGEA", Average, countAll: true),
        Aggregate("MAX", n => n.Count == 0 ? Number(0) : Number(n.Max())),
        Aggregate("MAXA", n => n.Count == 0 ? Number(0) : Number(n.Max()), countAll: true),
        Aggregate("MIN", n => n.Count == 0 ? Number(0) : Number(n.Min())),
        Aggregate("MINA", n => n.Count == 0 ? Number(0) : Number(n.Min()), countAll: true),
        Aggregate("MEDIAN", Median),
        Aggregate("MODE", Mode, kinds: Kinds.Array),
        Aggregate("MODE.SNGL", Mode, kinds: Kinds.Array),
        Aggregate("STDEV", n => Spread(n, sample: true, root: true)),
        Aggregate("STDEV.S", n => Spread(n, sample: true, root: true)),
        Aggregate("STDEVA", n => Spread(n, sample: true, root: true), countAll: true),
        Aggregate("STDEVP", n => Spread(n, sample: false, root: true)),
        Aggregate("STDEV.P", n => Spread(n, sample: false, root: true)),
        Aggregate("STDEVPA", n => Spread(n, sample: false, root: true), countAll: true),
        Aggregate("VAR", n => Spread(n, sample: true, root: false)),
        Aggregate("VAR.S", n => Spread(n, sample: true, root: false)),
        Aggregate("VARA", n => Spread(n, sample: true, root: false), countAll: true),
        Aggregate("VARP", n => Spread(n, sample: false, root: false)),
        Aggregate("VAR.P", n => Spread(n, sample: false, root: false)),
        Aggregate("VARPA", n => Spread(n, sample: false, root: false), countAll: true),
        Aggregate("AVEDEV", AverageDeviation),
        Aggregate("DEVSQ", n => Number(SquaredDeviations(n))),
        Aggregate("GEOMEAN", n => n.Count == 0 || n.Any(x => x <= 0) ? Error(CellError.Number) : Number(Math.Exp(n.Sum(Math.Log) / n.Count))),
        Aggregate("HARMEAN", n => n.Count == 0 || n.Any(x => x <= 0) ? Error(CellError.Number) : Number(n.Count / n.Sum(x => 1 / x))),
        new("LARGE", 2, 2, Kinds.AnyThenValues, (a, c) => Nth(a, c, largest: true)),
        new("SMALL", 2, 2, Kinds.AnyThenValues, (a, c) => Nth(a, c, largest: false)),
        new("PERCENTILE", 2, 2, Kinds.AnyThenValues, (a, c) => Percentile(a, c, exclusive: false, quartile: false)),
        new("PERCENTILE.INC", 2, 2, Kinds.AnyThenValues, (a, c) => Percentile(a, c, exclusive: false, quartile: false)),
        new("PERCENTILE.EXC", 2, 2, Kinds.AnyThenValues, (a, c) => Percentile(a, c, exclusive: true, quartile: false)),
        new("QUARTILE", 2, 2, Kinds.AnyThenValues, (a, c) => Percentile(a, c, exclusive: false, quartile: true)),
        new("QUARTILE.INC", 2, 2, Kinds.AnyThenValues, (a, c) => Percentile(a, c, exclusive: false, quartile: true)),
        new("QUARTILE.EXC", 2, 2, Kinds.AnyThenValues, (a, c) => Percentile(a, c, exclusive: true, quartile: true)),
        new("RANK", 2, 3, Kinds.ValueThenAnyThenValue, (a, c) => Rank(a, c, average: false)),
        new("RANK.EQ", 2, 3, Kinds.ValueThenAnyThenValue, (a, c) => Rank(a, c, average: false)),
        new("RANK.AVG", 2, 3, Kinds.ValueThenAnyThenValue, (a, c) => Rank(a, c, average: true)),
        new("CORREL", 2, 2, Kinds.Array, (a, c) => Paired(a, c, Correlation)),
        new("PEARSON", 2, 2, Kinds.Array, (a, c) => Paired(a, c, Correlation)),
        new("RSQ", 2, 2, Kinds.Array, (a, c) => Paired(a, c, (x, y) => Correlation(x, y) is { Kind: EvalKind.Number } r ? Number(r.Number * r.Number) : Correlation(x, y))),
        new("SLOPE", 2, 2, Kinds.Array, (a, c) => Paired(a, c, (y, x) => Line(x, y, slope: true))),
        new("INTERCEPT", 2, 2, Kinds.Array, (a, c) => Paired(a, c, (y, x) => Line(x, y, slope: false))),
        new("COVAR", 2, 2, Kinds.Array, (a, c) => Paired(a, c, (x, y) => Covariance(x, y, sample: false))),
        new("COVARIANCE.P", 2, 2, Kinds.Array, (a, c) => Paired(a, c, (x, y) => Covariance(x, y, sample: false))),
        new("COVARIANCE.S", 2, 2, Kinds.Array, (a, c) => Paired(a, c, (x, y) => Covariance(x, y, sample: true))),
        new("FORECAST", 3, 3, Kinds.ValueThenArrays, Forecast),
        new("FORECAST.LINEAR", 3, 3, Kinds.ValueThenArrays, Forecast),
        new("COUNTIF", 2, 2, Kinds.Conditions, (a, c) => Conditional(a, c, Conditions.CountIf)),
        new("COUNTIFS", 2, 254, Kinds.Conditions, (a, c) => Conditional(a, c, Conditions.CountIfs)),
        new("SUMIF", 2, 3, Kinds.Conditions, (a, c) => Conditional(a, c, Conditions.SumIf)),
        new("SUMIFS", 3, 255, Kinds.TargetThenConditions, (a, c) => Conditional(a, c, Conditions.SumIfs)),
        new("AVERAGEIF", 2, 3, Kinds.Conditions, (a, c) => Conditional(a, c, Conditions.AverageIf)),
        new("AVERAGEIFS", 3, 255, Kinds.TargetThenConditions, (a, c) => Conditional(a, c, Conditions.AverageIfs)),
        new("MAXIFS", 3, 255, Kinds.TargetThenConditions, (a, c) => Conditional(a, c, Conditions.MaxIfs)),
        new("MINIFS", 3, 255, Kinds.TargetThenConditions, (a, c) => Conditional(a, c, Conditions.MinIfs)),
    ];

    private static FormulaFunction Aggregate(string name, Func<List<double>, EvalValue> body, bool countAll = false, ArgumentKind[]? kinds = null) =>
        new(name, 1, 255, kinds ?? Kinds.Any, (a, c) =>
        {
            List<double> numbers = [];
            return Gather(a, c, numbers, countAll) is { } error ? error : body(numbers);
        });

    private static EvalValue Count(EvalValue[] arguments, EvalContext context)
    {
        double count = 0;
        foreach (EvalValue argument in arguments)
        {
            foreach ((EvalValue value, bool direct) in Scalars(argument))
            {
                if (value.Kind == EvalKind.Number
                    || (direct && value.Kind is EvalKind.Boolean or EvalKind.Blank or EvalKind.Missing)
                    || (direct && value.Kind == EvalKind.Text && ExcelValues.TryParse(value.Text, context, out _)))
                {
                    count++;
                }
            }
        }

        return Number(count);
    }

    private static EvalValue CountAll(EvalValue[] arguments, EvalContext context)
    {
        double count = 0;
        foreach (EvalValue argument in arguments)
        {
            foreach ((EvalValue value, bool direct) in Scalars(argument))
            {
                if (direct || value.Kind != EvalKind.Blank)
                {
                    count++;
                }
            }
        }

        return Number(count);
    }

    /// <summary>COUNTBLANK: the cells of a range that are empty or hold empty text.</summary>
    private static EvalValue CountBlank(EvalValue[] arguments, EvalContext context)
    {
        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        if (Grid.Of(arguments[0]) is not { } grid)
        {
            return Error(CellError.Value);
        }

        long filled = 0;
        foreach ((int row, int column) in grid.Occupied())
        {
            EvalValue value = grid[row, column];
            if (value.Kind != EvalKind.Blank && !(value.Kind == EvalKind.Text && value.Text.Length == 0))
            {
                filled++;
            }
        }

        return Number(grid.Count - filled);
    }

    private static EvalValue Average(List<double> numbers)
    {
        if (numbers.Count == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double total = 0;
        foreach (double n in numbers)
        {
            total += n;
        }

        return Number(total / numbers.Count);
    }

    private static EvalValue Median(List<double> numbers)
    {
        if (numbers.Count == 0)
        {
            return Error(CellError.Number);
        }

        numbers.Sort();
        int middle = numbers.Count / 2;
        return Number(numbers.Count % 2 == 1 ? numbers[middle] : (numbers[middle - 1] + numbers[middle]) / 2);
    }

    /// <summary>MODE: the value that occurs most often, the first one met on a tie; #N/A when none occurs twice.</summary>
    private static EvalValue Mode(List<double> numbers)
    {
        Dictionary<double, int> counts = [];
        double best = 0;
        int most = 1;
        foreach (double n in numbers)
        {
            int count = counts[n] = counts.GetValueOrDefault(n) + 1;
            if (count > most)
            {
                (best, most) = (n, count);
            }
        }

        if (most < 2)
        {
            return Error(CellError.NotAvailable);
        }

        // On a tie the value that appears first wins.
        foreach (double n in numbers)
        {
            if (counts[n] == most)
            {
                return Number(n);
            }
        }

        return Number(best);
    }

    private static double SquaredDeviations(List<double> numbers)
    {
        if (numbers.Count == 0)
        {
            return 0;
        }

        double mean = numbers.Sum() / numbers.Count, total = 0;
        foreach (double n in numbers)
        {
            total += (n - mean) * (n - mean);
        }

        return total;
    }

    private static EvalValue Spread(List<double> numbers, bool sample, bool root)
    {
        int divisor = sample ? numbers.Count - 1 : numbers.Count;
        if (divisor <= 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double variance = SquaredDeviations(numbers) / divisor;
        return Number(root ? Math.Sqrt(variance) : variance);
    }

    private static EvalValue AverageDeviation(List<double> numbers)
    {
        if (numbers.Count == 0)
        {
            return Error(CellError.Number);
        }

        double mean = numbers.Sum() / numbers.Count;
        return Number(numbers.Sum(n => Math.Abs(n - mean)) / numbers.Count);
    }

    /// <summary>LARGE and SMALL: the k-th largest or smallest number.</summary>
    private static EvalValue Nth(EvalValue[] arguments, EvalContext context, bool largest)
    {
        List<double> numbers = [];
        if (Gather(arguments, context, numbers, end: 1) is { } error)
        {
            return error;
        }

        if (!Num(arguments[1], context, out double kValue, out EvalValue kError))
        {
            return kError;
        }

        double k = Math.Ceiling(kValue);
        if (k < 1 || k > numbers.Count)
        {
            return Error(CellError.Number);
        }

        numbers.Sort();
        return Number(largest ? numbers[numbers.Count - (int)k] : numbers[(int)k - 1]);
    }

    /// <summary>PERCENTILE and QUARTILE, inclusive or exclusive: the value at a fraction of the sorted numbers, between two of them when it falls there.</summary>
    private static EvalValue Percentile(EvalValue[] arguments, EvalContext context, bool exclusive, bool quartile)
    {
        List<double> numbers = [];
        if (Gather(arguments, context, numbers, end: 1) is { } error)
        {
            return error;
        }

        if (!Num(arguments[1], context, out double k, out EvalValue kError))
        {
            return kError;
        }

        if (quartile)
        {
            k = Math.Truncate(k);
            if (exclusive ? k is < 1 or > 3 : k is < 0 or > 4)
            {
                return Error(CellError.Number);
            }

            k /= 4;
        }

        int n = numbers.Count;
        if (n == 0 || k < 0 || k > 1)
        {
            return Error(CellError.Number);
        }

        numbers.Sort();
        double rank = exclusive ? (k * (n + 1)) - 1 : k * (n - 1);
        if (exclusive && (rank < 0 || rank > n - 1))
        {
            return Error(CellError.Number);
        }

        int low = (int)Math.Floor(rank);
        double fraction = rank - low;
        return Number(low + 1 < n ? numbers[low] + (fraction * (numbers[low + 1] - numbers[low])) : numbers[low]);
    }

    /// <summary>RANK: the position of a number among others, largest first unless the order says otherwise; RANK.AVG averages ties.</summary>
    private static EvalValue Rank(EvalValue[] arguments, EvalContext context, bool average)
    {
        if (!Num(arguments[0], context, out double number, out EvalValue error))
        {
            return error;
        }

        if (arguments[1].Kind is not (EvalKind.Reference or EvalKind.Array))
        {
            return arguments[1].IsError ? arguments[1] : Error(CellError.Value);
        }

        List<double> numbers = [];
        if (Gather(arguments, context, numbers, first: 1, end: 2) is { } listError)
        {
            return listError;
        }

        if (!Optional(arguments, 2, 0, context, out double order, out error))
        {
            return error;
        }

        int better = 0, equal = 0;
        foreach (double n in numbers)
        {
            int compared = ExcelValues.CompareNumbers(n, number);
            if (compared == 0)
            {
                equal++;
            }
            else if (order == 0 ? compared > 0 : compared < 0)
            {
                better++;
            }
        }

        if (equal == 0)
        {
            return Error(CellError.NotAvailable);
        }

        return Number(average ? better + ((equal + 1) / 2.0) : better + 1);
    }

    /// <summary>
    /// The pairs of numbers two arguments hold at the same positions, as CORREL and SLOPE take them: a pair counts only when
    /// both are numbers. #N/A when the arguments differ in size.
    /// </summary>
    private static EvalValue Paired(EvalValue[] arguments, EvalContext context, Func<List<double>, List<double>, EvalValue> body)
    {
        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        if (arguments[1].IsError)
        {
            return arguments[1];
        }

        if (Grid.Of(arguments[0]) is not { } first || Grid.Of(arguments[1]) is not { } second)
        {
            return Error(CellError.Value);
        }

        if (first.Count != second.Count)
        {
            return Error(CellError.NotAvailable);
        }

        List<double> xs = [], ys = [];
        int columns = first.Columns, otherColumns = second.Columns;
        foreach ((int row, int column) in first.Occupied())
        {
            long index = ((long)row * columns) + column;
            EvalValue x = first[row, column], y = second[(int)(index / otherColumns), (int)(index % otherColumns)];
            if (x.IsError)
            {
                return x;
            }

            if (y.IsError)
            {
                return y;
            }

            if (x.Kind == EvalKind.Number && y.Kind == EvalKind.Number)
            {
                xs.Add(x.Number);
                ys.Add(y.Number);
            }
        }

        return body(xs, ys);
    }

    private static EvalValue Correlation(List<double> xs, List<double> ys)
    {
        if (xs.Count == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double mx = xs.Average(), my = ys.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < xs.Count; i++)
        {
            sxy += (xs[i] - mx) * (ys[i] - my);
            sxx += (xs[i] - mx) * (xs[i] - mx);
            syy += (ys[i] - my) * (ys[i] - my);
        }

        return sxx == 0 || syy == 0 ? Error(CellError.DivisionByZero) : Number(sxy / Math.Sqrt(sxx * syy));
    }

    private static EvalValue Line(List<double> xs, List<double> ys, bool slope)
    {
        if (xs.Count == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double mx = xs.Average(), my = ys.Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < xs.Count; i++)
        {
            sxy += (xs[i] - mx) * (ys[i] - my);
            sxx += (xs[i] - mx) * (xs[i] - mx);
        }

        if (sxx == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double b = sxy / sxx;
        return Number(slope ? b : my - (b * mx));
    }

    private static EvalValue Covariance(List<double> xs, List<double> ys, bool sample)
    {
        int divisor = sample ? xs.Count - 1 : xs.Count;
        if (xs.Count == 0 || divisor <= 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double mx = xs.Average(), my = ys.Average(), total = 0;
        for (int i = 0; i < xs.Count; i++)
        {
            total += (xs[i] - mx) * (ys[i] - my);
        }

        return Number(total / divisor);
    }

    private static EvalValue Forecast(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double x, out EvalValue error))
        {
            return error;
        }

        return Paired(arguments[1..], context, (ys, xs) =>
        {
            EvalValue slope = Line(xs, ys, slope: true);
            EvalValue intercept = Line(xs, ys, slope: false);
            return slope.IsError ? slope : Number(intercept.Number + (slope.Number * x));
        });
    }

    private enum Conditions
    {
        CountIf,
        CountIfs,
        SumIf,
        SumIfs,
        AverageIf,
        AverageIfs,
        MaxIfs,
        MinIfs,
    }

    /// <summary>
    /// COUNTIF, SUMIF, AVERAGEIF and their IFS forms, MAXIFS and MINIFS: the cells of a target range at the positions where
    /// every condition holds on its range. A whole column costs only its cells in use; blank positions count when every
    /// condition takes blanks.
    /// </summary>
    private static EvalValue Conditional(EvalValue[] arguments, EvalContext context, Conditions kind)
    {
        // Lay the arguments out as a target (or none, for counting) and pairs of range and condition.
        bool targetFirst = kind is Conditions.SumIfs or Conditions.AverageIfs or Conditions.MaxIfs or Conditions.MinIfs;
        int pairsStart = targetFirst ? 1 : 0;
        int pairsEnd = kind is Conditions.SumIf or Conditions.AverageIf ? 2 : arguments.Length;
        if ((pairsEnd - pairsStart) % 2 != 0)
        {
            return Error(CellError.Value);
        }

        List<(Grid Range, Criterion Condition)> conditions = [];
        for (int i = pairsStart; i < pairsEnd; i += 2)
        {
            if (arguments[i].IsError)
            {
                return arguments[i];
            }

            if (Grid.Of(arguments[i]) is not { } range)
            {
                return Error(CellError.Value);
            }

            if (conditions.Count > 0 && (range.Rows != conditions[0].Range.Rows || range.Columns != conditions[0].Range.Columns))
            {
                return Error(CellError.Value);
            }

            conditions.Add((range, Criterion.Parse(arguments[i + 1], context)));
        }

        Grid? target = null;
        if (targetFirst)
        {
            if (arguments[0].IsError)
            {
                return arguments[0];
            }

            target = Grid.Of(arguments[0]);
            if (target is not { } t || t.Rows != conditions[0].Range.Rows || t.Columns != conditions[0].Range.Columns)
            {
                return Error(CellError.Value);
            }
        }
        else if (kind is Conditions.SumIf or Conditions.AverageIf)
        {
            Grid first = conditions[0].Range;
            if (arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing)
            {
                if (arguments[2].IsError)
                {
                    return arguments[2];
                }

                target = Grid.Of(arguments[2])?.Resize(first.Rows, first.Columns);
                if (target is null)
                {
                    return Error(CellError.Value);
                }
            }
            else
            {
                target = first;
            }
        }

        bool Holds(int row, int column)
        {
            foreach ((Grid range, Criterion condition) in conditions)
            {
                if (!condition.Matches(range[row, column]))
                {
                    return false;
                }
            }

            return true;
        }

        if (target is not { } values)
        {
            return Number(CountMatches(conditions, Holds));
        }

        double total = 0, count = 0, best = double.NaN;
        List<(int Row, int Column)> positions = [.. values.Occupied()];
        foreach ((int row, int column) in positions)
        {
            EvalValue value = values[row, column];
            if (value.Kind != EvalKind.Number && !value.IsError)
            {
                continue;
            }

            if (!Holds(row, column))
            {
                continue;
            }

            if (value.IsError)
            {
                return value;
            }

            total += value.Number;
            count++;
            best = double.IsNaN(best) ? value.Number
                : kind == Conditions.MaxIfs ? Math.Max(best, value.Number)
                : Math.Min(best, value.Number);
        }

        return kind switch
        {
            Conditions.SumIf or Conditions.SumIfs => Number(total),
            Conditions.AverageIf or Conditions.AverageIfs => count == 0 ? Error(CellError.DivisionByZero) : Number(total / count),
            _ => Number(double.IsNaN(best) ? 0 : best),
        };
    }

    /// <summary>How many positions meet every condition: the positions any range fills, walked, and the rest at once.</summary>
    private static long CountMatches(List<(Grid Range, Criterion Condition)> conditions, Func<int, int, bool> holds)
    {
        Grid shape = conditions[0].Range;
        HashSet<long> seen = [];
        long count = 0;
        foreach ((Grid range, _) in conditions)
        {
            foreach ((int row, int column) in range.Occupied())
            {
                if (seen.Add(((long)row * CellAddress.MaxColumns) + column) && holds(row, column))
                {
                    count++;
                }
            }
        }

        if (conditions.All(c => c.Condition.MatchesBlank))
        {
            count += shape.Count - seen.Count;
        }

        return count;
    }
}
