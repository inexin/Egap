using System.Xml.Linq;

namespace Egap.Documents.Charts;

/// <summary>
/// What each kind of plot can hold. Excel refuses a whole file for a label position its chart type does not allow, and for a
/// grouping, scatter style or radar style on the wrong type; the positions were read from Excel, and the rest from the part's
/// schema, which has no place for the others.
/// </summary>
internal static class ChartRules
{
    private static readonly ChartLabelPosition[] Beside = [ChartLabelPosition.Top, ChartLabelPosition.Bottom, ChartLabelPosition.Left, ChartLabelPosition.Right, ChartLabelPosition.Center];
    private static readonly ChartLabelPosition[] Round = [ChartLabelPosition.BestFit, ChartLabelPosition.Center, ChartLabelPosition.InsideEnd, ChartLabelPosition.OutsideEnd];
    private static readonly ChartLabelPosition[] Stacked = [ChartLabelPosition.Center, ChartLabelPosition.InsideEnd, ChartLabelPosition.InsideBase];
    private static readonly ChartLabelPosition[] Clustered = [.. Stacked, ChartLabelPosition.OutsideEnd];

    /// <summary>The positions a plot's labels can take. Radar, area, doughnut, surface and most 3D plots take none.</summary>
    public static IReadOnlyList<ChartLabelPosition> LabelPositions(XElement plot) => plot.Name.LocalName switch
    {
        "barChart" => (string?)plot.Element(ChartNs.C + "grouping")?.Attribute("val") is "stacked" or "percentStacked" ? Stacked : Clustered,
        "lineChart" or "scatterChart" or "bubbleChart" or "stockChart" => Beside,
        "pieChart" or "pie3DChart" or "ofPieChart" => Round,
        _ => [],
    };

    /// <summary>The plot a chart element belongs to, or null when it is in none.</summary>
    public static XElement? PlotOf(XElement? element) => element?.AncestorsAndSelf().FirstOrDefault(e => e.Parent?.Name == ChartNs.C + "plotArea");

    /// <summary>Takes away the label positions a plot no longer allows, after its grouping changed.</summary>
    public static void RemoveLabelPositionsNotAllowed(XElement plot)
    {
        IReadOnlyList<ChartLabelPosition> allowed = LabelPositions(plot);
        foreach (XElement position in plot.Descendants(ChartNs.C + "dLblPos").ToList())
        {
            if (ChartDataLabels.ParsePosition((string?)position.Attribute("val")) is { } value && !allowed.Contains(value))
            {
                position.Remove();
            }
        }
    }
}
