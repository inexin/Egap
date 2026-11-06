using Egami;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Pdf;
using Egap.Spreadsheets.Printing;

namespace Egap.Spreadsheets.Tests;

/// <summary>Sheets laid out on pages (Build Plan task 17) and drawn to images and PDF (task 18).</summary>
public sealed class PrintTests
{
    private static readonly SheetPrintOptions Portable = new() { Fonts = FontSettings.Portable, PrintTime = new DateTime(2026, 11, 5, 9, 30, 0) };

    /// <summary>A sheet of <paramref name="rows"/> by <paramref name="columns"/> numbers, on A4 with Excel's normal margins.</summary>
    private static Worksheet Grid(int rows, int columns)
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.PageSetup.PaperSize = 9;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                sheet[r, c].Value = (r * 100) + c;
            }
        }

        return sheet;
    }

    [Fact]
    public void AnEmptySheetPrintsNothing()
    {
        Assert.Empty(Workbook.Create().Worksheets[0].Paginate(Portable).Pages);
    }

    [Fact]
    public void ColumnsAreSixtyFourPixelsInCalibri()
    {
        Worksheet sheet = Grid(1, 1);
        SheetFonts fonts = new(sheet.Workbook, FontSettings.Portable);

        Assert.Equal(7, fonts.MaxDigitWidth);
        Assert.Equal(64, fonts.DefaultColumnPixels(sheet));
        Assert.Equal(64, fonts.ColumnPixels(9.140625));
        Assert.Equal(0, fonts.ColumnPixels(0));
    }

    [Fact]
    public void PagesBreakWhereTheNextColumnOrRowDoesNotFit()
    {
        // A4 inside 0.7" side margins is 494.5 points: nine columns, which print 51.6 points wide (64 pixels in Calibri's
        // 47-dot digits at 600 dpi). Inside 0.75" top and bottom it is 733.9: 50 rows, which print 14.52 points tall.
        Worksheet sheet = Grid(100, 25);
        SheetPrintLayout layout = sheet.Paginate(Portable);

        Assert.Equal(6, layout.Pages.Count);
        Assert.Equal(RangeAddress.Parse("A1:I50"), layout.Pages[0].Cells);
        Assert.Equal(RangeAddress.Parse("A51:I100"), layout.Pages[1].Cells);
        Assert.Equal(RangeAddress.Parse("J1:R50"), layout.Pages[2].Cells);
        Assert.Equal(RangeAddress.Parse("S51:Y100"), layout.Pages[5].Cells);
        Assert.All(layout.Pages, p => Assert.Equal(595.28, p.Width, 0.01));
        Assert.Equal(Enumerable.Range(1, 6), layout.Pages.Select(p => p.Number));
    }

    [Fact]
    public void OverThenDownGoesAcrossFirst()
    {
        Worksheet sheet = Grid(100, 25);
        sheet.PageSetup.PageOrder = PrintPageOrder.OverThenDown;

        IReadOnlyList<SheetPage> pages = sheet.Paginate(Portable).Pages;

        Assert.Equal(RangeAddress.Parse("J1:R50"), pages[1].Cells);
        Assert.Equal(RangeAddress.Parse("A51:I100"), pages[3].Cells);
    }

    [Fact]
    public void LandscapeTurnsThePaper()
    {
        Worksheet sheet = Grid(100, 25);
        sheet.PageSetup.Orientation = PrintOrientation.Landscape;

        IReadOnlyList<SheetPage> pages = sheet.Paginate(Portable).Pages;

        Assert.Equal(841.89, pages[0].Width, 0.01);
        Assert.Equal(RangeAddress.Parse("A1:N33"), pages[0].Cells);
    }

    [Fact]
    public void ManualBreaksStartNewPages()
    {
        Worksheet sheet = Grid(30, 5);
        sheet.PageSetup.RowBreaks.Add(10);
        sheet.PageSetup.ColumnBreaks.Add(3);

        IReadOnlyList<SheetPage> pages = sheet.Paginate(Portable).Pages;

        Assert.Equal(["A1:C10", "A11:C30", "D1:E10", "D11:E30"], pages.Select(p => p.Cells.ToString()));
    }

    [Fact]
    public void FitToOnePageWideShrinksTheScaleToAWholePercent()
    {
        Worksheet sheet = Grid(100, 25);
        sheet.PageSetup.FitToPage = true;
        sheet.PageSetup.FitToWidth = 1;
        sheet.PageSetup.FitToHeight = 0;
        sheet.PageSetup.RowBreaks.Add(5);

        IReadOnlyList<SheetPage> pages = sheet.Paginate(Portable).Pages;

        // 25 columns of 430 dots in 494.5 points: 38 %, the largest whole percent at which they fit. Fitting ignores manual breaks.
        Assert.Equal(0.38, pages[0].Scale, 6);
        Assert.Equal(RangeAddress.Parse("A1:Y100"), pages[0].Cells);
        Assert.Single(pages);
    }

    [Fact]
    public void AScaleShrinksTheCells()
    {
        Worksheet sheet = Grid(100, 25);
        sheet.PageSetup.Scale = 50;

        IReadOnlyList<SheetPage> pages = sheet.Paginate(Portable).Pages;

        Assert.Equal(RangeAddress.Parse("A1:S100"), pages[0].Cells);
    }

    [Fact]
    public void TitleRowsRepeatAndTakeRoom()
    {
        Worksheet sheet = Grid(100, 5);
        sheet.PageSetup.PrintTitleRows = RangeAddress.Parse("1:2");

        IReadOnlyList<SheetPage> pages = sheet.Paginate(Portable).Pages;

        Assert.Equal(RangeAddress.Parse("A1:E50"), pages[0].Cells);
        Assert.Equal(RangeAddress.Parse("A51:E98"), pages[1].Cells);
        Assert.False(pages[0].Plan.TitleRows);
        Assert.True(pages[1].Plan.TitleRows);
    }

    [Fact]
    public void OnlyCellsThatShowExtendThePrint()
    {
        Worksheet sheet = Grid(3, 2);
        sheet["A300"].Style = CellStyle.Default with { Alignment = sheet["A1"].Style.Alignment with { WrapText = true } };
        sheet["Z1"].Style = sheet["A300"].Style;
        Assert.Equal(RangeAddress.Parse("A1:B3"), sheet.Paginate(Portable).Pages.Single().Cells);

        sheet["D5"].Style = CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromTheme(4)) };
        Assert.Equal(RangeAddress.Parse("A1:D5"), sheet.Paginate(Portable).Pages.Single().Cells);
    }

    [Fact]
    public void PrintAreasPrintEachRangeOnItsOwnPages()
    {
        Worksheet sheet = Grid(100, 25);
        sheet.PageSetup.PrintArea = [RangeAddress.Parse("B2:D5"), RangeAddress.Parse("F10:G12")];

        IReadOnlyList<SheetPage> pages = sheet.Paginate(Portable).Pages;

        Assert.Equal(["B2:D5", "F10:G12"], pages.Select(p => p.Cells.ToString()));
        Assert.Equal("Sheet1!$B$2:$D$5,Sheet1!$F$10:$G$12", sheet.Workbook.Names.Single(n => n.Name == "_xlnm.Print_Area").Formula);
        Assert.Equal(25, sheet.Paginate(Portable with { IgnorePrintAreas = true }).Pages.Sum(p => p.Cells.ColumnCount) / 2);
    }

    [Fact]
    public void PrintSettingsSurviveASave()
    {
        Worksheet sheet = Grid(3, 3);
        sheet.Name = "My sheet";
        sheet.PageSetup.PrintArea = [RangeAddress.Parse("A1:C3")];
        sheet.PageSetup.PrintTitleRows = RangeAddress.Parse("1:1");
        sheet.PageSetup.PrintTitleColumns = RangeAddress.Parse("A:A");
        sheet.PageSetup.RowBreaks.Add(2);
        sheet.PageSetup.ColumnBreaks.Add(1);

        (Workbook loaded, _) = XlsxSeed.RoundTrip(sheet.Workbook);
        PageSetup back = loaded.Worksheets[0].PageSetup;

        Assert.Equal([RangeAddress.Parse("A1:C3")], back.PrintArea);
        Assert.Equal(RangeAddress.Parse("1:1"), back.PrintTitleRows);
        Assert.Equal(RangeAddress.Parse("A:A"), back.PrintTitleColumns);
        Assert.Equal([2], back.RowBreaks);
        Assert.Equal([1], back.ColumnBreaks);
        Assert.Equal("'My sheet'!$A:$A,'My sheet'!$1:$1", loaded.Names.Single(n => n.Name == "_xlnm.Print_Titles").Formula);

        back.PrintTitleColumns = null;
        Assert.Equal("'My sheet'!$1:$1", loaded.Names.Single(n => n.Name == "_xlnm.Print_Titles").Formula);
        back.PrintArea = [];
        Assert.DoesNotContain(loaded.Names, n => n.Name == "_xlnm.Print_Area");
    }

    [Fact]
    public void HeaderCodesSplitIntoSectionsAndFields()
    {
        HeaderFooterCode code = HeaderFooterCode.Parse("&L&\"Arial,Bold\"&14Report &D&CPage &P of &N&R&KFF0000&A && more");

        Assert.Equal([HeaderFooterField.None, HeaderFooterField.Date], code.Sections[0].Select(r => r.Field));
        Assert.Equal("Arial", code.Sections[0][0].Format.Font);
        Assert.True(code.Sections[0][0].Format.Bold);
        Assert.Equal(14, code.Sections[0][0].Format.Size);
        Assert.Equal([HeaderFooterField.None, HeaderFooterField.Page, HeaderFooterField.None, HeaderFooterField.Pages], code.Sections[1].Select(r => r.Field));
        Assert.Equal(HeaderFooterField.Sheet, code.Sections[2][0].Field);
        Assert.Equal(CellColor.FromRgb(255, 0, 0), code.Sections[2][0].Format.Color);
        Assert.Equal(" & more", code.Sections[2][1].Text);
        Assert.Equal(1, HeaderFooterCode.Parse("&P+1").Sections[1][0].Offset);
        Assert.Equal("Plain", HeaderFooterCode.Parse("Plain").Sections[1][0].Text);
    }

    [Theory]
    [InlineData(4, 0.5999938962981048, 0x83, 0xCC, 0xEB)]
    [InlineData(4, -0.249977111117893, 0x10, 0x48, 0x61)]
    [InlineData(0, -0.1499984740745262, 0xD9, 0xD9, 0xD9)]
    [InlineData(1, 0.499984740745262, 0x80, 0x80, 0x80)]
    public void ThemeColoursTakeTheirTint(int theme, double tint, int red, int green, int blue)
    {
        Rgba8 color = SheetColors.Default.Resolve(CellColor.FromTheme(theme, tint), default);

        Assert.Equal((red, green, blue), (color.R, color.G, color.B));
    }

    [Fact]
    public void IndexedColoursComeFromThePalette()
    {
        Assert.Equal(new Rgba8(0xFF, 0, 0, 255), SheetColors.Default.Resolve(CellColor.FromIndex(10), default));
        Assert.Equal(new Rgba8(1, 2, 3, 4), SheetColors.Default.Resolve(CellColor.FromIndex(64), new Rgba8(1, 2, 3, 4)));
    }

    [Fact]
    public void APageRendersItsCells()
    {
        Worksheet sheet = Grid(5, 3);
        sheet["A1"].Style = CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromRgb(255, 0, 0)) };
        sheet.PageSetup.PrintGridLines = true;
        SheetPage page = sheet.Paginate(Portable).Pages[0];

        Raster<Rgba8> image = page.Render(72);

        Assert.Equal((596, 842), (image.Width, image.Height));

        // A1's red fill starts at the left and top margins: 0.7 and 0.75 inches.
        Rgba8 inside = image.GetPixel((int)(0.7 * 72) + 10, (int)(0.75 * 72) + 5);
        Assert.Equal((255, 0, 0), (inside.R, inside.G, inside.B));
        Assert.Contains(Pixels(image), p => p.R < 100 && p.G < 100 && p.B < 100);
    }

    [Fact]
    public void TextOverflowsIntoEmptyCellsButNotFullOnes()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "A long piece of text that runs well past its column";
        sheet["A2"].Value = "A long piece of text that runs well past its column";
        sheet["B2"].Value = "x";
        SheetPage page = sheet.Paginate(Portable).Pages[0];
        Raster<Rgba8> image = page.Render(72);
        int left = (int)(0.7 * 72) + 50, right = (int)(0.7 * 72) + 95;

        Assert.Contains(Column(image, left, right, (int)(0.75 * 72), 15), p => p.R < 128);
        Assert.DoesNotContain(Column(image, left + 10, right, (int)(0.75 * 72) + 15, 15), p => p.R < 128);
    }

    [Fact]
    public void EmptyPagesPrintUpToTheLastWithSomethingOnIt()
    {
        Worksheet sheet = Grid(1, 1);
        sheet[199, 0].Value = 1;
        int down = sheet.Paginate(Portable).Pages.Count;
        Assert.True(down >= 3);

        // The second column of pages holds one cell on its first page: its later pages are empty and at the end.
        sheet[0, 12].Value = 2;
        Assert.Equal(down + 1, sheet.Paginate(Portable).Pages.Count);

        // A cell on its last page keeps the empty ones before it, and gridlines keep them all.
        sheet[199, 12].Value = 3;
        Assert.Equal(2 * down, sheet.Paginate(Portable).Pages.Count);
        sheet[199, 12].Value = CellValue.Empty;
        sheet.PageSetup.PrintGridLines = true;
        Assert.Equal(2 * down, sheet.Paginate(Portable).Pages.Count);
    }

    [Fact]
    public void AnEmptyRowFollowsTheFontOfAllTheColumns()
    {
        Worksheet sheet = Grid(1, 1);
        double normal = new SheetGrid(sheet, new SheetFonts(sheet.Workbook, FontSettings.Portable)).Row(5);

        CellStyle small = CellStyle.Default with { Font = CellStyle.Default.Font with { Size = 8 } };
        int style = sheet.Workbook.Styles.Add(small);
        sheet.ColumnSpans.AddRead(new ColumnSpan { First = 0, Last = 9, Style = style });
        Assert.Equal(normal, new SheetGrid(sheet, new SheetFonts(sheet.Workbook, FontSettings.Portable)).Row(5));

        sheet.ColumnSpans.AddRead(new ColumnSpan { First = 10, Last = CellAddress.MaxColumns - 1, Style = style });
        Assert.True(new SheetGrid(sheet, new SheetFonts(sheet.Workbook, FontSettings.Portable)).Row(5) < normal);
    }

    [Fact]
    public void ASheetFittedToPagesWithNoPaperFitsThePrinters()
    {
        Worksheet sheet = Grid(2, 2);
        sheet.PageSetup.PaperSize = null;
        sheet.PageSetup.Orientation = PrintOrientation.Landscape;
        using MemoryStream plain = new();
        sheet.Workbook.Save(plain);
        plain.Position = 0;
        Assert.Equal(1, Workbook.Load(plain).Worksheets[0].PageSetup.PaperSize);

        sheet.PageSetup.FitToPage = true;
        using MemoryStream fitted = new();
        sheet.Workbook.Save(fitted);
        fitted.Position = 0;
        Assert.Null(Workbook.Load(fitted).Worksheets[0].PageSetup.PaperSize);
    }

    [Fact]
    public void ShowFormulasPrintsFormulasInColumnsOfTwiceTheWidth()
    {
        Worksheet sheet = Grid(1, 1);
        sheet["A1"].Value = 1.5;
        sheet["A1"].Style = sheet["A1"].Style with { NumberFormat = "0.00" };
        sheet["B1"].Formula = "A1*2";
        sheet.Workbook.Calculate();
        double width = new SheetGrid(sheet, new SheetFonts(sheet.Workbook, FontSettings.Portable)).Column(0);

        sheet.View.ShowFormulas = true;
        Assert.Equal(2 * width, new SheetGrid(sheet, new SheetFonts(sheet.Workbook, FontSettings.Portable)).Column(0));
        using MemoryStream stream = new();
        sheet.SaveAsPdf(stream, new SheetPdfOptions { Print = Portable });

        using PdfDocument pdf = PdfDocument.Open(stream.ToArray());
        string text = pdf.Pages[0].ExtractText();
        Assert.Contains("=A1*2", text, StringComparison.Ordinal);
        Assert.Contains("1.5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.50", text, StringComparison.Ordinal);

        using MemoryStream saved = new();
        sheet.Workbook.Save(saved);
        saved.Position = 0;
        Assert.True(Workbook.Load(saved).Worksheets[0].View.ShowFormulas);
    }

    [Fact]
    public void TextRunsOnOntoTheNextPage()
    {
        Worksheet sheet = Grid(1, 1);
        sheet[0, 0].Value = "1";
        sheet[1, 7].Value = "The start of a long text " + new string('x', 60);
        using MemoryStream stream = new();
        sheet.SaveAsPdf(stream, new SheetPdfOptions { Print = Portable });

        using PdfDocument pdf = PdfDocument.Open(stream.ToArray());
        Assert.Equal(2, pdf.Pages.Count);
        Assert.Contains("The start", pdf.Pages[1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void HeadersAndFootersShowTheirFields()
    {
        Worksheet sheet = Grid(100, 2);
        sheet.PageSetup.Footer = "&CPage &P of &N";
        sheet.PageSetup.Header = "&L&A";
        using MemoryStream stream = new();
        sheet.SaveAsPdf(stream, new SheetPdfOptions { Print = Portable });

        using PdfDocument pdf = PdfDocument.Open(stream.ToArray());
        Assert.Equal(2, pdf.Pages.Count);
        string text = pdf.Pages[1].ExtractText();
        Assert.Contains("Page 2 of 2", text, StringComparison.Ordinal);
        Assert.Contains("Sheet1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATaggedPdfHasATableOfTheCells()
    {
        Worksheet sheet = Grid(60, 3);
        sheet["A1"].Value = "Name";
        sheet.PageSetup.PrintTitleRows = RangeAddress.Parse("1:1");
        using MemoryStream stream = new();
        sheet.SaveAsPdf(stream, new SheetPdfOptions { Print = Portable, Conformance = PdfAConformance.PdfA2a, Title = "Grid" });

        using PdfDocument pdf = PdfDocument.Open(stream.ToArray());
        PdfStructureElement document = Assert.Single(pdf.Structure.Children);
        PdfStructureElement table = Assert.Single(Assert.Single(document.Elements).Elements);
        Assert.Equal("Table", table.Type.Name);
        Assert.Equal(60, table.Elements.Count());
        Assert.All(table.Elements.First().Elements, th => Assert.Equal("TH", th.Type.Name));
        Assert.Equal(3, table.Elements.Last().Elements.Count());
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public void ConditionalColourScalesShadeTheFill()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int r = 0; r < 3; r++)
        {
            sheet[r, 0].Value = r;
        }

        sheet.ConditionalFormats.Add("A1:A3", ConditionalFormatRule.ColorScale(CellColor.FromRgb(255, 255, 255), CellColor.FromRgb(0, 0, 255)));
        Raster<Rgba8> image = sheet.Paginate(Portable).Pages[0].Render(72);
        int x = (int)(0.7 * 72) + 3;

        Rgba8 top = image.GetPixel(x, (int)(0.75 * 72) + 2);
        Rgba8 bottom = image.GetPixel(x, (int)(0.75 * 72) + 32);
        Assert.Equal((255, 255, 255), (top.R, top.G, top.B));
        Assert.Equal((0, 0, 255), (bottom.R, bottom.G, bottom.B));
    }

    [Theory]
    [InlineData("&\"\"empty font")]
    [InlineData("&\"A font name longer than thirty-one characters,Bold\"text")]
    public void AHeaderFontACellCannotHavePrintsInTheNormalFont(string header)
    {
        // Found by fuzzing: a cell font's name is 1 to 31 characters, and a header code may name any.
        Worksheet sheet = Grid(2, 2);
        sheet.PageSetup.Header = header;
        using MemoryStream pdf = new();

        sheet.SaveAsPdf(pdf, new SheetPdfOptions { Print = Portable });

        Assert.Contains("text", PdfDocument.Open(pdf.ToArray()).Pages[0].ExtractText().Replace("empty font", "text", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AMergedCellAtThePageStartPrints()
    {
        // Measuring from a merged cell's first column to the page's first column once recursed for ever when they were the same.
        Worksheet sheet = Grid(60, 12);
        sheet.MergedCells.Add(RangeAddress.Parse("A2:C2"));
        sheet.MergedCells.Add(RangeAddress.Parse("J1:K3"));
        sheet["A2"].Style = CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromRgb(0, 0, 255)) };

        Assert.True(sheet.Paginate(Portable).Pages[0].Render(36).Width > 0);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2a, false)]
    [InlineData(PdfAConformance.PdfA2b, false)]
    [InlineData(PdfAConformance.None, true)]
    public void ConformingPrintsPassEgapsOwnChecks(PdfAConformance conformance, bool universal)
    {
        Worksheet sheet = Grid(60, 12);
        sheet.PageSetup.PrintTitleRows = RangeAddress.Parse("1:1");
        sheet.PageSetup.Header = "&CReport";
        sheet.MergedCells.Add(RangeAddress.Parse("A2:C2"));
        sheet["A2"].Style = CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromTheme(4, 0.4)) };
        using MemoryStream stream = new();

        sheet.SaveAsPdf(stream, new SheetPdfOptions { Print = Portable, Conformance = conformance, UniversalAccessibility = universal, Language = "en-US", Title = "Report" });

        PdfDocument pdf = PdfDocument.Open(stream.ToArray());
        PdfAReport report = universal ? PdfAConverter.CheckUniversalAccessibility(pdf) : PdfAConverter.Check(pdf, conformance);
        Assert.True(report.Succeeded, report.ToString());
    }

    [Fact]
    public void TheGuideSamplePrints()
    {
        // The sample of docs/articles/guides/spreadsheets.md, "Printing to PDF and images", with its files in a temporary folder.
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Workbook book = Workbook.Create();
            Worksheet sheet = book.Worksheets[0];
            for (int row = 0; row < 200; row++)
            {
                sheet[row, 0].Value = $"Item {row + 1}";
                sheet[row, 1].Value = row * 1.5;
            }

            sheet.PageSetup.PrintTitleRows = RangeAddress.Parse("1:1");
            sheet.PageSetup.RowBreaks.Add(100);
            sheet.PageSetup.Footer = "&CPage &P of &N";

            SheetPrintLayout layout = sheet.Paginate(Portable);
            sheet.SaveAsPdf(Path.Combine(folder, "items.pdf"), new SheetPdfOptions { Print = Portable });
            sheet.SaveAsPdf(Path.Combine(folder, "items-a.pdf"), new SheetPdfOptions { Print = Portable, Conformance = PdfAConformance.PdfA2a, Language = "en-US" });
            Raster<Rgba8> firstPage = layout.Pages[0].Render(150);

            // Letter paper: 47 rows, then 46 below the repeated title row, and row 101 starts a page.
            Assert.Equal(["A1:B47", "A48:B93", "A94:B100", "A101:B146", "A147:B192", "A193:B200"], layout.Pages.Select(p => p.Cells.ToString()));
            Assert.Equal(1275, firstPage.Width); // 8.5 inches at 150 dpi
            Assert.Equal(6, PdfDocument.Open(File.ReadAllBytes(Path.Combine(folder, "items-a.pdf"))).Pages.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static IEnumerable<Rgba8> Pixels(Raster<Rgba8> image)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                yield return image.GetPixel(x, y);
            }
        }
    }

    private static IEnumerable<Rgba8> Column(Raster<Rgba8> image, int left, int right, int top, int height)
    {
        for (int y = top; y < top + height; y++)
        {
            for (int x = left; x < right; x++)
            {
                yield return image.GetPixel(x, y);
            }
        }
    }
}
