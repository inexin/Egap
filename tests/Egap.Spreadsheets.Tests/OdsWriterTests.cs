using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Odf;
using Egap.Spreadsheets.Ods;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Writing OpenDocument spreadsheets: OpenFormula translation, values, styles and number formats, rows and columns, merges,
/// names, what-if tables, linked files and encryption, each read back by Egap's ODS reader.
/// </summary>
public sealed class OdsWriterTests
{
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

    [Theory]
    [InlineData("SUM(A1:B2)", "of:=SUM([.A1:.B2])")]
    [InlineData("A1+$B$2", "of:=[.A1]+[.$B$2]")]
    [InlineData("Sheet2!A1*2", "of:=[$Sheet2.A1]*2")]
    [InlineData("'My sheet'!A1:C3", "of:=[$'My sheet'.A1:.C3]")]
    [InlineData("SUM('It''s'!A1)", "of:=SUM([$'It''s'.A1])")]
    [InlineData("SUM(Sheet1:Sheet3!A1:B2)", "of:=SUM([$Sheet1.A1:$Sheet3.B2])")]
    [InlineData("IF(A1>0,\"yes;no\",\"no\")", "of:=IF([.A1]>0;\"yes;no\";\"no\")")]
    [InlineData("SUM({1,2;3,4})", "of:=SUM({1;2|3;4})")]
    [InlineData("AND(TRUE,A1)", "of:=AND(TRUE();[.A1])")]
    [InlineData("_xlfn.IFS(A1>1,1,TRUE,2)", "of:=COM.MICROSOFT.IFS([.A1]>1;1;TRUE();2)")]
    [InlineData("NORMSDIST(1)", "of:=LEGACY.NORMSDIST(1)")]
    [InlineData("CEILING(A1,2)", "of:=COM.MICROSOFT.CEILING([.A1];2)")]
    [InlineData("_xlfn.XOR(A1,B1)", "of:=XOR([.A1];[.B1])")]
    [InlineData("_xlfn.FORMULATEXT(A1)", "of:=FORMULA([.A1])")]
    [InlineData("_xlfn.F.DIST(A1,2,3,TRUE)", "of:=FDIST([.A1];2;3;TRUE())")]
    [InlineData("FDIST(A1,2,3)", "of:=LEGACY.FDIST([.A1];2;3)")]
    [InlineData("_xlfn.SKEW.P(A1:A3)", "of:=SKEWP([.A1:.A3])")]
    [InlineData("NETWORKDAYS.INTL(A1,B1)", "of:=COM.MICROSOFT.NETWORKDAYS.INTL([.A1];[.B1])")]
    [InlineData("_xlfn.LAMBDA(x,x+1)", "of:=_xlfn.LAMBDA(x;x+1)")]
    [InlineData("SUM((A1:A3,C1:C3))", "of:=SUM(([.A1:.A3]~[.C1:.C3]))")]
    [InlineData("SUM(A1:C3 B2:D4)", "of:=SUM([.A1:.C3]![.B2:.D4])")]
    [InlineData("SUM(A:A)", "of:=SUM([.A:.A])")]
    [InlineData("SUM($2:3)", "of:=SUM([.$2:.3])")]
    [InlineData("#N/A", "of:=#N/A")]
    [InlineData("A1%+-B1^2&\"x\"", "of:=[.A1]%+-[.B1]^2&\"x\"")]
    [InlineData("Sheet2!Rate*2", "of:=Sheet2.Rate*2")]
    [InlineData("Rate*2", "of:=Rate*2")]
    [InlineData("SUM( A1 , B1 )", "of:=SUM( [.A1] ; [.B1] )")]
    public void TranslatesToOpenFormula(string excel, string openFormula)
    {
        Assert.Equal(openFormula, OpenFormulaWriter.Translate(excel, null, default, _ => null));
    }

    [Theory]
    [InlineData("SUM(A1:B2)")]
    [InlineData("Sheet2!A1*2")]
    [InlineData("'My sheet'!A1:C3")]
    [InlineData("SUM(Sheet1:Sheet3!A1:B2)")]
    [InlineData("IF(A1>0,\"yes;no\",\"no\")")]
    [InlineData("SUM({1,2;3,4})")]
    [InlineData("_xlfn.IFS(A1>1,1,TRUE,2)")]
    [InlineData("NORMSDIST(1)")]
    [InlineData("_xlfn.F.DIST(A1,2,3,TRUE)")]
    [InlineData("FDIST(A1,2,3)")]
    [InlineData("_xlfn.SKEW.P(A1:A3)")]
    [InlineData("SUM((A1:A3,C1:C3))")]
    [InlineData("SUM(A1:C3 B2:D4)")]
    [InlineData("SUM(A:A)+SUM(2:3)")]
    [InlineData("VLOOKUP(A1,Sheet2!$A$1:$C$10,3,FALSE)")]
    public void ReadsBackWhatItWrites(string excel)
    {
        string written = OpenFormulaWriter.Translate(excel, null, default, _ => null)!;
        Assert.Equal(excel, OpenFormula.ToExcel(written, out bool exact, new HashSet<string> { "Sheet1", "Sheet2", "Sheet3", "My sheet" }));
        Assert.True(exact);
    }

    [Fact]
    public void WritesReferencesToOtherFilesWithTheirFile()
    {
        Assert.Equal("of:=['file:///C:/data/rates.xlsx'#$Rates.A1:.B2]*2",
            OpenFormulaWriter.Translate("[1]Rates!A1:B2*2", null, default, b => b == "1" ? "file:///C:/data/rates.xlsx" : null));
        Assert.Equal("of:=['rates.xlsx'#$$'Rate']",
            OpenFormulaWriter.Translate("[1]!Rate", null, default, b => b == "1" ? "rates.xlsx" : null));
        Assert.Equal("of:=#REF!", OpenFormulaWriter.Translate("[2]Rates!A1", null, default, _ => null));
    }

    [Theory]
    [InlineData(@"C:\data\rates.xlsx", "file:///C:/data/rates.xlsx")]
    [InlineData(@"\\server\share\rates.xlsx", "file://server/share/rates.xlsx")]
    [InlineData("rates.xlsx", "rates.xlsx")]
    [InlineData(@"..\rates.xlsx", "../rates.xlsx")]
    [InlineData("file:///C:/rates.xlsx", "file:///C:/rates.xlsx")]
    public void WritesLinkTargetsAsUrls(string target, string url) => Assert.Equal(url, OdsWriter.FileUrl(target));

    [Fact]
    public void RoundTripsValuesFormulasAndSheets()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.Name = "Report";
        sheet["A1"].Value = "Name";
        sheet["B1"].Value = 3.5;
        sheet["C1"].Value = true;
        sheet["D1"].Value = CellError.NotAvailable;
        sheet["E1"].Formula = "B1*2";
        sheet["F1"].Formula = "IF(C1,\"yes\",\"no\")";
        sheet["G1"].Formula = "C1=TRUE";
        sheet["H1"].Formula = "1/0";
        sheet["I1"].Formula = "FALSE()";
        sheet["A2"].Value = "Tab\there, line\nbreak";
        sheet["A3"].Value = "  spaced   out  ";
        sheet["B2"].Value = 45000.75;
        sheet["B2"].NumberFormat = "yyyy-mm-dd hh:mm";
        sheet["B3"].Value = 1.5;
        sheet["B3"].NumberFormat = "[h]:mm:ss";
        sheet["B4"].Value = 0.25;
        sheet["B4"].NumberFormat = "0.00%";
        sheet["B5"].Value = -12345.678;
        sheet["B5"].NumberFormat = "#,##0.00;[Red]-#,##0.00";
        sheet["C2"].Formula = "SUM(Data!A1:A3)";
        sheet.TabColor = CellColor.FromRgb(0, 128, 0);
        Worksheet data = book.Worksheets.Add("Data");
        data["A1"].Value = 1;
        data["A2"].Value = 2;
        data["A3"].Value = 3;
        data.Visibility = SheetVisibility.Hidden;
        book.CalculateFull();

        Workbook back = RoundTrip(book, WorkbookFormat.Ods);
        Worksheet report = back.Worksheets["Report"];
        Assert.Equal(WorkbookFormat.Ods, back.Format);
        Assert.Equal("Name", report["A1"].Value);
        Assert.Equal(3.5, report["B1"].Value);
        Assert.Equal(true, report["C1"].Value);
        Assert.Equal(CellError.NotAvailable, report["D1"].Value);
        Assert.Equal("B1*2", report["E1"].Formula);
        Assert.Equal(7.0, report["E1"].Value);
        Assert.Equal("yes", report["F1"].Value);
        Assert.Equal(true, report["G1"].Value);
        Assert.Equal(CellError.DivisionByZero, report["H1"].Value);
        Assert.Equal("FALSE()", report["I1"].Formula);
        Assert.Equal(false, report["I1"].Value);
        Assert.Equal(sheet["A2"].Value, report["A2"].Value);
        Assert.Equal("  spaced   out  ", report["A3"].Value);
        Assert.Equal(45000.75, report["B2"].Value.Number, 9);
        Assert.Equal("yyyy-mm-dd hh:mm", report["B2"].NumberFormat);
        Assert.Equal(1.5, report["B3"].Value.Number, 9);
        Assert.Equal("[h]:mm:ss", report["B3"].NumberFormat);
        Assert.Equal(0.25, report["B4"].Value);
        Assert.Equal("0.00%", report["B4"].NumberFormat);
        Assert.Equal(-12345.678, report["B5"].Value);
        Assert.Equal("#,##0.00;[Red]-#,##0.00", report["B5"].NumberFormat);
        Assert.Equal("SUM(Data!A1:A3)", report["C2"].Formula);
        Assert.Equal(6.0, report["C2"].Value);
        Assert.Equal(CellColor.FromRgb(0, 128, 0), report.TabColor);
        Assert.Equal(SheetVisibility.Hidden, back.Worksheets["Data"].Visibility);
    }

    [Theory]
    [InlineData("0.000")]
    [InlineData("#,##0")]
    [InlineData("0%")]
    [InlineData("0.00E+00")]
    [InlineData("yyyy-mm-dd")]
    [InlineData("d mmmm yyyy")]
    [InlineData("dddd")]
    [InlineData("h:mm AM/PM")]
    [InlineData("[h]:mm:ss")]
    [InlineData("mm:ss.0")]
    [InlineData("# ?/?")]
    [InlineData("#,##0;-#,##0")]
    [InlineData("0;[Red]-0;\"zero\"")]
    [InlineData("@")]
    [InlineData("\"Total: \"0.00")]
    [InlineData("0.0,,")]
    public void RoundTripsNumberFormats(string code)
    {
        Workbook book = Workbook.Create();
        book.Worksheets[0]["A1"].Value = 1234.5;
        book.Worksheets[0]["A1"].NumberFormat = code;
        Workbook back = RoundTrip(book, WorkbookFormat.Fods);
        Assert.Equal(code, back.Worksheets[0]["A1"].NumberFormat);
    }

    [Fact]
    public void RoundTripsStyles()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "x";
        CellStyle style = sheet["A1"].Style with
        {
            Font = CellFont.Default with { Name = "Arial", Size = 12, Bold = true, Italic = true, Underline = CellUnderline.Double, Strikethrough = true, Color = CellColor.FromRgb(200, 0, 0) },
            Fill = CellFill.Solid(CellColor.FromRgb(0xFF, 0xEE, 0xDD)),
            Borders = new CellBorders { Left = CellBorder.Thin, Top = new CellBorder(CellBorderStyle.Medium, CellColor.FromRgb(0, 0, 255)), Bottom = new CellBorder(CellBorderStyle.Double) },
            Alignment = CellAlignment.Default with { Horizontal = CellHorizontalAlignment.Center, Vertical = CellVerticalAlignment.Top, WrapText = true, TextRotation = 45 },
            Protection = CellProtection.Default with { Locked = false, Hidden = true },
        };
        sheet["A1"].Style = style;
        Workbook back = RoundTrip(book, WorkbookFormat.Ods);
        CellStyle read = back.Worksheets[0]["A1"].Style;
        Assert.Equal(style.Font with { Family = read.Font.Family, Scheme = read.Font.Scheme, CharacterSet = read.Font.CharacterSet }, read.Font);
        Assert.Equal(CellColor.FromRgb(0xFF, 0xEE, 0xDD), read.Fill.Foreground);
        Assert.Equal(CellBorderStyle.Thin, read.Borders.Left.Style);
        Assert.Equal(new CellBorder(CellBorderStyle.Medium, CellColor.FromRgb(0, 0, 255)), read.Borders.Top);
        Assert.Equal(CellBorderStyle.Double, read.Borders.Bottom.Style);
        Assert.Equal(style.Alignment, read.Alignment);
        Assert.Equal(style.Protection, read.Protection);
    }

    [Fact]
    public void RoundTripsRowsColumnsMergesAndArrays()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "merged";
        sheet["B2"].Value = "covered";
        sheet["E1"].Value = 1;
        sheet["E2"].Value = 2;
        sheet.Range("F1:F2").SetArrayFormula("E1:E2*2");
        sheet.MergedCells.Add(RangeAddress.Parse("A1:C3"));
        sheet.Columns["A"].Width = 20;
        sheet.Columns[3].Hidden = true;
        sheet.Columns[4].OutlineLevel = 1;
        sheet.Rows[1].Height = 28;
        sheet.Rows[4].OutlineLevel = 2;
        sheet.Rows[5].Hidden = true;
        sheet["A6"].Value = "after";
        book.CalculateFull();

        Worksheet back = RoundTrip(book, WorkbookFormat.Ods).Worksheets[0];
        Assert.Equal([RangeAddress.Parse("A1:C3")], back.MergedCells);
        Assert.Equal("covered", back["B2"].Value);
        Assert.Equal(20, back.Columns["A"].Width!.Value, 2);
        Assert.True(back.Columns[3].Hidden);
        Assert.Equal(1, back.Columns[4].OutlineLevel);
        Assert.Equal(28, back.Rows[1].Height);
        Assert.Equal(2, back.Rows[4].OutlineLevel);
        Assert.True(back.Rows[5].Hidden);
        Assert.Equal("after", back["A6"].Value);
        Assert.Equal("F1:F2", back["F1"].ArrayRange?.Address.ToString());
        Assert.Equal(4.0, back["F2"].Value);
    }

    [Fact]
    public void RoundTripsNamesAndPrintAreas()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.Name = "My sheet";
        sheet["A1"].Value = 2;
        book.Names.Add("Rate", "=0.25");
        book.Names.Add("Cells", "'My sheet'!$A$1:$B$2");
        book.Names.Add("Local", "'My sheet'!$A$1", sheet);
        book.Names.Add("_xlnm.Print_Area", "'My sheet'!$A$1:$C$9", sheet);
        sheet["C1"].Formula = "Rate*Local";

        Workbook back = RoundTrip(book, WorkbookFormat.Ods);
        Assert.Equal("0.25", back.Names["Rate"].Formula);
        Assert.Equal("'My sheet'!$A$1:$B$2", back.Names["Cells"].Formula);
        Assert.True(back.Names.TryGet("Local", back.Worksheets[0], out DefinedName? local));
        Assert.Equal("'My sheet'!$A$1", local.Formula);
        Assert.True(back.Names.TryGet("_xlnm.Print_Area", back.Worksheets[0], out DefinedName? print));
        Assert.Equal("'My sheet'!$A$1:$C$9", print.Formula);
        Assert.Equal("Rate*Local", back.Worksheets[0]["C1"].Formula);
    }

    [Fact]
    public void WritesDataTablesAsMultipleOperations()
    {
        // A one-input table: B1 holds the formula, A2:A3 the values for A1.
        Workbook source = Workbook.Load(Fods("""
            <table:table table:name="S"><table:table-row><table:table-cell office:value-type="float" office:value="1"/><table:table-cell table:formula="of:=[.A1]*10" office:value-type="float" office:value="10"/></table:table-row>
            <table:table-row><table:table-cell office:value-type="float" office:value="2"/><table:table-cell table:formula="of:=MULTIPLE.OPERATIONS([.B$1];[.$A$1];[.$A2])" office:value-type="float" office:value="20"/></table:table-row>
            <table:table-row><table:table-cell office:value-type="float" office:value="3"/><table:table-cell table:formula="of:=MULTIPLE.OPERATIONS([.B$1];[.$A$1];[.$A3])" office:value-type="float" office:value="30"/></table:table-row></table:table>
            """));
        string content = Content(source, WorkbookFormat.Fods);
        Assert.Contains("of:=MULTIPLE.OPERATIONS([.B$1];[.$A$1];[.$A2])", content, StringComparison.Ordinal);
        Assert.Contains("of:=MULTIPLE.OPERATIONS([.B$1];[.$A$1];[.$A3])", content, StringComparison.Ordinal);

        Workbook back = Workbook.Load(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        Assert.Equal(30.0, back.Worksheets[0]["B3"].Value);
    }

    [Fact]
    public void WritesLinkedFilesAsCachedTables()
    {
        Workbook source = Workbook.Load(Fods("""
            <table:table table:name="S"><table:table-row><table:table-cell table:formula="of:=['file:///C:/data/rates.ods'#$Rates.A1]*2" office:value-type="float" office:value="10"/></table:table-row></table:table>
            <table:table table:name="'file:///C:/data/rates.ods'#$Rates"><table:table-source xlink:href="file:///C:/data/rates.ods" table:table-name="Rates" table:mode="copy-results-only"/>
            <table:table-row><table:table-cell office:value-type="float" office:value="5"/></table:table-row></table:table>
            """));
        Assert.Equal("[1]Rates!A1*2", source.Worksheets[0]["A1"].Formula);

        string content = Content(source, WorkbookFormat.Fods);
        Assert.Contains("of:=['file:///C:/data/rates.ods'#$Rates.A1]*2", content, StringComparison.Ordinal);
        Assert.Contains("table:mode=\"copy-results-only\"", content, StringComparison.Ordinal);

        Workbook back = Workbook.Load(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        Assert.Equal("[1]Rates!A1*2", back.Worksheets[0]["A1"].Formula);
        Assert.Single(back.Worksheets);
        back.CalculateFull();
        Assert.Equal(10.0, back.Worksheets[0]["A1"].Value);
    }

    [Fact]
    public void EncryptsWithAPassword()
    {
        Workbook book = Workbook.Create();
        book.Worksheets[0]["A1"].Value = "secret";
        using MemoryStream stream = new();
        book.Save(stream, new WorkbookSaveOptions { Format = WorkbookFormat.Ods, Password = "pässword" });

        stream.Position = 0;
        Assert.Throws<DocumentPasswordException>(() => Workbook.Load(stream));
        stream.Position = 0;
        Assert.Throws<DocumentPasswordException>(() => Workbook.Load(stream, new WorkbookLoadOptions { Password = "wrong" }));
        stream.Position = 0;
        Workbook back = Workbook.Load(stream, new WorkbookLoadOptions { Password = "pässword" });
        Assert.Equal("secret", back.Worksheets[0]["A1"].Value);

        // AES-256-CBC with a PBKDF2 key from the password's SHA-256, as ODF 1.2 and later write.
        using ZipArchive zip = new(new MemoryStream(stream.ToArray()));
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        using StreamReader reader = new(zip.GetEntry("META-INF/manifest.xml")!.Open());
        string manifest = reader.ReadToEnd();
        Assert.Contains("http://www.w3.org/2001/04/xmlenc#aes256-cbc", manifest, StringComparison.Ordinal);
        Assert.Contains("PBKDF2", manifest, StringComparison.Ordinal);
        Assert.Contains("http://www.w3.org/2000/09/xmldsig#sha256", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesPasswordsForFormatsThatCannotBeEncrypted()
    {
        Workbook book = Workbook.Create();
        Assert.Throws<NotSupportedException>(() => book.Save(new MemoryStream(), new WorkbookSaveOptions { Format = WorkbookFormat.Fods, Password = "x" }));
        Assert.Throws<NotSupportedException>(() => book.Save(new MemoryStream(), new WorkbookSaveOptions { Format = WorkbookFormat.Xlsx, Password = "x" }));
    }

    [Theory]
    [InlineData(".ods", WorkbookFormat.Ods, "application/vnd.oasis.opendocument.spreadsheet")]
    [InlineData(".ots", WorkbookFormat.Ots, "application/vnd.oasis.opendocument.spreadsheet-template")]
    public void SavesByExtensionWithTheMimetypeFirst(string extension, WorkbookFormat format, string mediaType)
    {
        string path = Path.Combine(Path.GetTempPath(), "egap-ods-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            Workbook book = Workbook.Create();
            book.Worksheets[0]["A1"].Value = 1;
            book.Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(mediaType, OdfPackage.MediaTypeOfZip(bytes));
            Assert.Equal(format, Workbook.Load(path).Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWorkbookLoadedFromOdsSavesAsOdsToAStream()
    {
        Workbook book = Workbook.Load(Fods("<table:table table:name=\"S\"><table:table-row><table:table-cell office:value-type=\"float\" office:value=\"1\"/></table:table-row></table:table>"));
        Assert.Equal(WorkbookFormat.Fods, book.Format);
        using MemoryStream stream = new();
        book.Save(stream);
        Assert.StartsWith("<?xml", Encoding.UTF8.GetString(stream.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void WritesValidXmlForEveryPart()
    {
        Workbook book = Workbook.Create();
        book.Worksheets[0]["A1"].Value = "a\u0001b";
        using MemoryStream stream = new();
        book.Save(stream, new WorkbookSaveOptions { Format = WorkbookFormat.Ods });
        using ZipArchive zip = new(new MemoryStream(stream.ToArray()));
        foreach (string part in (string[])["content.xml", "styles.xml", "meta.xml", "META-INF/manifest.xml"])
        {
            using Stream entry = zip.GetEntry(part)!.Open();
            Assert.NotNull(XDocument.Load(entry).Root);
        }

        Assert.Equal("ab", Workbook.Load(new MemoryStream(stream.ToArray())).Worksheets[0]["A1"].Value);
    }

    private static Workbook RoundTrip(Workbook book, WorkbookFormat format)
    {
        using MemoryStream stream = new();
        book.Save(stream, new WorkbookSaveOptions { Format = format });
        stream.Position = 0;
        return Workbook.Load(stream);
    }

    private static string Content(Workbook book, WorkbookFormat format)
    {
        using MemoryStream stream = new();
        book.Save(stream, new WorkbookSaveOptions { Format = format });
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static MemoryStream Fods(string tables) => new(Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.spreadsheet">
        <office:body><office:spreadsheet>{tables}</office:spreadsheet></office:body></office:document>
        """));
}
