using System.Numerics;

using Egami.Drawing;
using Egami.Fonts.Layout;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.Charts;

/// <content>
/// The newer chart kinds: waterfall, histogram, Pareto, box and whisker, treemap, sunburst and funnel. Their layout rules and constants
/// in points come from measuring where Excel's own PDF puts each part of a chart.
/// </content>
internal sealed partial class ChartRenderer
{
    private const float ExtendedTextSize = 9;
    private const float ExtendedTitleSize = 14.04f;
    private const float ExtendedLine = 0.75f;
    private const float TitleBand = 20.85f;
    private const float LegendBand = 28.8f;
    private const float TitleBaseline = 18.6f;
    private const float ExtendedKey = 7.05f;
    private const float ExtendedKeyGap = 3.9f;
    private const float ExtendedEntryPad = 8.9f;
    private const float PlotEdge = 6.6f;
    private const float PlotTop = 10.95f;
    private const float RegionEdge = 5.85f;
    private const float ValueLabelEdge = 5.33f;
    private const float ValueLabelGap = 6.5f;
    private const float CategoryBaseline = 14.9f;
    private const float CategoryBand = 23;
    private const float LabelOverBar = 2.6f;
    private const float LabelUnderBar = 8.5f;
    private const float CellBorder = 1.5f;
    private const float CellLabelInset = 3.8f;
    private const float ParentLabelBaseline = 12.3f;
    private const float LeafLabelBaseline = 6.4f;
    private const float WhiskerCap = 8.25f;
    private const float MeanMark = 3f;
    private const float BoxShrink = 2.25f;
    private const float OutlierRadius = 2.25f;
    private const float ParetoLine = 2.25f;
    private const float ParetoLeft = 6;
    private const float ParetoTop = 1.2f;
    private const float SquareEnough = 0.95f;

    private Rgba8 ExtendedText => ChartPalette.Mix(palette.Dark, palette.Light, 0.35);

    private Rgba8 ExtendedRule => ChartPalette.Mix(palette.Dark, palette.Light, 0.85);

    private Rgba8 Accent(int index) => theme.Colors[ChartPalette.Accents[index % ChartPalette.Accents.Length]];

    private void DrawExtended()
    {
        RectF area = new(0, 0, width, height);
        if (Fill(chart.Format, area, palette.Light) is { } fill)
        {
            canvas.FillRect(area, fill);
        }

        if (LineOf(chart.Format, ExtendedRule, ExtendedLine) is { } frame)
        {
            float inset = frame.Stroke.Width / 2;
            canvas.StrokeRect(new RectF(inset, inset, width - frame.Stroke.Width, height - frame.Stroke.Width), frame.Stroke, frame.Color);
        }

        ChartPlot? plot = plots.FirstOrDefault(p => p.Series.Count > 0);
        if (plot is null)
        {
            DrawExtendedTitle();
            return;
        }

        switch (plot.Type)
        {
            case ChartType.Waterfall:
                DrawWaterfall(plot);
                break;
            case ChartType.Histogram or ChartType.Pareto:
                DrawBins(plot);
                break;
            case ChartType.BoxWhisker:
                DrawBoxes(plot);
                break;
            case ChartType.Treemap:
                DrawTreemap(plot);
                break;
            case ChartType.Sunburst:
                DrawSunburst(plot);
                break;
            case ChartType.Funnel:
                DrawFunnel(plot);
                break;
        }

        DrawExtendedTitle();
    }

    private Look ExtendedLook(ChartTextFormat format, float size, Rgba8? color = null)
    {
        Look look = LookOf(format, chart.TextFormat.Size is > 0 and < 1000 && format.Size is null ? (float)chart.TextFormat.Size.Value : size);
        return format.Color is null && chart.TextFormat.Color is null ? look with { Color = color ?? ExtendedText } : look;
    }

    private void DrawExtendedTitle()
    {
        if (!chart.HasTitle)
        {
            return;
        }

        string text = chart.Title.Text is { Length: > 0 } own ? own : "Chart Title";
        Look look = ExtendedLook(chart.Title.TextFormat, ExtendedTitleSize);
        TextAt(text, look, width / 2, TitleBaseline, TextAlign.Center);
    }

    /// <summary>A line of text with its baseline at <paramref name="baseline"/>, placed from <paramref name="x"/> as <paramref name="align"/> says.</summary>
    private void TextAt(string text, Look look, float x, float baseline, TextAlign align)
    {
        if (text.Length == 0)
        {
            return;
        }

        TextLayout layout = Lay(text, look, 1e5f, TextAlign.Left);
        float left = align switch
        {
            TextAlign.Center => x - (layout.LongestLine / 2),
            TextAlign.Right => x - layout.LongestLine,
            _ => x,
        };
        canvas.DrawTextLayout(layout, new Vector2(left, baseline - layout.Lines[0].Baseline));
    }

    /// <summary>Where the plot's top starts, the legend's band included, and what the title and a legend at the side or foot leave.</summary>
    private RectF ExtendedRegion(float edge, float topEdge, List<(string Text, Rgba8 Color)> entries)
    {
        bool shows = chart.HasLegend && entries.Count > 0;
        ChartLegendPosition position = chart.Legend.Position ?? ChartLegendPosition.Top;
        float top = topEdge + (chart.HasTitle ? TitleBand : 0);
        float left = edge;
        float right = width - edge;
        float bottom = height - edge;
        if (!shows || chart.Legend.Overlay == true)
        {
            return new RectF(left, top, Math.Max(right - left, 1), Math.Max(bottom - top, 1));
        }

        Look look = ExtendedLook(chart.Legend.TextFormat, ExtendedTextSize);
        float widest = entries.Max(e => Measure(e.Text, look).Width) + ExtendedKey + ExtendedKeyGap + ExtendedEntryPad;
        switch (position)
        {
            case ChartLegendPosition.Top:
                top += LegendBand;
                break;
            case ChartLegendPosition.Bottom:
                bottom -= LegendBand - edge + PlotEdge;
                break;
            case ChartLegendPosition.Left:
                left += widest;
                break;
            default:
                right -= widest;
                break;
        }

        return new RectF(left, top, Math.Max(right - left, 1), Math.Max(bottom - top, 1));
    }

    private void DrawExtendedLegend(List<(string Text, Rgba8 Color)> entries)
    {
        if (!chart.HasLegend || entries.Count == 0)
        {
            return;
        }

        Look look = ExtendedLook(chart.Legend.TextFormat, ExtendedTextSize);
        float titleRoom = chart.HasTitle ? TitleBand - 0.05f : 0;
        float line = Math.Max(LineHeight(look), ExtendedKey) + 2;
        ChartLegendPosition position = chart.Legend.Position ?? ChartLegendPosition.Top;
        if (position is ChartLegendPosition.Top or ChartLegendPosition.Bottom)
        {
            float total = entries.Sum(e => ExtendedKey + ExtendedKeyGap + Measure(e.Text, look).Width + ExtendedEntryPad) - ExtendedEntryPad;
            float x = (width - total) / 2;
            float middle = position == ChartLegendPosition.Top ? 18 + titleRoom : height - 12;
            foreach ((string text, Rgba8 color) in entries)
            {
                canvas.FillRect(new RectF(x, middle - (ExtendedKey / 2), ExtendedKey, ExtendedKey), new SolidPaint(color));
                TextAt(text, look, x + ExtendedKey + ExtendedKeyGap, middle + 3, TextAlign.Left);
                x += ExtendedKey + ExtendedKeyGap + Measure(text, look).Width + ExtendedEntryPad;
            }

            return;
        }

        float widest = entries.Max(e => Measure(e.Text, look).Width) + ExtendedKey + ExtendedKeyGap + ExtendedEntryPad;
        float left = position == ChartLegendPosition.Left ? PlotEdge : width - widest;
        float y = ((height + titleRoom - (entries.Count * line)) / 2) + (line / 2);
        foreach ((string text, Rgba8 color) in entries)
        {
            canvas.FillRect(new RectF(left, y - (ExtendedKey / 2), ExtendedKey, ExtendedKey), new SolidPaint(color));
            TextAt(text, look, left + ExtendedKey + ExtendedKeyGap, y + 3, TextAlign.Left);
            y += line;
        }
    }

    /// <summary>The colour a point draws in: its own, its series's or <paramref name="automatic"/>.</summary>
    private Paint PointPaint(ChartSeries series, int index, RectF box, Rgba8 automatic)
    {
        foreach (ChartPoint point in series.Points)
        {
            if (point.Index == index && Fill(point.Format, box, NoColor) is { } own)
            {
                return own;
            }
        }

        return Fill(series.Format, box, automatic) ?? new SolidPaint(NoColor);
    }

    private static double[] Finite(ChartSeries series) => [.. series.Values.Numbers.Where(v => v is { } n && double.IsFinite(n)).Select(v => v!.Value)];

    private static bool LabelsShow(ChartSeries series) => series.HasDataLabels;

    /// <summary>A plot with a value axis at its left and category labels under it, with room for a second axis's labels.</summary>
    private RectF DrawValueFrame(ChartPlot plot, ChartScale scale, string? code, IReadOnlyList<string> categories, float rightRoom, List<(string Text, Rgba8 Color)> entries, float leftRoom = 0, float topRoom = 0)
    {
        ChartAxis? values = plot.Axes.FirstOrDefault(a => a.Kind == ChartAxisKind.Value);
        ChartAxis? names = plot.Axes.FirstOrDefault(a => a.Kind == ChartAxisKind.Category);
        Look valueLook = ExtendedLook(values?.TextFormat ?? chart.TextFormat, ExtendedTextSize);
        Look nameLook = ExtendedLook(names?.TextFormat ?? chart.TextFormat, ExtendedTextSize);
        bool showValues = values is null || (!values.Hidden && values.TickLabelPosition != ChartTickLabelPosition.None);
        bool showNames = names is null || (!names.Hidden && names.TickLabelPosition != ChartTickLabelPosition.None);
        List<double> ticks = scale.Ticks();
        string[] labels = [.. ticks.Select(t => Number(t, values?.NumberFormat ?? code))];
        float widest = showValues && labels.Length > 0 ? labels.Max(l => Measure(l, valueLook).Width) : 0;
        RectF region = ExtendedRegion(0, PlotTop, entries);
        float titleWidth = values is { HasTitle: true } ? AxisTitleBand : 0;
        float titleHeight = names is { HasTitle: true } ? AxisTitleBand : 0;
        float left = region.Left + leftRoom + titleWidth + PlotEdge + (showValues ? ValueLabelEdge - PlotEdge + widest + ValueLabelGap : 0);
        float right = region.Right - Math.Max(PlotEdge, rightRoom);
        float top = region.Top + topRoom;
        float bottom = region.Bottom - titleHeight - (showNames ? CategoryBand : PlotEdge);
        if (!(right > left))
        {
            right = left + 1;
        }

        if (!(bottom > top))
        {
            bottom = top + 1;
        }

        RectF box = new(left, top, right - left, bottom - top);
        if (values is { HasTitle: true })
        {
            DrawTextIn(values.Title.Text is { Length: > 0 } text ? text : DefaultAxisTitle, valueLook, new RectF(region.Left, box.Top, titleWidth, box.Height), -90);
        }

        if (names is { HasTitle: true })
        {
            DrawTextIn(names.Title.Text is { Length: > 0 } text ? text : DefaultAxisTitle, nameLook, new RectF(box.Left, region.Bottom - titleHeight, box.Width, titleHeight), 0);
        }

        (Stroke Stroke, Rgba8 Color)? rule = LineOf(values?.MajorGridlines ?? chart.Format, ExtendedRule, ExtendedLine);
        Stroke hair = new(ExtendedLine);
        if (values is null || values.HasMajorGridlines)
        {
            foreach (double tick in ticks)
            {
                float y = box.Bottom - (float)(scale.Place(tick) * box.Height);
                canvas.DrawLine(new Vector2(box.Left, y), new Vector2(box.Right, y), rule?.Stroke ?? hair, rule?.Color ?? ExtendedRule);
            }
        }

        if (showValues)
        {
            float capHeight = valueLook.Size * 0.32f;
            for (int i = 0; i < ticks.Count; i++)
            {
                float y = box.Bottom - (float)(scale.Place(ticks[i]) * box.Height);
                TextAt(labels[i], valueLook, box.Left - ValueLabelGap, y + capHeight, TextAlign.Right);
            }
        }

        if (showNames && categories.Count > 0)
        {
            float band = box.Width / categories.Count;
            for (int i = 0; i < categories.Count; i++)
            {
                TextAt(FitTo(categories[i], nameLook, band), nameLook, box.Left + ((i + 0.5f) * band), box.Bottom + CategoryBaseline, TextAlign.Center);
            }
        }

        return box;
    }

    private const float AxisTitleBand = 17;
    private const string DefaultAxisTitle = "Axis Title";

    /// <summary>The rules at a plot's left and foot, drawn over its data.</summary>
    private void DrawValueFrameLines(RectF box, ChartScale scale)
    {
        Stroke hair = new(ExtendedLine);
        float zero = box.Bottom - (float)(scale.Place(Math.Clamp(0, scale.Minimum, scale.Maximum)) * box.Height);
        canvas.DrawLine(new Vector2(box.Left, zero), new Vector2(box.Right, zero), hair, ExtendedRule);
        canvas.DrawLine(new Vector2(box.Left, box.Top), new Vector2(box.Left, box.Bottom), hair, ExtendedRule);
    }

    /// <summary>A label cut to fit a width, with an ellipsis for what is left out.</summary>
    private string FitTo(string text, Look look, float room)
    {
        if (Measure(text, look).Width <= room)
        {
            return text;
        }

        for (int n = text.Length - 1; n > 0; n--)
        {
            string cut = text[..n].TrimEnd() + "…";
            if (Measure(cut, look).Width <= room)
            {
                return cut;
            }
        }

        return "";
    }

    private static double GapOf(ChartPlot plot, double otherwise) =>
        plot.Axes.FirstOrDefault(a => a.Kind == ChartAxisKind.Category) is not null && plot.GapWidth is { } percent ? percent / 100.0 : otherwise;

    private static (ChartScale Scale, string? Code) ExtendedScale(ChartPlot plot, ChartSeries series, double low, double high)
    {
        ChartAxis? axis = plot.Axes.FirstOrDefault(a => a.Kind == ChartAxisKind.Value);
        return (ChartScale.Automatic(low, high, _ => true, axis?.Minimum, axis?.Maximum, axis?.MajorUnit, axis?.MinorUnit), series.Values.FormatCode);
    }

    private void DrawWaterfall(ChartPlot plot)
    {
        ChartSeries series = plot.Series[0];
        IReadOnlyList<double?> numbers = series.Values.Numbers;
        IReadOnlyList<string?> names = series.Categories.Text;
        HashSet<int> totals = [.. series.Subtotals];
        int count = numbers.Count;
        double[] from = new double[count];
        double[] to = new double[count];
        double running = 0;
        double low = 0;
        double high = 0;
        for (int i = 0; i < count; i++)
        {
            double value = numbers[i] is { } n && double.IsFinite(n) ? n : 0;
            from[i] = totals.Contains(i) ? 0 : running;
            to[i] = totals.Contains(i) ? value : running + value;
            running = to[i];
            low = Math.Min(low, Math.Min(from[i], to[i]));
            high = Math.Max(high, Math.Max(from[i], to[i]));
        }

        List<(string Text, Rgba8 Color)> entries = [("Increase", Accent(0)), ("Decrease", Accent(1)), ("Total", Accent(2))];
        (ChartScale scale, string? code) = ExtendedScale(plot, series, low, high);
        string[] labels = [.. Enumerable.Range(0, count).Select(i => i < names.Count ? names[i] ?? "" : "")];
        RectF box = DrawValueFrame(plot, scale, code, labels, 0, entries);
        if (count == 0)
        {
            DrawExtendedLegend(entries);
            return;
        }

        float band = box.Width / count;
        float bar = band / (float)(1 + GapOf(plot, 0.5));
        float Y(double v) => box.Bottom - (float)(scale.Place(v) * box.Height);
        bool connectors = series.ShowConnectorLines ?? true;
        Stroke hair = new(ExtendedLine);
        Look look = ExtendedLook(series.DataLabels.TextFormat, ExtendedTextSize);
        canvas.Save();
        canvas.ClipRect(box);
        for (int i = 0; i < count; i++)
        {
            float x = box.Left + (i * band) + ((band - bar) / 2);
            float a = Y(from[i]);
            float b = Y(to[i]);
            RectF rect = new(x, Math.Min(a, b), bar, Math.Abs(a - b));
            Rgba8 automatic = totals.Contains(i) ? Accent(2) : to[i] >= from[i] ? Accent(0) : Accent(1);
            canvas.FillRect(rect, PointPaint(series, i, rect, automatic));
            if (connectors && i + 1 < count)
            {
                canvas.DrawLine(new Vector2(x + bar, b), new Vector2(x + band, b), hair, ExtendedRule);
            }
        }

        canvas.Restore();
        DrawValueFrameLines(box, scale);
        if (LabelsShow(series))
        {
            for (int i = 0; i < count; i++)
            {
                if (numbers[i] is not { } value)
                {
                    continue;
                }

                float x = box.Left + ((i + 0.5f) * band);
                bool down = to[i] < from[i];
                float edge = Y(to[i]);
                TextAt(Number(value, series.DataLabels.NumberFormat ?? code), look, x, down ? edge + LabelUnderBar : edge - LabelOverBar, TextAlign.Center);
            }
        }

        DrawExtendedLegend(entries);
    }

    /// <summary>Scott's rule for a bin's width: 3.5 standard deviations over the cube root of the count, to two digits.</summary>
    private static double BinWidth(double[] values)
    {
        double mean = values.Average();
        double spread = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / Math.Max(values.Length - 1, 1));
        double width = 3.5 * spread / Math.Cbrt(values.Length);
        if (!(width > 0) || !double.IsFinite(width))
        {
            return 1;
        }

        double power = Math.Pow(10, Math.Floor(Math.Log10(width)) - 1);
        return Math.Round(width / power) * power;
    }

    private (string[] Names, double[] Heights) BinsOf(ChartSeries series)
    {
        ChartBinning binning = series.Binning;
        double[] values = Finite(series);
        if (!series.HasBinning)
        {
            // A category's values add up.
            Dictionary<string, double> sums = new(StringComparer.Ordinal);
            List<string> order = [];
            IReadOnlyList<string?> names = series.Categories.Text;
            IReadOnlyList<double?> numbers = series.Values.Numbers;
            for (int i = 0; i < numbers.Count; i++)
            {
                string name = i < names.Count ? names[i] ?? "" : "";
                if (!sums.ContainsKey(name))
                {
                    sums[name] = 0;
                    order.Add(name);
                }

                sums[name] += numbers[i] is { } n && double.IsFinite(n) ? n : 0;
            }

            return ([.. order], [.. order.Select(n => sums[n])]);
        }

        if (values.Length == 0)
        {
            return ([], []);
        }

        double low = values.Min();
        double high = values.Max();
        double start = binning.Underflow ?? low;
        double end = binning.Overflow ?? high;
        double width = binning.BinSize is > 0 ? binning.BinSize.Value : binning.BinCount is > 0 ? (end - start) / binning.BinCount.Value : BinWidth(values);
        if (!(width > 0) || !double.IsFinite(width))
        {
            width = 1;
        }

        int count = Math.Clamp((int)Math.Ceiling(((end - start) / width) - 1e-9), 1, 1000);
        bool closedRight = binning.ClosedOnRight ?? true;
        double[] heights = new double[count + (binning.Underflow is null ? 0 : 1) + (binning.Overflow is null ? 0 : 1)];
        List<string> labels = [];
        int shift = binning.Underflow is null ? 0 : 1;
        string code = series.Values.FormatCode ?? "General";
        if (shift == 1)
        {
            labels.Add("≤" + " " + Number(start, code));
        }

        for (int i = 0; i < count; i++)
        {
            string a = Number(Digits(start + (i * width)), code);
            string b = Number(Digits(start + ((i + 1) * width)), code);
            labels.Add(closedRight
                ? (i == 0 && shift == 0 ? "[" : "(") + a + ", " + b + "]"
                : "[" + a + ", " + b + (i == count - 1 && binning.Overflow is null ? "]" : ")"));
        }

        if (binning.Overflow is not null)
        {
            labels.Add(">" + " " + Number(end, code));
        }

        foreach (double value in values)
        {
            if (binning.Underflow is not null && (closedRight ? value <= start : value < start))
            {
                heights[0]++;
                continue;
            }

            if (binning.Overflow is not null && (closedRight ? value > end : value >= end))
            {
                heights[^1]++;
                continue;
            }

            double place = (value - start) / width;
            int bin = closedRight ? (int)Math.Ceiling(place - 1e-9) - 1 : (int)Math.Floor(place + 1e-9);
            heights[shift + Math.Clamp(bin, 0, count - 1)]++;
        }

        return ([.. labels], heights);
    }

    /// <summary>A bin's edge as its label shows it: four significant digits.</summary>
    private static double Digits(double value)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return value;
        }

        double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(value))) - 3);
        return ChartScale.Clean(Math.Round(value / power) * power);
    }

    private void DrawBins(ChartPlot plot)
    {
        ChartSeries series = plot.Series[0];
        bool pareto = plot.Type == ChartType.Pareto;
        (string[] names, double[] heights) = BinsOf(series);
        if (pareto && !series.HasBinning)
        {
            int[] order = [.. Enumerable.Range(0, names.Length).OrderByDescending(i => heights[i])];
            names = [.. order.Select(i => names[i])];
            heights = [.. order.Select(i => heights[i])];
        }

        ChartSeries? line = pareto ? plot.Series.FirstOrDefault(s => s != series) : null;
        List<(string Text, Rgba8 Color)> entries = pareto
            ? [(SeriesName(series), Accent(0)), (line is null ? "Pareto" : SeriesName(line), Accent(1))]
            : [(SeriesName(series), Accent(0))];
        double high = heights.Length > 0 ? heights.Max() : 0;
        double low = heights.Length > 0 ? Math.Min(heights.Min(), 0) : 0;
        (ChartScale scale, string? code) = ExtendedScale(plot, series, low, high);
        if (series.HasBinning)
        {
            code = null;
        }

        Look look = ExtendedLook(chart.TextFormat, ExtendedTextSize);
        string[] percents = pareto ? [.. Enumerable.Range(0, 11).Select(i => Number(i / 10.0, "0%"))] : [];
        float rightRoom = pareto ? PlotEdge + percents.Max(p => Measure(p, look).Width) + PlotEdge : 0;
        RectF box = DrawValueFrame(plot, scale, code, names, rightRoom, entries, pareto ? ParetoLeft : 0, pareto ? ParetoTop : 0);
        if (names.Length > 0)
        {
            float band = box.Width / names.Length;
            float bar = band / (float)(1 + GapOf(plot, 0));
            float zero = box.Bottom - (float)(scale.Place(Math.Max(scale.Minimum, 0)) * box.Height);
            canvas.Save();
            canvas.ClipRect(box);
            for (int i = 0; i < names.Length; i++)
            {
                float y = box.Bottom - (float)(scale.Place(heights[i]) * box.Height);
                RectF rect = new(box.Left + (i * band) + ((band - bar) / 2), Math.Min(y, zero), bar, Math.Abs(y - zero));
                canvas.FillRect(rect, PointPaint(series, i, rect, Accent(0)));
            }

            canvas.Restore();
            DrawValueFrameLines(box, scale);
            if (LabelsShow(series))
            {
                Look labelLook = ExtendedLook(series.DataLabels.TextFormat, ExtendedTextSize);
                for (int i = 0; i < names.Length; i++)
                {
                    float y = box.Bottom - (float)(scale.Place(heights[i]) * box.Height);
                    TextAt(Number(heights[i], series.DataLabels.NumberFormat ?? code), labelLook, box.Left + ((i + 0.5f) * band), y - LabelOverBar, TextAlign.Center);
                }
            }

            if (pareto)
            {
                double total = heights.Sum();
                List<Vector2> points = [];
                double sum = 0;
                for (int i = 0; i < names.Length; i++)
                {
                    sum += heights[i];
                    float share = total > 0 ? (float)(sum / total) : 0;
                    points.Add(new Vector2(box.Left + ((i + 0.5f) * band), box.Bottom - (share * box.Height)));
                }

                (Stroke Stroke, Rgba8 Color)? ink = line is null ? null : LineOf(line.Format, Accent(1), ParetoLine);
                Stroke stroke = ink?.Stroke ?? new Stroke(ParetoLine);
                canvas.StrokePath(Path.Polygon(points.ToArray(), close: false), stroke with { Join = LineJoin.Round, Cap = LineCap.Round }, ink?.Color ?? Accent(1));
                float capHeight = look.Size * 0.32f;
                for (int i = 0; i < percents.Length; i++)
                {
                    TextAt(percents[i], look, box.Right + PlotEdge, box.Bottom - (i / 10f * box.Height) + capHeight, TextAlign.Left);
                }
            }
        }

        DrawExtendedLegend(entries);
    }

    /// <summary>A quartile of sorted values: exclusive counts from the ends past the data, inclusive from the ends of it.</summary>
    private static double Quartile(double[] sorted, double share, bool exclusive)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double place = exclusive ? (share * (sorted.Length + 1)) - 1 : share * (sorted.Length - 1);
        place = Math.Clamp(place, 0, sorted.Length - 1);
        int below = (int)Math.Floor(place);
        int above = Math.Min(below + 1, sorted.Length - 1);
        return sorted[below] + ((place - below) * (sorted[above] - sorted[below]));
    }

    private readonly record struct BoxStats(double Low, double Q1, double Median, double Q3, double High, double Mean, double[] Outliers, double[] Inner);

    private static BoxStats? StatsOf(List<double> values, bool exclusive)
    {
        if (values.Count == 0)
        {
            return null;
        }

        double[] sorted = [.. values.Order()];
        double q1 = Quartile(sorted, 0.25, exclusive);
        double median = Quartile(sorted, 0.5, exclusive);
        double q3 = Quartile(sorted, 0.75, exclusive);
        double reach = 1.5 * (q3 - q1);
        double[] within = [.. sorted.Where(v => v >= q1 - reach && v <= q3 + reach)];
        return new BoxStats(
            within.Length > 0 ? within[0] : q1,
            q1,
            median,
            q3,
            within.Length > 0 ? within[^1] : q3,
            sorted.Average(),
            [.. sorted.Where(v => v < q1 - reach || v > q3 + reach)],
            within);
    }

    private void DrawBoxes(ChartPlot plot)
    {
        List<string> names = [];
        Dictionary<(int Series, string Name), List<double>> groups = [];
        for (int s = 0; s < plot.Series.Count; s++)
        {
            ChartSeries series = plot.Series[s];
            IReadOnlyList<string?> text = series.Categories.Text;
            IReadOnlyList<double?> numbers = series.Values.Numbers;
            for (int i = 0; i < numbers.Count; i++)
            {
                string name = i < text.Count ? text[i] ?? "" : "";
                if (!names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }

                if (numbers[i] is { } n && double.IsFinite(n))
                {
                    if (!groups.TryGetValue((s, name), out List<double>? list))
                    {
                        groups[(s, name)] = list = [];
                    }

                    list.Add(n);
                }
            }
        }

        BoxStats?[,] stats = new BoxStats?[plot.Series.Count, names.Count];
        double low = double.PositiveInfinity;
        double high = double.NegativeInfinity;
        for (int s = 0; s < plot.Series.Count; s++)
        {
            bool exclusive = plot.Series[s].QuartileMethod != "inclusive";
            for (int c = 0; c < names.Count; c++)
            {
                if (groups.TryGetValue((s, names[c]), out List<double>? list) && StatsOf(list, exclusive) is { } st)
                {
                    stats[s, c] = st;
                    low = Math.Min(low, list.Min());
                    high = Math.Max(high, list.Max());
                }
            }
        }

        if (double.IsInfinity(low))
        {
            low = high = 0;
        }

        List<(string Text, Rgba8 Color)> entries = [.. plot.Series.Select((s, i) => (SeriesName(s), Accent(i)))];
        (ChartScale scale, string? code) = ExtendedScale(plot, plot.Series[0], low, high);
        RectF box = DrawValueFrame(plot, scale, code, names, 0, entries);
        float Y(double v) => box.Bottom - (float)(scale.Place(v) * box.Height);
        if (names.Count > 0)
        {
            float band = box.Width / names.Count;
            float cluster = band / (float)(1 + GapOf(plot, 1));
            float pitch = cluster / plot.Series.Count;
            float wide = Math.Max(pitch - BoxShrink, 1);
            canvas.Save();
            canvas.ClipRect(box);
            for (int s = 0; s < plot.Series.Count; s++)
            {
                ChartSeries series = plot.Series[s];
                Rgba8 color = series.Format.FillColor is { } own ? palette.Resolve(own) : Accent(s);
                Rgba8 ink = LineOf(series.Format, Darker(color), ExtendedLine)?.Color ?? Darker(color);
                Stroke stroke = LineOf(series.Format, Darker(color), ExtendedLine)?.Stroke ?? new Stroke(ExtendedLine);
                List<Vector2> means = [];
                for (int c = 0; c < names.Count; c++)
                {
                    if (stats[s, c] is not { } st)
                    {
                        continue;
                    }

                    float middle = box.Left + (c * band) + ((band - cluster) / 2) + ((s + 0.5f) * pitch);
                    RectF rect = new(middle - (wide / 2), Y(st.Q3), wide, Math.Max(Y(st.Q1) - Y(st.Q3), 0));
                    canvas.DrawLine(new Vector2(middle, Y(st.High)), new Vector2(middle, rect.Top), stroke, ink);
                    canvas.DrawLine(new Vector2(middle, rect.Bottom), new Vector2(middle, Y(st.Low)), stroke, ink);
                    canvas.DrawLine(new Vector2(middle - (WhiskerCap / 2), Y(st.High)), new Vector2(middle + (WhiskerCap / 2), Y(st.High)), stroke, ink);
                    canvas.DrawLine(new Vector2(middle - (WhiskerCap / 2), Y(st.Low)), new Vector2(middle + (WhiskerCap / 2), Y(st.Low)), stroke, ink);
                    canvas.FillRect(rect, new SolidPaint(color));
                    canvas.StrokeRect(rect, stroke, ink);
                    canvas.DrawLine(new Vector2(rect.Left, Y(st.Median)), new Vector2(rect.Right, Y(st.Median)), stroke, ink);
                    if (series.ShowMeanMarker ?? true)
                    {
                        float m = Y(st.Mean);
                        canvas.DrawLine(new Vector2(middle - MeanMark, m - MeanMark), new Vector2(middle + MeanMark, m + MeanMark), stroke, ink);
                        canvas.DrawLine(new Vector2(middle - MeanMark, m + MeanMark), new Vector2(middle + MeanMark, m - MeanMark), stroke, ink);
                    }

                    means.Add(new Vector2(middle, Y(st.Mean)));
                    if (series.ShowOutliers ?? true)
                    {
                        foreach (double v in st.Outliers)
                        {
                            canvas.StrokePath(Path.Circle(new Vector2(middle, Y(v)), OutlierRadius), stroke, ink);
                        }
                    }

                    if (series.ShowInnerPoints == true)
                    {
                        foreach (double v in st.Inner)
                        {
                            canvas.StrokePath(Path.Circle(new Vector2(middle, Y(v)), OutlierRadius), stroke, ink);
                        }
                    }
                }

                if (series.ShowMeanLine == true && means.Count > 1)
                {
                    canvas.StrokePath(Path.Polygon(means.ToArray(), close: false), stroke, ink);
                }
            }

            canvas.Restore();
        }

        DrawValueFrameLines(box, scale);
        DrawExtendedLegend(entries);
    }

    private static Rgba8 Darker(Rgba8 color) => new((byte)(color.R * 0.8), (byte)(color.G * 0.8), (byte)(color.B * 0.8), color.A);

    /// <summary>One node of a treemap's or sunburst's tree of categories.</summary>
    private sealed class TreeNode(string name)
    {
        public string Name { get; } = name;

        public double Size { get; set; }

        public int Point { get; set; } = -1;

        public int Group { get; set; }

        public List<TreeNode> Children { get; } = [];
    }

    /// <summary>The tree of categories: the outermost level at the top; the values add up towards it. Groups keep the order the file gives them.</summary>
    private static List<TreeNode> TreeOf(ChartSeries series)
    {
        IReadOnlyList<IReadOnlyList<string?>> levels = series.Categories.TextLevels;
        IReadOnlyList<double?> numbers = series.Values.Numbers;
        List<TreeNode> roots = [];
        for (int i = 0; i < numbers.Count; i++)
        {
            if (numbers[i] is not { } value || !double.IsFinite(value) || value <= 0)
            {
                continue;
            }

            List<TreeNode> here = roots;
            TreeNode? node = null;
            string? held = null;
            for (int level = levels.Count - 1; level >= 0; level--)
            {
                // A cell left empty goes on with the group above it, and an empty inner cell ends the path.
                string? name = i < levels[level].Count ? levels[level][i] : null;
                if (string.IsNullOrEmpty(name))
                {
                    if (level == levels.Count - 1)
                    {
                        for (int back = i - 1; back >= 0 && string.IsNullOrEmpty(held); back--)
                        {
                            held = back < levels[level].Count ? levels[level][back] : null;
                        }

                        name = held;
                    }
                    else
                    {
                        for (int back = i - 1; back >= 0; back--)
                        {
                            // Only a blank with something deeper on its row goes on with the name above it.
                            if (level > 0 && back < levels[level].Count && !string.IsNullOrEmpty(levels[level][back]))
                            {
                                name = levels[level][back];
                                break;
                            }

                            if (level == 0)
                            {
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }
                }

                TreeNode? found = here.FirstOrDefault(n => n.Name == name);
                if (found is null)
                {
                    found = new TreeNode(name);
                    here.Add(found);
                }

                found.Size += value;
                node = found;
                here = found.Children;
            }

            node?.Point = i;
        }

        for (int g = 0; g < roots.Count; g++)
        {
            Mark(roots[g], g);
        }

        return roots;

        static void Mark(TreeNode node, int group)
        {
            node.Group = group;
            foreach (TreeNode child in node.Children)
            {
                Mark(child, group);
            }
        }
    }

    /// <summary>Lays <paramref name="nodes"/> out in <paramref name="box"/> as squarified rows: along the shorter side, adding to a row while its worst aspect gets better.</summary>
    private static void Squarify(List<TreeNode> nodes, RectF box, Action<TreeNode, RectF> placed)
    {
        double total = nodes.Sum(n => n.Size);
        if (!(total > 0) || !(box.Width > 0) || !(box.Height > 0))
        {
            return;
        }

        TreeNode[] sorted = [.. nodes.OrderByDescending(n => n.Size)];
        double scale = box.Width * box.Height / total;
        double x = box.Left;
        double y = box.Top;
        double w = box.Width;
        double h = box.Height;
        int start = 0;
        while (start < sorted.Length)
        {
            // A box that is nearly square still lays its rows out as a wide one does.
            bool wide = w >= h * SquareEnough;
            double side = wide ? h : w;
            int end = start + 1;
            double sum = sorted[start].Size * scale;
            double worst = Worst(sorted[start].Size * scale, sorted[start].Size * scale, sum, side);
            while (end < sorted.Length)
            {
                double next = sorted[end].Size * scale;
                double trial = Worst(sorted[start].Size * scale, next, sum + next, side);
                if (trial > worst)
                {
                    break;
                }

                worst = trial;
                sum += next;
                end++;
            }

            double thick = sum / side;
            double along = 0;
            for (int i = start; i < end; i++)
            {
                double length = sorted[i].Size * scale / thick;
                RectF rect = wide
                    ? new RectF((float)x, (float)(y + along), (float)thick, (float)length)
                    : new RectF((float)(x + along), (float)y, (float)length, (float)thick);
                placed(sorted[i], rect);
                along += length;
            }

            if (wide)
            {
                x += thick;
                w -= thick;
            }
            else
            {
                y += thick;
                h -= thick;
            }

            start = end;
        }

        static double Worst(double largest, double smallest, double sum, double side)
        {
            double s2 = sum * sum;
            double l2 = side * side;
            return Math.Max(l2 * largest / s2, s2 / (l2 * smallest));
        }
    }

    private void DrawTreemap(ChartPlot plot)
    {
        ChartSeries series = plot.Series[0];
        List<TreeNode> roots = TreeOf(series);
        List<(string Text, Rgba8 Color)> entries = [.. roots.Select((r, i) => (r.Name, Accent(i)))];
        RectF region = ExtendedRegion(RegionEdge, 5.9f, entries);
        Stroke border = new(CellBorder);
        Rgba8 white = palette.Light;
        Look look = ExtendedLook(series.DataLabels.TextFormat, ExtendedTextSize, white);
        bool banner = series.ParentLabels == ChartParentLabels.Banner;
        bool parents = series.ParentLabels != ChartParentLabels.None && roots.Any(r => r.Children.Count > 0);
        bool labels = !series.HasDataLabels || series.DataLabels.ShowCategoryName || series.DataLabels.ShowValue;
        List<(string Text, float X, float Baseline, float Room)> texts = [];
        void Leaves(TreeNode node, RectF box)
        {
            if (node.Children.Count == 0)
            {
                canvas.FillRect(box, PointPaint(series, node.Point, box, Accent(node.Group)));
                canvas.StrokeRect(box, border, white);
                if (labels)
                {
                    string text = LeafText(series, node);
                    texts.Add((text, box.Left + CellLabelInset, box.Bottom - LeafLabelBaseline, box.Width - (CellLabelInset * 2)));
                }

                return;
            }

            Squarify(node.Children, box, Leaves);
        }

        Squarify(roots, region, (root, box) =>
        {
            RectF inner = box;
            if (banner && parents)
            {
                float strip = LineHeight(look) + 3;
                inner = new RectF(box.Left, box.Top + strip, box.Width, Math.Max(box.Height - strip, 0));
                canvas.FillRect(new RectF(box.Left, box.Top, box.Width, strip), new SolidPaint(Accent(root.Group)));
                texts.Add((root.Name, box.Left + CellLabelInset, box.Top + ParentLabelBaseline, box.Width - (CellLabelInset * 2)));
                Leaves(root, inner);
                return;
            }

            bool first = true;
            if (root.Children.Count == 0)
            {
                Leaves(root, inner);
                return;
            }

            Squarify(root.Children, inner, (child, rect) =>
            {
                Leaves(child, rect);
                if (first && parents)
                {
                    texts.Insert(0, (root.Name, rect.Left + CellLabelInset, rect.Top + ParentLabelBaseline, rect.Width - (CellLabelInset * 2)));
                    first = false;
                }
            });
        });
        foreach ((string text, float x, float baseline, float room) in texts)
        {
            if (room > 0 && baseline > region.Top)
            {
                TextAt(FitTo(text, look, room), look, x, baseline, TextAlign.Left);
            }
        }

        DrawExtendedLegend(entries);
    }

    private string LeafText(ChartSeries series, TreeNode node)
    {
        ChartDataLabels labels = series.DataLabels;
        bool name = !series.HasDataLabels || labels.ShowCategoryName;
        bool value = series.HasDataLabels && labels.ShowValue;
        string shown = name ? node.Name : "";
        if (value)
        {
            string number = Number(node.Size, labels.NumberFormat ?? series.Values.FormatCode);
            shown = shown.Length > 0 ? shown + (labels.Separator ?? ", ") + number : number;
        }

        return shown;
    }

    private void DrawSunburst(ChartPlot plot)
    {
        ChartSeries series = plot.Series[0];
        List<TreeNode> roots = TreeOf(series);
        List<(string Text, Rgba8 Color)> entries = [.. roots.Select((r, i) => (r.Name, Accent(i)))];
        RectF region = ExtendedRegion(RegionEdge, 5.9f, entries);
        int depth = Depth(roots);
        float radius = Math.Min(region.Width, region.Height) / 2;
        Vector2 center = new(region.Left + (region.Width / 2), region.Top + (region.Height / 2));
        float ring = radius / (depth + 1);
        Stroke border = new(CellBorder);
        Rgba8 white = palette.Light;
        Look look = ExtendedLook(series.DataLabels.TextFormat, ExtendedTextSize, white);
        List<(string Text, double Angle, float Inner, double Sweep)> texts = [];
        void Ring(List<TreeNode> nodes, double from, double sweep, int level, double total)
        {
            if (!(total > 0))
            {
                return;
            }

            double start = from;
            foreach (TreeNode node in nodes.OrderByDescending(n => n.Size))
            {
                double part = sweep * node.Size / total;
                float inner = ring * (level + 1);
                float outer = inner + ring;
                Path wedge = Wedge(center, inner, outer, start, part);
                RectF box = new(center.X - outer, center.Y - outer, outer * 2, outer * 2);
                canvas.FillPath(wedge, PointPaint(series, node.Point, box, Accent(node.Group)));
                canvas.StrokePath(wedge, border, white);
                texts.Add((node.Name, start + (part / 2), inner, part));
                Ring(node.Children, start, part, level + 1, node.Children.Sum(c => c.Size));
                start += part;
            }
        }

        Ring(roots, -90, 360, 0, roots.Sum(r => r.Size));
        if (!series.HasDataLabels || series.DataLabels.ShowCategoryName)
        {
            foreach ((string text, double angle, float inner, double sweep) in texts)
            {
                DrawRingLabel(text, look, center, inner, ring, angle, sweep);
            }
        }

        DrawExtendedLegend(entries);
    }

    private void DrawRingLabel(string text, Look look, Vector2 center, float inner, float ring, double angle, double sweep)
    {
        float middle = inner + (ring / 2);
        double along = middle * sweep * Math.PI / 180;

        // The label runs along the radius: it breaks to the ring's width, by the letter if it must, and has to fit the wedge's breadth.
        List<string> lines = BrokenTo(text, look, ring - 2);
        float line = LineHeight(look);
        if (lines.Count == 0 || lines.Count * line > along)
        {
            return;
        }

        double turned = angle;
        bool flip = Math.Cos(angle * Math.PI / 180) < 0;
        canvas.Save();
        canvas.Translate(center.X + (middle * (float)Math.Cos(angle * Math.PI / 180)), center.Y + (middle * (float)Math.Sin(angle * Math.PI / 180)));
        canvas.Rotate((float)(flip ? turned + 180 : turned));
        float first = (look.Size * 0.32f) - ((lines.Count - 1) * line / 2);
        for (int i = 0; i < lines.Count; i++)
        {
            TextAt(lines[i], look, 0, first + (i * line), TextAlign.Center);
        }

        canvas.Restore();
    }

    private List<string> BrokenTo(string text, Look look, float room)
    {
        List<string> lines = [];
        int start = 0;
        while (start < text.Length)
        {
            int end = text.Length;
            while (end > start && Measure(text[start..end], look).Width > room)
            {
                end--;
            }

            if (end == start)
            {
                return [];
            }

            lines.Add(text[start..end].Trim());
            start = end;
        }

        return lines;
    }

    private static int Depth(List<TreeNode> nodes) => nodes.Count == 0 ? 0 : 1 + nodes.Max(n => Depth(n.Children));

    private static Path Wedge(Vector2 center, float inner, float outer, double start, double sweep)
    {
        int steps = Math.Max((int)Math.Ceiling(Math.Abs(sweep) / 3), 1);
        List<Vector2> points = new((steps + 1) * 2);
        for (int i = 0; i <= steps; i++)
        {
            double a = (start + (sweep * i / steps)) * Math.PI / 180;
            points.Add(new Vector2(center.X + (outer * (float)Math.Cos(a)), center.Y + (outer * (float)Math.Sin(a))));
        }

        for (int i = steps; i >= 0; i--)
        {
            double a = (start + (sweep * i / steps)) * Math.PI / 180;
            points.Add(new Vector2(center.X + (inner * (float)Math.Cos(a)), center.Y + (inner * (float)Math.Sin(a))));
        }

        return Path.Polygon(points.ToArray(), close: true);
    }

    private void DrawFunnel(ChartPlot plot)
    {
        ChartSeries series = plot.Series[0];
        IReadOnlyList<string?> names = series.Categories.Text;
        IReadOnlyList<double?> numbers = series.Values.Numbers;
        int count = numbers.Count;
        List<(string Text, Rgba8 Color)> entries = [.. plot.Series.Select((s, i) => (SeriesName(s), Accent(i)))];
        ChartAxis? axis = plot.Axes.FirstOrDefault(a => a.Kind == ChartAxisKind.Category);
        Look look = ExtendedLook(axis?.TextFormat ?? chart.TextFormat, ExtendedTextSize);
        bool showNames = axis is null || (!axis.Hidden && axis.TickLabelPosition != ChartTickLabelPosition.None);
        string[] labels = [.. Enumerable.Range(0, count).Select(i => i < names.Count ? names[i] ?? "" : "")];
        float widest = showNames && count > 0 ? labels.Max(l => Measure(l, look).Width) : 0;
        RectF region = ExtendedRegion(0, PlotEdge, entries);
        float left = region.Left + (showNames ? ValueLabelEdge + widest + ValueLabelGap : PlotEdge);
        RectF box = new(left, region.Top, Math.Max(region.Right - PlotEdge - left, 1), Math.Max(region.Bottom - PlotEdge - region.Top, 1));
        if (count > 0)
        {
            double top = numbers.Max(v => v is { } n && double.IsFinite(n) ? Math.Abs(n) : 0);
            float band = box.Height / count;
            float bar = band / (float)(1 + GapOf(plot, 0.06));
            float middle = box.Left + (box.Width / 2);
            Look valueLook = ExtendedLook(series.DataLabels.TextFormat, ExtendedTextSize, palette.Light);
            float capHeight = look.Size * 0.32f;
            for (int i = 0; i < count; i++)
            {
                double value = numbers[i] is { } n && double.IsFinite(n) ? Math.Abs(n) : 0;
                float w = top > 0 ? (float)(value / top) * box.Width : 0;
                float y = box.Top + (i * band) + ((band - bar) / 2);
                RectF rect = new(middle - (w / 2), y, w, bar);
                canvas.FillRect(rect, PointPaint(series, i, rect, Accent(0)));
                if (showNames)
                {
                    TextAt(labels[i], look, box.Left - ValueLabelGap, y + (bar / 2) + capHeight, TextAlign.Right);
                }

                if (LabelsShow(series) && numbers[i] is { } shown)
                {
                    TextAt(Number(shown, series.DataLabels.NumberFormat ?? series.Values.FormatCode), valueLook, middle, y + (bar / 2) + capHeight, TextAlign.Center);
                }
            }

            canvas.DrawLine(new Vector2(box.Left, box.Top), new Vector2(box.Left, box.Bottom), new Stroke(ExtendedLine), ExtendedRule);
        }

        DrawExtendedLegend(entries);
    }
}
