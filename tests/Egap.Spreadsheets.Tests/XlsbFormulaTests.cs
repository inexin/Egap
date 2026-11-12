using Egap.Spreadsheets.Xls;
using Egap.Spreadsheets.Xlsb;

namespace Egap.Spreadsheets.Tests;

/// <summary>BIFF12 formula tokens made into the text Excel writes for them in an XLSX file; the token bytes are Excel's own.</summary>
public sealed class XlsbFormulaTests
{
    [Fact]
    public void KeepsSpacesBeforeOperatorsAndParentheses()
    {
        // SUM(B12) + (0.5*C12): the space before + and the one before the opening parenthesis.
        byte[] tokens = Convert.FromHexString("240B00000001C0" + "19100000" + "1F000000000000E03F" + "440B00000002C0" + "05" + "19400201" + "15" + "19400001" + "03");
        Assert.Equal("SUM(B12) + (0.5*C12)", XlsbFormula.Decode(new XlsLinks(), tokens, [], 0, 0));
    }

    [Fact]
    public void KeepsSpacesInsideACallsParentheses()
    {
        // NOW( )-1: a volatile PtgAttrSpace before the closing parenthesis.
        byte[] tokens = Convert.FromHexString("19410401" + "414A00" + "1E0100" + "04");
        Assert.Equal("NOW( )-1", XlsbFormula.Decode(new XlsLinks(), tokens, [], 0, 0));
    }

    [Fact]
    public void WritesTheWholeSheetAsRows()
    {
        byte[] tokens = Convert.FromHexString("25" + "00000000" + "FFFF0F00" + "0000" + "FF3F");
        Assert.Equal("$1:$1048576", XlsbFormula.Decode(new XlsLinks(), tokens, [], 0, 0));
    }

    [Fact]
    public void ReadsExcel2007FunctionsByNumber()
    {
        // IFERROR(1/0,-1): function 480, which BIFF8 numbers do not reach.
        byte[] tokens = Convert.FromHexString("1E0100" + "1E0000" + "06" + "1F000000000000F0BF" + "41E001");
        Assert.Equal("IFERROR(1/0,-1)", XlsbFormula.Decode(new XlsLinks(), tokens, [], 0, 0));
    }

    [Fact]
    public void NamesASheetsNameWithItsSheetAndAWorkbooksWithTheBook()
    {
        XlsLinks links = new();
        links.SheetNames.Add("Detail Data");
        links.Books.Add(new XlsSupBook { Kind = XlsSupBookKind.Self });
        links.Sheets.Add((0, -2, -2));
        links.Names.Add("fStart");
        links.Names.Add("cRows");
        links.NameSheets.Add(0);
        links.NameSheets.Add(-1);
        Assert.Equal("'Detail Data'!fStart", XlsbFormula.Decode(links, Convert.FromHexString("390000" + "01000000"), [], 0, 0));
        Assert.Equal("[0]!cRows", XlsbFormula.Decode(links, Convert.FromHexString("390000" + "02000000"), [], 0, 0));
    }

    [Fact]
    public void PrefixesAddInAndUserFunctionsAsExcelDoes()
    {
        XlsLinks links = new();
        links.Books.Add(new XlsSupBook { Kind = XlsSupBookKind.Self });
        XlsSupBook addIn = new() { Kind = XlsSupBookKind.AddIn };
        addIn.Names.Add("OSTRIP");
        links.Books.Add(addIn);
        links.Sheets.Add((1, -2, -2));
        links.Names.Add("Concat");

        // An add-in's function, and a macro's function that shares a built-in function's name.
        Assert.Equal("_xll.OSTRIP(5)", XlsbFormula.Decode(links, Convert.FromHexString("390000" + "01000000" + "1E0500" + "4202FF00"), [], 0, 0));
        Assert.Equal("_xludf.Concat(\"C\")", XlsbFormula.Decode(links, Convert.FromHexString("2301000000" + "1701004300" + "4202FF00"), [], 0, 0));
    }

    [Fact]
    public void QuotesTheSheetAfterARangesColon()
    {
        XlsLinks links = new();
        links.SheetNames.Add("Deals");
        links.Books.Add(new XlsSupBook { Kind = XlsSupBookKind.Self });
        links.Sheets.Add((0, 0, 0));
        byte[] tokens = Convert.FromHexString("3A0000" + "050000000500" + "3A0000" + "070000000500" + "11");
        Assert.Equal("Deals!$F$6:'Deals'!$F$8", XlsbFormula.Decode(links, tokens, [], 0, 0));
    }

    [Fact]
    public void ReadsADdeLinksItemByNumber()
    {
        XlsLinks links = new();
        links.Books.Add(new XlsSupBook { Kind = XlsSupBookKind.Self });
        XlsSupBook dde = new() { Kind = XlsSupBookKind.Other, Number = 1 };
        dde.Names.Add("NBP");
        dde.Names.Add("APU,DIVIDEND");
        links.Books.Add(dde);
        links.Sheets.Add((1, -2, -2));
        Assert.Equal("[1]!NBP", XlsbFormula.Decode(links, Convert.FromHexString("390000" + "01000000"), [], 0, 0));
        Assert.Equal("[1]!'APU,DIVIDEND'", XlsbFormula.Decode(links, Convert.FromHexString("390000" + "02000000"), [], 0, 0));
    }

    [Theory]
    [InlineData("Data", true)]
    [InlineData("Sheet1.Old", true)]
    [InlineData("E11.XLS", false)]
    [InlineData("May06", false)]
    [InlineData("Other Sheet", false)]
    public void QuotesASheetNameThatStartsLikeAReference(string name, bool plain)
    {
        // Excel writes 'E11.XLS'!A1: the part before the dot reads as a cell.
        Assert.Equal(plain, SheetNames.IsPlain(name));
    }
}
