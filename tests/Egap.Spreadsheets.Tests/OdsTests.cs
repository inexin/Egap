using System.IO.Compression;
using System.Text;

using Egap.Documents;
using Egap.Spreadsheets.Ods;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Reading OpenDocument spreadsheets: OpenFormula translation, cell values and types, styles and number formats, merged
/// cells, rows and columns, names and sheets, from flat FODS documents written here and their zipped ODS form, and from
/// <c>Oracle/ods-sample.ods</c>, which LibreOffice converted from <c>Oracle/ods-sample.xlsx</c>.
/// </summary>
public sealed class OdsTests
{
    private static readonly string Oracle = Path.Combine(AppContext.BaseDirectory, "Oracle");

    [Theory]
    [InlineData("of:=SUM([.A1:.B2])", "SUM(A1:B2)")]
    [InlineData("of:=[.A1]+[.$B$2]", "A1+$B$2")]
    [InlineData("of:=[$Sheet2.A1]*2", "Sheet2!A1*2")]
    [InlineData("of:=[$'My sheet'.A1:.C3]", "'My sheet'!A1:C3")]
    [InlineData("of:=SUM([$'It''s'.A1])", "SUM('It''s'!A1)")]
    [InlineData("of:=SUM([$Sheet1.A1:$Sheet3.B2])", "SUM(Sheet1:Sheet3!A1:B2)")]
    [InlineData("of:=IF([.A1]>0;\"yes;no\";\"no\")", "IF(A1>0,\"yes;no\",\"no\")")]
    [InlineData("of:=SUM({1;2|3;4})", "SUM({1,2;3,4})")]
    [InlineData("of:=TRUE()", "TRUE")]
    [InlineData("of:=AND(FALSE( );[.A1])", "AND(FALSE,A1)")]
    [InlineData("of:=COM.MICROSOFT.IFS([.A1]>1;1;TRUE();2)", "_xlfn.IFS(A1>1,1,TRUE,2)")]
    [InlineData("of:=LEGACY.NORMSDIST(1)", "NORMSDIST(1)")]
    [InlineData("of:=SUM([.A1:.A3]~[.C1:.C3])", "SUM(A1:A3,C1:C3)")]
    [InlineData("of:=SUM([.A1:.C3]![.B2:.D4])", "SUM(A1:C3 B2:D4)")]
    [InlineData("of:=[.A:.A]", "A:A")]
    [InlineData("of:=SUM([.B3:.B3])", "SUM(B3:B3)")]
    [InlineData("of:=SUM([.2:.3])", "SUM(2:3)")]
    [InlineData("of:=1E-3+2.5e+2", "1E-3+2.5e+2")]
    [InlineData("of:=#N/A", "#N/A")]
    [InlineData("of:=[.#REF!]", "#REF!")]
    [InlineData("oooc:=SUM([.A1])", "SUM(A1)")]
    [InlineData("=SUM([.A1])", "SUM(A1)")]
    [InlineData("msoxl:=SUM(A1:B2)", "SUM(A1:B2)")]
    [InlineData("of:=mod([.A1];2)", "MOD(A1,2)")]
    [InlineData("of:=FORMULA([.A1])", "_xlfn.FORMULATEXT(A1)")]
    public void TranslatesOpenFormula(string formula, string excel)
    {
        Assert.Equal(excel, OpenFormula.ToExcel(formula, out bool exact));
        Assert.True(exact);
    }

    [Fact]
    public void MarksReferencesToOtherFilesInexact()
    {
        Assert.Equal("#REF!+1", OpenFormula.ToExcel("of:=['file:///c:/x.ods'#$Sheet1.A1]+1", out bool exact));
        Assert.False(exact);
    }

    [Fact]
    public void LeavesOtherSyntaxesOut() => Assert.Null(OpenFormula.ToExcel("foo:=1", out _));

    [Fact]
    public void ReadsValuesOfEveryType()
    {
        Workbook book = Load(Flat(Table("Data",
            Row(Cell("float", "office:value=\"12.5\""), Cell("percentage", "office:value=\"0.25\""), Cell("currency", "office:value=\"3\" office:currency=\"EUR\"")),
            Row(Cell("date", "office:date-value=\"2024-03-01\""), Cell("date", "office:date-value=\"1900-01-01T12:00:00\""), Cell("time", "office:time-value=\"PT36H30M00S\"")),
            Row(Cell("boolean", "office:boolean-value=\"true\""), "<table:table-cell office:value-type=\"string\"><text:p>one<text:s text:c=\"2\"/>two</text:p><text:p>line<text:tab/>2</text:p></table:table-cell>",
                "<table:table-cell office:value-type=\"string\" office:string-value=\"attribute\"><text:p>shown</text:p></table:table-cell>"),
            Row("<table:table-cell table:formula=\"of:=1/0\" office:value-type=\"float\" office:value=\"0\" calcext:value-type=\"error\"><text:p>#DIV/0!</text:p></table:table-cell>",
                "<table:table-cell><text:p>no type</text:p></table:table-cell>"))));
        Worksheet sheet = book.Worksheets[0];
        Assert.Equal("Data", sheet.Name);
        Assert.Equal(12.5, sheet["A1"].Value.Number);
        Assert.Equal(0.25, sheet["B1"].Value.Number);
        Assert.Equal(3, sheet["C1"].Value.Number);
        Assert.Equal(45352, sheet["A2"].Value.Number);
        Assert.Equal(2.5, sheet["B2"].Value.Number);
        Assert.Equal(1.5208333333333333, sheet["C2"].Value.Number, 12);
        Assert.True(sheet["A3"].Value.Boolean);
        Assert.Equal("one  two\nline\t2", sheet["B3"].Value.Text);
        Assert.Equal("attribute", sheet["C3"].Value.Text);
        Assert.Equal("1/0", sheet["A4"].Formula);
        Assert.Equal(CellError.DivisionByZero, sheet["A4"].Value.Error);
        Assert.Equal("no type", sheet["B4"].Value.Text);
        Assert.False(book.Date1904);
    }

    [Fact]
    public void ReadsErrorsTypedIntoCellsAsValues()
    {
        Workbook book = Load(Flat(Table("S", Row("<table:table-cell table:formula=\"of:=#REF!\" office:value-type=\"float\" office:value=\"0\"/>",
            "<table:table-cell table:formula=\"of:=#N/A+1\" office:value-type=\"float\" office:value=\"0\" calcext:value-type=\"error\"><text:p>#N/A</text:p></table:table-cell>"))));
        Worksheet sheet = book.Worksheets[0];
        Assert.False(sheet["A1"].HasFormula);
        Assert.Equal(CellError.Reference, sheet["A1"].Value.Error);
        Assert.Equal("#N/A+1", sheet["B1"].Formula);
    }

    [Theory]
    [InlineData("1899-12-30", 0)]
    [InlineData("1900-03-01", 61)]
    [InlineData("2024-03-01T18:00:00", 45352.75)]
    [InlineData("2024-03-01T06:00:00.5Z", 45352.25000578704)]
    [InlineData("1800-01-01", -36522)]
    [InlineData("-0001-01-01", -694324)]
    [InlineData("12345-06-07", 3815117)]
    public void ReadsDatesOutsideDateTimeYears(string value, double serial)
    {
        Assert.Equal(serial, OdsReader.Date(value, new DateTime(1899, 12, 30))!.Value, 9);
    }

    [Theory]
    [InlineData("2024-13-01")]
    [InlineData("24-01-01")]
    [InlineData("2024-01-01X")]
    [InlineData("2024-01-01T25")]
    public void LeavesMalformedDatesOut(string value) => Assert.Null(OdsReader.Date(value, new DateTime(1899, 12, 30)));

    [Fact]
    public void Reads1904Dates()
    {
        Workbook book = Load(Flat(Table("S", Row(Cell("date", "office:date-value=\"1904-01-02\""))),
            calculation: "<table:calculation-settings><table:null-date table:date-value=\"1904-01-01\"/></table:calculation-settings>"));
        Assert.True(book.Date1904);
        Assert.Equal(1, book.Worksheets[0]["A1"].Value.Number);
    }

    [Fact]
    public void RepeatsCellsAndRowsAndSkipsTheFiller()
    {
        Workbook book = Load(Flat(Table("S",
            Row("<table:table-cell table:number-columns-repeated=\"3\" office:value-type=\"float\" office:value=\"7\"/>"),
            "<table:table-row table:number-rows-repeated=\"2\"><table:table-cell office:value-type=\"string\"><text:p>x</text:p></table:table-cell></table:table-row>",
            "<table:table-row table:number-rows-repeated=\"1048572\"><table:table-cell table:number-columns-repeated=\"16384\"/></table:table-row>")));
        Worksheet sheet = book.Worksheets[0];
        Assert.Equal([7.0, 7.0, 7.0], [sheet["A1"].Value.Number, sheet["B1"].Value.Number, sheet["C1"].Value.Number]);
        Assert.Equal("x", sheet["A2"].Value.Text);
        Assert.Equal("x", sheet["A3"].Value.Text);
        Assert.Equal("A1:C3", sheet.UsedRange!.Value.Address.ToString());
    }

    [Fact]
    public void ReadsFormulasArraysAndMerges()
    {
        Workbook book = Load(Flat(Table("S",
            Row("<table:table-cell table:formula=\"of:=SUM([.B1:.C1])\" office:value-type=\"float\" office:value=\"3\" table:number-columns-spanned=\"2\" table:number-rows-spanned=\"1\"/>",
                "<table:covered-table-cell office:value-type=\"float\" office:value=\"1\"/>", Cell("float", "office:value=\"2\"")),
            Row("<table:table-cell table:formula=\"of:={1;2}\" table:number-matrix-columns-spanned=\"2\" table:number-matrix-rows-spanned=\"1\" office:value-type=\"float\" office:value=\"1\"/>",
                Cell("float", "office:value=\"2\"")),
            Row("<table:table-cell table:formula=\"of:=[.A1]&amp;&quot;x&quot;\" office:value-type=\"string\" office:string-value=\"3x\"><text:p>3x</text:p></table:table-cell>"))));
        Worksheet sheet = book.Worksheets[0];
        Assert.Equal("SUM(B1:C1)", sheet["A1"].Formula);
        Assert.Equal(3, sheet["A1"].Value.Number);
        Assert.Equal(1, sheet["B1"].Value.Number);
        Assert.Equal([RangeAddress.Parse("A1:B1")], sheet.MergedCells);
        Assert.Equal("A2:B2", sheet["A2"].ArrayRange!.Value.Address.ToString());
        Assert.Equal("{1,2}", sheet["A2"].Formula);
        Assert.Equal(2, sheet["B2"].Value.Number);
        Assert.Equal("A1&\"x\"", sheet["A3"].Formula);
        Assert.Equal("3x", sheet["A3"].Value.Text);
    }

    [Fact]
    public void ReadsStylesAndNumberFormats()
    {
        string styles = """
            <number:number-style style:name="N1"><number:number number:decimal-places="2" number:min-decimal-places="2" number:min-integer-digits="1" number:grouping="true"/></number:number-style>
            <number:percentage-style style:name="N2"><number:number number:decimal-places="1" number:min-decimal-places="1" number:min-integer-digits="1"/><number:text>%</number:text></number:percentage-style>
            <number:date-style style:name="N3"><number:year number:style="long"/><number:text>-</number:text><number:month number:style="long"/><number:text>-</number:text><number:day number:style="long"/></number:date-style>
            <number:time-style style:name="N4" number:truncate-on-overflow="false"><number:hours/><number:text>:</number:text><number:minutes number:style="long"/></number:time-style>
            <number:number-style style:name="N5P0" style:volatile="true"><number:number number:decimal-places="0" number:min-integer-digits="1"/></number:number-style>
            <number:number-style style:name="N5"><style:text-properties fo:color="#ff0000"/><number:text>-</number:text><number:number number:decimal-places="0" number:min-integer-digits="1"/><style:map style:condition="value()&gt;=0" style:apply-style-name="N5P0"/></number:number-style>
            <style:style style:name="Default" style:family="table-cell"><style:text-properties style:font-name="Arial" fo:font-size="10pt"/></style:style>
            <style:style style:name="ce1" style:family="table-cell" style:parent-style-name="Default" style:data-style-name="N1">
              <style:table-cell-properties fo:background-color="#ffff00" fo:border="0.74pt solid #000000" fo:wrap-option="wrap" style:vertical-align="middle" style:text-align-source="fix"/>
              <style:paragraph-properties fo:text-align="center"/>
              <style:text-properties fo:font-weight="bold" fo:font-style="italic" fo:color="#0000ff" style:text-underline-style="solid" fo:font-size="12pt"/>
            </style:style>
            <style:style style:name="ce2" style:family="table-cell" style:data-style-name="N2"/>
            <style:style style:name="ce3" style:family="table-cell" style:data-style-name="N3"/>
            <style:style style:name="ce4" style:family="table-cell" style:data-style-name="N4"/>
            <style:style style:name="ce5" style:family="table-cell" style:data-style-name="N5"/>
            """;
        Workbook book = Load(Flat(Table("S",
            Row(Cell("float", "office:value=\"1234.5\" table:style-name=\"ce1\""), Cell("percentage", "office:value=\"0.5\" table:style-name=\"ce2\""),
                Cell("date", "office:date-value=\"2024-01-02\" table:style-name=\"ce3\""), Cell("time", "office:time-value=\"PT30H05M\" table:style-name=\"ce4\""),
                Cell("float", "office:value=\"-4\" table:style-name=\"ce5\""))), styles: styles));
        Worksheet sheet = book.Worksheets[0];
        CellStyle style = sheet["A1"].Style;
        Assert.Equal("#,##0.00", style.NumberFormat);
        Assert.True(style.Font.Bold);
        Assert.True(style.Font.Italic);
        Assert.Equal(CellUnderline.Single, style.Font.Underline);
        Assert.Equal(12, style.Font.Size);
        Assert.Equal("Arial", style.Font.Name);
        Assert.Equal(CellColor.FromRgb(0, 0, 255), style.Font.Color);
        Assert.Equal(CellFill.Solid(CellColor.FromRgb(255, 255, 0)), style.Fill);
        Assert.Equal(CellBorderStyle.Thin, style.Borders.Left.Style);
        Assert.Equal(CellBorderStyle.Thin, style.Borders.Bottom.Style);
        Assert.Equal(CellHorizontalAlignment.Center, style.Alignment.Horizontal);
        Assert.Equal(CellVerticalAlignment.Center, style.Alignment.Vertical);
        Assert.True(style.Alignment.WrapText);
        Assert.Equal("1,234.50", sheet["A1"].Text);
        Assert.Equal("0.0%", sheet["B1"].Style.NumberFormat);
        Assert.Equal("yyyy-mm-dd", sheet["C1"].Style.NumberFormat);
        Assert.Equal("2024-01-02", sheet["C1"].Text);
        Assert.Equal("[h]:mm", sheet["D1"].Style.NumberFormat);
        Assert.Equal("30:05", sheet["D1"].Text);
        Assert.Equal("0;[Red]-0", sheet["E1"].Style.NumberFormat);
    }

    [Fact]
    public void LeavesFontNamesExcelCannotHoldForTheDefault()
    {
        string styles = $"""<style:style style:name="ce1" style:family="table-cell"><style:text-properties style:font-name="{new string('F', 40)}"/></style:style>""";
        Workbook book = Load(Flat(Table("S", Row(Cell("float", "office:value=\"1\" table:style-name=\"ce1\""))), styles: styles));
        Assert.Equal(CellStyle.Default.Font.Name, book.Worksheets[0]["A1"].Style.Font.Name);
    }

    [Theory]
    [InlineData("0.2pt")]
    [InlineData("500pt")]
    public void LeavesFontSizesExcelCannotHoldForTheDefault(string size)
    {
        string styles = $"""<style:style style:name="ce1" style:family="table-cell"><style:text-properties fo:font-size="{size}"/></style:style>""";
        Workbook book = Load(Flat(Table("S", Row(Cell("float", "office:value=\"1\" table:style-name=\"ce1\""))), styles: styles));
        Assert.Equal(CellStyle.Default.Font.Size, book.Worksheets[0]["A1"].Style.Font.Size);
    }

    [Fact]
    public void ReadsColumnsRowsAndSheets()
    {
        string styles = """
            <style:style style:name="co1" style:family="table-column"><style:table-column-properties style:column-width="1in"/></style:style>
            <style:style style:name="ro1" style:family="table-row"><style:table-row-properties style:row-height="30pt" style:use-optimal-row-height="false"/></style:style>
            <style:style style:name="ta2" style:family="table"><style:table-properties table:display="false"/></style:style>
            """;
        string first = """
            <table:table table:name="One">
              <table:table-column table:style-name="co1" table:number-columns-repeated="2"/>
              <table:table-column-group><table:table-column table:visibility="collapse"/></table:table-column-group>
              <table:table-row table:style-name="ro1"><table:table-cell office:value-type="float" office:value="1"/></table:table-row>
              <table:table-row-group><table:table-row table:visibility="collapse"><table:table-cell office:value-type="float" office:value="2"/></table:table-row></table:table-row-group>
            </table:table>
            """;
        Workbook book = Load(Flat(first + Table("Two", Row(Cell("float", "office:value=\"3\""))).Replace("<table:table table:name=\"Two\"", "<table:table table:name=\"Two\" table:style-name=\"ta2\"", StringComparison.Ordinal),
            styles: styles));
        Worksheet one = book.Worksheets[0];
        Assert.Equal(["One", "Two"], book.Worksheets.Select(s => s.Name));
        Assert.Equal(13, one.Columns[0].Width!.Value, 1);
        Assert.Equal(13, one.Columns[1].Width!.Value, 1);
        Assert.True(one.Columns[2].Hidden);
        Assert.Equal(1, one.Columns[2].OutlineLevel);
        Assert.Equal(30, one.Rows[0].Height);
        Assert.True(one.Rows[1].Hidden);
        Assert.Equal(1, one.Rows[1].OutlineLevel);
        Assert.Equal(SheetVisibility.Hidden, book.Worksheets[1].Visibility);
    }

    [Fact]
    public void LeavesOutCopiesOfOtherFilesSheets()
    {
        string copy = """
            <table:table table:name="'file:///c:/other.xlsx'#$Prices" table:print="false">
              <table:table-source xlink:type="simple" xlink:href="file:///c:/other.xlsx" table:table-name="Prices" table:mode="copy-results-only" xmlns:xlink="http://www.w3.org/1999/xlink"/>
              <table:table-row><table:table-cell office:value-type="float" office:value="9"/></table:table-row>
            </table:table>
            """;
        Workbook book = Load(Flat(Table("Main", Row("<table:table-cell table:formula=\"of:=['file:///c:/other.xlsx'#$Prices.A1]\" office:value-type=\"float\" office:value=\"9\"/>")) + copy));
        Assert.Equal(["Main"], book.Worksheets.Select(s => s.Name));
        Assert.Equal("#REF!", book.Worksheets[0]["A1"].Formula);
        Assert.Equal(9, book.Worksheets[0]["A1"].Value.Number);
    }

    [Fact]
    public void ReadsNames()
    {
        string tables = Table("Data", Row(Cell("float", "office:value=\"1\"")), localNames:
            "<table:named-expressions><table:named-range table:name=\"Local\" table:base-cell-address=\"$Data.$A$1\" table:cell-range-address=\"$Data.$A$1\"/></table:named-expressions>")
            .Replace("<table:table table:name=\"Data\"", "<table:table table:name=\"Data\" table:print-ranges=\"$Data.$A$1:.$B$2\"", StringComparison.Ordinal);
        string names = """
            <table:named-expressions>
              <table:named-range table:name="Input" table:base-cell-address="$Data.$A$1" table:cell-range-address="$Data.$A$1:.$B$3"/>
              <table:named-expression table:name="Rate" table:base-cell-address="$Data.$A$1" table:expression="of:=[$Data.$A$1]*2"/>
            </table:named-expressions>
            """;
        Workbook book = Load(Flat(tables + names));
        Assert.Equal("Data!$A$1:$B$3", book.Names.Single(n => n.Name == "Input").Formula);
        Assert.Equal("Data!$A$1*2", book.Names.Single(n => n.Name == "Rate").Formula);
        DefinedName local = book.Names.Single(n => n.Name == "Local");
        Assert.Same(book.Worksheets[0], local.Sheet);
        Assert.Equal([RangeAddress.Parse("$A$1:$B$2")], book.Worksheets[0].PageSetup.PrintArea);
    }

    [Fact]
    public void ReadsZippedPackagesAndStreams()
    {
        string content = Flat(Table("Zip", Row(Cell("float", "office:value=\"42\""))));
        byte[] package = Zip(content);
        using MemoryStream stream = new(package);
        Assert.Equal(42, Workbook.Load(stream).Worksheets[0]["A1"].Value.Number);

        string path = Path.Combine(AppContext.BaseDirectory, "ods-zipped.ods");
        File.WriteAllBytes(path, package);
        Assert.Equal(42, Workbook.Load(path).Worksheets[0]["A1"].Value.Number);

        // A stream that cannot seek is read into memory and told apart the same way.
        using NonSeekable once = new(package);
        Assert.Equal(42, Workbook.Load(once).Worksheets[0]["A1"].Value.Number);
    }

    [Fact]
    public void RejectsOtherOpenDocuments()
    {
        string text = Flat(Table("S", Row())).Replace("opendocument.spreadsheet", "opendocument.text", StringComparison.Ordinal);
        Assert.Throws<DocumentException>(() => Workbook.Load(new MemoryStream(Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    public void LimitsRepeatedCells()
    {
        string huge = Flat(Table("S", "<table:table-row table:number-rows-repeated=\"1048576\"><table:table-cell table:number-columns-repeated=\"16384\" office:value-type=\"float\" office:value=\"1\"/></table:table-row>"));
        Assert.Throws<DocumentLimitException>(() => Load(huge));
    }

    [Fact]
    public void SavesWhatItReadAsXlsx()
    {
        Workbook book = Load(Flat(Table("S", Row(Cell("float", "office:value=\"5\""), "<table:table-cell table:formula=\"of:=[.A1]*2\" office:value-type=\"float\" office:value=\"10\"/>"))));
        using MemoryStream saved = new();
        book.Save(saved);
        saved.Position = 0;
        Workbook again = Workbook.Load(saved);
        Assert.Equal("A1*2", again.Worksheets[0]["B1"].Formula);
        Assert.Equal(10, again.Worksheets[0]["B1"].Value.Number);
    }

    [Fact]
    public void ReadsWhatLibreOfficeWrote()
    {
        Workbook xlsx = Workbook.Load(Path.Combine(Oracle, "ods-sample.xlsx"));
        Workbook ods = Workbook.Load(Path.Combine(Oracle, "ods-sample.ods"));
        Assert.Equal(xlsx.Worksheets.Select(s => s.Name), ods.Worksheets.Select(s => s.Name));
        for (int index = 0; index < xlsx.Worksheets.Count; index++)
        {
            foreach (Cell cell in xlsx.Worksheets[index].Cells)
            {
                Cell other = ods.Worksheets[index].Cells[cell.Row, cell.Column];
                Assert.Equal((cell.Address, cell.Value, cell.Formula), (other.Address, other.Value, other.Formula));
                Assert.Equal((cell.Address, cell.Style.NumberFormat, cell.Style.Font.Bold), (other.Address, other.Style.NumberFormat, other.Style.Font.Bold));
            }

            Assert.Equal(xlsx.Worksheets[index].MergedCells, ods.Worksheets[index].MergedCells);
        }

        Assert.Equal(xlsx.Names.Select(n => (n.Name, n.Formula)), ods.Names.Select(n => (n.Name, n.Formula)));
    }

    private static Workbook Load(string flat) => Workbook.Load(new MemoryStream(Encoding.UTF8.GetBytes(flat)));

    private static string Cell(string type, string attributes) => $"<table:table-cell office:value-type=\"{type}\" {attributes}/>";

    private static string Row(params string[] cells) => "<table:table-row>" + string.Concat(cells) + "</table:table-row>";

    private static string Table(string name, params string[] rows) => Table(name, rows, null);

    private static string Table(string name, string row, string? localNames) => Table(name, [row], localNames);

    private static string Table(string name, string[] rows, string? localNames) =>
        $"<table:table table:name=\"{name}\">" + string.Concat(rows) + localNames + "</table:table>";

    private static string Flat(string tables, string? styles = null, string? calculation = null) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
          xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
          xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" xmlns:number="urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0"
          xmlns:calcext="urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0"
          office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.spreadsheet">
          <office:styles>{styles}</office:styles>
          <office:body><office:spreadsheet>{calculation}{tables}</office:spreadsheet></office:body>
        </office:document>
        """;

    /// <summary>A flat document as an ODS package: the mimetype stored first, then content.xml and styles.xml.</summary>
    private static byte[] Zip(string flat)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "mimetype", "application/vnd.oasis.opendocument.spreadsheet", CompressionLevel.NoCompression);
            string content = flat.Replace("<office:document ", "<office:document-content ", StringComparison.Ordinal)
                .Replace("</office:document>", "</office:document-content>", StringComparison.Ordinal);
            Write(zip, "content.xml", content, CompressionLevel.Optimal);
            Write(zip, "META-INF/manifest.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3">
                  <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.spreadsheet"/>
                  <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                </manifest:manifest>
                """, CompressionLevel.Optimal);
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string text, CompressionLevel level)
    {
        using Stream entry = zip.CreateEntry(name, level).Open();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        entry.Write(bytes);
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
