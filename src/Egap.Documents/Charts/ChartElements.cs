using System.Xml.Linq;

namespace Egap.Documents.Charts;

/// <summary>Where a chart's legend sits.</summary>
public enum ChartLegendPosition
{
    /// <summary>Below the plot.</summary>
    Bottom,

    /// <summary>Left of the plot.</summary>
    Left,

    /// <summary>Right of the plot.</summary>
    Right,

    /// <summary>Above the plot.</summary>
    Top,

    /// <summary>In the top-right corner; c: charts only.</summary>
    TopRight,
}

/// <summary>A marker's shape.</summary>
public enum ChartMarkerSymbol
{
    /// <summary>No marker.</summary>
    None,

    /// <summary>A circle.</summary>
    Circle,

    /// <summary>A dash.</summary>
    Dash,

    /// <summary>A diamond.</summary>
    Diamond,

    /// <summary>A short dash.</summary>
    Dot,

    /// <summary>A picture.</summary>
    Picture,

    /// <summary>A plus sign.</summary>
    Plus,

    /// <summary>A square.</summary>
    Square,

    /// <summary>A star.</summary>
    Star,

    /// <summary>A triangle.</summary>
    Triangle,

    /// <summary>A cross.</summary>
    X,

    /// <summary>The chart picks one.</summary>
    Automatic,
}

/// <summary>Where a data label sits against its point.</summary>
public enum ChartLabelPosition
{
    /// <summary>Wherever fits best; pie charts.</summary>
    BestFit,

    /// <summary>Below.</summary>
    Bottom,

    /// <summary>In the centre.</summary>
    Center,

    /// <summary>Inside, at the base.</summary>
    InsideBase,

    /// <summary>Inside, at the end.</summary>
    InsideEnd,

    /// <summary>To the left.</summary>
    Left,

    /// <summary>Outside the end.</summary>
    OutsideEnd,

    /// <summary>To the right.</summary>
    Right,

    /// <summary>Above.</summary>
    Top,
}

/// <summary>The curve a trendline fits.</summary>
public enum ChartTrendlineType
{
    /// <summary>Exponential.</summary>
    Exponential,

    /// <summary>A straight line.</summary>
    Linear,

    /// <summary>Logarithmic.</summary>
    Logarithmic,

    /// <summary>A moving average.</summary>
    MovingAverage,

    /// <summary>A polynomial.</summary>
    Polynomial,

    /// <summary>A power curve.</summary>
    Power,
}

/// <summary>The direction of error bars.</summary>
public enum ChartErrorBarDirection
{
    /// <summary>Along the horizontal axis.</summary>
    X,

    /// <summary>Along the vertical axis.</summary>
    Y,
}

/// <summary>Which side of a point error bars show.</summary>
public enum ChartErrorBarSides
{
    /// <summary>Both sides.</summary>
    Both,

    /// <summary>Below only.</summary>
    Minus,

    /// <summary>Above only.</summary>
    Plus,
}

/// <summary>What sets the length of error bars.</summary>
public enum ChartErrorBarValue
{
    /// <summary>Values given for each point.</summary>
    Custom,

    /// <summary>One fixed amount.</summary>
    FixedValue,

    /// <summary>A percentage of the point.</summary>
    Percentage,

    /// <summary>Standard deviations.</summary>
    StandardDeviation,

    /// <summary>The standard error.</summary>
    StandardError,
}

/// <summary>What a manual layout's left and top measure from.</summary>
public enum ChartLayoutMode
{
    /// <summary>The chart's edge.</summary>
    Edge,

    /// <summary>Where the chart would have put the element itself.</summary>
    Factor,
}

/// <summary>A chart's title, or an axis's.</summary>
public sealed class ChartTitle
{
    private readonly ChartNode node;
    private readonly bool extended;

    internal ChartTitle(ChartNode node, bool extended)
    {
        this.node = node;
        this.extended = extended;
    }

    /// <summary>
    /// The text: written in the chart, or kept for the cell a reference names. Null when there is none, as for Excel's
    /// automatic title of a chart with one series. Setting text removes a reference; line breaks make paragraphs.
    /// </summary>
    public string? Text
    {
        get
        {
            XElement? title = node.Find();
            XElement? tx = title?.Element(node.Ns + "tx");
            if (tx is null)
            {
                return ParagraphsOf(title?.Element(node.Ns + "txPr"));
            }

            if (tx.Element(node.Ns + "rich") is { } rich)
            {
                return ParagraphsOf(rich);
            }

            if (extended)
            {
                return (string?)tx.Element(ChartNs.Cx + "txData")?.Element(ChartNs.Cx + "v");
            }

            XElement? cache = tx.Element(ChartNs.C + "strRef")?.Element(ChartNs.C + "strCache");
            return (string?)cache?.Elements(ChartNs.C + "pt").FirstOrDefault()?.Element(ChartNs.C + "v");
        }
        set
        {
            if (value is null)
            {
                node["tx"].Remove();
                return;
            }

            string safe = Docx.OpcPackageWriter.XmlSafe(value);
            XElement title = node.Ensure();
            if (extended)
            {
                node["tx"].Remove();
                ChartOrder.Insert(title, new XElement(ChartNs.Cx + "tx", new XElement(ChartNs.Cx + "txData", new XElement(ChartNs.Cx + "v", safe))));
                XElement body = node["txPr"].Ensure();
                body.Elements(ChartNs.A + "p").Remove();
                if (body.Element(ChartNs.A + "bodyPr") is null)
                {
                    body.AddFirst(new XElement(ChartNs.A + "bodyPr"));
                }

                body.Add(Paragraphs(safe, null));
                return;
            }

            XElement? rich = title.Element(ChartNs.C + "tx")?.Element(ChartNs.C + "rich");
            XElement? first = rich?.Element(ChartNs.A + "p")?.Element(ChartNs.A + "pPr");
            XElement? keptBody = rich?.Element(ChartNs.A + "bodyPr");
            node["tx"].Remove();
            ChartOrder.Insert(title, new XElement(
                ChartNs.C + "tx",
                new XElement(
                    ChartNs.C + "rich",
                    keptBody is null ? new XElement(ChartNs.A + "bodyPr") : new XElement(keptBody),
                    new XElement(ChartNs.A + "lstStyle"),
                    Paragraphs(safe, first))));
        }
    }

    /// <summary>The cell the text comes from, such as <c>Sheet1!$A$1</c>, or null. Setting a reference removes written text.</summary>
    public string? Reference
    {
        get
        {
            XElement? tx = node.Find()?.Element(node.Ns + "tx");
            return extended
                ? (string?)tx?.Element(ChartNs.Cx + "txData")?.Element(ChartNs.Cx + "f")
                : (string?)tx?.Element(ChartNs.C + "strRef")?.Element(ChartNs.C + "f");
        }
        set
        {
            if (value is null)
            {
                if (Reference is not null)
                {
                    node["tx"].Remove();
                }

                return;
            }

            string? text = Text;
            node["tx"].Remove();
            XElement title = node.Ensure();
            ChartOrder.Insert(title, extended
                ? new XElement(ChartNs.Cx + "tx", new XElement(ChartNs.Cx + "txData", new XElement(ChartNs.Cx + "f", value), text is null ? null : new XElement(ChartNs.Cx + "v", text)))
                : new XElement(ChartNs.C + "tx", new XElement(
                    ChartNs.C + "strRef",
                    new XElement(ChartNs.C + "f", value),
                    text is null ? null : new XElement(ChartNs.C + "strCache", new XElement(ChartNs.C + "ptCount", new XAttribute("val", 1)), new XElement(ChartNs.C + "pt", new XAttribute("idx", 0), new XElement(ChartNs.C + "v", text))))));
        }
    }

    /// <summary>Whether the title sits over the plot rather than beside it, or null when the chart decides.</summary>
    public bool? Overlay
    {
        get => extended ? node.BoolAttribute("overlay") : node.Bool("overlay");
        set
        {
            if (extended)
            {
                node.SetBoolAttribute("overlay", value);
            }
            else
            {
                node.SetBool("overlay", value);
            }
        }
    }

    /// <summary>The title's fill and outline.</summary>
    public ChartFormat Format => new(node);

    /// <summary>How the title's text looks.</summary>
    public ChartTextFormat TextFormat
    {
        get
        {
            ChartNode rich = node["tx"]["rich"];
            return new ChartTextFormat(rich.Exists ? rich : node["txPr"]);
        }
    }

    /// <summary>Where the title sits when placed by hand; c: charts only.</summary>
    public ChartLayout Layout => new(node);

    internal static string? ParagraphsOf(XElement? body)
    {
        if (body is null)
        {
            return null;
        }

        List<string> paragraphs = [.. body.Elements(ChartNs.A + "p").Select(p => string.Concat(p.Elements().Where(e => e.Name.LocalName is "r" or "fld").Select(r => (string?)r.Element(ChartNs.A + "t"))))];
        return paragraphs.All(p => p.Length == 0) ? null : string.Join('\n', paragraphs);
    }

    private static IEnumerable<XElement> Paragraphs(string text, XElement? properties) =>
        text.Split('\n').Select(line => new XElement(
            ChartNs.A + "p",
            properties is null ? null : new XElement(properties),
            new XElement(ChartNs.A + "r", new XElement(ChartNs.A + "rPr", new XAttribute("lang", "en-US")), new XElement(ChartNs.A + "t", line))));
}

/// <summary>A chart's legend.</summary>
public sealed class ChartLegend
{
    private static readonly ChartNames<ChartLegendPosition> Positions = new(
        (ChartLegendPosition.Bottom, "b"), (ChartLegendPosition.Left, "l"), (ChartLegendPosition.Right, "r"), (ChartLegendPosition.Top, "t"), (ChartLegendPosition.TopRight, "tr"));

    private readonly ChartNode node;
    private readonly bool extended;

    internal ChartLegend(ChartNode node, bool extended)
    {
        this.node = node;
        this.extended = extended;
    }

    /// <summary>Where the legend sits; null when the file does not say, which draws it on the right.</summary>
    public ChartLegendPosition? Position
    {
        get => Positions.Parse(extended ? node.Attribute("pos") : node.Value("legendPos"));
        set
        {
            if (extended)
            {
                node.SetAttribute("pos", Positions.Name(value));
            }
            else
            {
                node.SetValue("legendPos", Positions.Name(value));
            }
        }
    }

    /// <summary>Whether the legend sits over the plot, or null when the chart decides.</summary>
    public bool? Overlay
    {
        get => extended ? node.BoolAttribute("overlay") : node.Bool("overlay");
        set
        {
            if (extended)
            {
                node.SetBoolAttribute("overlay", value);
            }
            else
            {
                node.SetBool("overlay", value);
            }
        }
    }

    /// <summary>The legend entries taken out, by their index; c: charts only.</summary>
    public IReadOnlyList<int> DeletedEntries => [.. (node.Find()?.Elements(ChartNs.C + "legendEntry") ?? [])
        .Where(e => ChartText.Bool((string?)e.Element(ChartNs.C + "delete")?.Attribute("val")) ?? e.Element(ChartNs.C + "delete") is not null)
        .Select(e => ChartText.Int((string?)e.Element(ChartNs.C + "idx")?.Attribute("val")) ?? -1)
        .Where(i => i >= 0)];

    /// <summary>The legend's fill and outline.</summary>
    public ChartFormat Format => new(node);

    /// <summary>How the legend's text looks.</summary>
    public ChartTextFormat TextFormat => new(node["txPr"]);

    /// <summary>Where the legend sits when placed by hand; c: charts only.</summary>
    public ChartLayout Layout => new(node);
}

/// <summary>Where an element sits when placed by hand, as fractions of the chart. Null values leave it to the chart.</summary>
public sealed class ChartLayout
{
    private static readonly ChartNames<ChartLayoutMode> Modes = new((ChartLayoutMode.Edge, "edge"), (ChartLayoutMode.Factor, "factor"));

    private readonly ChartNode manual;

    internal ChartLayout(ChartNode holder)
    {
        manual = holder["layout"]["manualLayout"];
    }

    /// <summary>The left, as a fraction of the chart's width, or null.</summary>
    public double? X
    {
        get => manual.Double("x");
        set => manual.SetDouble("x", value);
    }

    /// <summary>The top, as a fraction of the chart's height, or null.</summary>
    public double? Y
    {
        get => manual.Double("y");
        set => manual.SetDouble("y", value);
    }

    /// <summary>The width, as a fraction of the chart's width, or null.</summary>
    public double? Width
    {
        get => manual.Double("w");
        set => manual.SetDouble("w", value);
    }

    /// <summary>The height, as a fraction of the chart's height, or null.</summary>
    public double? Height
    {
        get => manual.Double("h");
        set => manual.SetDouble("h", value);
    }

    /// <summary>What <see cref="X"/> measures from; the schema's default is <see cref="ChartLayoutMode.Factor"/>.</summary>
    public ChartLayoutMode? XMode
    {
        get => Modes.Parse(manual.Value("xMode"));
        set => manual.SetValue("xMode", Modes.Name(value));
    }

    /// <summary>What <see cref="Y"/> measures from; the schema's default is <see cref="ChartLayoutMode.Factor"/>.</summary>
    public ChartLayoutMode? YMode
    {
        get => Modes.Parse(manual.Value("yMode"));
        set => manual.SetValue("yMode", Modes.Name(value));
    }

    /// <summary>For a plot area: whether the size is that of the plot alone, without its tick labels; null when the file does not say, which counts them.</summary>
    public bool? InnerPlot
    {
        get => manual.Value("layoutTarget") switch
        {
            "inner" => true,
            "outer" => false,
            _ => null,
        };
        set => manual.SetValue("layoutTarget", value is { } inner ? (inner ? "inner" : "outer") : null);
    }
}

/// <summary>A series's or point's marker.</summary>
public sealed class ChartMarker
{
    private static readonly ChartNames<ChartMarkerSymbol> Symbols = new(
        (ChartMarkerSymbol.None, "none"), (ChartMarkerSymbol.Circle, "circle"), (ChartMarkerSymbol.Dash, "dash"), (ChartMarkerSymbol.Diamond, "diamond"),
        (ChartMarkerSymbol.Dot, "dot"), (ChartMarkerSymbol.Picture, "picture"), (ChartMarkerSymbol.Plus, "plus"), (ChartMarkerSymbol.Square, "square"),
        (ChartMarkerSymbol.Star, "star"), (ChartMarkerSymbol.Triangle, "triangle"), (ChartMarkerSymbol.X, "x"), (ChartMarkerSymbol.Automatic, "auto"));

    private readonly ChartNode node;

    internal ChartMarker(ChartNode node)
    {
        this.node = node;
    }

    /// <summary>The shape, or null when the chart picks.</summary>
    public ChartMarkerSymbol? Symbol
    {
        get => Symbols.Parse(node.Value("symbol"));
        set => node.SetValue("symbol", Symbols.Name(value));
    }

    /// <summary>The size in points, 2 to 72, or null for the chart's default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is outside 2 to 72.</exception>
    public int? Size
    {
        get => node.Int("size");
        set
        {
            if (value is < 2 or > 72)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A marker's size is from 2 to 72.");
            }

            node.SetInt("size", value);
        }
    }

    /// <summary>The marker's fill and outline.</summary>
    public ChartFormat Format => new(node);
}

/// <summary>What is shown as data labels, for a series, one point or a whole plot.</summary>
public sealed class ChartDataLabels
{
    private static readonly ChartNames<ChartLabelPosition> Positions = new(
        (ChartLabelPosition.BestFit, "bestFit"), (ChartLabelPosition.Bottom, "b"), (ChartLabelPosition.Center, "ctr"), (ChartLabelPosition.InsideBase, "inBase"),
        (ChartLabelPosition.InsideEnd, "inEnd"), (ChartLabelPosition.Left, "l"), (ChartLabelPosition.OutsideEnd, "outEnd"), (ChartLabelPosition.Right, "r"), (ChartLabelPosition.Top, "t"));

    private readonly ChartNode node;
    private readonly bool extended;

    internal ChartDataLabels(ChartNode node, bool extended)
    {
        this.node = node;
        this.extended = extended;
    }

    /// <summary>Whether the labels show the point's value.</summary>
    public bool ShowValue
    {
        get => Show("showVal", "value");
        set => SetShow("showVal", "value", value);
    }

    /// <summary>Whether the labels show the category's name.</summary>
    public bool ShowCategoryName
    {
        get => Show("showCatName", "categoryName");
        set => SetShow("showCatName", "categoryName", value);
    }

    /// <summary>Whether the labels show the series's name.</summary>
    public bool ShowSeriesName
    {
        get => Show("showSerName", "seriesName");
        set => SetShow("showSerName", "seriesName", value);
    }

    /// <summary>Whether the labels show the point's share of the whole; c: pie charts only.</summary>
    public bool ShowPercent
    {
        get => !extended && node.Bool("showPercent") == true;
        set
        {
            if (!extended)
            {
                SetAll();
                node.SetBool("showPercent", value);
            }
        }
    }

    /// <summary>Whether the labels show the bubble's size; c: bubble charts only.</summary>
    public bool ShowBubbleSize
    {
        get => !extended && node.Bool("showBubbleSize") == true;
        set
        {
            if (!extended)
            {
                SetAll();
                node.SetBool("showBubbleSize", value);
            }
        }
    }

    /// <summary>Whether the labels show the legend's key; c: charts only.</summary>
    public bool ShowLegendKey
    {
        get => !extended && node.Bool("showLegendKey") == true;
        set
        {
            if (!extended)
            {
                SetAll();
                node.SetBool("showLegendKey", value);
            }
        }
    }

    /// <summary>Where labels sit against their points, or null for the chart's default.</summary>
    public ChartLabelPosition? Position
    {
        get => Positions.Parse(extended ? node.Attribute("pos") : node.Value("dLblPos"));
        set
        {
            if (extended)
            {
                node.SetAttribute("pos", Positions.Name(value));
            }
            else
            {
                node.SetValue("dLblPos", Positions.Name(value));
            }
        }
    }

    /// <summary>The number format of the values shown, or null to follow the cells.</summary>
    public string? NumberFormat
    {
        get => node["numFmt"].Attribute("formatCode");
        set
        {
            if (value is null)
            {
                node["numFmt"].Remove();
                return;
            }

            XElement format = node["numFmt"].Ensure();
            format.SetAttributeValue("formatCode", value);
            format.SetAttributeValue("sourceLinked", "0");
        }
    }

    /// <summary>What goes between the parts of a label, or null for the default.</summary>
    public string? Separator
    {
        get => (string?)node.Find()?.Element(node.Ns + "separator");
        set
        {
            if (value is null)
            {
                node["separator"].Remove();
            }
            else
            {
                node["separator"].Ensure().Value = Docx.OpcPackageWriter.XmlSafe(value);
            }
        }
    }

    /// <summary>Whether the labels are switched off here, though an outer level has them.</summary>
    public bool Deleted => !extended && node.Bool("delete") == true;

    /// <summary>The labels' fill and outline.</summary>
    public ChartFormat Format => new(node);

    /// <summary>How the labels' text looks.</summary>
    public ChartTextFormat TextFormat => new(node["txPr"]);

    private bool Show(string name, string attribute) =>
        extended ? node["visibility"].BoolAttribute(attribute) == true : node.Bool(name) == true;

    private void SetShow(string name, string attribute, bool value)
    {
        if (extended)
        {
            node["visibility"].SetBoolAttribute(attribute, value);
            return;
        }

        SetAll();
        node.SetBool(name, value);
    }

    /// <summary>The schema wants all of a c: element's flags together, and no delete beside them.</summary>
    private void SetAll()
    {
        node["delete"].Remove();
        foreach (string flag in (string[])["showLegendKey", "showVal", "showCatName", "showSerName", "showPercent", "showBubbleSize"])
        {
            if (node.Bool(flag) is null)
            {
                node.SetBool(flag, false);
            }
        }
    }
}

/// <summary>A series's trendline.</summary>
public sealed class ChartTrendline
{
    private static readonly ChartNames<ChartTrendlineType> Types = new(
        (ChartTrendlineType.Exponential, "exp"), (ChartTrendlineType.Linear, "linear"), (ChartTrendlineType.Logarithmic, "log"),
        (ChartTrendlineType.MovingAverage, "movingAvg"), (ChartTrendlineType.Polynomial, "poly"), (ChartTrendlineType.Power, "power"));

    private readonly ChartNode node;

    internal ChartTrendline(XElement element)
    {
        Element = element;
        node = new ChartNode(element);
    }

    /// <summary>The curve it fits; null when the file gives one this model does not know.</summary>
    public ChartTrendlineType? Type
    {
        get => Types.Parse(node.Value("trendlineType"));
        set => node.SetValue("trendlineType", Types.Name(value ?? throw new ArgumentNullException(nameof(value))));
    }

    /// <summary>The name in the legend, or null for the automatic one.</summary>
    public string? Name
    {
        get => (string?)node.Find()?.Element(ChartNs.C + "name");
        set
        {
            if (value is null)
            {
                node["name"].Remove();
            }
            else
            {
                node["name"].Ensure().Value = Docx.OpcPackageWriter.XmlSafe(value);
            }
        }
    }

    /// <summary>A polynomial's order, 2 to 6, or null.</summary>
    public int? Order
    {
        get => node.Int("order");
        set => node.SetInt("order", value);
    }

    /// <summary>A moving average's period, or null.</summary>
    public int? Period
    {
        get => node.Int("period");
        set => node.SetInt("period", value);
    }

    /// <summary>How far past the last point it runs, in categories or X units, or null.</summary>
    public double? Forward
    {
        get => node.Double("forward");
        set => node.SetDouble("forward", value);
    }

    /// <summary>How far before the first point it runs, or null.</summary>
    public double? Backward
    {
        get => node.Double("backward");
        set => node.SetDouble("backward", value);
    }

    /// <summary>Where it is held to cross the value axis, or null to fit that too.</summary>
    public double? Intercept
    {
        get => node.Double("intercept");
        set => node.SetDouble("intercept", value);
    }

    /// <summary>Whether its equation shows.</summary>
    public bool ShowEquation
    {
        get => node.Bool("dispEq") == true;
        set => node.SetBool("dispEq", value);
    }

    /// <summary>Whether its R squared shows.</summary>
    public bool ShowRSquared
    {
        get => node.Bool("dispRSqr") == true;
        set => node.SetBool("dispRSqr", value);
    }

    /// <summary>The line's look.</summary>
    public ChartFormat Format => new(node);

    internal XElement Element { get; }
}

/// <summary>A series's error bars.</summary>
public sealed class ChartErrorBars
{
    private static readonly ChartNames<ChartErrorBarDirection> Directions = new((ChartErrorBarDirection.X, "x"), (ChartErrorBarDirection.Y, "y"));

    private static readonly ChartNames<ChartErrorBarSides> SideNames = new((ChartErrorBarSides.Both, "both"), (ChartErrorBarSides.Minus, "minus"), (ChartErrorBarSides.Plus, "plus"));

    private static readonly ChartNames<ChartErrorBarValue> ValueNames = new(
        (ChartErrorBarValue.Custom, "cust"), (ChartErrorBarValue.FixedValue, "fixedVal"), (ChartErrorBarValue.Percentage, "percentage"),
        (ChartErrorBarValue.StandardDeviation, "stdDev"), (ChartErrorBarValue.StandardError, "stdErr"));

    private readonly ChartNode node;

    internal ChartErrorBars(XElement element)
    {
        Element = element;
        node = new ChartNode(element);
    }

    /// <summary>Their direction, or null when the chart has only one they can take.</summary>
    public ChartErrorBarDirection? Direction
    {
        get => Directions.Parse(node.Value("errDir"));
        set => node.SetValue("errDir", Directions.Name(value));
    }

    /// <summary>Which side of a point they show.</summary>
    public ChartErrorBarSides? Sides
    {
        get => SideNames.Parse(node.Value("errBarType"));
        set => node.SetValue("errBarType", SideNames.Name(value ?? throw new ArgumentNullException(nameof(value))));
    }

    /// <summary>What sets their length.</summary>
    public ChartErrorBarValue? ValueType
    {
        get => ValueNames.Parse(node.Value("errValType"));
        set => node.SetValue("errValType", ValueNames.Name(value ?? throw new ArgumentNullException(nameof(value))));
    }

    /// <summary>The amount, for all kinds but custom.</summary>
    public double? Value
    {
        get => node.Double("val");
        set => node.SetDouble("val", value);
    }

    /// <summary>Whether their ends have no caps.</summary>
    public bool NoEndCap
    {
        get => node.Bool("noEndCap") == true;
        set => node.SetBool("noEndCap", value);
    }

    /// <summary>The lengths upwards, for custom bars.</summary>
    public ChartData Plus => new(node["plus"], false);

    /// <summary>The lengths downwards, for custom bars.</summary>
    public ChartData Minus => new(node["minus"], false);

    /// <summary>The bars' look.</summary>
    public ChartFormat Format => new(node);

    internal XElement Element { get; }
}

/// <summary>What one point of a series does differently.</summary>
public sealed class ChartPoint
{
    private readonly ChartNode node;
    private readonly bool extended;

    internal ChartPoint(XElement element, bool extended)
    {
        node = new ChartNode(element);
        this.extended = extended;
    }

    /// <summary>The point's position in the series, from 0.</summary>
    public int Index => (extended ? ChartText.Int(node.Attribute("idx")) : node.Int("idx")) ?? 0;

    /// <summary>The point's fill and outline.</summary>
    public ChartFormat Format => new(node);

    /// <summary>The point's marker.</summary>
    public ChartMarker Marker => new(node["marker"]);

    /// <summary>How far a pie slice is pulled out, as a percentage of the radius, or null.</summary>
    public int? Explosion
    {
        get => extended ? null : node.Int("explosion");
        set => node.SetInt("explosion", value);
    }

    /// <summary>Whether a negative bar swaps its colours, or null.</summary>
    public bool? InvertIfNegative
    {
        get => extended ? null : node.Bool("invertIfNegative");
        set => node.SetBool("invertIfNegative", value);
    }
}

/// <summary>How a histogram or Pareto chart groups its values into bins.</summary>
public sealed class ChartBinning
{
    private readonly ChartNode node;

    internal ChartBinning(ChartNode node)
    {
        this.node = node;
    }

    /// <summary>The width of a bin, or null when not fixed.</summary>
    public double? BinSize
    {
        get => node["binSize"].DoubleAttribute("val");
        set
        {
            if (value is null)
            {
                node["binSize"].Remove();
            }
            else
            {
                node["binCount"].Remove();
                node["binSize"].SetDoubleAttribute("val", value);
            }
        }
    }

    /// <summary>The number of bins, or null when not fixed.</summary>
    public int? BinCount
    {
        get => ChartText.Int(node["binCount"].Attribute("val"));
        set
        {
            if (value is null)
            {
                node["binCount"].Remove();
            }
            else
            {
                node["binSize"].Remove();
                node["binCount"].SetDoubleAttribute("val", value);
            }
        }
    }

    /// <summary>The value below which all go in one bin, or null for none.</summary>
    public double? Underflow
    {
        get => node.DoubleAttribute("underflow");
        set => node.SetDoubleAttribute("underflow", value);
    }

    /// <summary>The value above which all go in one bin, or null for none.</summary>
    public double? Overflow
    {
        get => node.DoubleAttribute("overflow");
        set => node.SetDoubleAttribute("overflow", value);
    }

    /// <summary>Whether a bin holds its upper limit rather than its lower; null when the file does not say.</summary>
    public bool? ClosedOnRight
    {
        get => node.Attribute("intervalClosed") switch
        {
            "r" => true,
            "l" => false,
            _ => null,
        };
        set => node.SetAttribute("intervalClosed", value is { } right ? (right ? "r" : "l") : null);
    }
}
