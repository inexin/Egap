using System.Xml.Linq;

namespace Egap.Documents.Charts;

/// <summary>What a chart or one of its plots draws.</summary>
public enum ChartType
{
    /// <summary>Upright bars.</summary>
    Column,

    /// <summary>Sideways bars.</summary>
    Bar,

    /// <summary>Lines.</summary>
    Line,

    /// <summary>Filled areas under lines.</summary>
    Area,

    /// <summary>A pie.</summary>
    Pie,

    /// <summary>A pie with some slices pulled out into a second pie.</summary>
    PieOfPie,

    /// <summary>A pie with some slices pulled out into a stacked bar.</summary>
    BarOfPie,

    /// <summary>A ring.</summary>
    Doughnut,

    /// <summary>Points by X and Y.</summary>
    Scatter,

    /// <summary>Points by X and Y with a size.</summary>
    Bubble,

    /// <summary>Values around a circle.</summary>
    Radar,

    /// <summary>High, low and close prices.</summary>
    Stock,

    /// <summary>A surface over two categories.</summary>
    Surface,

    /// <summary>Steps that add up: a cx: chart.</summary>
    Waterfall,

    /// <summary>Values counted into bins: a cx: chart.</summary>
    Histogram,

    /// <summary>Bars in falling order with a running total line: a cx: chart.</summary>
    Pareto,

    /// <summary>Quartiles of each series: a cx: chart.</summary>
    BoxWhisker,

    /// <summary>Nested rectangles: a cx: chart.</summary>
    Treemap,

    /// <summary>Nested rings: a cx: chart.</summary>
    Sunburst,

    /// <summary>Narrowing bars: a cx: chart.</summary>
    Funnel,

    /// <summary>Values on a map: a cx: chart, kept and edited but not on the plan to be drawn.</summary>
    RegionMap,

    /// <summary>A type this model does not know, kept as it is.</summary>
    Unknown,
}

/// <summary>How the series of a plot sit against each other.</summary>
public enum ChartGrouping
{
    /// <summary>Each from the axis, one behind the other.</summary>
    Standard,

    /// <summary>Side by side.</summary>
    Clustered,

    /// <summary>On top of each other.</summary>
    Stacked,

    /// <summary>On top of each other, as shares of the whole.</summary>
    PercentStacked,
}

/// <summary>The shape of a 3D bar.</summary>
public enum ChartBarShape
{
    /// <summary>A box.</summary>
    Box,

    /// <summary>A cone.</summary>
    Cone,

    /// <summary>A cone cut at the largest value's height.</summary>
    ConeToMax,

    /// <summary>A cylinder.</summary>
    Cylinder,

    /// <summary>A pyramid.</summary>
    Pyramid,

    /// <summary>A pyramid cut at the largest value's height.</summary>
    PyramidToMax,
}

/// <summary>How a scatter chart draws its series.</summary>
public enum ChartScatterStyle
{
    /// <summary>Lines and markers, as the series say.</summary>
    LineMarker,

    /// <summary>Lines.</summary>
    Line,

    /// <summary>Markers.</summary>
    Marker,

    /// <summary>Smoothed lines.</summary>
    Smooth,

    /// <summary>Smoothed lines and markers.</summary>
    SmoothMarker,

    /// <summary>Neither.</summary>
    None,
}

/// <summary>How a radar chart draws its series.</summary>
public enum ChartRadarStyle
{
    /// <summary>Lines.</summary>
    Standard,

    /// <summary>Lines and markers.</summary>
    Marker,

    /// <summary>Filled.</summary>
    Filled,
}

/// <summary>
/// One group of series drawn the same way against the same axes. A plain chart has one plot; a combination chart has more.
/// Properties a type does not use read as null.
/// </summary>
public sealed class ChartPlot
{
    private static readonly ChartNames<ChartGrouping> Groupings = new(
        (ChartGrouping.Standard, "standard"), (ChartGrouping.Clustered, "clustered"), (ChartGrouping.Stacked, "stacked"), (ChartGrouping.PercentStacked, "percentStacked"));

    private static readonly ChartNames<ChartBarShape> Shapes = new(
        (ChartBarShape.Box, "box"), (ChartBarShape.Cone, "cone"), (ChartBarShape.ConeToMax, "coneToMax"), (ChartBarShape.Cylinder, "cylinder"),
        (ChartBarShape.Pyramid, "pyramid"), (ChartBarShape.PyramidToMax, "pyramidToMax"));

    private static readonly ChartNames<ChartScatterStyle> ScatterStyles = new(
        (ChartScatterStyle.LineMarker, "lineMarker"), (ChartScatterStyle.Line, "line"), (ChartScatterStyle.Marker, "marker"),
        (ChartScatterStyle.Smooth, "smooth"), (ChartScatterStyle.SmoothMarker, "smoothMarker"), (ChartScatterStyle.None, "none"));

    private static readonly ChartNames<ChartRadarStyle> RadarStyles = new(
        (ChartRadarStyle.Standard, "standard"), (ChartRadarStyle.Marker, "marker"), (ChartRadarStyle.Filled, "filled"));

    private readonly Chart chart;
    private readonly ChartNode node;

    internal ChartPlot(Chart chart, XElement element)
    {
        this.chart = chart;
        Element = element;
        node = new ChartNode(element);
        Series = new ChartSeriesCollection(chart, this);
    }

    /// <summary>What the plot draws.</summary>
    public ChartType Type
    {
        get
        {
            switch (Element.Name.LocalName)
            {
                case "barChart" or "bar3DChart":
                    return node.Value("barDir") == "bar" ? ChartType.Bar : ChartType.Column;
                case "lineChart" or "line3DChart":
                    return ChartType.Line;
                case "areaChart" or "area3DChart":
                    return ChartType.Area;
                case "pieChart" or "pie3DChart":
                    return ChartType.Pie;
                case "ofPieChart":
                    return node.Value("ofPieType") == "bar" ? ChartType.BarOfPie : ChartType.PieOfPie;
                case "doughnutChart":
                    return ChartType.Doughnut;
                case "scatterChart":
                    return ChartType.Scatter;
                case "bubbleChart":
                    return ChartType.Bubble;
                case "radarChart":
                    return ChartType.Radar;
                case "stockChart":
                    return ChartType.Stock;
                case "surfaceChart" or "surface3DChart":
                    return ChartType.Surface;
                case "plotAreaRegion":
                    return ExtendedType();
                default:
                    return ChartType.Unknown;
            }
        }
    }

    /// <summary>Whether the plot is drawn in 3D.</summary>
    public bool Is3D => Element.Name.LocalName.Contains("3D", StringComparison.Ordinal);

    /// <summary>The plot's series.</summary>
    public ChartSeriesCollection Series { get; }

    /// <summary>The axes the plot draws against; none for pie and doughnut charts.</summary>
    public IReadOnlyList<ChartAxis> Axes
    {
        get
        {
            if (chart.IsExtended)
            {
                return chart.Axes;
            }

            HashSet<string?> ids = [.. Element.Elements(ChartNs.C + "axId").Select(e => (string?)e.Attribute("val"))];
            return [.. chart.Axes.Where(a => ids.Contains(a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))];
        }
    }

    /// <summary>How the series sit against each other, or null when the type has no such choice.</summary>
    public ChartGrouping? Grouping
    {
        get => Groupings.Parse(node.Value("grouping"));
        set => node.SetValue("grouping", Groupings.Name(value));
    }

    /// <summary>Whether each point gets its own colour, or null when not said.</summary>
    public bool? VaryColors
    {
        get => node.Bool("varyColors");
        set => node.SetBool("varyColors", value);
    }

    /// <summary>
    /// The gap between bar clusters, as a percentage of a bar's width, or null when not said. A cx: chart keeps it as a
    /// fraction on its category axis; it reads and writes as a percentage here too.
    /// </summary>
    public int? GapWidth
    {
        get => chart.IsExtended ? Scaling?.DoubleAttribute("gapWidth") is { } gap && gap >= 0 ? (int)Math.Round(gap * 100) : null : node.Int("gapWidth");
        set
        {
            if (chart.IsExtended)
            {
                Scaling?.SetDoubleAttribute("gapWidth", value / 100.0);
            }
            else
            {
                node.SetInt("gapWidth", value);
            }
        }
    }

    /// <summary>How far bars of a cluster overlap, as a percentage from -100 to 100, or null when not said.</summary>
    public int? Overlap
    {
        get => node.Int("overlap");
        set => node.SetInt("overlap", value);
    }

    /// <summary>The depth of the gap between bars of a 3D chart, as a percentage, or null when not said.</summary>
    public int? GapDepth
    {
        get => node.Int("gapDepth");
        set => node.SetInt("gapDepth", value);
    }

    /// <summary>The shape of 3D bars, or null when not said.</summary>
    public ChartBarShape? BarShape
    {
        get => Shapes.Parse(node.Value("shape"));
        set => node.SetValue("shape", Shapes.Name(value));
    }

    /// <summary>The angle in degrees of a pie's or doughnut's first slice, or null when not said.</summary>
    public int? FirstSliceAngle
    {
        get => node.Int("firstSliceAng");
        set => node.SetInt("firstSliceAng", value);
    }

    /// <summary>The size of a doughnut's hole, as a percentage, or null when not said.</summary>
    public int? HoleSize
    {
        get => node.Int("holeSize");
        set => node.SetInt("holeSize", value);
    }

    /// <summary>How a scatter chart draws its series, or null for other types.</summary>
    public ChartScatterStyle? ScatterStyle
    {
        get => ScatterStyles.Parse(node.Value("scatterStyle"));
        set => node.SetValue("scatterStyle", ScatterStyles.Name(value));
    }

    /// <summary>How a radar chart draws its series, or null for other types.</summary>
    public ChartRadarStyle? RadarStyle
    {
        get => RadarStyles.Parse(node.Value("radarStyle"));
        set => node.SetValue("radarStyle", RadarStyles.Name(value));
    }

    /// <summary>A bubble chart's scale, as a percentage of the default size, or null when not said.</summary>
    public int? BubbleScale
    {
        get => node.Int("bubbleScale");
        set => node.SetInt("bubbleScale", value);
    }

    /// <summary>Whether a bubble chart shows negative values, or null when not said.</summary>
    public bool? ShowNegativeBubbles
    {
        get => node.Bool("showNegBubbles");
        set => node.SetBool("showNegBubbles", value);
    }

    /// <summary>Whether a bubble's width, not its area, stands for its size; null when not said.</summary>
    public bool? BubbleSizeIsWidth
    {
        get => node.Value("sizeRepresents") switch
        {
            "w" => true,
            "area" => false,
            _ => null,
        };
        set => node.SetValue("sizeRepresents", value is { } width ? (width ? "w" : "area") : null);
    }

    /// <summary>Whether a surface chart is a wireframe, or null when not said.</summary>
    public bool? Wireframe
    {
        get => node.Bool("wireframe");
        set => node.SetBool("wireframe", value);
    }

    /// <summary>Whether a line or stock chart has lines from high to low, as a stock chart's are.</summary>
    public bool HasHighLowLines => node["hiLowLines"].Exists;

    /// <summary>Whether a line or stock chart has bars from its first to its last series, as a stock chart's are.</summary>
    public bool HasUpDownBars => node["upDownBars"].Exists;

    /// <summary>Whether a line or area chart has lines from its points down to the axis.</summary>
    public bool HasDropLines => node["dropLines"].Exists;

    /// <summary>Whether a line chart's series have markers by default, or null when not said.</summary>
    public bool? ShowMarkers
    {
        get => node.Bool("marker");
        set => node.SetBool("marker", value);
    }

    /// <summary>The data labels for all the plot's series that have none of their own; c: charts only.</summary>
    public ChartDataLabels DataLabels => new(node["dLbls"], false);

    internal XElement Element { get; }

    internal IEnumerable<XElement> SeriesElements() => Element.Elements(Element.Name.Namespace + (chart.IsExtended ? "series" : "ser"));

    private ChartNode? Scaling
    {
        get
        {
            XElement? axis = Element.Parent?.Elements(ChartNs.Cx + "axis").FirstOrDefault(a => a.Element(ChartNs.Cx + "catScaling") is not null);
            return axis is null ? null : new ChartNode(axis)["catScaling"];
        }
    }

    private ChartType ExtendedType()
    {
        List<XElement> series = [.. SeriesElements()];
        if (series.Any(s => (string?)s.Attribute("layoutId") == "paretoLine"))
        {
            return ChartType.Pareto;
        }

        return (string?)series.FirstOrDefault()?.Attribute("layoutId") switch
        {
            "waterfall" => ChartType.Waterfall,
            "clusteredColumn" => ChartType.Histogram,
            "boxWhisker" => ChartType.BoxWhisker,
            "treemap" => ChartType.Treemap,
            "sunburst" => ChartType.Sunburst,
            "funnel" => ChartType.Funnel,
            "regionMap" => ChartType.RegionMap,
            _ => ChartType.Unknown,
        };
    }
}
