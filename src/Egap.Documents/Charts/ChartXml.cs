using System.Globalization;
using System.Xml.Linq;

namespace Egap.Documents.Charts;

/// <summary>The namespaces of chart parts (ECMA-376 Part 1, 21.2, and MS-ODRAWXML for the newer chart types).</summary>
internal static class ChartNs
{
    public const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public const string ExtendedUri = "http://schemas.microsoft.com/office/drawing/2014/chartex";
    public static readonly XNamespace C = ChartUri;
    public static readonly XNamespace Cx = ExtendedUri;
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
}

/// <summary>
/// The place in a chart part of an element that may not exist yet. Reading never creates anything; writing makes the element
/// and its parents, each in the place the schema gives it, and leaves everything else in the part as it was.
/// </summary>
internal sealed class ChartNode
{
    private readonly XElement? element;

    /// <summary>The node's element or, if it has none yet, that of the nearest node above it that has.</summary>
    public XElement? Nearest => element ?? Find() ?? parent?.Nearest;
    private readonly ChartNode? parent;
    private readonly XName? name;
    private readonly XNamespace? ns;
    private readonly Func<XElement?>? find;
    private readonly Func<XElement>? ensure;

    public ChartNode(XElement element)
    {
        this.element = element;
    }

    /// <summary>A place worked out when it is used, for an element that may have to be made elsewhere in the part.</summary>
    public ChartNode(XNamespace ns, Func<XElement?> find, Func<XElement> ensure)
    {
        this.ns = ns;
        this.find = find;
        this.ensure = ensure;
    }

    private ChartNode(ChartNode parent, XName name)
    {
        this.parent = parent;
        this.name = name;
    }

    /// <summary>A place outside the part: for what the part's kind of chart cannot hold, so that writing there changes nothing.</summary>
    public static ChartNode Nowhere(XName name) => new(new XElement(name));

    public XNamespace Ns => ns ?? element?.Name.Namespace ?? parent!.Ns;

    public ChartNode this[string localName] => new(this, Ns + localName);

    public ChartNode Child(XName childName) => new(this, childName);

    public XElement? Find() => find is not null ? find() : element ?? parent!.Find()?.Element(name!);

    public XElement Ensure()
    {
        if (ensure is not null)
        {
            return ensure();
        }

        if (element is not null)
        {
            return element;
        }

        XElement holder = parent!.Ensure();
        if (holder.Element(name!) is { } found)
        {
            return found;
        }

        XElement made = new(name!);
        ChartOrder.Insert(holder, made);
        return made;
    }

    public void Remove() => Find()?.Remove();

    public bool Exists => Find() is not null;

    /// <summary>The <c>val</c> of a child, such as <c>&lt;c:gapWidth val="150"/&gt;</c>.</summary>
    public string? Value(string child) => (string?)Find()?.Element(Ns + child)?.Attribute("val");

    public void SetValue(string child, string? value)
    {
        if (value is null)
        {
            this[child].Remove();
        }
        else
        {
            this[child].Ensure().SetAttributeValue("val", value);
        }
    }

    /// <summary>A true-or-false child. The schema's default for an element with no <c>val</c> is true.</summary>
    public bool? Bool(string child)
    {
        XElement? found = Find()?.Element(Ns + child);
        return found is null ? null : ChartText.Bool((string?)found.Attribute("val")) ?? true;
    }

    public void SetBool(string child, bool? value) => SetValue(child, value is { } b ? (b ? "1" : "0") : null);

    public double? Double(string child) => ChartText.Double(Value(child));

    public void SetDouble(string child, double? value) => SetValue(child, value is { } d ? ChartText.Text(d) : null);

    public int? Int(string child) => ChartText.Int(Value(child));

    public void SetInt(string child, int? value) => SetValue(child, value?.ToString(CultureInfo.InvariantCulture));

    public string? Attribute(string attribute) => (string?)Find()?.Attribute(attribute);

    public void SetAttribute(string attribute, string? value)
    {
        if (value is not null || Exists)
        {
            Ensure().SetAttributeValue(attribute, value);
        }
    }

    public double? DoubleAttribute(string attribute) => ChartText.Double(Attribute(attribute));

    public void SetDoubleAttribute(string attribute, double? value) => SetAttribute(attribute, value is { } d ? ChartText.Text(d) : null);

    public bool? BoolAttribute(string attribute) => ChartText.Bool(Attribute(attribute));

    public void SetBoolAttribute(string attribute, bool? value) => SetAttribute(attribute, value is { } b ? (b ? "1" : "0") : null);
}

/// <summary>Numbers and flags as chart parts write them.</summary>
internal static class ChartText
{
    /// <summary>Renames an element, dropping any declaration on it that would give the new name's prefix another meaning.</summary>
    public static void Rename(XElement element, XName name)
    {
        string? prefix = element.Parent?.GetPrefixOfNamespace(name.Namespace);
        foreach (XAttribute declaration in element.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.LocalName == prefix && a.Value != name.NamespaceName).ToList())
        {
            declaration.Remove();
        }

        element.Name = name;
    }

    public static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static double? Double(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

    public static int? Int(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    public static bool? Bool(string? text) => text switch
    {
        "1" or "true" or "on" => true,
        "0" or "false" or "off" => false,
        _ => null,
    };
}

/// <summary>Where each child of a chart element goes: the schema's sequences, one list for the same element in both namespaces.</summary>
internal static class ChartOrder
{
    private const string Plot =
        "barDir ofPieType scatterStyle radarStyle wireframe grouping varyColors ser dLbls bandFmts dropLines hiLowLines upDownBars firstSliceAng holeSize "
        + "gapWidth gapDepth overlap splitType splitPos custSplit secondPieSize serLines bubble3D bubbleScale showNegBubbles sizeRepresents shape marker smooth axId extLst";

    private const string Axis =
        "axId scaling delete axPos majorGridlines minorGridlines title numFmt majorTickMark minorTickMark tickLblPos spPr txPr crossAx crosses crossesAt crossBetween "
        + "auto lblAlgn lblOffset baseTimeUnit tickLblSkip tickMarkSkip noMultiLvlLbl majorUnit majorTimeUnit minorUnit minorTimeUnit dispUnits extLst";

    private static readonly Dictionary<string, string[]> Orders = Build();

    private static Dictionary<string, string[]> Build()
    {
        Dictionary<string, string[]> orders = new()
        {
            ["chartSpace"] = Split("date1904 lang roundedCorners AlternateContent style clrMapOvr pivotSource protection chartData chart spPr txPr externalData printSettings fmtOvrs userShapes extLst"),
            ["chart"] = Split("title autoTitleDeleted pivotFmts view3D floor sideWall backWall plotArea legend plotVisOnly dispBlanksAs showDLblsOverMax extLst"),
            ["view3D"] = Split("rotX hPercent rotY depthPercent rAngAx perspective extLst"),
            ["plotArea"] = Split("layout plotAreaRegion barChart bar3DChart lineChart line3DChart areaChart area3DChart pieChart pie3DChart doughnutChart ofPieChart scatterChart bubbleChart radarChart stockChart surfaceChart surface3DChart "
                + "catAx valAx dateAx serAx axis dTable spPr extLst"),
            ["title"] = Split("tx layout overlay spPr txPr extLst"),
            ["legend"] = Split("legendPos legendEntry layout overlay spPr txPr extLst"),
            ["ser"] = Split("idx order tx spPr invertIfNegative pictureOptions marker explosion dPt dLbls trendline errBars cat val xVal yVal bubbleSize shape smooth bubble3D extLst"),
            ["dPt"] = Split("idx invertIfNegative marker bubble3D explosion spPr pictureOptions extLst"),
            ["marker"] = Split("symbol size spPr extLst"),
            ["dLbls"] = Split("dLbl delete numFmt spPr txPr dLblPos showLegendKey showVal showCatName showSerName showPercent showBubbleSize separator showLeaderLines leaderLines extLst"),
            ["dLbl"] = Split("idx delete layout tx numFmt spPr txPr dLblPos showLegendKey showVal showCatName showSerName showPercent showBubbleSize separator extLst"),
            ["trendline"] = Split("name spPr trendlineType order period forward backward intercept dispRSqr dispEq trendlineLbl extLst"),
            ["errBars"] = Split("errDir errBarType errValType noEndCap plus minus val spPr extLst"),
            ["scaling"] = Split("logBase orientation max min extLst"),
            ["numRef"] = Split("f numCache extLst"),
            ["strRef"] = Split("f strCache extLst"),
            ["multiLvlStrRef"] = Split("f multiLvlStrCache extLst"),
            ["manualLayout"] = Split("layoutTarget xMode yMode wMode hMode x y w h extLst"),
            ["spPr"] = Split("xfrm custGeom prstGeom noFill solidFill gradFill blipFill pattFill grpFill ln effectLst effectDag scene3d sp3d extLst"),
            ["ln"] = Split("noFill solidFill gradFill pattFill prstDash custDash round bevel miter headEnd tailEnd extLst"),
            ["txPr"] = Split("bodyPr lstStyle p"),
            ["rich"] = Split("bodyPr lstStyle p"),
            ["defRPr"] = Split("ln noFill solidFill gradFill blipFill pattFill grpFill effectLst effectDag highlight uLnTx uLn uFillTx uFill latin ea cs sym hlinkClick hlinkMouseOver rtl extLst"),
            ["chartData"] = Split("externalData data extLst"),
            ["data"] = Split("strDim numDim extLst"),
            ["plotAreaRegion"] = Split("plotSurface series extLst"),
            ["series"] = Split("tx spPr valueColors valueColorPositions dataPt dataLabels dataId layoutPr axisId extLst"),
            ["axis"] = Split("catScaling valScaling title units majorGridlines minorGridlines majorTickMarks minorTickMarks tickLabels numFmt spPr txPr extLst"),
            ["dataLabels"] = Split("numFmt spPr txPr visibility separator dataLabel dataLabelHidden extLst"),
            ["layoutPr"] = Split("parentLabelLayout regionLabelLayout visibility aggregation binning geography statistics subtotals extLst"),
        };
        string[] plot = Split(Plot);
        foreach (string name in Split("barChart bar3DChart lineChart line3DChart areaChart area3DChart pieChart pie3DChart doughnutChart ofPieChart scatterChart bubbleChart radarChart stockChart surfaceChart surface3DChart"))
        {
            orders[name] = plot;
        }

        string[] axis = Split(Axis);
        foreach (string name in Split("catAx valAx dateAx serAx"))
        {
            orders[name] = axis;
        }

        return orders;
    }

    private static string[] Split(string names) => names.Split(' ');

    /// <summary>Puts a new child where its parent's schema wants it: before the first child that comes later in the sequence.</summary>
    public static void Insert(XElement parent, XElement child)
    {
        if (!Orders.TryGetValue(parent.Name.LocalName, out string[]? order))
        {
            parent.Add(child);
            return;
        }

        int rank = Array.IndexOf(order, child.Name.LocalName);
        foreach (XElement existing in parent.Elements())
        {
            int other = Array.IndexOf(order, existing.Name.LocalName);
            if (other > rank || (rank < 0 && existing.Name.LocalName == "extLst"))
            {
                existing.AddBeforeSelf(child);
                return;
            }
        }

        parent.Add(child);
    }
}

/// <summary>An enumeration and the names a chart part writes for it.</summary>
internal sealed class ChartNames<T>(params (T Value, string Name)[] items)
    where T : struct, Enum
{
    public T? Parse(string? name)
    {
        foreach ((T value, string text) in items)
        {
            if (text == name)
            {
                return value;
            }
        }

        return null;
    }

    public string? Name(T? value)
    {
        if (value is null)
        {
            return null;
        }

        foreach ((T item, string text) in items)
        {
            if (EqualityComparer<T>.Default.Equals(item, value.Value))
            {
                return text;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "Not a value this chart element takes.");
    }
}
