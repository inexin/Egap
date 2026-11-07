using System.Numerics;
using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Charts;

namespace Egap.Documents.Tests;

/// <summary>Drawing the newer chart types and 3D charts, and Word's newer charts coming in as charts.</summary>
public sealed class ChartThreeDTests
{
    private const string Xmlns = "xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    private static Chart Filled(Chart chart)
    {
        chart.Plots[0].Series.Add("North", ["Jan", "Feb", "Mar", "Apr"], [10, 20, 5, 30]);
        if (!chart.IsExtended)
        {
            chart.Plots[0].Series.Add("South", ["Jan", "Feb", "Mar", "Apr"], [3, 1, 2, 4]);
        }

        return chart;
    }

    private static Raster<Rgba8> Drawn(Chart chart, int width = 360, int height = 216)
    {
        Raster<Rgba8> raster = Raster.Create(width, height, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        chart.Draw(canvas, new RectF(0, 0, width, height));
        return raster;
    }

    private static int Ink(Raster<Rgba8> raster, int fromY = 0)
    {
        int ink = 0;
        for (int y = fromY; y < raster.Height; y++)
        {
            foreach (Rgba8 pixel in raster.Row(y))
            {
                ink += pixel.R < 250 || pixel.G < 250 || pixel.B < 250 ? 1 : 0;
            }
        }

        return ink;
    }

    [Theory]
    [InlineData(ChartType.Waterfall)]
    [InlineData(ChartType.Histogram)]
    [InlineData(ChartType.Pareto)]
    [InlineData(ChartType.BoxWhisker)]
    [InlineData(ChartType.Treemap)]
    [InlineData(ChartType.Sunburst)]
    [InlineData(ChartType.Funnel)]
    public void EveryNewerTypeDraws(ChartType type)
    {
        Chart chart = Filled(Chart.Create(type));
        Assert.True(ChartRenderer.Draws(chart));
        using Raster<Rgba8> raster = Drawn(chart);
        Assert.True(Ink(raster) > 500, type.ToString());
    }

    [Fact]
    public void ARegionMapIsLeftToItsPicture()
    {
        Assert.False(ChartRenderer.Draws(Chart.Create(ChartType.RegionMap)));
    }

    [Theory]
    [InlineData("sunburst")]
    [InlineData("treemap")]
    public void ATreeOfTensOfThousandsOfLevelsDraws(string layout)
    {
        // Each level is one more call deep in the walks over the tree; this many would overflow the stack.
        StringBuilder xml = new("<cx:chartSpace xmlns:cx=\"http://schemas.microsoft.com/office/drawing/2014/chartex\"><cx:chartData><cx:data id=\"0\"><cx:strDim type=\"cat\">");
        for (int i = 0; i < 50_000; i++)
        {
            xml.Append("<cx:lvl ptCount=\"1\"><cx:pt idx=\"0\">a</cx:pt></cx:lvl>");
        }

        xml.Append("</cx:strDim><cx:numDim type=\"size\"><cx:lvl ptCount=\"1\"><cx:pt idx=\"0\">5</cx:pt></cx:lvl></cx:numDim></cx:data></cx:chartData>")
            .Append("<cx:chart><cx:plotArea><cx:plotAreaRegion><cx:series layoutId=\"").Append(layout).Append("\"><cx:dataId val=\"0\"/></cx:series></cx:plotAreaRegion></cx:plotArea></cx:chart></cx:chartSpace>");
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml.ToString()));
        Chart chart = Chart.Load(stream, new DocumentLimits { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 16 * 1024 * 1024 });
        using Raster<Rgba8> raster = Drawn(chart);
        Assert.True(Ink(raster) > 500, layout);
    }

    [Theory]
    [InlineData(ChartType.Column)]
    [InlineData(ChartType.Bar)]
    [InlineData(ChartType.Line)]
    [InlineData(ChartType.Area)]
    [InlineData(ChartType.Pie)]
    public void EveryThreeDFormDraws(ChartType type)
    {
        Chart chart = Filled(Chart.Create(type, threeD: true));
        Assert.True(ChartRenderer.Draws(chart));
        using Raster<Rgba8> raster = Drawn(chart);
        Assert.True(Ink(raster) > 500, type.ToString());
    }

    [Theory]
    [InlineData(ChartType.Surface)]
    [InlineData(ChartType.PieOfPie)]
    [InlineData(ChartType.BarOfPie)]
    public void SurfacesAndPiesWithASecondPlotDraw(ChartType type)
    {
        Chart chart = Filled(Chart.Create(type));
        Assert.True(ChartRenderer.Draws(chart));
        using Raster<Rgba8> raster = Drawn(chart);
        Assert.True(Ink(raster) > 500, type.ToString());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 16)]
    [InlineData(120, 60)]
    public void ATinyThreeDChartStillDraws(int width, int height)
    {
        using Raster<Rgba8> raster = Drawn(Filled(Chart.Create(ChartType.Column, threeD: true)), width, height);
        Assert.Equal(width, raster.Width);
    }

    [Fact]
    public void RightAngleAxesKeepTheFrontARectangle()
    {
        ChartProjection view = new(true, 15, 20, 0, 100, 50, 30, new RectF(0, 0, 200, 100));
        Vector2 corner = view.At(0, 0, 0);
        Assert.Equal(corner.Y, view.At(100, 0, 0).Y, 3);
        Assert.Equal(corner.X, view.At(0, 50, 0).X, 3);
        Assert.True(view.At(0, 0, 30).X > corner.X);
        Assert.True(view.At(0, 0, 30).Y < corner.Y);
    }

    [Theory]
    [InlineData(true, 0.0)]
    [InlineData(false, 30.0)]
    public void TheBoxFitsItsRoom(bool rightAngle, double perspective)
    {
        RectF room = new(10, 20, 200, 100);
        ChartProjection view = new(rightAngle, 15, 20, perspective, 100, 50, 30, room);
        foreach (float x in new[] { 0f, 100f })
        {
            foreach (float y in new[] { 0f, 50f })
            {
                foreach (float z in new[] { 0f, 30f })
                {
                    Vector2 at = view.At(x, y, z);
                    Assert.InRange(at.X, room.Left - 0.01f, room.Right + 0.01f);
                    Assert.InRange(at.Y, room.Top - 0.01f, room.Bottom + 0.01f);
                }
            }
        }
    }

    [Fact]
    public void NearerPointsAreNearer()
    {
        ChartProjection view = new(false, 15, 20, 30, 100, 50, 30, new RectF(0, 0, 200, 100));
        Assert.True(view.Far(50, 25, 0) < view.Far(50, 25, 30));
        Assert.True(view.FromRight);
        Assert.True(view.FromFront);
        Assert.True(view.FromAbove);
    }

    private static Chart ColumnsWith(string extra)
    {
        string part = $"""
            <c:chartSpace {Xmlns}><c:chart><c:view3D><c:rotX val="15"/><c:rotY val="20"/><c:rAngAx val="1"/></c:view3D><c:plotArea><c:layout/>
            <c:bar3DChart><c:barDir val="col"/><c:grouping val="clustered"/>
            <c:ser><c:idx val="0"/><c:order val="0"/><c:tx><c:v>North</c:v></c:tx>
            <c:cat><c:strLit><c:ptCount val="2"/><c:pt idx="0"><c:v>Jan</c:v></c:pt><c:pt idx="1"><c:v>Feb</c:v></c:pt></c:strLit></c:cat>
            <c:val><c:numLit><c:ptCount val="2"/><c:pt idx="0"><c:v>1</c:v></c:pt><c:pt idx="1"><c:v>2</c:v></c:pt></c:numLit></c:val></c:ser>
            <c:axId val="1"/><c:axId val="2"/></c:bar3DChart>
            <c:catAx><c:axId val="1"/><c:scaling/><c:delete val="0"/><c:axPos val="b"/><c:crossAx val="2"/></c:catAx>
            <c:valAx><c:axId val="2"/><c:scaling/><c:delete val="0"/><c:axPos val="l"/><c:crossAx val="1"/></c:valAx>
            {extra}</c:plotArea></c:chart></c:chartSpace>
            """;
        return Chart.Load(new MemoryStream(Encoding.UTF8.GetBytes(part)));
    }

    [Fact]
    public void ADataTableTakesRoomUnderThePlot()
    {
        const string Table = "<c:dTable><c:showHorzBorder val=\"1\"/><c:showVertBorder val=\"1\"/><c:showOutline val=\"1\"/><c:showKeys val=\"1\"/></c:dTable>";
        using Raster<Rgba8> plain = Drawn(ColumnsWith(string.Empty));
        using Raster<Rgba8> withTable = Drawn(ColumnsWith(Table));
        Assert.True(Ink(withTable, plain.Height - 40) > Ink(plain, plain.Height - 40));
    }

    [Fact]
    public void ATitlesColourMayComeFromItsRun()
    {
        const string Part = $"<c:chartSpace {Xmlns}><c:chart><c:title><c:tx><c:rich><a:bodyPr/><a:p><a:pPr><a:defRPr/></a:pPr><a:r><a:rPr><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:rPr><a:t>Red</a:t></a:r></a:p></c:rich></c:tx></c:title><c:plotArea><c:layout/></c:plotArea></c:chart></c:chartSpace>";
        Chart chart = Chart.Load(new MemoryStream(Encoding.UTF8.GetBytes(Part)));
        Assert.NotNull(chart.Title.TextFormat.Color);
    }

    private const string DarkStyle = "<cs:chartStyle xmlns:cs=\"http://schemas.microsoft.com/office/drawing/2012/chartStyle\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><cs:chartArea><cs:spPr><a:solidFill><a:srgbClr val=\"202020\"/></a:solidFill></cs:spPr></cs:chartArea><cs:title><cs:fontRef idx=\"minor\"><a:srgbClr val=\"FF0000\"/></cs:fontRef></cs:title></cs:chartStyle>";

    [Fact]
    public void ANewerChartDrawsWithItsStylePartsLook()
    {
        using Raster<Rgba8> raster = Drawn(Assert.Single(Document.Load(WordFileWith(Filled(Chart.Create(ChartType.Waterfall)), true, DarkStyle)).Descendants<Island>()).Chart!);
        Assert.Equal(0x20, raster.Row(raster.Height / 2)[1].R);
    }

    [Fact]
    public void AMalformedStylePartLeavesTheChart()
    {
        Island island = Assert.Single(Document.Load(WordFileWith(Filled(Chart.Create(ChartType.Waterfall)), true, "<broken")).Descendants<Island>());
        Assert.Equal(ChartType.Waterfall, island.Chart!.Type);
        using Raster<Rgba8> raster = Drawn(island.Chart);
        Assert.Equal(255, raster.Row(raster.Height / 2)[1].R);
    }

    [Fact]
    public void ATurnedBoxAndItsTableGoWhereTheyArePlaced()
    {
        const string Table = "<c:dTable><c:showHorzBorder val=\"1\"/><c:showVertBorder val=\"1\"/><c:showOutline val=\"1\"/></c:dTable>";
        const string Placed = "<c:layout><c:manualLayout><c:layoutTarget val=\"inner\"/><c:xMode val=\"edge\"/><c:yMode val=\"edge\"/><c:x val=\"0.2\"/><c:y val=\"0.1\"/><c:w val=\"0.6\"/><c:h val=\"0.25\"/></c:manualLayout></c:layout>";
        string part = $"""
            <c:chartSpace {Xmlns}><c:chart><c:view3D><c:perspective val="30"/></c:view3D><c:plotArea>{Placed}
            <c:bar3DChart><c:barDir val="col"/><c:grouping val="clustered"/>
            <c:ser><c:idx val="0"/><c:order val="0"/><c:tx><c:v>North</c:v></c:tx>
            <c:cat><c:strLit><c:ptCount val="2"/><c:pt idx="0"><c:v>Jan</c:v></c:pt><c:pt idx="1"><c:v>Feb</c:v></c:pt></c:strLit></c:cat>
            <c:val><c:numLit><c:ptCount val="2"/><c:pt idx="0"><c:v>1</c:v></c:pt><c:pt idx="1"><c:v>2</c:v></c:pt></c:numLit></c:val></c:ser>
            <c:axId val="1"/><c:axId val="2"/></c:bar3DChart>
            <c:catAx><c:axId val="1"/><c:scaling/><c:delete val="1"/><c:axPos val="b"/><c:crossAx val="2"/></c:catAx>
            <c:valAx><c:axId val="2"/><c:scaling/><c:delete val="1"/><c:axPos val="l"/><c:crossAx val="1"/></c:valAx>
            {Table}</c:plotArea></c:chart></c:chartSpace>
            """;
        using Raster<Rgba8> raster = Drawn(Chart.Load(new MemoryStream(Encoding.UTF8.GetBytes(part))), 400, 300);

        // The table follows the plot, so the chart's bottom stays empty inside its frame.
        Assert.True(Ink(raster, 110) > 0);
        for (int y = 240; y < 290; y++)
        {
            foreach (Rgba8 pixel in raster.Row(y)[20..380])
            {
                Assert.Equal(255, pixel.R);
            }
        }
    }

    private static MemoryStream WordFileWith(Chart chart, bool wrapped, string? stylePart = null)
    {
        const string Graphic = "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.microsoft.com/office/drawing/2014/chartex\"><cx:chart xmlns:cx=\"http://schemas.microsoft.com/office/drawing/2014/chartex\" r:id=\"rId9\"/></a:graphicData></a:graphic>";
        string drawing = $"<w:drawing><wp:inline xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"><wp:extent cx=\"3657600\" cy=\"2743200\"/><wp:docPr id=\"1\" name=\"Chart\"/>{Graphic}</wp:inline></w:drawing>";
        string run = wrapped
            ? $"<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice xmlns:cx1=\"http://schemas.microsoft.com/office/drawing/2015/9/8/chartex\" Requires=\"cx1\">{drawing}</mc:Choice><mc:Fallback><w:t>No chart</w:t></mc:Fallback></mc:AlternateContent>"
            : drawing;
        string body = $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:r>{run}</w:r></w:p></w:body></w:document>";
        using MemoryStream part = new();
        chart.Save(part);
        Docx.OpcPackageWriter writer = new();
        writer.AddPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", Encoding.UTF8.GetBytes(body));
        writer.AddPart("/word/charts/chartEx1.xml", Chart.ExtendedContentType, part.ToArray());
        writer.AddRelationship("/", "rId1", Docx.OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rId9", Chart.ExtendedRelationship, "charts/chartEx1.xml");
        if (stylePart is not null)
        {
            writer.AddPart("/word/charts/style1.xml", Chart.StyleContentType, Encoding.UTF8.GetBytes(stylePart));
            writer.AddRelationship("/word/charts/chartEx1.xml", "rId1", Chart.StyleRelationship, "style1.xml");
        }

        MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        return stream;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WordsNewerChartsComeInAsCharts(bool wrapped)
    {
        Document document = Document.Load(WordFileWith(Filled(Chart.Create(ChartType.Waterfall)), wrapped));
        Island island = Assert.Single(document.Descendants<Island>());
        Assert.Equal(IslandKind.Chart, island.Kind);
        Assert.NotNull(island.Chart);
        Assert.True(island.Chart.IsExtended);
        Assert.Equal(ChartType.Waterfall, island.Chart.Type);
    }

    [Fact]
    public void ARegionMapComesBackFromAWordFile()
    {
        Document document = Document.Load(WordFileWith(Chart.Create(ChartType.RegionMap), true));
        using MemoryStream saved = new();
        document.SaveAsDocx(saved);
        saved.Position = 0;
        Island back = Assert.Single(Document.Load(saved).Descendants<Island>());
        Assert.Equal(ChartType.RegionMap, back.Chart!.Type);
    }

    [Fact]
    public void AnEditedNewerChartComesBackFromAWordFile()
    {
        Document document = Document.Load(WordFileWith(Filled(Chart.Create(ChartType.Funnel)), true));
        Assert.Single(document.Descendants<Island>()).Chart!.Title.Text = "Edited";
        using MemoryStream saved = new();
        document.SaveAsDocx(saved);
        saved.Position = 0;
        Island back = Assert.Single(Document.Load(saved).Descendants<Island>());
        Assert.Equal(ChartType.Funnel, back.Chart!.Type);
        Assert.Equal("Edited", back.Chart.Title.Text);
    }
}
