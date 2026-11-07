using System.Xml.Linq;

namespace Egap.Documents.Charts;

/// <summary>What an axis measures.</summary>
public enum ChartAxisKind
{
    /// <summary>Categories in order.</summary>
    Category,

    /// <summary>Values on a scale.</summary>
    Value,

    /// <summary>Dates on a time scale.</summary>
    Date,

    /// <summary>The series of a 3D chart.</summary>
    Series,
}

/// <summary>Which side of the plot an axis sits on.</summary>
public enum ChartAxisPosition
{
    /// <summary>Below.</summary>
    Bottom,

    /// <summary>On the left.</summary>
    Left,

    /// <summary>On the right.</summary>
    Right,

    /// <summary>Above.</summary>
    Top,
}

/// <summary>Where tick marks sit against an axis.</summary>
public enum ChartTickMark
{
    /// <summary>No marks.</summary>
    None,

    /// <summary>Inside the plot.</summary>
    Inside,

    /// <summary>Outside the plot.</summary>
    Outside,

    /// <summary>Across the axis.</summary>
    Cross,
}

/// <summary>Where tick labels sit.</summary>
public enum ChartTickLabelPosition
{
    /// <summary>No labels.</summary>
    None,

    /// <summary>Beside the axis.</summary>
    NextToAxis,

    /// <summary>At the high end of the other axis.</summary>
    High,

    /// <summary>At the low end of the other axis.</summary>
    Low,
}

/// <summary>Where an axis crosses the other one, short of a set value.</summary>
public enum ChartCrosses
{
    /// <summary>At zero, or as near as the scale goes.</summary>
    AutoZero,

    /// <summary>At the other axis's lowest value.</summary>
    Minimum,

    /// <summary>At the other axis's highest value.</summary>
    Maximum,
}

/// <summary>A unit of a date axis.</summary>
public enum ChartTimeUnit
{
    /// <summary>Days.</summary>
    Days,

    /// <summary>Months.</summary>
    Months,

    /// <summary>Years.</summary>
    Years,
}

/// <summary>An axis of a chart.</summary>
public sealed class ChartAxis
{
    private static readonly ChartNames<ChartAxisPosition> Positions = new(
        (ChartAxisPosition.Bottom, "b"), (ChartAxisPosition.Left, "l"), (ChartAxisPosition.Right, "r"), (ChartAxisPosition.Top, "t"));

    private static readonly ChartNames<ChartTickMark> Marks = new(
        (ChartTickMark.None, "none"), (ChartTickMark.Inside, "in"), (ChartTickMark.Outside, "out"), (ChartTickMark.Cross, "cross"));

    private static readonly ChartNames<ChartTickLabelPosition> Labels = new(
        (ChartTickLabelPosition.None, "none"), (ChartTickLabelPosition.NextToAxis, "nextTo"), (ChartTickLabelPosition.High, "high"), (ChartTickLabelPosition.Low, "low"));

    private static readonly ChartNames<ChartCrosses> Crossings = new(
        (ChartCrosses.AutoZero, "autoZero"), (ChartCrosses.Minimum, "min"), (ChartCrosses.Maximum, "max"));

    private static readonly ChartNames<ChartTimeUnit> Units = new((ChartTimeUnit.Days, "days"), (ChartTimeUnit.Months, "months"), (ChartTimeUnit.Years, "years"));

    private readonly Chart chart;
    private readonly ChartNode node;
    private readonly bool extended;

    internal ChartAxis(Chart chart, XElement element)
    {
        this.chart = chart;
        Element = element;
        node = new ChartNode(element);
        extended = chart.IsExtended;
    }

    /// <summary>What the axis measures.</summary>
    public ChartAxisKind Kind => Element.Name.LocalName switch
    {
        "valAx" => ChartAxisKind.Value,
        "dateAx" => ChartAxisKind.Date,
        "serAx" => ChartAxisKind.Series,
        "axis" when Element.Element(ChartNs.Cx + "valScaling") is not null => ChartAxisKind.Value,
        _ => ChartAxisKind.Category,
    };

    /// <summary>The number that ties the axis to its plots in the file.</summary>
    public long Id => long.TryParse(extended ? node.Attribute("id") : node.Value("axId"), out long id) ? id : -1;

    /// <summary>The axis this one crosses, or null.</summary>
    public ChartAxis? CrossAxis => extended ? null : chart.Axes.FirstOrDefault(a => a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == node.Value("crossAx"));

    /// <summary>Whether the axis is hidden.</summary>
    public bool Hidden
    {
        get => extended ? node.BoolAttribute("hidden") == true : node.Bool("delete") == true;
        set
        {
            if (extended)
            {
                node.SetBoolAttribute("hidden", value);
            }
            else
            {
                node.SetBool("delete", value);
            }
        }
    }

    /// <summary>Which side of the plot the axis sits on, or null; c: charts only.</summary>
    public ChartAxisPosition? Position
    {
        get => extended ? null : Positions.Parse(node.Value("axPos"));
        set
        {
            if (!extended)
            {
                node.SetValue("axPos", Positions.Name(value));
            }
        }
    }

    /// <summary>Whether the axis runs backwards; c: charts only.</summary>
    public bool Reversed
    {
        get => !extended && node["scaling"].Value("orientation") == "maxMin";
        set
        {
            if (!extended)
            {
                node["scaling"].SetValue("orientation", value ? "maxMin" : "minMax");
            }
        }
    }

    /// <summary>The lowest value shown, or null when the chart decides.</summary>
    public double? Minimum
    {
        get => extended ? ValueScaling.DoubleAttribute("min") : node["scaling"].Double("min");
        set
        {
            if (extended)
            {
                ValueScaling.SetDoubleAttribute("min", value);
            }
            else
            {
                node["scaling"].SetDouble("min", value);
            }
        }
    }

    /// <summary>The highest value shown, or null when the chart decides.</summary>
    public double? Maximum
    {
        get => extended ? ValueScaling.DoubleAttribute("max") : node["scaling"].Double("max");
        set
        {
            if (extended)
            {
                ValueScaling.SetDoubleAttribute("max", value);
            }
            else
            {
                node["scaling"].SetDouble("max", value);
            }
        }
    }

    /// <summary>The base of a logarithmic scale, or null for a straight one; c: charts only.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The base is outside 2 to 1000.</exception>
    public double? LogBase
    {
        get => extended ? null : node["scaling"].Double("logBase");
        set
        {
            if (value is < 2 or > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A logarithm's base is from 2 to 1000.");
            }

            if (!extended)
            {
                node["scaling"].SetDouble("logBase", value);
            }
        }
    }

    /// <summary>The distance between major ticks, or null when the chart decides.</summary>
    public double? MajorUnit
    {
        get => extended ? ValueScaling.DoubleAttribute("majorUnit") : node.Double("majorUnit");
        set
        {
            if (extended)
            {
                ValueScaling.SetDoubleAttribute("majorUnit", value);
            }
            else
            {
                node.SetDouble("majorUnit", value);
            }
        }
    }

    /// <summary>The distance between minor ticks, or null when the chart decides.</summary>
    public double? MinorUnit
    {
        get => extended ? ValueScaling.DoubleAttribute("minorUnit") : node.Double("minorUnit");
        set
        {
            if (extended)
            {
                ValueScaling.SetDoubleAttribute("minorUnit", value);
            }
            else
            {
                node.SetDouble("minorUnit", value);
            }
        }
    }

    /// <summary>Whether the axis has major gridlines.</summary>
    public bool HasMajorGridlines
    {
        get => node["majorGridlines"].Exists;
        set => SetPresent("majorGridlines", value);
    }

    /// <summary>Whether the axis has minor gridlines.</summary>
    public bool HasMinorGridlines
    {
        get => node["minorGridlines"].Exists;
        set => SetPresent("minorGridlines", value);
    }

    /// <summary>The look of the major gridlines.</summary>
    public ChartFormat MajorGridlines => new(node["majorGridlines"]);

    /// <summary>The look of the minor gridlines.</summary>
    public ChartFormat MinorGridlines => new(node["minorGridlines"]);

    /// <summary>Whether the axis has a title.</summary>
    public bool HasTitle
    {
        get => node["title"].Exists;
        set => SetPresent("title", value);
    }

    /// <summary>The axis's title. Writing to it gives the axis one.</summary>
    public ChartTitle Title => new(node["title"], extended);

    /// <summary>The number format of the tick labels, or null to follow the cells.</summary>
    public string? NumberFormat
    {
        get => NumberFormatLinked ? null : node["numFmt"].Attribute("formatCode");
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

    /// <summary>The major tick marks, or null for the chart's default.</summary>
    public ChartTickMark? MajorTickMark
    {
        get => extended ? Marks.Parse(node["majorTickMarks"].Attribute("type")) : Marks.Parse(node.Value("majorTickMark"));
        set
        {
            if (extended)
            {
                node["majorTickMarks"].SetAttribute("type", Marks.Name(value));
            }
            else
            {
                node.SetValue("majorTickMark", Marks.Name(value));
            }
        }
    }

    /// <summary>The minor tick marks, or null for the chart's default.</summary>
    public ChartTickMark? MinorTickMark
    {
        get => extended ? Marks.Parse(node["minorTickMarks"].Attribute("type")) : Marks.Parse(node.Value("minorTickMark"));
        set
        {
            if (extended)
            {
                node["minorTickMarks"].SetAttribute("type", Marks.Name(value));
            }
            else
            {
                node.SetValue("minorTickMark", Marks.Name(value));
            }
        }
    }

    /// <summary>Where the tick labels sit, or null for the chart's default. A cx: axis has them beside it or not at all.</summary>
    public ChartTickLabelPosition? TickLabelPosition
    {
        get => extended
            ? (node["tickLabels"].Exists ? ChartTickLabelPosition.NextToAxis : ChartTickLabelPosition.None)
            : Labels.Parse(node.Value("tickLblPos"));
        set
        {
            if (extended)
            {
                SetPresent("tickLabels", value is not ChartTickLabelPosition.None);
            }
            else
            {
                node.SetValue("tickLblPos", Labels.Name(value));
            }
        }
    }

    /// <summary>Where the axis crosses the other, or null when at a set value or not said; c: charts only.</summary>
    public ChartCrosses? Crosses
    {
        get => extended ? null : Crossings.Parse(node.Value("crosses"));
        set
        {
            if (!extended)
            {
                node["crossesAt"].Remove();
                node.SetValue("crosses", Crossings.Name(value));
            }
        }
    }

    /// <summary>The value at which the axis crosses the other, or null; c: charts only.</summary>
    public double? CrossesAt
    {
        get => extended ? null : node.Double("crossesAt");
        set
        {
            if (!extended)
            {
                if (value is not null)
                {
                    node["crosses"].Remove();
                }

                node.SetDouble("crossesAt", value);
            }
        }
    }

    /// <summary>For a value axis: whether it crosses between categories rather than at them; null when not said.</summary>
    public bool? CrossesBetweenCategories
    {
        get => node.Value("crossBetween") switch
        {
            "between" => true,
            "midCat" => false,
            _ => null,
        };
        set
        {
            if (!extended)
            {
                node.SetValue("crossBetween", value is { } between ? (between ? "between" : "midCat") : null);
            }
        }
    }

    /// <summary>For a category axis: how many categories between labels, or null when the chart decides.</summary>
    public int? TickLabelSkip
    {
        get => Classic.Int("tickLblSkip");
        set => Classic.SetInt("tickLblSkip", value);
    }

    /// <summary>For a category axis: how many categories between tick marks, or null when the chart decides.</summary>
    public int? TickMarkSkip
    {
        get => Classic.Int("tickMarkSkip");
        set => Classic.SetInt("tickMarkSkip", value);
    }

    /// <summary>For a category axis: labels of several levels are drawn as one, or null when not said.</summary>
    public bool? NoMultiLevelLabels
    {
        get => Classic.Bool("noMultiLvlLbl");
        set => Classic.SetBool("noMultiLvlLbl", value);
    }

    /// <summary>For a date axis: the unit of its points, or null when the chart decides.</summary>
    public ChartTimeUnit? BaseTimeUnit
    {
        get => Units.Parse(Classic.Value("baseTimeUnit"));
        set => Classic.SetValue("baseTimeUnit", Units.Name(value));
    }

    /// <summary>For a date axis: the unit of <see cref="MajorUnit"/>, or null.</summary>
    public ChartTimeUnit? MajorTimeUnit
    {
        get => Units.Parse(Classic.Value("majorTimeUnit"));
        set => Classic.SetValue("majorTimeUnit", Units.Name(value));
    }

    /// <summary>For a date axis: the unit of <see cref="MinorUnit"/>, or null.</summary>
    public ChartTimeUnit? MinorTimeUnit
    {
        get => Units.Parse(Classic.Value("minorTimeUnit"));
        set => Classic.SetValue("minorTimeUnit", Units.Name(value));
    }

    /// <summary>For a value axis: the unit its labels count in, such as <c>thousands</c> or <c>percentage</c>; null for ones.</summary>
    public string? DisplayUnit => extended ? node["units"].Attribute("unit") : node["dispUnits"].Value("builtInUnit");

    /// <summary>The axis line's look.</summary>
    public ChartFormat Format => new(node);

    /// <summary>How the tick labels look.</summary>
    public ChartTextFormat TextFormat => new(node["txPr"]);

    internal XElement Element { get; }

    /// <summary>What only a c: axis has; on a cx: axis, nowhere.</summary>
    private ChartNode Classic => extended ? ChartNode.Nowhere(Element.Name) : node;

    /// <summary>A cx: value axis's scale; a category axis has its own, and no other beside it.</summary>
    private ChartNode ValueScaling => Element.Element(ChartNs.Cx + "catScaling") is null ? node["valScaling"] : ChartNode.Nowhere(ChartNs.Cx + "valScaling");

    private bool NumberFormatLinked => node["numFmt"].BoolAttribute("sourceLinked") == true;

    private void SetPresent(string name, bool present)
    {
        if (present)
        {
            node[name].Ensure();
        }
        else
        {
            node[name].Remove();
        }
    }
}
