using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.Charts;

/// <content>The plots: bars, areas, lines, points, bubbles, radars, pies and rings.</content>
internal sealed partial class ChartRenderer
{
    private const float SeriesLineWidth = 3f;
    private const float RadarLabelLines = 0.824f;
    private const int AutomaticMarkerSize = 9;

    private static readonly ChartMarkerSymbol[] AutomaticMarkers =
    [
        ChartMarkerSymbol.Diamond, ChartMarkerSymbol.Square, ChartMarkerSymbol.Triangle, ChartMarkerSymbol.X, ChartMarkerSymbol.Star,
        ChartMarkerSymbol.Circle, ChartMarkerSymbol.Plus, ChartMarkerSymbol.Dot, ChartMarkerSymbol.Dash,
    ];

    private void DrawPlots(ChartBoxes boxes)
    {
        // Areas go behind bars, and bars behind lines and points, however the file orders them.
        foreach (ChartPlot plot in plots.OrderBy(Depth))
        {
            switch (plot.Type)
            {
                case ChartType.Pie or ChartType.PieOfPie or ChartType.BarOfPie:
                    DrawPie(plot, boxes.Plot);
                    break;
                case ChartType.Doughnut:
                    DrawDoughnut(plot, boxes.Plot);
                    break;
                case ChartType.Radar:
                    DrawRadar(plot, boxes);
                    break;
                case ChartType.Column or ChartType.Bar:
                    if (Pair(boxes, plot) is { } bars)
                    {
                        DrawBars(plot, bars.Across, bars.Up, boxes.Plot);
                    }

                    break;
                case ChartType.Area:
                    if (Pair(boxes, plot) is { } areas)
                    {
                        DrawAreas(plot, areas.Across, areas.Up, boxes.Plot);
                    }

                    break;
                case ChartType.Line or ChartType.Stock:
                    if (Pair(boxes, plot) is { } lines)
                    {
                        DrawLines(plot, lines.Across, lines.Up, boxes.Plot);
                    }

                    break;
                case ChartType.Scatter:
                    if (Pair(boxes, plot) is { } points)
                    {
                        DrawScatter(plot, points.Across, points.Up, boxes.Plot);
                    }

                    break;
                case ChartType.Bubble:
                    if (Pair(boxes, plot) is { } bubbles)
                    {
                        DrawBubbles(plot, bubbles.Across, bubbles.Up, boxes.Plot);
                    }

                    break;
            }
        }

        static int Depth(ChartPlot plot) => plot.Type switch
        {
            ChartType.Area => 0,
            ChartType.Column or ChartType.Bar => 1,
            ChartType.Pie or ChartType.Doughnut => 1,
            _ => 2,
        };
    }

    private static (DrawnAxis Across, DrawnAxis Up)? Pair(ChartBoxes boxes, ChartPlot plot)
    {
        DrawnAxis? up = boxes.Axes.FirstOrDefault(a => !a.IsCategories && a.Plots.Contains(plot) && (a.Cross?.IsCategories == true || !a.Horizontal));
        DrawnAxis? across = up?.Cross;
        return up is null || across is null ? null : (across, up);
    }

    private Rgba8 SeriesColor(ChartSeries series) => palette.Series(series.Index, seriesCount);

    private Rgba8 PointColor(int index, int count) => palette.Series(index, count);

    private static ChartPoint? PointOf(ChartSeries series, int index)
    {
        foreach (ChartPoint point in series.Points)
        {
            if (point.Index == index)
            {
                return point;
            }
        }

        return null;
    }

    private static List<ChartSeries> Ordered(ChartPlot plot) => [.. plot.Series.OrderBy(s => s.Order)];

    private void DrawBars(ChartPlot plot, DrawnAxis across, DrawnAxis up, RectF area)
    {
        List<ChartSeries> series = Ordered(plot);
        if (series.Count == 0 || across.Count == 0)
        {
            return;
        }

        ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Clustered;
        bool stacked = grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
        bool percent = grouping == ChartGrouping.PercentStacked;
        int slots = stacked ? 1 : series.Count;
        double overlap = Math.Clamp((plot.Overlap ?? (stacked ? 100 : 0)) / 100.0, -1, 1);
        double gap = Math.Clamp((plot.GapWidth ?? 150) / 100.0, 0, 5);
        float band = across.Band(area);
        float bar = (float)(band / (slots - ((slots - 1) * overlap) + gap));
        float total = (float)(bar * (slots - ((slots - 1) * overlap)));
        float baseline = LineAt(across, area);
        bool sideways = plot.Type == ChartType.Bar;
        canvas.Save();
        canvas.ClipRect(area);
        int categories = series.Max(s => s.Values.Count);
        double[] positive = new double[categories];
        double[] negative = new double[categories];
        double[] sums = new double[categories];
        if (percent)
        {
            foreach (ChartSeries s in series)
            {
                IReadOnlyList<double?> values = s.Values.Numbers;
                for (int i = 0; i < values.Count; i++)
                {
                    sums[i] += Math.Abs(values[i] ?? 0);
                }
            }
        }

        for (int k = 0; k < series.Count; k++)
        {
            ChartSeries s = series[k];
            IReadOnlyList<double?> values = s.Values.Numbers;
            Rgba8 own = SeriesColor(s);
            int slot = stacked ? 0 : k;
            bool vary = plot.VaryColors == true && series.Count == 1;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not { } raw || !double.IsFinite(raw))
                {
                    continue;
                }

                double value = percent && sums[i] > 0 ? raw / sums[i] : raw;
                double from = 0;
                double to = value;
                if (stacked)
                {
                    if (value >= 0)
                    {
                        from = positive[i];
                        to = from + value;
                        positive[i] = to;
                    }
                    else
                    {
                        from = negative[i];
                        to = from + value;
                        negative[i] = to;
                    }
                }

                float a = stacked ? up.Value(from, area) : baseline;
                float b = up.Value(to, area);
                float center = across.Category(i, area);
                float start = center - (total / 2) + (float)(slot * bar * (1 - overlap));
                if (across.Model.Reversed)
                {
                    start = center + (total / 2) - (float)(slot * bar * (1 - overlap)) - bar;
                }

                if (sideways && !across.Model.Reversed)
                {
                    // A bar chart's categories climb the page, the first series lowest in each.
                    start = center + (total / 2) - (float)(slot * bar * (1 - overlap)) - bar;
                }

                RectF rect = sideways
                    ? RectF.FromEdges(Math.Min(a, b), start, Math.Max(a, b), start + bar)
                    : RectF.FromEdges(start, Math.Min(a, b), start + bar, Math.Max(a, b));
                ChartPoint? point = PointOf(s, i);
                ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
                Rgba8 automatic = vary ? PointColor(i, values.Count) : own;
                bool invert = raw < 0 && (point?.InvertIfNegative ?? s.InvertIfNegative) == true;
                Paint? fill = invert ? new SolidPaint(palette.Light) : Fill(format, rect, automatic);
                if (fill is not null)
                {
                    canvas.FillRect(rect, fill);
                }

                if (LineOf(format, invert ? automatic : NoColor, 0.75f) is { } line)
                {
                    canvas.StrokeRect(rect, line.Stroke, line.Color);
                }
            }
        }

        canvas.Restore();
        foreach (ChartSeries s in series)
        {
            DrawTrendlines(s, across, up, area);
            DrawErrorBars(s, across, up, area, plot);
        }
    }

    private static bool Has(ChartFormat format) => format.FillKind != ChartFillKind.Automatic || format.LineKind != ChartLineKind.Automatic;

    private void DrawAreas(ChartPlot plot, DrawnAxis across, DrawnAxis up, RectF area)
    {
        List<ChartSeries> series = Ordered(plot);
        if (series.Count == 0 || across.Count == 0)
        {
            return;
        }

        ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Standard;
        bool stacked = grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
        bool percent = grouping == ChartGrouping.PercentStacked;
        int count = series.Max(s => s.Values.Count);
        double[] sums = new double[count];
        double[] below = new double[count];
        foreach (ChartSeries s in series)
        {
            IReadOnlyList<double?> values = s.Values.Numbers;
            for (int i = 0; i < values.Count; i++)
            {
                sums[i] += Math.Abs(values[i] ?? 0);
            }
        }

        canvas.Save();
        canvas.ClipRect(area);
        float baseline = LineAt(across, area);
        foreach (ChartSeries s in series)
        {
            IReadOnlyList<double?> values = s.Values.Numbers;
            if (values.Count == 0)
            {
                continue;
            }

            PathBuilder builder = new();
            List<Vector2> top = [];
            List<Vector2> under = [];
            for (int i = 0; i < count; i++)
            {
                double raw = i < values.Count ? values[i] ?? 0 : 0;
                double value = percent && sums[i] > 0 ? raw / sums[i] : raw;
                double from = stacked ? below[i] : 0;
                double to = stacked ? from + value : value;
                below[i] = to;
                float x = across.Category(i, area);
                top.Add(Point(across, x, up.Value(to, area)));
                under.Add(Point(across, x, stacked ? up.Value(from, area) : baseline));
            }

            builder.MoveTo(top[0]);
            foreach (Vector2 p in top.Skip(1))
            {
                builder.LineTo(p);
            }

            for (int i = under.Count - 1; i >= 0; i--)
            {
                builder.LineTo(under[i]);
            }

            builder.Close();
            Path shape = builder.ToPath();
            if (Fill(s.Format, area, SeriesColor(s)) is { } fill)
            {
                canvas.FillPath(shape, fill);
            }

            if (LineOf(s.Format, NoColor, 0.75f) is { } line)
            {
                canvas.StrokePath(shape, line.Stroke, line.Color);
            }
        }

        canvas.Restore();
    }

    private static Vector2 Point(DrawnAxis across, float along, float value) => across.Horizontal ? new Vector2(along, value) : new Vector2(value, along);

    private void DrawLines(ChartPlot plot, DrawnAxis across, DrawnAxis up, RectF area)
    {
        List<ChartSeries> series = Ordered(plot);
        if (series.Count == 0 || across.Count == 0)
        {
            return;
        }

        ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Standard;
        bool stacked = grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
        bool percent = grouping == ChartGrouping.PercentStacked;
        int count = series.Max(s => s.Values.Count);
        double[] sums = new double[count];
        double[] below = new double[count];
        if (percent)
        {
            foreach (ChartSeries s in series)
            {
                IReadOnlyList<double?> values = s.Values.Numbers;
                for (int i = 0; i < values.Count; i++)
                {
                    sums[i] += Math.Abs(values[i] ?? 0);
                }
            }
        }

        ChartBlanks blanks = chart.DisplayBlanksAs ?? ChartBlanks.Gap;
        List<List<Vector2?>> all = [];
        foreach (ChartSeries s in series)
        {
            IReadOnlyList<double?> values = s.Values.Numbers;
            List<Vector2?> points = [];
            for (int i = 0; i < values.Count; i++)
            {
                double? raw = values[i];
                if ((raw is null || !double.IsFinite(raw.Value)) && blanks != ChartBlanks.Zero)
                {
                    points.Add(null);
                    continue;
                }

                double v = raw is { } r && double.IsFinite(r) ? r : 0;
                if (percent && sums[i] > 0)
                {
                    v /= sums[i];
                }

                if (stacked)
                {
                    below[i] += v;
                    v = below[i];
                }

                points.Add(Point(across, across.Category(i, area), up.Value(v, area)));
            }

            all.Add(points);
        }

        DrawHighLow(plot, across, area, all);
        for (int k = 0; k < series.Count; k++)
        {
            ChartSeries s = series[k];
            if (LineOf(s.Format, plot.Type == ChartType.Stock ? NoColor : SeriesColor(s), SeriesLineWidth) is { } line)
            {
                StrokeSeries(all[k], line, s.Smooth == true, blanks == ChartBlanks.Span);
            }

            DrawMarkers(s, all[k], plot.ShowMarkers != false, SeriesColor(s));
            DrawTrendlines(s, across, up, area);
            DrawErrorBars(s, across, up, area, plot);
        }

        if (plot.HasDropLines && LineOf(plot.DropLinesFormat, palette.AutomaticLine, 0.75f) is { } drop)
        {
            float floor = LineAt(across, area);
            for (int i = 0; i < count; i++)
            {
                Vector2? top = all.Select(p => i < p.Count ? p[i] : null).Where(p => p is not null).OrderBy(p => across.Horizontal ? p!.Value.Y : -p!.Value.X).FirstOrDefault();
                if (top is { } t)
                {
                    canvas.DrawLine(t, across.Horizontal ? new Vector2(t.X, floor) : new Vector2(floor, t.Y), drop.Stroke, drop.Color);
                }
            }
        }
    }

    private void DrawHighLow(ChartPlot plot, DrawnAxis across, RectF area, List<List<Vector2?>> all)
    {
        int count = all.Count == 0 ? 0 : all.Max(p => p.Count);
        float bar = (float)(across.Band(area) / (1 + (Math.Clamp(plot.UpDownGapWidth ?? 150, 0, 500) / 100.0)));
        for (int i = 0; i < count; i++)
        {
            List<Vector2> here = [.. all.Where(p => i < p.Count && p[i] is not null).Select(p => p[i]!.Value)];
            if (here.Count < 2)
            {
                continue;
            }

            if (plot.HasHighLowLines && LineOf(plot.HighLowFormat, palette.AutomaticLine, 0.75f) is { } line)
            {
                canvas.DrawLine(new Vector2(here[0].X, here.Min(p => p.Y)), new Vector2(here[0].X, here.Max(p => p.Y)), line.Stroke, line.Color);
            }

            if (plot.HasUpDownBars)
            {
                Vector2 open = all[0].Count > i && all[0][i] is { } o ? o : here[0];
                Vector2 close = all[^1].Count > i && all[^1][i] is { } c ? c : here[^1];
                bool rising = close.Y <= open.Y;
                ChartFormat format = rising ? plot.UpBarsFormat : plot.DownBarsFormat;
                RectF rect = RectF.FromEdges(open.X - (bar / 2), Math.Min(open.Y, close.Y), open.X + (bar / 2), Math.Max(open.Y, close.Y));
                if (Fill(format, rect, rising ? palette.Light : palette.Dark) is { } fill)
                {
                    canvas.FillRect(rect, fill);
                }

                if (LineOf(format, palette.AutomaticLine, 0.75f) is { } edge)
                {
                    canvas.StrokeRect(rect, edge.Stroke, edge.Color);
                }
            }
        }
    }

    private void StrokeSeries(List<Vector2?> points, (Stroke Stroke, Rgba8 Color) line, bool smooth, bool span)
    {
        List<Vector2> run = [];
        foreach (Vector2? point in points)
        {
            if (point is { } p)
            {
                run.Add(p);
            }
            else if (!span)
            {
                Flush();
            }
        }

        Flush();

        void Flush()
        {
            if (run.Count > 1)
            {
                Stroke stroke = line.Stroke with { Cap = LineCap.Round, Join = LineJoin.Round };
                canvas.StrokePath(smooth ? Smooth(run) : Path.Polygon(run.ToArray(), close: false), stroke, line.Color);
            }

            run.Clear();
        }
    }

    /// <summary>A curve through the points, with the tangent at each from its neighbours.</summary>
    private static Path Smooth(List<Vector2> points)
    {
        PathBuilder builder = new();
        builder.MoveTo(points[0]);
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 before = points[Math.Max(i - 1, 0)];
            Vector2 from = points[i];
            Vector2 to = points[i + 1];
            Vector2 after = points[Math.Min(i + 2, points.Count - 1)];
            builder.CubicTo(from + ((to - before) / 6), to - ((after - from) / 6), to);
        }

        return builder.ToPath();
    }

    private void DrawMarkers(ChartSeries series, List<Vector2?> points, bool plotShows, Rgba8 automatic)
    {
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i] is not { } p)
            {
                continue;
            }

            ChartPoint? own = PointOf(series, i);
            ChartMarker marker = own is not null && own.Marker.Symbol is not null ? own.Marker : series.Marker;
            DrawMarker(marker, series, p, plotShows, automatic);
        }
    }

    private void DrawMarker(ChartMarker marker, ChartSeries series, Vector2 at, bool automaticShows, Rgba8 automatic, float? keySize = null)
    {
        ChartMarkerSymbol symbol = marker.Symbol ?? ChartMarkerSymbol.Automatic;
        if (symbol == ChartMarkerSymbol.Automatic)
        {
            if (!automaticShows)
            {
                return;
            }

            symbol = AutomaticMarkers[Math.Max(series.Index, 0) % AutomaticMarkers.Length];
        }

        if (symbol is ChartMarkerSymbol.None or ChartMarkerSymbol.Picture)
        {
            return;
        }

        float size = Math.Clamp(marker.Size ?? AutomaticMarkerSize, 2, keySize ?? 72);
        float r = size / 2;
        Path? closed = symbol switch
        {
            ChartMarkerSymbol.Square => Path.Rect(new RectF(at.X - r, at.Y - r, size, size)),
            ChartMarkerSymbol.Circle => Path.Circle(at, r),
            ChartMarkerSymbol.Diamond => Path.Polygon([new(at.X, at.Y - r), new(at.X + r, at.Y), new(at.X, at.Y + r), new(at.X - r, at.Y)]),
            ChartMarkerSymbol.Triangle => Path.Polygon([new(at.X, at.Y - r), new(at.X + r, at.Y + r), new(at.X - r, at.Y + r)]),
            ChartMarkerSymbol.Dash => Path.Rect(new RectF(at.X - r, at.Y - (r / 4), size, r / 2)),
            ChartMarkerSymbol.Dot => Path.Rect(new RectF(at.X - (r / 2), at.Y - (r / 4), r, r / 2)),
            _ => null,
        };
        ChartFormat format = marker.Format;
        if (closed is not null)
        {
            if (Fill(format, closed.Bounds, automatic) is { } fill)
            {
                canvas.FillPath(closed, fill);
            }

            if (LineOf(format, automatic, 0.75f) is { } edge)
            {
                canvas.StrokePath(closed, edge.Stroke, edge.Color);
            }

            return;
        }

        (Stroke Stroke, Rgba8 Color)? ink = LineOf(format, automatic, 0.75f);
        if (ink is not { } line)
        {
            return;
        }

        if (symbol is ChartMarkerSymbol.Plus or ChartMarkerSymbol.Star)
        {
            canvas.DrawLine(new Vector2(at.X - r, at.Y), new Vector2(at.X + r, at.Y), line.Stroke, line.Color);
            canvas.DrawLine(new Vector2(at.X, at.Y - r), new Vector2(at.X, at.Y + r), line.Stroke, line.Color);
        }

        if (symbol is ChartMarkerSymbol.X or ChartMarkerSymbol.Star)
        {
            canvas.DrawLine(new Vector2(at.X - r, at.Y - r), new Vector2(at.X + r, at.Y + r), line.Stroke, line.Color);
            canvas.DrawLine(new Vector2(at.X - r, at.Y + r), new Vector2(at.X + r, at.Y - r), line.Stroke, line.Color);
        }
    }

    private void DrawScatter(ChartPlot plot, DrawnAxis across, DrawnAxis up, RectF area)
    {
        ChartScatterStyle style = plot.ScatterStyle ?? ChartScatterStyle.Marker;
        ChartBlanks blanks = chart.DisplayBlanksAs ?? ChartBlanks.Gap;
        foreach (ChartSeries s in Ordered(plot))
        {
            List<Vector2?> points = [];
            IReadOnlyList<double?> xs = s.Categories.IsNumeric ? s.Categories.Numbers : [];
            IReadOnlyList<double?> ys = s.Values.Numbers;
            for (int i = 0; i < ys.Count; i++)
            {
                if (ys[i] is not { } y || !double.IsFinite(y))
                {
                    points.Add(null);
                    continue;
                }

                double x = i < xs.Count && xs[i] is { } given ? given : i + 1;
                points.Add(new Vector2(across.Value(x, area), up.Value(y, area)));
            }

            // Without its own line, a series with the line-and-marker style draws its line; the marker style has none.
            Rgba8 own = SeriesColor(s);
            Rgba8 automaticLine = style is ChartScatterStyle.Marker or ChartScatterStyle.None ? NoColor : own;
            canvas.Save();
            canvas.ClipRect(area);
            if (LineOf(s.Format, automaticLine, SeriesLineWidth) is { } line)
            {
                StrokeSeries(points, line, s.Smooth == true || (s.Smooth is null && style is ChartScatterStyle.Smooth or ChartScatterStyle.SmoothMarker), blanks == ChartBlanks.Span);
            }

            canvas.Restore();
            DrawMarkers(s, points, style is not (ChartScatterStyle.Line or ChartScatterStyle.Smooth), own);
            DrawTrendlines(s, across, up, area);
            DrawErrorBars(s, across, up, area, plot);
        }
    }

    private void DrawBubbles(ChartPlot plot, DrawnAxis across, DrawnAxis up, RectF area)
    {
        List<ChartSeries> series = Ordered(plot);
        double largest = series.SelectMany(s => s.BubbleSizes.Numbers).Select(v => Math.Abs(v ?? 0)).DefaultIfEmpty(0).Max();
        float reach = Math.Min(area.Width, area.Height) * 0.25f * Math.Clamp(plot.BubbleScale ?? 100, 0, 300) / 100f / 2;
        canvas.Save();
        canvas.ClipRect(area);
        foreach (ChartSeries s in series)
        {
            IReadOnlyList<double?> xs = s.Categories.IsNumeric ? s.Categories.Numbers : [];
            IReadOnlyList<double?> ys = s.Values.Numbers;
            IReadOnlyList<double?> sizes = s.BubbleSizes.Numbers;
            Rgba8 own = SeriesColor(s);
            for (int i = 0; i < ys.Count; i++)
            {
                if (ys[i] is not { } y || !double.IsFinite(y))
                {
                    continue;
                }

                double size = i < sizes.Count ? sizes[i] ?? 1 : 1;
                if (size < 0 && plot.ShowNegativeBubbles != true)
                {
                    continue;
                }

                double amount = largest > 0 ? Math.Abs(size) / largest : 1;
                float r = (float)((plot.BubbleSizeIsWidth == true ? amount : Math.Sqrt(amount)) * reach);
                if (r <= 0)
                {
                    continue;
                }

                double x = i < xs.Count && xs[i] is { } given ? given : i + 1;
                Vector2 at = new(across.Value(x, area), up.Value(y, area));
                ChartPoint? point = PointOf(s, i);
                ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
                Path circle = Path.Circle(at, r);
                if (Fill(format, circle.Bounds, plot.VaryColors == true && series.Count == 1 ? PointColor(i, ys.Count) : own) is { } fill)
                {
                    canvas.FillPath(circle, fill);
                }

                if (LineOf(format, NoColor, 0.75f) is { } line)
                {
                    canvas.StrokePath(circle, line.Stroke, line.Color);
                }
            }
        }

        canvas.Restore();
    }

    private RectF RadarPlot(ChartBoxes boxes, float left, float top, float right, float bottom)
    {
        DrawnAxis? spokes = boxes.Axes.FirstOrDefault(a => a.IsCategories);
        Look look = spokes is null ? LookOf(chart.TextFormat, baseSize) : AxisLook(spokes);
        float h = Measure("0", look).Height;
        float w = spokes is null || spokes.Model.Hidden ? 0 : spokes.Labels.Select(t => Measure(t, look).Width).DefaultIfEmpty(0).Max();
        float l = left + PlotMargin;
        float r = right - PlotMargin;
        float t = top;
        float b = bottom - PlotMargin;
        float side = Math.Max(Math.Min(r - l - (2 * w), b - t - (2 * RadarLabelLines * h)), 1);
        return new RectF(l + ((r - l - side) / 2), t + ((b - t - side) / 2), side, side);
    }

    private void DrawRadar(ChartPlot plot, ChartBoxes boxes)
    {
        DrawnAxis? spokes = boxes.Axes.FirstOrDefault(a => a.IsCategories && a.Plots.Contains(plot));
        DrawnAxis? scale = spokes?.Cross;
        if (spokes is null || scale is null || spokes.Count == 0)
        {
            return;
        }

        RectF area = boxes.Plot;
        Vector2 center = new(area.X + (area.Width / 2), area.Y + (area.Height / 2));
        float radius = area.Width / 2;
        int n = spokes.Count;
        Vector2 Spoke(int i, float reach)
        {
            double angle = (2 * Math.PI * i / n) - (Math.PI / 2);
            return center + new Vector2((float)(Math.Cos(angle) * reach), (float)(Math.Sin(angle) * reach));
        }

        ChartAxis valueModel = scale.Model;
        (Stroke Stroke, Rgba8 Color)? grid = valueModel.HasMajorGridlines ? LineOf(valueModel.MajorGridlines, palette.AutomaticLine, 0.75f) : null;
        foreach (double tick in scale.Scale.Ticks())
        {
            float reach = (float)(scale.Scale.Place(tick) * radius);
            if (grid is { } g && reach > 0)
            {
                Vector2[] ring = [.. Enumerable.Range(0, n).Select(i => Spoke(i, reach))];
                canvas.StrokePath(Path.Polygon(ring), g.Stroke, g.Color);
            }
        }

        if (LineOf(valueModel.Format, palette.AutomaticLine, 0.75f) is { } spokeInk && !valueModel.Hidden)
        {
            for (int i = 0; i < n; i++)
            {
                canvas.DrawLine(center, Spoke(i, radius), spokeInk.Stroke, spokeInk.Color);
            }
        }

        bool filled = plot.RadarStyle == ChartRadarStyle.Filled;
        foreach (ChartSeries s in Ordered(plot))
        {
            IReadOnlyList<double?> values = s.Values.Numbers;
            List<Vector2?> points = [];
            for (int i = 0; i < values.Count && i < n; i++)
            {
                double v = values[i] is { } given && double.IsFinite(given) ? given : scale.Scale.Minimum;
                points.Add(Spoke(i, (float)(Math.Clamp(scale.Scale.Place(v), 0, 1.5) * radius)));
            }

            if (points.Count == 0)
            {
                continue;
            }

            Path shape = Path.Polygon([.. points.Select(p => p!.Value)]);
            Rgba8 own = SeriesColor(s);
            if (filled && Fill(s.Format, area, own) is { } fill)
            {
                canvas.FillPath(shape, fill);
            }

            if (LineOf(s.Format, filled ? NoColor : own, filled ? 0.75f : SeriesLineWidth) is { } line && points.Count > 1)
            {
                canvas.StrokePath(shape, line.Stroke with { Join = LineJoin.Round }, line.Color);
            }

            DrawMarkers(s, points, plot.RadarStyle == ChartRadarStyle.Marker, own);
        }

        if (!valueModel.Hidden && valueModel.TickLabelPosition != ChartTickLabelPosition.None)
        {
            Look look = AxisLook(scale);
            float h = Measure("0", look).Height;
            List<double> ticks = scale.Scale.Ticks();
            for (int i = 0; i < ticks.Count && i < scale.Labels.Count; i++)
            {
                float w = Measure(scale.Labels[i], look).Width;
                DrawTextAt(scale.Labels[i], look, new Vector2(center.X - w - 4, center.Y - (float)(scale.Scale.Place(ticks[i]) * radius) - (h / 2)));
            }
        }

        if (!spokes.Model.Hidden && spokes.Model.TickLabelPosition != ChartTickLabelPosition.None)
        {
            Look look = AxisLook(spokes);
            for (int i = 0; i < n && i < spokes.Labels.Count; i++)
            {
                Vector2 tip = Spoke(i, radius + 4);
                (float w, float h) = Measure(spokes.Labels[i], look);
                float dx = tip.X - center.X;
                float dy = tip.Y - center.Y;
                float x = Math.Abs(dx) < 1 ? tip.X - (w / 2) : dx > 0 ? tip.X : tip.X - w;
                float y = Math.Abs(dy) < 1 ? tip.Y - (h / 2) : dy > 0 ? tip.Y : tip.Y - h;
                DrawTextAt(spokes.Labels[i], look, new Vector2(x, y));
            }
        }
    }

    /// <summary>The room outside a pie for labels that sit outside it.</summary>
    private float OutsideLabelRoom(float side)
    {
        float room = 0;
        foreach (ChartPlot plot in plots)
        {
            foreach (ChartSeries s in plot.Series.Take(1))
            {
                ChartDataLabels labels = LabelsFor(s, plot);
                if (!AnyLabelText(labels) || labels.Position is ChartLabelPosition.Center or ChartLabelPosition.InsideEnd)
                {
                    continue;
                }

                Look look = LookOf(labels.TextFormat, baseSize);
                int count = s.Values.Count;
                for (int i = 0; i < count && i < 256; i++)
                {
                    (float w, float h) = Measure(LabelText(labels, s, plot, i), look);
                    room = Math.Max(room, Math.Max(w * 0.6f, h) + 4);
                }
            }
        }

        return Math.Min(room, side * 0.25f);
    }

    private void DrawPie(ChartPlot plot, RectF area)
    {
        if (plot.Series.Count == 0)
        {
            return;
        }

        ChartSeries s = Ordered(plot)[0];
        IReadOnlyList<double?> values = s.Values.Numbers;
        double total = values.Sum(v => v is > 0 ? v.Value : 0);
        if (!(total > 0))
        {
            return;
        }

        bool vary = plot.VaryColors != false;
        int largest = s.Points.Select(p => p.Explosion ?? 0).Append(s.Explosion ?? 0).Max();
        float outer = area.Width / 2 / (1 + (Math.Clamp(largest, 0, 400) / 100f));
        Vector2 center = new(area.X + (area.Width / 2), area.Y + (area.Height / 2));
        double angle = (plot.FirstSliceAngle ?? 0) - 90;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not > 0)
            {
                continue;
            }

            double sweep = values[i]!.Value / total * 360;
            ChartPoint? point = PointOf(s, i);
            double middle = (angle + (sweep / 2)) * Math.PI / 180;
            float push = outer * Math.Clamp(point?.Explosion ?? s.Explosion ?? 0, 0, 400) / 100f;
            Vector2 at = center + new Vector2((float)Math.Cos(middle) * push, (float)Math.Sin(middle) * push);
            RectF oval = new(at.X - outer, at.Y - outer, 2 * outer, 2 * outer);
            Path slice = sweep >= 359.999 ? Path.Oval(oval) : Path.Pie(oval, (float)angle, (float)sweep);
            ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
            Rgba8 automatic = vary ? PointColor(i, values.Count) : SeriesColor(s);
            if (Fill(format, oval, automatic) is { } fill)
            {
                canvas.FillPath(slice, fill);
            }

            if (LineOf(format, NoColor, 0.75f) is { } line)
            {
                canvas.StrokePath(slice, line.Stroke, line.Color);
            }

            angle += sweep;
        }
    }

    private void DrawDoughnut(ChartPlot plot, RectF area)
    {
        List<ChartSeries> series = Ordered(plot);
        if (series.Count == 0)
        {
            return;
        }

        float outer = area.Width / 2;
        float hole = outer * Math.Clamp(plot.HoleSize ?? 10, 0, 90) / 100f;
        float ring = (outer - hole) / series.Count;
        Vector2 center = new(area.X + outer, area.Y + outer);
        for (int k = 0; k < series.Count; k++)
        {
            ChartSeries s = series[k];
            IReadOnlyList<double?> values = s.Values.Numbers;
            double total = values.Sum(v => v is > 0 ? v.Value : 0);
            if (!(total > 0))
            {
                continue;
            }

            float inner = hole + (k * ring);
            float rim = inner + ring;
            double angle = (plot.FirstSliceAngle ?? 0) - 90;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not > 0)
                {
                    continue;
                }

                double sweep = Math.Min(values[i]!.Value / total * 360, 359.999);
                Path slice = RingSlice(center, inner, rim, angle, sweep);
                ChartPoint? point = PointOf(s, i);
                ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
                if (Fill(format, area, plot.VaryColors != false ? PointColor(i, values.Count) : SeriesColor(s)) is { } fill)
                {
                    canvas.FillPath(slice, fill);
                }

                if (LineOf(format, palette.Light, 0.75f) is { } line)
                {
                    canvas.StrokePath(slice, line.Stroke, line.Color);
                }

                angle += sweep;
            }
        }
    }

    private static Path RingSlice(Vector2 center, float inner, float outer, double start, double sweep)
    {
        PathBuilder builder = new();
        double a = start * Math.PI / 180;
        double b = (start + sweep) * Math.PI / 180;
        Vector2 On(double angle, float r) => center + new Vector2((float)Math.Cos(angle) * r, (float)Math.Sin(angle) * r);
        builder.MoveTo(On(a, outer));
        builder.ArcTo(new Vector2(outer, outer), 0, sweep > 180, true, On(b, outer));
        builder.LineTo(On(b, inner));
        builder.ArcTo(new Vector2(inner, inner), 0, sweep > 180, false, On(a, inner));
        builder.Close();
        return builder.ToPath();
    }
}
