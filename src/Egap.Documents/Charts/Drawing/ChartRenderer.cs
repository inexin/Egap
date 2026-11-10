using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

using Egap.Documents.DrawingML;
using Egap.Documents.Layout;
using Egap.Documents.NumberFormats;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.Charts;

/// <summary>Where a chart's parts go, in points from its corner.</summary>
internal sealed class ChartBoxes
{
    /// <summary>The inside of the plot area: where axes run and data draws.</summary>
    public RectF Plot { get; set; }

    /// <summary>The chart's title, if it shows one.</summary>
    public RectF? Title { get; set; }

    /// <summary>The legend, if the chart shows one.</summary>
    public RectF? Legend { get; set; }

    /// <summary>The axes that lay out, shown or not.</summary>
    public List<DrawnAxis> Axes { get; } = [];
}

/// <summary>
/// Draws a chart on an Egami canvas. Layout follows what Excel reports: the rules and their constants in points come
/// from reading Excel's own positions through tools/excel-chart-layout-oracle.ps1 and are checked against them by ChartLayoutOracleTests.
/// </summary>
internal sealed partial class ChartRenderer
{
    private const float EdgeMargin = 6;
    private const float PlotMargin = 11;
    private const float LabelMargin = 5;
    private const float TextBoxPadding = 3;

    private static readonly Rgba8 NoColor = new(0, 0, 0, 0);
    private static readonly ConcurrentDictionary<string, FormatSection[]> Formats = new(StringComparer.Ordinal);

    private readonly Chart chart;
    private readonly Theme theme;
    private readonly FontProvider fonts;
    private readonly CultureInfo culture;
    private readonly bool word;
    private readonly ChartPalette palette;
    private readonly float baseSize;
    private readonly bool baseSizeSet;
    private readonly List<ChartPlot> plots;
    private readonly int seriesCount;
    private readonly Dictionary<(ChartSeries Series, int Index), Mark> marks = [];
    private Canvas canvas = null!;
    private float radarRoom;
    private DrawnAxis? surfaceAxis;
    private float width;
    private float height;

    public ChartRenderer(Chart chart, ChartDrawOptions? options)
    {
        chart = chart.WithStylePartLook();
        this.chart = chart;
        theme = options?.Theme ?? new Document().Theme;
        fonts = options?.Provider ?? new FontProvider(theme.Document, options?.Fonts);
        culture = options?.Culture ?? CultureInfo.InvariantCulture;
        word = options?.Host == ChartHost.Word;
        int style = chart.Style ?? 2;
        palette = new ChartPalette(theme, style >= 100 ? style - 100 : style);
        baseSizeSet = chart.TextFormat.Size is not null;
        baseSize = (float)(chart.TextFormat.Size is > 0 and < 1000 ? chart.TextFormat.Size.Value : 10);
        plots = [.. chart.Plots];
        seriesCount = plots.Sum(p => p.Series.Count);
    }

    /// <summary>Whether the renderer draws <paramref name="chart"/>: every type but a region map and a plot of no type it knows.</summary>
    public static bool Draws(Chart chart) =>
        chart.IsExtended
            ? chart.Plots.All(p => p.Type is not (ChartType.RegionMap or ChartType.Unknown))
            : chart.Plots.All(p => p.Type != ChartType.Unknown);

    /// <summary>Draws the chart into <paramref name="bounds"/>.</summary>
    public void Draw(Canvas target, RectF bounds)
    {
        if (!(bounds.Width > 0 && bounds.Height > 0))
        {
            return;
        }

        canvas = target;
        marks.Clear();
        DrawnLabels.Clear();
        DrawnErrorBars.Clear();
        if (chart.IsExtended)
        {
            width = bounds.Width;
            height = bounds.Height;
            canvas.Save();
            canvas.ClipRect(bounds);
            canvas.Translate(bounds.X, bounds.Y);
            DrawExtended();
            canvas.Restore();
            return;
        }

        ChartBoxes boxes = Layout(bounds.Width, bounds.Height);
        canvas.Save();
        canvas.ClipRect(bounds);
        canvas.Translate(bounds.X, bounds.Y);
        DrawChartArea();
        if (plots.Any(IsThreeD) || plots.Any(p => p.Type is ChartType.PieOfPie or ChartType.BarOfPie))
        {
            DrawThreeDOrPair(boxes);
            DrawTitle(boxes);
            DrawLegend(boxes);
            canvas.Restore();
            return;
        }

        DrawPlotBackground(boxes);
        foreach (DrawnAxis axis in boxes.Axes)
        {
            DrawGridlines(axis, boxes.Plot);
        }

        DrawPlots(boxes);
        foreach (DrawnAxis axis in boxes.Axes)
        {
            DrawAxis(axis, boxes.Plot);
        }

        DrawDataLabels(boxes);
        DrawTitle(boxes);
        DrawLegend(boxes);
        canvas.Restore();
    }

    /// <summary>Lays the chart out in a box of the size given, in points.</summary>
    public ChartBoxes Layout(float chartWidth, float chartHeight)
    {
        width = chartWidth;
        height = chartHeight;
        ChartBoxes boxes = new();
        surfaceAxis = null;
        if (plots.Any(p => p.Type == ChartType.Surface))
        {
            // A surface's legend lists the bands of its scale, and the legend has to know before the plot's size does.
            ChartBoxes early = new();
            BuildAxes(early);
            if (early.Axes.FirstOrDefault(a => !a.IsCategories) is { } values)
            {
                Scale(values, new RectF(0, 0, width, height * SurfaceAxisShare));
                surfaceAxis = values;
            }
        }

        float top = PlotMargin;
        float titleBottom = 0;
        if (TitleText() is { } title)
        {
            Look look = TitleLook();
            (float w, float h) = Measure(TitleLines(title, look), look);
            RectF box = new((width - w - 4) / 2, EdgeMargin, w + 4, h + TextBoxPadding);
            float automaticTitleBottom = box.Bottom;
            box = Placed(chart.Title.Layout, box);
            boxes.Title = box;
            if (chart.Title.Overlay != true)
            {
                titleBottom = chart.Title.Layout.X is null ? box.Bottom : automaticTitleBottom;
                top = titleBottom + PlotMargin;
            }
        }

        float left = 0;
        float right = width;
        float bottom = height;
        if (LegendEntries() is { Count: > 0 } entries && chart.HasLegend)
        {
            ChartLegendPosition position = chart.Legend.Position ?? ChartLegendPosition.Right;
            bool overlay = chart.Legend.Overlay == true;
            (float w, float h) = LegendSize(entries, position, width, height - titleBottom);
            RectF box = position switch
            {
                ChartLegendPosition.Bottom => new((width - w) / 2, height - EdgeMargin - h, w, h),
                ChartLegendPosition.Top => new((width - w) / 2, titleBottom > 0 ? titleBottom + EdgeMargin : EdgeMargin, w, h),
                ChartLegendPosition.Left => new(EdgeMargin, Math.Max(titleBottom, 0) + ((height - titleBottom - h) / 2), w, h),
                ChartLegendPosition.TopRight => new(width - EdgeMargin - w, EdgeMargin, w, h),
                _ => new(width - EdgeMargin - w, Math.Max(titleBottom, 0) + ((height - titleBottom - h) / 2), w, h),
            };

            // A legend moved by hand still leaves the plot the room it would have had where its position says.
            boxes.Legend = Placed(chart.Legend.Layout, box);
            if (!overlay)
            {
                switch (position)
                {
                    case ChartLegendPosition.Bottom:
                        bottom = box.Top;
                        break;
                    case ChartLegendPosition.Top:
                        top = box.Bottom + PlotMargin;
                        break;
                    case ChartLegendPosition.Left:
                        left = box.Right;
                        break;
                    default:
                        right = box.Left;
                        break;
                }
            }
        }

        bottom -= DataTableRoom();
        BuildAxes(boxes);
        LayOutPlot(boxes, left, top, right, bottom);
        return boxes;
    }

    private RectF Placed(ChartLayout layout, RectF box)
    {
        float x = box.X;
        float y = box.Y;
        if (layout.X is { } lx)
        {
            x = layout.XMode == ChartLayoutMode.Edge ? (float)(lx * width) : box.X + (float)(lx * width);
        }

        if (layout.Y is { } ly)
        {
            y = layout.YMode == ChartLayoutMode.Edge ? (float)(ly * height) : box.Y + (float)(ly * height);
        }

        float w = layout.Width is > 0 ? (float)(layout.Width.Value * width) : box.Width;
        float h = layout.Height is > 0 ? (float)(layout.Height.Value * height) : box.Height;
        return Finite(new RectF(x, y, w, h), box);
    }

    private static RectF Finite(RectF rect, RectF otherwise) =>
        float.IsFinite(rect.X) && float.IsFinite(rect.Y) && float.IsFinite(rect.Width) && float.IsFinite(rect.Height) ? rect : otherwise;

    /// <summary>The text the title shows: its own, or the series's name when the file says only that a title is wanted.</summary>
    private string? TitleText()
    {
        if (chart.IsExtended)
        {
            return chart.HasTitle ? chart.Title.Text : null;
        }

        if (chart.HasTitle)
        {
            return chart.Title.Text is { Length: > 0 } text ? text : seriesCount == 1 && plots.First(p => p.Series.Count > 0).Series[0].Name is { Length: > 0 } name ? name : "Chart Title";
        }

        // With no title element and the flag not set, a chart of one series titles itself with the series's name.
        // So does a pie, which shows only its first series however many the file holds.
        if (!chart.AutoTitleDeleted && (seriesCount == 1 || (plots.Count == 1 && plots[0].Type == ChartType.Pie && seriesCount > 0)))
        {
            return SeriesName(plots.First(p => p.Series.Count > 0).Series[0]);
        }

        return null;
    }

    private static string SeriesName(ChartSeries series) => series.Name is { Length: > 0 } name ? name : "Series" + (series.Index + 1).ToString(CultureInfo.InvariantCulture);

    private void DrawChartArea()
    {
        RectF area = new(0, 0, width, height);
        bool rounded = chart.RoundedCorners ?? !word;
        Path shape = rounded ? Path.RoundRect(new RoundRect(area, 6.5f)) : Path.Rect(area);
        ChartFormat format = chart.Format;
        if (Fill(format, area, palette.IsDarkStyle ? palette.Dark : palette.Light) is { } fill)
        {
            canvas.FillPath(shape, fill);
        }

        if (LineOf(format, palette.AutomaticLine, 0.75f) is { } line)
        {
            // The frame sits on the chart's edge: half of it would clip, so it comes in by that half.
            RectF inset = new(line.Stroke.Width / 2, line.Stroke.Width / 2, width - line.Stroke.Width, height - line.Stroke.Width);
            canvas.StrokePath(rounded ? Path.RoundRect(new RoundRect(inset, 6.5f)) : Path.Rect(inset), line.Stroke, line.Color);
        }
    }

    private void DrawPlotBackground(ChartBoxes boxes)
    {
        ChartFormat format = chart.PlotAreaFormat;
        if (Fill(format, boxes.Plot, NoColor) is { } fill && !IsRound())
        {
            canvas.FillRect(boxes.Plot, fill);
        }

        if (LineOf(format, NoColor, 0.75f) is { } line && !IsRound())
        {
            canvas.StrokeRect(boxes.Plot, line.Stroke, line.Color);
        }

        bool IsRound() => plots.All(p => p.Type is ChartType.Pie or ChartType.Doughnut or ChartType.PieOfPie or ChartType.BarOfPie);
    }

    /// <summary>A title's lines: four fifths of the chart wide and about a third of it high, with an ellipsis for what is left out.</summary>
    private string TitleLines(string title, Look look)
    {
        float limit = Math.Max(width * TitleWidthShare, 1);
        int most = Math.Max((int)(height * TitleHeightShare / Math.Max(Measure("0", look).Height, 1)), 1);
        return CutTo(Wrapped(title, look, limit), most, look, limit);
    }

    private void DrawTitle(ChartBoxes boxes)
    {
        if (boxes.Title is not { } box || TitleText() is not { } text)
        {
            return;
        }

        ChartFormat format = chart.Title.Format;
        if (Fill(format, box, NoColor) is { } fill)
        {
            canvas.FillRect(box, fill);
        }

        if (LineOf(format, NoColor, 0.75f) is { } line)
        {
            canvas.StrokeRect(box, line.Stroke, line.Color);
        }

        Look look = TitleLook();
        string lines = TitleLines(text, look);

        // A cut title stays centred as if its ellipsis were not there.
        float past = lines.EndsWith('…') && (chart.Title.TextFormat.Rotation ?? 0) == 0 ? Measure("…", look).Width / 2 : 0;
        DrawTextIn(lines, look, new RectF(box.X + past, box.Y, box.Width, box.Height), chart.Title.TextFormat.Rotation);
    }

    /// <summary>Text drawn in the middle of a box, turned by <paramref name="rotation"/> degrees clockwise.</summary>
    private void DrawTextIn(string text, Look look, RectF box, double? rotation)
    {
        if (text.Length == 0)
        {
            return;
        }

        float angle = (float)(rotation ?? 0);
        TextLayout layout = angle == 0 ? Lay(text, look, box.Width + 1, TextAlign.Center) : Lay(text, look, 1e5f, TextAlign.Left);
        if (angle == 0)
        {
            canvas.DrawTextLayout(layout, new Vector2(box.X, box.Y + ((box.Height - layout.Height) / 2)));
            return;
        }

        canvas.Save();
        canvas.Translate(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        canvas.Rotate(angle);
        canvas.DrawTextLayout(layout, new Vector2(-layout.LongestLine / 2, -layout.Height / 2));
        canvas.Restore();
    }

    private void DrawTextAt(string text, Look look, Vector2 corner)
    {
        if (text.Length > 0)
        {
            canvas.DrawTextLayout(Lay(text, look, 1e5f, TextAlign.Left), corner);
        }
    }

    private const float TitleWidthShare = 0.8f;
    private const float TitleHeightShare = 0.35f;

    /// <summary>What a piece of chart text looks like.</summary>
    private readonly record struct Look(string Font, float Size, bool Bold, bool Italic, Rgba8 Color);

    private Look TitleLook() => LookOf(chart.Title.TextFormat, baseSizeSet || (word && chart.HasTitle && chart.Title.Text is { Length: > 0 } && !plots.Any(IsThreeD)) ? (baseSizeSet ? baseSize * 1.2f : baseSize) : baseSize * 1.8f, true);

    private Look LookOf(ChartTextFormat format, float size, bool bold = false)
    {
        ChartTextFormat all = chart.TextFormat;
        string font = format.FontName ?? all.FontName ?? "+mn-lt";
        font = font switch
        {
            "+mn-lt" or "+mn-ea" or "+mn-cs" => theme.MinorLatinFont,
            "+mj-lt" or "+mj-ea" or "+mj-cs" => theme.MajorLatinFont,
            _ => font,
        };

        // A file can name no font at all, in the chart or in its theme.
        if (string.IsNullOrWhiteSpace(font))
        {
            font = string.IsNullOrWhiteSpace(theme.MinorLatinFont) ? "Calibri" : theme.MinorLatinFont;
        }

        double? asked = format.Size;
        Rgba8 color = format.Color is { } own ? palette.Resolve(own) : all.Color is { } shared ? palette.Resolve(shared) : palette.IsDarkStyle ? palette.Light : palette.Dark;
        return new Look(
            font,
            asked is > 0 and < 1000 ? (float)asked.Value : size,
            format.Bold ?? all.Bold ?? bold,
            format.Italic ?? all.Italic ?? false,
            color);
    }

    private TextLayout Lay(string text, Look look, float wrap, TextAlign align)
    {
        FontStyle style = (look.Bold, look.Italic) switch { (true, true) => FontStyle.BoldItalic, (true, false) => FontStyle.Bold, (false, true) => FontStyle.Italic, _ => FontStyle.Normal };
        TextStyle textStyle = new()
        {
            FontFamilies = fonts.ResolveFamily(look.Font, style) is { } family ? [family] : null,
            Size = look.Size,
            FontStyle = style,
            Color = look.Color,
        };
        Egami.Fonts.Layout.ParagraphStyle paragraph = new() { DefaultStyle = textStyle, Align = align, Fallback = fonts.Fallback };
        return Egami.Fonts.Layout.Paragraph.Create(text, paragraph).Layout(Math.Max(1, wrap));
    }

    private (float Width, float Height) Measure(string text, Look look, float wrap = 1e5f)
    {
        if (text.Length == 0)
        {
            return (0, 0);
        }

        TextLayout layout = Lay(text, look, wrap, TextAlign.Left);

        // Layout rounds a line's metrics to whole units, as browsers do; Excel keeps the font's own.
        return (layout.LongestLine, layout.Lines.Count * LineHeight(look));
    }

    private float LineHeight(Look look)
    {
        FontStyle style = (look.Bold, look.Italic) switch { (true, true) => FontStyle.BoldItalic, (true, false) => FontStyle.Bold, (false, true) => FontStyle.Italic, _ => FontStyle.Normal };
        return new Font(fonts.Resolve(look.Font, style).Typeface, look.Size).LineHeight;
    }

    /// <summary>A number as the format code shows it; the code's own words for what it cannot show.</summary>
    private string Number(double value, string? code)
    {
        if (!double.IsFinite(value))
        {
            return "";
        }

        FormatSection[] sections = Formats.GetOrAdd(string.IsNullOrEmpty(code) ? "General" : LocalCode(code), c =>
        {
            if (Formats.Count > 1024)
            {
                Formats.Clear();
            }

            return FormatParser.Parse(c);
        });
        (FormatSection? section, bool keepSign) = FormatSections.Choose(sections, value);
        if (section is null)
        {
            return new string('#', 10);
        }

        string text = NumberFormatter.Format(section, keepSign ? value : Math.Abs(value), section.Culture ?? culture, chart.Date1904);
        int mark = text.IndexOf(FormatSections.FillMark, StringComparison.Ordinal);
        while (mark >= 0)
        {
            text = text.Remove(mark, Math.Min(2, text.Length - mark));
            mark = text.IndexOf(FormatSections.FillMark, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>A format code that stands for the machine's own way of writing it, in the culture's: Excel's short date, and a percent's spacing.</summary>
    private string LocalCode(string code)
    {
        if (culture.Equals(CultureInfo.InvariantCulture))
        {
            return code;
        }

        if (code is "m/d/yyyy" or "mm-dd-yy")
        {
            return culture.DateTimeFormat.ShortDatePattern.ToLowerInvariant();
        }

        return culture.NumberFormat.PercentPositivePattern == 0 && code is "0%" or "0.0%" or "0.00%" ? code[..^1] + "\\ %" : code;
    }

    /// <summary>The paint for a fill; null when it draws nothing. Automatic takes <paramref name="automatic"/>, which is empty when automatic is nothing.</summary>
    private Paint? Fill(ChartFormat format, RectF box, Rgba8 automatic)
    {
        switch (format.FillKind)
        {
            case ChartFillKind.None:
                return null;
            case ChartFillKind.Solid when format.FillColor is { } color:
                return new SolidPaint(palette.Resolve(color));
            case ChartFillKind.Gradient:
                {
                    GradientStop[] stops = [.. format.GradientStops.Where(s => s.Color is not null).OrderBy(s => s.Position)
                        .Select(s => new GradientStop((float)Math.Clamp(s.Position, 0, 1), palette.Resolve(s.Color!)))];
                    if (stops.Length == 0)
                    {
                        break;
                    }

                    if (stops.Length == 1)
                    {
                        return new SolidPaint(stops[0].Color);
                    }

                    (Vector2 start, Vector2 end) = DrawingFills.LinearEnds(box.X, box.Y, box.Width, box.Height, format.GradientAngle ?? 90);
                    return new LinearGradient(start, end, stops);
                }

            case ChartFillKind.Pattern:
                {
                    // A hatch comes out at a page's size as its two inks' blend, by how much of the pattern is ink.
                    Rgba8 ink = format.PatternForeground is { } foreground ? palette.Resolve(foreground) : palette.Dark;
                    Rgba8 paper = format.PatternBackground is { } background ? palette.Resolve(background) : palette.Light;
                    return new SolidPaint(ChartPalette.Mix(paper, ink, DrawingFills.PatternInk(format.PatternName)));
                }

            case ChartFillKind.Automatic:
                break;
        }

        return automatic.A == 0 ? null : new SolidPaint(automatic);
    }

    private (Stroke Stroke, Rgba8 Color)? LineOf(ChartFormat format, Rgba8 automatic, float automaticWidth)
    {
        Rgba8 color;
        switch (format.LineKind)
        {
            case ChartLineKind.None:
                return null;
            case ChartLineKind.Solid when format.LineColor is { } own:
                color = palette.Resolve(own);
                break;
            default:
                if (format.LineColor is { } some)
                {
                    color = palette.Resolve(some);
                }
                else if (automatic.A != 0)
                {
                    color = automatic;
                }
                else
                {
                    return null;
                }

                break;
        }

        float lineWidth = format.LineWidth is >= 0 and < 2000 ? (float)format.LineWidth.Value : automaticWidth;
        lineWidth = Math.Max(lineWidth, 0.25f);
        Stroke stroke = new(lineWidth);
        if (Dash(format.LineDash, lineWidth) is { } dash)
        {
            stroke = stroke with { Dash = dash };
        }

        return (stroke, color);
    }

    private static DashPattern? Dash(ChartLineDash? dash, float w) =>
        DrawingFills.Dash(ChartFormat.DashPreset(dash), w) is { } pattern ? new DashPattern(pattern, 0) : null;
}
