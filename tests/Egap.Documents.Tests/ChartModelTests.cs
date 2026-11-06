using Egap.Documents.Charts;
using Egap.Documents.Docx;

namespace Egap.Documents.Tests;

/// <summary>The chart model on its own and as a chart in a Word document.</summary>
public sealed class ChartModelTests
{
    private static Document Reloaded(Document document)
    {
        MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        stream.Position = 0;
        return DocxFormat.Load(stream);
    }

    private static Chart RoundTrip(Chart chart)
    {
        using MemoryStream stream = new();
        chart.Save(stream);
        stream.Position = 0;
        return Chart.Load(stream);
    }

    private static Chart Made()
    {
        Chart chart = Chart.Create(ChartType.Column);
        chart.Plots[0].Series.Add("North", ["Jan", "Feb"], [1, 2]);
        chart.Plots[0].Series.Add("South", ["Jan", "Feb"], [3, null]);
        return chart;
    }

    [Theory]
    [InlineData(ChartType.Column, false)]
    [InlineData(ChartType.Column, true)]
    [InlineData(ChartType.Bar, false)]
    [InlineData(ChartType.Bar, true)]
    [InlineData(ChartType.Line, false)]
    [InlineData(ChartType.Line, true)]
    [InlineData(ChartType.Area, false)]
    [InlineData(ChartType.Area, true)]
    [InlineData(ChartType.Pie, false)]
    [InlineData(ChartType.Pie, true)]
    [InlineData(ChartType.PieOfPie, false)]
    [InlineData(ChartType.BarOfPie, false)]
    [InlineData(ChartType.Doughnut, false)]
    [InlineData(ChartType.Scatter, false)]
    [InlineData(ChartType.Bubble, false)]
    [InlineData(ChartType.Radar, false)]
    [InlineData(ChartType.Stock, false)]
    [InlineData(ChartType.Surface, false)]
    [InlineData(ChartType.Surface, true)]
    [InlineData(ChartType.Waterfall, false)]
    [InlineData(ChartType.Histogram, false)]
    [InlineData(ChartType.Pareto, false)]
    [InlineData(ChartType.BoxWhisker, false)]
    [InlineData(ChartType.Treemap, false)]
    [InlineData(ChartType.Sunburst, false)]
    [InlineData(ChartType.Funnel, false)]
    public void EveryTypeIsMadeAndComesBack(ChartType type, bool threeD)
    {
        Chart back = RoundTrip(Chart.Create(type, threeD));
        Assert.Equal(type, back.Type);
        Assert.Equal(threeD, back.Plots[0].Is3D);
        Assert.Equal(type >= ChartType.Waterfall, back.IsExtended);
    }

    [Theory]
    [InlineData(ChartType.Radar)]
    [InlineData(ChartType.Stock)]
    [InlineData(ChartType.Waterfall)]
    public void ATypeWithNo3DFormIsRefused(ChartType type) => Assert.Throws<ArgumentException>(() => Chart.Create(type, true));

    [Fact]
    public void ValuesWithGapsComeBack()
    {
        Chart back = RoundTrip(Made());
        Assert.Equal(2, back.Series.Count);
        Assert.Equal("North", back.Series[0].Name);
        Assert.Equal([3d, null], back.Series[1].Values.Numbers);
        Assert.Equal(["Jan", "Feb"], back.Series[1].Categories.Text);
    }

    [Fact]
    public void PropertiesComeBack()
    {
        Chart chart = Made();
        chart.Title.Text = "Sales";
        chart.Legend.Position = ChartLegendPosition.Bottom;
        chart.DisplayBlanksAs = ChartBlanks.Span;
        chart.Format.FillColor = ChartColor.FromHex("102030");
        ChartAxis value = chart.Axes.First(a => a.Kind == ChartAxisKind.Value);
        value.Minimum = -5;
        value.Maximum = 5;
        value.MajorUnit = 1;
        value.Title.Text = "Units";
        value.NumberFormat = "0.00";
        value.MajorTickMark = ChartTickMark.Cross;
        ChartSeries series = chart.Series[0];
        series.Format.LineWidth = 2.5;
        series.DataLabels.ShowValue = true;
        series.Marker.Symbol = ChartMarkerSymbol.Diamond;

        Chart back = RoundTrip(chart);
        Assert.Equal("Sales", back.Title.Text);
        Assert.Equal(ChartLegendPosition.Bottom, back.Legend.Position);
        Assert.Equal(ChartBlanks.Span, back.DisplayBlanksAs);
        ChartAxis axis = back.Axes.First(a => a.Kind == ChartAxisKind.Value);
        Assert.Equal(-5, axis.Minimum);
        Assert.Equal(5, axis.Maximum);
        Assert.Equal(1, axis.MajorUnit);
        Assert.Equal("Units", axis.Title.Text);
        Assert.Equal("0.00", axis.NumberFormat);
        Assert.Equal(ChartTickMark.Cross, axis.MajorTickMark);
        Assert.Equal(2.5, back.Series[0].Format.LineWidth);
        Assert.True(back.Series[0].DataLabels.ShowValue);
        Assert.Equal(ChartMarkerSymbol.Diamond, back.Series[0].Marker.Symbol);
    }

    [Fact]
    public void WhatTheModelDoesNotHoldStays()
    {
        const string Part = """
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:x="urn:x">
              <c:chart><c:plotArea><c:barChart><c:barDir val="col"/><x:mine a="1"/>
                <c:ser><c:idx val="0"/><c:order val="0"/><x:other/></c:ser></c:barChart></c:plotArea></c:chart>
              <x:outer/>
            </c:chartSpace>
            """;
        Chart chart = Chart.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Part)));
        chart.Title.Text = "Added";
        chart.Series[0].Name = "One";
        using MemoryStream saved = new();
        chart.Save(saved);
        string xml = System.Text.Encoding.UTF8.GetString(saved.ToArray());
        Assert.Contains("<x:mine a=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("<x:other", xml, StringComparison.Ordinal);
        Assert.Contains("<x:outer", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<a/>")]
    [InlineData("<!DOCTYPE c [<!ENTITY x 'y'>]><c/>")]
    public void WhatIsNoChartIsRefused(string text) =>
        Assert.Throws<DocumentException>(() => Chart.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))));

    [Fact]
    public void ADeclarationThatBreaksTheXmlReaderIsRefused()
    {
        byte[] part = [.. "<?xml version=\"1.0U"u8, 0xEC, .. "F-8\" standalone=\"yes\"?>"u8];
        Assert.Throws<DocumentException>(() => Chart.Load(new MemoryStream(part)));
    }

    [Fact]
    public void AnOddlyDeclaredPrefixStillSaves()
    {
        const string Part = """
            <cx:chartSpace xmlns:cx="http://schemas.microsoft.com/office/drawing/2014/chartex"><cx:chartData><cx:data id="0">
            <cx:numDim type="size" xmlns:cx="urn:other"><cx:f>A1</cx:f></cx:numDim></cx:data></cx:chartData>
            <cx:chart><cx:plotArea><cx:plotAreaRegion><cx:series layoutId="treemap"><cx:dataId val="0"/></cx:series></cx:plotAreaRegion></cx:plotArea></cx:chart></cx:chartSpace>
            """;
        Chart chart = Chart.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Part)));
        chart.Series[0].Values.SetNumbers([1, 2]);
        chart.Series[0].Categories.SetText(["a", "b"]);
        using MemoryStream saved = new();
        chart.Save(saved);
        saved.Position = 0;
        Assert.NotNull(Chart.Load(saved));
    }

    [Fact]
    public void ACountBeyondTheLimitStaysCheap()
    {
        const string Part = """
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart><c:plotArea><c:barChart><c:barDir val="col"/>
            <c:ser><c:idx val="0"/><c:order val="0"/><c:val><c:numLit><c:ptCount val="2147483647"/><c:pt idx="2147483646"><c:v>1</c:v></c:pt></c:numLit></c:val></c:ser>
            </c:barChart></c:plotArea></c:chart></c:chartSpace>
            """;
        Chart chart = Chart.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Part)));
        Assert.True(chart.Series[0].Values.Count <= 1 << 20);
        Assert.All(chart.Series[0].Values.Numbers.Take(4), n => Assert.Null(n));
    }

    [Fact]
    public void ACloneIsItsOwn()
    {
        Chart chart = Made();
        Chart copy = chart.Clone();
        copy.Series[0].Name = "Other";
        Assert.Equal("North", chart.Series[0].Name);
    }

    [Fact]
    public void AChartMadeForAWordDocumentComesBack()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("See: ");
        paragraph.Inlines.Add(new Island(document, Made(), 360, 216));

        Document back = Reloaded(document);
        Island island = Assert.Single(back.Descendants<Island>());
        Assert.Equal(IslandKind.Chart, island.Kind);
        Assert.Equal(360, island.Width, 1);
        Assert.Equal(216, island.Height, 1);
        Assert.NotNull(island.Chart);
        Assert.Equal(ChartType.Column, island.Chart.Type);
        Assert.Equal("South", island.Chart.Series[1].Name);
    }

    [Fact]
    public void AChartEditedInAWordDocumentKeepsItsEdit()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("").Inlines.Add(new Island(document, Made(), 360, 216));
        Document once = Reloaded(document);
        Assert.Single(once.Descendants<Island>()).Chart!.Title.Text = "Edited";

        Document twice = Reloaded(once);
        Assert.Equal("Edited", Assert.Single(twice.Descendants<Island>()).Chart!.Title.Text);
    }

    [Fact]
    public void ACopiedChartSavesAsTwo()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("");
        Island island = new(document, Made(), 360, 216);
        paragraph.Inlines.Add(island);
        Island copy = (Island)island.Clone();
        copy.Chart!.Title.Text = "Copy";
        paragraph.Inlines.Add(copy);

        List<Island> back = [.. Reloaded(document).Descendants<Island>()];
        Assert.Equal(2, back.Count);
        Assert.False(back[0].Chart!.HasTitle);
        Assert.Equal("Copy", back[1].Chart!.Title.Text);
    }

    [Fact]
    public void ACopyOfAnUntouchedChartReadsTheSame()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("");
        paragraph.Inlines.Add(new Island(document, Made(), 360, 216));
        Document back = Reloaded(document);
        Island copy = (Island)back.Descendants<Island>().First().Clone();
        Assert.Equal("North", copy.Chart!.Series[0].Name);
    }

    [Fact]
    public void EditingACopyOfAReadChartLeavesTheFirst()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("");
        paragraph.Inlines.Add(new Island(document, Made(), 360, 216));
        Document back = Reloaded(document);
        Island first = back.Descendants<Island>().First();
        Island copy = (Island)first.Clone();
        back.Descendants<Paragraph>().First().Inlines.Add(copy);
        copy.Chart!.Title.Text = "Copy";

        List<Island> saved = [.. Reloaded(back).Descendants<Island>()];
        Assert.Equal(2, saved.Count);
        Assert.False(saved[0].Chart!.HasTitle);
        Assert.Equal("Copy", saved[1].Chart!.Title.Text);
    }

    [Fact]
    public void EditingAReadChartAfterCopyingItLeavesTheCopy()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("").Inlines.Add(new Island(document, Made(), 360, 216));
        Document back = Reloaded(document);
        Island first = back.Descendants<Island>().First();
        Island copy = (Island)first.Clone();
        back.Sections[0].Body.AddParagraph("").Inlines.Add(copy);
        first.Chart!.Title.Text = "New";

        List<Island> saved = [.. Reloaded(back).Descendants<Island>()];
        Assert.Equal(2, saved.Count);
        Assert.Equal("New", saved[0].Chart!.Title.Text);
        Assert.False(saved[1].Chart!.HasTitle);
    }

    [Fact]
    public void ACopyNobodyEditedStillSharesThePart()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("").Inlines.Add(new Island(document, Made(), 360, 216));
        Document back = Reloaded(document);
        back.Sections[0].Body.AddParagraph("").Inlines.Add((Island)back.Descendants<Island>().First().Clone());

        using MemoryStream saved = new();
        DocxFormat.Save(back, saved);
        saved.Position = 0;
        using System.IO.Compression.ZipArchive zip = new(saved);
        Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/charts/chart", StringComparison.Ordinal));
    }

    [Fact]
    public void AReadChartImportedIntoAnotherDocumentComesAlong()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("").Inlines.Add(new Island(document, Made(), 360, 216));
        Document back = Reloaded(document);

        Document other = new();
        other.AddSection().Body.AddParagraph("").Inlines.Add(other.Import(back.Descendants<Island>().First()));
        Assert.Equal("North", Assert.Single(Reloaded(other).Descendants<Island>()).Chart!.Series[0].Name);
    }

    [Fact]
    public void ACellLinkedTitleKeepsItsText()
    {
        Chart chart = Made();
        chart.Title.Text = "From the cell";
        chart.Title.Reference = "Sheet1!$A$1";
        ChartAxis axis = chart.Axes[0];
        axis.Title.Text = "Axis cell";
        axis.Title.Reference = "Sheet1!$B$1";

        Chart back = RoundTrip(chart);
        Assert.Equal("From the cell", back.Title.Text);
        Assert.Equal("Sheet1!$A$1", back.Title.Reference);
        Assert.Equal("Axis cell", back.Axes[0].Title.Text);
        Assert.Equal("Sheet1!$B$1", back.Axes[0].Title.Reference);
    }

    [Fact]
    public void ACopyGoingToAnotherDocumentDropsWhatItCannotTake()
    {
        Chart chart = Made();
        chart.Root.Add(new System.Xml.Linq.XElement(ChartNs.C + "externalData", new System.Xml.Linq.XAttribute(ChartNs.R + "id", "rId1")));
        Assert.Empty(chart.WithoutRelatedParts().Root.Elements(ChartNs.C + "externalData"));
        Assert.Single(chart.Root.Elements(ChartNs.C + "externalData"));
    }

    [Fact]
    public void AChartOfTheNewerTypesIsRefusedInAWordDocument()
    {
        Document document = new();
        Assert.Throws<ArgumentException>(() => new Island(document, Chart.Create(ChartType.Waterfall), 100, 100));
    }

    [Fact]
    public void OtherIslandsHaveNoChart()
    {
        Document document = new();
        Assert.Null(new Island(document, IslandKind.Math, "docx", "<m:oMath/>").Chart);
    }
}
