using System.IO.Compression;

using Egap.Documents.Charts;

namespace Egap.Spreadsheets.Tests;

/// <summary>Charts on sheets and chart sheets: read, kept byte for byte when left alone, edited and filled from cells.</summary>
public sealed class ChartTests
{
    private static string ExcelBook => Path.Combine(AppContext.BaseDirectory, "Oracle", "charts.xlsx");

    private static Workbook Sample()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "Month";
        sheet["B1"].Value = "North";
        sheet["C1"].Value = "South";
        string[] months = ["Jan", "Feb", "Mar"];
        for (int row = 0; row < 3; row++)
        {
            sheet[row + 1, 0].Value = months[row];
            sheet[row + 1, 1].Value = row + 1;
            sheet[row + 1, 2].Value = (row + 1) * 10;
        }

        return book;
    }

    private static Workbook Reloaded(Workbook book)
    {
        MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Workbook.Load(stream);
    }

    private static Dictionary<string, byte[]> Parts(Stream package)
    {
        using ZipArchive zip = new(package, ZipArchiveMode.Read, leaveOpen: true);
        Dictionary<string, byte[]> parts = [];
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream stream = entry.Open();
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            parts[entry.FullName] = copy.ToArray();
        }

        return parts;
    }

    private static Dictionary<string, byte[]> Saved(Workbook book)
    {
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Parts(stream);
    }

    [Fact]
    public void ChartsLeftAloneKeepTheirBytes()
    {
        Dictionary<string, byte[]> original;
        using (FileStream file = File.OpenRead(ExcelBook))
        {
            original = Parts(file);
        }

        Workbook book = Workbook.Load(ExcelBook);
        foreach (Worksheet sheet in book.Worksheets)
        {
            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                _ = drawing.Chart?.Series;
            }
        }

        Dictionary<string, byte[]> saved = Saved(book);
        List<string> charts = [.. original.Keys.Where(k => k.StartsWith("xl/charts/", StringComparison.Ordinal) && !k.Contains("_rels", StringComparison.Ordinal))];
        Assert.True(charts.Count > 100);
        foreach (string chart in charts)
        {
            Assert.True(saved.ContainsKey(chart), chart);
            Assert.Equal(original[chart], saved[chart]);
        }
    }

    [Fact]
    public void AnEditChangesOneChartAndKeepsWhatTheModelDoesNotHold()
    {
        Dictionary<string, byte[]> original;
        using (FileStream file = File.OpenRead(ExcelBook))
        {
            original = Parts(file);
        }

        Workbook book = Workbook.Load(ExcelBook);
        book.Worksheets["Charts"].Drawings.First(d => d.Name == "Dressed").Chart!.Title.Text = "Changed";
        Dictionary<string, byte[]> saved = Saved(book);
        List<string> changed = [.. original.Keys.Where(k => k.StartsWith("xl/charts/", StringComparison.Ordinal) && !k.Contains("_rels", StringComparison.Ordinal) && !original[k].SequenceEqual(saved[k]))];
        string only = Assert.Single(changed);
        string xml = System.Text.Encoding.UTF8.GetString(saved[only]);
        Assert.Contains("Changed", xml, StringComparison.Ordinal);
        Assert.Contains("c16:uniqueId", xml, StringComparison.Ordinal);
        Assert.Contains("c14:style", xml, StringComparison.Ordinal);
        Assert.Contains("xl/charts/_rels/" + only["xl/charts/".Length..] + ".rels", saved.Keys);
    }

    [Fact]
    public void EveryExcelChartIsTheTypeItIsCalled()
    {
        Workbook book = Workbook.Load(ExcelBook);
        Dictionary<string, Chart> charts = book.Worksheets.SelectMany(s => s.Drawings).Where(d => d.Kind == SheetDrawingKind.Chart).ToDictionary(d => d.Name, d => d.Chart!);
        Assert.Equal(ChartType.Column, charts["ColumnClustered"].Type);
        Assert.Equal(ChartType.Bar, charts["BarStacked"].Type);
        Assert.Equal(ChartGrouping.Stacked, charts["BarStacked"].Plots[0].Grouping);
        Assert.Equal(ChartType.Scatter, charts["ScatterSmooth"].Type);
        Assert.Equal(ChartType.Stock, charts["StockOHLC"].Type);
        Assert.Equal(4, charts["StockOHLC"].Series.Count);
        Assert.Equal(ChartType.BarOfPie, charts["BarOfPie"].Type);
        Assert.True(charts["Column3D"].Plots[0].Is3D);
        Assert.Equal(ChartType.Waterfall, charts["Waterfall"].Type);
        Assert.Equal(ChartType.Pareto, charts["Pareto"].Type);
        Assert.True(charts["Pareto"].IsExtended);
        Assert.Equal(ChartType.BoxWhisker, charts["BoxWhisker"].Type);
        Assert.Equal(2, charts["Combo"].Plots.Count(p => p.Type is ChartType.Column or ChartType.Line));
        Assert.Equal(ChartAxisKind.Date, charts["DateAxis"].Axes[0].Kind);
        Assert.Equal(10, charts["LogAxis"].Axes.Last(a => a.Kind == ChartAxisKind.Value).LogBase);
        Assert.Equal(2, charts["MultiLevel"].Series[0].Categories.TextLevels.Count);
    }

    [Fact]
    public void CellsFillAChartThroughItsDefinedNames()
    {
        Workbook book = Workbook.Load(ExcelBook);
        Chart chart = book.Worksheets["Data"].Drawings.First(d => d.Name == "Waterfall").Chart!;
        Assert.Equal(0, chart.Series[0].Values.Count);
        book.Worksheets["Data"].Drawings.First(d => d.Name == "Waterfall").RefreshChart();
        Assert.True(chart.Series[0].Values.Count > 0);
        Assert.True(chart.Series[0].Categories.Count > 0);
        Assert.All(chart.Series[0].Values.Numbers, n => Assert.NotNull(n));
    }

    [Fact]
    public void RefreshReadsTheCellsNow()
    {
        Workbook book = Sample();
        Worksheet sheet = book.Worksheets[0];
        SheetDrawing drawing = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("E1:K12"), RangeAddress.Parse("A1:C4"));
        Chart chart = drawing.Chart!;
        Assert.Equal(2, chart.Series.Count);
        Assert.Equal("North", chart.Series[0].Name);
        Assert.Equal("Sheet1!$B$1", chart.Series[0].NameReference);
        Assert.Equal("Sheet1!$A$2:$A$4", chart.Series[0].Categories.Reference);
        Assert.Equal([1d, 2d, 3d], chart.Series[0].Values.Numbers);
        Assert.Equal(["Jan", "Feb", "Mar"], chart.Series[0].Categories.Text);

        sheet["B2"].Value = 42;
        sheet["A2"].Value = "New";
        Assert.Equal(1d, chart.Series[0].Values.Numbers[0]);
        drawing.RefreshChart();
        Assert.Equal(42d, chart.Series[0].Values.Numbers[0]);
        Assert.Equal("New", chart.Series[0].Categories.Text[0]);
    }

    [Fact]
    public void SavingAChangedChartReadsTheCells()
    {
        Workbook book = Sample();
        Worksheet sheet = book.Worksheets[0];
        sheet.Drawings.AddChart(ChartType.Line, RangeAddress.Parse("E1:K12"), RangeAddress.Parse("A1:B4"));
        sheet["B3"].Value = 99;
        Chart chart = Reloaded(book).Worksheets[0].Drawings[0].Chart!;
        Assert.Equal(99d, chart.Series[0].Values.Numbers[1]);
    }

    [Fact]
    public void HiddenCellsStayOutUnlessTheChartSaysOtherwise()
    {
        Workbook book = Sample();
        Worksheet sheet = book.Worksheets[0];
        SheetDrawing drawing = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("E1:K12"), RangeAddress.Parse("A1:B4"));
        sheet.Rows[2].Hidden = true;
        drawing.RefreshChart();
        Assert.Equal([1d, 3d], drawing.Chart!.Series[0].Values.Numbers);
        drawing.Chart.PlotVisibleOnly = false;
        drawing.RefreshChart();
        Assert.Equal([1d, 2d, 3d], drawing.Chart.Series[0].Values.Numbers);
    }

    [Fact]
    public void ReferencesReachAreasNamesAndWholeColumns()
    {
        Workbook book = Sample();
        Worksheet sheet = book.Worksheets[0];
        book.Names.Add("Picked", "Sheet1!$B$2:$B$3");
        SheetDrawing drawing = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("E1:K12"));
        ChartPlot plot = drawing.Chart!.Plots[0];
        ChartSeries areas = plot.Series.Add("(Sheet1!$B$2,Sheet1!$C$4)");
        ChartSeries named = plot.Series.Add("Picked");
        ChartSeries column = plot.Series.Add("Sheet1!$C:$C");
        ChartSeries nothing = plot.Series.Add("Nowhere!$A$1");
        drawing.RefreshChart();
        Assert.Equal([1d, 30d], areas.Values.Numbers);
        Assert.Equal([1d, 2d], named.Values.Numbers);
        Assert.Equal(4, column.Values.Count);
        Assert.Equal(30d, column.Values.Numbers[3]);
        Assert.Equal(0, nothing.Values.Count);
    }

    [Fact]
    public void TextCountsAsZeroAndCellsPastTheSheetAsGaps()
    {
        Workbook book = Sample();
        Worksheet sheet = book.Worksheets[0];
        sheet["B3"].Value = "n/a";
        SheetDrawing drawing = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("E1:K12"));
        ChartSeries series = drawing.Chart!.Plots[0].Series.Add("Sheet1!$B$2:$B$6");
        drawing.RefreshChart();
        Assert.Equal([1d, 0d, 3d, null, null], series.Values.Numbers);
    }

    [Fact]
    public void AHugeReferenceIsCappedAndQuick()
    {
        Workbook book = Sample();
        SheetDrawing drawing = book.Worksheets[0].Drawings.AddChart(ChartType.Column, RangeAddress.Parse("E1:K12"));
        ChartSeries values = drawing.Chart!.Plots[0].Series.Add("Sheet1!$A$1:$XFC$1048575", "Sheet1!$A$1:$XFC$1048575");
        drawing.RefreshChart();
        Assert.True(values.Values.Count <= 1 << 20);
    }

    [Fact]
    public void ALoopOfNamesEnds()
    {
        Workbook book = Sample();
        book.Names.Add("One", "Two");
        book.Names.Add("Two", "One");
        SheetDrawing drawing = book.Worksheets[0].Drawings.AddChart(ChartType.Column, RangeAddress.Parse("E1:K12"));
        ChartSeries series = drawing.Chart!.Plots[0].Series.Add("One");
        drawing.RefreshChart();
        Assert.Equal(0, series.Values.Count);
    }

    [Theory]
    [InlineData(ChartType.Scatter)]
    [InlineData(ChartType.Bubble)]
    public void PointChartsTakeTheirXFromTheFirstColumn(ChartType type)
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int row = 0; row < 3; row++)
        {
            sheet[row, 0].Value = row;
            sheet[row, 1].Value = row * 2;
            sheet[row, 2].Value = row * 3;
        }

        Chart chart = sheet.Drawings.AddChart(type, RangeAddress.Parse("E1:K12"), RangeAddress.Parse("A1:C3")).Chart!;
        ChartSeries series = chart.Series[0];
        Assert.Equal([0d, 1d, 2d], series.Categories.Numbers);
        Assert.Equal([0d, 2d, 4d], series.Values.Numbers);
        if (type == ChartType.Bubble)
        {
            Assert.Single(chart.Series);
            Assert.Equal([0d, 3d, 6d], series.BubbleSizes.Numbers);
        }
        else
        {
            Assert.Equal(2, chart.Series.Count);
        }
    }

    [Fact]
    public void AChartSheetIsReadAndEdited()
    {
        Workbook book = Workbook.Load(ExcelBook);
        ChartSheet page = Assert.Single(book.ChartSheets);
        Assert.Equal("ChartPage", page.Name);
        Assert.Equal(SheetVisibility.Visible, page.Visibility);
        page.Chart!.Title.Text = "On its own";
        ChartSheet back = Assert.Single(Reloaded(book).ChartSheets);
        Assert.Equal("On its own", back.Chart!.Title.Text);
    }

    [Fact]
    public void ARemovedChartIsGone()
    {
        Workbook book = Sample();
        Worksheet sheet = book.Worksheets[0];
        SheetDrawing drawing = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("E1:K12"), RangeAddress.Parse("A1:B4"));
        Assert.Single(Reloaded(book).Worksheets[0].Drawings);
        sheet.Drawings.Remove(drawing);
        Assert.DoesNotContain(Saved(book).Keys, k => k.StartsWith("xl/charts/", StringComparison.Ordinal));
    }

    [Fact]
    public void OtherObjectsHaveNoChart()
    {
        Workbook book = Sample();
        SheetDrawing picture = book.Worksheets[0].Drawings.AddPicture([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10], RangeAddress.Parse("E1:F2"));
        Assert.Null(picture.Chart);
        Assert.Throws<InvalidOperationException>(picture.RefreshChart);
    }

    [Theory]
    [InlineData("O'Brien's", "'O''Brien''s'!$A$2:$A$3")]
    [InlineData("2024", "'2024'!$A$2:$A$3")]
    [InlineData("Plain_1", "Plain_1!$A$2:$A$3")]
    public void SheetNamesAreQuotedInReferences(string name, string reference)
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.Name = name;
        sheet["A1"].Value = "Label";
        sheet["B1"].Value = "Value";
        sheet["A2"].Value = "a";
        sheet["B2"].Value = 1;
        sheet["A3"].Value = "b";
        sheet["B3"].Value = 2;
        Chart chart = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("E1:K12"), RangeAddress.Parse("A1:B3")).Chart!;
        Assert.Equal(reference, chart.Series[0].Categories.Reference);
        Assert.Equal([1d, 2d], chart.Series[0].Values.Numbers);
    }
}
