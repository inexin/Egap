using System.Xml.Linq;

using Egap.Documents.Charts;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets;

/// <summary>A sheet that holds one chart and no cells. Its chart can be read and edited; the sheet itself is kept as the file had it.</summary>
public sealed class ChartSheet
{
    private readonly Workbook book;
    private readonly OpcPart? part;
    private readonly OpcPackage? package;
    private Chart? chart;
    private bool chartRead;

    internal ChartSheet(Workbook book, string name, SheetVisibility visibility, OpcPart part, OpcPackage package)
    {
        this.book = book;
        Name = name;
        Visibility = visibility;
        this.part = part;
        this.package = package;
    }

    /// <summary>A chart sheet read from a file that keeps no parts, such as an XLS file's: its chart is made in memory.</summary>
    internal ChartSheet(Workbook book, string name, SheetVisibility visibility, Chart chart, Worksheet? after)
    {
        this.book = book;
        Name = name;
        Visibility = visibility;
        this.chart = chart;
        chartRead = true;
        After = after;
    }

    /// <summary>For a chart sheet made in memory, the worksheet it comes after in the tabs, or null when it comes first.</summary>
    internal Worksheet? After { get; }

    /// <summary>Whether the sheet has no parts of its own and is written whole.</summary>
    internal bool IsNew => part is null;

    /// <summary>The sheet's name.</summary>
    public string Name { get; }

    /// <summary>Whether the sheet's tab shows.</summary>
    public SheetVisibility Visibility { get; }

    /// <summary>The chart, or null when its part cannot be read.</summary>
    public Chart? Chart
    {
        get
        {
            if (!chartRead)
            {
                chartRead = true;
                ChartPart = FindPart();
                chart = ChartPart is { } found ? Chart.Read(found) : null;
            }

            return chart;
        }
    }

    /// <summary>The chart as it was read, if anyone has asked for it.</summary>
    internal Chart? LoadedChart => chartRead ? chart : null;

    internal OpcPart? ChartPart { get; private set; }

    /// <summary>Reads the cells the chart's references name into the values the chart keeps. Saving does this for a chart that changed.</summary>
    /// <exception cref="InvalidOperationException">The chart's part cannot be read.</exception>
    public void RefreshChart() => ChartCells.Fill(Chart ?? throw new InvalidOperationException("The sheet's chart cannot be read."), book);

    /// <summary>The paper, orientation and margins in points the sheet's part names; a chart sheet prints landscape unless it says not to.</summary>
    internal (int? Paper, bool Portrait, (double Left, double Top, double Right, double Bottom) Margins) ReadPageSetup()
    {
        XElement? root = part is null ? null : SafeXml.TryLoad(part)?.Root;
        XElement? setup = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "pageSetup");
        XElement? margins = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "pageMargins");
        double Margin(string name, double inches)
        {
            string? text = (string?)margins?.Attribute(name);
            double value = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double read) && read is >= 0 and < 100 ? read : inches;
            return value * 72;
        }

        int? paper = int.TryParse((string?)setup?.Attribute("paperSize"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int code) ? code : null;
        return (paper, (string?)setup?.Attribute("orientation") == "portrait", (Margin("left", 0.7), Margin("top", 0.75), Margin("right", 0.7), Margin("bottom", 0.75)));
    }

    private OpcPart? FindPart()
    {
        if (part is null)
        {
            return null;
        }

        if (SafeXml.TryLoad(part)?.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "drawing") is not { } element
            || Related(part, (string?)element.Attribute(XlsxNames.R + "id")) is not { } drawing
            || SafeXml.TryLoad(drawing)?.Root is not { } root)
        {
            return null;
        }

        XElement? reference = root.Descendants().FirstOrDefault(e => e.Name == SheetObjectReader.C + "chart" || e.Name == SheetObjectReader.Cx + "chart");
        return Related(drawing, (string?)reference?.Attribute(XlsxNames.R + "id"));
    }

    private OpcPart? Related(OpcPart from, string? id)
    {
        OpcRelationship? relationship = id is null ? null : from.GetRelationship(id);
        return relationship is { External: false } ? package?.GetPart(relationship.TargetName) : null;
    }
}
