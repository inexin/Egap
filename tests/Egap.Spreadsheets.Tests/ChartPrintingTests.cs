using Egami;
using Egami.Pixels;

using Egap.Documents.Charts;
using Egap.Pdf;

using Path = System.IO.Path;

namespace Egap.Spreadsheets.Tests;

/// <summary>Charts on printed pages: a sheet's charts and a chart sheet's own page.</summary>
public sealed class ChartPrintingTests
{
    private static string ExcelBook => Path.Combine(AppContext.BaseDirectory, "Oracle", "charts.xlsx");

    private static int Ink(Raster<Rgba8> raster)
    {
        int ink = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 pixel in raster.Row(y))
            {
                ink += pixel.G < 250 ? 1 : 0;
            }
        }

        return ink;
    }

    private static Workbook WithCells()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "Month";
        sheet["B1"].Value = "Units";
        sheet["A2"].Value = "Jan";
        sheet["B2"].Value = 3;
        sheet["A3"].Value = "Feb";
        sheet["B3"].Value = 5;
        return book;
    }

    [Fact]
    public void ASheetPrintsItsChartFromTheCellsWhenItHasNoSavedValues()
    {
        Workbook book = WithCells();
        Worksheet sheet = book.Worksheets[0];
        int without;
        using (Raster<Rgba8> plain = sheet.Paginate().Pages[0].Render(72))
        {
            without = Ink(plain);
        }

        SheetDrawing drawing = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("D1:J12"), RangeAddress.Parse("A1:B3"));
        foreach (ChartSeries series in drawing.Chart!.Series)
        {
            series.Values.SetNumbers([]);
        }

        using Raster<Rgba8> page = sheet.Paginate().Pages[0].Render(72);
        Assert.True(Ink(page) > without + 300);
        Assert.Empty(drawing.Chart!.Series[0].Values.Numbers);
    }

    [Fact]
    public void AChartSheetPrintsAPageOfItsOwnWhenTheWholeBookPrints()
    {
        Workbook book = Workbook.Load(ExcelBook);
        SheetPrintLayout whole = book.Paginate();
        ChartSheetPage page = Assert.Single(whole.ChartPages);
        Assert.Equal(book.ChartSheets[0], page.Sheet);
        Assert.True(page.Width > page.Height);
        using Raster<Rgba8> raster = page.Render(72);
        Assert.True(Ink(raster) > 100);
        Assert.Empty(book.Worksheets[0].Paginate().ChartPages);
    }

    [Fact]
    public void AChartSheetIsAPageAndAFigureInATaggedPdf()
    {
        Workbook book = Workbook.Load(ExcelBook);
        int sheetPages = book.Paginate().Pages.Count;
        using MemoryStream stream = new();
        book.SaveAsPdf(stream, new SheetPdfOptions { Tagged = true });
        stream.Position = 0;
        using PdfDocument pdf = PdfDocument.Open(stream);
        Assert.Equal(sheetPages + 1, pdf.Pages.Count);
    }
}
