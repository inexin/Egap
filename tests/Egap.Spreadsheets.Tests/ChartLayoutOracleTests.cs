using System.Globalization;
using System.Text;

using Egami.Drawing;

using Path = System.IO.Path;

using Egap.Documents.Charts;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Where the renderer lays a chart's parts out, against where Excel itself says they are: tools/excel-chart-layout-oracle.ps1 made
/// Oracle/chart-layout.xlsx and read Oracle/chart-layout.excel.tsv from Excel. Excel's positions count from four points inside the chart's corner.
/// </summary>
public sealed class ChartLayoutOracleTests
{
    private const double ExcelOrigin = 4;

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Oracle");

    private static (float Width, float Height) Size(string name) => name.Split('-') switch
    {
        [_, "720"] => (720, 432),
        [_, "480"] => (480, 288),
        [_, "240"] => (240, 150),
        _ => (360, 216),
    };

    private static List<(string Chart, string Key, double Expected, double Actual)> Differences()
    {
        Dictionary<string, Dictionary<string, double>> facts = [];
        foreach (string line in File.ReadAllLines(Path.Combine(Folder, "chart-layout.excel.tsv")))
        {
            string[] parts = line.Split('\t');
            string name = parts[0][(parts[0].IndexOf('!', StringComparison.Ordinal) + 1)..];
            if (!facts.TryGetValue(name, out Dictionary<string, double>? chart))
            {
                facts[name] = chart = [];
            }

            chart[parts[1]] = double.Parse(parts[2], CultureInfo.InvariantCulture);
        }

        Workbook book = Workbook.Load(Path.Combine(Folder, "chart-layout.xlsx"));
        List<(string, string, double, double)> found = [];
        foreach (SheetDrawing drawing in book.Worksheets["Charts"]!.Drawings)
        {
            if (drawing.Chart is not { } chart || !facts.TryGetValue(drawing.Name, out Dictionary<string, double>? excel))
            {
                continue;
            }

            (float width, float height) = Size(drawing.Name);
            ChartBoxes boxes = new ChartRenderer(chart, null).Layout(width, height);
            Add("plot", boxes.Plot, excel, "plot", drawing.Name, found);
            if (boxes.Title is { } title)
            {
                Add("title", title, excel, "title", drawing.Name, found);
            }

            if (boxes.Legend is { } legend)
            {
                Add("legend", legend, excel, "legend", drawing.Name, found);
            }

            foreach (DrawnAxis axis in boxes.Axes.Where(a => !a.IsCategories && !a.Horizontal || a.Model.Kind == ChartAxisKind.Value))
            {
                string label = axis.Horizontal && chart.Type is ChartType.Scatter or ChartType.Bubble ? "axis11" : "axis21";
                foreach ((string key, double actual) in new[] { ("min", axis.Scale.Minimum), ("max", axis.Scale.Maximum), ("major", axis.Scale.Major) })
                {
                    if (excel.TryGetValue($"{label}.{key}", out double expected))
                    {
                        found.Add((drawing.Name, $"scale.{key}", expected, actual));
                    }
                }
            }
        }

        return found;
    }

    private static void Add(string name, RectF box, Dictionary<string, double> excel, string prefix, string chart, List<(string, string, double, double)> found)
    {
        foreach ((string side, double actual, double origin) in new[]
        {
            ("left", (double)box.Left, ExcelOrigin), ("top", (double)box.Top, ExcelOrigin), ("width", (double)box.Width, 0), ("height", (double)box.Height, 0),
        })
        {
            if (excel.TryGetValue($"{prefix}.{side}", out double expected))
            {
                found.Add((chart, $"{name}.{side}", expected + origin, actual));
            }
        }
    }

    /// <summary>Bubble sizes make Excel's axes depend on more than the data's extent, which is left to the visual checks.</summary>
    [Fact]
    public void AutomaticScalesAreExcels()
    {
        List<string> wrong = [.. Differences().Where(d => d.Key.StartsWith("scale.", StringComparison.Ordinal) && !d.Chart.StartsWith("Bubble", StringComparison.Ordinal) && Math.Abs(d.Expected - d.Actual) > Math.Abs(d.Expected) * 1e-6)
            .Select(d => $"{d.Chart} {d.Key}: Excel {d.Expected}, here {d.Actual}")];
        Report();
        Assert.Empty(wrong);
    }

    /// <summary>The parts lie where Excel puts them, to two points: what stays out is text measured with hinting and a locale's percent sign.</summary>
    [Fact]
    public void PartsLieWhereExcelPutsThem()
    {
        List<(string Chart, string Key, double Expected, double Actual)> parts = [.. Differences().Where(d => !d.Key.StartsWith("scale.", StringComparison.Ordinal))];
        int close = parts.Count(d => Math.Abs(d.Expected - d.Actual) <= 2);
        Assert.True(parts.Count > 1000, $"Only {parts.Count} parts compared.");
        Assert.True(close >= parts.Count * 0.97, $"{close} of {parts.Count} parts within two points.");
    }

    private static void Report()
    {
        if (Environment.GetEnvironmentVariable("EGAP_CHART_LAYOUT_REPORT") is not { Length: > 0 } path)
        {
            return;
        }

        StringBuilder text = new();
        foreach ((string chart, string key, double expected, double actual) in Differences())
        {
            text.Append(CultureInfo.InvariantCulture, $"{chart}\t{key}\t{expected:0.##}\t{actual:0.##}\t{actual - expected:0.##}\n");
        }

        File.WriteAllText(path, text.ToString());
    }
}
