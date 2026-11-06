using System.Text;
using System.Xml.Linq;

using Egap.Documents;

namespace Egap.Spreadsheets.Tests;

/// <summary>Loading and saving XLSX, XLSM, XLTX and XLTM packages, and what a round trip keeps.</summary>
public sealed class XlsxTests
{
    private static readonly XNamespace Ns = XlsxSeed.Main;

    [Fact]
    public void AWorkbookMadeInCodeComesBackAsItWas()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.Name = "Report";
        sheet["A1"].Value = "Name";
        sheet["B1"].Value = 3.5;
        sheet["C1"].Value = true;
        sheet["D1"].Value = CellError.NotAvailable;
        sheet["E1"].Formula = "B1*2";
        sheet["A2"].Value = "Tab\there, line\nbreak, _x0041_ and \u0001";
        sheet["A3"].Value = "  spaced  ";
        sheet["A1"].Style = sheet["A1"].Style with
        {
            Font = CellFont.Default with { Bold = true, Color = CellColor.FromRgb(200, 0, 0) },
            Fill = CellFill.Solid(CellColor.FromTheme(4, 0.4)),
            Borders = CellBorders.Box(CellBorder.Thin),
            Alignment = CellAlignment.Default with { Horizontal = CellHorizontalAlignment.Center, WrapText = true },
            Protection = CellProtection.Default with { Locked = false },
        };
        sheet["B1"].NumberFormat = "0.000";
        sheet["B2"].NumberFormat = "yyyy-mm-dd";
        sheet.Columns["A"].Width = 20;
        sheet.Columns[3].Hidden = true;
        sheet.Rows[1].Height = 28;
        sheet.Rows[4].OutlineLevel = 1;
        sheet.MergedCells.Add(RangeAddress.Parse("A5:C6"));
        sheet.View.FreezePanes(CellAddress.Parse("B2"));
        sheet.View.ZoomScale = 85;
        sheet.View.ShowGridLines = false;
        sheet.PageSetup.Orientation = PrintOrientation.Landscape;
        sheet.PageSetup.PaperSize = 9;
        sheet.PageSetup.FitToPage = true;
        sheet.PageSetup.FitToHeight = 0;
        sheet.PageSetup.Header = "&CReport";
        sheet.PageSetup.LeftMargin = 0.5;
        sheet.TabColor = CellColor.FromRgb(0, 128, 0);
        sheet.DefaultRowHeight = 18;
        Worksheet hidden = book.Worksheets.Add("Hidden");
        hidden.Visibility = SheetVisibility.VeryHidden;
        hidden["A1"].Value = "Name";
        book.Names.Add("Rate", "=0.25");
        book.Names.Add("Local", "Report!$A$1", sheet).Hidden = true;
        book.Date1904 = true;

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Worksheet back = loaded.Worksheets["Report"];

        Assert.Equal("Name", back["A1"].Value);
        Assert.Equal(3.5, back["B1"].Value);
        Assert.Equal(true, back["C1"].Value);
        Assert.Equal(CellError.NotAvailable, back["D1"].Value);
        Assert.Equal("B1*2", back["E1"].Formula);
        Assert.Equal(sheet["A2"].Value, back["A2"].Value);
        Assert.Equal("  spaced  ", back["A3"].Value);
        Assert.Equal(sheet["A1"].Style, back["A1"].Style);
        Assert.Equal("0.000", back["B1"].NumberFormat);
        Assert.Equal("yyyy-mm-dd", back["B2"].NumberFormat);
        Assert.Equal(20, back.Columns["A"].Width);
        Assert.True(back.Columns[3].Hidden);
        Assert.Equal(28, back.Rows[1].Height);
        Assert.Equal(1, back.Rows[4].OutlineLevel);
        Assert.Equal([RangeAddress.Parse("A5:C6")], back.MergedCells);
        Assert.Equal((1, 1), (back.View.FrozenRows, back.View.FrozenColumns));
        Assert.Equal(85, back.View.ZoomScale);
        Assert.False(back.View.ShowGridLines);
        Assert.Equal(PrintOrientation.Landscape, back.PageSetup.Orientation);
        Assert.Equal(9, back.PageSetup.PaperSize);
        Assert.True(back.PageSetup.FitToPage);
        Assert.Equal(0, back.PageSetup.FitToHeight);
        Assert.Equal("&CReport", back.PageSetup.Header);
        Assert.Equal(0.5, back.PageSetup.LeftMargin);
        Assert.Equal(CellColor.FromRgb(0, 128, 0), back.TabColor);
        Assert.Equal(18, back.DefaultRowHeight);
        Assert.Equal(SheetVisibility.VeryHidden, loaded.Worksheets["Hidden"].Visibility);
        Assert.Equal("0.25", loaded.Names["Rate"].Formula);
        Assert.True(loaded.Names.TryGet("Local", back, out DefinedName? local));
        Assert.True(local.Hidden);
        Assert.True(loaded.Date1904);
        Assert.Equal(WorkbookFormat.Xlsx, loaded.Format);

        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);
        XElement sst = XlsxSeed.Xml(parts, "xl/sharedStrings.xml").Root!;
        Assert.Equal("4", (string?)sst.Attribute("count"));
        Assert.Equal("3", (string?)sst.Attribute("uniqueCount"));
        Assert.Contains("_x005F_x0041_", Encoding.UTF8.GetString(parts["xl/sharedStrings.xml"]), StringComparison.Ordinal);
        Assert.Equal("1", (string?)XlsxSeed.Xml(parts, "xl/workbook.xml").Root!.Element(Ns + "calcPr")!.Attribute("fullCalcOnLoad"));
    }

    [Fact]
    public void AnExcelWorkbookLoadsWithWhatTheModelHolds()
    {
        Workbook book = XlsxSeed.LoadFeatures();

        Assert.Equal(["Data", "Hidden", "Table"], book.Worksheets.Select(s => s.Name));
        Worksheet data = book.Worksheets["Data"];
        Assert.Equal("Item", data["A1"].Value);
        Assert.Equal("Total", data["D1"].Value);
        Assert.Equal("When", data["E1"].Value);
        Assert.Equal(1.25, data["B2"].Value);
        Assert.Equal("B2*C2", data["D2"].Formula);
        Assert.Equal(12.5, data["D2"].Value);
        Assert.Equal("B3*C3", data["D3"].Formula);
        Assert.True(data["D3"].HasFormula);
        Assert.Equal(22.5, data["D3"].Value);
        Assert.Equal(45000.5, data["E3"].Value);
        Assert.Equal(CellError.DivisionByZero, data["F3"].Value);
        Assert.Equal("Text Apple", data["F4"].Value);
        Assert.Equal("Line one\nLine two", data["F5"].Value.Text.ReplaceLineEndings("\n"));
        Assert.Equal("Bold and plain", data["A9"].Value);
        Assert.Equal("Carriage\rreturn and _x0041_", data["A10"].Value);
        Assert.True(data["A1"].Style.Font.Bold);
        Assert.Equal(CellFillPattern.Solid, data["A1"].Style.Fill.Pattern);
        Assert.Equal(CellBorderStyle.Thin, data["A1"].Style.Borders.Bottom.Style);
        Assert.Equal("#,##0.00\\ \"kr\"", data["B2"].NumberFormat);
        Assert.Equal("mm-dd-yy", data["E2"].NumberFormat);
        Assert.True(data["F5"].Style.Alignment.WrapText);
        Assert.Equal(18, data.Columns["A"].Width);
        Assert.True(data.Columns["F"].Hidden);
        Assert.Equal(30, data.Rows[3].Height);
        Assert.Null(data.Rows[4].Height);
        Assert.Equal(2, data.Rows[2].OutlineLevel);
        Assert.Equal([RangeAddress.Parse("A9:C9")], data.MergedCells);
        Assert.Equal(1, data.View.FrozenRows);
        Assert.Equal(120, data.View.ZoomScale);
        Assert.True(data.View.Selected);
        Assert.Equal(CellColor.FromArgb(0xFF00B050), data.TabColor);
        Assert.True(data.PageSetup.FitToPage);
        Assert.Equal(PrintOrientation.Landscape, data.PageSetup.Orientation);
        Assert.True(data.PageSetup.CenterHorizontally);
        Assert.Equal("&RPage &P of &N", data.PageSetup.Footer);
        Assert.Equal(SheetVisibility.Hidden, book.Worksheets["Hidden"].Visibility);
        Assert.Equal("Bo", book.Worksheets["Table"]["A3"].Value);
        Assert.Equal(9, book.Worksheets["Table"]["B3"].Value);
        Assert.Equal("Data!$A$1:$E$7", book.Names.First(n => n.Name == "_xlnm.Print_Area").Formula);
        Assert.Same(data, book.Names.First(n => n.Name == "LocalRange").Sheet);
        Assert.Equal("Rate", book.Names["TaxRate"].Comment);
        Assert.Equal(5, book.Names.Count);
    }

    [Fact]
    public void CalculationSettingsAreReadAndWritten()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Assert.Equal(CalculationMode.Automatic, book.Calculation.Mode);
        Assert.False(book.Calculation.Iterate);
        Assert.Equal(100, book.Calculation.MaxIterations);

        // Defaults leave calcPr as the file had it.
        (_, MemoryStream unchanged) = XlsxSeed.RoundTrip(book);
        Assert.Equal(["calcId"], XlsxSeed.Xml(XlsxSeed.Parts(unchanged), "xl/workbook.xml").Root!.Element(Ns + "calcPr")!.Attributes().Select(a => a.Name.LocalName));

        book.Calculation.Mode = CalculationMode.Manual;
        book.Calculation.Iterate = true;
        book.Calculation.MaxIterations = 250;
        book.Calculation.MaxChange = 0.5;
        book.Calculation.Concurrent = false;
        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        XElement calcPr = XlsxSeed.Xml(XlsxSeed.Parts(package), "xl/workbook.xml").Root!.Element(Ns + "calcPr")!;

        Assert.Equal("manual", (string?)calcPr.Attribute("calcMode"));
        Assert.Equal("250", (string?)calcPr.Attribute("iterateCount"));
        Assert.Null(calcPr.Attribute("fullPrecision"));
        Assert.Equal(CalculationMode.Manual, loaded.Calculation.Mode);
        Assert.True(loaded.Calculation.Iterate);
        Assert.Equal(250, loaded.Calculation.MaxIterations);
        Assert.Equal(0.5, loaded.Calculation.MaxChange);
        Assert.False(loaded.Calculation.Concurrent);
        Assert.True(loaded.Calculation.FullPrecision);

        // A new workbook writes calcPr only when a setting needs it.
        Workbook created = Workbook.Create();
        created.Calculation.Mode = CalculationMode.AutomaticExceptTables;
        (Workbook again, _) = XlsxSeed.RoundTrip(created);
        Assert.Equal(CalculationMode.AutomaticExceptTables, again.Calculation.Mode);

        Assert.Throws<ArgumentOutOfRangeException>(() => book.Calculation.MaxIterations = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Calculation.MaxIterations = 32_768);
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Calculation.MaxChange = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Calculation.MaxChange = double.NaN);
    }

    [Fact]
    public void AnUnchangedWorkbookSavesWithEveryPartAndElementKept()
    {
        Workbook book = XlsxSeed.LoadFeatures();

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);

        foreach (string kept in (string[])["docProps/core.xml", "docProps/app.xml", "xl/theme/theme1.xml", "xl/comments1.xml", "xl/drawings/drawing1.xml",
            "xl/drawings/vmlDrawing1.vml", "xl/printerSettings/printerSettings1.bin", "xl/tables/table1.xml", "xl/chartsheets/sheet1.xml"])
        {
            Assert.True(parts.ContainsKey(kept), kept);
        }

        Assert.False(parts.ContainsKey("xl/calcChain.xml"));
        XElement workbook = XlsxSeed.Xml(parts, "xl/workbook.xml").Root!;
        Assert.Null(workbook.Element(Ns + "calcPr")!.Attribute("fullCalcOnLoad"));
        Assert.Equal(["Data", "Hidden", "Chart1", "Table"], workbook.Element(Ns + "sheets")!.Elements().Select(s => (string?)s.Attribute("name")));
        Assert.Equal("2", (string?)workbook.Elements(Ns + "definedNames").Elements().Single(n => (string?)n.Attribute("name") == "ChartLocal").Attribute("localSheetId"));
        Assert.NotNull(workbook.Element(Ns + "extLst"));

        XElement sheet = XlsxSeed.Xml(parts, "xl/worksheets/sheet1.xml").Root!;
        Assert.Equal(
            ["sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetProtection", "mergeCells", "conditionalFormatting",
                "dataValidations", "hyperlinks", "printOptions", "pageMargins", "pageSetup", "headerFooter", "drawing", "legacyDrawing", "extLst"],
            sheet.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("A1:F12", (string?)sheet.Element(Ns + "dimension")!.Attribute("ref"));
        Assert.NotNull(sheet.Element(Ns + "sheetPr")!.Element(Ns + "outlinePr"));
        Assert.Equal("rId2", (string?)sheet.Element(Ns + "pageSetup")!.Attribute(XlsxSeed.R + "id"));
        XElement row1 = sheet.Element(Ns + "sheetData")!.Elements().First();
        Assert.Equal("0.25", (string?)row1.Attribute(XNamespace.Get("http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac") + "dyDescent"));
        XElement d4 = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "D4");
        Assert.Equal("1", (string?)d4.Attribute("cm"));
        Assert.Equal("array", (string?)d4.Element(Ns + "f")!.Attribute("t"));
        XElement row5 = sheet.Element(Ns + "sheetData")!.Elements().Single(r => (string?)r.Attribute("r") == "5");
        Assert.Equal("30", (string?)row5.Attribute("ht"));
        Assert.Null(row5.Attribute("customHeight"));
        Assert.Equal("shared", (string?)sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "D3").Element(Ns + "f")!.Attribute("t"));

        XElement styles = XlsxSeed.Xml(parts, "xl/styles.xml").Root!;
        Assert.Equal("degree", styles.Descendants(Ns + "gradientFill").Single().Attributes().First().Name.LocalName);
        Assert.Equal(2, styles.Element(Ns + "cellStyles")!.Elements().Count());
        Assert.Equal("1", (string?)styles.Element(Ns + "fonts")!.Attribute(XNamespace.Get("http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac") + "knownFonts"));
        Assert.Contains(XlsxSeed.Xml(parts, "xl/sharedStrings.xml").Root!.Elements(), si => si.Element(Ns + "r") is not null);
        Assert.DoesNotContain("Unused", Encoding.UTF8.GetString(parts["xl/sharedStrings.xml"]), StringComparison.Ordinal);

        Assert.Equal(book.Worksheets["Data"]["A9"].Value, loaded.Worksheets["Data"]["A9"].Value);
        Assert.Equal(book.Worksheets["Data"]["B2"].Style, loaded.Worksheets["Data"]["B2"].Style);
        string rels = Encoding.UTF8.GetString(parts["xl/worksheets/_rels/sheet1.xml.rels"]);
        Assert.Contains("https://example.com/", rels, StringComparison.Ordinal);
        Assert.Contains("comments1.xml", rels, StringComparison.Ordinal);
    }

    [Fact]
    public void EditsAreSavedAndAskExcelToCalculate()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet data = book.Worksheets["Data"];
        data["C2"].Value = 20;
        data["G1"].Value = "New";
        data["G2"].Style = data["A1"].Style with { Font = data["A1"].Style.Font with { Italic = true } };
        data.Rows[3].Height = null;
        data.Columns["A"].Width = 25;
        data.View.FrozenRows = 0;
        data.PageSetup.Orientation = PrintOrientation.Portrait;
        data.Name = "Renamed";
        Worksheet added = book.Worksheets.Insert(0, "First");
        added["A1"].Formula = "Renamed!C2";

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);
        Worksheet back = loaded.Worksheets["Renamed"];

        Assert.Equal(["First", "Renamed", "Hidden", "Table"], loaded.Worksheets.Select(s => s.Name));
        Assert.Equal(20, back["C2"].Value);
        Assert.Equal("New", back["G1"].Value);
        Assert.True(back["G2"].Style.Font.Italic);
        Assert.True(back["G2"].Style.Font.Bold);
        Assert.Null(back.Rows[3].Height);
        Assert.Equal(25, back.Columns["A"].Width);
        Assert.Equal(0, back.View.FrozenRows);
        Assert.Equal(PrintOrientation.Portrait, back.PageSetup.Orientation);
        Assert.Equal("Renamed!C2", loaded.Worksheets["First"]["A1"].Formula);
        XElement workbook = XlsxSeed.Xml(parts, "xl/workbook.xml").Root!;
        Assert.Equal("1", (string?)workbook.Element(Ns + "calcPr")!.Attribute("fullCalcOnLoad"));
        Assert.Equal(["First", "Renamed", "Hidden", "Chart1", "Table"], workbook.Element(Ns + "sheets")!.Elements().Select(s => (string?)s.Attribute("name")));
        Assert.Equal("1", (string?)workbook.Elements(Ns + "definedNames").Elements().First(n => (string?)n.Attribute("name") == "LocalRange").Attribute("localSheetId"));
        Assert.Equal("3", (string?)workbook.Elements(Ns + "definedNames").Elements().First(n => (string?)n.Attribute("name") == "ChartLocal").Attribute("localSheetId"));
        Assert.True(parts.ContainsKey("xl/worksheets/sheet4.xml"));
        XElement view = XlsxSeed.Xml(parts, "xl/worksheets/sheet1.xml").Root!.Descendants(Ns + "sheetView").Single();
        Assert.Null(view.Element(Ns + "pane"));
    }

    [Fact]
    public void ARemovedSheetTakesItsPartsAndLocalNamesWithIt()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        book.Worksheets.Remove(book.Worksheets["Data"]);

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);

        Assert.False(parts.ContainsKey("xl/worksheets/sheet1.xml"));
        Assert.False(parts.ContainsKey("xl/comments1.xml"));
        Assert.False(parts.ContainsKey("xl/drawings/drawing1.xml"));
        Assert.True(parts.ContainsKey("xl/tables/table1.xml"));
        Assert.DoesNotContain(loaded.Names, n => n.Name == "LocalRange");
        Assert.Contains(loaded.Names, n => n.Name == "ChartLocal");
        Assert.Equal(["Hidden", "Table"], loaded.Worksheets.Select(s => s.Name));
    }

    [Fact]
    public void CellsSharingAFormulaWhoseFirstCellChangedKeepTheirFormulas()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        book.Worksheets["Data"]["D2"].Value = 1;

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);

        Cell d3 = loaded.Worksheets["Data"]["D3"];
        Assert.Equal("B3*C3", d3.Formula);
        Assert.Equal(22.5, d3.Value);
        Assert.DoesNotContain("si=\"0\"", Encoding.UTF8.GetString(XlsxSeed.Parts(package)["xl/worksheets/sheet1.xml"]), StringComparison.Ordinal);
    }

    [Fact]
    public void MacrosStayInMacroFilesAndLeaveOtherwise()
    {
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["[Content_Types].xml"] = seed["[Content_Types].xml"]
            .Replace("spreadsheetml.sheet.main+xml", "PLACEHOLDER", StringComparison.Ordinal)
            .Replace("application/vnd.openxmlformats-officedocument.PLACEHOLDER", "application/vnd.ms-excel.sheet.macroEnabled.main+xml", StringComparison.Ordinal)
            .Replace("<Override PartName=\"/xl/workbook.xml\"", "<Override PartName=\"/xl/vbaProject.bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/><Override PartName=\"/xl/workbook.xml\"", StringComparison.Ordinal);
        seed["xl/_rels/workbook.xml.rels"] = seed["xl/_rels/workbook.xml.rels"].Replace("</Relationships>",
            "<Relationship Id=\"rId9\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"vbaProject.bin\"/></Relationships>", StringComparison.Ordinal);
        Workbook book = Workbook.Load(XlsxSeed.Package(seed, new() { ["xl/vbaProject.bin"] = [1, 2, 3] }));
        Assert.Equal(WorkbookFormat.Xlsm, book.Format);

        (Workbook macro, MemoryStream withMacros) = XlsxSeed.RoundTrip(book);
        Assert.Equal(WorkbookFormat.Xlsm, macro.Format);
        Assert.Equal([1, 2, 3], XlsxSeed.Parts(withMacros)["xl/vbaProject.bin"]);

        (Workbook plain, MemoryStream withoutMacros) = XlsxSeed.RoundTrip(book, new WorkbookSaveOptions { Format = WorkbookFormat.Xlsx });
        Assert.Equal(WorkbookFormat.Xlsx, plain.Format);
        Assert.False(XlsxSeed.Parts(withoutMacros).ContainsKey("xl/vbaProject.bin"));
    }

    [Theory]
    [InlineData(".xlsx", WorkbookFormat.Xlsx, "spreadsheetml.sheet.main+xml")]
    [InlineData(".XLSM", WorkbookFormat.Xlsm, "sheet.macroEnabled.main+xml")]
    [InlineData(".xltx", WorkbookFormat.Xltx, "spreadsheetml.template.main+xml")]
    [InlineData(".xltm", WorkbookFormat.Xltm, "template.macroEnabled.main+xml")]
    public void TheExtensionNamesTheKindOfFile(string extension, WorkbookFormat format, string contentType)
    {
        string path = Path.Combine(Path.GetTempPath(), "egap-sheets-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            Workbook book = Workbook.Create();
            book.Worksheets[0]["A1"].Value = 1;
            book.Save(path);

            Workbook loaded = Workbook.Load(path);
            Assert.Equal(format, loaded.Format);
            Assert.Equal(1, loaded.Worksheets[0]["A1"].Value);
            using FileStream stream = File.OpenRead(path);
            Assert.Contains(contentType, Encoding.UTF8.GetString(XlsxSeed.Parts(stream)["[Content_Types].xml"]), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SavingNeedsASheetAndAKnownExtension()
    {
        Assert.Throws<InvalidOperationException>(() => Workbook.CreateEmpty().Save(new MemoryStream()));
        Assert.Throws<ArgumentException>(() => Workbook.Create().Save(Path.Combine(Path.GetTempPath(), "book.ods")));
    }

    [Fact]
    public void FilesThatAreNotWorkbooksAreRefused()
    {
        Assert.Throws<DocumentException>(() => Workbook.Load(new MemoryStream([1, 2, 3, 4])));
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["[Content_Types].xml"] = seed["[Content_Types].xml"].Replace("spreadsheetml.sheet.main+xml", "wordprocessingml.document.main+xml", StringComparison.Ordinal);
        Assert.Throws<DocumentException>(() => Workbook.Load(XlsxSeed.Package(seed)));
        seed = XlsxSeed.Features();
        seed["xl/worksheets/sheet2.xml"] = "<worksheet><sheetData>";
        Assert.Throws<DocumentException>(() => Workbook.Load(XlsxSeed.Package(seed)));
        Assert.Throws<DocumentLimitException>(() => Workbook.Load(XlsxSeed.Package(XlsxSeed.Features()), new WorkbookLoadOptions { Limits = new DocumentLimits { MaxEntries = 5 } }));
    }

    [Fact]
    public void AStrictWorkbookLoads()
    {
        const string strict = "http://purl.oclc.org/ooxml/spreadsheetml/main";
        const string strictR = "http://purl.oclc.org/ooxml/officeDocument/relationships";
        Dictionary<string, string> seed = XlsxSeed.Features();
        foreach (string name in seed.Keys.ToList())
        {
            seed[name] = seed[name].Replace(XlsxSeed.Main, strict, StringComparison.Ordinal)
                .Replace("http://schemas.openxmlformats.org/officeDocument/2006/relationships\"", strictR + "\"", StringComparison.Ordinal);
        }

        Workbook book = Workbook.Load(XlsxSeed.Package(seed));

        Assert.Equal("Item", book.Worksheets["Data"]["A1"].Value);
        Assert.True(book.Worksheets["Data"]["A1"].Style.Font.Bold);
        Assert.Equal(1, book.Worksheets["Data"].View.FrozenRows);
        (Workbook loaded, _) = XlsxSeed.RoundTrip(book);
        Assert.Equal("Bold and plain", loaded.Worksheets["Data"]["A9"].Value);
    }

    [Fact]
    public void StylesThatHoldMoreThanTheModelAreNotSharedWithNewOnes()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet data = book.Worksheets["Data"];
        CellStyle gradient = data["B12"].Style;
        Assert.Equal(CellFill.None, gradient.Fill);

        data["H1"].Style = gradient;

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        XElement sheet = XlsxSeed.Xml(XlsxSeed.Parts(package), "xl/worksheets/sheet1.xml").Root!;
        string? b12 = (string?)sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "B12").Attribute("s");
        Assert.Equal("7", b12);
        Assert.DoesNotContain(sheet.Descendants(Ns + "c"), c => (string?)c.Attribute("r") == "H1");
        Assert.Equal(CellStyle.Default with { Font = CellFont.Default with { Scheme = CellFontScheme.Minor } }, loaded.Worksheets["Data"]["H1"].Style);
    }

    [Fact]
    public void ADataTableFormulaKeepsItsAttributes()
    {
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["xl/worksheets/sheet2.xml"] = seed["xl/worksheets/sheet2.xml"].Replace(
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>8</v></c></row>",
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>8</v></c></row><row r=\"2\"><c r=\"B2\"><f t=\"dataTable\" ref=\"B2:B3\" dt2D=\"0\" dtr=\"0\" r1=\"A1\"/><v>4</v></c></row>",
            StringComparison.Ordinal);
        Workbook book = Workbook.Load(XlsxSeed.Package(seed));
        Assert.True(book.Worksheets["Hidden"]["B2"].HasFormula);

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);

        XElement f = XlsxSeed.Xml(XlsxSeed.Parts(package), "xl/worksheets/sheet2.xml").Root!.Descendants(Ns + "f").Single();
        Assert.Equal("dataTable", (string?)f.Attribute("t"));
        Assert.Equal("A1", (string?)f.Attribute("r1"));
        Assert.Equal(4, loaded.Worksheets["Hidden"]["B2"].Value);
    }

    [Fact]
    public void AFooterAddedToAHeaderComesAfterIt()
    {
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["xl/worksheets/sheet2.xml"] = seed["xl/worksheets/sheet2.xml"].Replace(
            "</worksheet>", "<headerFooter><oddHeader>Top</oddHeader><evenHeader>Even</evenHeader></headerFooter></worksheet>", StringComparison.Ordinal);
        Workbook book = Workbook.Load(XlsxSeed.Package(seed));
        book.Worksheets["Hidden"].PageSetup.Footer = "Bottom";

        (_, MemoryStream package) = XlsxSeed.RoundTrip(book);

        XElement headerFooter = XlsxSeed.Xml(XlsxSeed.Parts(package), "xl/worksheets/sheet2.xml").Root!.Element(Ns + "headerFooter")!;
        Assert.Equal(["oddHeader", "oddFooter", "evenHeader"], headerFooter.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void AStylePartWithoutTablesLoadsWithTheDefaultStyle()
    {
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["xl/styles.xml"] = $"<styleSheet xmlns=\"{XlsxSeed.Main}\"/>";

        Workbook book = Workbook.Load(XlsxSeed.Package(seed));

        Assert.Equal(CellStyle.Default, book.Worksheets["Data"]["A1"].Style);
        (Workbook loaded, _) = XlsxSeed.RoundTrip(book);
        Assert.Equal("Item", loaded.Worksheets["Data"]["A1"].Value);
    }

    [Fact]
    public void DatesUseTheWorkbooksDateSystem()
    {
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["xl/workbook.xml"] = seed["xl/workbook.xml"].Replace("<workbookPr ", "<workbookPr date1904=\"1\" ", StringComparison.Ordinal);

        Workbook book = Workbook.Load(XlsxSeed.Package(seed));

        Assert.True(book.Date1904);
        Assert.Equal(45000.5 - 1462, book.Worksheets["Data"]["E3"].Value.Number, 6);
    }
}
