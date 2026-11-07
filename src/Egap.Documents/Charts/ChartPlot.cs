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
/// Properties a type does not use read as null. On a cx: chart only the series, axes and gap width apply; setting the rest does nothing.
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
                    return Classic.Value("barDir") == "bar" ? ChartType.Bar : ChartType.Column;
                case "lineChart" or "line3DChart":
                    return ChartType.Line;
                case "areaChart" or "area3DChart":
                    return ChartType.Area;
                case "pieChart" or "pie3DChart":
                    return ChartType.Pie;
                case "ofPieChart":
                    return Classic.Value("ofPieType") == "bar" ? ChartType.BarOfPie : ChartType.PieOfPie;
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

    /// <summary>
    /// How the series sit against each other, or null when the type has no such choice. Column, bar, line and area plots have
    /// it; line and area plots are not clustered. Changing it takes away label positions the new grouping does not allow.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plot has no such grouping.</exception>
    public ChartGrouping? Grouping
    {
        get => Groupings.Parse(Classic.Value("grouping"));
        set
        {
            Allow("grouping", value, "barChart", "bar3DChart", "lineChart", "line3DChart", "areaChart", "area3DChart");
            if (value == ChartGrouping.Clustered && !chart.IsExtended && Element.Name.LocalName is not ("barChart" or "bar3DChart"))
            {
                throw new InvalidOperationException($"A {Type} plot cannot be clustered.");
            }

            Classic.SetValue("grouping", Groupings.Name(value));
            if (!chart.IsExtended)
            {
                ChartRules.RemoveLabelPositionsNotAllowed(Element);
            }
        }
    }

    /// <summary>Whether each point gets its own colour, or null when not said. Stock and surface plots have no such choice.</summary>
    /// <exception cref="InvalidOperationException">The plot has no such choice.</exception>
    public bool? VaryColors
    {
        get => Classic.Bool("varyColors");
        set
        {
            Allow("colour for each point", value, "barChart", "bar3DChart", "lineChart", "line3DChart", "areaChart", "area3DChart", "pieChart", "pie3DChart", "ofPieChart", "doughnutChart", "scatterChart", "bubbleChart", "radarChart");
            Classic.SetBool("varyColors", value);
        }
    }

    /// <summary>
    /// The gap between bar clusters, as a percentage of a bar's width, or null when not said. A cx: chart keeps it as a
    /// fraction on its category axis; it reads and writes as a percentage here too.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public int? GapWidth
    {
        get => chart.IsExtended ? Scaling?.DoubleAttribute("gapWidth") is { } gap && gap >= 0 ? (int)Math.Round(gap * 100) : null : Classic.Int("gapWidth");
        set
        {
            if (chart.IsExtended)
            {
                Scaling?.SetDoubleAttribute("gapWidth", value / 100.0);
            }
            else
            {
                Allow("gap width", value, "barChart", "bar3DChart", "ofPieChart");
                Classic.SetInt("gapWidth", value);
            }
        }
    }

    /// <summary>How far bars of a cluster overlap, as a percentage from -100 to 100, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public int? Overlap
    {
        get => Classic.Int("overlap");
        set
        {
            Allow("overlap", value, "barChart");
            Classic.SetInt("overlap", value);
        }
    }

    /// <summary>The depth of the gap between bars of a 3D chart, as a percentage, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public int? GapDepth
    {
        get => Classic.Int("gapDepth");
        set
        {
            Allow("gap depth", value, "bar3DChart", "line3DChart", "area3DChart");
            Classic.SetInt("gapDepth", value);
        }
    }

    /// <summary>The shape of 3D bars, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public ChartBarShape? BarShape
    {
        get => Shapes.Parse(Classic.Value("shape"));
        set
        {
            Allow("bar shape", value, "bar3DChart");
            Classic.SetValue("shape", Shapes.Name(value));
        }
    }

    /// <summary>The angle in degrees of a pie's or doughnut's first slice, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public int? FirstSliceAngle
    {
        get => Classic.Int("firstSliceAng");
        set
        {
            Allow("first slice angle", value, "pieChart", "doughnutChart");
            Classic.SetInt("firstSliceAng", value);
        }
    }

    /// <summary>The size of a doughnut's hole, as a percentage, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public int? HoleSize
    {
        get => Classic.Int("holeSize");
        set
        {
            Allow("hole size", value, "doughnutChart");
            Classic.SetInt("holeSize", value);
        }
    }

    /// <summary>How a scatter chart draws its series, or null for other types.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public ChartScatterStyle? ScatterStyle
    {
        get => ScatterStyles.Parse(Classic.Value("scatterStyle"));
        set
        {
            Allow("scatter style", value, "scatterChart");
            Classic.SetValue("scatterStyle", ScatterStyles.Name(value));
        }
    }

    /// <summary>How a radar chart draws its series, or null for other types.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public ChartRadarStyle? RadarStyle
    {
        get => RadarStyles.Parse(Classic.Value("radarStyle"));
        set
        {
            Allow("radar style", value, "radarChart");
            Classic.SetValue("radarStyle", RadarStyles.Name(value));
        }
    }

    /// <summary>A bubble chart's scale, as a percentage of the default size, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public int? BubbleScale
    {
        get => Classic.Int("bubbleScale");
        set
        {
            Allow("bubble scale", value, "bubbleChart");
            Classic.SetInt("bubbleScale", value);
        }
    }

    /// <summary>Whether a bubble chart shows negative values, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public bool? ShowNegativeBubbles
    {
        get => Classic.Bool("showNegBubbles");
        set
        {
            Allow("choice of negative bubbles", value, "bubbleChart");
            Classic.SetBool("showNegBubbles", value);
        }
    }

    /// <summary>Whether a bubble's width, not its area, stands for its size; null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public bool? BubbleSizeIsWidth
    {
        get => Classic.Value("sizeRepresents") switch
        {
            "w" => true,
            "area" => false,
            _ => null,
        };
        set
        {
            Allow("bubble size choice", value, "bubbleChart");
            Classic.SetValue("sizeRepresents", value is { } width ? (width ? "w" : "area") : null);
        }
    }

    /// <summary>Whether a surface chart is a wireframe, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public bool? Wireframe
    {
        get => Classic.Bool("wireframe");
        set
        {
            Allow("wireframe", value, "surfaceChart", "surface3DChart");
            Classic.SetBool("wireframe", value);
        }
    }

    /// <summary>Whether a line or stock chart has lines from high to low, as a stock chart's are.</summary>
    public bool HasHighLowLines => Classic["hiLowLines"].Exists;

    /// <summary>Whether a line or stock chart has bars from its first to its last series, as a stock chart's are.</summary>
    public bool HasUpDownBars => Classic["upDownBars"].Exists;

    /// <summary>Whether a line or area chart has lines from its points down to the axis.</summary>
    public bool HasDropLines => Classic["dropLines"].Exists;

    /// <summary>Whether a line chart's series have markers by default, or null when not said.</summary>
    /// <exception cref="InvalidOperationException">The plot has no place for it: see the charts guide.</exception>
    public bool? ShowMarkers
    {
        get => Classic.Bool("marker");
        set
        {
            Allow("markers setting", value, "lineChart");
            Classic.SetBool("marker", value);
        }
    }

    /// <summary>The data labels for all the plot's series that have none of their own; c: charts only.</summary>
    public ChartDataLabels DataLabels => new(Classic["dLbls"], false);

    /// <summary>How the high-low lines draw.</summary>
    internal ChartFormat HighLowFormat => new(Classic["hiLowLines"]);

    /// <summary>How the drop lines draw.</summary>
    internal ChartFormat DropLinesFormat => new(Classic["dropLines"]);

    /// <summary>How the up bars draw.</summary>
    internal ChartFormat UpBarsFormat => new(Classic["upDownBars"]["upBars"]);

    /// <summary>How the down bars draw.</summary>
    internal ChartFormat DownBarsFormat => new(Classic["upDownBars"]["downBars"]);

    /// <summary>The space between up and down bars, as a percentage of a bar's width.</summary>
    internal int? UpDownGapWidth => Classic["upDownBars"].Int("gapWidth");

    internal XElement Element { get; }

    /// <summary>Refuses a value for something the plot's element has no place for; a null only takes it away, and is always fine.</summary>
    private void Allow(string what, object? value, params string[] elements)
    {
        if (value is not null && !chart.IsExtended && !elements.Contains(Element.Name.LocalName))
        {
            throw new InvalidOperationException($"A {(Is3D ? "3D " : "")}{Type} plot has no {what}.");
        }
    }

    internal IEnumerable<XElement> SeriesElements() => Element.Elements(Element.Name.Namespace + (chart.IsExtended ? "series" : "ser"));

    /// <summary>What only a c: chart's plot has; on a cx: chart, nowhere.</summary>
    private ChartNode Classic => chart.IsExtended ? ChartNode.Nowhere(Element.Name) : node;

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
