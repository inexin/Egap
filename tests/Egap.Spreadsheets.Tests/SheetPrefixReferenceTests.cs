using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Sheets written before references, tables and names, where a word that is a cell reference in one style must not become a
/// table or a name in the other, and a table's sheet is dropped when the formula is written again.
/// </summary>
public sealed class SheetPrefixReferenceTests
{
    private static readonly CellAddress Cell = new(5, 5);

    [Theory]
    [InlineData("R1C1:B3Data!C[-4]")]
    [InlineData("R1C1:Data!R1C1")]
    [InlineData("R1C1:'B3Data'!C[-4]")]
    [InlineData("Sheet1!R")]
    [InlineData("SUM(Sheet1!R[-1],1)")]
    public void R1C1ReferencesAfterASheetParse(string text)
    {
        Assert.NotNull(FormulaParser.TryParse(text, r1c1: true, Cell));
    }

    [Theory]
    [InlineData("Sheet1!R[-1S]")]
    [InlineData("Sheet1!C[x]")]
    [InlineData("Sheet1!RC2[x]")]
    [InlineData("Sheet1!A1[x]")]
    [InlineData("R[x]")]
    [InlineData("Sheet1!R")]
    [InlineData("Sheet1!C")]
    public void AWordThatIsAReferenceIsNoTableOrNameAfterASheetInA1Style(string text)
    {
        Assert.Null(FormulaParser.TryParse(text, r1c1: false, Cell));
    }

    [Fact]
    public void ATablesSheetIsDroppedWhenTheFormulaIsWrittenAgain()
    {
        ParsedFormula parsed = FormulaParser.TryParse("C2!Sales[Amount]+1", r1c1: false, Cell)!;

        Assert.Equal("C2!Sales[Amount]+1", FormulaPrinter.Print(parsed, ReferenceStyle.Written));
        Assert.Equal("Sales[Amount]+1", FormulaPrinter.Print(parsed, ReferenceStyle.A1, Cell));
        Assert.Equal("Sales[Amount]+1", FormulaPrinter.Print(parsed, ReferenceStyle.R1C1));
    }
}
