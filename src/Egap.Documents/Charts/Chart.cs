using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using Egami.Drawing;

using Egap.Documents.Docx;

namespace Egap.Documents.Charts;

/// <summary>How a chart draws cells with no value.</summary>
public enum ChartBlanks
{
    /// <summary>A gap.</summary>
    Gap,

    /// <summary>As zero.</summary>
    Zero,

    /// <summary>A line across, from the point before to the point after.</summary>
    Span,
}

/// <summary>
/// A chart, as Excel, Word and PowerPoint keep one in its own part: a DrawingML chart (<c>c:</c>) or one of the newer types
/// (<c>cx:</c>). You can read, change and write what the properties name; everything else in the part stays as the file had
/// it, and a chart you did not change is saved byte for byte. Charts come from a workbook's drawings, from a Word document's
/// islands, or from <see cref="Create"/>.
/// </summary>
public sealed class Chart
{
    internal const string StyleRelationship = "http://schemas.microsoft.com/office/2011/relationships/chartStyle";
    internal const string ColorsRelationship = "http://schemas.microsoft.com/office/2011/relationships/chartColorStyle";
    internal const string StyleContentType = "application/vnd.ms-office.chartstyle+xml";
    internal const string ColorsContentType = "application/vnd.ms-office.chartcolorstyle+xml";
    private const string StyleUri = "http://schemas.microsoft.com/office/drawing/2012/chartStyle";

    /// <summary>The content type of a c: chart part.</summary>
    internal const string ContentType = "application/vnd.openxmlformats-officedocument.drawingml.chart+xml";

    /// <summary>The content type of a cx: chart part.</summary>
    internal const string ExtendedContentType = "application/vnd.ms-office.chartex+xml";

    /// <summary>The relationship type from a drawing to a c: chart part.</summary>
    internal const string Relationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart";

    /// <summary>The relationship type from a drawing to a cx: chart part.</summary>
    internal const string ExtendedRelationship = "http://schemas.microsoft.com/office/2014/relationships/chartEx";

    private static readonly ChartNames<ChartBlanks> BlankNames = new((ChartBlanks.Gap, "gap"), (ChartBlanks.Zero, "zero"), (ChartBlanks.Span, "span"));

    private static readonly string[] PlotNames =
        ["barChart", "bar3DChart", "lineChart", "line3DChart", "areaChart", "area3DChart", "pieChart", "pie3DChart", "doughnutChart", "ofPieChart", "scatterChart", "bubbleChart", "radarChart", "stockChart", "surfaceChart", "surface3DChart"];

    private readonly XDocument document;
    private readonly ChartNode space;
    private readonly ChartNode body;

    private XElement? stylePart;

    private Chart(XDocument document)
    {
        this.document = document;
        Root = document.Root!;
        space = new ChartNode(Root);
        body = space["chart"];
        document.Changed += (_, _) => IsChanged = true;
    }

    /// <summary>Rewrites the part's formulas: the references of its series, labels and titles, in any of the namespaces that hold them.</summary>
    internal void RewriteReferences(Func<string, string> rewrite)
    {
        foreach (XElement formula in Root.Descendants().Where(e => e.Name.LocalName == "f" && !e.HasElements))
        {
            string text = rewrite(formula.Value);
            if (text != formula.Value)
            {
                formula.Value = text;
            }
        }
    }

    /// <summary>Whether this is one of the newer types kept in a <c>cx:</c> part.</summary>
    public bool IsExtended => Root.Name.Namespace == ChartNs.Cx;

    /// <summary>What the chart draws: its first plot's type, or <see cref="ChartType.Unknown"/> with no plot.</summary>
    public ChartType Type => Plots.Count > 0 ? Plots[0].Type : ChartType.Unknown;

    /// <summary>The chart's plots, in the order the file has them. A combination chart has more than one.</summary>
    public IReadOnlyList<ChartPlot> Plots
    {
        get
        {
            XElement? area = PlotArea.Find();
            if (area is null)
            {
                return [];
            }

            return IsExtended
                ? [.. area.Elements(ChartNs.Cx + "plotAreaRegion").Select(PlotOf)]
                : [.. area.Elements().Where(e => e.Name.Namespace == ChartNs.C && Array.IndexOf(PlotNames, e.Name.LocalName) >= 0).Select(PlotOf)];
        }
    }

    /// <summary>The chart's axes, in the order the file has them.</summary>
    public IReadOnlyList<ChartAxis> Axes
    {
        get
        {
            XElement? area = PlotArea.Find();
            return area is null ? [] : [.. area.Elements().Where(e => e.Name.LocalName is "catAx" or "valAx" or "dateAx" or "serAx" or "axis").Select(AxisOf)];
        }
    }

    /// <summary>All the series of all the plots, by their drawing order.</summary>
    public IReadOnlyList<ChartSeries> Series => [.. Plots.SelectMany(p => p.Series).OrderBy(s => s.Order)];

    /// <summary>Whether the chart has a title.</summary>
    public bool HasTitle
    {
        get => body["title"].Exists;
        set
        {
            if (value)
            {
                body["title"].Ensure();
                if (!IsExtended)
                {
                    body.SetBool("autoTitleDeleted", false);
                }
            }
            else
            {
                body["title"].Remove();
                if (!IsExtended)
                {
                    body.SetBool("autoTitleDeleted", true);
                }
            }
        }
    }

    /// <summary>Whether the file turned off the title a chart of one series would otherwise take from the series's name.</summary>
    internal bool AutoTitleDeleted => body.Bool("autoTitleDeleted") ?? false;

    /// <summary>Whether the chart's dates count from 1904.</summary>
    internal bool Date1904 => space.Bool("date1904") ?? false;

    /// <summary>The chart's title. Writing to it gives the chart one.</summary>
    public ChartTitle Title => new(body["title"], IsExtended);

    /// <summary>Whether the chart has a legend.</summary>
    public bool HasLegend
    {
        get => body["legend"].Exists;
        set
        {
            if (value)
            {
                body["legend"].Ensure();
            }
            else
            {
                body["legend"].Remove();
            }
        }
    }

    /// <summary>The chart's legend. Writing to it gives the chart one.</summary>
    public ChartLegend Legend => new(body["legend"], IsExtended);

    /// <summary>The look of the chart's background.</summary>
    public ChartFormat Format => new(space);

    /// <summary>How the chart's text looks, unless an element says otherwise.</summary>
    public ChartTextFormat TextFormat => new(space["txPr"]);

    /// <summary>The look of the plot's background.</summary>
    public ChartFormat PlotAreaFormat => new(PlotArea);

    /// <summary>Where the plot sits when placed by hand; c: charts only.</summary>
    public ChartLayout PlotAreaLayout => new(IsExtended ? ChartNode.Nowhere(ChartNs.Cx + "plotArea") : PlotArea);

    /// <summary>Whether corners are rounded, or null when not said; c: charts only.</summary>
    public bool? RoundedCorners
    {
        get => IsExtended ? null : space.Bool("roundedCorners");
        set
        {
            if (!IsExtended)
            {
                space.SetBool("roundedCorners", value);
            }
        }
    }

    /// <summary>Whether cells in hidden rows and columns are left out, or null when not said; c: charts only. Excel's default is true.</summary>
    public bool? PlotVisibleOnly
    {
        get => IsExtended ? null : body.Bool("plotVisOnly");
        set
        {
            if (!IsExtended)
            {
                body.SetBool("plotVisOnly", value);
            }
        }
    }

    /// <summary>How blank cells draw, or null when not said; c: charts only.</summary>
    public ChartBlanks? DisplayBlanksAs
    {
        get => IsExtended ? null : BlankNames.Parse(body.Value("dispBlanksAs"));
        set
        {
            if (!IsExtended)
            {
                body.SetValue("dispBlanksAs", BlankNames.Name(value));
            }
        }
    }

    /// <summary>The number of the chart's style, 1 to 48, or null when not said; c: charts only.</summary>
    public int? Style
    {
        get
        {
            if (IsExtended)
            {
                return null;
            }

            XElement? style = Root.Element(ChartNs.C + "style")
                ?? Root.Elements().FirstOrDefault(e => e.Name.LocalName == "AlternateContent")?.Descendants(ChartNs.C + "style").FirstOrDefault();
            return ChartText.Int((string?)style?.Attribute("val"));
        }
        set
        {
            if (!IsExtended)
            {
                Root.Elements().Where(e => e.Name.LocalName == "AlternateContent").Remove();
                space.SetInt("style", value);
            }
        }
    }

    /// <summary>The 3D view; c: charts only.</summary>
    public ChartView3D View3D => new(IsExtended ? ChartNode.Nowhere(ChartNs.Cx + "view3D") : body["view3D"]);

    /// <summary>Whether the part has changed since it was read.</summary>
    internal bool IsChanged { get; private set; }

    internal XElement Root { get; }

    private ChartNode PlotArea => body["plotArea"];

    internal List<ChartSeries> AllSeries => [.. SeriesInFile()];

    /// <summary>A new chart of a type, with empty axes where the type has them, a legend where Excel gives it one, and no series.</summary>
    /// <param name="type">What the chart draws.</param>
    /// <param name="threeD">Draws it in 3D: column, bar, line, area, pie and surface charts.</param>
    /// <exception cref="ArgumentException">The type has no 3D form, or it is <see cref="ChartType.Unknown"/>.</exception>
    public static Chart Create(ChartType type, bool threeD = false)
    {
        if (type is ChartType.Unknown || !Enum.IsDefined(type))
        {
            throw new ArgumentException("A chart needs a type to be made.", nameof(type));
        }

        if (threeD && type is not (ChartType.Column or ChartType.Bar or ChartType.Line or ChartType.Area or ChartType.Pie or ChartType.Surface))
        {
            throw new ArgumentException($"A {type} chart has no 3D form.", nameof(threeD));
        }

        XDocument made = type >= ChartType.Waterfall ? ExtendedTemplate(type) : Template(type, threeD);
        return new Chart(made) { IsNew = true };
    }

    /// <summary>Reads a chart part: the XML of a <c>c:chartSpace</c> or <c>cx:chartSpace</c>.</summary>
    /// <exception cref="DocumentException">The data is not a chart part.</exception>
    public static Chart Load(Stream stream, DocumentLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= new DocumentLimits();
        try
        {
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = limits.MaxPartSize,
                CloseInput = false,
            });
            XDocument document = XDocument.Load(reader);
            return Of(document) ?? throw new DocumentException("The XML is not a chart part.");
        }
        catch (XmlException error)
        {
            throw new DocumentException("The chart part is not well-formed XML: " + error.Message, error);
        }
        catch (ArgumentOutOfRangeException error)
        {
            // .NET's XML reader lets this out when a declaration's encoding cannot be switched to.
            throw new DocumentException("The chart part is not well-formed XML: " + error.Message, error);
        }
    }

    /// <summary>Writes the chart part.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(ToBytes());
    }

    /// <summary>Draws the chart on a canvas: its data, as its part last cached it, into <paramref name="bounds"/>, in the canvas's points.
    /// A region map draws nothing: its shapes come from a map service.</summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bounds">Where the chart goes.</param>
    /// <param name="options">The theme, fonts and culture; Office's defaults when null.</param>
    /// <exception cref="ArgumentNullException">The canvas is null.</exception>
    public void Draw(Canvas canvas, RectF bounds, ChartDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        new ChartRenderer(this, options).Draw(canvas, bounds);
    }

    /// <summary>A copy of the chart that shares nothing with this one.</summary>
    public Chart Clone() => new(new XDocument(document)) { IsNew = true };

    /// <summary>
    /// Adds a plot of another type to make a combination chart, on the first plot's axes or, with
    /// <paramref name="secondaryAxis"/>, on a value axis of its own on the right.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a cx: chart, which has one plot, or the chart has no axes to share.</exception>
    /// <exception cref="ArgumentException">The type cannot share axes with others: pie, doughnut, surface, stock and the cx: types.</exception>
    public ChartPlot AddPlot(ChartType type, bool secondaryAxis = false)
    {
        if (IsExtended)
        {
            throw new InvalidOperationException("A chart of the newer types has one plot.");
        }

        if (type is not (ChartType.Column or ChartType.Bar or ChartType.Line or ChartType.Area or ChartType.Scatter or ChartType.Radar or ChartType.Bubble))
        {
            throw new ArgumentException($"A {type} plot does not share a chart with others.", nameof(type));
        }

        List<XElement> ids = Plots.Count == 0 ? [] : [.. Plots[0].Element.Elements(ChartNs.C + "axId")];
        if (ids.Count < 2)
        {
            throw new InvalidOperationException("The chart has no axes to share.");
        }

        XElement area = PlotArea.Ensure();
        (string first, string second) = ((string)ids[0].Attribute("val")!, (string)ids[1].Attribute("val")!);
        if (secondaryAxis)
        {
            long next = 1 + Axes.Max(a => a.Id);
            (first, second) = (next.ToString(CultureInfo.InvariantCulture), (next + 1).ToString(CultureInfo.InvariantCulture));

            // Excel takes the axes in the order of the file, pair by pair: these go after all the others, not by kind.
            XElement last = Axes[^1].Element;
            last.AddAfterSelf(
                ValueAxis(second, first, "r", false, "max"),
                type is ChartType.Scatter or ChartType.Bubble ? ValueAxis(first, second, "b", true, "autoZero") : CategoryAxis(first, second, "b", true));
        }

        XElement plot = PlotElement(type, false);
        plot.Add(new XElement(ChartNs.C + "axId", new XAttribute("val", first)), new XElement(ChartNs.C + "axId", new XAttribute("val", second)));
        ChartOrder.Insert(area, plot);
        return PlotOf(plot);
    }

    /// <summary>Whether this was made in code, not read from a part: it needs a part of its own when saved.</summary>
    internal bool IsNew { get; private init; }

    /// <summary>The chart in a package's part, or null when the part is not a chart; a malformed part counts as none.</summary>
    internal static Chart? Read(OpcPart part)
    {
        try
        {
            Chart? chart = Of(part.LoadXml());
            if (chart is { IsExtended: true } && part.RelatedPart(StyleRelationship) is { } style)
            {
                chart.UseStylePart(style);
            }

            return chart;
        }
        catch (DocumentException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            // .NET's XML reader lets this out when a declaration's encoding cannot be switched to.
            return null;
        }
    }

    /// <summary>A 3D chart's back wall.</summary>
    internal ChartFormat BackWallFormat => new(body["backWall"]);

    /// <summary>A 3D chart's side wall.</summary>
    internal ChartFormat SideWallFormat => new(body["sideWall"]);

    /// <summary>A 3D chart's floor.</summary>
    internal ChartFormat FloorFormat => new(body["floor"]);

    internal byte[] ToBytes() => OpcPackageWriter.Serialize(document);

    internal ChartSeries SeriesOf(XElement element)
    {
        ChartSeries? series = element.Annotation<ChartSeries>();
        if (series is null)
        {
            series = new ChartSeries(this, element);
            element.AddAnnotation(series);
        }

        return series;
    }

    /// <summary>Every set of values and names that reads cells, for filling in from a workbook.</summary>
    internal IEnumerable<ChartSeries> SeriesInFile() => Plots.SelectMany(p => p.SeriesElements()).Select(SeriesOf);

    internal ChartSeries AddSeries(ChartPlot plot)
    {
        if (IsExtended)
        {
            List<XElement> all = [.. plot.SeriesElements()];
            string layout = (string?)all.FirstOrDefault()?.Attribute("layoutId") ?? "clusteredColumn";
            XElement? template = all.FirstOrDefault()?.Element(ChartNs.Cx + "layoutPr");
            HashSet<string?> taken = [.. all.Select(s => (string?)s.Attribute("uniqueId"))];
            string uniqueId = Enumerable.Range(all.Count, all.Count + 1)
                .Select(number => "{" + number.ToString("X8", CultureInfo.InvariantCulture) + "-0000-4000-8000-000000000000}")
                .First(id => !taken.Contains(id));
            string dataId = NextDataId();
            XElement data = new(ChartNs.Cx + "data", new XAttribute("id", dataId));
            ChartOrder.Insert(space["chartData"].Ensure(), data);
            XElement made = new(
                ChartNs.Cx + "series",
                new XAttribute("layoutId", layout),
                new XAttribute("uniqueId", uniqueId),
                new XElement(ChartNs.Cx + "dataId", new XAttribute("val", dataId)),
                template is null ? null : new XElement(template));
            ChartOrder.Insert(plot.Element, made);
            return SeriesOf(made);
        }

        List<ChartSeries> series = [.. SeriesInFile()];
        int next = series.Count == 0 ? 0 : 1 + Math.Max(series.Max(s => s.Index), series.Max(s => s.Order));
        XElement element = new(
            ChartNs.C + "ser",
            new XElement(ChartNs.C + "idx", new XAttribute("val", next)),
            new XElement(ChartNs.C + "order", new XAttribute("val", next)));
        XElement? last = plot.SeriesElements().LastOrDefault();
        if (last is not null)
        {
            last.AddAfterSelf(element);
        }
        else
        {
            ChartOrder.Insert(plot.Element, element);
        }

        ChartSeries added = SeriesOf(element);
        switch (plot.Type)
        {
            case ChartType.Column or ChartType.Bar or ChartType.Bubble:
                added.InvertIfNegative = false;
                break;
            case ChartType.Line or ChartType.Scatter or ChartType.Stock when !plot.Is3D:
                added.Smooth = false;
                break;
        }

        return added;
    }

    /// <summary>A cx: series's data element, or null if the file has none, as a Pareto chart's line has none.</summary>
    internal XElement? FindExtendedData(ChartSeries series)
    {
        string? id = (string?)series.Element.Element(ChartNs.Cx + "dataId")?.Attribute("val");
        return id is null ? null : Root.Element(ChartNs.Cx + "chartData")?.Elements(ChartNs.Cx + "data").FirstOrDefault(d => (string?)d.Attribute("id") == id);
    }

    /// <summary>A cx: series's data element, made if the file has none.</summary>
    internal XElement ExtendedData(ChartSeries series)
    {
        if (FindExtendedData(series) is { } found)
        {
            return found;
        }

        string id = NextDataId();
        XElement made = new(ChartNs.Cx + "data", new XAttribute("id", id));
        ChartOrder.Insert(space["chartData"].Ensure(), made);
        new ChartNode(series.Element)["dataId"].SetAttribute("val", id);
        return made;
    }

    internal void RemoveExtendedData(ChartSeries series)
    {
        string? id = (string?)series.Element.Element(ChartNs.Cx + "dataId")?.Attribute("val");
        if (!Plots.SelectMany(p => p.SeriesElements()).Any(s => s != series.Element && (string?)s.Element(ChartNs.Cx + "dataId")?.Attribute("val") == id))
        {
            Root.Element(ChartNs.Cx + "chartData")?.Elements(ChartNs.Cx + "data").Where(d => (string?)d.Attribute("id") == id).Remove();
        }
    }

    private static Chart? Of(XDocument document)
    {
        XElement? root = document.Root;
        return root is not null && root.Name.LocalName == "chartSpace" && (root.Name.Namespace == ChartNs.C || root.Name.Namespace == ChartNs.Cx) ? new Chart(document) : null;
    }

    private static XDocument Template(ChartType type, bool threeD)
    {
        const string X = "111111111";
        const string Y = "222222222";
        const string Z = "333333333";
        XElement plot = PlotElement(type, threeD);
        XElement area = new(ChartNs.C + "plotArea", new XElement(ChartNs.C + "layout"), plot);
        if (type is not (ChartType.Pie or ChartType.PieOfPie or ChartType.BarOfPie or ChartType.Doughnut))
        {
            plot.Add(new XElement(ChartNs.C + "axId", new XAttribute("val", X)), new XElement(ChartNs.C + "axId", new XAttribute("val", Y)));
            bool sideways = type == ChartType.Bar;
            if (type is ChartType.Scatter or ChartType.Bubble)
            {
                area.Add(ValueAxis(X, Y, "b", false, "autoZero", "midCat"), ValueAxis(Y, X, "l", false, "autoZero", "midCat"));
            }
            else
            {
                area.Add(CategoryAxis(X, Y, sideways ? "l" : "b", false), ValueAxis(Y, X, sideways ? "b" : "l", false, "autoZero"));
            }

            if (type is ChartType.Surface)
            {
                plot.Add(new XElement(ChartNs.C + "axId", new XAttribute("val", Z)));
                area.Add(new XElement(
                    ChartNs.C + "serAx",
                    new XElement(ChartNs.C + "axId", new XAttribute("val", Z)),
                    new XElement(ChartNs.C + "scaling", new XElement(ChartNs.C + "orientation", new XAttribute("val", "minMax"))),
                    new XElement(ChartNs.C + "delete", new XAttribute("val", 0)),
                    new XElement(ChartNs.C + "axPos", new XAttribute("val", "b")),
                    new XElement(ChartNs.C + "crossAx", new XAttribute("val", Y))));
            }
        }

        XElement chart = new(
            ChartNs.C + "chart",
            new XElement(ChartNs.C + "autoTitleDeleted", new XAttribute("val", 1)),
            // A flat surface chart is one seen from straight above.
            threeD ? new XElement(ChartNs.C + "view3D", new XElement(ChartNs.C + "rotX", new XAttribute("val", 15)), new XElement(ChartNs.C + "rotY", new XAttribute("val", 20)), new XElement(ChartNs.C + "rAngAx", new XAttribute("val", 0)))
                : type is ChartType.Surface ? new XElement(ChartNs.C + "view3D", new XElement(ChartNs.C + "rotX", new XAttribute("val", 90)), new XElement(ChartNs.C + "rotY", new XAttribute("val", 0)), new XElement(ChartNs.C + "rAngAx", new XAttribute("val", 0)), new XElement(ChartNs.C + "perspective", new XAttribute("val", 0)))
                : null,
            area,
            new XElement(ChartNs.C + "legend", new XElement(ChartNs.C + "legendPos", new XAttribute("val", "r")), new XElement(ChartNs.C + "overlay", new XAttribute("val", 0))),
            new XElement(ChartNs.C + "plotVisOnly", new XAttribute("val", 1)),
            new XElement(ChartNs.C + "dispBlanksAs", new XAttribute("val", "gap")));
        return new XDocument(new XElement(
            ChartNs.C + "chartSpace",
            new XAttribute(XNamespace.Xmlns + "c", ChartNs.ChartUri),
            new XAttribute(XNamespace.Xmlns + "a", ChartNs.A.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", ChartNs.R.NamespaceName),
            new XElement(ChartNs.C + "roundedCorners", new XAttribute("val", 0)),
            chart));
    }

    /// <summary>Keeps a style part's look for drawing; a malformed part counts as none, since the chart stands without it.</summary>
    private void UseStylePart(OpcPart part)
    {
        try
        {
            UseStylePart(part.LoadXml().Root);
        }
        catch (DocumentException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
            // .NET's XML reader lets this out when a declaration's encoding cannot be switched to.
        }
    }

    internal void UseStylePart(XElement? root) => stylePart = root;

    /// <summary>
    /// For drawing: this chart with what its style part says written in where a cx: chart says nothing itself, since Excel and
    /// Word draw such a chart by the part. The chart itself when it has no such part.
    /// </summary>
    internal Chart WithStylePartLook()
    {
        if (stylePart is null)
        {
            return this;
        }

        Chart copy = new(new XDocument(document));
        XNamespace cs = StyleUri;
        void Give(XElement? holder, string name)
        {
            if (holder is null || stylePart.Element(cs + name) is not { } entry)
            {
                return;
            }

            if (holder.Element(ChartNs.Cx + "spPr") is null && entry.Element(cs + "spPr") is { } shape)
            {
                holder.Add(new XElement(ChartNs.Cx + "spPr", shape.Elements()));
            }

            if (holder.Element(ChartNs.Cx + "txPr") is null)
            {
                XElement run = new(ChartNs.A + "defRPr", entry.Element(cs + "defRPr")?.Attributes().Where(a => a.Name.LocalName is "sz" or "b" or "i"));
                if (entry.Element(cs + "fontRef")?.Elements().FirstOrDefault() is { } color)
                {
                    run.Add(new XElement(ChartNs.A + "solidFill", color));
                }

                holder.Add(new XElement(ChartNs.Cx + "txPr", new XElement(ChartNs.A + "bodyPr"), new XElement(ChartNs.A + "p", new XElement(ChartNs.A + "pPr", run))));
            }
        }

        XElement? chart = copy.Root.Element(ChartNs.Cx + "chart");
        Give(copy.Root, "chartArea");
        Give(chart?.Element(ChartNs.Cx + "title"), "title");
        Give(chart?.Element(ChartNs.Cx + "legend"), "legend");
        foreach (XElement axis in chart?.Descendants(ChartNs.Cx + "axis") ?? [])
        {
            Give(axis, axis.Element(ChartNs.Cx + "catScaling") is null ? "valueAxis" : "categoryAxis");
            Give(axis.Element(ChartNs.Cx + "majorGridlines"), "gridlineMajor");
        }

        foreach (XElement labels in chart?.Descendants(ChartNs.Cx + "dataLabels") ?? [])
        {
            Give(labels, "dataLabel");
        }

        return copy;
    }

    /// <summary>A copy that points at no other part: for a part that goes where those parts are not.</summary>
    internal Chart WithoutRelatedParts()
    {
        Chart copy = Clone();
        foreach (XElement element in copy.document.Descendants().Where(e => e.Attributes().Any(a => a.Name.Namespace == ChartNs.R)).ToList())
        {
            element.Remove();
        }

        return copy;
    }

    /// <summary>
    /// The chart style part a cx: chart must have before Excel will open its workbook: every element the schema asks for, none
    /// of them saying more than the theme's defaults.
    /// </summary>
    internal static byte[] ExtendedStylePart()
    {
        XNamespace cs = StyleUri;
        string[] entries =
        [
            "axisTitle", "categoryAxis", "chartArea", "dataLabel", "dataLabelCallout", "dataPoint", "dataPoint3D", "dataPointLine", "dataPointMarker",
            "dataPointWireframe", "dataTable", "downBar", "dropLine", "errorBar", "floor", "gridlineMajor", "gridlineMinor", "hiLoLine", "leaderLine", "legend",
            "plotArea", "plotArea3D", "seriesAxis", "seriesLine", "title", "trendline", "trendlineLabel", "upBar", "valueAxis", "wall",
        ];
        XDocument document = new(new XElement(
            cs + "chartStyle",
            new XAttribute(XNamespace.Xmlns + "cs", StyleUri),
            new XAttribute(XNamespace.Xmlns + "a", ChartNs.A.NamespaceName),
            new XAttribute("id", 410),
            entries.Select(entry => new XElement(
                cs + entry,
                new XElement(cs + "lnRef", new XAttribute("idx", 0)),
                new XElement(cs + "fillRef", new XAttribute("idx", 0)),
                new XElement(cs + "effectRef", new XAttribute("idx", 0)),
                new XElement(cs + "fontRef", new XAttribute("idx", "minor"))))));
        return Bytes(document);
    }

    /// <summary>The colour part that goes with <see cref="ExtendedStylePart"/>: the theme's accents in turn.</summary>
    internal static byte[] ExtendedColorsPart()
    {
        XNamespace cs = StyleUri;
        XDocument document = new(new XElement(
            cs + "colorStyle",
            new XAttribute(XNamespace.Xmlns + "cs", StyleUri),
            new XAttribute(XNamespace.Xmlns + "a", ChartNs.A.NamespaceName),
            new XAttribute("meth", "cycle"),
            new XAttribute("id", 10),
            Enumerable.Range(1, 6).Select(i => new XElement(ChartNs.A + "schemeClr", new XAttribute("val", "accent" + i)))));
        return Bytes(document);
    }

    private static byte[] Bytes(XDocument document)
    {
        using MemoryStream stream = new();
        document.Save(stream, SaveOptions.DisableFormatting);
        return stream.ToArray();
    }

    private static XElement PlotElement(ChartType type, bool threeD)
    {
        static XElement V(string name, object value) => new(ChartNs.C + name, new XAttribute("val", value));
        return type switch
        {
            ChartType.Column or ChartType.Bar when threeD => new XElement(ChartNs.C + "bar3DChart", V("barDir", type == ChartType.Bar ? "bar" : "col"), V("grouping", "clustered"), V("varyColors", 0), V("gapWidth", 150)),
            ChartType.Column or ChartType.Bar => new XElement(ChartNs.C + "barChart", V("barDir", type == ChartType.Bar ? "bar" : "col"), V("grouping", "clustered"), V("varyColors", 0), V("gapWidth", 150)),
            ChartType.Line when threeD => new XElement(ChartNs.C + "line3DChart", V("grouping", "standard"), V("varyColors", 0)),
            ChartType.Line => new XElement(ChartNs.C + "lineChart", V("grouping", "standard"), V("varyColors", 0), V("marker", 1)),
            ChartType.Area when threeD => new XElement(ChartNs.C + "area3DChart", V("grouping", "standard"), V("varyColors", 0)),
            ChartType.Area => new XElement(ChartNs.C + "areaChart", V("grouping", "standard"), V("varyColors", 0)),
            ChartType.Pie when threeD => new XElement(ChartNs.C + "pie3DChart", V("varyColors", 1)),
            ChartType.Pie => new XElement(ChartNs.C + "pieChart", V("varyColors", 1), V("firstSliceAng", 0)),
            ChartType.PieOfPie or ChartType.BarOfPie => new XElement(ChartNs.C + "ofPieChart", V("ofPieType", type == ChartType.BarOfPie ? "bar" : "pie"), V("varyColors", 1), V("gapWidth", 100), V("secondPieSize", 75)),
            ChartType.Doughnut => new XElement(ChartNs.C + "doughnutChart", V("varyColors", 1), V("firstSliceAng", 0), V("holeSize", 75)),
            ChartType.Scatter => new XElement(ChartNs.C + "scatterChart", V("scatterStyle", "lineMarker"), V("varyColors", 0)),
            ChartType.Bubble => new XElement(ChartNs.C + "bubbleChart", V("varyColors", 0), V("bubbleScale", 100), V("showNegBubbles", 0)),
            ChartType.Radar => new XElement(ChartNs.C + "radarChart", V("radarStyle", "marker"), V("varyColors", 0)),
            ChartType.Stock => new XElement(ChartNs.C + "stockChart", new XElement(ChartNs.C + "hiLowLines")),
            ChartType.Surface when threeD => new XElement(ChartNs.C + "surface3DChart", V("wireframe", 0)),
            _ => new XElement(ChartNs.C + "surfaceChart", V("wireframe", 0)),
        };
    }

    private static XElement CategoryAxis(string id, string cross, string position, bool hidden) => new(
        ChartNs.C + "catAx",
        new XElement(ChartNs.C + "axId", new XAttribute("val", id)),
        new XElement(ChartNs.C + "scaling", new XElement(ChartNs.C + "orientation", new XAttribute("val", "minMax"))),
        new XElement(ChartNs.C + "delete", new XAttribute("val", hidden ? 1 : 0)),
        new XElement(ChartNs.C + "axPos", new XAttribute("val", position)),
        new XElement(ChartNs.C + "majorTickMark", new XAttribute("val", "out")),
        new XElement(ChartNs.C + "minorTickMark", new XAttribute("val", "none")),
        new XElement(ChartNs.C + "tickLblPos", new XAttribute("val", "nextTo")),
        new XElement(ChartNs.C + "crossAx", new XAttribute("val", cross)),
        new XElement(ChartNs.C + "crosses", new XAttribute("val", "autoZero")),
        new XElement(ChartNs.C + "auto", new XAttribute("val", 1)),
        new XElement(ChartNs.C + "lblAlgn", new XAttribute("val", "ctr")),
        new XElement(ChartNs.C + "lblOffset", new XAttribute("val", 100)),
        new XElement(ChartNs.C + "noMultiLvlLbl", new XAttribute("val", 0)));

    private static XElement ValueAxis(string id, string cross, string position, bool hidden, string crosses, string between = "between") => new(
        ChartNs.C + "valAx",
        new XElement(ChartNs.C + "axId", new XAttribute("val", id)),
        new XElement(ChartNs.C + "scaling", new XElement(ChartNs.C + "orientation", new XAttribute("val", "minMax"))),
        new XElement(ChartNs.C + "delete", new XAttribute("val", hidden ? 1 : 0)),
        new XElement(ChartNs.C + "axPos", new XAttribute("val", position)),
        position is "l" && between == "between" ? new XElement(ChartNs.C + "majorGridlines") : null,
        new XElement(ChartNs.C + "numFmt", new XAttribute("formatCode", "General"), new XAttribute("sourceLinked", 1)),
        new XElement(ChartNs.C + "majorTickMark", new XAttribute("val", "out")),
        new XElement(ChartNs.C + "minorTickMark", new XAttribute("val", "none")),
        new XElement(ChartNs.C + "tickLblPos", new XAttribute("val", "nextTo")),
        new XElement(ChartNs.C + "crossAx", new XAttribute("val", cross)),
        new XElement(ChartNs.C + "crosses", new XAttribute("val", crosses)),
        new XElement(ChartNs.C + "crossBetween", new XAttribute("val", between)));

    /// <summary>The newer types as Excel saves a new chart of each.</summary>
    private static XDocument ExtendedTemplate(ChartType type)
    {
        string layout = type switch
        {
            ChartType.Waterfall => "waterfall",
            ChartType.Histogram or ChartType.Pareto => "clusteredColumn",
            ChartType.BoxWhisker => "boxWhisker",
            ChartType.Treemap => "treemap",
            ChartType.Sunburst => "sunburst",
            ChartType.Funnel => "funnel",
            _ => "regionMap",
        };
        XElement region = new(ChartNs.Cx + "plotAreaRegion");
        XElement area = new(ChartNs.Cx + "plotArea", region);
        static XElement Axis(string id, XElement scaling, bool grid = false) => new(
            ChartNs.Cx + "axis",
            new XAttribute("id", id),
            scaling,
            grid ? new XElement(ChartNs.Cx + "majorGridlines") : null,
            new XElement(ChartNs.Cx + "tickLabels"));
        XElement Category(double gap) => new(ChartNs.Cx + "catScaling", new XAttribute("gapWidth", gap.ToString("R", CultureInfo.InvariantCulture)));
        XElement Value() => new(ChartNs.Cx + "valScaling");
        switch (type)
        {
            case ChartType.Waterfall:
                area.Add(Axis("0", Category(0.5)), Axis("1", Value(), true));
                break;
            case ChartType.Histogram:
                area.Add(Axis("0", Category(0)), Axis("1", Value(), true));
                break;
            case ChartType.Pareto:
                area.Add(
                    Axis("0", Category(0)),
                    Axis("1", Value(), true),
                    new XElement(
                        ChartNs.Cx + "axis",
                        new XAttribute("id", "2"),
                        new XElement(ChartNs.Cx + "valScaling", new XAttribute("max", 1), new XAttribute("min", 0)),
                        new XElement(ChartNs.Cx + "units", new XAttribute("unit", "percentage")),
                        new XElement(ChartNs.Cx + "tickLabels")));
                break;
            case ChartType.BoxWhisker:
                area.Add(Axis("0", Category(1)), Axis("1", Value(), true));
                break;
            case ChartType.Funnel:
                area.Add(Axis("1", Category(0.06)));
                break;
        }

        XElement chart = new(
            ChartNs.Cx + "chart",
            new XElement(ChartNs.Cx + "title", new XAttribute("pos", "t"), new XAttribute("align", "ctr"), new XAttribute("overlay", 0)),
            area);
        if (type is ChartType.Waterfall or ChartType.BoxWhisker or ChartType.Treemap or ChartType.Sunburst)
        {
            chart.Add(new XElement(ChartNs.Cx + "legend", new XAttribute("pos", "t"), new XAttribute("align", "ctr"), new XAttribute("overlay", 0)));
        }

        XElement first = new(
            ChartNs.Cx + "series",
            new XAttribute("layoutId", layout),
            new XAttribute("uniqueId", "{00000000-0000-4000-8000-000000000000}"),
            new XElement(ChartNs.Cx + "dataId", new XAttribute("val", 0)));
        XElement? properties = type switch
        {
            ChartType.Waterfall => new XElement(ChartNs.Cx + "layoutPr", new XElement(ChartNs.Cx + "subtotals")),
            ChartType.Histogram => new XElement(ChartNs.Cx + "layoutPr", new XElement(ChartNs.Cx + "binning", new XAttribute("intervalClosed", "r"))),
            ChartType.Pareto => new XElement(ChartNs.Cx + "layoutPr", new XElement(ChartNs.Cx + "aggregation")),
            ChartType.BoxWhisker => new XElement(
                ChartNs.Cx + "layoutPr",
                new XElement(ChartNs.Cx + "visibility", new XAttribute("meanLine", 0), new XAttribute("meanMarker", 1), new XAttribute("nonoutliers", 0), new XAttribute("outliers", 1)),
                new XElement(ChartNs.Cx + "statistics", new XAttribute("quartileMethod", "exclusive"))),
            ChartType.Treemap => new XElement(ChartNs.Cx + "layoutPr", new XElement(ChartNs.Cx + "parentLabelLayout", new XAttribute("val", "overlapping"))),
            _ => null,
        };
        first.Add(properties);
        region.Add(first);
        if (type == ChartType.Pareto)
        {
            first.Add(new XElement(ChartNs.Cx + "axisId", new XAttribute("val", 1)));
            region.Add(new XElement(
                ChartNs.Cx + "series",
                new XAttribute("layoutId", "paretoLine"),
                new XAttribute("ownerIdx", 0),
                new XAttribute("uniqueId", "{00000001-0000-4000-8000-000000000000}"),
                new XElement(ChartNs.Cx + "axisId", new XAttribute("val", 2))));
        }

        return new XDocument(new XElement(
            ChartNs.Cx + "chartSpace",
            new XAttribute(XNamespace.Xmlns + "cx", ChartNs.ExtendedUri),
            new XAttribute(XNamespace.Xmlns + "a", ChartNs.A.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", ChartNs.R.NamespaceName),
            new XElement(ChartNs.Cx + "chartData", new XElement(ChartNs.Cx + "data", new XAttribute("id", 0))),
            chart));
    }

    private ChartPlot PlotOf(XElement element)
    {
        ChartPlot? plot = element.Annotation<ChartPlot>();
        if (plot is null)
        {
            plot = new ChartPlot(this, element);
            element.AddAnnotation(plot);
        }

        return plot;
    }

    private ChartAxis AxisOf(XElement element)
    {
        ChartAxis? axis = element.Annotation<ChartAxis>();
        if (axis is null)
        {
            axis = new ChartAxis(this, element);
            element.AddAnnotation(axis);
        }

        return axis;
    }

    private string NextDataId()
    {
        int next = 0;
        foreach (XElement data in Root.Element(ChartNs.Cx + "chartData")?.Elements(ChartNs.Cx + "data") ?? [])
        {
            next = Math.Max(next, (ChartText.Int((string?)data.Attribute("id")) ?? -1) + 1);
        }

        return next.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>A 3D chart's view.</summary>
public sealed class ChartView3D
{
    private readonly ChartNode node;

    internal ChartView3D(ChartNode node)
    {
        this.node = node;
    }

    /// <summary>The tilt in degrees, -90 to 90, or null when not said.</summary>
    public int? RotationX
    {
        get => node.Int("rotX");
        set => node.SetInt("rotX", value);
    }

    /// <summary>The turn in degrees, 0 to 360, or null when not said.</summary>
    public int? RotationY
    {
        get => node.Int("rotY");
        set => node.SetInt("rotY", value);
    }

    /// <summary>The height as a percentage of the base's width, or null when not said.</summary>
    public int? HeightPercent
    {
        get => node.Int("hPercent");
        set => node.SetInt("hPercent", value);
    }

    /// <summary>The depth as a percentage of the base's width, or null when not said.</summary>
    public int? DepthPercent
    {
        get => node.Int("depthPercent");
        set => node.SetInt("depthPercent", value);
    }

    /// <summary>The field of view in degrees, or null when not said.</summary>
    public int? Perspective
    {
        get => node.Int("perspective");
        set => node.SetInt("perspective", value);
    }

    /// <summary>Whether axes stay at right angles, or null when not said.</summary>
    public bool? RightAngleAxes
    {
        get => node.Bool("rAngAx");
        set => node.SetBool("rAngAx", value);
    }
}
