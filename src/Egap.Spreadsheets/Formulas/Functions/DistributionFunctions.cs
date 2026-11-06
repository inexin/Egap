namespace Egap.Spreadsheets.Formulas;

/// <summary>Probability distributions, tests, regression and the remaining statistics.</summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> DistributionFunctions() =>
    [
        DistFlagged("BETA.DIST", 4, 6, 3, a => DistBeta(a[0], a[1], a[2], a[3] != 0, DistOr(a, 4, 0), DistOr(a, 5, 1))),
        DistPlain("BETADIST", 3, 5, a => DistBeta(a[0], a[1], a[2], true, DistOr(a, 3, 0), DistOr(a, 4, 1))),
        DistPlain("BETA.INV", 3, 5, a => DistBetaInverse(a[0], a[1], a[2], DistOr(a, 3, 0), DistOr(a, 4, 1))),
        DistPlain("BETAINV", 3, 5, a => DistBetaInverse(a[0], a[1], a[2], DistOr(a, 3, 0), DistOr(a, 4, 1))),
        DistFlagged("BINOM.DIST", 4, 4, 3, a => DistBinomial(a[0], a[1], a[2], a[3] != 0)),
        DistFlagged("BINOMDIST", 4, 4, 3, a => DistBinomial(a[0], a[1], a[2], a[3] != 0)),
        DistPlain("BINOM.DIST.RANGE", 3, 4, DistBinomialRange),
        DistPlain("BINOM.INV", 3, 3, a => DistBinomialInverse(a[0], a[1], a[2])),
        DistPlain("CRITBINOM", 3, 3, a => DistBinomialInverse(a[0], a[1], a[2])),
        DistFlagged("CHISQ.DIST", 3, 3, 2, a => DistChiSquare(a[0], a[1], a[2] != 0)),
        DistPlain("CHISQ.DIST.RT", 2, 2, a => DistChiSquareRight(a[0], a[1])),
        DistPlain("CHIDIST", 2, 2, a => DistChiSquareRight(a[0], a[1])),
        DistPlain("CHISQ.INV", 2, 2, a => DistChiSquareInverse(a[0], a[1], rightTail: false)),
        DistPlain("CHISQ.INV.RT", 2, 2, a => DistChiSquareInverse(a[0], a[1], rightTail: true)),
        DistPlain("CHIINV", 2, 2, a => DistChiSquareInverse(a[0], a[1], rightTail: true)),
        new("CHISQ.TEST", 2, 2, Kinds.Array, DistChiSquareTest),
        new("CHITEST", 2, 2, Kinds.Array, DistChiSquareTest),
        DistPlain("CONFIDENCE.NORM", 3, 3, a => DistConfidenceNormal(a[0], a[1], a[2])),
        DistPlain("CONFIDENCE", 3, 3, a => DistConfidenceNormal(a[0], a[1], a[2])),
        new("CONFIDENCE.T", 3, 3, Kinds.Value, DistConfidenceT),
        DistFlagged("EXPON.DIST", 3, 3, 2, a => DistExponential(a[0], a[1], a[2] != 0)),
        DistFlagged("EXPONDIST", 3, 3, 2, a => DistExponential(a[0], a[1], a[2] != 0)),
        DistFlagged("F.DIST", 4, 4, 3, a => DistF(a[0], a[1], a[2], a[3] != 0)),
        DistPlain("F.DIST.RT", 3, 3, a => DistFRight(a[0], a[1], a[2])),
        DistPlain("FDIST", 3, 3, a => DistFRight(a[0], a[1], a[2])),
        DistPlain("F.INV", 3, 3, a => DistFInverse(a[0], a[1], a[2], rightTail: false)),
        DistPlain("F.INV.RT", 3, 3, a => DistFInverse(a[0], a[1], a[2], rightTail: true)),
        DistPlain("FINV", 3, 3, a => DistFInverse(a[0], a[1], a[2], rightTail: true)),
        new("F.TEST", 2, 2, Kinds.Array, DistFTest),
        new("FTEST", 2, 2, Kinds.Array, DistFTest),
        DistPlain("FISHER", 1, 1, a => a[0] <= -1 || a[0] >= 1 ? double.NaN : 0.5 * Math.Log((1 + a[0]) / (1 - a[0]))),
        DistPlain("FISHERINV", 1, 1, a => DistFisherInverse(a[0])),
        new("FREQUENCY", 2, 2, Kinds.Array, DistFrequency),
        DistPlain("GAMMA", 1, 1, a => DistMath.Gamma(a[0])),
        DistPlain("GAMMALN", 1, 1, a => a[0] <= 0 ? double.NaN : DistMath.LogGamma(a[0])),
        DistPlain("GAMMALN.PRECISE", 1, 1, a => a[0] <= 0 ? double.NaN : DistMath.LogGamma(a[0])),
        DistFlagged("GAMMA.DIST", 4, 4, 3, a => DistGammaDist(a[0], a[1], a[2], a[3] != 0)),
        DistFlagged("GAMMADIST", 4, 4, 3, a => DistGammaDist(a[0], a[1], a[2], a[3] != 0)),
        DistPlain("GAMMA.INV", 3, 3, a => DistGammaInverse(a[0], a[1], a[2])),
        DistPlain("GAMMAINV", 3, 3, a => DistGammaInverse(a[0], a[1], a[2])),
        DistPlain("GAUSS", 1, 1, a => DistMath.NormalCentral(a[0])),
        DistFlagged("HYPGEOM.DIST", 5, 5, 4, a => DistHypergeometric(a[0], a[1], a[2], a[3], a[4] != 0)),
        DistPlain("HYPGEOMDIST", 4, 4, a => DistHypergeometric(a[0], a[1], a[2], a[3], false)),
        Aggregate("KURT", DistKurtosis),
        DistFlagged("LOGNORM.DIST", 4, 4, 3, a => DistLogNormal(a[0], a[1], a[2], a[3] != 0)),
        DistPlain("LOGNORMDIST", 3, 3, a => DistLogNormal(a[0], a[1], a[2], true)),
        DistPlain("LOGNORM.INV", 3, 3, a => DistLogNormalInverse(a[0], a[1], a[2])),
        DistPlain("LOGINV", 3, 3, a => DistLogNormalInverse(a[0], a[1], a[2])),
        new("MODE.MULT", 1, 255, Kinds.Array, DistModes),
        DistFlagged("NEGBINOM.DIST", 4, 4, 3, a => DistNegativeBinomial(a[0], a[1], a[2], a[3] != 0)),
        DistPlain("NEGBINOMDIST", 3, 3, a => DistNegativeBinomial(a[0], a[1], a[2], false)),
        DistFlagged("NORM.DIST", 4, 4, 3, a => DistNormal(a[0], a[1], a[2], a[3] != 0)),
        DistFlagged("NORMDIST", 4, 4, 3, a => DistNormal(a[0], a[1], a[2], a[3] != 0)),
        DistFlagged("NORM.S.DIST", 2, 2, 1, a => DistNormal(a[0], 0, 1, a[1] != 0)),
        DistPlain("NORMSDIST", 1, 1, a => DistNormal(a[0], 0, 1, true)),
        DistPlain("NORM.INV", 3, 3, a => DistNormalInverse(a[0], a[1], a[2])),
        DistPlain("NORMINV", 3, 3, a => DistNormalInverse(a[0], a[1], a[2])),
        DistPlain("NORM.S.INV", 1, 1, a => DistNormalInverse(a[0], 0, 1)),
        DistPlain("NORMSINV", 1, 1, a => DistNormalInverse(a[0], 0, 1)),
        new("PERCENTRANK", 2, 3, Kinds.ArrayThenValues, (a, c) => DistPercentRank(a, c, exclusive: false)),
        new("PERCENTRANK.INC", 2, 3, Kinds.ArrayThenValues, (a, c) => DistPercentRank(a, c, exclusive: false)),
        new("PERCENTRANK.EXC", 2, 3, Kinds.ArrayThenValues, (a, c) => DistPercentRank(a, c, exclusive: true)),
        DistPlain("PERMUTATIONA", 2, 2, a => DistPermutationA(a[0], a[1])),
        DistPlain("PHI", 1, 1, a => DistDensityOfZ(a[0])),
        DistFlagged("POISSON.DIST", 3, 3, 2, a => DistPoisson(a[0], a[1], a[2] != 0)),
        DistFlagged("POISSON", 3, 3, 2, a => DistPoisson(a[0], a[1], a[2] != 0)),
        new("PROB", 3, 4, DistTwoArraysThenValues, DistProb),
        Aggregate("SKEW", n => DistSkewness(n, sample: true)),
        Aggregate("SKEW.P", n => DistSkewness(n, sample: false)),
        new("STANDARDIZE", 3, 3, Kinds.Value, DistStandardize),
        new("STEYX", 2, 2, Kinds.Array, (a, c) => Paired(a, c, DistStandardErrorOfFit)),
        DistFlagged("T.DIST", 3, 3, 2, a => DistStudent(a[0], a[1], a[2] != 0)),
        DistPlain("T.DIST.2T", 2, 2, a => DistStudentTwoTail(a[0], a[1])),
        DistPlain("T.DIST.RT", 2, 2, a => DistStudentRight(a[0], a[1])),
        DistPlain("TDIST", 3, 3, a => DistStudentLegacy(a[0], a[1], a[2])),
        DistPlain("T.INV", 2, 2, a => DistStudentInverse(a[0], a[1])),
        DistPlain("T.INV.2T", 2, 2, a => DistStudentInverseTwoTail(a[0], a[1])),
        DistPlain("TINV", 2, 2, a => DistStudentInverseTwoTail(a[0], a[1])),
        new("T.TEST", 4, 4, DistTwoArraysThenValues, DistStudentTest),
        new("TTEST", 4, 4, DistTwoArraysThenValues, DistStudentTest),
        new("TRIMMEAN", 2, 2, Kinds.ArrayThenValues, DistTrimMean),
        DistFlagged("WEIBULL.DIST", 4, 4, 3, a => DistWeibull(a[0], a[1], a[2], a[3] != 0)),
        DistFlagged("WEIBULL", 4, 4, 3, a => DistWeibull(a[0], a[1], a[2], a[3] != 0)),
        new("Z.TEST", 2, 3, Kinds.ArrayThenValues, DistZTest),
        new("ZTEST", 2, 3, Kinds.ArrayThenValues, DistZTest),
        new("LINEST", 1, 4, DistRegressionKinds, (a, c) => DistLinest(a, c, exponential: false)),
        new("LOGEST", 1, 4, DistRegressionKinds, (a, c) => DistLinest(a, c, exponential: true)),
        new("TREND", 1, 4, DistProjectionKinds, (a, c) => DistProject(a, c, exponential: false)),
        new("GROWTH", 1, 4, DistProjectionKinds, (a, c) => DistProject(a, c, exponential: true)),
    ];

    private static readonly ArgumentKind[] DistTwoArraysThenValues = [ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Value];
    private static readonly ArgumentKind[] DistRegressionKinds = [ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Value];
    private static readonly ArgumentKind[] DistProjectionKinds = [ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Value];

    /// <summary>A function of numbers given as values: the first argument that is no number gives its error; a result that is not finite is #NUM!.</summary>
    private static FormulaFunction DistPlain(string name, int min, int max, Func<double[], double> body) => DistFlagged(name, min, max, -1, body);

    /// <summary>The same, with one argument a TRUE or FALSE, handed on as 1 or 0.</summary>
    private static FormulaFunction DistFlagged(string name, int min, int max, int flag, Func<double[], double> body) =>
        new(name, min, max, Kinds.Value, (arguments, context) =>
        {
            double[] numbers = new double[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i >= min && arguments[i].Kind == EvalKind.Missing)
                {
                    numbers[i] = double.NaN;
                }
                else if (i == flag)
                {
                    if (!ExcelValues.TryBoolean(arguments[i], out bool on, out EvalValue flagError))
                    {
                        return flagError;
                    }

                    numbers[i] = on ? 1 : 0;
                }
                else if (!Num(arguments[i], context, out numbers[i], out EvalValue error))
                {
                    return error;
                }
                else if (Math.Abs(numbers[i]) < Subnormal)
                {
                    numbers[i] = 0;
                }
            }

            double result = body(numbers);
            return double.IsNegativeInfinity(result) ? Error(CellError.DivisionByZero) : Number(Math.Abs(result) < Subnormal ? 0 : result);
        });

    /// <summary>Below this, the smallest normal number, Excel reads and gives 0.</summary>
    private const double Subnormal = 2.2250738585072014e-308;

    /// <summary>What a body gives for #DIV/0!.</summary>
    private const double DistDivisionByZero = double.NegativeInfinity;

    /// <summary>An optional argument: the fallback when left off or left empty.</summary>
    private static double DistOr(double[] a, int index, double fallback) => index < a.Length && !double.IsNaN(a[index]) ? a[index] : fallback;

    private static double DistFisherInverse(double y)
    {
        double e = Math.Exp(2 * y);
        return double.IsInfinity(e) ? 1 : (e - 1) / (e + 1);
    }

    /// <summary>The standard normal density; #NUM! when z squared is too large to hold.</summary>
    private static double DistDensityOfZ(double z) => Math.Abs(z) > 1e154 ? double.NaN : DistMath.NormalDensity(z);

    private static double DistBeta(double x, double a, double b, bool cumulative, double low, double high)
    {
        if (a <= 0 || b <= 0 || x < low || x > high || low == high)
        {
            return double.NaN;
        }

        double width = high - low, z = (x - low) / width, y = (high - x) / width;
        if (cumulative)
        {
            return DistMath.BetaLower(z, y, a, b);
        }

        // Excel looks at x itself here, before it scales it to the range.
        if ((x == 0 && a <= 1) || (x == 1 && b <= 1))
        {
            return double.NaN;
        }

        return z <= 0 || y <= 0 ? 0 : DistMath.BetaDensity(z, y, a, b) / width;
    }

    private static double DistBetaInverse(double p, double a, double b, double low, double high)
    {
        // Past 1E+17 the tails take too long to search, and Excel doesn't answer within minutes.
        if (a <= 0 || b <= 0 || a > 1e17 || b > 1e17 || p <= 0 || p >= 1 || low >= high)
        {
            return double.NaN;
        }

        return low + ((high - low) * DistMath.BetaInverse(p, 1 - p, a, b));
    }

    private static double DistBinomial(double k, double n, double p, bool cumulative)
    {
        if (k < 0)
        {
            return double.NaN;
        }

        k = Math.Truncate(k);
        n = Math.Truncate(n);
        if (n < 0 || n > MaxCount || k > n || p < 0 || p > 1)
        {
            return double.NaN;
        }

        double q = 1 - p;
        if (!cumulative)
        {
            return k == 0 && q == 0 && n > 0 ? double.NaN : DistMath.BinomialTerm(k, n, p, q);
        }

        return k == n ? 1 : p == 0 ? 1 : q == 0 ? 0 : DistMath.BetaLower(q, p, n - k, k + 1);
    }

    private static double DistBinomialRange(double[] a)
    {
        double n = Math.Truncate(a[0]), p = a[1], first = Math.Truncate(a[2]), last = Math.Truncate(DistOr(a, 3, first));
        if (n < 0 || n > MaxCount || p < 0 || p > 1 || first < 0 || first > n || last < first || last > n)
        {
            return double.NaN;
        }

        if (first == last)
        {
            return DistMath.BinomialTerm(first, n, p, 1 - p);
        }

        double q = 1 - p;
        if (p == 0)
        {
            return first == 0 ? 1 : 0;
        }

        if (q == 0)
        {
            return last == n ? 1 : 0;
        }

        if (last - first > 1e6)
        {
            return DistBinomial(last, n, p, true) - (first > 0 ? DistBinomial(first - 1, n, p, true) : 0);
        }

        double total = 0;
        for (double k = first; k <= last; k++)
        {
            total += DistMath.BinomialTerm(k, n, p, q);
        }

        return total;
    }

    private static double DistBinomialInverse(double n, double p, double alpha)
    {
        n = Math.Truncate(n);
        if (n < 0 || n > MaxCount || p <= 0 || p >= 1 || alpha <= 0 || alpha >= 1)
        {
            return double.NaN;
        }

        double q = 1 - p;
        double first = Math.Exp(n * Math.Log(q));
        if (n <= 1000 && first > 0)
        {
            // The first term by its logarithm and compared strictly, the others not, as Excel's results at exact hits show.
            double term = first, total = first;
            for (double k = 0; k < n; k++)
            {
                if (k == 0 ? total > alpha : total >= alpha)
                {
                    return k;
                }

                term *= (n - k) / (k + 1) * p / q;
                total += term;
            }

            return n;
        }

        double low = -1, high = n;
        while (high - low > 1)
        {
            double middle = Math.Floor((low + high) / 2);
            if (DistMath.BetaLower(q, p, n - middle, middle + 1) >= alpha)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return high;
    }

    private static bool DistBadFreedom(double df) => df < 1 || df > 1e10;

    private static double DistChiSquare(double x, double df, bool cumulative)
    {
        df = Math.Truncate(df);
        if (x < 0 || DistBadFreedom(df))
        {
            return double.NaN;
        }

        return cumulative ? DistMath.GammaLower(df / 2, x / 2) : DistMath.GammaDensity(x / 2, df / 2) / 2;
    }

    private static double DistChiSquareRight(double x, double df)
    {
        df = Math.Truncate(df);
        return x < 0 || DistBadFreedom(df) ? double.NaN : DistMath.GammaUpper(df / 2, x / 2);
    }

    private static double DistChiSquareInverse(double p, double df, bool rightTail)
    {
        df = Math.Truncate(df);
        if (p < 0 || p > 1 || df < 1 || (!rightTail && df > 1e10))
        {
            return double.NaN;
        }

        double lower = rightTail ? 1 - p : p, upper = rightTail ? p : 1 - p;
        return lower == 0 ? 0 : upper == 0 ? double.NaN : 2 * DistMath.GammaInverse(lower, upper, df / 2);
    }

    /// <summary>The numbers of a grid in reading order, each slot either a number or null; the first error met comes back.</summary>
    private static EvalValue? DistCells(in EvalValue argument, out double?[] cells, out int rows, out int columns)
    {
        cells = [];
        rows = columns = 0;
        if (argument.IsError)
        {
            return argument;
        }

        if (Grid.Of(argument) is not { } grid)
        {
            return Error(CellError.Value);
        }

        rows = grid.Rows;
        columns = grid.Columns;
        if (grid.Count > 1 << 24)
        {
            return Error(CellError.Value);
        }

        cells = new double?[grid.Count];
        EvalValue? firstError = null;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                EvalValue value = grid[r, c];
                if (value.Kind == EvalKind.Number)
                {
                    cells[(r * columns) + c] = value.Number;
                }
                else if (value.IsError && firstError is null)
                {
                    firstError = value;
                }
            }
        }

        return firstError;
    }

    private static EvalValue DistChiSquareTest(EvalValue[] arguments, EvalContext context)
    {
        if (DistCells(arguments[0], out double?[] actual, out int rows, out int columns) is { } error)
        {
            return error;
        }

        if (DistCells(arguments[1], out double?[] expected, out _, out _) is { } otherError)
        {
            return otherError;
        }

        if (actual.Length != expected.Length || actual.Length == 1)
        {
            return Error(CellError.NotAvailable);
        }

        double df = rows == 1 ? columns - 1 : columns == 1 ? rows - 1 : (rows - 1) * (columns - 1.0);
        double chi = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] is not { } a || expected[i] is not { } e)
            {
                continue;
            }

            if (e < 0)
            {
                return Error(CellError.Number);
            }

            if (e == 0)
            {
                return Error(CellError.DivisionByZero);
            }

            chi += (a - e) * (a - e) / e;
        }

        return Number(DistMath.GammaUpper(df / 2, chi / 2));
    }

    private static double DistConfidenceNormal(double alpha, double sd, double size)
    {
        size = Math.Truncate(size);
        return alpha <= 0 || alpha >= 1 || sd <= 0 || size < 1 ? double.NaN : -DistMath.NormalInverse(alpha / 2) * sd / Math.Sqrt(size);
    }

    private static EvalValue DistConfidenceT(EvalValue[] arguments, EvalContext context)
    {
        double[] a = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!Num(arguments[i], context, out a[i], out EvalValue error))
            {
                return error;
            }
        }

        double size = Math.Truncate(a[2]);
        if (a[0] <= 0 || a[0] >= 1 || a[1] <= 0 || size < 1 || size - 1 > 1e10)
        {
            return Error(CellError.Number);
        }

        return size == 1 ? Error(CellError.DivisionByZero) : Number(DistMath.StudentInverseTail(a[0] / 2, size - 1) * a[1] / Math.Sqrt(size));
    }

    private static double DistExponential(double x, double lambda, bool cumulative)
    {
        if (x < 0 || lambda <= 0)
        {
            return double.NaN;
        }

        double power = lambda * x;
        if (double.IsInfinity(power))
        {
            return double.NaN;
        }

        return cumulative ? -DistMath.ExpM1(-power) : lambda * Math.Exp(-power);
    }

    private static double DistF(double x, double d1, double d2, bool cumulative)
    {
        d1 = Math.Truncate(d1);
        d2 = Math.Truncate(d2);
        if (x < 0 || DistBadFreedom(d1) || DistBadFreedom(d2))
        {
            return double.NaN;
        }

        return cumulative ? DistMath.FLower(x, d1, d2) : DistMath.FDensity(x, d1, d2);
    }

    private static double DistFRight(double x, double d1, double d2)
    {
        d1 = Math.Truncate(d1);
        d2 = Math.Truncate(d2);
        return x < 0 || DistBadFreedom(d1) || DistBadFreedom(d2) ? double.NaN : DistMath.FUpper(x, d1, d2);
    }

    private static double DistFInverse(double p, double d1, double d2, bool rightTail)
    {
        d1 = Math.Truncate(d1);
        d2 = Math.Truncate(d2);
        if (p < 0 || p > 1 || DistBadFreedom(d1) || DistBadFreedom(d2))
        {
            return double.NaN;
        }

        double lower = rightTail ? 1 - p : p, upper = rightTail ? p : 1 - p;
        return lower == 0 ? 0 : upper == 0 ? double.NaN : DistMath.FInverse(lower, upper, d1, d2);
    }

    private static EvalValue DistFTest(EvalValue[] arguments, EvalContext context)
    {
        List<double> first = [], second = [];
        if (Gather(arguments, context, first, end: 1) is { } error)
        {
            return error;
        }

        if (Gather(arguments, context, second, first: 1, end: 2) is { } otherError)
        {
            return otherError;
        }

        if (first.Count < 2 || second.Count < 2)
        {
            return Error(CellError.DivisionByZero);
        }

        double v1 = SquaredDeviations(first) / (first.Count - 1), v2 = SquaredDeviations(second) / (second.Count - 1);
        if (v1 == 0 || v2 == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double f = v1 / v2, upper = DistMath.FUpper(f, first.Count - 1, second.Count - 1), lower = DistMath.FLower(f, first.Count - 1, second.Count - 1);
        return Number(2 * Math.Min(upper, lower));
    }

    private static EvalValue DistFrequency(EvalValue[] arguments, EvalContext context)
    {
        if (DistCells(arguments[0], out double?[] data, out _, out _) is { } error)
        {
            return error;
        }

        // An error among the bins counts as no bin; with none at all there is one bin, at 0.
        _ = DistCells(arguments[1], out double?[] binCells, out _, out _);
        List<double> bins = [.. binCells.Where(b => b.HasValue).Select(b => b!.Value)];
        if (bins.Count == 0)
        {
            bins.Add(0);
        }

        double[] counts = new double[bins.Count + 1];
        int[] order = [.. Enumerable.Range(0, bins.Count).OrderBy(i => bins[i])];
        foreach (double? cell in data)
        {
            if (cell is not { } value)
            {
                continue;
            }

            int slot = bins.Count;
            foreach (int i in order)
            {
                if (value <= bins[i])
                {
                    slot = i;
                    break;
                }
            }

            counts[slot]++;
        }

        return EvalValue.FromArray(new EvalArray(counts.Length, 1, [.. counts.Select(Number)]));
    }

    private static double DistGammaDist(double x, double a, double b, bool cumulative)
    {
        if (x < 0 || a <= 0 || b <= 0)
        {
            return double.NaN;
        }

        if (cumulative)
        {
            return DistMath.GammaLower(a, x / b);
        }

        return x == 0 && a <= 1 ? double.NaN : DistMath.GammaDensity(x / b, a) / b;
    }

    private static double DistGammaInverse(double p, double a, double b)
    {
        if (p < 0 || p > 1 || a <= 0 || b <= 0)
        {
            return double.NaN;
        }

        return p == 0 ? 0 : p == 1 ? double.NaN : b * DistMath.GammaInverse(p, 1 - p, a);
    }

    private static double DistHypergeometricTerm(double s, double n, double k, double total)
    {
        double p = n / total, q = 1 - p;
        return DistMath.BinomialTerm(s, k, p, q) * DistMath.BinomialTerm(n - s, total - k, p, q) / DistMath.BinomialTerm(n, total, p, q);
    }

    private static double DistHypergeometric(double s, double n, double k, double total, bool cumulative)
    {
        s = Math.Truncate(s);
        n = Math.Truncate(n);
        k = Math.Truncate(k);
        total = Math.Truncate(total);
        double lowest = Math.Max(0, n - total + k), highest = Math.Min(n, k);
        if (s < 0 || n < 0 || k < 0 || total < 0 || total > MaxCount || n > total || k > total || total == 0)
        {
            return double.NaN;
        }

        if (!cumulative && (s < lowest || s > highest))
        {
            return 0;
        }

        if (!cumulative)
        {
            return DistHypergeometricTerm(s, n, k, total);
        }

        if (s >= highest)
        {
            return 1;
        }

        if (s < lowest)
        {
            return 0;
        }

        double mode = Math.Floor((n + 1) * (k + 1) / (total + 2));
        if (s < mode)
        {
            double term = DistHypergeometricTerm(s, n, k, total), sum = 0;
            for (double i = s; i >= lowest; i--)
            {
                sum += term;
                if (term < 1e-20 * sum)
                {
                    break;
                }

                term *= i * (total - k - n + i) / ((n - i + 1) * (k - i + 1));
            }

            return sum;
        }
        else
        {
            double term = DistHypergeometricTerm(s + 1, n, k, total), sum = 0;
            for (double i = s + 1; i <= highest; i++)
            {
                sum += term;
                if (term < 1e-20 * sum)
                {
                    break;
                }

                term *= (n - i) * (k - i) / ((i + 1) * (total - k - n + i + 1));
            }

            return 1 - sum;
        }
    }

    private static EvalValue DistKurtosis(List<double> numbers)
    {
        double n = numbers.Count;
        if (n < 4)
        {
            return Error(CellError.DivisionByZero);
        }

        double mean = numbers.Sum() / n, s2 = SquaredDeviations(numbers) / (n - 1);
        if (s2 == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double total = 0;
        foreach (double x in numbers)
        {
            double z = (x - mean) * (x - mean) / s2;
            total += z * z;
        }

        return Number((n * (n + 1) / ((n - 1) * (n - 2) * (n - 3)) * total) - (3 * (n - 1) * (n - 1) / ((n - 2) * (n - 3))));
    }

    private static double DistLogNormal(double x, double mean, double sd, bool cumulative)
    {
        if (x <= 0 || sd <= 0)
        {
            return double.NaN;
        }

        double z = (Math.Log(x) - mean) / sd;
        return cumulative ? DistMath.NormalLower(z) : DistMath.NormalDensity(z) / (x * sd);
    }

    private static double DistLogNormalInverse(double p, double mean, double sd) =>
        p <= 0 || p >= 1 || sd <= 0 ? double.NaN : Math.Exp(mean + (sd * DistMath.NormalInverse(p)));

    private static EvalValue DistModes(EvalValue[] arguments, EvalContext context)
    {
        List<double> numbers = [];
        foreach (EvalValue argument in arguments)
        {
            foreach ((EvalValue value, _) in Scalars(argument))
            {
                if (value.IsError)
                {
                    return value;
                }

                if (value.Kind == EvalKind.Number)
                {
                    numbers.Add(value.Number);
                }
            }
        }

        Dictionary<double, int> counts = [];
        int most = 1;
        foreach (double n in numbers)
        {
            most = Math.Max(most, counts[n] = counts.GetValueOrDefault(n) + 1);
        }

        if (most < 2)
        {
            return Error(CellError.NotAvailable);
        }

        List<EvalValue> modes = [];
        HashSet<double> seen = [];
        foreach (double n in numbers)
        {
            if (counts[n] == most && seen.Add(n))
            {
                modes.Add(Number(n));
            }
        }

        return EvalValue.FromArray(new EvalArray(modes.Count, 1, [.. modes]));
    }

    private static double DistNegativeBinomial(double failures, double successes, double p, bool cumulative)
    {
        failures = Math.Truncate(failures);
        successes = Math.Truncate(successes);
        if (failures < 0 || successes < 1 || p <= 0 || p >= 1)
        {
            return double.NaN;
        }

        double q = 1 - p;
        return cumulative ? DistMath.BetaLower(p, q, successes, failures + 1)
            : successes / (successes + failures) * DistMath.BinomialTerm(successes, successes + failures, p, q);
    }

    private static double DistNormal(double x, double mean, double sd, bool cumulative)
    {
        if (sd <= 0)
        {
            return double.NaN;
        }

        double z = (x - mean) / sd;
        return cumulative ? DistMath.NormalLower(z) : DistDensityOfZ(z) / sd;
    }

    private static double DistNormalInverse(double p, double mean, double sd) =>
        p <= 0 || p >= 1 || sd <= 0 ? double.NaN : mean + (sd * DistMath.NormalInverse(p));

    private static EvalValue DistPercentRank(EvalValue[] arguments, EvalContext context, bool exclusive)
    {
        List<double> numbers = [];
        if (Gather(arguments, context, numbers, end: 1) is { } error)
        {
            return error;
        }

        if (!Num(arguments[1], context, out double x, out EvalValue xError))
        {
            return xError;
        }

        if (!Optional(arguments, 2, 3, context, out double digitsValue, out EvalValue digitsError))
        {
            return digitsError;
        }

        double digits = Math.Truncate(digitsValue);
        if (digits < 1)
        {
            return Error(CellError.Number);
        }

        if (numbers.Count == 0)
        {
            return Error(CellError.NotAvailable);
        }

        numbers.Sort();
        int n = numbers.Count;
        if (x < numbers[0] || x > numbers[n - 1])
        {
            return Error(CellError.NotAvailable);
        }

        int below = numbers.Count(v => v < x);
        double position = below < n && numbers[below] == x ? below : below - 1 + ((x - numbers[below - 1]) / (numbers[below] - numbers[below - 1]));
        double rank = n == 1 ? 1 : exclusive ? (position + 1) / (n + 1) : position / (n - 1);

        // Cut to the digits asked for, once noise past the fifteenth digit is gone.
        double scale = Math.Pow(10, digits), scaled = rank * scale;
        scaled = double.Parse(scaled.ToString("E14", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        return Number(Math.Floor(scaled) / scale);
    }

    private static double DistPermutationA(double n, double k)
    {
        k = Math.Truncate(k);
        return n < 0 || k < 0 || Math.Truncate(n) > MaxCount ? double.NaN : Math.Pow(Math.Truncate(n), k);
    }

    private static double DistPoisson(double x, double mean, bool cumulative)
    {
        if (x < 0 || mean < 0)
        {
            return double.NaN;
        }

        x = Math.Truncate(x);

        return cumulative ? DistMath.GammaUpper(x + 1, mean) : DistMath.PoissonTerm(x, mean);
    }

    private static EvalValue DistProb(EvalValue[] arguments, EvalContext context)
    {
        if (DistCells(arguments[0], out double?[] xs, out _, out _) is { } error)
        {
            return error;
        }

        if (DistCells(arguments[1], out double?[] ps, out _, out _) is { } otherError)
        {
            return otherError;
        }

        if (!Num(arguments[2], context, out double low, out EvalValue lowError))
        {
            return lowError;
        }

        double high = low;
        if (arguments.Length > 3 && arguments[3].Kind != EvalKind.Missing && !Num(arguments[3], context, out high, out EvalValue highError))
        {
            return highError;
        }

        if (xs.Length != ps.Length)
        {
            return Error(CellError.NotAvailable);
        }

        double total = 0, sum = 0;
        int pairs = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            if (xs[i] is not { } x || ps[i] is not { } p)
            {
                continue;
            }

            pairs++;
            total += p;
            if (x >= low && x <= high)
            {
                sum += p;
            }
        }

        if (pairs == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        return Math.Abs(total - 1) > 5e-15 ? Error(CellError.Number) : Number(sum);
    }

    private static EvalValue DistSkewness(List<double> numbers, bool sample)
    {
        double n = numbers.Count;
        if (n < 3)
        {
            return Error(CellError.DivisionByZero);
        }

        double mean = numbers.Sum() / n, s2 = SquaredDeviations(numbers) / (sample ? n - 1 : n);
        if (s2 == 0)
        {
            return Error(CellError.DivisionByZero);
        }

        double s = Math.Sqrt(s2), total = 0;
        foreach (double x in numbers)
        {
            double z = (x - mean) / s;
            total += z * z * z;
        }

        return Number(sample ? n / ((n - 1) * (n - 2)) * total : total / n);
    }

    private static EvalValue DistStandardize(EvalValue[] arguments, EvalContext context)
    {
        double[] a = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!Num(arguments[i], context, out a[i], out EvalValue error))
            {
                return error;
            }
        }

        return a[2] <= 0 ? Error(CellError.Number) : Number((a[0] - a[1]) / a[2]);
    }

    private static EvalValue DistStandardErrorOfFit(List<double> ys, List<double> xs)
    {
        int n = xs.Count;
        if (n < 3)
        {
            return Error(CellError.DivisionByZero);
        }

        double mx = xs.Average(), my = ys.Average(), sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            sxx += (xs[i] - mx) * (xs[i] - mx);
            syy += (ys[i] - my) * (ys[i] - my);
            sxy += (xs[i] - mx) * (ys[i] - my);
        }

        return sxx == 0 ? Error(CellError.DivisionByZero) : Number(Math.Sqrt(Math.Max(0, syy - (sxy * sxy / sxx)) / (n - 2)));
    }

    private static double DistStudent(double x, double df, bool cumulative)
    {
        df = Math.Truncate(df);
        if (!cumulative)
        {
            return df < 1 ? DistDivisionByZero : DistMath.StudentDensity(x, df);
        }

        return DistBadFreedom(df) ? double.NaN : DistMath.StudentLower(x, df);
    }

    private static double DistStudentTwoTail(double x, double df)
    {
        df = Math.Truncate(df);
        return DistBadFreedom(df) || x < 0 ? double.NaN : 2 * DistMath.StudentTail(x, df);
    }

    private static double DistStudentRight(double x, double df)
    {
        df = Math.Truncate(df);
        return DistBadFreedom(df) ? double.NaN : x < 0 ? 1 - DistMath.StudentTail(-x, df) : DistMath.StudentTail(x, df);
    }

    private static double DistStudentLegacy(double x, double df, double tails)
    {
        df = Math.Truncate(df);
        tails = Math.Truncate(tails);
        return x < 0 || DistBadFreedom(df) || tails is not (1 or 2) ? double.NaN : tails * DistMath.StudentTail(x, df);
    }

    private static double DistStudentInverse(double p, double df)
    {
        df = Math.Truncate(df);
        if (p == 0 && df == 1)
        {
            return DistDivisionByZero;
        }

        if (p <= 0 || p >= 1 || DistBadFreedom(df))
        {
            return double.NaN;
        }

        return p < 0.5 ? -DistMath.StudentInverseTail(p, df) : DistMath.StudentInverseTail(1 - p, df);
    }

    private static double DistStudentInverseTwoTail(double p, double df)
    {
        df = Math.Truncate(df);
        if (p == 0 && df == 1)
        {
            return DistDivisionByZero;
        }

        if (p <= 0 || p >= 2 || DistBadFreedom(df))
        {
            return double.NaN;
        }

        // Past 1 the two tails overlap and the answer turns negative.
        return p > 1 ? -DistMath.StudentInverseTail((2 - p) / 2, df) : DistMath.StudentInverseTail(p / 2, df);
    }

    private static EvalValue DistStudentTest(EvalValue[] arguments, EvalContext context)
    {
        if (DistCells(arguments[0], out double?[] first, out _, out _) is { } error)
        {
            return error;
        }

        if (DistCells(arguments[1], out double?[] second, out _, out _) is { } otherError)
        {
            return otherError;
        }

        if (!Num(arguments[2], context, out double tailsValue, out EvalValue tailsError))
        {
            return tailsError;
        }

        if (!Num(arguments[3], context, out double typeValue, out EvalValue typeError))
        {
            return typeError;
        }

        double tails = Math.Truncate(tailsValue), type = Math.Truncate(typeValue);
        if (tails is not (1 or 2) || type is not (1 or 2 or 3))
        {
            return Error(CellError.Number);
        }

        double t, df;
        if (type == 1)
        {
            if (first.Length != second.Length)
            {
                return Error(CellError.NotAvailable);
            }

            List<double> differences = [];
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] is { } a && second[i] is { } b)
                {
                    differences.Add(a - b);
                }
            }

            int n = differences.Count;
            if (n < 2)
            {
                return Error(CellError.DivisionByZero);
            }

            double variance = SquaredDeviations(differences) / (n - 1);
            if (variance == 0)
            {
                return Error(CellError.DivisionByZero);
            }

            t = differences.Sum() / n / Math.Sqrt(variance / n);
            df = n - 1;
        }
        else
        {
            List<double> x = [.. first.Where(v => v.HasValue).Select(v => v!.Value)], y = [.. second.Where(v => v.HasValue).Select(v => v!.Value)];
            int nx = x.Count, ny = y.Count;
            if (nx < 2 || ny < 2)
            {
                return Error(CellError.DivisionByZero);
            }

            double vx = SquaredDeviations(x) / (nx - 1), vy = SquaredDeviations(y) / (ny - 1);
            if (type == 2)
            {
                df = nx + ny - 2;
                double pooled = ((nx - 1) * vx + ((ny - 1) * vy)) / df;
                if (pooled == 0)
                {
                    return Error(CellError.DivisionByZero);
                }

                t = (x.Sum() / nx - (y.Sum() / ny)) / Math.Sqrt(pooled * (1.0 / nx + (1.0 / ny)));
            }
            else
            {
                double ax = vx / nx, ay = vy / ny;
                if (ax + ay == 0)
                {
                    return Error(CellError.DivisionByZero);
                }

                df = (ax + ay) * (ax + ay) / ((ax * ax / (nx - 1)) + (ay * ay / (ny - 1)));
                t = (x.Sum() / nx - (y.Sum() / ny)) / Math.Sqrt(ax + ay);
            }
        }

        return Number(tails * DistMath.StudentTail(Math.Abs(t), df));
    }

    private static EvalValue DistTrimMean(EvalValue[] arguments, EvalContext context)
    {
        List<double> numbers = [];
        if (Gather(arguments, context, numbers, end: 1) is { } error)
        {
            return error;
        }

        if (!Num(arguments[1], context, out double percent, out EvalValue percentError))
        {
            return percentError;
        }

        int n = numbers.Count;
        if (n == 0 || percent < 0 || percent >= 1)
        {
            return Error(CellError.Number);
        }

        int cut = (int)Math.Floor(n * percent / 2);
        numbers.Sort();
        double total = 0;
        for (int i = cut; i < n - cut; i++)
        {
            total += numbers[i];
        }

        return Number(total / (n - (2 * cut)));
    }

    private static double DistWeibull(double x, double alpha, double beta, bool cumulative)
    {
        if (x < 0 || alpha <= 0 || beta <= 0)
        {
            return double.NaN;
        }

        if (x == 0)
        {
            return 0;
        }

        double power = Math.Exp(alpha * Math.Log(x / beta));
        return cumulative ? -DistMath.ExpM1(-power) : alpha / beta * Math.Exp((alpha - 1) * Math.Log(x / beta)) * Math.Exp(-power);
    }

    private static EvalValue DistZTest(EvalValue[] arguments, EvalContext context)
    {
        List<double> numbers = [];
        if (Gather(arguments, context, numbers, end: 1) is { } error)
        {
            return error;
        }

        if (!Num(arguments[1], context, out double x, out EvalValue xError))
        {
            return xError;
        }

        if (numbers.Count == 0)
        {
            return Error(CellError.NotAvailable);
        }

        if (numbers.Count < 2)
        {
            return Error(CellError.DivisionByZero);
        }

        double sigma;
        if (arguments.Length > 2)
        {
            if (!Num(arguments[2], context, out sigma, out EvalValue sigmaError))
            {
                return sigmaError;
            }

            if (sigma <= 0)
            {
                return Error(CellError.Number);
            }
        }
        else
        {
            sigma = Math.Sqrt(SquaredDeviations(numbers) / (numbers.Count - 1));
            if (sigma == 0)
            {
                return Error(CellError.DivisionByZero);
            }
        }

        return Number(DistMath.NormalUpper((numbers.Sum() / numbers.Count - x) / (sigma / Math.Sqrt(numbers.Count))));
    }
}
