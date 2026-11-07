using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Charts;

namespace Egap.Documents.Tests;

/// <summary>Drawing charts on an Egami canvas: every type draws, and the layout rules Excel's and Word's output showed.</summary>
public sealed class ChartRendererTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    private static Chart Made(ChartType type)
    {
        Chart chart = Chart.Create(type);
        chart.Plots[0].Series.Add("North", ["Jan", "Feb", "Mar"], [1, 2, 3]);
        chart.Plots[0].Series.Add("South", ["Jan", "Feb", "Mar"], [3, 1, 2]);
        chart.Plots[0].Series.Add("East", ["Jan", "Feb", "Mar"], [2, 3, 1]);
        return chart;
    }

    private static int Ink(Chart chart, int width = 360, int height = 216)
    {
        using Raster<Rgba8> raster = Raster.Create(width, height, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        chart.Draw(canvas, new RectF(0, 0, width, height));
        int ink = 0;
        for (int y = 8; y < height - 8; y++)
        {
            foreach (Rgba8 pixel in raster.Row(y)[8..(width - 8)])
            {
                ink += pixel.R < 250 || pixel.G < 250 || pixel.B < 250 ? 1 : 0;
            }
        }

        return ink;
    }

    private static Chart FromXml(string plot, string extra = "")
    {
        string part = $"""
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <c:chart><c:plotArea>{plot}</c:plotArea>{extra}</c:chart>
            </c:chartSpace>
            """;
        return Chart.Load(new MemoryStream(Encoding.UTF8.GetBytes(part)));
    }

    [Theory]
    [InlineData(ChartType.Column)]
    [InlineData(ChartType.Bar)]
    [InlineData(ChartType.Line)]
    [InlineData(ChartType.Area)]
    [InlineData(ChartType.Pie)]
    [InlineData(ChartType.Doughnut)]
    [InlineData(ChartType.Scatter)]
    [InlineData(ChartType.Bubble)]
    [InlineData(ChartType.Radar)]
    [InlineData(ChartType.Stock)]
    public void EveryTypeDraws(ChartType type)
    {
        Chart chart = Made(type);
        Assert.True(ChartRenderer.Draws(chart));
        Assert.True(Ink(chart) > 500, type.ToString());
    }

    [Fact]
    public void ThreeDAndExtendedChartsAreNotDrawnYet()
    {
        Assert.False(ChartRenderer.Draws(Chart.Create(ChartType.Column, threeD: true)));
        Assert.False(ChartRenderer.Draws(Chart.Create(ChartType.Waterfall)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 16)]
    [InlineData(120, 60)]
    public void ATinyChartStillDraws(int width, int height)
    {
        using Raster<Rgba8> raster = Raster.Create(Math.Max(width, 1), Math.Max(height, 1), White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        Made(ChartType.Column).Draw(canvas, new RectF(0, 0, width, height));
    }

    [Fact]
    public void BadValuesDrawNothingBad()
    {
        Chart chart = Chart.Create(ChartType.Line);
        chart.Plots[0].Series.Add("a", ["x", "y", "z", "w"], [double.NaN, double.PositiveInfinity, null, 1e300]);
        Ink(chart);
        Chart empty = Chart.Create(ChartType.Pie);
        Ink(empty);
    }

    [Fact]
    public void AnEmptyFontNameDrawsInTheThemesFont()
    {
        Chart chart = Made(ChartType.Column);
        chart.TextFormat.FontName = "";
        Assert.True(Ink(chart) > 500);
    }

    [Fact]
    public void ASmallPlotStopsAtOneStep()
    {
        Chart chart = Made(ChartType.Scatter);
        ChartBoxes boxes = new ChartRenderer(chart, null).Layout(60, 30);
        Assert.All(boxes.Axes.Where(a => !a.IsCategories), a => Assert.True(a.Scale.Maximum <= 100, a.Scale.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ALegendMovedByHandLeavesThePlotTheSameRoom()
    {
        Chart free = Made(ChartType.Column);
        free.Legend.Position = ChartLegendPosition.Right;
        Chart moved = Made(ChartType.Column);
        moved.Legend.Position = ChartLegendPosition.Right;
        moved.Legend.Layout.X = 0.1;
        moved.Legend.Layout.Y = 0.1;
        moved.Legend.Layout.XMode = ChartLayoutMode.Edge;
        moved.Legend.Layout.YMode = ChartLayoutMode.Edge;
        ChartBoxes a = new ChartRenderer(free, null).Layout(360, 216);
        ChartBoxes b = new ChartRenderer(moved, null).Layout(360, 216);
        Assert.Equal(a.Plot.Right, b.Plot.Right);
        Assert.True(b.Legend!.Value.Left < a.Legend!.Value.Left);
    }

    [Fact]
    public void AnOverlaidLegendLeavesNoRoom()
    {
        Chart over = Made(ChartType.Column);
        over.Legend.Position = ChartLegendPosition.Right;
        over.Legend.Overlay = true;
        Chart beside = Made(ChartType.Column);
        beside.Legend.Position = ChartLegendPosition.Right;
        Assert.True(new ChartRenderer(over, null).Layout(360, 216).Plot.Right > new ChartRenderer(beside, null).Layout(360, 216).Plot.Right);
    }

    [Fact]
    public void ADateAxisCountsInTheWidestUnitThatKeepsItsDatesApart()
    {
        // 1 May to 1 September 2002, a month apart.
        Chart chart = FromXml("""
            <c:areaChart><c:grouping val="standard"/>
              <c:ser><c:idx val="0"/><c:order val="0"/>
                <c:cat><c:numRef><c:numCache><c:formatCode>m/d/yyyy</c:formatCode><c:ptCount val="5"/>
                  <c:pt idx="0"><c:v>37377</c:v></c:pt><c:pt idx="1"><c:v>37408</c:v></c:pt><c:pt idx="2"><c:v>37438</c:v></c:pt><c:pt idx="3"><c:v>37469</c:v></c:pt><c:pt idx="4"><c:v>37500</c:v></c:pt>
                </c:numCache></c:numRef></c:cat>
                <c:val><c:numRef><c:numCache><c:ptCount val="5"/><c:pt idx="0"><c:v>1</c:v></c:pt><c:pt idx="1"><c:v>2</c:v></c:pt><c:pt idx="2"><c:v>3</c:v></c:pt><c:pt idx="3"><c:v>4</c:v></c:pt><c:pt idx="4"><c:v>5</c:v></c:pt></c:numCache></c:numRef></c:val>
              </c:ser><c:axId val="1"/><c:axId val="2"/></c:areaChart>
            <c:dateAx><c:axId val="1"/><c:scaling/><c:delete val="0"/><c:axPos val="b"/><c:crossAx val="2"/></c:dateAx>
            <c:valAx><c:axId val="2"/><c:scaling/><c:delete val="0"/><c:axPos val="l"/><c:crossAx val="1"/></c:valAx>
            """);
        DrawnAxis dates = new ChartRenderer(chart, null).Layout(360, 216).Axes.First(a => a.IsCategories);
        Assert.Equal(5, dates.Count);
        Assert.Equal(5, dates.Labels.Count);
    }

    [Fact]
    public void AColumnChartIsNotReversedInItsLegendButAStackAtTheSideIs()
    {
        Chart stacked = FromXml("""
            <c:barChart><c:barDir val="col"/><c:grouping val="stacked"/>
              <c:ser><c:idx val="0"/><c:order val="0"/><c:tx><c:v>First</c:v></c:tx><c:val><c:numLit><c:ptCount val="1"/><c:pt idx="0"><c:v>1</c:v></c:pt></c:numLit></c:val></c:ser>
              <c:ser><c:idx val="1"/><c:order val="1"/><c:tx><c:v>Second</c:v></c:tx><c:val><c:numLit><c:ptCount val="1"/><c:pt idx="0"><c:v>1</c:v></c:pt></c:numLit></c:val></c:ser>
              <c:axId val="1"/><c:axId val="2"/></c:barChart>
            <c:catAx><c:axId val="1"/><c:scaling/><c:delete val="0"/><c:axPos val="b"/><c:crossAx val="2"/></c:catAx>
            <c:valAx><c:axId val="2"/><c:scaling/><c:delete val="0"/><c:axPos val="l"/><c:crossAx val="1"/></c:valAx>
            """, """<c:legend><c:legendPos val="r"/></c:legend>""");
        Assert.Equal(["Second", "First"], new ChartRenderer(stacked, null).LegendTexts());
        stacked.Legend.Position = ChartLegendPosition.Bottom;
        Assert.Equal(["First", "Second"], new ChartRenderer(stacked, null).LegendTexts());
        Chart columns = Made(ChartType.Column);
        Assert.Equal(["North", "South", "East"], new ChartRenderer(columns, null).LegendTexts());
    }

    [Fact]
    public void AClusteredBarChartsLegendIsReversedWhereverItIs()
    {
        Chart bars = Made(ChartType.Bar);
        bars.Legend.Position = ChartLegendPosition.Bottom;
        Assert.Equal(["East", "South", "North"], new ChartRenderer(bars, null).LegendTexts());
    }

    [Fact]
    public void AWordChartDrawsInPlaceOfItsPicture()
    {
        Document document = new();
        Chart chart = Made(ChartType.Column);
        Island island = new(document, chart, 300, 180);
        document.AddSection().Body.AddParagraph("").Inlines.Add(island);
        using Raster<Rgba8> page = document.Layout().Pages[0].Render(72);
        int ink = 0;
        for (int y = 0; y < page.Height; y++)
        {
            foreach (Rgba8 pixel in page.Row(y))
            {
                ink += pixel.G < 250 ? 1 : 0;
            }
        }

        Assert.True(ink > 500, ink.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
