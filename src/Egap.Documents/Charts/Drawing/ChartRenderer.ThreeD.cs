using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.Charts;

/// <summary>
/// How a 3D chart's box lands on the page. X runs along the front to the right, Y up and Z to the back. With right-angle axes the front stays a
/// rectangle and depth goes off at a slant; otherwise the box turns and tilts and comes nearer with perspective. Either way it fits the plot's box.
/// </summary>
internal sealed class ChartProjection
{
    private readonly bool rightAngle;
    private readonly double sinY;
    private readonly double cosY;
    private readonly double sinX;
    private readonly double cosX;
    private readonly double distance;
    private readonly float scaleX;
    private readonly float scaleY;
    private readonly float shiftX;
    private readonly float shiftY;

    public ChartProjection(bool rightAngle, double rotX, double rotY, double perspective, float width, float height, float depth, RectF box)
    {
        this.rightAngle = rightAngle;
        Width = width;
        Height = height;
        Depth = depth;
        sinY = Math.Sin(rotY * Math.PI / 180);
        cosY = Math.Cos(rotY * Math.PI / 180);
        sinX = Math.Sin(rotX * Math.PI / 180);
        cosX = Math.Cos(rotX * Math.PI / 180);
        double reach = perspective > 0 ? Math.Tan(Math.Clamp(perspective, 1, 200) * ChartRenderer.PerspectiveShare * Math.PI / 180) : 0;
        distance = reach > 0 ? width * ChartRenderer.PerspectiveDistance / reach : 0;
        float left = float.MaxValue;
        float right = float.MinValue;
        float top = float.MaxValue;
        float bottom = float.MinValue;
        foreach (float x in new[] { 0, width })
        {
            foreach (float y in new[] { 0, height })
            {
                foreach (float z in new[] { 0, depth })
                {
                    Vector2 at = Raw(x, y, z);
                    left = Math.Min(left, at.X);
                    right = Math.Max(right, at.X);
                    top = Math.Min(top, at.Y);
                    bottom = Math.Max(bottom, at.Y);
                }
            }
        }

        float wide = Math.Max(right - left, 1e-3f);
        float high = Math.Max(bottom - top, 1e-3f);
        float across = box.Width / wide;
        float down = box.Height / high;
        scaleX = Math.Min(across, down);
        scaleY = scaleX;
        shiftX = box.Left + ((box.Width - (wide * scaleX)) / 2) - (left * scaleX);
        shiftY = box.Top + ((box.Height - (high * scaleY)) / 2) - (top * scaleY);
    }

    public bool IsRightAngle => rightAngle;

    public float Width { get; }

    public float Height { get; }

    public float Depth { get; }

    /// <summary>Whether the eye is on the right of the box, looking at the sides that face right.</summary>
    public bool FromRight => sinY >= 0;

    /// <summary>Whether the eye is on the front of the box, which is where Z starts.</summary>
    public bool FromFront => cosY >= 0;

    /// <summary>Whether the eye is above the box.</summary>
    public bool FromAbove => sinX >= 0;

    public Vector2 At(float x, float y, float z)
    {
        Vector2 raw = Raw(x, y, z);
        return new Vector2((raw.X * scaleX) + shiftX, (raw.Y * scaleY) + shiftY);
    }

    /// <summary>How far a point is from the eye: larger is further, and draws first.</summary>
    public float Far(float x, float y, float z)
    {
        double back = (z * cosY) - (x * sinY);
        return (float)((back * cosX) - (y * sinX));
    }

    private Vector2 Raw(float x, float y, float z)
    {
        if (rightAngle)
        {
            return new Vector2((float)(x + (z * sinY)), (float)(-y - (z * sinX)));
        }

        // The box turns about its own middle.
        double cx = x - (Width / 2.0);
        double cy = y - (Height / 2.0);
        double cz = z - (Depth / 2.0);
        double px = (cx * cosY) + (cz * sinY);
        double back = (cz * cosY) - (cx * sinY);
        double py = (cy * cosX) + (back * sinX);
        double far = (back * cosX) - (cy * sinX);
        double near = distance > 0 ? distance / Math.Max(distance + far, distance * 0.05) : 1;
        return new Vector2((float)(px * near), (float)(-py * near));
    }
}

/// <content>3D charts: the box, its walls and gridlines, and bars, areas, lines and surfaces in it; 3D pies; pies with a second plot.</content>
internal sealed partial class ChartRenderer
{
    internal const double PerspectiveShare = 0.5;
    internal const double PerspectiveDistance = 0.6;
    private const float FrontShade = 1f;
    private const float TopShade = 0.76f;
    private const float SideShade = 0.64f;
    private const int ConeSides = 24;
    private const float BoxOrder = 1000;

    private static bool IsThreeD(ChartPlot plot) => plot.Is3D || plot.Type == ChartType.Surface;

    /// <summary>One flat piece of the scene: its corners on the page, its colour, how far it is and its outline.</summary>
    private readonly record struct Facet(Vector2[] Corners, Rgba8 Color, float Far, int Order, Rgba8 Outline = default, float OutlineWidth = 0);

    private void DrawThreeDOrPair(ChartBoxes boxes)
    {
        foreach (ChartPlot plot in plots)
        {
            if (plot.Type is ChartType.PieOfPie or ChartType.BarOfPie)
            {
                DrawPieOfPie(plot, roundRoom);
            }
            else if (plot.Type == ChartType.Pie && plot.Is3D)
            {
                DrawPie3D(plot, roundRoom);
            }
        }

        if (plots.Any(p => IsThreeD(p) && p.Type != ChartType.Pie))
        {
            DrawThreeD(boxes);
        }
    }

    private void DrawThreeD(ChartBoxes boxes)
    {
        ChartPlot plot = plots.First(p => IsThreeD(p) && p.Type != ChartType.Pie);
        ChartView3D view = chart.View3D;
        bool rightAngle = view.RightAngleAxes ?? (view.Perspective is null && plot.Type is ChartType.Column or ChartType.Bar);
        if (Pair(boxes, plot) is not { } pair)
        {
            return;
        }

        (DrawnAxis across, DrawnAxis up) = pair;
        bool sideways = plot.Type == ChartType.Bar;
        ChartAxis? seriesAxis = plot.Axes.FirstOrDefault(a => a.Kind == ChartAxisKind.Series);
        List<ChartSeries> series = Ordered(plot);
        ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Standard;
        bool standard = grouping == ChartGrouping.Standard && plot.Type is not (ChartType.Column or ChartType.Bar)
            || (plot.Type is ChartType.Column or ChartType.Bar && grouping == ChartGrouping.Standard);
        bool stacked = grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
        bool hasDepthAxis = seriesAxis is not null && !seriesAxis.Hidden && series.Count > 0 && (standard || plot.Type == ChartType.Surface);
        bool surface = plot.Type == ChartType.Surface;
        int rows = surface ? Math.Max(series.Count - 1, 1) : standard ? Math.Max(series.Count, 1) : 1;
        float depthShare = (float)Math.Clamp((view.DepthPercent ?? 100) / 100.0, 0.2, 20);
        float gapDepth = surface ? 0 : (float)Math.Clamp((plot.GapDepth ?? 150) / 100.0, 0, 5);
        float gapWidth = (float)Math.Clamp((plot.GapWidth ?? 150) / 100.0, 0, 5);
        int slots = stacked || standard ? 1 : Math.Max(series.Count, 1);
        int cells = Math.Max(across.Between ? across.Count : across.Count - 1, 1);
        double overlap = Math.Clamp((plot.Overlap ?? (stacked ? 100 : 0)) / 100.0, -1, 1);
        bool isBars = plot.Type is ChartType.Column or ChartType.Bar;

        // Depth comes from a bar's width, or for a plot without bars from a category's.
        float cellShare = 1f / cells;
        float barShare = isBars ? (float)(cellShare / (slots - ((slots - 1) * overlap) + gapWidth)) : cellShare;
        // Without bars, a series takes one category's share of the width, whichever way categories sit on their axis.
        int columns = Math.Max(across.Count, 1);
        float depthOfLength = isBars ? barShare * depthShare * (1 + gapDepth) * rows : (surface ? series.Count : rows) * depthShare / columns;
        double rotX = view.RotationX ?? 15;
        double rotY = view.RotationY ?? 20;
        bool top = surface && Math.Abs(rotX) >= 89.5;
        Look axisLook = AxisLook(across);
        float seriesRoom = hasDepthAxis && !top ? series.Max(s => Measure(SeriesName(s), axisLook).Width) + LabelMargin : 0;
        ChartProjection Build()
        {
            float widestLeft = sideways
                ? (LabelsShown(across) && across.Labels.Count > 0 ? across.Labels.Max(l => Measure(l, axisLook).Width) : 0)
                : (LabelsShown(up) && !top && up.Labels.Count > 0 ? up.Labels.Max(l => Measure(l, AxisLook(up)).Width) : 0);
            float marginLeft = rightAngle ? (sideways ? BarLeft3D : ColumnLeft3D) : TurnedLeft3D;
            float titleLeft = (sideways ? across : up).TitleBox?.Width ?? 0;
            float left = roomLeft + marginLeft + widestLeft + titleLeft + (rightAngle ? LabelGap3D : LabelGapTurned3D);
            float right = roomRight - (rightAngle ? (sideways ? BarRight3D : ColumnRight3D) : TurnedRight3D) - seriesRoom;
            float upper = boxes.Plot.Top + (rightAngle ? Top3D : boxes.Title is null ? -TurnedUntitledTop3D : 0);
            RectF room = RectF.FromEdges(left, upper, Math.Max(right, left + 1), Math.Max(boxes.Plot.Bottom - (rightAngle ? Bottom3D : 0) + (across.Rotation != 0 ? TurnedLabelRoom3D : 0), upper + 1));
            if (!rightAngle && HandPlaced3D() is { } placed)
            {
                room = placed;
            }

            float length;
            float height = 100;
            float acrossLength;
            if (rightAngle)
            {
                float slantX = Math.Abs((float)Math.Sin(rotY * Math.PI / 180));
                float slantY = Math.Abs((float)Math.Sin(rotX * Math.PI / 180));
                if (sideways)
                {
                    length = Math.Max(room.Height / (1 + (depthOfLength * slantY)), 1);
                    acrossLength = Math.Max(room.Width - (depthOfLength * length * slantX), 1);
                    height = length;
                }
                else
                {
                    length = Math.Max(room.Width / (1 + (depthOfLength * slantX)), 1);
                    height = Math.Max(room.Height - (depthOfLength * length * slantY), 1);
                    acrossLength = length;
                }
            }
            else
            {
                length = 100;
                acrossLength = 100;
                float deep = Math.Max(depthOfLength * length, 0.5f);
                double turned = view.Perspective ?? 30;
                if (view.HeightPercent is > 0)
                {
                    height = (float)Math.Clamp(view.HeightPercent.Value, 5, 500);
                }
                else
                {
                    // With no height set the box's height goes by the shape of its room.
                    height = 100 * Math.Max(TurnedHeight3D * room.Height / room.Width, TurnedLeastHeight3D);
                }

                return new ChartProjection(false, rotX, rotY, turned, 100, height, deep, room);
            }

            float depth = Math.Max(depthOfLength * length, 0.5f);
            return new ChartProjection(true, rotX, rotY, 0, acrossLength, sideways ? length : height, depth, room);
        }

        // The scale's step goes by how long its axis comes out, and its labels take room from that length.
        ChartProjection projection = Build();
        for (int pass = 0; pass < 3; pass++)
        {
            Vector2 start = projection.At(0, 0, 0);
            Vector2 end = sideways ? projection.At(projection.Width, 0, 0) : projection.At(0, projection.Height, 0);
            float long3 = top ? boxes.Plot.Height * SurfaceFlatScale : Math.Abs(end.Y - start.Y) + Math.Abs(end.X - start.X) + 1;
            if (surface && !top && Math.Abs(rotX) >= 20)
            {
                // Excel takes a coarser scale for a surface it tilts further.
                long3 *= SurfaceTiltedScale;
            }

            if (HandPlaced3D() is not null)
            {
                long3 *= PlacedAxisShare;
            }

            Scale(up, new RectF(0, 0, long3, long3));

            // The labels along the front go by how wide the categories come out on the page, and may turn or wrap.
            Vector2 frontEnd = sideways ? projection.At(0, projection.Height, 0) : projection.At(projection.Width, 0, 0);
            float front = Math.Abs(frontEnd.X - start.X) + Math.Abs(frontEnd.Y - start.Y);
            float shown = (across.Between || across.Count < 2 ? front : front * (across.Count - 1) / across.Count) * LabelBandShare3D;
            MeasureLabels(across, new RectF(0, 0, shown, shown));
            MeasureLabels(up, new RectF(0, 0, front, front));
            boxes.Plot = PlotFrom(boxes, roomLeft, roomTop, roomRight, roomBottom);
            projection = Build();
        }

        if (surface)
        {
            surfaceAxis = up;
        }

        canvas.Save();
        DrawWalls(boxes, projection, across, up, sideways, top);
        List<Facet> facets = [];
        switch (plot.Type)
        {
            case ChartType.Column or ChartType.Bar:
                BarFacets(plot, series, across, up, projection, sideways, standard, rows, facets);
                break;
            case ChartType.Area:
                AreaFacets(plot, series, across, up, projection, standard, rows, gapDepth, facets);
                break;
            case ChartType.Line:
                LineFacets(series, across, up, projection, standard, rows, gapDepth, facets);
                break;
            case ChartType.Surface:
                SurfaceFacets(plot, series, across, up, seriesAxis, projection, facets);
                break;
        }

        foreach (Facet facet in facets.OrderByDescending(f => f.Far).ThenBy(f => f.Order))
        {
            Path path = Path.Polygon(facet.Corners, close: true);
            if (facet.Color.A != 0)
            {
                canvas.FillPath(path, new SolidPaint(facet.Color));
            }

            if (facet.OutlineWidth > 0)
            {
                canvas.StrokePath(path, new Stroke(facet.OutlineWidth), facet.Outline);
            }
        }

        DrawAxes3D(projection, across, up, series, hasDepthAxis, sideways, top);
        DrawAxisTitle(across);
        DrawAxisTitle(up);
        DrawDataTable(plot, series, across);
        canvas.Restore();
    }

    private const float TurnedHeight3D = 0.7f;
    private const float TurnedLeastHeight3D = 0.2f;
    private const float LabelBandShare3D = 0.8f;
    private const float TurnedLabelRoom3D = 6;
    private const float TurnedUntitledTop3D = 4;
    private const float TurnedLeft3D = 28;
    private const float TurnedRight3D = 20;
    private const float PlacedAxisShare = 0.5f;
    private const float TablePlacedLeft = 13;
    private const float TablePlacedRight = 4;
    private const float TablePlacedGap = 7;
    private const float TableInset = 11;
    private const float TableRowShare = 1.22f;
    private const float TableKey = 6.5f;
    private const float TableKeyGap = 3;

    private XElement? DataTableOf() => plots.Select(p => p.Element.Parent?.Element(ChartNs.C + "dTable")).FirstOrDefault(e => e is not null);

    /// <summary>How much of the chart's height a 3D chart's data table takes: its rows and the margin under them.</summary>
    /// <summary>Where the part places a 3D plot's inner box by hand; the box fits in it, and its labels go outside.</summary>
    private RectF? HandPlaced3D() =>
        chart.PlotAreaLayout.InnerPlot == true ? ManualPlot(new RectF(0, 0, width, height)) : null;

    private float DataTableRoom()
    {
        if (DataTableOf() is null || plots.FirstOrDefault(IsThreeD) is not { } plot)
        {
            return 0;
        }

        return ((plot.Series.Count + 1) * TableRow()) + TableInset;
    }

    private float TableRow() => Measure("0", LookOf(chart.TextFormat, baseSize)).Height * TableRowShare;

    /// <summary>A 3D chart's data table: a row of categories over a row for each series, the width of the chart.</summary>
    private void DrawDataTable(ChartPlot plot, List<ChartSeries> series, DrawnAxis across)
    {
        if (DataTableOf() is not { } table || across.Count == 0)
        {
            return;
        }

        static bool On(XElement table, string name) => (string?)table.Element(ChartNs.C + name)?.Attribute("val") is "1" or "true";
        Look look = LookOf(chart.TextFormat, baseSize);
        float row = TableRow();
        bool keys = On(table, "showKeys");
        float keyRoom = series.Max(s => Measure(SeriesName(s), look).Width) + (keys ? TableKey + (2 * TableKeyGap) : 0) + (TableKeyGap / 2);
        float left = TableInset;
        float right = width - TableInset;
        float bottom = height - TableInset;
        float top = bottom - ((series.Count + 1) * row);
        float first = left + keyRoom;
        if (HandPlaced3D() is { } placed)
        {
            // Under a plot placed by hand the table follows the plot, not the chart's edges.
            first = placed.X + TablePlacedLeft;
            left = first - keyRoom;
            right = placed.Right - TablePlacedRight;
            top = placed.Bottom + TablePlacedGap;
            bottom = top + ((series.Count + 1) * row);
        }

        float each = (right - first) / across.Count;
        float capDrop = (row + (look.Size * 0.68f)) / 2;
        (Stroke stroke, Rgba8 ink) = (new Stroke(0.75f), palette.AutomaticLine);
        if (On(table, "showOutline"))
        {
            canvas.DrawLine(new Vector2(first, top), new Vector2(right, top), stroke, ink);
            canvas.DrawLine(new Vector2(left, top + row), new Vector2(left, bottom), stroke, ink);
            canvas.DrawLine(new Vector2(left, bottom), new Vector2(right, bottom), stroke, ink);
            canvas.DrawLine(new Vector2(right, top), new Vector2(right, bottom), stroke, ink);
        }

        for (int i = 0; i < across.Count && i < across.Labels.Count; i++)
        {
            TextAt(across.Labels[i], look, first + ((i + 0.5f) * each), top + capDrop, Egami.Fonts.Layout.TextAlign.Center);
            if (On(table, "showVertBorder"))
            {
                canvas.DrawLine(new Vector2(first + (i * each), top), new Vector2(first + (i * each), bottom), stroke, ink);
            }
        }

        for (int k = 0; k < series.Count; k++)
        {
            float y = top + ((k + 1) * row);
            if (On(table, "showHorzBorder"))
            {
                canvas.DrawLine(new Vector2(left, y), new Vector2(right, y), stroke, ink);
            }

            float x = left + TableKeyGap;
            if (keys)
            {
                canvas.FillRect(new RectF(x, y + ((row - TableKey) / 2), TableKey, TableKey), new SolidPaint(SolidColor(series[k].Format, SeriesColor(series[k]))));
                x += TableKey + TableKeyGap;
            }

            TextAt(SeriesName(series[k]), look, x, y + capDrop, Egami.Fonts.Layout.TextAlign.Left);
            IReadOnlyList<double?> values = series[k].Values.Numbers;
            for (int i = 0; i < across.Count && i < values.Count; i++)
            {
                if (values[i] is { } value)
                {
                    TextAt(TableNumber(value), look, first + ((i + 0.5f) * each), y + capDrop, Egami.Fonts.Layout.TextAlign.Center);
                }
            }
        }
    }

    private const float Tick3D = 4;
    /// <summary>A number as General shows it in a table cell: as many digits as fit in eleven characters.</summary>
    private string TableNumber(double value)
    {
        for (int digits = 11; digits > 1; digits--)
        {
            string text = value.ToString("G" + digits.ToString(CultureInfo.InvariantCulture), culture);
            if (text.Length <= 11)
            {
                return text;
            }
        }

        return value.ToString("G1", culture);
    }

    private const float SurfaceAxisShare = 0.45f;
    private const float SurfaceTiltedScale = 0.6f;
    private const float SurfaceFlatScale = 0.75f;
    private const float ColumnLeft3D = 21;
    private const float BarLeft3D = 13.5f;
    private const float LabelGap3D = 10;
    private const float LabelGapTurned3D = 7;
    private const float ColumnRight3D = 25;
    private const float BarRight3D = 22.5f;
    private const float Top3D = 3;
    private const float Bottom3D = 2.25f;

    private (Stroke Stroke, Rgba8 Color) GridInk(DrawnAxis axis) =>
        LineOf(axis.Model.MajorGridlines, palette.AutomaticLine, 0.75f) ?? (new Stroke(0.75f), palette.AutomaticLine);

    private void DrawWalls(ChartBoxes boxes, ChartProjection view, DrawnAxis across, DrawnAxis up, bool sideways, bool top)
    {
        float w = view.Width;
        float h = view.Height;
        float d = view.Depth;
        float wallZ = view.FromFront ? d : 0;
        float nearZ = d - wallZ;
        float wallX = view.FromRight ? 0 : w;
        float floorY = view.FromAbove ? 0 : h;
        Wall(chart.BackWallFormat, [view.At(0, 0, wallZ), view.At(w, 0, wallZ), view.At(w, h, wallZ), view.At(0, h, wallZ)]);
        Wall(chart.SideWallFormat, [view.At(wallX, 0, 0), view.At(wallX, 0, d), view.At(wallX, h, d), view.At(wallX, h, 0)]);
        Wall(chart.FloorFormat, [view.At(0, floorY, 0), view.At(w, floorY, 0), view.At(w, floorY, d), view.At(0, floorY, d)]);
        if (top)
        {
            return;
        }

        if (up.Model.HasMajorGridlines)
        {
            (Stroke stroke, Rgba8 color) = GridInk(up);
            foreach (double tick in up.Scale.Ticks())
            {
                float place = (float)PlaceOn(up, tick);
                if (sideways)
                {
                    float x = place * w;
                    canvas.DrawLine(view.At(x, floorY, nearZ), view.At(x, floorY, wallZ), stroke, color);
                    canvas.DrawLine(view.At(x, 0, wallZ), view.At(x, h, wallZ), stroke, color);
                }
                else
                {
                    float y = place * h;
                    canvas.DrawLine(view.At(wallX, y, nearZ), view.At(wallX, y, wallZ), stroke, color);
                    canvas.DrawLine(view.At(0, y, wallZ), view.At(w, y, wallZ), stroke, color);
                }
            }
        }

        if (across.Model.HasMajorGridlines && across.Count > 0)
        {
            (Stroke stroke, Rgba8 color) = GridInk(across);
            int count = across.Between ? across.Count : across.Count - 1;
            for (int i = 0; i <= count; i++)
            {
                float share = i / (float)Math.Max(count, 1);
                if (sideways)
                {
                    canvas.DrawLine(view.At(wallX, share * h, nearZ), view.At(wallX, share * h, wallZ), stroke, color);
                    canvas.DrawLine(view.At(0, share * h, wallZ), view.At(w, share * h, wallZ), stroke, color);
                }
                else
                {
                    canvas.DrawLine(view.At(share * w, floorY, nearZ), view.At(share * w, floorY, wallZ), stroke, color);
                    canvas.DrawLine(view.At(share * w, 0, wallZ), view.At(share * w, h, wallZ), stroke, color);
                }
            }
        }

        void Wall(ChartFormat format, Vector2[] corners)
        {
            RectF box = RectF.FromEdges(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
            Path path = Path.Polygon(corners, close: true);
            if (Fill(format, box, NoColor) is { } fill)
            {
                canvas.FillPath(path, fill);
            }

            if (LineOf(format, NoColor, 0.75f) is { } line)
            {
                canvas.StrokePath(path, line.Stroke, line.Color);
            }
        }
    }

    private static double PlaceOn(DrawnAxis axis, double value)
    {
        double place = Math.Clamp(axis.Scale.Place(value), 0, 1);
        return axis.Model.Reversed ? 1 - place : place;
    }

    private static double CategoryOn(DrawnAxis axis, int index)
    {
        double place = axis.CategoryPlace(index);
        return axis.Model.Reversed ? 1 - place : place;
    }

    private static Rgba8 Shade(Rgba8 color, float share) => new(
        (byte)Math.Clamp(color.R * share, 0, 255),
        (byte)Math.Clamp(color.G * share, 0, 255),
        (byte)Math.Clamp(color.B * share, 0, 255),
        color.A);

    /// <summary>A face's shade from which way it looks: full from the front, less from above and least from the side.</summary>
    private static float ShadeOf(Vector3 normal)
    {
        float size = normal.LengthSquared();
        if (!(size > 0))
        {
            return FrontShade;
        }

        return ((normal.Z * normal.Z * FrontShade) + (normal.Y * normal.Y * TopShade) + (normal.X * normal.X * SideShade)) / size;
    }

    private Rgba8 SolidColor(ChartFormat format, Rgba8 automatic)
    {
        if (format.FillKind == ChartFillKind.None)
        {
            return NoColor;
        }

        if (format.FillKind == ChartFillKind.Gradient && format.GradientStops.FirstOrDefault(s => s.Color is not null) is { Color: { } first })
        {
            return palette.Resolve(first);
        }

        return format.FillColor is { } own ? palette.Resolve(own) : automatic;
    }

    private void BarFacets(ChartPlot plot, List<ChartSeries> series, DrawnAxis across, DrawnAxis up, ChartProjection view, bool sideways, bool standard, int rows, List<Facet> facets)
    {
        if (series.Count == 0 || across.Count == 0)
        {
            return;
        }

        ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Clustered;
        bool stacked = grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
        bool percent = grouping == ChartGrouping.PercentStacked;
        int slots = stacked || standard ? 1 : series.Count;
        double overlap = Math.Clamp((plot.Overlap ?? (stacked ? 100 : 0)) / 100.0, -1, 1);
        double gap = Math.Clamp((plot.GapWidth ?? 150) / 100.0, 0, 5);
        float gapDepth = (float)Math.Clamp((plot.GapDepth ?? 150) / 100.0, 0, 5);
        float along = sideways ? view.Height : view.Width;
        float along2 = sideways ? view.Width : view.Height;
        float band = along / Math.Max(across.Between ? across.Count : across.Count - 1, 1);
        float bar = (float)(band / (slots - ((slots - 1) * overlap) + gap));
        float total = (float)(bar * (slots - ((slots - 1) * overlap)));
        float cell = view.Depth / rows;
        float thick = cell / (1 + gapDepth);
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

        double baseline = up.Scale.Minimum <= 0 && up.Scale.Maximum >= 0 ? 0 : up.Scale.Minimum > 0 ? up.Scale.Minimum : up.Scale.Maximum;
        bool vary = plot.VaryColors == true && series.Count == 1;
        ChartBarShape shapeOfPlot = plot.BarShape ?? ChartBarShape.Box;
        for (int k = 0; k < series.Count; k++)
        {
            ChartSeries s = series[k];
            IReadOnlyList<double?> values = s.Values.Numbers;
            Rgba8 own = SeriesColor(s);
            int slot = stacked || standard ? 0 : k;
            float z = (standard ? (k + 0.5f) * cell : cell / 2) - (thick / 2);
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not { } raw || !double.IsFinite(raw))
                {
                    continue;
                }

                double value = percent && sums[i] > 0 ? raw / sums[i] : raw;
                double from = baseline;
                double to = value;
                if (stacked)
                {
                    from = value >= 0 ? positive[i] : negative[i];
                    to = from + value;
                    if (value >= 0)
                    {
                        positive[i] = to;
                    }
                    else
                    {
                        negative[i] = to;
                    }
                }

                float a = (float)PlaceOn(up, from) * along2;
                float b = (float)PlaceOn(up, to) * along2;
                float middle = ((float)CategoryOn(across, i) * along) + ((float)(slot * bar * (1 - overlap)) + (bar / 2) - (total / 2));
                float start = middle - (bar / 2);
                ChartPoint? point = PointOf(s, i);
                ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
                Rgba8 color = SolidColor(format, vary ? PointColor(i, values.Count) : own);
                ChartBarShape shape = ChartPlot.ShapeOf(s) ?? shapeOfPlot;
                float tip = shape is ChartBarShape.ConeToMax or ChartBarShape.PyramidToMax ? along2 : b;
                if (shape is ChartBarShape.Box)
                {
                    Box(start, Math.Min(a, b), z, bar, Math.Abs(a - b), thick, color, view, sideways, facets, k);
                }
                else
                {
                    Round(start, a, b, tip, z, bar, thick, color, shape, view, sideways, facets, k);
                }
            }
        }
    }

    private static void Box(float start, float low, float z, float bar, float rise, float thick, Rgba8 color, ChartProjection view, bool sideways, List<Facet> facets, int order)
    {
        (float x0, float x1, float y0, float y1) = sideways ? (low, low + rise, start, start + bar) : (start, start + bar, low, low + rise);
        float z1 = z + thick;

        // What sits further along draws later: by where a box stands, then up its stack.
        float far = (sideways ? view.Far(0, (y0 + y1) / 2, (z + z1) / 2) - (x0 * 1e-3f * (view.FromRight ? 1 : -1)) : view.Far((x0 + x1) / 2, 0, (z + z1) / 2) - (y0 * 1e-3f * (view.FromAbove ? 1 : -1))) * BoxOrder;

        // A box draws as one: faces of neighbours never interleave, and the near face comes last.
        void Add(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float shade, int step)
        {
            facets.Add(new Facet([view.At(a.X, a.Y, a.Z), view.At(b.X, b.Y, b.Z), view.At(c.X, c.Y, c.Z), view.At(d.X, d.Y, d.Z)], Shade(color, shade), far, (order * 8) + step));
        }

        float xSide = view.FromRight ? x1 : x0;
        float ySide = view.FromAbove ? y1 : y0;
        float zSide = view.FromFront ? z : z1;
        Add(new(xSide, y0, z), new(xSide, y1, z), new(xSide, y1, z1), new(xSide, y0, z1), SideShade, 1);
        Add(new(x0, ySide, z), new(x1, ySide, z), new(x1, ySide, z1), new(x0, ySide, z1), TopShade, 2);
        Add(new(x0, y0, zSide), new(x1, y0, zSide), new(x1, y1, zSide), new(x0, y1, zSide), FrontShade, 3);
    }

    private static void Round(float start, float a, float b, float tip, float z, float bar, float thick, Rgba8 color, ChartBarShape shape, ChartProjection view, bool sideways, List<Facet> facets, int order)
    {
        int sides = shape is ChartBarShape.Pyramid or ChartBarShape.PyramidToMax ? 4 : ConeSides;
        bool narrows = shape is not ChartBarShape.Cylinder;
        float cx = start + (bar / 2);
        float cz = z + (thick / 2);
        float far = sideways ? view.Far(0, cx, cz) * BoxOrder : view.Far(cx, 0, cz) * BoxOrder;
        far -= Math.Min(a, b) * 1e-3f * BoxOrder;
        float end = narrows ? tip : b;
        float shareAtEnd = narrows && Math.Abs(tip - a) > 1e-3f ? 1 - (Math.Abs(b - a) / Math.Abs(tip - a)) : 1;
        Vector2 P(double angle, float at, float share)
        {
            float along = cx + (bar / 2 * share * (float)Math.Cos(angle));
            float deep = cz + (thick / 2 * share * (float)Math.Sin(angle));
            return sideways ? view.At(at, along, deep) : view.At(along, at, deep);
        }

        // The end drawn first when it looks away, last when it looks at the eye.
        List<Facet> sidesOut = [];
        double turn = sides == 4 ? Math.PI / 4 : 0;
        for (int i = 0; i < sides; i++)
        {
            double t0 = turn + (i * 2 * Math.PI / sides);
            double t1 = turn + ((i + 1) * 2 * Math.PI / sides);
            double tm = (t0 + t1) / 2;
            Vector3 normal = new((float)Math.Cos(tm), 0, (float)Math.Sin(tm));
            float depthOfFace = sideways ? view.Far(a, cx + normal.X, cz + normal.Z) : view.Far(cx + normal.X, a, cz + normal.Z);
            float centre = sideways ? view.Far(a, cx, cz) : view.Far(cx, a, cz);
            if (depthOfFace > centre)
            {
                continue;
            }

            Vector2[] corners = [P(t0, a, 1), P(t1, a, 1), P(t1, narrows ? b : end, shareAtEnd), P(t0, narrows ? b : end, shareAtEnd)];
            sidesOut.Add(new Facet(corners, Shade(color, ShadeOf(sideways ? new Vector3(normal.Z * 0.2f, normal.X, normal.Z) : normal)), far, 0));
        }

        int step = 0;
        foreach (Facet facet in sidesOut)
        {
            facets.Add(facet with { Order = (order * 8) + (step++ % 7) });
        }

        if (shareAtEnd > 0.01f)
        {
            Vector2[] cap = new Vector2[sides];
            for (int i = 0; i < sides; i++)
            {
                cap[i] = P(turn + (i * 2 * Math.PI / sides), narrows ? b : end, shareAtEnd);
            }

            facets.Add(new Facet(cap, Shade(color, TopShade), far - 0.001f, (order * 8) + 7));
        }
    }

    private void AreaFacets(ChartPlot plot, List<ChartSeries> series, DrawnAxis across, DrawnAxis up, ChartProjection view, bool standard, int rows, float gapDepth, List<Facet> facets)
    {
        ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Standard;
        bool stacked = grouping is ChartGrouping.Stacked or ChartGrouping.PercentStacked;
        bool percent = grouping == ChartGrouping.PercentStacked;
        int count = series.Count == 0 ? 0 : series.Max(s => s.Values.Count);
        if (count == 0)
        {
            return;
        }

        float cell = view.Depth / rows;
        float thick = cell / (1 + gapDepth);
        double[] totals = new double[count];
        double[] running = new double[count];
        if (percent)
        {
            foreach (ChartSeries s in series)
            {
                for (int i = 0; i < s.Values.Count; i++)
                {
                    totals[i] += Math.Abs(s.Values.Numbers[i] ?? 0);
                }
            }
        }

        for (int k = 0; k < series.Count; k++)
        {
            ChartSeries s = series[k];
            Rgba8 color = SolidColor(s.Format, SeriesColor(s));
            float z0 = (standard ? (k + 0.5f) * cell : cell / 2) - (thick / 2);
            float z1 = z0 + thick;
            float front = view.FromFront ? z0 : z1;
            double[] low = new double[count];
            double[] high = new double[count];
            for (int i = 0; i < count; i++)
            {
                double v = i < s.Values.Count && s.Values.Numbers[i] is { } n && double.IsFinite(n) ? n : 0;
                if (percent && totals[i] > 0)
                {
                    v /= totals[i];
                }

                low[i] = stacked ? running[i] : Math.Max(up.Scale.Minimum, Math.Min(0, up.Scale.Maximum));
                high[i] = stacked ? running[i] + v : v;
                running[i] = stacked ? high[i] : 0;
            }

            float X(int i) => (float)CategoryOn(across, i) * view.Width;
            float Y(double v) => (float)PlaceOn(up, v) * view.Height;
            float far = view.Far(view.Width / 2, view.Height / 2, (z0 + z1) / 2);
            int order = k * 1000;
            int step = 0;

            // The sides and top first, then the face looking at the eye, as one body.
            int nearEnd = view.FromRight ? count - 1 : 0;
            facets.Add(new Facet(
                [view.At(X(nearEnd), Y(low[nearEnd]), z0), view.At(X(nearEnd), Y(high[nearEnd]), z0), view.At(X(nearEnd), Y(high[nearEnd]), z1), view.At(X(nearEnd), Y(low[nearEnd]), z1)],
                Shade(color, SideShade),
                far,
                order + step++));
            for (int i = 0; i + 1 < count; i++)
            {
                float dx = X(i + 1) - X(i);
                float dy = Y(high[i + 1]) - Y(high[i]);
                Vector3 normal = new(-dy, dx, 0);
                if (view.FromAbove ? normal.Y < 0 : normal.Y > 0)
                {
                    continue;
                }

                facets.Add(new Facet(
                    [view.At(X(i), Y(high[i]), z0), view.At(X(i + 1), Y(high[i + 1]), z0), view.At(X(i + 1), Y(high[i + 1]), z1), view.At(X(i), Y(high[i]), z1)],
                    Shade(color, ShadeOf(normal)),
                    far,
                    order + step++));
            }

            List<Vector2> face = [];
            for (int i = 0; i < count; i++)
            {
                face.Add(view.At(X(i), Y(high[i]), front));
            }

            for (int i = count - 1; i >= 0; i--)
            {
                face.Add(view.At(X(i), Y(low[i]), front));
            }

            // A series with no line of its own has a thin one in the background's colour.
            (Stroke Stroke, Rgba8 Color)? edge = LineOf(s.Format, palette.AutomaticEdge, 0.75f);
            facets.Add(new Facet([.. face], color, far, order + 999, edge?.Color ?? default, edge?.Stroke.Width ?? 0));
        }
    }

    private void LineFacets(List<ChartSeries> series, DrawnAxis across, DrawnAxis up, ChartProjection view, bool standard, int rows, float gapDepth, List<Facet> facets)
    {
        float cell = view.Depth / rows;
        float thick = cell / (1 + gapDepth);
        for (int k = 0; k < series.Count; k++)
        {
            ChartSeries s = series[k];
            Rgba8 color = LineOf(s.Format, SeriesColor(s), 2.25f)?.Color ?? SeriesColor(s);
            float z0 = (standard ? (k + 0.5f) * cell : cell / 2) - (thick / 2);
            float z1 = z0 + thick;
            IReadOnlyList<double?> values = s.Values.Numbers;
            for (int i = 0; i + 1 < values.Count; i++)
            {
                if (values[i] is not { } a || values[i + 1] is not { } b || !double.IsFinite(a) || !double.IsFinite(b))
                {
                    continue;
                }

                float x0 = (float)CategoryOn(across, i) * view.Width;
                float x1 = (float)CategoryOn(across, i + 1) * view.Width;
                float y0 = (float)PlaceOn(up, a) * view.Height;
                float y1 = (float)PlaceOn(up, b) * view.Height;
                Vector3 normal = new(y0 - y1, x1 - x0, 0);
                facets.Add(new Facet(
                    [view.At(x0, y0, z0), view.At(x1, y1, z0), view.At(x1, y1, z1), view.At(x0, y0, z1)],
                    Shade(color, ShadeOf(normal)),
                    view.Far((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                    k));
            }
        }
    }

    /// <summary>What a surface's band of values looks like: a band format of its own, or the colour of its place.</summary>
    private Rgba8 BandColor(ChartPlot plot, int band, int count)
    {
        XElement? own = plot.Element.Element(ChartNs.C + "bandFmts")?.Elements(ChartNs.C + "bandFmt").FirstOrDefault(e => ChartText.Int((string?)e.Element(ChartNs.C + "idx")?.Attribute("val")) == band);
        if (own is not null && new ChartFormat(new ChartNode(own)) is { FillColor: { } color })
        {
            return palette.Resolve(color);
        }

        return palette.Series(band, Math.Max(count, 1));
    }

    private void SurfaceFacets(ChartPlot plot, List<ChartSeries> series, DrawnAxis across, DrawnAxis up, ChartAxis? seriesAxis, ChartProjection view, List<Facet> facets)
    {
        if (series.Count == 0)
        {
            return;
        }

        int columns = series.Max(s => s.Values.Count);
        if (columns == 0)
        {
            return;
        }

        List<double> levels = up.Scale.Ticks();
        if (levels.Count < 2)
        {
            levels = [up.Scale.Minimum, up.Scale.Maximum];
        }

        bool wire = plot.Wireframe == true;
        bool flat = Math.Abs(view.At(0, 0, 0).Y - view.At(0, view.Height, 0).Y) < 0.5f;
        float Z(int k) => series.Count > 1 ? (seriesAxis?.Reversed == true ? series.Count - 1 - k : k) / (float)(series.Count - 1) * view.Depth : view.Depth / 2;
        float X(int i) => columns > 1 ? i / (float)(columns - 1) * view.Width : view.Width / 2;
        double V(int k, int i) => i < series[k].Values.Count && series[k].Values.Numbers[i] is { } n && double.IsFinite(n) ? n : 0;
        int rows = Math.Max(series.Count - 1, 1);
        for (int k = 0; k < rows; k++)
        {
            int next = Math.Min(k + 1, series.Count - 1);
            for (int i = 0; i + 1 < columns; i++)
            {
                // A cell's two triangles meet at its middle.
                Vector3 a = new(X(i), (float)V(k, i), Z(k));
                Vector3 b = new(X(i + 1), (float)V(k, i + 1), Z(k));
                Vector3 c = new(X(i + 1), (float)V(next, i + 1), Z(next));
                Vector3 d = new(X(i), (float)V(next, i), Z(next));
                Vector3 m = (a + b + c + d) / 4;
                foreach ((Vector3 p, Vector3 q) in new[] { (a, b), (b, c), (c, d), (d, a) })
                {
                    List<Vector3> triangle = [p, q, m];
                    for (int band = 0; band + 1 < levels.Count; band++)
                    {
                        List<Vector3> piece = ClipBand(triangle, levels[band], levels[band + 1]);
                        if (piece.Count < 3)
                        {
                            continue;
                        }

                        Vector3 lift(Vector3 v) => new(v.X, (float)PlaceOn(up, v.Y) * view.Height, v.Z);
                        Vector3[] world = [.. piece.Select(lift)];
                        Vector3 normal = Vector3.Cross(world[1] - world[0], world[2] - world[0]);
                        Vector2[] corners = [.. world.Select(v => view.At(v.X, v.Y, v.Z))];
                        Rgba8 color = BandColor(plot, band, levels.Count - 1);
                        Vector3 mid = world.Aggregate(Vector3.Zero, (t, v) => t + v) / world.Length;
                        float far = view.Far(mid.X, mid.Y, mid.Z);
                        if (wire)
                        {
                            facets.Add(new Facet(corners, NoColor, far, band, color, 0.75f));
                        }
                        else
                        {
                            facets.Add(new Facet(corners, flat ? color : Shade(color, SurfaceShade(normal)), far, band));
                        }
                    }
                }
            }
        }
    }

    /// <summary>A surface's facet is brightest looking up at the eye and dims as it turns away.</summary>
    private static float SurfaceShade(Vector3 normal)
    {
        float size = normal.Length();
        if (!(size > 0))
        {
            return 1;
        }

        Vector3 light = Vector3.Normalize(new Vector3(-0.35f, 0.8f, -0.45f));
        return 0.62f + (0.45f * Math.Abs(Vector3.Dot(normal, light)) / size);
    }

    /// <summary>The part of a polygon whose heights are between two levels.</summary>
    private static List<Vector3> ClipBand(List<Vector3> polygon, double low, double high)
    {
        return Clip(Clip(polygon, (float)low, true), (float)high, false);

        static List<Vector3> Clip(List<Vector3> points, float level, bool above)
        {
            List<Vector3> kept = [];
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                Vector3 q = points[(i + 1) % points.Count];
                bool inP = above ? p.Y >= level : p.Y <= level;
                bool inQ = above ? q.Y >= level : q.Y <= level;
                if (inP)
                {
                    kept.Add(p);
                }

                if (inP != inQ)
                {
                    kept.Add(Vector3.Lerp(p, q, (level - p.Y) / (q.Y - p.Y)));
                }
            }

            return kept;
        }
    }

    private void DrawAxes3D(ChartProjection view, DrawnAxis across, DrawnAxis up, List<ChartSeries> series, bool hasDepthAxis, bool sideways, bool top)
    {
        float w = view.Width;
        float h = view.Height;
        float d = view.Depth;
        float nearZ = view.FromFront ? 0 : d;
        float floorY = view.FromAbove ? 0 : h;
        float edgeX = view.FromRight ? 0 : w;
        float farEdgeX = view.FromRight ? w : 0;

        // The value axis stands on the corner furthest left on the page.
        float upZ = view.At(edgeX, 0, nearZ).X <= view.At(edgeX, 0, d - nearZ).X ? nearZ : d - nearZ;
        if (!top)
        {
            DrawLines3D();
        }

        Look upLook = AxisLook(up);
        if (LabelsShown(up) && !top)
        {
            List<double> ticks = up.Scale.Ticks();
            string[] labels = [.. ticks.Select(t => TickText(up, t, up.Plots.Any(p => p.Grouping == ChartGrouping.PercentStacked)))];
            float capHeight = upLook.Size * 0.32f;
            for (int i = 0; i < ticks.Count; i++)
            {
                float place = (float)PlaceOn(up, ticks[i]);
                if (sideways)
                {
                    Vector2 at = view.At(place * w, floorY, nearZ);
                    TextAt(labels[i], upLook, at.X, at.Y + LabelMargin + upLook.Size + 1, Egami.Fonts.Layout.TextAlign.Center);
                }
                else
                {
                    Vector2 at = view.At(edgeX, place * h, nearZ);
                    Vector2 back = view.At(edgeX, place * h, d - nearZ);
                    Vector2 use = at.X <= back.X ? at : back;
                    TextAt(labels[i], upLook, use.X - (view.IsRightAngle ? LabelGap3D : LabelGapTurned3D), use.Y + capHeight, Egami.Fonts.Layout.TextAlign.Right);
                }
            }
        }

        Look look = AxisLook(across);
        if (LabelsShown(across) && across.Count > 0)
        {
            for (int i = 0; i < across.Count; i += Math.Max(across.Skip, 1))
            {
                if (i >= across.Labels.Count)
                {
                    break;
                }

                float place = (float)CategoryOn(across, i);
                if (sideways)
                {
                    Vector2 at = view.At(edgeX, place * h, nearZ);
                    TextAt(across.Labels[i], look, at.X - LabelGap3D, at.Y + (look.Size * 0.32f), Egami.Fonts.Layout.TextAlign.Right);
                }
                else
                {
                    Vector2 at = view.At(place * w, floorY, nearZ);
                    if (across.Rotation != 0 || across.Wrap < 1e4f)
                    {
                        DrawOneLabel(across, across.Labels[i], look, at.X, at.Y + LabelMargin, false, Measure("0", look).Height);
                    }
                    else
                    {
                        TextAt(across.Labels[i], look, at.X, at.Y + LabelMargin + look.Size + 4, Egami.Fonts.Layout.TextAlign.Center);
                    }
                }
            }
        }

        void DrawLines3D()
        {
            if (!across.Model.Hidden && LineOf(across.Model.Format, palette.AutomaticLine, 0.75f) is { } catInk)
            {
                Vector2 from = sideways ? view.At(edgeX, 0, nearZ) : view.At(0, floorY, nearZ);
                Vector2 to = sideways ? view.At(edgeX, h, nearZ) : view.At(w, floorY, nearZ);
                canvas.DrawLine(from, to, catInk.Stroke, catInk.Color);
                int cells = Math.Max(across.Between ? across.Count : across.Count - 1, 1);
                for (int i = 0; (across.Model.MajorTickMark ?? ChartTickMark.Cross) != ChartTickMark.None && i <= cells; i++)
                {
                    Vector2 at = Vector2.Lerp(from, to, i / (float)cells);
                    canvas.DrawLine(at, at + (sideways ? new Vector2(-Tick3D, 0) : new Vector2(0, Tick3D)), catInk.Stroke, catInk.Color);
                }
            }

            if (!up.Model.Hidden && LineOf(up.Model.Format, palette.AutomaticLine, 0.75f) is { } valueInk)
            {
                Vector2 from = sideways ? view.At(0, floorY, nearZ) : view.At(edgeX, 0, upZ);
                Vector2 to = sideways ? view.At(w, floorY, nearZ) : view.At(edgeX, h, upZ);
                canvas.DrawLine(from, to, valueInk.Stroke, valueInk.Color);
                foreach (double tick in (up.Model.MajorTickMark ?? ChartTickMark.Cross) != ChartTickMark.None ? up.Scale.Ticks() : [])
                {
                    Vector2 at = Vector2.Lerp(from, to, (float)PlaceOn(up, tick));
                    canvas.DrawLine(at, at + (sideways ? new Vector2(0, Tick3D) : new Vector2(-Tick3D, 0)), valueInk.Stroke, valueInk.Color);
                }
            }
        }

        if (hasDepthAxis && series.Count > 0)
        {
            int rows = series.Count;
            bool ends = plots.Any(p => p.Type == ChartType.Surface);
            for (int k = 0; k < rows; k++)
            {
                float share = ends ? k / (float)Math.Max(rows - 1, 1) : (k + 0.5f) / rows;
                Vector2 at = view.At(farEdgeX, floorY, share * d);
                TextAt(SeriesName(series[k]), look, at.X + LabelMargin, at.Y + (look.Size * 0.32f) + 3, Egami.Fonts.Layout.TextAlign.Left);
            }
        }
    }

    /// <summary>A 3D pie: a tilted disc on a rim, in the whole of the room the chart leaves.</summary>
    private void DrawPie3D(ChartPlot plot, RectF room)
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

        room = RectF.FromEdges(room.Left, room.Top + 2.25f, room.Right, room.Bottom - 3.75f);
        ChartView3D view = chart.View3D;
        float tilt = (float)Math.Clamp(Math.Abs(view.RotationX ?? 30), 10, 90);
        float squash = (float)Math.Sin(tilt * Math.PI / 180) * Pie3DSquash;
        float rimShare = (float)Math.Cos(tilt * Math.PI / 180) * Pie3DRim;
        float rx = Math.Min(room.Width / 2, room.Height / ((2 * squash) + (2 * rimShare)));
        float ry = rx * squash;
        float rim = rx * 2 * rimShare;
        Vector2 center = new(room.Left + (room.Width / 2), room.Top + ((room.Height - rim) / 2));
        double start = (view.RotationY ?? plot.FirstSliceAngle ?? 0) - 90;
        bool vary = plot.VaryColors != false;
        List<(double From, double Sweep, Rgba8 Color, ChartFormat Format, Vector2 Push)> slices = [];
        int largest = s.Points.Select(p => p.Explosion ?? 0).Append(s.Explosion ?? 0).Max();
        double angle = start;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not > 0)
            {
                continue;
            }

            double sweep = values[i]!.Value / total * 360;
            ChartPoint? point = PointOf(s, i);
            ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
            Rgba8 color = SolidColor(format, vary ? PointColor(i, values.Count) : SeriesColor(s));
            float pushBy = Math.Clamp(point?.Explosion ?? s.Explosion ?? 0, 0, 400) / 100f;
            double middle = (angle + (sweep / 2)) * Math.PI / 180;
            Vector2 push = new((float)Math.Cos(middle) * rx * pushBy, (float)Math.Sin(middle) * ry * pushBy);
            slices.Add((angle, sweep, color, format, push));
            angle += sweep;
        }

        if (largest > 0)
        {
            float shrink = 1 + (Math.Clamp(largest, 0, 400) / 100f);
            rx /= shrink;
            ry /= shrink;
            rim /= shrink;
            slices = [.. slices.Select(x => (x.From, x.Sweep, x.Color, x.Format, x.Push / shrink))];
        }

        Vector2 E(Vector2 at, double degrees, float drop) => at + new Vector2((float)Math.Cos(degrees * Math.PI / 180) * rx, ((float)Math.Sin(degrees * Math.PI / 180) * ry) + drop);

        // The rims that face the eye: the disc's lower half, whichever slices it falls in.
        foreach ((double from, double sweep, Rgba8 color, _, Vector2 push) in slices.OrderBy(x => Math.Abs(Math.Sin((x.From + (x.Sweep / 2)) * Math.PI / 180))))
        {
            // Only what lies on the disc's lower half: at most two runs for a slice that wraps round the far side.
            for (int turn = -1; turn <= 1; turn++)
            {
                double low = Math.Max(from, turn * 360);
                double high = Math.Min(from + sweep, 180 + (turn * 360));
                if (high - low <= 0)
                {
                    continue;
                }

                Vector2 at = center + push;
                List<Vector2> band = [];
                int steps = Math.Max((int)Math.Ceiling((high - low) / 2), 1);
                for (int i = 0; i <= steps; i++)
                {
                    band.Add(E(at, low + ((high - low) * i / steps), 0));
                }

                for (int i = steps; i >= 0; i--)
                {
                    band.Add(E(at, low + ((high - low) * i / steps), rim));
                }

                // The rim darkens from the left of the disc to its right.
                Paint paint = new LinearGradient(
                    at - new Vector2(rx, 0),
                    at + new Vector2(rx, 0),
                    [new GradientStop(0, Shade(color, 0.62f)), new GradientStop(0.5f, Shade(color, 0.75f)), new GradientStop(0.85f, Shade(color, 0.3f)), new GradientStop(1, Shade(color, 0.42f))]);
                canvas.FillPath(Path.Polygon(band.ToArray(), close: true), paint);
            }
        }

        foreach ((double from, double sweep, Rgba8 color, ChartFormat format, Vector2 push) in slices)
        {
            Vector2 at = center + push;
            int steps = Math.Max((int)Math.Ceiling(sweep / 2), 1);
            List<Vector2> edge = [at];
            for (int i = 0; i <= steps; i++)
            {
                edge.Add(E(at, from + (sweep * i / steps), 0));
            }

            Path top = Path.Polygon(edge.ToArray(), close: true);
            canvas.FillPath(top, new SolidPaint(color));
            if (LineOf(format, NoColor, 0.75f) is { } line)
            {
                canvas.StrokePath(top, new Stroke(Pie3DOutline), line.Color);
            }
        }
    }

    /// <summary>A pie with a second plot: the slices it takes out of the first go on a smaller pie or a bar, with lines between.</summary>
    private void DrawPieOfPie(ChartPlot plot, RectF room)
    {
        if (plot.Series.Count == 0)
        {
            return;
        }

        ChartSeries s = Ordered(plot)[0];
        IReadOnlyList<double?> values = s.Values.Numbers;
        int count = values.Count;
        bool[] second = SecondPlotPoints(plot, values);
        double total = values.Sum(v => v is > 0 ? v.Value : 0);
        double other = Enumerable.Range(0, count).Where(i => second[i] && values[i] is > 0).Sum(i => values[i]!.Value);
        if (!(total > 0))
        {
            return;
        }

        // The labels beside the bar take from the plots' size, not from where they are centred.
        RectF centred = room;
        ChartDataLabels shown = LabelsFor(s, plot);
        if (plot.Type == ChartType.BarOfPie && AnyLabelText(shown))
        {
            // The bar's labels sit outside it, in the plot's room.
            Look look = LookOf(shown.TextFormat, baseSize);
            float widest = Enumerable.Range(0, count).Where(i => second[i]).Select(i => Measure(LabelText(shown, s, plot, i), look).Width).DefaultIfEmpty(0).Max();
            room = room with { Width = room.Width - widest - BarLabelGap - BarLabelGap };
        }

        double gap = Math.Clamp((plot.GapWidth ?? 100) / 100.0, 0, 5);
        double size = Math.Clamp(ChartText.Int((string?)plot.Element.Element(ChartNs.C + "secondPieSize")?.Attribute("val")) ?? 75, 5, 200) / 100.0;
        bool isBar = plot.Type == ChartType.BarOfPie;
        double gapShare = gap * (isBar ? BarGapShare : PieGapShare);
        float side = Math.Min(room.Width / (float)(1 + (isBar ? size / 2 : size) + (gapShare / 2)), room.Height);
        float radius = side / 2;
        float small = radius * (float)size;
        int largest = s.Points.Select(p => p.Explosion ?? 0).Append(s.Explosion ?? 0).Max();
        float between = (float)(gapShare * radius);
        float whole = (2 * radius) + between + (isBar ? small : 2 * small);
        float left = centred.Left + ((centred.Width - whole) / 2);
        Vector2 main = new(left + radius, room.Top + (room.Height / 2));
        Vector2 aside = new(left + (2 * radius) + between + (isBar ? small / 2 : small), main.Y);
        bool vary = plot.VaryColors != false;
        double otherSweep = other / total * 360;

        // The slice standing for the second plot faces it.
        double angle = otherSweep / 2;

        // Slices pushed out of the first pie leave it smaller in the same room.
        radius /= 1 + (Math.Clamp(largest, 0, 400) / 100f * PieOfPieExplosionShare);
        Vector2 Pushed(int i, double from, double sweep)
        {
            float by = Math.Clamp(PointOf(s, i)?.Explosion ?? s.Explosion ?? 0, 0, 400) / 100f * radius;
            double middle = (from + (sweep / 2)) * Math.PI / 180;
            return new Vector2((float)Math.Cos(middle) * by, (float)Math.Sin(middle) * by);
        }

        ChartDataLabels labels = LabelsFor(s, plot);
        bool labelled = AnyLabelText(labels);
        Look labelLook = LookOf(labels.TextFormat, baseSize);
        List<(string Text, Vector2 At, Egami.Fonts.Layout.TextAlign Align)> texts = [];
        void Inside(int i, Vector2 center, float r, double from, double sweep)
        {
            string text = labelled ? LabelText(labels, s, plot, i) : string.Empty;
            if (text.Length > 0)
            {
                double middle = (from + (sweep / 2)) * Math.PI / 180;
                texts.Add((text, center + (new Vector2((float)Math.Cos(middle), (float)Math.Sin(middle)) * r * PieLabelShare) + new Vector2(0, labelLook.Size * 0.32f), Egami.Fonts.Layout.TextAlign.Center));
            }
        }

        RectF oval = new(main.X - radius, main.Y - radius, 2 * radius, 2 * radius);
        double firstAfter = angle;
        for (int i = 0; i < count; i++)
        {
            if (second[i] || values[i] is not > 0)
            {
                continue;
            }

            double sweep = values[i]!.Value / total * 360;
            Vector2 push = Pushed(i, firstAfter, sweep);
            Slice(new RectF(oval.X + push.X, oval.Y + push.Y, oval.Width, oval.Height), firstAfter, sweep, i);
            Inside(i, main + push, radius, firstAfter, sweep);
            firstAfter += sweep;
        }

        Vector2 otherPush = Vector2.Zero;
        if (other > 0)
        {
            Rgba8 color = PointColor(count, count + 1);
            ChartPoint? point = PointOf(s, count);
            ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
            float by = Math.Clamp(point?.Explosion ?? s.Explosion ?? 0, 0, 400) / 100f * radius;
            otherPush = new Vector2(by, 0);
            RectF moved = new(oval.X + by, oval.Y, oval.Width, oval.Height);
            Path slice = Path.Pie(moved, (float)(-otherSweep / 2), (float)otherSweep);
            canvas.FillPath(slice, Fill(format, moved, color) ?? new SolidPaint(color));
            if (labelled && (labels.ShowValue || labels.ShowPercent))
            {
                string text = labels.ShowValue ? other.ToString(culture) : (other / total).ToString("0%", culture);
                texts.Add((text, main + otherPush + new Vector2(radius * PieLabelShare, labelLook.Size * 0.32f), Egami.Fonts.Layout.TextAlign.Center));
            }

            if (LineOf(format, NoColor, 0.75f) is { } line)
            {
                canvas.StrokePath(slice, line.Stroke, line.Color);
            }
        }

        bool bar = plot.Type == ChartType.BarOfPie;
        float wide = small;
        (Stroke Stroke, Rgba8 Color)? seriesLine = LineOf(new ChartFormat(new ChartNode(plot.Element.Element(ChartNs.C + "serLines") ?? new XElement(ChartNs.C + "serLines"))), palette.AutomaticLine, 0.75f);
        Vector2 upper = main + otherPush + (new Vector2((float)Math.Cos(-otherSweep / 2 * Math.PI / 180), (float)Math.Sin(-otherSweep / 2 * Math.PI / 180)) * radius);
        Vector2 lower = main + otherPush + (new Vector2((float)Math.Cos(otherSweep / 2 * Math.PI / 180), (float)Math.Sin(otherSweep / 2 * Math.PI / 180)) * radius);
        if (plot.Element.Element(ChartNs.C + "serLines") is not null && seriesLine is { } lines && other > 0)
        {
            Vector2 toTop;
            Vector2 toBottom;
            if (bar)
            {
                toTop = new Vector2(aside.X - (wide / 2), aside.Y - small);
                toBottom = new Vector2(aside.X - (wide / 2), aside.Y + small);
            }
            else
            {
                // Lines touch the second pie where they would just graze it.
                toTop = Tangent(upper, aside, small, -1);
                toBottom = Tangent(lower, aside, small, 1);
            }

            canvas.DrawLine(upper, toTop, lines.Stroke, lines.Color);
            canvas.DrawLine(lower, toBottom, lines.Stroke, lines.Color);
        }

        if (other > 0)
        {
            RectF smallOval = new(aside.X - small, aside.Y - small, 2 * small, 2 * small);
            double at = otherSweep / 2;
            float y = aside.Y - small;
            for (int i = 0; i < count; i++)
            {
                if (!second[i] || values[i] is not > 0)
                {
                    continue;
                }

                if (bar)
                {
                    float tall = (float)(values[i]!.Value / other) * (2 * small);
                    RectF rect = new(aside.X - (wide / 2), y, wide, tall);
                    ChartPoint? point = PointOf(s, i);
                    ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
                    Rgba8 automatic = vary ? PointColor(i, count) : SeriesColor(s);
                    canvas.FillRect(rect, Fill(format, rect, automatic) ?? new SolidPaint(automatic));
                    if (LineOf(format, NoColor, 0.75f) is { } line)
                    {
                        canvas.StrokeRect(rect, line.Stroke, line.Color);
                    }

                    string text = labelled ? LabelText(labels, s, plot, i) : string.Empty;
                    if (text.Length > 0)
                    {
                        texts.Add((text, new Vector2(rect.Right + BarLabelGap, y + (tall / 2) + (labelLook.Size * 0.32f)), Egami.Fonts.Layout.TextAlign.Left));
                    }

                    y += tall;
                }
                else
                {
                    double sweep = values[i]!.Value / other * 360;
                    Slice(smallOval, at, sweep, i);
                    Inside(i, aside, small, at, sweep);
                    at += sweep;
                }
            }
        }

        foreach ((string text, Vector2 where, Egami.Fonts.Layout.TextAlign align) in texts)
        {
            TextAt(text, labelLook, where.X, where.Y, align);
        }

        void Slice(RectF box, double from, double sweep, int i)
        {
            ChartPoint? point = PointOf(s, i);
            ChartFormat format = point is not null && Has(point.Format) ? point.Format : s.Format;
            Rgba8 automatic = vary ? PointColor(i, count) : SeriesColor(s);
            Path slice = sweep >= 359.999 ? Path.Oval(box) : Path.Pie(box, (float)from, (float)sweep);
            canvas.FillPath(slice, Fill(format, box, automatic) ?? new SolidPaint(automatic));
            if (LineOf(format, NoColor, 0.75f) is { } line)
            {
                canvas.StrokePath(slice, line.Stroke, line.Color);
            }
        }
    }

    private static Vector2 Tangent(Vector2 from, Vector2 center, float radius, int side)
    {
        Vector2 to = center - from;
        float length = to.Length();
        if (!(length > radius))
        {
            return center + new Vector2(0, side * radius);
        }

        double angle = Math.Atan2(to.Y, to.X);
        double spread = Math.Asin(radius / length);
        double line = angle + (side * spread);
        float reach = (float)Math.Sqrt((length * length) - (radius * radius));
        return from + (new Vector2((float)Math.Cos(line), (float)Math.Sin(line)) * reach);
    }

    /// <summary>Which points go on the second plot: by position, by value, by percent, by a custom list; automatically the last third or so.</summary>
    private static bool[] SecondPlotPoints(ChartPlot plot, IReadOnlyList<double?> values)
    {
        int count = values.Count;
        bool[] second = new bool[count];
        XElement element = plot.Element;
        string? kind = (string?)element.Element(ChartNs.C + "splitType")?.Attribute("val");
        double? split = ChartText.Double((string?)element.Element(ChartNs.C + "splitPos")?.Attribute("val"));
        double total = values.Sum(v => v is > 0 ? v.Value : 0);
        switch (kind)
        {
            case "val":
                for (int i = 0; i < count; i++)
                {
                    second[i] = values[i] < (split ?? 0);
                }

                break;
            case "percent":
                for (int i = 0; i < count; i++)
                {
                    second[i] = total > 0 && values[i] / total * 100 < (split ?? 0);
                }

                break;
            case "cust":
                foreach (XElement index in element.Element(ChartNs.C + "custSplit")?.Elements(ChartNs.C + "secondPiePt") ?? [])
                {
                    if (ChartText.Int((string?)index.Attribute("val")) is { } at && at >= 0 && at < count)
                    {
                        second[at] = true;
                    }
                }

                break;
            default:
                int last = (int)Math.Round(split ?? (kind == "pos" ? 2 : AutomaticSecond(count)));
                for (int i = Math.Max(count - last, 0); i < count; i++)
                {
                    second[i] = true;
                }

                break;
        }

        return second;
    }

    private static int AutomaticSecond(int count) => Math.Max((count + 2) / 3, 1);

    private const float PieOfPieExplosionShare = 0.8f;
    private const float Pie3DOutline = 0.75f;
    private const float Pie3DRim = 0.129f;
    private const float Pie3DSquash = 0.94f;
    private const float PieGapShare = 1.02f;
    private const float BarGapShare = 0.53f;
    private const float PieLabelShare = 0.68f;
    private const float BarLabelGap = 8;
}
