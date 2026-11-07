using System.Collections;
using System.Xml.Linq;

namespace Egap.Documents.Charts;

/// <summary>How a treemap draws the labels of its parent categories.</summary>
public enum ChartParentLabels
{
    /// <summary>No labels.</summary>
    None,

    /// <summary>A band above each group.</summary>
    Banner,

    /// <summary>Over the group's top-left corner.</summary>
    Overlapping,
}

/// <summary>One series of a chart.</summary>
public sealed class ChartSeries
{
    private readonly Chart chart;
    private readonly ChartNode node;
    private readonly bool extended;

    internal ChartSeries(Chart chart, XElement element)
    {
        this.chart = chart;
        Element = element;
        node = new ChartNode(element);
        extended = chart.IsExtended;
    }

    /// <summary>Refuses a value for something the series's plot has no place for; a null only takes it away, and is always fine.</summary>
    private void Allow(string what, object? value, params string[] plots)
    {
        if (value is not null && !extended && Element.Parent is { } plot && !plots.Contains(plot.Name.LocalName))
        {
            throw new InvalidOperationException($"A series of a {plot.Name.LocalName} has no {what}.");
        }
    }

    /// <summary>The series's place in the file's numbering, which picks its automatic colour.</summary>
    public int Index => extended ? chart.AllSeries.IndexOf(this) : node.Int("idx") ?? 0;

    /// <summary>The series's place in drawing order and the legend.</summary>
    public int Order
    {
        get => extended ? chart.AllSeries.IndexOf(this) : node.Int("order") ?? 0;
        set
        {
            if (!extended)
            {
                node.SetInt("order", value);
            }
        }
    }

    /// <summary>The series's name as the legend shows it, or null. Setting it removes a reference to a cell.</summary>
    public string? Name
    {
        get
        {
            XElement? tx = Element.Element(node.Ns + "tx");
            return extended
                ? (string?)tx?.Element(ChartNs.Cx + "txData")?.Element(ChartNs.Cx + "v")
                : (string?)tx?.Element(ChartNs.C + "v")
                    ?? (string?)tx?.Element(ChartNs.C + "strRef")?.Element(ChartNs.C + "strCache")?.Elements(ChartNs.C + "pt").FirstOrDefault()?.Element(ChartNs.C + "v");
        }
        set
        {
            node["tx"].Remove();
            if (value is null)
            {
                return;
            }

            string safe = Docx.OpcPackageWriter.XmlSafe(value);
            ChartOrder.Insert(Element, extended
                ? new XElement(ChartNs.Cx + "tx", new XElement(ChartNs.Cx + "txData", new XElement(ChartNs.Cx + "v", safe)))
                : new XElement(ChartNs.C + "tx", new XElement(ChartNs.C + "v", safe)));
        }
    }

    /// <summary>The cell the name comes from, such as <c>Sheet1!$B$1</c>, or null. The name itself stays until the cells are read.</summary>
    public string? NameReference
    {
        get
        {
            XElement? tx = Element.Element(node.Ns + "tx");
            return extended
                ? (string?)tx?.Element(ChartNs.Cx + "txData")?.Element(ChartNs.Cx + "f")
                : (string?)tx?.Element(ChartNs.C + "strRef")?.Element(ChartNs.C + "f");
        }
        set
        {
            string? name = Name;
            node["tx"].Remove();
            if (value is null)
            {
                Name = name;
                return;
            }

            ChartOrder.Insert(Element, extended
                ? new XElement(ChartNs.Cx + "tx", new XElement(ChartNs.Cx + "txData", new XElement(ChartNs.Cx + "f", value), name is null ? null : new XElement(ChartNs.Cx + "v", name)))
                : new XElement(ChartNs.C + "tx", new XElement(
                    ChartNs.C + "strRef",
                    new XElement(ChartNs.C + "f", value),
                    name is null ? null : new XElement(ChartNs.C + "strCache", new XElement(ChartNs.C + "ptCount", new XAttribute("val", 1)), new XElement(ChartNs.C + "pt", new XAttribute("idx", 0), new XElement(ChartNs.C + "v", name))))));
        }
    }

    /// <summary>The categories, or for a scatter or bubble chart the X values.</summary>
    public ChartData Categories => extended ? Dimension(strings: true) : new ChartData(node[UsesXY ? "xVal" : "cat"], false);

    /// <summary>The values, or for a scatter or bubble chart the Y values.</summary>
    public ChartData Values => extended ? Dimension(strings: false) : new ChartData(node[UsesXY ? "yVal" : "val"], false, numbersOnly: true);

    /// <summary>A bubble chart's bubble sizes; c: charts only.</summary>
    public ChartData BubbleSizes => new(Classic["bubbleSize"], extended, numbersOnly: true);

    /// <summary>The series's fill and outline.</summary>
    public ChartFormat Format => new(node);

    /// <summary>The series's marker; line, scatter and radar charts.</summary>
    public ChartMarker Marker => new(Classic["marker"]);

    /// <summary>Whether the line is smoothed, or null when not said; line and scatter charts.</summary>
    /// <exception cref="InvalidOperationException">The series's plot is of another type.</exception>
    public bool? Smooth
    {
        get => Classic.Bool("smooth");
        set
        {
            Allow("smoothing", value, "lineChart", "line3DChart", "scatterChart", "stockChart");
            Classic.SetBool("smooth", value);
        }
    }

    /// <summary>Whether a negative bar swaps its colours, or null when not said; bar and bubble charts.</summary>
    /// <exception cref="InvalidOperationException">The series's plot is of another type.</exception>
    public bool? InvertIfNegative
    {
        get => Classic.Bool("invertIfNegative");
        set
        {
            Allow("colours for negative values", value, "barChart", "bar3DChart", "bubbleChart");
            Classic.SetBool("invertIfNegative", value);
        }
    }

    /// <summary>How far a pie's slices are pulled out, as a percentage of the radius, or null; pie and doughnut charts.</summary>
    /// <exception cref="InvalidOperationException">The series's plot is of another type.</exception>
    public int? Explosion
    {
        get => Classic.Int("explosion");
        set
        {
            Allow("explosion", value, "pieChart", "pie3DChart", "ofPieChart", "doughnutChart");
            Classic.SetInt("explosion", value);
        }
    }

    /// <summary>Whether the bubbles have a 3D look, or null when not said; bubble charts.</summary>
    /// <exception cref="InvalidOperationException">The series's plot is of another type.</exception>
    public bool? Bubble3D
    {
        get => Classic.Bool("bubble3D");
        set
        {
            Allow("3D bubbles", value, "bubbleChart");
            Classic.SetBool("bubble3D", value);
        }
    }

    /// <summary>Whether the series has data labels of its own.</summary>
    public bool HasDataLabels
    {
        get
        {
            XElement? labels = Element.Element(node.Ns + (extended ? "dataLabels" : "dLbls"));
            return labels is not null && (extended || !new ChartDataLabels(new ChartNode(labels), false).Deleted);
        }
        set
        {
            ChartNode labels = node[extended ? "dataLabels" : "dLbls"];
            if (!value)
            {
                labels.Remove();
                return;
            }

            if (!labels.Exists || new ChartDataLabels(labels, extended).Deleted)
            {
                ChartDataLabels made = new(labels, extended);
                made.ShowValue = true;
            }
        }
    }

    /// <summary>The series's data labels. Writing to them gives the series some.</summary>
    public ChartDataLabels DataLabels => new(node[extended ? "dataLabels" : "dLbls"], extended);

    /// <summary>What single points do differently.</summary>
    public IReadOnlyList<ChartPoint> Points =>
        [.. Element.Elements(node.Ns + (extended ? "dataPt" : "dPt")).Select(e => new ChartPoint(e, extended))];

    /// <summary>The data labels of single points, by their position; c: charts only.</summary>
    public IReadOnlyDictionary<int, ChartDataLabels> PointLabels
    {
        get
        {
            Dictionary<int, ChartDataLabels> labels = [];
            foreach (XElement label in Element.Element(ChartNs.C + "dLbls")?.Elements(ChartNs.C + "dLbl") ?? [])
            {
                if (ChartText.Int((string?)label.Element(ChartNs.C + "idx")?.Attribute("val")) is { } index)
                {
                    labels[index] = new ChartDataLabels(new ChartNode(label), false);
                }
            }

            return labels;
        }
    }

    /// <summary>The trendlines; c: charts only.</summary>
    public IReadOnlyList<ChartTrendline> Trendlines => [.. Element.Elements(ChartNs.C + "trendline").Select(e => new ChartTrendline(e))];

    /// <summary>The error bars; c: charts only. A scatter or bubble series can have them in both directions.</summary>
    public IReadOnlyList<ChartErrorBars> ErrorBars => [.. Element.Elements(ChartNs.C + "errBars").Select(e => new ChartErrorBars(e))];

    /// <summary>A waterfall's points that are totals rather than steps, by position.</summary>
    public IReadOnlyList<int> Subtotals
    {
        get => [.. (LayoutProperties["subtotals"].Find()?.Elements(ChartNs.Cx + "idx") ?? [])
            .Select(e => ChartText.Int((string?)e.Attribute("val")) ?? -1).Where(i => i >= 0)];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            XElement subtotals = LayoutProperties["subtotals"].Ensure();
            subtotals.RemoveNodes();
            subtotals.Add(value.Select(i => new XElement(ChartNs.Cx + "idx", new XAttribute("val", i))));
        }
    }

    /// <summary>How a histogram or Pareto chart groups its values into bins.</summary>
    public ChartBinning Binning => new(LayoutProperties["binning"]);

    /// <summary>A box and whisker chart's way of working out quartiles: <c>exclusive</c>, <c>inclusive</c>, or null when not said.</summary>
    public string? QuartileMethod
    {
        get => LayoutProperties["statistics"].Attribute("quartileMethod");
        set => LayoutProperties["statistics"].SetAttribute("quartileMethod", value);
    }

    /// <summary>A box and whisker chart: whether the mean line shows, or null when not said.</summary>
    public bool? ShowMeanLine
    {
        get => LayoutProperties["visibility"].BoolAttribute("meanLine");
        set => LayoutProperties["visibility"].SetBoolAttribute("meanLine", value);
    }

    /// <summary>A box and whisker chart: whether the mean marker shows, or null when not said.</summary>
    public bool? ShowMeanMarker
    {
        get => LayoutProperties["visibility"].BoolAttribute("meanMarker");
        set => LayoutProperties["visibility"].SetBoolAttribute("meanMarker", value);
    }

    /// <summary>A box and whisker chart: whether points that are not outliers show, or null when not said.</summary>
    public bool? ShowInnerPoints
    {
        get => LayoutProperties["visibility"].BoolAttribute("nonoutliers");
        set => LayoutProperties["visibility"].SetBoolAttribute("nonoutliers", value);
    }

    /// <summary>A box and whisker chart: whether outliers show, or null when not said.</summary>
    public bool? ShowOutliers
    {
        get => LayoutProperties["visibility"].BoolAttribute("outliers");
        set => LayoutProperties["visibility"].SetBoolAttribute("outliers", value);
    }

    /// <summary>A waterfall: whether the lines between steps show, or null when not said.</summary>
    public bool? ShowConnectorLines
    {
        get => LayoutProperties["visibility"].BoolAttribute("connectorLines");
        set => LayoutProperties["visibility"].SetBoolAttribute("connectorLines", value);
    }

    /// <summary>A treemap's labels of parent categories, or null when not said.</summary>
    public ChartParentLabels? ParentLabels
    {
        get => LayoutProperties["parentLabelLayout"].Attribute("val") switch
        {
            "none" => ChartParentLabels.None,
            "banner" => ChartParentLabels.Banner,
            "overlapping" => ChartParentLabels.Overlapping,
            _ => null,
        };
        set
        {
            if (value is null)
            {
                LayoutProperties["parentLabelLayout"].Remove();
                return;
            }

            LayoutProperties["parentLabelLayout"].SetAttribute("val", value switch
            {
                ChartParentLabels.None => "none",
                ChartParentLabels.Banner => "banner",
                _ => "overlapping",
            });
        }
    }

    internal XElement Element { get; }

    /// <summary>The data the series reads, for filling in values from cells.</summary>
    internal IEnumerable<ChartData> Data()
    {
        yield return Categories;
        yield return Values;
        if (Element.Element(node.Ns + "bubbleSize") is not null)
        {
            yield return BubbleSizes;
        }
    }

    /// <summary>What only a c: series has; on a cx: series, nowhere.</summary>
    private ChartNode Classic => extended ? ChartNode.Nowhere(Element.Name) : node;

    /// <summary>What only a cx: series has; on a c: series, nowhere.</summary>
    private ChartNode LayoutProperties => extended ? node["layoutPr"] : ChartNode.Nowhere(ChartNs.C + "layoutPr");

    private bool UsesXY =>
        Element.Element(ChartNs.C + "xVal") is not null || Element.Element(ChartNs.C + "yVal") is not null
        || Element.Parent?.Name.LocalName is "scatterChart" or "bubbleChart";

    /// <summary>A cx: series's categories or values. Reading makes nothing; a dimension and its data are made when first written.</summary>
    private ChartData Dimension(bool strings) => new(
        new ChartNode(ChartNs.Cx, () => FindDimension(chart.FindExtendedData(this), strings), () => EnsureDimension(strings)),
        true,
        numbersOnly: !strings);

    private static XElement? FindDimension(XElement? data, bool strings) => strings
        ? data?.Elements().FirstOrDefault(e => (string?)e.Attribute("type") is "cat" or "colorStr" or "entityId")
        : data?.Elements().FirstOrDefault(e => e.Name.LocalName == "numDim" && (string?)e.Attribute("type") != "cat");

    private XElement EnsureDimension(bool strings)
    {
        XElement data = chart.ExtendedData(this);
        if (FindDimension(data, strings) is { } found)
        {
            return found;
        }

        XElement made = new(
            ChartNs.Cx + (strings ? "strDim" : "numDim"),
            new XAttribute("type", strings ? "cat" : ValueDimension));
        if (strings)
        {
            data.AddFirst(made);
        }
        else
        {
            ChartOrder.Insert(data, made);
        }

        return made;
    }

    private string ValueDimension => (string?)Element.Attribute("layoutId") is "treemap" or "sunburst" ? "size" : "val";
}

/// <summary>The series of one plot of a chart.</summary>
public sealed class ChartSeriesCollection : IReadOnlyList<ChartSeries>
{
    private readonly Chart chart;
    private readonly ChartPlot plot;

    internal ChartSeriesCollection(Chart chart, ChartPlot plot)
    {
        this.chart = chart;
        this.plot = plot;
    }

    /// <inheritdoc/>
    public int Count => List().Count;

    /// <inheritdoc/>
    public ChartSeries this[int index] => List()[index];

    /// <summary>
    /// Adds a series that reads cells: <paramref name="values"/> and <paramref name="categories"/> are references such as
    /// <c>Sheet1!$B$2:$B$5</c>, <paramref name="name"/> the cell with its name. A scatter or bubble chart takes X values as
    /// the categories.
    /// </summary>
    /// <param name="values">The reference to the values.</param>
    /// <param name="categories">The reference to the categories or X values, or null.</param>
    /// <param name="name">The reference to the cell with the name, or null.</param>
    /// <param name="bubbleSizes">For a bubble chart, the reference to the sizes, or null.</param>
    /// <exception cref="ArgumentException">The reference to the values is empty.</exception>
    public ChartSeries Add(string values, string? categories = null, string? name = null, string? bubbleSizes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(values);
        ChartSeries series = AddEmpty();
        if (name is not null)
        {
            series.NameReference = name;
        }

        if (categories is not null)
        {
            series.Categories.Reference = categories;
        }

        series.Values.Reference = values;
        if (bubbleSizes is not null)
        {
            series.BubbleSizes.Reference = bubbleSizes;
        }

        return series;
    }

    /// <summary>Adds a series whose name and values are written in the chart, not read from cells.</summary>
    public ChartSeries Add(string? name, IEnumerable<string?>? categories, IEnumerable<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ChartSeries series = AddEmpty();
        series.Name = name;
        if (categories is not null)
        {
            series.Categories.SetText(categories);
        }

        series.Values.SetNumbers(values);
        return series;
    }

    /// <summary>Takes a series off the chart.</summary>
    /// <returns>Whether it was in this plot.</returns>
    public bool Remove(ChartSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!List().Contains(series))
        {
            return false;
        }

        if (chart.IsExtended)
        {
            chart.RemoveExtendedData(series);
        }

        series.Element.Remove();
        return true;
    }

    /// <inheritdoc/>
    public IEnumerator<ChartSeries> GetEnumerator() => List().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private List<ChartSeries> List() => [.. plot.SeriesElements().Select(chart.SeriesOf)];

    private ChartSeries AddEmpty() => chart.AddSeries(plot);
}
