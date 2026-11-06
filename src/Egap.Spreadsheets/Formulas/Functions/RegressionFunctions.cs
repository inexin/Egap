namespace Egap.Spreadsheets.Formulas;

/// <summary>LINEST, LOGEST, TREND and GROWTH: least squares by a QR decomposition, with collinear columns left out as Excel leaves them out.</summary>
internal static partial class FormulaFunctions
{
    /// <summary>A fitted line: coefficients by variable (0 for one left out), their standard errors, and the summary statistics.</summary>
    private sealed class DistFit
    {
        public required double[] Coefficients { get; init; }

        public required double[] Errors { get; init; }

        public required bool[] Dropped { get; init; }

        public double Intercept { get; init; }

        public double InterceptError { get; init; }

        public double SumSquaresRegression { get; init; }

        public double SumSquaresResidual { get; init; }

        public double SumSquaresTotal { get; init; }

        public int Freedom { get; init; }

        public int RegressionFreedom { get; init; }
    }

    /// <summary>The y values and the x matrix (one array per variable) the first arguments give, with how they lie.</summary>
    private static EvalValue? DistRegressionInput(EvalValue[] arguments, EvalContext context, bool exponential, out double[] ys, out double[][] xs, out bool byRows, out int yRows, out int yColumns)
    {
        ys = [];
        xs = [];
        byRows = false;
        yRows = yColumns = 0;
        if (DistAllNumbers(arguments[0], out double[] yCells, out yRows, out yColumns) is { } yError)
        {
            return yError;
        }

        if (exponential)
        {
            foreach (double y in yCells)
            {
                if (y <= 0)
                {
                    return Error(CellError.Number);
                }
            }

            yCells = [.. yCells.Select(y => Math.Log(y))];
        }

        ys = yCells;
        int n = ys.Length;
        byRows = yRows == 1 && yColumns > 1;
        if (arguments.Length < 2 || arguments[1].Kind == EvalKind.Missing)
        {
            double[] counting = new double[n];
            for (int i = 0; i < n; i++)
            {
                counting[i] = i + 1;
            }

            xs = [counting];
            return null;
        }

        if (DistAllNumbers(arguments[1], out double[] xCells, out int xRows, out int xColumns) is { } xError)
        {
            return xError;
        }

        if (yRows > 1 && yColumns > 1)
        {
            if (xCells.Length != n)
            {
                return Error(CellError.Reference);
            }

            xs = [xCells];
            return null;
        }

        if (byRows)
        {
            if (xColumns != n)
            {
                return Error(CellError.Reference);
            }

            xs = new double[xRows][];
            for (int v = 0; v < xRows; v++)
            {
                xs[v] = new double[n];
                Array.Copy(xCells, v * xColumns, xs[v], 0, n);
            }

            return null;
        }

        if (yRows == 1 && yColumns == 1 && xCells.Length != 1)
        {
            if (xRows != 1)
            {
                return Error(CellError.Reference);
            }
        }
        else if (xRows != n)
        {
            return Error(CellError.Reference);
        }

        xs = new double[xColumns][];
        for (int v = 0; v < xColumns; v++)
        {
            xs[v] = new double[n];
            for (int i = 0; i < n; i++)
            {
                xs[v][i] = xCells[(i * xColumns) + v];
            }
        }

        return null;
    }

    /// <summary>Every cell of an argument as a number; #VALUE! when a cell holds anything else.</summary>
    private static EvalValue? DistAllNumbers(in EvalValue argument, out double[] numbers, out int rows, out int columns)
    {
        numbers = [];
        if (DistCells(argument, out double?[] cells, out rows, out columns) is not null)
        {
            return Error(CellError.Value);
        }

        numbers = new double[cells.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } number)
            {
                return Error(CellError.Value);
            }

            numbers[i] = number;
        }

        return null;
    }

    /// <summary>
    /// Least squares by Householder's QR with column pivoting, the constant first when there is one, then at each step the
    /// column with the largest part left. A column is left out when what is left of it is under a millionth of its own size,
    /// as Excel does it. A column that is all one value alongside a constant, or all zeros without one, is no pivot but still
    /// uses up a row, a degree of freedom and that row's share of the residual, as in Excel.
    /// </summary>
    private static DistFit DistSolve(double[] ys, double[][] xs, bool constant)
    {
        const double Collinear = 1e-6, Vanishing = 1e-13;
        int n = ys.Length, k = xs.Length;
        double[][] columns = new double[k][];
        double yMean = ys.Sum() / n;
        double[] y = new double[n];
        for (int i = 0; i < n; i++)
        {
            y[i] = constant ? ys[i] - yMean : ys[i];
        }

        double[] size = new double[k], means = new double[k];
        bool[] vanished = new bool[k], blank = new bool[k], used = new bool[k];
        for (int v = 0; v < k; v++)
        {
            double mean = constant ? xs[v].Sum() / n : 0;
            means[v] = mean;
            columns[v] = new double[n];
            for (int i = 0; i < n; i++)
            {
                columns[v][i] = xs[v][i] - mean;
            }

            size[v] = DistNorm(columns[v]);
            blank[v] = size[v] == 0;
            vanished[v] = !blank[v] && size[v] <= Vanishing * DistNorm(xs[v]);
        }

        int row = 0;
        if (constant)
        {
            // With the means taken out, the reflection that takes the column of ones to its first row: Excel's other rows.
            double[] u = new double[n];
            Array.Fill(u, 1 / Math.Sqrt(n));
            u[0] += 1;
            DistReflect(u, 0, columns, y);
            row = 1;
        }

        List<int> pivots = [];
        while (row < n)
        {
            int best = -1;
            double most = 0;
            for (int v = 0; v < k; v++)
            {
                if (used[v] || blank[v] || vanished[v])
                {
                    continue;
                }

                double left = DistNorm(columns[v], row);
                if (left <= Collinear * size[v])
                {
                    continue;
                }

                if (best < 0 || DistBetterPivot(left, size[v], most, size[best], constant))
                {
                    (best, most) = (v, left);
                }
            }

            if (best < 0)
            {
                break;
            }

            used[best] = true;
            pivots.Add(best);
            double[] pivot = columns[best];
            double[] w = new double[n];
            Array.Copy(pivot, row, w, row, n - row);
            double alpha = pivot[row] > 0 ? -most : most;
            w[row] -= alpha;
            DistReflect(w, row, columns, y);
            row++;
        }

        for (int v = 0; v < k && row < n; v++)
        {
            if (blank[v])
            {
                row++;
            }
        }

        int first = constant ? 1 : 0, solved = first + pivots.Count;
        double[,] r = new double[solved, solved];
        if (constant)
        {
            r[0, 0] = -Math.Sqrt(n);
        }

        for (int i = 0; i < solved; i++)
        {
            for (int j = first; j < solved; j++)
            {
                r[i, j] = j >= i ? columns[pivots[j - first]][i] : 0;
                if (i == 0 && constant)
                {
                    r[i, j] = r[0, 0] * means[pivots[j - first]];
                }
            }
        }

        if (constant)
        {
            y[0] = r[0, 0] * yMean;
        }

        double[] beta = new double[solved];
        for (int i = solved - 1; i >= 0; i--)
        {
            double value = y[i];
            for (int j = i + 1; j < solved; j++)
            {
                value -= r[i, j] * beta[j];
            }

            beta[i] = value / r[i, i];
        }

        int freedom = Math.Max(0, n - row);
        double residualSquares = 0;
        for (int i = row; i < n; i++)
        {
            residualSquares += y[i] * y[i];
        }

        double total = 0;
        foreach (double value in ys)
        {
            double centred = constant ? value - yMean : value;
            total += centred * centred;
        }

        double variance = freedom > 0 ? residualSquares / freedom : 0;

        // The inverse of R gives the coefficients' variances, as sums of squares of its rows.
        double[,] inverse = new double[solved, solved];
        for (int i = solved - 1; i >= 0; i--)
        {
            inverse[i, i] = 1 / r[i, i];
            for (int c = i + 1; c < solved; c++)
            {
                double value = 0;
                for (int m = i + 1; m <= c; m++)
                {
                    value -= r[i, m] * inverse[m, c];
                }

                inverse[i, c] = value / r[i, i];
            }
        }

        double StandardError(int at)
        {
            double sum = 0;
            for (int c = at; c < solved; c++)
            {
                sum += inverse[at, c] * inverse[at, c];
            }

            return Math.Sqrt(sum * variance);
        }

        double[] coefficients = new double[k], errors = new double[k];
        bool[] dropped = new bool[k];
        Array.Fill(dropped, true);
        double intercept = yMean;
        for (int j = 0; j < pivots.Count; j++)
        {
            int v = pivots[j];
            dropped[v] = false;
            coefficients[v] = beta[j + first];
            errors[v] = StandardError(j + first);
            intercept -= coefficients[v] * means[v];
        }

        return new DistFit
        {
            Coefficients = coefficients,
            Errors = errors,
            Dropped = dropped,
            Intercept = constant ? intercept : 0,
            InterceptError = constant ? StandardError(0) : 0,
            SumSquaresResidual = residualSquares,
            SumSquaresTotal = total,
            SumSquaresRegression = total - residualSquares,
            Freedom = freedom,
            RegressionFreedom = row - first,
        };
    }

    /// <summary>
    /// Whether a later column takes over as the pivot: when more of it is left. On a tie short of noise the smaller column
    /// overall stays; between columns of one size the later one when there is a constant, the one with more left when not.
    /// </summary>
    private static bool DistBetterPivot(double left, double size, double bestLeft, double bestSize, bool constant)
    {
        const double Tie = 1e-12;
        if (Math.Abs(left - bestLeft) > Tie * Math.Max(left, bestLeft))
        {
            return left > bestLeft;
        }

        return Math.Abs(size - bestSize) > Tie * Math.Max(size, bestSize) ? size < bestSize : constant || left > bestLeft;
    }

    /// <summary>Applies the reflection I - 2vv'/v'v to every column and to y, on the rows from <paramref name="from"/> on.</summary>
    private static void DistReflect(double[] v, int from, double[][] columns, double[] y)
    {
        double scale = 0;
        for (int i = from; i < v.Length; i++)
        {
            scale += v[i] * v[i];
        }

        if (scale == 0)
        {
            return;
        }

        foreach (double[] column in columns)
        {
            DistReflectOne(v, from, scale, column);
        }

        DistReflectOne(v, from, scale, y);
    }

    private static void DistReflectOne(double[] v, int from, double scale, double[] target)
    {
        double dot = 0;
        for (int i = from; i < v.Length; i++)
        {
            dot += v[i] * target[i];
        }

        double factor = 2 * dot / scale;
        for (int i = from; i < v.Length; i++)
        {
            target[i] -= factor * v[i];
        }
    }

    private static double DistDot(double[] a, double[] b)
    {
        double total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    private static double DistNorm(double[] a) => Math.Sqrt(DistDot(a, a));

    private static double DistNorm(double[] a, int from)
    {
        double total = 0;
        for (int i = from; i < a.Length; i++)
        {
            total += a[i] * a[i];
        }

        return Math.Sqrt(total);
    }

    private static EvalValue DistLinest(EvalValue[] arguments, EvalContext context, bool exponential)
    {
        if (DistRegressionInput(arguments, context, exponential, out double[] ys, out double[][] xs, out _, out _, out _) is { } inputError)
        {
            return inputError;
        }

        if (!DistOptionalFlag(arguments, 2, true, out bool constant, out EvalValue constantError))
        {
            return constantError;
        }

        if (!DistOptionalFlag(arguments, 3, false, out bool stats, out EvalValue statsError))
        {
            return statsError;
        }

        DistFit fit = DistSolve(ys, xs, constant);
        int k = xs.Length, columns = k + 1, rows = stats ? 5 : 1;
        EvalValue[] items = new EvalValue[rows * columns];
        EvalValue notAvailable = Error(CellError.NotAvailable);
        Array.Fill(items, notAvailable);
        EvalValue Value(double number) => Number(exponential ? Math.Exp(number) : number);
        for (int v = 0; v < k; v++)
        {
            items[k - 1 - v] = Value(fit.Coefficients[v]);
        }

        items[k] = Value(fit.Intercept);
        if (stats)
        {
            int df = fit.Freedom;
            for (int v = 0; v < k; v++)
            {
                items[columns + k - 1 - v] = Number(fit.Errors[v]);
            }

            items[columns + k] = constant ? Number(fit.InterceptError) : notAvailable;
            items[2 * columns] = fit.SumSquaresTotal == 0 ? Number(1) : Number(fit.SumSquaresRegression / fit.SumSquaresTotal);
            items[(2 * columns) + 1] = Number(df > 0 ? Math.Sqrt(fit.SumSquaresResidual / df) : 0);
            items[3 * columns] = df == 0 || fit.SumSquaresResidual == 0 ? Error(CellError.Number)
                : Number(fit.RegressionFreedom == 0 ? 0 : fit.SumSquaresRegression / fit.RegressionFreedom / (fit.SumSquaresResidual / df));
            items[(3 * columns) + 1] = Number(df);
            items[4 * columns] = Number(fit.SumSquaresRegression);
            items[(4 * columns) + 1] = Number(fit.SumSquaresResidual);
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }

    private static bool DistOptionalFlag(EvalValue[] arguments, int index, bool fallback, out bool flag, out EvalValue error)
    {
        if (index >= arguments.Length || arguments[index].Kind == EvalKind.Missing)
        {
            flag = fallback;
            error = default;
            return true;
        }

        return ExcelValues.TryBoolean(arguments[index], out flag, out error);
    }

    private static EvalValue DistProject(EvalValue[] arguments, EvalContext context, bool exponential)
    {
        if (DistRegressionInput(arguments, context, exponential, out double[] ys, out double[][] xs, out bool byRows, out int yRows, out int yColumns) is { } inputError)
        {
            return inputError;
        }

        if (!DistOptionalFlag(arguments, 3, true, out bool constant, out EvalValue constantError))
        {
            return constantError;
        }

        int k = xs.Length;
        double[] news;
        int newRows, newColumns;
        if (arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing)
        {
            if (DistAllNumbers(arguments[2], out news, out newRows, out newColumns) is { } newError)
            {
                return newError;
            }
        }
        else
        {
            // Without new x values, the fitted values of the known ones, shaped as the y values are.
            news = new double[ys.Length * k];
            for (int i = 0; i < ys.Length; i++)
            {
                for (int v = 0; v < k; v++)
                {
                    news[byRows ? (v * ys.Length) + i : (i * k) + v] = xs[v][i];
                }
            }

            (newRows, newColumns) = byRows ? (k, ys.Length) : (ys.Length, k);
        }

        DistFit fit = DistSolve(ys, xs, constant);
        EvalValue Predict(Func<int, double> x)
        {
            double total = fit.Intercept;
            for (int v = 0; v < k; v++)
            {
                total += fit.Coefficients[v] * x(v);
            }

            return Number(exponential ? Math.Exp(total) : total);
        }

        EvalValue[] items;
        if (k == 1)
        {
            items = new EvalValue[news.Length];
            for (int i = 0; i < news.Length; i++)
            {
                double value = news[i];
                items[i] = Predict(_ => value);
            }

            return EvalValue.FromArray(new EvalArray(newRows, newColumns, items));
        }

        if (byRows)
        {
            if (newRows != k)
            {
                return Error(CellError.Reference);
            }

            items = new EvalValue[newColumns];
            for (int c = 0; c < newColumns; c++)
            {
                int column = c;
                items[c] = Predict(v => news[(v * newColumns) + column]);
            }

            return EvalValue.FromArray(new EvalArray(1, newColumns, items));
        }

        if (newColumns != k)
        {
            return Error(CellError.Reference);
        }

        items = new EvalValue[newRows];
        for (int row = 0; row < newRows; row++)
        {
            int at = row;
            items[row] = Predict(v => news[(at * k) + v]);
        }

        return EvalValue.FromArray(new EvalArray(newRows, 1, items));
    }
}
