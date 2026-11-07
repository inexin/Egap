using System.Globalization;
using System.Numerics;

using Egami.Drawing;
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
    private const float KeyMarkerSize = 7;
    private const float FlatLineEntryExtra = 5.1f;
    private const float LegendLines = 1.48f;

    /// <summary>The labels that apply to a series: its own, or its plot's.</summary>
    private static ChartDataLabels LabelsFor(ChartSeries series, ChartPlot plot) => series.HasDataLabels ? series.DataLabels : plot.DataLabels;

    private static bool AnyLabelText(ChartDataLabels labels) =>
        !labels.Deleted && (labels.ShowValue || labels.ShowCategoryName || labels.ShowSeriesName || labels.ShowPercent || labels.ShowBubbleSize);

    private string LabelText(ChartDataLabels labels, ChartSeries series, ChartPlot plot, int index)
    {
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

        _ = plot;
        return string.Join(labels.Separator ?? (labels.ShowPercent && plot.Type is ChartType.Pie or ChartType.Doughnut ? "; " : ", "), parts);
    }

    private void DrawDataLabels(ChartBoxes boxes)
    {
        foreach (ChartPlot plot in plots)
        {
            (DrawnAxis Across, DrawnAxis Up)? pair = Pair(boxes, plot);
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
                    ChartDataLabels labels = own.TryGetValue(i, out ChartDataLabels? point) ? point : shared;
                    if (!AnyLabelText(labels) || i >= series.Values.Numbers.Count || series.Values.Numbers[i] is not { } value || !double.IsFinite(value))
                    {
                        continue;
                    }

                    string text = LabelText(labels, series, plot, i);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    Look look = LookOf(labels.TextFormat.Size is null && shared.TextFormat.Size is not null ? shared.TextFormat : labels.TextFormat, baseSize);
                    (float w, float h) = Measure(text, look);
                    Vector2? center = plot.Type switch
                    {
                        ChartType.Pie or ChartType.PieOfPie or ChartType.BarOfPie => PieLabel(plot, series, i, boxes.Plot, labels.Position, w, h),
                        ChartType.Doughnut => RingLabel(plot, series, i, boxes.Plot),
                        ChartType.Radar => null,
                        _ when pair is { } p => CartesianLabel(plot, series, i, value, p.Across, p.Up, boxes.Plot, labels.Position, w, h),
                        _ => null,
                    };
                    if (center is { } at)
                    {
                        RectF box = new(at.X - (w / 2), at.Y - (h / 2), w, h);
                        if (Fill(labels.Format, box, NoColor) is { } fill)
                        {
                            canvas.FillRect(box, fill);
                        }

                        DrawTextAt(text, look, new Vector2(box.X, box.Y));
                    }
                }
            }
        }
    }

    private static Vector2? CartesianLabel(ChartPlot plot, ChartSeries series, int index, double value, DrawnAxis across, DrawnAxis up, RectF area, ChartLabelPosition? position, float w, float h)
    {
        const float Gap = 3;
        bool xy = plot.Type is ChartType.Scatter or ChartType.Bubble;
        IReadOnlyList<double?> xs = xy && series.Categories.IsNumeric ? series.Categories.Numbers : [];
        double x = index < xs.Count && xs[index] is { } gx ? gx : index + 1;
        ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Standard;
        bool stacked = grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
        double from = 0;
        double to = value;
        if (stacked)
        {
            double sum = 0;
            double total = 0;
            foreach (ChartSeries s in Ordered(plot))
            {
                double v = index < s.Values.Count ? Math.Abs(s.Values.Numbers[index] ?? 0) : 0;
                total += v;
            }

            foreach (ChartSeries s in Ordered(plot))
            {
                if (s.Order >= series.Order)
                {
                    break;
                }

                sum += index < s.Values.Count ? s.Values.Numbers[index] ?? 0 : 0;
            }

            double scale = grouping == ChartGrouping.PercentStacked && total > 0 ? 1 / total : 1;
            from = sum * scale;
            to = (sum + value) * scale;
        }

        float along = xy ? across.Value(x, area) : across.Category(index, area);
        if (plot.Type is ChartType.Column or ChartType.Bar && !stacked)
        {
            along += ClusterShift(plot, series, across, area);
        }

        float start = up.Value(from, area);
        float end = up.Value(to, area);
        bool bars = plot.Type is ChartType.Column or ChartType.Bar;
        bool area2 = plot.Type == ChartType.Area;
        ChartLabelPosition place = position ?? (bars ? (stacked ? ChartLabelPosition.Center : ChartLabelPosition.OutsideEnd) : area2 ? ChartLabelPosition.Center : ChartLabelPosition.Right);
        bool sideways = plot.Type == ChartType.Bar;
        float towards = end >= start ? 1 : -1;
        float thick = sideways ? w : h;
        float where = place switch
        {
            ChartLabelPosition.Center => (start + end) / 2,
            ChartLabelPosition.InsideEnd => end - (towards * ((thick / 2) + Gap)),
            ChartLabelPosition.InsideBase => start + (towards * ((thick / 2) + Gap)),
            ChartLabelPosition.OutsideEnd => end + (towards * ((thick / 2) + Gap)),
            _ => end,
        };
        if (!bars)
        {
            Vector2 at = across.Horizontal ? new Vector2(along, end) : new Vector2(end, along);
            return place switch
            {
                ChartLabelPosition.Left => at - new Vector2((w / 2) + Gap + 2, 0),
                ChartLabelPosition.Top => at - new Vector2(0, (h / 2) + Gap),
                ChartLabelPosition.Bottom => at + new Vector2(0, (h / 2) + Gap),
                ChartLabelPosition.Center => area2 ? (across.Horizontal ? new Vector2(along, where) : new Vector2(where, along)) : at,
                _ => at + new Vector2((w / 2) + Gap + 2, 0),
            };
        }

        return sideways ? new Vector2(where, along) : new Vector2(along, where);
    }

    private static float ClusterShift(ChartPlot plot, ChartSeries series, DrawnAxis across, RectF area)
    {
        List<ChartSeries> all = Ordered(plot);
        int slots = all.Count;
        if (slots <= 1)
        {
            return 0;
        }

        double overlap = Math.Clamp((plot.Overlap ?? 0) / 100.0, -1, 1);
        double gap = Math.Clamp((plot.GapWidth ?? 150) / 100.0, 0, 5);
        float bar = (float)(across.Band(area) / (slots - ((slots - 1) * overlap) + gap));
        float total = (float)(bar * (slots - ((slots - 1) * overlap)));
        int slot = all.FindIndex(s => s.Order == series.Order);
        float offset = (float)(slot * bar * (1 - overlap)) + (bar / 2) - (total / 2);
        return plot.Type == ChartType.Bar != across.Model.Reversed ? -offset : offset;
    }

    private static Vector2? PieLabel(ChartPlot plot, ChartSeries series, int index, RectF area, ChartLabelPosition? position, float w, float h)
    {
        IReadOnlyList<double?> values = series.Values.Numbers;
        double total = values.Sum(v => v is > 0 ? v.Value : 0);
        if (!(total > 0) || values[index] is not > 0)
        {
            return null;
        }

        double angle = plot.FirstSliceAngle ?? 0;
        for (int i = 0; i < index; i++)
        {
            angle += values[i] is > 0 ? values[i]!.Value / total * 360 : 0;
        }

        double middle = (angle + (values[index]!.Value / total * 180) - 90) * Math.PI / 180;
        Vector2 center = new(area.X + (area.Width / 2), area.Y + (area.Height / 2));
        float radius = area.Width / 2;
        Vector2 unit = new((float)Math.Cos(middle), (float)Math.Sin(middle));
        switch (position)
        {
            case ChartLabelPosition.Center:
                return center + (unit * radius * 0.5f);
            case ChartLabelPosition.InsideEnd:
                return center + (unit * radius * 0.8f);
            default:
                return center + (unit * (radius + 4)) + new Vector2(unit.X * w / 2, unit.Y * h / 2);
        }
    }

    private static Vector2? RingLabel(ChartPlot plot, ChartSeries series, int index, RectF area)
    {
        IReadOnlyList<double?> values = series.Values.Numbers;
        double total = values.Sum(v => v is > 0 ? v.Value : 0);
        if (!(total > 0) || values[index] is not > 0)
        {
            return null;
        }

        double angle = plot.FirstSliceAngle ?? 0;
        for (int i = 0; i < index; i++)
        {
            angle += values[i] is > 0 ? values[i]!.Value / total * 360 : 0;
        }

        List<ChartSeries> all = Ordered(plot);
        float outer = area.Width / 2;
        float hole = outer * Math.Clamp(plot.HoleSize ?? 10, 0, 90) / 100f;
        float ring = (outer - hole) / all.Count;
        float radius = hole + ((all.FindIndex(s => s.Order == series.Order) + 0.5f) * ring);
        double middle = (angle + (values[index]!.Value / total * 180) - 90) * Math.PI / 180;
        return new Vector2(area.X + outer + ((float)Math.Cos(middle) * radius), area.Y + outer + ((float)Math.Sin(middle) * radius));
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
        List<(double X, double Y)> data = [];
        for (int i = 0; i < ys.Count; i++)
        {
            if (ys[i] is { } y && double.IsFinite(y))
            {
                double x = i < xs.Count && xs[i] is { } given ? given : i + 1;
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

    /// <summary>The ends of a point's Y error bar: nothing when the series has none.</summary>
    private static IEnumerable<(double Down, double Up)> ErrorRange(ChartSeries series, int index, double value)
    {
        foreach (ChartErrorBars bars in series.ErrorBars)
        {
            if (bars.Direction == ChartErrorBarDirection.X)
            {
                continue;
            }

            (double down, double up) = ErrorAmounts(bars, series.Values.Numbers, index, value);
            yield return (value - down, value + up);
        }
    }

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
        List<double> numbers = [.. values.Where(v => v is not null).Select(v => v!.Value)];
        if (numbers.Count < 2)
        {
            return 0;
        }

        double mean = numbers.Average();
        return Math.Sqrt(numbers.Sum(n => (n - mean) * (n - mean)) / (numbers.Count - 1));
    }

    private static double Error(IReadOnlyList<double?> values)
    {
        int count = values.Count(v => v is not null);
        return count < 2 ? 0 : Deviation(values) / Math.Sqrt(count);
    }

    private void DrawErrorBars(ChartSeries series, DrawnAxis across, DrawnAxis up, RectF area, ChartPlot plot)
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
            IReadOnlyList<double?> source = alongX ? xs : ys;
            for (int i = 0; i < ys.Count; i++)
            {
                if (ys[i] is not { } y || !double.IsFinite(y))
                {
                    continue;
                }

                double x = i < xs.Count && xs[i] is { } given ? given : i + 1;
                float along = xy ? across.Value(x, area) : across.Category(i, area);
                if (plot.Type is ChartType.Column or ChartType.Bar && (plot.Grouping ?? ChartGrouping.Clustered) == ChartGrouping.Clustered)
                {
                    along += ClusterShift(plot, series, across, area);
                }

                float level = up.Value(y, area);
                double own = alongX ? x : y;
                (double down, double upward) = ErrorAmounts(bars, source, i, own);
                if (alongX)
                {
                    Segment(new Vector2(across.Value(x - down, area), level), new Vector2(across.Value(x + upward, area), level), true);
                }
                else
                {
                    Vector2 low = across.Horizontal ? new Vector2(along, up.Value(y - down, area)) : new Vector2(up.Value(y - down, area), along);
                    Vector2 high = across.Horizontal ? new Vector2(along, up.Value(y + upward, area)) : new Vector2(up.Value(y + upward, area), along);
                    Segment(low, high, !across.Horizontal);
                }
            }

            void Segment(Vector2 a, Vector2 b, bool flat)
            {
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

    private List<Entry> LegendEntries()
    {
        List<Entry> entries = [];
        if (chart.IsExtended)
        {
            return entries;
        }

        List<Entry> trends = [];
        foreach (ChartPlot plot in plots)
        {
            List<ChartSeries> series = Ordered(plot);
            bool perPoint = plot.Type is ChartType.Pie or ChartType.Doughnut or ChartType.PieOfPie or ChartType.BarOfPie && plot.VaryColors != false
                || plot.VaryColors == true && series.Count == 1;
            if (perPoint && series.Count > 0)
            {
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

        // A legend at the side reads as the stack does, top series first, and as a bar chart's series do.
        if (chart.HasLegend && chart.Legend.Position is null or ChartLegendPosition.Right or ChartLegendPosition.Left or ChartLegendPosition.TopRight
            && plots.Count == 1 && plots[0].Type is ChartType.Column or ChartType.Bar or ChartType.Line or ChartType.Area
            && (plots[0].Type == ChartType.Bar || plots[0].Grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked))
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
            float each = widths.Max() + (key > LegendKey ? FlatLineEntryExtra : FlatEntryExtra);
            int across = Math.Max(1, (int)Math.Floor((room - 12) / Math.Max(each, 1)));
            int rows = (int)Math.Ceiling(entries.Count / (double)Math.Max(across, 1));
            return (Math.Min(each * Math.Min(across, entries.Count), room - 12), rows * step);
        }

        float full = widths.Max();
        int perColumn = Math.Max(1, (int)Math.Floor((tall - 12) / step));
        int columns = (int)Math.Ceiling(entries.Count / (double)perColumn);
        return (Math.Min(full * columns, room * 0.5f), Math.Min(entries.Count, perColumn) * step);
    }

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
        canvas.Save();
        canvas.ClipRect(box);
        float keySize = look.Size * LegendKeyShare;
        float keyGap = look.Size * LegendGapShare;
        float origin = flat ? LegendFlatInset : LegendSideInset;
        bool wide = key > LegendKey;
        float length = flat ? FlatLineKeyLength : LineKeyLength;
        for (int i = 0; i < entries.Count; i++)
        {
            int column = flat ? i % across : i / rows;
            int row = flat ? i / across : i % rows;
            float x = box.X + origin + (column * each);
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
        if (LineOf(entry.Format, entry.Color, SeriesLineWidth) is { } line)
        {
            canvas.DrawLine(new Vector2(box.Left, middle), new Vector2(box.Right, middle), line.Stroke, line.Color);
        }

        if (entry.Series is { } series)
        {
            DrawMarker(series.Marker, series, new Vector2(box.X + (box.Width / 2), middle), entry.Markers, entry.Color, KeyMarkerSize);
        }
    }
}
