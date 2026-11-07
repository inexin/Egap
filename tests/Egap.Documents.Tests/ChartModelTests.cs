using System.IO.Compression;
using System.Xml.Linq;

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
    public void ANewTitleSitsBesideThePlot()
    {
        Chart chart = Chart.Create(ChartType.Column);
        chart.HasTitle = false;
        chart.Title.Text = "New";
        Assert.False(chart.Title.Overlay);
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

    /// <summary>A document whose one chart has an embedded workbook and a style part, as Word saves them.</summary>
    private static byte[] WithWorkbook()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("").Inlines.Add(new Island(document, Made(), 360, 216));
        MemoryStream made = new();
        DocxFormat.Save(document, made);
        using MemoryStream stream = new();
        stream.Write(made.ToArray());
        using (ZipArchive zip = new(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            string chart = zip.Entries.First(e => e.FullName.StartsWith("word/charts/chart", StringComparison.Ordinal)).FullName;
            string name = chart["word/charts/".Length..];
            Add(zip, "word/embeddings/book1.xlsx", [1, 2, 3]);
            Add(zip, "word/charts/style1.xml", "<cs:chartStyle xmlns:cs=\"http://schemas.microsoft.com/office/drawing/2012/chartStyle\"/>"u8.ToArray());
            Add(zip, $"word/charts/_rels/{name}.rels", System.Text.Encoding.UTF8.GetBytes(
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/package\" Target=\"../embeddings/book1.xlsx\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.microsoft.com/office/2011/relationships/chartStyle\" Target=\"style1.xml\"/>"
                + "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com/\" TargetMode=\"External\"/>"
                + "</Relationships>"));
            ZipArchiveEntry types = zip.GetEntry("[Content_Types].xml")!;
            string text;
            using (StreamReader reader = new(types.Open()))
            {
                text = reader.ReadToEnd();
            }

            types.Delete();
            Add(zip, "[Content_Types].xml", System.Text.Encoding.UTF8.GetBytes(text.Replace(
                "</Types>",
                "<Default Extension=\"xlsx\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\"/>"
                + "<Override PartName=\"/word/charts/style1.xml\" ContentType=\"application/vnd.ms-office.chartstyle+xml\"/></Types>",
                StringComparison.Ordinal)));
        }

        return stream.ToArray();

        static void Add(ZipArchive zip, string name, byte[] data)
        {
            using Stream entry = zip.CreateEntry(name).Open();
            entry.Write(data);
        }
    }

    [Fact]
    public void AnEditedCopyHasItsOwnWorkbook()
    {
        Document read = DocxFormat.Load(new MemoryStream(WithWorkbook()));
        Island copy = (Island)read.Descendants<Island>().First().Clone();
        read.Descendants<Paragraph>().First().Inlines.Add(copy);
        copy.Chart!.Title.Text = "Copy";
        MemoryStream saved = new();
        DocxFormat.Save(read, saved);
        saved.Position = 0;
        using ZipArchive zip = new(saved);

        List<string> rels = [.. zip.Entries.Where(e => e.FullName.StartsWith("word/charts/_rels/", StringComparison.Ordinal)).Select(e => e.FullName)];
        Assert.Equal(2, rels.Count);
        List<XDocument> documents = [.. rels.Select(r => XDocument.Load(zip.GetEntry(r)!.Open()))];
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        string Target(XDocument d, string id) => (string)d.Root!.Elements(ns + "Relationship").First(e => (string?)e.Attribute("Id") == id).Attribute("Target")!;
        Assert.NotEqual(Target(documents[0], "rId1"), Target(documents[1], "rId1"));
        Assert.NotEqual(Target(documents[0], "rId2"), Target(documents[1], "rId2"));
        Assert.Equal("https://example.com/", Target(documents[1], "rId3"));
        foreach (string target in documents.SelectMany(d => new[] { Target(d, "rId1"), Target(d, "rId2") }))
        {
            string part = OpcPackage.ResolveTarget("/word/charts/chart1.xml", target);
            Assert.NotNull(zip.GetEntry(part[1..]));
        }

        // Word opens each workbook by its own part's type.
        Assert.Contains(".xlsx", zip.Entries.Select(e => e.FullName).First(n => n.StartsWith("word/embeddings/", StringComparison.Ordinal)), StringComparison.Ordinal);

        // The file must still load, with the copy's data.
        List<Island> back = [.. DocxFormat.Load(new MemoryStream(saved.ToArray())).Descendants<Island>()];
        Assert.Equal(2, back.Count);
        Assert.Equal("Copy", back[1].Chart!.Title.Text);
    }

    /// <summary>The positions Excel takes for each type: its own setter, and files with each position opened in Excel.</summary>
    [Theory]
    [InlineData(ChartType.Column, false, ChartGrouping.Clustered, "Center InsideBase InsideEnd OutsideEnd")]
    [InlineData(ChartType.Column, false, ChartGrouping.Stacked, "Center InsideBase InsideEnd")]
    [InlineData(ChartType.Column, false, ChartGrouping.PercentStacked, "Center InsideBase InsideEnd")]
    [InlineData(ChartType.Bar, false, ChartGrouping.Clustered, "Center InsideBase InsideEnd OutsideEnd")]
    [InlineData(ChartType.Bar, false, ChartGrouping.Stacked, "Center InsideBase InsideEnd")]
    [InlineData(ChartType.Bar, false, ChartGrouping.PercentStacked, "Center InsideBase InsideEnd")]
    [InlineData(ChartType.Line, false, ChartGrouping.Standard, "Bottom Center Left Right Top")]
    [InlineData(ChartType.Line, false, ChartGrouping.Stacked, "Bottom Center Left Right Top")]
    [InlineData(ChartType.Area, false, ChartGrouping.Standard, "")]
    [InlineData(ChartType.Pie, false, null, "BestFit Center InsideEnd OutsideEnd")]
    [InlineData(ChartType.Pie, true, null, "BestFit Center InsideEnd OutsideEnd")]
    [InlineData(ChartType.PieOfPie, false, null, "BestFit Center InsideEnd OutsideEnd")]
    [InlineData(ChartType.Doughnut, false, null, "")]
    [InlineData(ChartType.Scatter, false, null, "Bottom Center Left Right Top")]
    [InlineData(ChartType.Bubble, false, null, "Bottom Center Left Right Top")]
    [InlineData(ChartType.Radar, false, null, "")]
    [InlineData(ChartType.Stock, false, null, "Bottom Center Left Right Top")]
    [InlineData(ChartType.Column, true, ChartGrouping.Clustered, "")]
    [InlineData(ChartType.Line, true, ChartGrouping.Standard, "")]
    [InlineData(ChartType.Area, true, ChartGrouping.Standard, "")]
    public void ALabelTakesOnlyThePositionsItsTypeAllows(ChartType type, bool threeD, ChartGrouping? grouping, string allowed)
    {
        Chart chart = Chart.Create(type, threeD);
        ChartPlot plot = chart.Plots[0];
        if (grouping is not null)
        {
            plot.Grouping = grouping;
        }

        ChartSeries series = plot.Series.Add("S", ["A", "B"], [1, 2]);
        foreach (ChartLabelPosition position in Enum.GetValues<ChartLabelPosition>())
        {
            if (allowed.Split(' ').Contains(position.ToString()))
            {
                series.DataLabels.Position = position;
                Assert.Equal(position, series.DataLabels.Position);
                plot.DataLabels.Position = position;
                series.DataLabels.Position = null;
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => series.DataLabels.Position = position);
                Assert.Throws<InvalidOperationException>(() => plot.DataLabels.Position = position);
            }
        }
    }

    [Fact]
    public void GroupingTakesAwayAPositionItNoLongerAllows()
    {
        Chart chart = Chart.Create(ChartType.Column);
        ChartPlot plot = chart.Plots[0];
        plot.Grouping = ChartGrouping.Clustered;
        ChartSeries first = plot.Series.Add("S", ["A"], [1]);
        ChartSeries second = plot.Series.Add("T", ["A"], [1]);
        first.DataLabels.Position = ChartLabelPosition.OutsideEnd;
        second.DataLabels.Position = ChartLabelPosition.Center;
        plot.Grouping = ChartGrouping.Stacked;
        Assert.Null(first.DataLabels.Position);
        Assert.Equal(ChartLabelPosition.Center, second.DataLabels.Position);
    }

    public static TheoryData<string, ChartType, bool> Misplaced() => new()
    {
        { "GapWidth", ChartType.Line, false },
        { "Overlap", ChartType.Pie, false },
        { "Overlap", ChartType.Column, true },
        { "GapDepth", ChartType.Column, false },
        { "BarShape", ChartType.Column, false },
        { "FirstSliceAngle", ChartType.Column, false },
        { "HoleSize", ChartType.Pie, false },
        { "ScatterStyle", ChartType.Line, false },
        { "RadarStyle", ChartType.Column, false },
        { "BubbleScale", ChartType.Scatter, false },
        { "Wireframe", ChartType.Column, false },
        { "ShowMarkers", ChartType.Scatter, false },
        { "Grouping", ChartType.Scatter, false },
        { "Grouping", ChartType.Pie, false },
        { "GroupingClustered", ChartType.Line, false },
        { "GroupingClustered", ChartType.Area, false },
        { "VaryColors", ChartType.Stock, false },
        { "Smooth", ChartType.Column, false },
        { "InvertIfNegative", ChartType.Line, false },
        { "Explosion", ChartType.Column, false },
        { "Bubble3D", ChartType.Pie, false },
    };

    private static void Set(string property, ChartPlot plot, bool clear)
    {
        ChartSeries series = plot.Series.Count > 0 ? plot.Series[0] : plot.Series.Add("S", ["A"], [1]);
        switch (property)
        {
            case "GapWidth": plot.GapWidth = clear ? null : 50; break;
            case "Overlap": plot.Overlap = clear ? null : 50; break;
            case "GapDepth": plot.GapDepth = clear ? null : 50; break;
            case "BarShape": plot.BarShape = clear ? null : ChartBarShape.Cone; break;
            case "FirstSliceAngle": plot.FirstSliceAngle = clear ? null : 50; break;
            case "HoleSize": plot.HoleSize = clear ? null : 50; break;
            case "ScatterStyle": plot.ScatterStyle = clear ? null : ChartScatterStyle.Line; break;
            case "RadarStyle": plot.RadarStyle = clear ? null : ChartRadarStyle.Filled; break;
            case "BubbleScale": plot.BubbleScale = clear ? null : 50; break;
            case "Wireframe": plot.Wireframe = clear ? null : true; break;
            case "ShowMarkers": plot.ShowMarkers = clear ? null : true; break;
            case "Grouping": plot.Grouping = clear ? null : ChartGrouping.Stacked; break;
            case "GroupingClustered": plot.Grouping = clear ? null : ChartGrouping.Clustered; break;
            case "VaryColors": plot.VaryColors = clear ? null : true; break;
            case "Smooth": series.Smooth = clear ? null : true; break;
            case "InvertIfNegative": series.InvertIfNegative = clear ? null : true; break;
            case "Explosion": series.Explosion = clear ? null : 10; break;
            case "Bubble3D": series.Bubble3D = clear ? null : true; break;
            default: throw new ArgumentException(property);
        }
    }

    [Theory]
    [MemberData(nameof(Misplaced))]
    public void APropertyItsTypeHasNoPlaceForThrows(string property, ChartType type, bool threeD)
    {
        ChartPlot plot = Chart.Create(type, threeD).Plots[0];
        Assert.Throws<InvalidOperationException>(() => Set(property, plot, clear: false));
        Set(property, plot, clear: true);
    }

    [Theory]
    [InlineData("GapWidth", ChartType.Column, false)]
    [InlineData("GapWidth", ChartType.BarOfPie, false)]
    [InlineData("Overlap", ChartType.Bar, false)]
    [InlineData("GapDepth", ChartType.Column, true)]
    [InlineData("BarShape", ChartType.Bar, true)]
    [InlineData("FirstSliceAngle", ChartType.Doughnut, false)]
    [InlineData("HoleSize", ChartType.Doughnut, false)]
    [InlineData("ScatterStyle", ChartType.Scatter, false)]
    [InlineData("RadarStyle", ChartType.Radar, false)]
    [InlineData("BubbleScale", ChartType.Bubble, false)]
    [InlineData("Wireframe", ChartType.Surface, false)]
    [InlineData("ShowMarkers", ChartType.Line, false)]
    [InlineData("Grouping", ChartType.Area, false)]
    [InlineData("GroupingClustered", ChartType.Column, false)]
    [InlineData("VaryColors", ChartType.Column, false)]
    [InlineData("Smooth", ChartType.Scatter, false)]
    [InlineData("InvertIfNegative", ChartType.Bubble, false)]
    [InlineData("Explosion", ChartType.Doughnut, false)]
    [InlineData("Bubble3D", ChartType.Bubble, false)]
    public void APropertyItsTypeHasIsSet(string property, ChartType type, bool threeD)
    {
        ChartPlot plot = Chart.Create(type, threeD).Plots[0];
        Set(property, plot, clear: false);
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

    [Theory]
    [InlineData(ChartType.Treemap)]
    [InlineData(ChartType.Sunburst)]
    [InlineData(ChartType.RegionMap)]
    public void AnExtendedChartWithoutACategoryAxisHasNoGapWidth(ChartType type)
    {
        Assert.Null(Chart.Create(type).Plots[0].GapWidth);
    }

    [Theory]
    [InlineData("<cx:catScaling/>", null)]
    [InlineData("<cx:catScaling gapWidth=\"auto\"/>", null)]
    [InlineData("<cx:catScaling gapWidth=\"-1\"/>", null)]
    [InlineData("<cx:catScaling gapWidth=\"0\"/>", 0)]
    [InlineData("<cx:catScaling gapWidth=\"0.5\"/>", 50)]
    public void AnExtendedChartReadsItsGapWidthAsAPercentage(string scaling, int? expected)
    {
        string xml = $"""
            <cx:chartSpace xmlns:cx="http://schemas.microsoft.com/office/drawing/2014/chartex"><cx:chart><cx:plotArea><cx:plotAreaRegion><cx:series layoutId="waterfall"/></cx:plotAreaRegion>
            <cx:axis id="0">{scaling}</cx:axis></cx:plotArea></cx:chart></cx:chartSpace>
            """;
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Equal(expected, Chart.Load(stream).Plots[0].GapWidth);
    }

    [Fact]
    public void LabelsInLevelsCannotLoseTheirReference()
    {
        Chart chart = Made();
        ChartData categories = chart.Series[0].Categories;
        categories.Reference = "Sheet1!$A$2:$B$3";
        categories.SetTextLevels([["Jan", "Feb"], ["Q1", null]]);

        Assert.Throws<InvalidOperationException>(() => categories.Reference = null);
        Assert.Equal("Sheet1!$A$2:$B$3", categories.Reference);
        Assert.Equal(2, categories.TextLevels.Count);

        categories.SetText(categories.Text);
        categories.Reference = null;
        ChartData back = RoundTrip(chart).Series[0].Categories;
        Assert.Null(back.Reference);
        Assert.Equal(["Jan", "Feb"], back.Text);
    }

    private static Chart Parsed(string xml) => Chart.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));

    private const string RichTitle = """
        <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><c:chart><c:title><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:rPr lang="en-US" sz="1400" b="1"><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill><a:latin typeface="Arial"/></a:rPr><a:t>Old</a:t></a:r></a:p></c:rich></c:tx></c:title><c:plotArea><c:barChart><c:ser><c:idx val="0"/><c:order val="0"/><c:dLbls><c:delete val="1"/></c:dLbls><c:val><c:numRef><c:f>S!$A$1:$A$2</c:f><c:extLst><c:ext uri="u"/></c:extLst></c:numRef></c:val></c:ser></c:barChart></c:plotArea></c:chart></c:chartSpace>
        """;

    [Theory]
    [InlineData(ChartType.Column)]
    [InlineData(ChartType.Waterfall)]
    public void WritingNumbersWithoutAFormatKeepsTheOneTheyHad(ChartType type)
    {
        Chart chart = Chart.Create(type);
        ChartSeries series = type == ChartType.Column ? chart.Plots[0].Series.Add("a", null, [1]) : chart.Plots[0].Series[0];
        series.Values.SetNumbers([1, 2], "0.0%");
        series.Values.SetNumbers([3, 4]);
        Assert.Equal("0.0%", RoundTrip(chart).Plots[0].Series[0].Values.FormatCode);
    }

    [Theory]
    [InlineData(ChartType.Waterfall)]
    [InlineData(ChartType.Histogram)]
    [InlineData(ChartType.Pareto)]
    [InlineData(ChartType.BoxWhisker)]
    [InlineData(ChartType.Treemap)]
    [InlineData(ChartType.Sunburst)]
    [InlineData(ChartType.Funnel)]
    [InlineData(ChartType.RegionMap)]
    public void ReadingAnExtendedChartChangesNothing(ChartType type)
    {
        Chart chart = RoundTrip(Chart.Create(type));
        foreach (ChartSeries series in chart.Series)
        {
            _ = (series.Categories.Reference, series.Categories.Count, series.Categories.Text, series.Categories.TextLevels, series.Categories.IsNumeric);
            _ = (series.Values.Reference, series.Values.Numbers, series.Values.FormatCode, series.BubbleSizes.Reference, series.Subtotals, series.Binning.BinSize);
        }

        Assert.False(chart.IsChanged);
    }

    [Fact]
    public void WhatOnlyAPlainChartHasWritesNothingToAnExtendedOne()
    {
        Chart chart = Chart.Create(ChartType.Waterfall);
        byte[] before = chart.ToBytes();
        ChartSeries series = chart.Plots[0].Series[0];
        series.Smooth = true;
        series.InvertIfNegative = true;
        series.Explosion = 5;
        series.Bubble3D = true;
        series.Marker.Symbol = ChartMarkerSymbol.Circle;
        series.BubbleSizes.SetNumbers([1]);
        series.BubbleSizes.Reference = "S!$A$1";
        ChartPlot plot = chart.Plots[0];
        plot.Grouping = ChartGrouping.Stacked;
        plot.VaryColors = true;
        plot.Overlap = 5;
        plot.GapDepth = 5;
        plot.BarShape = ChartBarShape.Cone;
        plot.FirstSliceAngle = 5;
        plot.HoleSize = 50;
        plot.ScatterStyle = ChartScatterStyle.Line;
        plot.RadarStyle = ChartRadarStyle.Filled;
        plot.BubbleScale = 50;
        plot.ShowNegativeBubbles = true;
        plot.BubbleSizeIsWidth = true;
        plot.Wireframe = true;
        plot.ShowMarkers = true;
        plot.DataLabels.ShowValue = true;
        chart.View3D.RotationX = 5;
        chart.Title.Layout.X = 0.5;
        chart.Legend.Layout.X = 0.5;
        chart.PlotAreaLayout.X = 0.5;
        chart.Legend.Position = ChartLegendPosition.TopRight;
        foreach (ChartAxis axis in chart.Axes)
        {
            axis.TickLabelSkip = 2;
            axis.TickMarkSkip = 2;
            axis.NoMultiLevelLabels = true;
            axis.BaseTimeUnit = ChartTimeUnit.Days;
            axis.MajorTimeUnit = ChartTimeUnit.Days;
            axis.MinorTimeUnit = ChartTimeUnit.Days;
        }

        Assert.Equal(before, chart.ToBytes());
        Assert.Null(series.Smooth);
        Assert.Null(plot.Overlap);
        Assert.Null(chart.View3D.RotationX);
    }

    [Fact]
    public void APointOfAnExtendedChartTakesOnlyWhatItHas()
    {
        Chart chart = Parsed("""
            <cx:chartSpace xmlns:cx="http://schemas.microsoft.com/office/drawing/2014/chartex"><cx:chart><cx:plotArea><cx:plotAreaRegion><cx:series layoutId="funnel"><cx:dataPt idx="0"/></cx:series></cx:plotAreaRegion></cx:plotArea></cx:chart></cx:chartSpace>
            """);
        byte[] before = chart.ToBytes();
        ChartPoint point = chart.Series[0].Points[0];
        point.Explosion = 5;
        point.InvertIfNegative = true;
        point.Marker.Symbol = ChartMarkerSymbol.Circle;
        Assert.Equal(before, chart.ToBytes());
    }

    [Fact]
    public void WhatOnlyAnExtendedChartHasWritesNothingToAPlainOne()
    {
        Chart chart = Made();
        byte[] before = chart.ToBytes();
        ChartSeries series = chart.Series[0];
        series.Subtotals = [1];
        series.Binning.BinSize = 1;
        series.Binning.Underflow = 1;
        series.QuartileMethod = "exclusive";
        series.ShowMeanLine = true;
        series.ParentLabels = ChartParentLabels.Banner;
        Assert.Equal(before, chart.ToBytes());
        Assert.Empty(series.Subtotals);
    }

    [Fact]
    public void ACategoryAxisOfAnExtendedChartKeepsItsOneScale()
    {
        Chart chart = Chart.Create(ChartType.Waterfall);
        ChartAxis category = chart.Axes.First(a => a.Kind == ChartAxisKind.Category);
        category.Minimum = 1;
        category.Maximum = 2;
        category.MajorUnit = 1;
        category.MinorUnit = 1;
        Assert.Null(category.Element.Element(ChartNs.Cx + "valScaling"));
        Assert.Equal(ChartAxisKind.Category, category.Kind);

        ChartAxis value = chart.Axes.First(a => a.Kind == ChartAxisKind.Value);
        value.Minimum = 1;
        Assert.Equal(1, RoundTrip(chart).Axes.First(a => a.Kind == ChartAxisKind.Value).Minimum);
    }

    [Fact]
    public void ClearingAnExtendedTitlesTextClearsIt()
    {
        Chart chart = Chart.Create(ChartType.Waterfall);
        chart.Title.Text = "Gone";
        chart.Title.Text = null;
        Assert.Null(chart.Title.Text);
        Assert.Null(RoundTrip(chart).Title.Text);
    }

    [Theory]
    [InlineData(ChartType.Column)]
    [InlineData(ChartType.Waterfall)]
    public void NewTitleTextKeepsTheLookItHad(ChartType type)
    {
        Chart chart = Chart.Create(type);
        chart.Title.Text = "One";
        chart.Title.TextFormat.Bold = true;
        chart.Title.Text = "Two";
        Chart back = RoundTrip(chart);
        Assert.Equal("Two", back.Title.Text);
        Assert.True(back.Title.TextFormat.Bold);
    }

    [Fact]
    public void NewTitleTextKeepsItsRunsLook()
    {
        Chart chart = Parsed(RichTitle);
        chart.Title.Text = "New";
        XElement run = chart.Root.Descendants(ChartNs.A + "rPr").Single();
        Assert.Equal("1400", (string?)run.Attribute("sz"));
        Assert.Equal("New", chart.Title.Text);
    }

    [Theory]
    [InlineData(ChartType.Column)]
    [InlineData(ChartType.Waterfall)]
    public void TakingATitlesReferenceAwayKeepsItsText(ChartType type)
    {
        Chart chart = Chart.Create(type);
        chart.Title.Text = "Kept";
        chart.Title.Reference = "S!$A$1";
        chart.Title.Reference = null;
        Chart back = RoundTrip(chart);
        Assert.Equal("Kept", back.Title.Text);
        Assert.Null(back.Title.Reference);
    }

    [Theory]
    [InlineData(ChartType.Column)]
    [InlineData(ChartType.Waterfall)]
    public void ATitleKeepsItsLookWhenItGetsAReference(ChartType type)
    {
        Chart chart = Chart.Create(type);
        chart.Title.Text = "Linked";
        chart.Title.TextFormat.Bold = true;
        chart.Title.Reference = "S!$A$1";
        Chart back = RoundTrip(chart);
        Assert.True(back.Title.TextFormat.Bold);
        Assert.Equal("Linked", back.Title.Text);

        back.Title.Text = "Written";
        Assert.True(back.Title.TextFormat.Bold);
    }

    [Fact]
    public void AColourOrFontIsNotHiddenByARunThatSaysOtherwise()
    {
        Chart chart = Parsed(RichTitle);
        chart.Title.TextFormat.Color = ChartColor.FromHex("00FF00");
        chart.Title.TextFormat.FontName = "Calibri";
        XElement run = chart.Root.Descendants(ChartNs.A + "rPr").Single();
        Assert.Null(run.Element(ChartNs.A + "solidFill"));
        Assert.Null(run.Element(ChartNs.A + "latin"));
    }

    [Fact]
    public void DeletedLabelsComeBackWhenAskedFor()
    {
        Chart chart = Parsed(RichTitle);
        ChartSeries series = chart.Series[0];
        Assert.False(series.HasDataLabels);
        series.HasDataLabels = true;
        Assert.True(series.HasDataLabels);
        Assert.True(series.DataLabels.ShowValue);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("format")]
    [InlineData("separator")]
    public void WritingToDeletedLabelsLeavesNoDeleteBesideThem(string what)
    {
        ChartSeries series = Parsed(RichTitle).Series[0];
        ChartDataLabels labels = series.DataLabels;
        switch (what)
        {
            case "position":
                labels.Position = ChartLabelPosition.Center;
                break;
            case "format":
                labels.NumberFormat = "0";
                break;
            default:
                labels.Separator = ";";
                break;
        }

        XElement element = series.Element.Element(ChartNs.C + "dLbls")!;
        Assert.Null(element.Element(ChartNs.C + "delete"));
        Assert.NotNull(element.Element(ChartNs.C + "showVal"));
    }

    [Fact]
    public void LabelsThatFollowTheirCellsHaveNoFormatOfTheirOwn()
    {
        Chart chart = Made();
        chart.Series[0].DataLabels.NumberFormat = "0";
        chart.Series[0].Element.Element(ChartNs.C + "dLbls")!.Element(ChartNs.C + "numFmt")!.SetAttributeValue("sourceLinked", "1");
        Assert.Null(chart.Series[0].DataLabels.NumberFormat);
    }

    [Theory]
    [InlineData(ChartType.Column)]
    [InlineData(ChartType.Bubble)]
    [InlineData(ChartType.Waterfall)]
    public void ValuesThatMustBeNumbersRefuseText(ChartType type)
    {
        Chart chart = Chart.Create(type);
        ChartSeries series = type == ChartType.Waterfall ? chart.Plots[0].Series[0] : chart.Plots[0].Series.Add("a", null, [1]);
        Assert.Throws<InvalidOperationException>(() => series.Values.SetText(["a"]));
        Assert.Throws<InvalidOperationException>(() => series.Values.SetTextLevels([["a"]]));
        if (type == ChartType.Bubble)
        {
            Assert.Throws<InvalidOperationException>(() => series.BubbleSizes.SetText(["a"]));
        }

        series.Categories.SetText(["a"]);
    }

    [Fact]
    public void LabelsInLevelsWithoutAReferenceReadAsHavingNone()
    {
        Chart chart = Made();
        ChartData categories = chart.Series[0].Categories;
        categories.SetTextLevels([["Jan", "Feb"], ["Q1", null]]);
        Assert.Null(categories.Reference);
        Assert.Null(RoundTrip(chart).Series[0].Categories.Reference);
    }

    [Fact]
    public void AReferencesExtensionsGoWithIt()
    {
        Chart chart = Parsed(RichTitle);
        chart.Series[0].Values.Reference = null;
        Assert.Empty(chart.Root.Descendants(ChartNs.C + "extLst"));

        Chart other = Parsed(RichTitle);
        other.Series[0].Values.Reference = "S!$B$1:$B$2";
        Assert.Empty(other.Root.Descendants(ChartNs.C + "extLst"));

        Chart same = Parsed(RichTitle);
        same.Series[0].Values.Reference = "S!$A$1:$A$2";
        Assert.Single(same.Root.Descendants(ChartNs.C + "extLst"));
    }

    [Fact]
    public void ANewSeriesGetsAnIdNoOtherHasAndGoesBeforeExtensions()
    {
        Chart chart = Chart.Create(ChartType.Funnel);
        ChartSeriesCollection all = chart.Plots[0].Series;
        ChartSeries second = all.Add("b", null, [1]);
        all.Add("c", null, [1]);
        all.Remove(second);
        all.Add("d", null, [1]);
        XElement region = chart.Plots[0].Element;
        List<string?> ids = [.. region.Elements(ChartNs.Cx + "series").Select(e => (string?)e.Attribute("uniqueId"))];
        Assert.Equal(ids.Count, ids.Distinct().Count());

        region.Add(new XElement(ChartNs.Cx + "extLst"));
        all.Add("e", null, [1]);
        Assert.Equal("extLst", region.Elements().Last().Name.LocalName);
    }

    [Fact]
    public void AChartEditedInAHeaderIsSaved()
    {
        Document document = new();
        Section section = document.AddSection();
        section.Body.AddParagraph("Body");
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("").Inlines.Add(new Island(document, Made(), 360, 216));
        Document back = Reloaded(document);
        Island island = back.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).Descendants<Island>().Single();
        island.Chart!.Title.Text = "In the header";

        Island read = Reloaded(back).Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).Descendants<Island>().Single();
        Assert.Equal("In the header", read.Chart!.Title.Text);
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
