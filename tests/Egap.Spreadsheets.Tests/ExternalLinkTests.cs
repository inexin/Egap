namespace Egap.Spreadsheets.Tests;

/// <summary>Formulas that read the values a workbook keeps for its linked workbooks, as Excel does when links aren't updated.</summary>
public sealed class ExternalLinkTests
{
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string Link = $"""
        <externalLink xmlns="{XlsxSeed.Main}" xmlns:r="{Rel}"><externalBook r:id="rId1">
        <sheetNames><sheetName val="Prices"/><sheetName val="Other Sheet"/></sheetNames>
        <definedNames><definedName name="Rate" refersTo="Prices!$A$1"/><definedName name="Table" refersTo="Prices!$A$1:$A$3"/><definedName name="Gone" refersTo="#REF!"/></definedNames>
        <sheetDataSet>
        <sheetData sheetId="0">
        <row r="1"><cell r="A1"><v>2.5</v></cell><cell r="B1" t="str"><v>first</v></cell></row>
        <row r="2"><cell r="A2"><v>7</v></cell><cell r="B2" t="b"><v>1</v></cell></row>
        <row r="3"><cell r="A3"><v>9</v></cell><cell r="B3" t="e"><v>#N/A</v></cell></row>
        </sheetData>
        <sheetData sheetId="1" refreshError="1"/>
        </sheetDataSet></externalBook></externalLink>
        """;

    private static Dictionary<string, string> Parts(string link) => new()
    {
        ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            <Override PartName="/xl/externalLinks/externalLink1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml"/></Types>
            """,
        ["_rels/.rels"] = $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rel}/officeDocument" Target="xl/workbook.xml"/></Relationships>""",
        ["xl/_rels/workbook.xml.rels"] = $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rel}/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{Rel}/externalLink" Target="externalLinks/externalLink1.xml"/></Relationships>""",
        ["xl/workbook.xml"] = $"""<workbook xmlns="{XlsxSeed.Main}" xmlns:r="{Rel}"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets><externalReferences><externalReference r:id="rId2"/></externalReferences></workbook>""",
        ["xl/worksheets/sheet1.xml"] = $"""<worksheet xmlns="{XlsxSeed.Main}"><sheetData/></worksheet>""",
        ["xl/externalLinks/externalLink1.xml"] = link,
        ["xl/externalLinks/_rels/externalLink1.xml.rels"] = $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rel}/externalLinkPath" Target="prices.xlsx" TargetMode="External"/></Relationships>""",
    };

    private static CellValue Calculated(string formula, string link = Link)
    {
        Workbook book = Workbook.Load(XlsxSeed.Package(Parts(link)));
        Cell cell = book.Worksheets[0]["D1"];
        cell.Formula = formula;
        book.CalculateFull();
        return cell.Value;
    }

    [Theory]
    [InlineData("[1]Prices!A1", 2.5)]
    [InlineData("[1]Prices!$A$2*2", 14)]
    [InlineData("SUM([1]Prices!A1:A3)", 18.5)]
    [InlineData("COUNT([1]Prices!A1:C9)", 3)]
    [InlineData("VLOOKUP(7,[1]Prices!A1:B3,1,FALSE)", 7)]
    [InlineData("INDEX([1]Prices!A1:B3,3,1)", 9)]
    [InlineData("[1]!Rate", 2.5)]
    [InlineData("[1]Prices!C9", 0)]
    [InlineData("SUM([1]!Table)", 18.5)]
    public void FormulasReadTheKeptNumbers(string formula, double expected)
    {
        Assert.Equal(expected, Calculated(formula).Number);
    }

    [Fact]
    public void TextLogicalValuesAndErrorsComeBackAsKept()
    {
        Assert.Equal("first", Calculated("[1]Prices!B1").Text);
        Assert.True(Calculated("[1]Prices!B2").Boolean);
        Assert.Equal(CellError.NotAvailable, Calculated("[1]Prices!B3").Error);
    }

    [Theory]
    [InlineData("[1]Other Sheet!A1")]
    [InlineData("[1]Nowhere!A1")]
    [InlineData("[2]Prices!A1")]
    [InlineData("[1]!Gone")]
    [InlineData("INDIRECT(\"[1]Prices!A2\")")]
    public void WhatIsNotKeptIsARefError(string formula)
    {
        string written = formula.Contains(' ', StringComparison.Ordinal) ? "'" + formula.Replace("!", "'!", StringComparison.Ordinal) : formula;
        Assert.Equal(CellError.Reference, Calculated(written).Error);
    }

    [Theory]
    [InlineData("Sheet1")]
    [InlineData("A name far longer than the thirty-one characters of a tab")]
    public void ALinkedSheetMayHaveANameATabCouldNot(string name)
    {
        string link = Link.Replace("Prices", name, StringComparison.Ordinal);
        Assert.Equal(2.5, Calculated($"'[1]{name}'!A1", link).Number);
    }

    [Fact]
    public void AnEmptyPartOfAKeptRangeIsBlank()
    {
        Assert.Equal(0, Calculated("SUM([1]Prices!C1:C3)").Number);
    }

    [Theory]
    [InlineData("<externalLink")]
    [InlineData("<other/>")]
    public void ALinkThatCannotBeReadShowsRefErrorsAndTheWorkbookStillLoads(string link)
    {
        Assert.Equal(CellError.Reference, Calculated("[1]Prices!A1", link).Error);
    }

    [Fact]
    public void ReadingALinkedBookIsNoChangeToTheWorkbook()
    {
        Workbook book = Workbook.Load(XlsxSeed.Package(Parts(Link)));
        Assert.Equal(0, book.ChangeCount);
        Worksheet sheet = book.Worksheets[0];
        sheet["D1"].Formula = "[1]Prices!A1";
        sheet["D2"].Formula = "D1*2";
        long before = book.ChangeCount;

        // The first calculation reads the part, in the middle of the calculation.
        book.Calculate();

        Assert.Equal(before, book.ChangeCount);
        Assert.Equal(5, sheet["D2"].Value.Number);
        Assert.Equal(0, book.Calculate().Calculated);
    }

    [Fact]
    public void TheLinkPartSavesAsItWas()
    {
        Workbook book = Workbook.Load(XlsxSeed.Package(Parts(Link)));
        book.Worksheets[0]["D1"].Formula = "[1]Prices!A1";
        book.CalculateFull();

        (Workbook loaded, MemoryStream saved) = XlsxSeed.RoundTrip(book);

        Dictionary<string, byte[]> parts = XlsxSeed.Parts(saved);
        Assert.Equal(Link.Trim(), System.Text.Encoding.UTF8.GetString(parts["xl/externalLinks/externalLink1.xml"]));
        Assert.Contains("externalReference", System.Text.Encoding.UTF8.GetString(parts["xl/workbook.xml"]), StringComparison.Ordinal);
        Assert.Equal(2.5, loaded.Worksheets[0]["D1"].Value.Number);
        loaded.CalculateFull();
        Assert.Equal(2.5, loaded.Worksheets[0]["D1"].Value.Number);
    }
}
