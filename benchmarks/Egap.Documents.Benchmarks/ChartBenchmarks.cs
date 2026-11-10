using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Charts;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// Reading a chart part and drawing it: a column chart of six series whose fills and lines use theme colours with changes,
/// gradients and dashes, the DrawingML formatting charts share with shapes. <see cref="Load"/> reads the part;
/// <see cref="Draw"/> draws the read chart at 600 by 400 points.
/// </summary>
[MemoryDiagnoser]
public class ChartBenchmarks
{
    private byte[] part = [];
    private Chart chart = null!;
    private Raster<Rgba8> raster = null!;

    [GlobalSetup]
    public void Setup()
    {
        part = Encoding.UTF8.GetBytes(Part());
        chart = Chart.Load(new MemoryStream(part));
        raster = Raster.Create(600, 400, new Rgba8(255, 255, 255, 255));
    }

    [GlobalCleanup]
    public void Cleanup() => raster.Dispose();

    [Benchmark]
    public int Load()
    {
        using MemoryStream stream = new(part, writable: false);
        return Chart.Load(stream).Plots.Count;
    }

    [Benchmark]
    public void Draw()
    {
        using RasterCanvas<Rgba8> canvas = new(raster);
        chart.Draw(canvas, new RectF(0, 0, 600, 400));
    }

    private static string Part()
    {
        string[] fills =
        [
            """<a:solidFill><a:schemeClr val="accent1"><a:lumMod val="75000"/></a:schemeClr></a:solidFill><a:ln w="12700"><a:solidFill><a:schemeClr val="accent1"><a:shade val="50000"/></a:schemeClr></a:solidFill></a:ln>""",
            """<a:gradFill><a:gsLst><a:gs pos="0"><a:schemeClr val="accent2"><a:tint val="60000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="accent2"/></a:gs></a:gsLst><a:lin ang="5400000"/></a:gradFill>""",
            """<a:solidFill><a:srgbClr val="4472C4"><a:alpha val="80000"/></a:srgbClr></a:solidFill><a:ln w="19050"><a:solidFill><a:sysClr val="windowText" lastClr="000000"/></a:solidFill><a:prstDash val="dash"/></a:ln>""",
            """<a:pattFill prst="ltUpDiag"><a:fgClr><a:schemeClr val="accent4"/></a:fgClr><a:bgClr><a:schemeClr val="bg1"/></a:bgClr></a:pattFill>""",
            """<a:solidFill><a:schemeClr val="accent5"><a:lumMod val="60000"/><a:lumOff val="40000"/></a:schemeClr></a:solidFill><a:ln w="9525"><a:solidFill><a:prstClr val="black"/></a:solidFill><a:prstDash val="sysDot"/></a:ln>""",
            """<a:solidFill><a:schemeClr val="accent6"><a:satMod val="150000"/><a:hueOff val="600000"/></a:schemeClr></a:solidFill>""",
        ];
        StringBuilder series = new();
        for (int s = 0; s < fills.Length; s++)
        {
            series.Append(CultureInfo.InvariantCulture, $"""<c:ser><c:idx val="{s}"/><c:order val="{s}"/><c:tx><c:v>Series {s + 1}</c:v></c:tx><c:spPr>{fills[s]}</c:spPr><c:cat><c:strLit><c:ptCount val="12"/>""");
            for (int p = 0; p < 12; p++)
            {
                series.Append(CultureInfo.InvariantCulture, $"""<c:pt idx="{p}"><c:v>M{p + 1}</c:v></c:pt>""");
            }

            series.Append("""</c:strLit></c:cat><c:val><c:numLit><c:ptCount val="12"/>""");
            for (int p = 0; p < 12; p++)
            {
                series.Append(CultureInfo.InvariantCulture, $"""<c:pt idx="{p}"><c:v>{((s * 7) + (p * 13)) % 40 + 5}</c:v></c:pt>""");
            }

            series.Append("</c:numLit></c:val></c:ser>");
        }

        return $"""
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <c:chart><c:plotArea><c:barChart><c:barDir val="col"/><c:grouping val="clustered"/>{series}<c:axId val="1"/><c:axId val="2"/></c:barChart>
                <c:catAx><c:axId val="1"/><c:scaling/><c:axPos val="b"/><c:crossAx val="2"/></c:catAx>
                <c:valAx><c:axId val="2"/><c:scaling/><c:axPos val="l"/><c:majorGridlines><c:spPr><a:ln w="9525"><a:solidFill><a:schemeClr val="tx1"><a:lumMod val="15000"/><a:lumOff val="85000"/></a:schemeClr></a:solidFill></a:ln></c:spPr></c:majorGridlines><c:crossAx val="1"/></c:valAx>
              </c:plotArea><c:legend><c:legendPos val="r"/></c:legend></c:chart>
            </c:chartSpace>
            """;
    }
}
