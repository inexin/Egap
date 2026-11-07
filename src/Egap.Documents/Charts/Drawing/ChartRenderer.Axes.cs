using System.Globalization;
using System.Numerics;

using Egami.Drawing;
using Egami.Fonts.Layout;
using Egami.Pixels;

namespace Egap.Documents.Charts;

/// <summary>An axis as it lays out: which way it runs, its scale and its labels.</summary>
internal sealed class DrawnAxis(ChartAxis model, bool horizontal, bool categories)
{
    public ChartAxis Model { get; } = model;

    /// <summary>Whether the axis runs across the chart.</summary>
    public bool Horizontal { get; } = horizontal;

    /// <summary>Whether the axis places categories rather than numbers.</summary>
    public bool IsCategories { get; } = categories;

    public List<ChartPlot> Plots { get; } = [];

    /// <summary>The scale of a numeric axis.</summary>
    public ChartScale Scale { get; set; }

    /// <summary>How many categories the axis has room for.</summary>
    public int Count { get; set; }

    /// <summary>Whether categories sit between tick marks, not on them.</summary>
    public bool Between { get; set; } = true;

    /// <summary>The label of each category or tick, from the start of the axis.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>For a date axis, each category's place among the days it spans; empty otherwise.</summary>
    public double[] Places { get; set; } = [];

    /// <summary>Show one label in this many.</summary>
    public int Skip { get; set; } = 1;

    /// <summary>The degrees the labels turn: Excel's automatic 45 when they do not fit across, or what the file says.</summary>
    public float Rotation { get; set; }

    /// <summary>The width category labels wrap to; infinite when they do not.</summary>
    public float Wrap { get; set; } = 1e5f;

    /// <summary>The room the labels take beside the axis, across it.</summary>
    public float LabelRoom { get; set; }

    /// <summary>The labels of the levels above the first, each level's runs of a label and how many categories it spans.</summary>
    public List<List<(string Text, int Start, int Span)>> Levels { get; } = [];

    /// <summary>The axis's title box.</summary>
    public RectF? TitleBox { get; set; }

    /// <summary>The other axis of the pair.</summary>
    public DrawnAxis? Cross { get; set; }

    /// <summary>Whether the axis sits at the far side (right or top) of the plot.</summary>
    public bool FarSide { get; set; }

    /// <summary>Where a number or a place along the axis lands, along the plot.</summary>
    public float At(double place, RectF plot)
    {
        if (Model.Reversed)
        {
            place = 1 - place;
        }

        return Horizontal ? plot.Left + (float)(place * plot.Width) : plot.Bottom - (float)(place * plot.Height);
    }

    /// <summary>Where a number lands along the plot.</summary>
    public float Value(double value, RectF plot) => At(Scale.Place(value), plot);

    /// <summary>Where a category's middle lands along the plot.</summary>
    public float Category(int index, RectF plot) => At(CategoryPlace(index), plot);

    /// <summary>A category's middle, from 0 to 1 along the axis.</summary>
    public double CategoryPlace(int index)
    {
        double i = Places.Length > index ? Places[index] : index;
        if (Count <= 0)
        {
            return 0.5;
        }

        return Between ? (i + 0.5) / Count : Count > 1 ? i / (Count - 1) : 0.5;
    }

    /// <summary>The length along the plot of one category.</summary>
    public float Band(RectF plot) => (Horizontal ? plot.Width : plot.Height) / Math.Max(Between ? Count : Count - 1, 1);
}

/// <content>Axes: pairs, scales, labels, the room they take, and drawing them.</content>
internal sealed partial class ChartRenderer
{
    private const float LabelGap = 6.2f;
    private const float SideLabelGap = 8f;
    private const float MinimumLabelSizes = 2.8f;

    private void BuildAxes(ChartBoxes boxes)
    {
        Dictionary<long, DrawnAxis> known = [];
        foreach (ChartPlot plot in plots)
        {
            if (plot.Type is ChartType.Pie or ChartType.Doughnut or ChartType.PieOfPie or ChartType.BarOfPie || chart.IsExtended)
            {
                continue;
            }

            IReadOnlyList<ChartAxis> axes = plot.Axes;
            ChartAxis? first = axes.FirstOrDefault(a => a.Kind is ChartAxisKind.Category or ChartAxisKind.Date);
            ChartAxis? value = axes.FirstOrDefault(a => a.Kind == ChartAxisKind.Value && a != first);
            bool xy = plot.Type is ChartType.Scatter or ChartType.Bubble;
            if (xy)
            {
                List<ChartAxis> values = [.. axes.Where(a => a.Kind == ChartAxisKind.Value)];
                first = values.FirstOrDefault(a => a.Position is ChartAxisPosition.Bottom or ChartAxisPosition.Top) ?? values.FirstOrDefault();
                value = values.FirstOrDefault(a => a.Id != first?.Id);
            }

            if (first is null || value is null)
            {
                continue;
            }

            bool sideways = plot.Type == ChartType.Bar;
            if (!known.TryGetValue(first.Id, out DrawnAxis? across))
            {
                across = new DrawnAxis(first, !sideways, !xy);
                known[first.Id] = across;
                boxes.Axes.Add(across);
            }

            if (!known.TryGetValue(value.Id, out DrawnAxis? up))
            {
                up = new DrawnAxis(value, sideways, false);
                known[value.Id] = up;
                boxes.Axes.Add(up);
            }

            across.Cross = up;
            up.Cross = across;
            across.Plots.Add(plot);
            up.Plots.Add(plot);
            across.FarSide = first.Position is ChartAxisPosition.Top or ChartAxisPosition.Right || first.Crosses == ChartCrosses.Maximum;
            up.FarSide = value.Position is ChartAxisPosition.Top or ChartAxisPosition.Right || value.Crosses == ChartCrosses.Maximum;
        }

        foreach (DrawnAxis axis in boxes.Axes.Where(a => a.IsCategories))
        {
            ReadCategories(axis);
        }
    }

    private void ReadCategories(DrawnAxis axis)
    {
        List<ChartSeries> series = [.. axis.Plots.SelectMany(p => p.Series)];
        axis.Count = series.Count == 0 ? 0 : series.Max(s => Math.Max(s.Values.Count, s.Categories.Count));
        axis.Between = axis.Cross?.Model.CrossesBetweenCategories ?? true;
        ChartSeries? source = series.FirstOrDefault(s => s.Categories.Count > 0);
        if (source is null)
        {
            axis.Labels = [.. Enumerable.Range(1, axis.Count).Select(i => i.ToString(CultureInfo.InvariantCulture))];
            return;
        }

        ChartData categories = source.Categories;
        if (categories.TextLevels is { Count: > 1 } levels)
        {
            axis.Labels = [.. levels[0].Select(t => t ?? "")];
            foreach (IReadOnlyList<string?> level in levels.Skip(1))
            {
                List<(string, int, int)> runs = [];
                for (int i = 0; i < level.Count; i++)
                {
                    if (level[i] is { Length: > 0 } text)
                    {
                        int end = i + 1;
                        while (end < level.Count && string.IsNullOrEmpty(level[end]))
                        {
                            end++;
                        }

                        runs.Add((text, i, end - i));
                    }
                }

                axis.Levels.Add(runs);
            }

            return;
        }

        if (categories.IsNumeric)
        {
            string? code = axis.Model.NumberFormat ?? categories.FormatCode;
            IReadOnlyList<double?> numbers = categories.Numbers;
            if (axis.Model.Kind == ChartAxisKind.Date && numbers.Count > 0 && numbers.Any(n => n is not null))
            {
                double low = numbers.Where(n => n is not null).Min()!.Value;
                double high = numbers.Where(n => n is not null).Max()!.Value;
                if (high - low < 200000)
                {
                    axis.Places = [.. numbers.Select(n => (n ?? low) - low)];
                    axis.Count = (int)(high - low) + 1;
                    axis.Labels = [.. Enumerable.Range(0, axis.Count).Select(i => Number(low + i, code))];
                    return;
                }
            }

            axis.Labels = [.. numbers.Select(n => n is { } v ? Number(v, code) : "")];
            return;
        }

        axis.Labels = [.. categories.Text.Select(t => t ?? "")];
    }

    private void LayOutPlot(ChartBoxes boxes, float left, float top, float right, float bottom)
    {
        if (boxes.Axes.Count == 0)
        {
            bool radial = plots.Count > 0;
            RectF inner = RectF.FromEdges(left + PlotMargin, top, right - PlotMargin, bottom - PlotMargin);
            if (left == 0 && radial)
            {
                inner = RectF.FromEdges(PlotMargin, top, right - PlotMargin, bottom - PlotMargin);
            }

            boxes.Plot = RoundPlot(ManualPlot(inner) ?? inner, boxes);
            return;
        }

        // Two passes: the scales' steps depend on the plot's size, and the room their labels take sets that size.
        RectF plot = RectF.FromEdges(left + 30, top, right - PlotMargin, bottom - 30);
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (DrawnAxis axis in boxes.Axes)
            {
                if (!axis.IsCategories)
                {
                    Scale(axis, plot);
                }
            }

            foreach (DrawnAxis axis in boxes.Axes)
            {
                MeasureLabels(axis, plot);
            }

            plot = PlotFrom(boxes, left, top, right, bottom);
        }

        if (plots.Any(p => p.Type == ChartType.Radar))
        {
            plot = RadarPlot(boxes, left, top, right, bottom);
            foreach (DrawnAxis axis in boxes.Axes.Where(a => !a.IsCategories))
            {
                Scale(axis, plot with { Height = plot.Height / 2 });
            }
        }

        boxes.Plot = plot;
    }

    private RectF RoundPlot(RectF inner, ChartBoxes boxes)
    {
        float side = Math.Max(Math.Min(inner.Width, inner.Height), 1);
        float room = plots.Any(p => p.Type is ChartType.Pie or ChartType.PieOfPie or ChartType.BarOfPie) ? OutsideLabelRoom(side) : 0;
        side = Math.Max(side - (2 * room), 1);
        _ = boxes;
        return new RectF(inner.X + ((inner.Width - side) / 2), inner.Y + ((inner.Height - side) / 2), side, side);
    }

    private RectF? ManualPlot(RectF inner)
    {
        ChartLayout layout = chart.PlotAreaLayout;
        if (layout.X is { } x && layout.Y is { } y && layout.Width is > 0 && layout.Height is > 0 && (layout.XMode == ChartLayoutMode.Edge || layout.XMode is null))
        {
            return Finite(new RectF((float)(x * width), (float)(y * height), (float)(layout.Width.Value * width), (float)(layout.Height.Value * height)), inner);
        }

        return null;
    }

    private RectF PlotFrom(ChartBoxes boxes, float left, float top, float right, float bottom)
    {
        float l = Side(boxes, left, ChartAxisPosition.Left);
        float r = right - (Side(boxes, width - right == 0 ? 0 : 0, ChartAxisPosition.Right));
        float t = top - PlotMargin + Side(boxes, 0, ChartAxisPosition.Top);
        float b = bottom - Side(boxes, 0, ChartAxisPosition.Bottom);
        if (!boxes.Axes.Any(a => HasSideLabels(a, ChartAxisPosition.Top) || a.Model.HasTitle && Sits(a, ChartAxisPosition.Top)))
        {
            t = top;
        }

        // Categories on their tick marks hang half a label past the plot at each end: the margin takes it.
        foreach (DrawnAxis axis in boxes.Axes.Where(a => a.Horizontal && a.IsCategories && !a.Between && a.Labels.Count > 0 && LabelsShown(a)))
        {
            Look look = AxisLook(axis);
            l = Math.Max(l, left + PlotMargin + (Measure(axis.Labels[0], look).Width / 2));
            r = Math.Min(r, right - PlotMargin - (Measure(axis.Labels[^1], look).Width / 2));
        }

        // A horizontal number axis lets its last label hang half out over the margin.
        foreach (DrawnAxis axis in boxes.Axes.Where(a => a.Horizontal && !a.IsCategories && a.Labels.Count > 0 && LabelsShown(a)))
        {
            float half = Measure(axis.Labels[^1], AxisLook(axis)).Width / 2;
            r = Math.Min(r, right - PlotMargin - half);
        }

        if (right - r < 1)
        {
            r = right - PlotMargin;
        }

        RectF plot = Finite(RectF.FromEdges(l, t, Math.Max(r, l + 1), Math.Max(b, t + 1)), new RectF(0, 0, 1, 1));
        if (ManualPlot(plot) is { } manual)
        {
            bool outer = chart.PlotAreaLayout.InnerPlot != true;
            if (!outer)
            {
                return manual;
            }

            // An outer layout holds the axes' labels too.
            float inLeft = boxes.Axes.Where(a => Sits(a, ChartAxisPosition.Left)).Sum(a => a.LabelRoom);
            float inRight = boxes.Axes.Where(a => Sits(a, ChartAxisPosition.Right)).Sum(a => a.LabelRoom);
            float inTop = boxes.Axes.Where(a => Sits(a, ChartAxisPosition.Top)).Sum(a => a.LabelRoom);
            float inBottom = boxes.Axes.Where(a => Sits(a, ChartAxisPosition.Bottom)).Sum(a => a.LabelRoom);
            return RectF.FromEdges(manual.Left + inLeft, manual.Top + inTop, Math.Max(manual.Right - inRight, manual.Left + inLeft + 1), Math.Max(manual.Bottom - inBottom, manual.Top + inTop + 1));
        }

        PlaceAxisTitles(boxes, plot);
        return plot;
    }

    private static bool Sits(DrawnAxis axis, ChartAxisPosition side) => side switch
    {
        ChartAxisPosition.Left => !axis.Horizontal && !axis.FarSide,
        ChartAxisPosition.Right => !axis.Horizontal && axis.FarSide,
        ChartAxisPosition.Top => axis.Horizontal && axis.FarSide,
        _ => axis.Horizontal && !axis.FarSide,
    };

    private static bool LabelsShown(DrawnAxis axis) => !axis.Model.Hidden && axis.Model.TickLabelPosition != ChartTickLabelPosition.None;

    private static bool HasSideLabels(DrawnAxis axis, ChartAxisPosition side) => Sits(axis, side) && LabelsOutside(axis);

    /// <summary>Whether the labels go outside the plot: an axis that crosses inside it keeps its labels inside too.</summary>
    private static bool LabelsOutside(DrawnAxis axis)
    {
        if (!LabelsShown(axis))
        {
            return false;
        }

        if (axis.Model.TickLabelPosition is ChartTickLabelPosition.Low or ChartTickLabelPosition.High || axis.Cross is not { } cross || cross.IsCategories)
        {
            return true;
        }

        ChartScale scale = cross.Scale;
        double crossing = axis.Model.Crosses switch
        {
            ChartCrosses.Maximum => scale.Maximum,
            ChartCrosses.Minimum => scale.Minimum,
            _ when axis.Model.CrossesAt is { } at => Math.Clamp(at, scale.Minimum, scale.Maximum),
            _ => scale.IsLog ? scale.Minimum : Math.Clamp(0, scale.Minimum, scale.Maximum),
        };
        double place = scale.Place(crossing);
        return axis.FarSide ? place > 0.999 : place < 0.001;
    }

    /// <summary>What a side of the plot sets aside: the margin, the labels' room and an axis title.</summary>
    private float Side(ChartBoxes boxes, float start, ChartAxisPosition side)
    {
        float labels = 0;
        float title = 0;
        foreach (DrawnAxis axis in boxes.Axes.Where(a => Sits(a, side)))
        {
            if (LabelsOutside(axis))
            {
                labels = Math.Max(labels, axis.LabelRoom);
            }

            if (!axis.Model.Hidden && axis.Model.HasTitle && axis.Model.Title.Overlay != true)
            {
                title = Math.Max(title, AxisTitleSize(axis).Thick);
            }
        }

        return start + (title > 0 ? PlotMargin + title + labels : labels > 0 ? LabelMargin + labels : PlotMargin);
    }

    private (string Text, float Long, float Thick) AxisTitleSize(DrawnAxis axis)
    {
        string text = axis.Model.Title.Text ?? "Axis Title";
        (float w, float h) = Measure(text, AxisTitleLook(axis));
        return (text, w + 4, h + TextBoxPadding);
    }

    private void PlaceAxisTitles(ChartBoxes boxes, RectF plot)
    {
        foreach (DrawnAxis axis in boxes.Axes)
        {
            if (axis.Model.Hidden || !axis.Model.HasTitle)
            {
                continue;
            }

            (_, float along, float thick) = AxisTitleSize(axis);
            float room = LabelsOutside(axis) ? axis.LabelRoom : 0;
            RectF box;
            if (axis.Horizontal)
            {
                float x = plot.Left + ((plot.Width - along) / 2);
                box = axis.FarSide ? new RectF(x, plot.Top - room - thick, along, thick) : new RectF(x, plot.Bottom + room, along, thick);
            }
            else
            {
                float y = plot.Top + ((plot.Height - along) / 2);
                box = axis.FarSide ? new RectF(plot.Right + room, y, thick, along) : new RectF(plot.Left - room - thick, y, thick, along);
            }

            axis.TitleBox = Placed(axis.Model.Title.Layout, box);
        }
    }

    private Look AxisLook(DrawnAxis axis) => LookOf(axis.Model.TextFormat, baseSize);

    private Look AxisTitleLook(DrawnAxis axis) => LookOf(axis.Model.Title.TextFormat, baseSize, true);

    /// <summary>The numbers a value axis has to show, across the plots that use it.</summary>
    private static (double Low, double High, bool Percent) Extent(DrawnAxis axis)
    {
        double low = double.PositiveInfinity;
        double high = double.NegativeInfinity;
        bool percent = false;
        foreach (ChartPlot plot in axis.Plots)
        {
            ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Standard;
            bool xy = plot.Type is ChartType.Scatter or ChartType.Bubble;
            if (xy && axis.Cross is { } && axis == XAxis(axis))
            {
                foreach (ChartSeries series in plot.Series)
                {
                    IReadOnlyList<double?> xs = series.Categories.IsNumeric ? series.Categories.Numbers : [];
                    int count = series.Values.Count;
                    for (int i = 0; i < count; i++)
                    {
                        Include(i < xs.Count && xs[i] is { } x ? x : i + 1);
                    }
                }

                continue;
            }

            if (grouping == ChartGrouping.PercentStacked && plot.Type is ChartType.Column or ChartType.Bar or ChartType.Area or ChartType.Line)
            {
                percent = true;
                Include(0);
                Include(1);
                if (plot.Series.Any(s => s.Values.Numbers.Any(v => v < 0)))
                {
                    Include(-1);
                }

                continue;
            }

            if (grouping == ChartGrouping.Stacked && plot.Type is ChartType.Column or ChartType.Bar or ChartType.Area or ChartType.Line)
            {
                int count = plot.Series.Count == 0 ? 0 : plot.Series.Max(s => s.Values.Count);
                for (int i = 0; i < count; i++)
                {
                    double positive = 0;
                    double negative = 0;
                    foreach (ChartSeries series in plot.Series)
                    {
                        if (i < series.Values.Count && series.Values.Numbers[i] is { } v && double.IsFinite(v))
                        {
                            if (v >= 0)
                            {
                                positive += v;
                            }
                            else
                            {
                                negative += v;
                            }
                        }
                    }

                    Include(positive);
                    Include(negative);
                }

                continue;
            }

            foreach (ChartSeries series in plot.Series)
            {
                IReadOnlyList<double?> values = series.Values.Numbers;
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i] is { } v)
                    {
                        Include(v);
                        foreach ((double down, double up) in ErrorRange(series, i, v))
                        {
                            Include(down);
                            Include(up);
                        }
                    }
                }
            }
        }

        if (double.IsInfinity(low))
        {
            low = 0;
            high = 0;
        }

        // Bubbles reach past their centres, by about an eighth of the data's span on Excel's axes.
        if (axis.Plots.Any(p => p.Type == ChartType.Bubble))
        {
            double reach = (high - low) * 0.12;
            high += reach;
            low -= reach;
        }

        return (low, high, percent);

        void Include(double v)
        {
            if (double.IsFinite(v))
            {
                low = Math.Min(low, v);
                high = Math.Max(high, v);
            }
        }
    }

    private static DrawnAxis? XAxis(DrawnAxis axis) => axis.Horizontal ? axis : axis.Cross;

    private void Scale(DrawnAxis axis, RectF plot)
    {
        (double low, double high, bool percent) = Extent(axis);
        ChartAxis model = axis.Model;
        Look look = AxisLook(axis);
        (float _, float lineHeight) = Measure("0", look);

        // Excel settles the step before it knows the other axis's labels go inside the plot: the room they would take still counts.
        bool radar = plots.Any(p => p.Type == ChartType.Radar);
        float along = axis.Horizontal ? plot.Width : plot.Height;
        if (axis.Cross is { } cross && LabelsShown(cross) && cross.Horizontal != axis.Horizontal && !LabelsOutside(cross) && !radar)
        {
            along = Math.Max(along - Math.Max(cross.LabelRoom - (PlotMargin - LabelMargin), 0), 1);
        }

        // It also counts the other axis's labels as one line, before they wrap.
        if (axis.Cross is { Horizontal: true, IsCategories: true, Wrap: < 1e4f } wrapped && !axis.Horizontal && !radar)
        {
            along += Math.Max(wrapped.LabelRoom - lineHeight - HorizontalLabelPadding, 0);
        }

        if (model.LogBase is >= 2 and <= 1000)
        {
            axis.Scale = ChartScale.Logarithmic(low, high, model.LogBase.Value, model.Minimum, model.Maximum, model.MajorUnit);
        }
        else
        {
            // Excel takes the first step whose labels and one more line's room fit: across the axis by the line, along it by the widest label.
            bool Fits(ChartScale scale)
            {
                double each = along / (((scale.Maximum - scale.Minimum) / scale.Major) + 1);
                if (!axis.Horizontal)
                {
                    return each >= (radar ? look.Size * 0.85 : percent ? look.Size * 0.9 : (look.Size * 1.07) + 1.2);
                }

                float widest = scale.Ticks().Select(t => Measure(TickText(axis, t, percent), look).Width).DefaultIfEmpty(0).Max();
                return each >= Math.Max(widest * MinimumLabelWidths, look.Size * MinimumLabelSizes);
            }

            if (percent)
            {
                low = Math.Min(low, 0);
                high = Math.Max(high, low < 0 ? 0 : 1);
                axis.Scale = ChartScale.Automatic(low, high, Fits, model.Minimum ?? (low < 0 ? -1 : 0), model.Maximum ?? 1, model.MajorUnit, model.MinorUnit);
            }
            else
            {
                axis.Scale = ChartScale.Automatic(low, high, Fits, model.Minimum, model.Maximum, model.MajorUnit, model.MinorUnit, axis.Plots.Any(p => p.Type == ChartType.Radar) ? 0 : 0.05);
            }
        }

        axis.Labels = [.. axis.Scale.Ticks().Select(t => TickText(axis, t, percent))];
    }

    private string TickText(DrawnAxis axis, double tick, bool percent)
    {
        string? code = axis.Model.NumberFormat;
        code ??= percent ? "0%" : axis.Plots.SelectMany(p => p.Series).FirstOrDefault(s => s.Values.Count > 0)?.Values.FormatCode;
        double unit = Unit(axis.Model.DisplayUnit);
        return Number(ChartScale.Clean(tick / unit), code);
    }

    private static double Unit(string? name) => name switch
    {
        "hundreds" => 1e2,
        "thousands" => 1e3,
        "tenThousands" => 1e4,
        "hundredThousands" => 1e5,
        "millions" => 1e6,
        "tenMillions" => 1e7,
        "hundredMillions" => 1e8,
        "billions" => 1e9,
        "trillions" => 1e12,
        _ => 1,
    };

    private void MeasureLabels(DrawnAxis axis, RectF plot)
    {
        Look look = AxisLook(axis);
        (float _, float lineHeight) = Measure("0", look);
        if (axis.Model.Hidden || axis.Model.TickLabelPosition == ChartTickLabelPosition.None || axis.Labels.Count == 0)
        {
            axis.LabelRoom = 0;
            return;
        }

        double? rotation = axis.Model.TextFormat.Rotation;
        axis.Rotation = (float)(rotation ?? 0);
        axis.Skip = axis.Model.TickLabelSkip is > 0 ? axis.Model.TickLabelSkip.Value : 1;
        axis.Wrap = 1e5f;
        if (!axis.Horizontal)
        {
            float widest = axis.Labels.Select(t => Measure(t, look).Width).DefaultIfEmpty(0).Max();
            axis.LabelRoom = widest + VerticalLabelPadding;
            if (axis.IsCategories && axis.Model.TickLabelSkip is null)
            {
                float each = axis.Band(plot);
                axis.Skip = Math.Max(1, (int)Math.Ceiling(lineHeight / Math.Max(each, 0.1f)));
            }

            // Bars lay their categories' labels out to a fraction of the chart at most, wrapping past that.
            if (axis.IsCategories && widest > width * 0.3f)
            {
                axis.Wrap = width * 0.3f;
                axis.LabelRoom = axis.Wrap + VerticalLabelPadding;
            }

            return;
        }

        float label = lineHeight;
        if (axis.IsCategories)
        {
            float band = axis.Band(plot) * axis.Skip;
            float widest = axis.Labels.Select(t => Measure(t, look).Width).DefaultIfEmpty(0).Max();
            if (rotation is null && widest > band)
            {
                float word = axis.Labels.Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => Measure(w, look).Width).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
                float wrapped = axis.Labels.Select(t => Measure(t, look, band).Height).DefaultIfEmpty(0).Max();
                if (word <= band && wrapped <= height * 0.25f)
                {
                    axis.Wrap = band;
                    label = wrapped;
                }
                else if (band >= lineHeight * 1.2f)
                {
                    axis.Rotation = -45;
                }
                else
                {
                    axis.Rotation = -90;
                    if (axis.Model.TickLabelSkip is null && band < lineHeight)
                    {
                        axis.Skip = Math.Max(1, (int)Math.Ceiling(lineHeight / Math.Max(axis.Band(plot), 0.1f)));
                    }
                }
            }
            else if (rotation is not null && Math.Abs(rotation.Value) < 1 && widest > band && axis.Model.TickLabelSkip is null)
            {
                axis.Skip = Math.Max(1, (int)Math.Ceiling(widest / Math.Max(axis.Band(plot), 0.1f)));
            }

            if (axis.Rotation != 0)
            {
                double radians = Math.Abs(axis.Rotation) * Math.PI / 180;
                label = Math.Min((float)((widest * Math.Sin(radians)) + (lineHeight * Math.Cos(radians))), height * 0.45f);
            }

            label += axis.Levels.Count * (lineHeight + 2);
        }

        axis.LabelRoom = label + HorizontalLabelPadding;
    }

    private const float MinimumLabelWidths = 2.85f;
    private const float VerticalLabelPadding = 10.9f;
    private const float HorizontalLabelPadding = 7.8f;

    private void DrawGridlines(DrawnAxis axis, RectF plot)
    {
        if (plots.Any(p => p.Type == ChartType.Radar))
        {
            return;
        }

        ChartAxis model = axis.Model;
        if (model.HasMinorGridlines && LineOf(model.MinorGridlines, palette.AutomaticLine, 0.75f) is { } minor)
        {
            foreach (float place in MinorPlaces(axis, plot))
            {
                Across(place, minor);
            }
        }

        if (model.HasMajorGridlines && LineOf(model.MajorGridlines, palette.AutomaticLine, 0.75f) is { } major)
        {
            foreach (float place in MajorPlaces(axis, plot))
            {
                Across(place, major);
            }
        }

        void Across(float place, (Stroke Stroke, Rgba8 Color) line)
        {
            Vector2 start = axis.Horizontal ? new Vector2(place, plot.Top) : new Vector2(plot.Left, place);
            Vector2 end = axis.Horizontal ? new Vector2(place, plot.Bottom) : new Vector2(plot.Right, place);
            canvas.DrawLine(start, end, line.Stroke, line.Color);
        }
    }

    private static List<float> MajorPlaces(DrawnAxis axis, RectF plot)
    {
        List<float> places = [];
        if (axis.IsCategories)
        {
            int segments = axis.Between ? axis.Count : axis.Count - 1;
            int step = axis.Model.TickMarkSkip is > 0 ? axis.Model.TickMarkSkip.Value : axis.Skip;
            for (int i = 0; i <= segments && segments > 0 && places.Count < 5000; i += Math.Max(step, 1))
            {
                places.Add(axis.At((double)i / segments, plot));
            }

            return places;
        }

        foreach (double tick in axis.Scale.Ticks())
        {
            places.Add(axis.Value(tick, plot));
        }

        return places;
    }

    private static List<float> MinorPlaces(DrawnAxis axis, RectF plot)
    {
        List<float> places = [];
        if (axis.IsCategories)
        {
            return MajorPlaces(axis, plot);
        }

        ChartScale scale = axis.Scale;
        if (scale.IsLog)
        {
            foreach (double tick in scale.Ticks())
            {
                for (int k = 2; k < scale.LogBase && k < 11; k++)
                {
                    if (tick * k < scale.Maximum)
                    {
                        places.Add(axis.Value(tick * k, plot));
                    }
                }
            }

            return places;
        }

        if (scale.Minor > 0)
        {
            int count = (int)Math.Min((scale.Maximum - scale.Minimum) / scale.Minor, 2000);
            for (int i = 0; i <= count; i++)
            {
                places.Add(axis.Value(scale.Minimum + (i * scale.Minor), plot));
            }
        }

        return places;
    }

    /// <summary>Where along the cross axis this axis's line runs.</summary>
    private static float LineAt(DrawnAxis axis, RectF plot)
    {
        ChartAxis model = axis.Model;
        if (axis.Cross is not { } cross)
        {
            return axis.Horizontal ? plot.Bottom : plot.Left;
        }

        if (cross.IsCategories)
        {
            double place = model.Crosses switch
            {
                ChartCrosses.Maximum => 1,
                ChartCrosses.Minimum => 0,
                _ when model.CrossesAt is { } at => cross.Count > 0 ? Math.Clamp((at - 1 + (cross.Between ? 0 : 0)) / Math.Max(cross.Between ? cross.Count : cross.Count - 1, 1), 0, 1) : 0,
                _ => 0,
            };
            return cross.At(place, plot);
        }

        ChartScale scale = cross.Scale;
        double value = model.Crosses switch
        {
            ChartCrosses.Maximum => scale.Maximum,
            ChartCrosses.Minimum => scale.Minimum,
            _ when model.CrossesAt is { } at => Math.Clamp(at, scale.Minimum, scale.Maximum),
            _ => scale.IsLog ? scale.Minimum : Math.Clamp(0, scale.Minimum, scale.Maximum),
        };
        return cross.Value(value, plot);
    }

    private void DrawAxis(DrawnAxis axis, RectF plot)
    {
        ChartAxis model = axis.Model;
        if (model.Hidden || plots.Any(p => p.Type == ChartType.Radar))
        {
            DrawAxisTitle(axis);
            return;
        }

        float line = LineAt(axis, plot);
        (Stroke Stroke, Rgba8 Color)? ink = LineOf(model.Format, palette.AutomaticLine, 0.75f);
        if (ink is { } axisInk)
        {
            Vector2 start = axis.Horizontal ? new Vector2(plot.Left, line) : new Vector2(line, plot.Top);
            Vector2 end = axis.Horizontal ? new Vector2(plot.Right, line) : new Vector2(line, plot.Bottom);
            canvas.DrawLine(start, end, axisInk.Stroke, axisInk.Color);
            DrawTicks(axis, plot, line, axisInk, model.MajorTickMark ?? ChartTickMark.Cross, 4, MajorPlaces(axis, plot));
            if (model.MinorTickMark is { } minor && minor != ChartTickMark.None)
            {
                DrawTicks(axis, plot, line, axisInk, minor, 2, MinorPlaces(axis, plot));
            }
        }

        if (LabelsShown(axis))
        {
            DrawLabels(axis, plot, line);
        }

        DrawAxisTitle(axis);
    }

    private void DrawAxisTitle(DrawnAxis axis)
    {
        if (axis.TitleBox is not { } box)
        {
            return;
        }

        string text = axis.Model.Title.Text ?? "Axis Title";
        double? rotation = axis.Model.Title.TextFormat.Rotation;
        DrawTextIn(text, AxisTitleLook(axis), box, rotation ?? (axis.Horizontal ? 0 : -90));
    }

    private void DrawTicks(DrawnAxis axis, RectF plot, float line, (Stroke Stroke, Rgba8 Color) ink, ChartTickMark mark, float size, List<float> places)
    {
        if (mark == ChartTickMark.None)
        {
            return;
        }

        bool far = axis.FarSide;
        float outside = mark is ChartTickMark.Outside or ChartTickMark.Cross ? size : 0;
        float inside = mark is ChartTickMark.Inside or ChartTickMark.Cross ? size : 0;
        foreach (float place in places)
        {
            if (axis.Horizontal)
            {
                float down = far ? inside : outside;
                float up = far ? outside : inside;
                canvas.DrawLine(new Vector2(place, line - up), new Vector2(place, line + down), ink.Stroke, ink.Color);
            }
            else
            {
                float back = far ? inside : outside;
                float forth = far ? outside : inside;
                canvas.DrawLine(new Vector2(line - back, place), new Vector2(line + forth, place), ink.Stroke, ink.Color);
            }
        }

        _ = plot;
    }

    private void DrawLabels(DrawnAxis axis, RectF plot, float line)
    {
        Look look = AxisLook(axis);
        (float _, float lineHeight) = Measure("0", look);
        ChartTickLabelPosition position = axis.Model.TickLabelPosition ?? ChartTickLabelPosition.NextToAxis;
        bool far = axis.FarSide;
        float edge = position switch
        {
            ChartTickLabelPosition.Low => axis.Horizontal ? (axis.Cross?.Model.Reversed == true ? plot.Top : plot.Bottom) : axis.Cross?.Model.Reversed == true ? plot.Right : plot.Left,
            ChartTickLabelPosition.High => axis.Horizontal ? (axis.Cross?.Model.Reversed == true ? plot.Bottom : plot.Top) : axis.Cross?.Model.Reversed == true ? plot.Left : plot.Right,
            _ => line,
        };
        if (position == ChartTickLabelPosition.Low)
        {
            far = axis.Cross?.Model.Reversed == true;
        }
        else if (position == ChartTickLabelPosition.High)
        {
            far = axis.Cross?.Model.Reversed != true;
        }

        for (int i = 0; i < axis.Labels.Count; i += Math.Max(axis.Skip, 1))
        {
            string text = axis.Labels[i];
            if (text.Length == 0)
            {
                continue;
            }

            float along = axis.IsCategories ? axis.At((axis.Between ? i + 0.5 : i) / Math.Max(axis.Between ? axis.Count : axis.Count - 1, 1), plot) : axis.Value(axis.Scale.Ticks()[i], plot);
            if (axis.IsCategories && axis.Places.Length == 0 && i < axis.Count)
            {
                along = axis.Category(i, plot);
            }

            DrawOneLabel(axis, text, look, along, edge, far, lineHeight);
        }

        if (axis.Levels.Count > 0 && axis.Horizontal)
        {
            float row = edge + (far ? -1 : 1) * (LabelGap + (lineHeight + 2));
            foreach (List<(string Text, int Start, int Span)> level in axis.Levels)
            {
                foreach ((string text, int start, int span) in level)
                {
                    float from = axis.Category(start, plot) - (axis.Band(plot) / 2);
                    float to = axis.Category(start + span - 1, plot) + (axis.Band(plot) / 2);
                    DrawTextIn(text, look, new RectF(Math.Min(from, to), far ? row - lineHeight : row, Math.Abs(to - from), lineHeight), 0);
                }

                row += (far ? -1 : 1) * (lineHeight + 2);
            }
        }
    }

    private void DrawOneLabel(DrawnAxis axis, string text, Look look, float along, float edge, bool far, float lineHeight)
    {
        float angle = axis.Rotation;
        if (angle != 0)
        {
            TextLayout turned = Lay(text, look, 1e5f, TextAlign.Left);
            canvas.Save();
            if (axis.Horizontal)
            {
                // A turned label ends under its tick: its far end is where the tick is, as Excel draws it.
                canvas.Translate(along, far ? edge - LabelGap : edge + LabelGap);
                canvas.Rotate(angle);
                bool endsAtTick = angle < 0;
                canvas.DrawTextLayout(turned, new Vector2(endsAtTick ? -turned.LongestLine : 0, -lineHeight / 2));
            }
            else
            {
                canvas.Translate(far ? edge + SideLabelGap : edge - SideLabelGap, along);
                canvas.Rotate(angle);
                canvas.DrawTextLayout(turned, new Vector2(far ? 0 : -turned.LongestLine, -lineHeight / 2));
            }

            canvas.Restore();
            return;
        }

        TextLayout layout = Lay(text, look, axis.Wrap, axis.Horizontal ? TextAlign.Center : far ? TextAlign.Left : TextAlign.Right);
        float w = axis.Wrap < 1e4f ? axis.Wrap : layout.LongestLine;
        if (axis.Wrap >= 1e4f)
        {
            layout = Lay(text, look, w + 1, axis.Horizontal ? TextAlign.Center : far ? TextAlign.Left : TextAlign.Right);
            w += 1;
        }

        Vector2 corner = axis.Horizontal
            ? new Vector2(along - (w / 2), far ? edge - LabelGap - layout.Height : edge + LabelGap)
            : new Vector2(far ? edge + SideLabelGap : edge - SideLabelGap - w, along - (layout.Height / 2));
        canvas.DrawTextLayout(layout, corner);
    }
}
