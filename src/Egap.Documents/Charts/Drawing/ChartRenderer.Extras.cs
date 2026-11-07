using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using Egami.Drawing;
using Egami.Fonts.Layout;
using Egami.Pixels;

namespace Egap.Documents.Charts;

/// <content>Data labels, trendlines, error bars and the legend.</content>
internal sealed partial class ChartRenderer
{
    private const float LegendKey = 7;
    private const float LineKey = 22.6f;
    private const float FlatEntryExtra = 2.5f;
    private const float LegendKeyShare = 0.549f;
    private const float LegendGapShare = 0.275f;
    private const float LegendFlatInset = 6.4f;
    private const float LegendSideInset = 1.5f;
    private const float LineKeyInset = 3;
    private const float LineKeyLength = 18.4f;
    private const float FlatLineKeyLength = 26;
    private const float LineKeyGap = 1.5f;
    private const float LegendWrappedHeightShare = 0.5f;
    private const float WordLineKeyExtra = 1.9f;
    private const float KeyMarkerSize = 7;
    private const float FlatLineEntryExtra = 5.1f;
    private const float LegendLines = 1.48f;
    private const float LegendWidthShare = 0.41f;
    private const float LabelWidthShare = 0.17f;

    /// <summary>The data labels of the last draw, with their boxes.</summary>
    internal List<(string Text, RectF Box)> DrawnLabels { get; } = [];

    /// <summary>The ends of the error bars of the last draw.</summary>
    internal List<(Vector2 A, Vector2 B)> DrawnErrorBars { get; } = [];

    /// <summary>A text broken into lines of at most a width: after spaces and hyphens, and inside a word wider than that, as Office does.</summary>
    private string Wrapped(string text, Look look, float limit)
    {
        if (Measure(text, look).Width <= limit)
        {
            return text;
        }

        List<string> lines = [];
        string line = "";
        foreach (Match piece in Pieces().Matches(text))
        {
            string candidate = line + piece.Value;
            if (Measure(candidate.TrimEnd(), look).Width <= limit)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
            {
                lines.Add(line.TrimEnd());
                line = "";
            }

            foreach (char c in piece.Value)
            {
                if (line.Length > 0 && Measure((line + c).TrimEnd(), look).Width > limit)
                {
                    lines.Add(line);
                    line = "";
                }

                line += c;
            }
        }

        if (line.Trim().Length > 0)
        {
            lines.Add(line.TrimEnd());
        }

        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"[^ -]*[ -]+|[^ -]+", RegexOptions.CultureInvariant)]
    private static partial Regex Pieces();

    /// <summary>A wrapped text cut to some lines, the last ending in an ellipsis when something was left out.</summary>
    private string CutTo(string wrapped, int most, Look look, float limit)
    {
        string[] lines = wrapped.Split('\n');
        if (lines.Length <= most)
        {
            return wrapped;
        }

        string last = lines[most - 1].TrimEnd();
        while (last.Length > 1 && Measure(last + "\u2026", look).Width > limit)
        {
            last = last[..^1].TrimEnd();
        }

        return string.Join('\n', lines[..(most - 1)].Append(last + "\u2026"));
    }

    /// <summary>A data label's text: under a fifth of the chart wide, and a little over half of it high, with an ellipsis for what is left out.</summary>
    private string LabelLines(string text, Look look)
    {
        string wrapped = Wrapped(text, look, Math.Max(width * LabelWidthShare, 1));
        int most = Math.Max((int)(height * 0.55f / Math.Max(Measure("0", look).Height, 1)), 1);
        return CutTo(wrapped, most, look, Math.Max(width * LabelWidthShare, 1));
    }

    /// <summary>Whether a label fits inside a slice of a pie, as the best fit wants, with room to spare along the slice's arc.</summary>
    private static bool FitsInSlice(float sweep, float radius, float w, float h) => sweep * radius * 0.7f >= 2 * Math.Max(w, h);

    /// <summary>Text lying centred on its lines from a corner.</summary>
    private void DrawLines(string text, Look look, Vector2 corner, float w)
    {
        if (text.Length > 0)
        {
            canvas.DrawTextLayout(Lay(text, look, w + 1, TextAlign.Center), corner);
        }
    }

    /// <summary>The labels that apply to a series: its own, or its plot's.</summary>
    private static ChartDataLabels LabelsFor(ChartSeries series, ChartPlot plot) => series.HasDataLabels ? series.DataLabels : plot.DataLabels;

    private static bool AnyLabelText(ChartDataLabels labels) =>
        !labels.Deleted && (labels.ShowValue || labels.ShowCategoryName || labels.ShowSeriesName || labels.ShowPercent || labels.ShowBubbleSize || labels.CustomText(_ => null) is not null);

    private string LabelText(ChartDataLabels labels, ChartSeries series, ChartPlot plot, int index)
    {
        // Text of the label's own, with its fields filled in.
        double? own = index < series.Values.Count ? series.Values.Numbers[index] : null;
        if (labels.CustomText(field => field switch
        {
            "VALUE" or "YVALUE" => own is { } v ? Number(v, labels.NumberFormat ?? series.Values.FormatCode) : "",
            "PERCENTAGE" => own is { } v && series.Values.Numbers.Sum(n => n is > 0 ? n.Value : 0) is > 0 and var sum ? Number(v / sum, labels.NumberFormat ?? "0%") : "",
            "SERIESNAME" => SeriesName(series),
            "CATEGORYNAME" or "XVALUE" => series.Categories.IsNumeric
                ? (index < series.Categories.Numbers.Count && series.Categories.Numbers[index] is { } c ? Number(c, series.Categories.FormatCode) : "")
                : index < series.Categories.Text.Count ? series.Categories.Text[index] ?? "" : "",
            _ => null,
        }) is { } custom)
        {
            return custom;
        }

        List<string> parts = [];
        if (labels.ShowSeriesName)
        {
            parts.Add(SeriesName(series));
        }

        if (labels.ShowCategoryName)
        {
            ChartData categories = series.Categories;
            if (categories.IsNumeric)
            {
                parts.Add(index < categories.Numbers.Count && categories.Numbers[index] is { } c ? Number(c, categories.FormatCode) : "");
            }
            else
            {
                parts.Add(index < categories.Text.Count ? categories.Text[index] ?? "" : (index + 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        double? value = index < series.Values.Count ? series.Values.Numbers[index] : null;
        if (labels.ShowValue && value is { } v)
        {
            parts.Add(Number(v, labels.NumberFormat ?? series.Values.FormatCode));
        }

        if (labels.ShowPercent && value is { } share)
        {
            double total = series.Values.Numbers.Sum(n => n is > 0 ? n.Value : 0);
            parts.Add(total > 0 ? Number(share / total, labels.NumberFormat ?? "0%") : "");
        }

        if (labels.ShowBubbleSize && index < series.BubbleSizes.Count && series.BubbleSizes.Numbers[index] is { } size)
        {
            parts.Add(Number(size, labels.NumberFormat ?? series.BubbleSizes.FormatCode));
        }

        string separator = culture.Equals(CultureInfo.InvariantCulture) ? ", " : culture.TextInfo.ListSeparator + " ";
        return string.Join(labels.Separator ?? (labels.ShowPercent && plot.Type is ChartType.Pie or ChartType.Doughnut ? "; " : separator), parts);
    }

    private void DrawDataLabels(ChartBoxes boxes)
    {
        foreach (ChartPlot plot in plots)
        {
            foreach (ChartSeries series in Ordered(plot))
            {
                IReadOnlyDictionary<int, ChartDataLabels> own = series.PointLabels;
                ChartDataLabels shared = LabelsFor(series, plot);
                if (!AnyLabelText(shared) && own.Count == 0)
                {
                    continue;
                }

                for (int i = 0; i < series.Values.Count; i++)
                {
                    // Only what the plot drew gets a label: a pie draws one series, and a point past its axis's end draws nothing.
                    ChartDataLabels labels = own.TryGetValue(i, out ChartDataLabels? point) ? point : shared;
                    if (!AnyLabelText(labels) || !marks.TryGetValue((series, i), out Mark mark))
                    {
                        continue;
                    }

                    string text = LabelText(labels, series, plot, i);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    Look look = LookOf(labels.TextFormat.Size is null && shared.TextFormat.Size is not null ? shared.TextFormat : labels.TextFormat, baseSize);
                    text = LabelLines(text, look);
                    (float w, float h) = Measure(text, look);
                    if (LabelPlace(mark, plot, boxes.Plot, labels.Position, w, h) is { } at)
                    {
                        // A label stays inside the chart.
                        RectF box = new(Math.Clamp(at.X - (w / 2), 0, Math.Max(width - w, 0)), Math.Clamp(at.Y - (h / 2), 0, Math.Max(height - h, 0)), w, h);
                        if (Fill(labels.Format, box, NoColor) is { } fill)
                        {
                            canvas.FillRect(box, fill);
                        }

                        DrawLines(text, look, new Vector2(box.X, box.Y), w);
                        DrawnLabels.Add((text, box));
                    }
                }
            }
        }
    }

    /// <summary>The middle of a label on what its plot drew; null when the point lies off the plot.</summary>
    private static Vector2? LabelPlace(Mark mark, ChartPlot plot, RectF area, ChartLabelPosition? position, float w, float h)
    {
        const float Gap = 3;
        switch (mark.Kind)
        {
            case MarkKind.Slice:
                {
                    Vector2 unit = new((float)Math.Cos(mark.Middle), (float)Math.Sin(mark.Middle));
                    if (position is null or ChartLabelPosition.BestFit && FitsInSlice(mark.Sweep, mark.Radius, w, h))
                    {
                        return mark.At + (unit * mark.Radius * 0.65f);
                    }

                    return position switch
                    {
                        ChartLabelPosition.Center => mark.At + (unit * mark.Radius * 0.5f),
                        ChartLabelPosition.InsideEnd => mark.At + (unit * mark.Radius * 0.8f),
                        _ => mark.At + (unit * (mark.Radius + 4)) + new Vector2(unit.X * w / 2, unit.Y * h / 2),
                    };
                }

            case MarkKind.Ring:
                return mark.At + (new Vector2((float)Math.Cos(mark.Middle), (float)Math.Sin(mark.Middle)) * (mark.Inner + mark.Radius) / 2);
            case MarkKind.Radar:
                {
                    // Outward along its spoke.
                    Vector2 out_ = mark.At - mark.Center;
                    Vector2 unit = out_.LengthSquared() > 0.01f ? Vector2.Normalize(out_) : new Vector2(0, -1);
                    return mark.At + new Vector2(unit.X * ((w / 2) + Gap), unit.Y * ((h / 2) + Gap));
                }

            case MarkKind.Area:
                {
                    float middle = (mark.Start + mark.End) / 2;
                    return mark.Across ? new Vector2(mark.Along, middle) : new Vector2(middle, mark.Along);
                }

            case MarkKind.Bar:
                {
                    bool stacked = plot.Grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
                    const float OutsideGap = 5;
                    float towards = mark.End >= mark.Start ? 1 : -1;
                    float thick = mark.Across ? h : w;
                    float where = (position ?? (stacked ? ChartLabelPosition.Center : ChartLabelPosition.OutsideEnd)) switch
                    {
                        ChartLabelPosition.Center => (mark.Start + mark.End) / 2,
                        ChartLabelPosition.InsideEnd => mark.End - (towards * ((thick / 2) + Gap)),
                        ChartLabelPosition.InsideBase => mark.Start + (towards * ((thick / 2) + Gap)),
                        ChartLabelPosition.OutsideEnd => mark.End + (towards * ((thick / 2) + OutsideGap)),
                        _ => mark.End,
                    };
                    return mark.Across ? new Vector2(mark.Along, where) : new Vector2(where, mark.Along);
                }

            default:
                {
                    if (!OnPlot(mark.At, area))
                    {
                        return null;
                    }

                    // Beside a point, the label stands clear of a marker; a bubble's starts from its edge.
                    const float Beside = 8;
                    float r = mark.Radius;
                    return position switch
                    {
                        ChartLabelPosition.Left => mark.At - new Vector2((w / 2) + Beside + r, 0),
                        ChartLabelPosition.Top => mark.At - new Vector2(0, (h / 2) + Gap + r),
                        ChartLabelPosition.Bottom => mark.At + new Vector2(0, (h / 2) + Gap + r),
                        ChartLabelPosition.Center => mark.At,
                        _ => mark.At + new Vector2((w / 2) + Beside + r, 0),
                    };
                }
        }
    }

    private void DrawTrendlines(ChartSeries series, DrawnAxis across, DrawnAxis up, RectF area)
    {
        foreach (ChartTrendline trend in series.Trendlines)
        {
            if (Curve(trend, series, across, up, area) is { Count: > 1 } points)
            {
                if (LineOf(trend.Format, palette.Dark, 0.75f) is { } line)
                {
                    canvas.Save();
                    canvas.ClipRect(area);
                    canvas.StrokePath(Egami.Drawing.Path.Polygon(points.ToArray(), close: false), line.Stroke, line.Color);
                    canvas.Restore();
                }
            }
        }
    }

    private static List<Vector2>? Curve(ChartTrendline trend, ChartSeries series, DrawnAxis across, DrawnAxis up, RectF area)
    {
        bool xy = !across.IsCategories;
        IReadOnlyList<double?> ys = series.Values.Numbers;
        IReadOnlyList<double?> xs = xy && series.Categories.IsNumeric ? series.Categories.Numbers : [];
        double[] places = xy ? [] : across.Places;
        List<(double X, double Y)> data = [];
        for (int i = 0; i < ys.Count; i++)
        {
            if (ys[i] is { } y && double.IsFinite(y))
            {
                double x = i < xs.Count && xs[i] is { } given ? given : i < places.Length ? places[i] + 1 : i + 1;
                if (double.IsFinite(x))
                {
                    data.Add((x, y));
                }
            }
        }

        if (data.Count < 2)
        {
            return null;
        }

        ChartTrendlineType type = trend.Type ?? ChartTrendlineType.Linear;
        Vector2 Place(double x, double y)
        {
            float along = xy ? across.Value(x, area) : across.At((across.Between ? x - 0.5 : x - 1) / Math.Max(across.Between ? across.Count : across.Count - 1, 1), area);
            float value = up.Value(y, area);
            return across.Horizontal ? new Vector2(along, value) : new Vector2(value, along);
        }

        if (type == ChartTrendlineType.MovingAverage)
        {
            int period = Math.Clamp(trend.Period ?? 2, 2, Math.Max(data.Count, 2));
            List<Vector2> moving = [];
            for (int i = period - 1; i < data.Count; i++)
            {
                double mean = 0;
                for (int k = 0; k < period; k++)
                {
                    mean += data[i - k].Y;
                }

                moving.Add(Place(data[i].X, mean / period));
            }

            return moving;
        }

        Func<double, double>? fit = Fit(type, trend.Order ?? 2, data, trend.Intercept);
        if (fit is null)
        {
            return null;
        }

        double low = data.Min(p => p.X) - (trend.Backward is >= 0 ? trend.Backward.Value : 0);
        double high = data.Max(p => p.X) + (trend.Forward is >= 0 ? trend.Forward.Value : 0);
        List<Vector2> points = [];
        for (int i = 0; i <= 64; i++)
        {
            double x = low + ((high - low) * i / 64);
            double y = fit(x);
            if (double.IsFinite(y))
            {
                points.Add(Place(x, y));
            }
        }

        return points;
    }

    /// <summary>The least-squares curve of a kind through the data; null when the data cannot give one, such as a logarithm of a negative.</summary>
    private static Func<double, double>? Fit(ChartTrendlineType type, int order, List<(double X, double Y)> data, double? intercept)
    {
        switch (type)
        {
            case ChartTrendlineType.Linear:
                {
                    (double a, double b) = Line(data, intercept);
                    return x => a + (b * x);
                }

            case ChartTrendlineType.Logarithmic when data.All(p => p.X > 0):
                {
                    (double a, double b) = Line([.. data.Select(p => (Math.Log(p.X), p.Y))], null);
                    return x => a + (b * Math.Log(x));
                }

            case ChartTrendlineType.Exponential when data.All(p => p.Y > 0):
                {
                    (double a, double b) = Line([.. data.Select(p => (p.X, Math.Log(p.Y)))], null);
                    return x => Math.Exp(a + (b * x));
                }

            case ChartTrendlineType.Power when data.All(p => p.X > 0 && p.Y > 0):
                {
                    (double a, double b) = Line([.. data.Select(p => (Math.Log(p.X), Math.Log(p.Y)))], null);
                    return x => Math.Exp(a) * Math.Pow(x, b);
                }

            case ChartTrendlineType.Polynomial:
                return Polynomial(data, Math.Clamp(order, 2, 6));
            default:
                return null;
        }
    }

    private static (double A, double B) Line(List<(double X, double Y)> data, double? intercept)
    {
        double n = data.Count;
        double sx = data.Sum(p => p.X);
        double sy = data.Sum(p => p.Y);
        double sxx = data.Sum(p => p.X * p.X);
        double sxy = data.Sum(p => p.X * p.Y);
        if (intercept is { } set)
        {
            return (set, sxx != 0 ? (sxy - (set * sx)) / sxx : 0);
        }

        double d = (n * sxx) - (sx * sx);
        double b = d != 0 ? ((n * sxy) - (sx * sy)) / d : 0;
        return ((sy - (b * sx)) / n, b);
    }

    private static Func<double, double>? Polynomial(List<(double X, double Y)> data, int order)
    {
        int size = order + 1;
        double[,] m = new double[size, size + 1];
        foreach ((double x, double y) in data)
        {
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    m[r, c] += Math.Pow(x, r + c);
                }

                m[r, size] += y * Math.Pow(x, r);
            }
        }

        for (int i = 0; i < size; i++)
        {
            int pivot = i;
            for (int r = i + 1; r < size; r++)
            {
                if (Math.Abs(m[r, i]) > Math.Abs(m[pivot, i]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[pivot, i]) < 1e-12)
            {
                return null;
            }

            for (int c = 0; c <= size; c++)
            {
                (m[i, c], m[pivot, c]) = (m[pivot, c], m[i, c]);
            }

            for (int r = 0; r < size; r++)
            {
                if (r != i)
                {
                    double f = m[r, i] / m[i, i];
                    for (int c = i; c <= size; c++)
                    {
                        m[r, c] -= f * m[i, c];
                    }
                }
            }
        }

        double[] terms = new double[size];
        for (int i = 0; i < size; i++)
        {
            terms[i] = m[i, size] / m[i, i];
        }

        return x =>
        {
            double sum = 0;
            for (int i = size - 1; i >= 0; i--)
            {
                sum = (sum * x) + terms[i];
            }

            return sum;
        };
    }

    /// <summary>The ends of a point's error bars along one axis, in the point's own units: nothing when the series has none.</summary>
    private static IEnumerable<(double Low, double High)> ErrorRange(ChartSeries series, int index, double value, IReadOnlyList<double?> values, bool xy, bool alongX)
    {
        foreach (ChartErrorBars bars in series.ErrorBars)
        {
            if ((xy && bars.Direction == ChartErrorBarDirection.X) == alongX)
            {
                yield return ErrorEnds(bars, values, index, value);
            }
        }
    }

    /// <summary>The ends of a point's error bar. A standard deviation's stand around the series's mean, the same for every point, as Excel draws them.</summary>
    private static (double Low, double High) ErrorEnds(ChartErrorBars bars, IReadOnlyList<double?> values, int index, double value)
    {
        (double down, double up) = ErrorAmounts(bars, values, index, value);
        double middle = bars.ValueType == ChartErrorBarValue.StandardDeviation && Finite(values).Count > 0 ? Finite(values).Average() : value;
        return (middle - down, middle + up);
    }

    private static List<double> Finite(IReadOnlyList<double?> values) => [.. values.Where(v => v is { } n && double.IsFinite(n)).Select(v => v!.Value)];

    private static (double Down, double Up) ErrorAmounts(ChartErrorBars bars, IReadOnlyList<double?> values, int index, double value)
    {
        double amount = bars.Value ?? 1;
        (double down, double up) = bars.ValueType switch
        {
            ChartErrorBarValue.Percentage => (Math.Abs(value) * amount / 100, Math.Abs(value) * amount / 100),
            ChartErrorBarValue.StandardDeviation => (Deviation(values) * amount, Deviation(values) * amount),
            ChartErrorBarValue.StandardError => (Error(values), Error(values)),
            ChartErrorBarValue.Custom => (
                index < bars.Minus.Count ? bars.Minus.Numbers[index] ?? 0 : 0,
                index < bars.Plus.Count ? bars.Plus.Numbers[index] ?? 0 : 0),
            _ => (amount, amount),
        };
        return bars.Sides switch
        {
            ChartErrorBarSides.Plus => (0, up),
            ChartErrorBarSides.Minus => (down, 0),
            _ => (down, up),
        };
    }

    private static double Deviation(IReadOnlyList<double?> values)
    {
        List<double> numbers = Finite(values);
        if (numbers.Count < 2)
        {
            return 0;
        }

        double mean = numbers.Average();
        return Math.Sqrt(numbers.Sum(n => (n - mean) * (n - mean)) / (numbers.Count - 1));
    }

    private static double Error(IReadOnlyList<double?> values)
    {
        int count = Finite(values).Count;
        return count < 2 ? 0 : Deviation(values) / Math.Sqrt(count);
    }

    private void DrawErrorBars(ChartSeries series, DrawnAxis across, DrawnAxis up, RectF area)
    {
        IReadOnlyList<ChartErrorBars> all = series.ErrorBars;
        if (all.Count == 0)
        {
            return;
        }

        const float Cap = 2.5f;
        bool xy = !across.IsCategories;
        IReadOnlyList<double?> ys = series.Values.Numbers;
        IReadOnlyList<double?> xs = xy && series.Categories.IsNumeric ? series.Categories.Numbers : [];
        canvas.Save();
        canvas.ClipRect(area);
        foreach (ChartErrorBars bars in all)
        {
            if (LineOf(bars.Format, palette.Dark, 0.75f) is not { } line)
            {
                continue;
            }

            bool alongX = bars.Direction == ChartErrorBarDirection.X && xy;
            for (int i = 0; i < ys.Count; i++)
            {
                if (ys[i] is not { } y || !double.IsFinite(y) || !marks.TryGetValue((series, i), out Mark mark))
                {
                    continue;
                }

                if (alongX)
                {
                    double x = i < xs.Count && xs[i] is { } given ? given : i + 1;
                    (double from, double to) = ErrorEnds(bars, xs, i, x);
                    float level = up.Value(mark.Level, area);
                    Segment(new Vector2(across.Value(from, area), level), new Vector2(across.Value(to, area), level), true);
                }
                else
                {
                    // From the mark's own end: the top of its stack, and as a share where the plot stacks to shares.
                    (double low, double high) = ErrorEnds(bars, ys, i, y);
                    float a = up.Value(mark.Level + ((low - y) * mark.Unit), area);
                    float b = up.Value(mark.Level + ((high - y) * mark.Unit), area);
                    Segment(Point(across, mark.Along, a), Point(across, mark.Along, b), !across.Horizontal);
                }
            }

            void Segment(Vector2 a, Vector2 b, bool flat)
            {
                DrawnErrorBars.Add((a, b));
                canvas.DrawLine(a, b, line.Stroke, line.Color);
                if (!bars.NoEndCap)
                {
                    Vector2 cap = flat ? new Vector2(0, Cap) : new Vector2(Cap, 0);
                    canvas.DrawLine(a - cap, a + cap, line.Stroke, line.Color);
                    canvas.DrawLine(b - cap, b + cap, line.Stroke, line.Color);
                }
            }
        }

        canvas.Restore();
    }

    /// <summary>One entry of a legend.</summary>
    private sealed record Entry(string Text, ChartFormat Format, Rgba8 Color, bool IsLine, ChartSeries? Series, bool Markers, bool Round = false);

    /// <summary>The legend's entries in the order they draw.</summary>
    internal List<string> LegendTexts() => [.. LegendEntries().Select(e => e.Text)];

    private List<Entry> LegendEntries()
    {
        List<Entry> entries = [];
        if (chart.IsExtended)
        {
            return entries;
        }

        List<Entry> trends = [];
        bool perPointLegend = false;
        foreach (ChartPlot plot in plots)
        {
            List<ChartSeries> series = Ordered(plot);
            bool perPoint = plot.Type is ChartType.Pie or ChartType.Doughnut or ChartType.PieOfPie or ChartType.BarOfPie && plot.VaryColors != false
                || plot.VaryColors == true && series.Count == 1;
            if (perPoint && series.Count > 0)
            {
                perPointLegend = true;
                ChartSeries s = series[0];
                int count = Math.Min(Math.Max(s.Values.Count, s.Categories.Count), 512);
                for (int i = 0; i < count; i++)
                {
                    ChartPoint? point = PointOf(s, i);
                    string text = s.Categories.IsNumeric
                        ? (i < s.Categories.Numbers.Count && s.Categories.Numbers[i] is { } n ? Number(n, s.Categories.FormatCode) : (i + 1).ToString(CultureInfo.InvariantCulture))
                        : i < s.Categories.Text.Count ? s.Categories.Text[i] ?? "" : (i + 1).ToString(CultureInfo.InvariantCulture);
                    entries.Add(new Entry(text, point is not null && Has(point.Format) ? point.Format : s.Format, PointColor(i, count), false, null, false));
                }

                continue;
            }

            bool lines = plot.Type is ChartType.Line or ChartType.Stock or ChartType.Scatter || (plot.Type == ChartType.Radar && plot.RadarStyle != ChartRadarStyle.Filled);
            foreach (ChartSeries s in series)
            {
                bool wide = lines && s.Format.LineKind != ChartLineKind.None && plot.ScatterStyle is not (ChartScatterStyle.Marker or ChartScatterStyle.None);
                entries.Add(new Entry(SeriesName(s), s.Format, SeriesColor(s), wide, lines ? s : null, MarkersOn(plot), plot.Type == ChartType.Bubble));
                trends.AddRange(s.Trendlines.Select(trend => new Entry(trend.Name ?? TrendName(trend, s), trend.Format, palette.Dark, true, null, false)));
            }
        }

        // Trendlines come after every series.
        entries.AddRange(trends);

        IReadOnlyList<int> deleted = chart.HasLegend ? chart.Legend.DeletedEntries : [];
        entries = deleted.Count == 0 ? entries : [.. entries.Where((_, i) => !deleted.Contains(i))];

        // A legend at the side reads as the stack does, top series first; a clustered bar chart's reads as its bars do, wherever it is,
        // and a reversed axis puts the first series on top.
        bool side = chart.HasLegend && chart.Legend.Position is null or ChartLegendPosition.Right or ChartLegendPosition.Left or ChartLegendPosition.TopRight;
        bool stacked = plots.Count == 1 && plots[0].Grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
        if (chart.HasLegend && plots.Count == 1 && !perPointLegend
            && ((plots[0].Type == ChartType.Bar && (stacked ? side : !plots[0].Axes.Any(a => a.Kind is ChartAxisKind.Category or ChartAxisKind.Date && a.Reversed)))
                || (plots[0].Type is ChartType.Column or ChartType.Line or ChartType.Area && stacked && side)))
        {
            entries.Reverse();
        }

        return entries;
    }

    private static bool MarkersOn(ChartPlot plot) => plot.Type switch
    {
        ChartType.Scatter => plot.ScatterStyle is not (ChartScatterStyle.Line or ChartScatterStyle.Smooth),
        ChartType.Radar => plot.RadarStyle == ChartRadarStyle.Marker,
        _ => plot.ShowMarkers != false,
    };

    private static string TrendName(ChartTrendline trend, ChartSeries series)
    {
        string kind = trend.Type switch
        {
            ChartTrendlineType.Exponential => "Expon.",
            ChartTrendlineType.Logarithmic => "Log.",
            ChartTrendlineType.Polynomial => "Poly.",
            ChartTrendlineType.Power => "Power",
            ChartTrendlineType.MovingAverage => (trend.Period ?? 2).ToString(CultureInfo.InvariantCulture) + " per. Mov. Avg.",
            _ => "Linear",
        };
        return kind + " (" + SeriesName(series) + ")";
    }

    private Look LegendLook() => LookOf(chart.Legend.TextFormat, baseSize);

    private static float KeyWidth(List<Entry> entries) => entries.Any(e => e.IsLine) ? LineKey : LegendKey;

    private (float Width, float Height) LegendSize(List<Entry> entries, ChartLegendPosition position, float room, float tall)
    {
        Look look = LegendLook();
        float line = Measure("0", look).Height;
        float step = line * LegendLines;
        float key = KeyWidth(entries);
        float[] widths = [.. entries.Select(e => Measure(e.Text, look).Width + key + LegendPad(key))];
        if (position is ChartLegendPosition.Top or ChartLegendPosition.Bottom)
        {
            if (word && key <= LegendKey && widths.Sum() <= room - 12)
            {
                return (widths.Sum(), step);
            }

            float each = widths.Max() + (key > LegendKey ? FlatLineEntryExtra : FlatEntryExtra);
            int across = Math.Max(1, (int)Math.Floor((room - 12) / Math.Max(each, 1)));
            int rows = (int)Math.Ceiling(entries.Count / (double)Math.Max(across, 1));
            return (Math.Min(each * Math.Min(across, entries.Count), room - 12), rows * step);
        }

        float full = widths.Max();
        float most = width * LegendWidthShare;
        if (full > most)
        {
            List<string> wrapped = [.. entries.Select(e => WrappedEntry(e, look, key))];
            float tallestEntries = 0;
            foreach (string entry in wrapped)
            {
                float next = Measure(entry, look).Height + step - line;
                if (tallestEntries > 0 && tallestEntries + next > Math.Min(tall, height * LegendWrappedHeightShare))
                {
                    break;
                }

                tallestEntries += next;
            }

            return (wrapped.Max(t => Measure(t, look).Width) + key + LegendPad(key), tallestEntries);
        }

        int perColumn = Math.Max(1, (int)Math.Floor((tall - 6) / step));
        int columns = (int)Math.Ceiling(entries.Count / (double)perColumn);
        return (Math.Min(full * columns, room * 0.5f), Math.Min(entries.Count, perColumn) * step);
    }

    private string WrappedEntry(Entry entry, Look look, float key) => Wrapped(entry.Text, look, Math.Max((width * LegendWidthShare) - key - LegendPad(key) - 4, 1));

    private static float LegendPad(float key) => key > LegendKey ? 7.4f : 10f;

    private void DrawLegend(ChartBoxes boxes)
    {
        if (boxes.Legend is not { } box || !chart.HasLegend)
        {
            return;
        }

        ChartFormat format = chart.Legend.Format;
        if (Fill(format, box, NoColor) is { } fill)
        {
            canvas.FillRect(box, fill);
        }

        if (LineOf(format, NoColor, 0.75f) is { } frame)
        {
            canvas.StrokeRect(box, frame.Stroke, frame.Color);
        }

        List<Entry> entries = LegendEntries();
        if (entries.Count == 0)
        {
            return;
        }

        Look look = LegendLook();
        float line = Measure("0", look).Height;
        float step = line * LegendLines;
        float key = KeyWidth(entries);
        ChartLegendPosition position = chart.Legend.Position ?? ChartLegendPosition.Right;
        float each = entries.Select(e => Measure(e.Text, look).Width + key + LegendPad(key)).Max();
        bool flat = position is ChartLegendPosition.Top or ChartLegendPosition.Bottom;
        if (flat)
        {
            each += key > LegendKey ? FlatLineEntryExtra : FlatEntryExtra;
        }

        int across = Math.Max(1, (int)Math.Floor((box.Width + 0.5f) / Math.Max(each, 1)));
        int rows = Math.Max(1, (int)Math.Floor((box.Height + 0.5f) / step));
        float[]? own = null;
        if (word && flat && key <= LegendKey && chart.Legend.Layout.Width is not > 0)
        {
            float[] widths = [.. entries.Select(e => Measure(e.Text, look).Width + key + LegendPad(key))];
            if (widths.Sum() <= box.Width + 0.5f)
            {
                own = widths;
                across = entries.Count;
            }
        }

        // Sized by hand it fills its box: across when the entries fit on the rows it has, and evenly.
        if (chart.Legend.Layout.Width is > 0 && chart.Legend.Layout.Height is > 0)
        {
            flat = across >= entries.Count || rows < entries.Count;
            if (flat)
            {
                int used = Math.Min(across, entries.Count);
                each = box.Width / used;
                int needed = (int)Math.Ceiling(entries.Count / (double)used);
                step = box.Height / needed;
            }
            else
            {
                step = box.Height / entries.Count;
            }
        }

        canvas.Save();
        float longer = word && !flat && key > LegendKey ? WordLineKeyExtra : 0;
        canvas.ClipRect(box with { Width = box.Width + longer });
        bool byHand = chart.Legend.Layout.Width is > 0 && chart.Legend.Layout.Height is > 0;
        float keySize = look.Size * LegendKeyShare;
        float keyGap = look.Size * LegendGapShare;
        float origin = flat ? LegendFlatInset : LegendSideInset;
        bool wide = key > LegendKey;
        float length = (flat ? FlatLineKeyLength : LineKeyLength) + longer;
        if (!flat && !byHand && each > width * LegendWidthShare)
        {
            // Too wide for its share of a narrow chart: each entry wraps, its key on the first line.
            float down = box.Y;
            foreach (Entry entry in entries)
            {
                string wrapped = WrappedEntry(entry, look, key);
                if (down > box.Y && down + Measure(wrapped, look).Height + step - line > box.Bottom + 0.5f)
                {
                    break;
                }

                float keyTop = down + ((step - keySize) / 2);
                float at = box.X + origin;
                float textAt = wide ? at + LineKeyInset + length + LineKeyGap : at + keyGap + keySize + keyGap;
                DrawKey(entry, wide ? new RectF(at + LineKeyInset, keyTop, length, keySize) : new RectF(at + keyGap, keyTop, keySize, keySize));
                DrawTextAt(wrapped, look, new Vector2(textAt, down + ((step - line) / 2)));
                down += Measure(wrapped, look).Height + step - line;
            }

            canvas.Restore();
            return;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            int column = flat ? i % across : i / rows;
            int row = flat ? i / across : i % rows;
            float x = box.X + origin + (own is null ? column * each : own.Take(column).Sum());
            float y = box.Y + (row * step) + ((step - line) / 2);
            float keyTop = y + ((line - keySize) / 2);
            float textAt;
            if (wide)
            {
                // With a line among them every key is as long as a line's.
                DrawKey(entries[i], new RectF(x + LineKeyInset, keyTop, length, keySize));
                textAt = x + LineKeyInset + length + LineKeyGap;
            }
            else
            {
                DrawKey(entries[i], new RectF(x + keyGap, keyTop, keySize, keySize));
                textAt = x + keyGap + keySize + keyGap;
            }

            DrawTextAt(entries[i].Text, look, new Vector2(textAt, y));
        }

        canvas.Restore();
    }

    private void DrawKey(Entry entry, RectF box)
    {
        if (!entry.IsLine && entry.Series is { } dotted)
        {
            DrawMarker(dotted.Marker, dotted, new Vector2(box.X + (box.Width / 2), box.Y + (box.Height / 2)), entry.Markers, entry.Color, KeyMarkerSize);
            return;
        }

        if (!entry.IsLine)
        {
            if (Fill(entry.Format, box, entry.Color) is { } fill)
            {
                canvas.FillPath(entry.Round ? Egami.Drawing.Path.Oval(box) : Egami.Drawing.Path.Rect(box), fill);
            }

            if (LineOf(entry.Format, NoColor, 0.75f) is { } edge)
            {
                canvas.StrokeRect(box, edge.Stroke, edge.Color);
            }

            return;
        }

        float middle = box.Y + (box.Height / 2);
        if (LineOf(entry.Format, entry.Color, entry.Series is null ? 0.75f : SeriesLineWidth) is { } line)
        {
            canvas.DrawLine(new Vector2(box.Left, middle), new Vector2(box.Right, middle), line.Stroke, line.Color);
        }

        if (entry.Series is { } series)
        {
            DrawMarker(series.Marker, series, new Vector2(box.X + (box.Width / 2), middle), entry.Markers, entry.Color, KeyMarkerSize);
        }
    }
}
