using System.Numerics;
using System.Text;
using System.Xml.Linq;

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

    private static ChartRenderer Drawn(Chart chart, int width = 360, int height = 216)
    {
        using Raster<Rgba8> raster = Raster.Create(width, height, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        ChartRenderer renderer = new(chart, null);
        renderer.Draw(canvas, new RectF(0, 0, width, height));
        return renderer;
    }

    /// <summary>Where a colour runs down one column of a drawn chart.</summary>
    private static (int Top, int Bottom) RunOf(Chart chart, Rgba8 color, int x, int from, int to)
    {
        using Raster<Rgba8> raster = Raster.Create(360, 216, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        chart.Draw(canvas, new RectF(0, 0, 360, 216));
        int top = -1;
        int bottom = -1;
        for (int y = from; y < to; y++)
        {
            Rgba8 pixel = raster.Row(y)[x];
            if (Math.Abs(pixel.R - color.R) < 4 && Math.Abs(pixel.G - color.G) < 4 && Math.Abs(pixel.B - color.B) < 4)
            {
                top = top < 0 ? y : top;
                bottom = y;
            }
        }

        return (top, bottom);
    }

    private static void WithErrorBars(ChartSeries series, string kind, double amount) =>
        series.Element.Add(new XElement(
            ChartNs.C + "errBars",
            new XElement(ChartNs.C + "errValType", new XAttribute("val", kind)),
            new XElement(ChartNs.C + "val", new XAttribute("val", amount.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

    private static Vector2 Middle(RectF box) => new(box.X + (box.Width / 2), box.Y + (box.Height / 2));

    private static Chart Labelled(ChartType type, ChartGrouping? grouping, params double[][] series)
    {
        Chart chart = Chart.Create(type);
        ChartPlot plot = chart.Plots[0];
        plot.Grouping = grouping;
        string[] categories = [.. Enumerable.Range(1, series[0].Length).Select(i => "C" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        for (int k = 0; k < series.Length; k++)
        {
            ChartSeries one = plot.Series.Add("S" + k.ToString(System.Globalization.CultureInfo.InvariantCulture), categories, [.. series[k].Select(v => (double?)v)]);
            one.DataLabels.ShowValue = true;
        }

        return chart;
    }

    [Theory]
    [InlineData(4.3, 5, 0.5)]
    [InlineData(4.0, 4.5, 0.5)]
    [InlineData(0.32, 0.35, 0.05)]
    [InlineData(57, 60, 10)]
    public void ASingleValueHasTheSameHeadroomAsAnyOther(double value, double maximum, double step)
    {
        // As Excel's axis for one column.
        ChartScale scale = new ChartRenderer(Labelled(ChartType.Column, null, [value]), null).Layout(432, 252).Axes.First(a => !a.IsCategories).Scale;
        Assert.Equal(maximum, scale.Maximum, 9);
        Assert.Equal(step, scale.Major, 9);
        Assert.Equal(0, scale.Minimum, 9);
    }

    [Fact]
    public void ATitleInASmallChartKeepsOneLine()
    {
        Chart chart = Labelled(ChartType.Column, null, [1, 2]);
        chart.HasTitle = true;
        chart.Title.Text = "Chart Title";
        float wide = new ChartRenderer(chart, null).Layout(432, 252).Title!.Value.Height;
        RectF small = new ChartRenderer(chart, null).Layout(94, 93).Title!.Value;
        Assert.Equal(wide, small.Height, 0.01f);
        Assert.True(small.Width <= 94 * 0.8f + 4, small.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ALongTitleWrapsWhereThereIsRoom()
    {
        Chart chart = Labelled(ChartType.Column, null, [1, 2]);
        chart.HasTitle = true;
        chart.Title.Text = "Chart Title";
        float one = new ChartRenderer(chart, null).Layout(432, 252).Title!.Value.Height;
        chart.Title.Text = string.Join(' ', Enumerable.Repeat("Chart Title", 10));
        Assert.True(new ChartRenderer(chart, null).Layout(432, 252).Title!.Value.Height > one * 1.5f);
    }

    [Fact]
    public void AWrappedLegendLeavesOutWhatDoesNotFit()
    {
        static Chart With(int entries)
        {
            Chart chart = Chart.Create(ChartType.Column);
            foreach (int k in Enumerable.Range(0, entries))
            {
                chart.Plots[0].Series.Add("Long name", ["A"], [1]);
            }

            chart.HasLegend = true;
            chart.Legend.Position = ChartLegendPosition.Right;
            return chart;
        }

        // In a chart this small the second entry would take the legend past half the height.
        float one = new ChartRenderer(With(1), null).Layout(94, 93).Legend!.Value.Height;
        Assert.Equal(one, new ChartRenderer(With(3), null).Layout(94, 93).Legend!.Value.Height);
        Assert.True(new ChartRenderer(With(3), null).Layout(300, 300).Legend!.Value.Height > new ChartRenderer(With(1), null).Layout(300, 300).Legend!.Value.Height);
    }

    [Fact]
    public void ALevelTitleBesideAVerticalAxisWraps()
    {
        Chart chart = Labelled(ChartType.Column, null, [1, 2]);
        ChartAxis values = chart.Plots[0].Axes.First(a => a.Kind == ChartAxisKind.Value);
        values.HasTitle = true;
        values.Title.Text = "Horizontal, long axis title which breaks";
        values.Title.TextFormat.Rotation = 0;
        RectF box = new ChartRenderer(chart, null).Layout(432, 252).Axes.First(a => !a.IsCategories).TitleBox!.Value;
        Assert.True(box.Width <= (432 * 0.19f) + 4, box.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(box.Height > box.Width / 2, box.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void LabelsLeaveAPie86PercentOfItsRoom()
    {
        // Measured in Excel at three sizes, with and without explosion.
        Chart bare = Chart.Create(ChartType.Pie);
        bare.Plots[0].Series.Add("S", ["A", "B"], [1, 1]);
        float room = new ChartRenderer(bare, null).Layout(265, 192).Plot.Width;
        Chart labelled = Labelled(ChartType.Pie, null, [1, 1]);
        Assert.Equal(room * 0.86f, new ChartRenderer(labelled, null).Layout(265, 192).Plot.Width, 0.5f);
    }

    [Fact]
    public void AnExplosionDoesNotChangeThePlot()
    {
        Chart plain = Labelled(ChartType.Pie, null, [1, 1]);
        float side = new ChartRenderer(plain, null).Layout(265, 192).Plot.Width;
        plain.Plots[0].Series[0].Explosion = 25;
        Assert.Equal(side, new ChartRenderer(plain, null).Layout(265, 192).Plot.Width);
    }

    [Fact]
    public void AFlatLegendInWordTakesEachEntrysOwnWidth()
    {
        Chart chart = Labelled(ChartType.Pie, null, [1, 1, 1]);
        chart.HasLegend = true;
        chart.Legend.Position = ChartLegendPosition.Top;
        chart.Plots[0].VaryColors = true;
        float excel = new ChartRenderer(chart, null).Layout(360, 216).Legend!.Value.Width;
        float word = new ChartRenderer(chart, new ChartDrawOptions { Host = ChartHost.Word }).Layout(360, 216).Legend!.Value.Width;
        Assert.True(word < excel, $"{word} {excel}");
    }

    [Fact]
    public void ARadarWithLongLabelsInASmallChartStillHasAPlot()
    {
        Chart chart = Chart.Create(ChartType.Radar);
        chart.Plots[0].Series.Add("S", ["1/5/2002", "1/6/2002", "1/7/2002", "1/8/2002", "1/9/2002"], [1, 2, 3, 4, 5]);
        Assert.True(new ChartRenderer(chart, null).Layout(94, 93).Plot.Width >= 4);
    }

    [Fact]
    public void AReversedBarChartPutsItsFirstSeriesOnTop()
    {
        // As Excel draws it: the first category and, in each, the first series at the top, with each label beside its own bar.
        Chart chart = Labelled(ChartType.Bar, ChartGrouping.Clustered, [5, 4], [2, 3]);
        ChartAxis categories = chart.Plots[0].Axes.First(a => a.Kind == ChartAxisKind.Category);
        categories.Reversed = true;
        ChartRenderer renderer = Drawn(chart);
        RectF five = renderer.DrawnLabels.First(l => l.Text == "5").Box;
        RectF two = renderer.DrawnLabels.First(l => l.Text == "2").Box;
        Assert.True(Middle(five).Y < Middle(two).Y);
        ChartBoxes boxes = renderer.Layout(360, 216);
        DrawnAxis axis = boxes.Axes.First(a => a.IsCategories);
        RectF plot = boxes.Plot;
        Assert.True(Middle(five).Y < axis.Category(0, plot));
        Assert.True(Middle(two).Y > axis.Category(0, plot));

        // The bars themselves: the first category's upper half is the first series's.
        Rgba8 first = new ChartPalette(new Document().Theme, 2).Series(0, 2);
        int x = (int)boxes.Axes.First(a => !a.IsCategories).Value(1, plot);
        (int top, int bottom) = RunOf(chart, first, x, (int)plot.Top, (int)axis.Category(0, plot));
        Assert.True(top >= 0 && bottom >= top);
    }

    [Fact]
    public void AReversedBarChartsLegendReadsAsItsBarsDo()
    {
        Chart chart = Labelled(ChartType.Bar, ChartGrouping.Clustered, [5, 4], [2, 3]);
        chart.Plots[0].Axes.First(a => a.Kind == ChartAxisKind.Category).Reversed = true;
        Assert.Equal(["S0", "S1"], new ChartRenderer(chart, null).LegendTexts());
    }

    [Fact]
    public void AStackedLabelSitsOnItsOwnPieceOfTheStack()
    {
        // 5, -3 and 4 stack positive and negative apart: the 4 spans 5 to 9, not 2 to 6.
        Chart chart = Labelled(ChartType.Column, ChartGrouping.Stacked, [5], [-3], [4]);
        ChartRenderer renderer = Drawn(chart);
        ChartBoxes boxes = renderer.Layout(360, 216);
        DrawnAxis up = boxes.Axes.First(a => !a.IsCategories);
        Assert.Equal(up.Value(7, boxes.Plot), Middle(renderer.DrawnLabels.First(l => l.Text == "4").Box).Y, 1f);
        Assert.Equal(up.Value(-1.5, boxes.Plot), Middle(renderer.DrawnLabels.First(l => l.Text == "-3").Box).Y, 1f);
    }

    [Fact]
    public void APercentStackedLabelSitsOnItsOwnPieceOfTheStack()
    {
        Chart chart = Labelled(ChartType.Column, ChartGrouping.PercentStacked, [6], [-3], [3]);
        ChartRenderer renderer = Drawn(chart);
        ChartBoxes boxes = renderer.Layout(360, 216);
        DrawnAxis up = boxes.Axes.First(a => !a.IsCategories);
        Assert.Equal(up.Value(0.625, boxes.Plot), Middle(renderer.DrawnLabels.First(l => l.Text == "3").Box).Y, 1f);
    }

    [Fact]
    public void APieLabelsOnlyTheSeriesItDraws()
    {
        Chart chart = Labelled(ChartType.Pie, null, [1, 2, 3], [4, 5, 6]);
        Assert.Equal(["1", "2", "3"], Drawn(chart).DrawnLabels.Select(l => l.Text));
    }

    [Fact]
    public void APulledOutSliceTakesItsLabelWithIt()
    {
        // Only the first of the two is pulled: the pie shrinks for it, so the other's label comes in with its smaller slice.
        Chart pulled = Labelled(ChartType.Pie, null, [1, 1]);
        ChartSeries series = pulled.Plots[0].Series[0];
        series.DataLabels.Position = ChartLabelPosition.OutsideEnd;
        series.Element.Add(new XElement(ChartNs.C + "dPt", new XElement(ChartNs.C + "idx", new XAttribute("val", "0")), new XElement(ChartNs.C + "explosion", new XAttribute("val", "100"))));
        List<(string Text, RectF Box)> labels = Drawn(pulled).DrawnLabels;
        ChartBoxes boxes = new ChartRenderer(pulled, null).Layout(360, 216);
        float center = boxes.Plot.X + (boxes.Plot.Width / 2);
        float reach = boxes.Plot.Width / 2;
        Assert.True(Middle(labels[1].Box).X > center - (reach * 0.8f));
        Assert.True(labels[0].Box.Left > center + (reach * 0.9f));
    }

    [Fact]
    public void ADoughnutLabelSitsInItsOwnRing()
    {
        Chart chart = Labelled(ChartType.Doughnut, null, [1, 1], [1, 1]);
        ChartBoxes boxes = new ChartRenderer(chart, null).Layout(360, 216);
        float center = boxes.Plot.X + (boxes.Plot.Width / 2);
        float outer = boxes.Plot.Width / 2;
        List<(string Text, RectF Box)> labels = Drawn(chart).DrawnLabels;
        Assert.Equal(4, labels.Count);
        float inner = Math.Abs(Middle(labels[0].Box).X - center);
        float rim = Math.Abs(Middle(labels[2].Box).X - center);
        Assert.True(inner < rim && rim < outer);
    }

    [Fact]
    public void ABubblesLabelGoesPastItsEdge()
    {
        Chart chart = Labelled(ChartType.Bubble, null, [2, 5, 3]);
        ChartSeries series = chart.Plots[0].Series[0];
        series.BubbleSizes.SetNumbers([9, 4, 16]);
        ChartRenderer renderer = Drawn(chart);
        ChartBoxes boxes = renderer.Layout(360, 216);
        DrawnAxis across = boxes.Axes.First(a => a.Horizontal);
        float reach = Math.Min(boxes.Plot.Width, boxes.Plot.Height) * 0.125f * 0.75f;
        Assert.True(renderer.DrawnLabels.First(l => l.Text == "3").Box.Left > across.Value(3, boxes.Plot) + reach);
    }

    [Fact]
    public void ARadarLabelsItsPointsOutward()
    {
        Chart chart = Labelled(ChartType.Radar, null, [2, 5, 3, 4]);
        ChartRenderer renderer = Drawn(chart);
        ChartBoxes boxes = renderer.Layout(360, 216);
        Assert.Equal(4, renderer.DrawnLabels.Count);
        float middle = boxes.Plot.Y + (boxes.Plot.Height / 2);
        DrawnAxis scale = boxes.Axes.First(a => !a.IsCategories);
        float radius = boxes.Plot.Width / 2;
        float point = middle - (float)(scale.Scale.Place(2) * radius);
        Assert.True(renderer.DrawnLabels.First(l => l.Text == "2").Box.Bottom <= point + 1);
    }

    [Fact]
    public void APointOffTheAxisHasNoLabel()
    {
        Chart chart = Labelled(ChartType.Scatter, null, [2, 7, 3]);
        chart.Plots[0].Axes.First(a => a.Position is ChartAxisPosition.Left).Maximum = 6;
        Assert.DoesNotContain(Drawn(chart).DrawnLabels, l => l.Text == "7");
    }

    [Fact]
    public void AnErrorBarHangsFromTheTopOfItsStack()
    {
        Chart chart = Labelled(ChartType.Column, ChartGrouping.Stacked, [5], [4]);
        WithErrorBars(chart.Plots[0].Series[1], "fixedVal", 1);
        ChartRenderer renderer = Drawn(chart);
        ChartBoxes boxes = renderer.Layout(360, 216);
        DrawnAxis up = boxes.Axes.First(a => !a.IsCategories);
        Assert.Equal(12, up.Scale.Maximum);
        (Vector2 a, Vector2 b) = Assert.Single(renderer.DrawnErrorBars);
        Assert.Equal(up.Value(8, boxes.Plot), a.Y, 1f);
        Assert.Equal(up.Value(10, boxes.Plot), b.Y, 1f);
    }

    [Fact]
    public void APercentStackedErrorBarIsAShareOfItsStack()
    {
        // 10 % of 6, in a stack of 8, is 7.5 % of the axis.
        Chart chart = Labelled(ChartType.Column, ChartGrouping.PercentStacked, [2], [6]);
        WithErrorBars(chart.Plots[0].Series[1], "percentage", 10);
        ChartRenderer renderer = Drawn(chart);
        ChartBoxes boxes = renderer.Layout(360, 216);
        DrawnAxis up = boxes.Axes.First(a => !a.IsCategories);
        (Vector2 a, Vector2 b) = Assert.Single(renderer.DrawnErrorBars);
        Assert.Equal(up.Value(0.925, boxes.Plot), a.Y, 1f);
        Assert.Equal(up.Value(1.075, boxes.Plot), b.Y, 1f);
    }

    [Fact]
    public void AStandardDeviationBarCentresOnTheSeriesMean()
    {
        // Excel's rule: every point's bar is the same, around the mean. 2, 5, 3, 7: mean 4.25, deviation 2.217.
        Chart chart = Labelled(ChartType.Column, ChartGrouping.Clustered, [2, 5, 3, 7]);
        WithErrorBars(chart.Plots[0].Series[0], "stdDev", 1);
        ChartRenderer renderer = Drawn(chart);
        ChartBoxes boxes = renderer.Layout(360, 216);
        DrawnAxis up = boxes.Axes.First(a => !a.IsCategories);
        Assert.Equal(4, renderer.DrawnErrorBars.Count);
        Assert.All(renderer.DrawnErrorBars, bar =>
        {
            Assert.Equal(up.Value(4.25 - 2.217, boxes.Plot), bar.A.Y, 1f);
            Assert.Equal(up.Value(4.25 + 2.217, boxes.Plot), bar.B.Y, 1f);
        });
    }

    [Theory]
    [InlineData(ChartType.Area)]
    [InlineData(ChartType.Bubble)]
    public void AreasAndBubblesDrawErrorBars(ChartType type)
    {
        Chart chart = Labelled(type, null, [2, 5, 3]);
        WithErrorBars(chart.Plots[0].Series[0], "fixedVal", 1);
        Assert.Equal(3, Drawn(chart).DrawnErrorBars.Count);
    }

    [Fact]
    public void AClusteredErrorBarIsOnItsOwnBarInAReversedBarChart()
    {
        Chart chart = Labelled(ChartType.Bar, ChartGrouping.Clustered, [5, 4], [2, 3]);
        chart.Plots[0].Axes.First(a => a.Kind == ChartAxisKind.Category).Reversed = true;
        WithErrorBars(chart.Plots[0].Series[0], "fixedVal", 1);
        ChartRenderer renderer = Drawn(chart);
        Vector2 label = Middle(renderer.DrawnLabels.First(l => l.Text == "5").Box);
        Assert.Equal(label.Y, renderer.DrawnErrorBars[0].A.Y, 1f);
    }

    [Fact]
    public void AnEmptyAxisTitleElementGivesTheAxisItsDefaultTitle()
    {
        Chart chart = FromXml("""
            <c:lineChart><c:grouping val="standard"/>
              <c:ser><c:idx val="0"/><c:order val="0"/><c:val><c:numLit><c:ptCount val="2"/><c:pt idx="0"><c:v>1</c:v></c:pt><c:pt idx="1"><c:v>2</c:v></c:pt></c:numLit></c:val></c:ser>
              <c:axId val="1"/><c:axId val="2"/></c:lineChart>
            <c:catAx><c:axId val="1"/><c:scaling/><c:axPos val="b"/><c:crossAx val="2"/></c:catAx>
            <c:valAx><c:axId val="2"/><c:scaling/><c:axPos val="l"/><c:majorGridlines/><c:title/><c:crossAx val="1"/></c:valAx>
            """);
        ChartBoxes boxes = new ChartRenderer(chart, null).Layout(360, 216);
        RectF title = boxes.Axes.First(a => !a.IsCategories).TitleBox!.Value;
        using Raster<Rgba8> raster = Raster.Create(360, 216, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        chart.Draw(canvas, new RectF(0, 0, 360, 216));
        int ink = 0;
        for (int y = (int)title.Top; y < (int)title.Bottom; y++)
        {
            foreach (Rgba8 pixel in raster.Row(y)[(int)title.Left..(int)title.Right])
            {
                ink += pixel.R < 128 ? 1 : 0;
            }
        }

        Assert.True(ink > 20, ink.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void APlotWhoseSecondAxesAreDeletedGoesByTheFirst()
    {
        // As Word draws it: the line lies on the bars' scale, and along their categories however many its own names.
        Chart chart = FromXml("""
            <c:barChart><c:barDir val="col"/><c:grouping val="clustered"/>
              <c:ser><c:idx val="0"/><c:order val="0"/><c:val><c:numLit><c:ptCount val="2"/><c:pt idx="0"><c:v>1000</c:v></c:pt><c:pt idx="1"><c:v>2000</c:v></c:pt></c:numLit></c:val></c:ser>
              <c:axId val="1"/><c:axId val="2"/></c:barChart>
            <c:lineChart><c:grouping val="standard"/>
              <c:ser><c:idx val="1"/><c:order val="1"/><c:cat><c:strLit><c:ptCount val="4"/><c:pt idx="0"><c:v>a</c:v></c:pt></c:strLit></c:cat><c:val><c:numLit><c:ptCount val="2"/><c:pt idx="0"><c:v>5</c:v></c:pt><c:pt idx="1"><c:v>6</c:v></c:pt></c:numLit></c:val></c:ser>
              <c:axId val="3"/><c:axId val="4"/></c:lineChart>
            <c:catAx><c:axId val="1"/><c:scaling/><c:delete val="0"/><c:axPos val="b"/><c:crossAx val="2"/></c:catAx>
            <c:valAx><c:axId val="2"/><c:scaling/><c:delete val="1"/><c:axPos val="l"/><c:crossAx val="1"/></c:valAx>
            <c:valAx><c:axId val="4"/><c:scaling/><c:delete val="1"/><c:axPos val="r"/><c:crossAx val="3"/><c:crosses val="max"/></c:valAx>
            <c:catAx><c:axId val="3"/><c:scaling/><c:delete val="1"/><c:axPos val="b"/><c:crossAx val="4"/></c:catAx>
            """);
        ChartBoxes boxes = new ChartRenderer(chart, null).Layout(360, 216);
        DrawnAxis up = Assert.Single(boxes.Axes, a => !a.IsCategories);
        Assert.Equal(2, up.Plots.Count);
        Assert.Equal(2, Assert.Single(boxes.Axes, a => a.IsCategories).Count);
    }

    [Fact]
    public void ALabelsOwnTextShowsWithItsFieldsFilledIn()
    {
        Chart chart = FromXml("""
            <c:pieChart><c:ser><c:idx val="0"/><c:order val="0"/><c:dLbls>
              <c:dLbl><c:idx val="0"/><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>Mine</a:t></a:r></a:p><a:p><a:fld id="{00000000-0000-0000-0000-000000000000}" type="PERCENTAGE"><a:t>[x]</a:t></a:fld></a:p></c:rich></c:tx><c:showPercent val="1"/></c:dLbl>
              <c:showPercent val="1"/></c:dLbls>
              <c:val><c:numLit><c:ptCount val="2"/><c:pt idx="0"><c:v>1</c:v></c:pt><c:pt idx="1"><c:v>3</c:v></c:pt></c:numLit></c:val></c:ser></c:pieChart>
            """);
        Assert.Contains(Drawn(chart).DrawnLabels, l => l.Text == "Mine\n25%");
    }

    [Fact]
    public void ACultureWritesItsOwnDatesPercentsAndListSeparator()
    {
        System.Globalization.CultureInfo norwegian = System.Globalization.CultureInfo.GetCultureInfo("nb-NO");
        Chart chart = Labelled(ChartType.Pie, null, [1, 3]);
        ChartDataLabels labels = chart.Plots[0].Series[0].DataLabels;
        labels.ShowPercent = true;
        labels.ShowValue = true;
        using Raster<Rgba8> raster = Raster.Create(360, 216, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        ChartRenderer renderer = new(chart, new ChartDrawOptions { Culture = norwegian });
        renderer.Draw(canvas, new RectF(0, 0, 360, 216));
        Assert.Contains(renderer.DrawnLabels, l => l.Text == "1; 25\u00a0%" || l.Text == "1; 25 %");
    }

    [Fact]
    public void AWordChartHasSquareCornersUnlessItSaysOtherwise()
    {
        Chart chart = Made(ChartType.Column);
        chart.Format.LineColor = ChartColor.FromRgb(new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> raster = Raster.Create(100, 100, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        chart.Draw(canvas, new RectF(0, 0, 100, 100), new ChartDrawOptions { Host = ChartHost.Word });
        Assert.True(raster.Row(0)[0].R < 128);
    }

    [Fact]
    public void ANarrowLegendWrapsAtAHyphenThenInsideAWord()
    {
        Chart chart = Made(ChartType.Column);
        chart.Plots[0].Series[0].Name = "Y-Values and more";
        ChartBoxes boxes = new ChartRenderer(chart, null).Layout(120, 150);
        Assert.True(boxes.Legend!.Value.Width < 120 * 0.42f);
    }

    [Fact]
    public void ARadarSettlesItsStepByTheWholePlot()
    {
        // From Excel: 290 by 288 points takes steps of 1 for data up to 7, 290 by 220 steps of 2.
        Chart tall = Labelled(ChartType.Radar, null, [2, 5, 3, 7, 4]);
        tall.HasLegend = false;
        Assert.Equal(1, new ChartRenderer(tall, null).Layout(290, 288).Axes.First(a => !a.IsCategories).Scale.Major);
        Assert.Equal(2, new ChartRenderer(tall, null).Layout(290, 176).Axes.First(a => !a.IsCategories).Scale.Major);
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
