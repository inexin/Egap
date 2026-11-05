using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Tests;

/// <summary>The formula tokenizer and parser: Excel's grammar read, and the text written back as it was or in another style.</summary>
public sealed class FormulaParserTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("1.5E+3+.25-3e2")]
    [InlineData("\"a \"\"quoted\"\" text\"&\"\"")]
    [InlineData("TRUE<>false")]
    [InlineData("#N/A")]
    [InlineData("#DIV/0!+#ref!")]
    [InlineData("{1,2;3,4}")]
    [InlineData("{-1,\"a\",TRUE;#N/A,+2.5,FALSE}")]
    [InlineData("SUM( A1 , B2:C3 )")]
    [InlineData("  1 +\n2  ")]
    [InlineData("$A$1+A$2+$B3+b4")]
    [InlineData("A:A+$B:$C+1:1+$2:$3")]
    [InlineData("Sheet1!A1+'My sheet'!$B$2+'O''Brien'!C3")]
    [InlineData("SUM(Sheet1:Sheet3!A1:B2)")]
    [InlineData("SUM('Jan 1:Dec 31'!A1)")]
    [InlineData("[1]Sheet1!A1+[1]!Rate+'[2]Other sheet'!B2")]
    [InlineData("Sheet1!#REF!+#REF!")]
    [InlineData("SUM((A1:B2,D4),A1:B2 B1:C3)")]
    [InlineData("-2^2*-A1%")]
    [InlineData("@A1:A10+_xlfn.SINGLE(B1:B10)")]
    [InlineData("A1#+_xlfn.ANCHORARRAY(B1)+Name#")]
    [InlineData("IF(A1,,2)+IF(,)+NOW()+PI( )")]
    [InlineData("Sales[Amount]+Sales[]+Sales[#All]+Sales[[#Headers],[Region]:[Amount]]")]
    [InlineData("[@Amount]+Sales[@]+Sales[@[Unit price]]+Sales[[#This Row],[Amount]]")]
    [InlineData("Sales[Col'[1']]")]
    [InlineData("_xlfn.XLOOKUP(A1,B:B,C:C)+_xlfn._xlws.SORT(D1:D9)")]
    [InlineData("A1:INDEX(B:B,3)")]
    [InlineData("Rate*Sheet1!Local+Total.2023+\\Path+_under")]
    [InlineData("LOG10(100)+R1C1x+Rate2")]
    [InlineData("1<=2>=3<4>5=6&7")]
    [InlineData("[1]!Rate+[1]!MyFunction(A1)")]
    public void FormulasPrintAsWritten(string formula)
    {
        ParsedFormula parsed = FormulaParser.Parse(formula, r1c1: false, default);

        Assert.Equal(formula, FormulaPrinter.Print(parsed, ReferenceStyle.Written));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("SUM(", 4)]
    [InlineData("1+", 2)]
    [InlineData("\"abc", 0)]
    [InlineData("{1,2;3}", 6)]
    [InlineData("SUM(A1))", 7)]
    [InlineData("1 2", 2)]
    [InlineData("A1,B1", 2)]
    [InlineData("#BAD!", 0)]
    [InlineData("Sheet1!", 7)]
    [InlineData("'Sheet1'A1", 8)]
    [InlineData("Table[[#Nope]]", 7)]
    [InlineData("Table[[A],[B]]", 10)]
    [InlineData("1e999", 0)]
    [InlineData("$Name", 0)]
    [InlineData("1+R1C1", 2)]
    [InlineData("Sheet1!rc", 7)]
    [InlineData("C2[Amount]", 0)]
    public void BrokenFormulasAreRefusedWithAPosition(string formula, int position)
    {
        FormulaException error = Assert.Throws<FormulaException>(() => FormulaParser.Parse(formula, r1c1: false, default));

        Assert.Equal(formula, error.Formula);
        Assert.Equal(position, error.Position);
    }

    [Fact]
    public void DeepNestingIsRefusedNotOverflowed()
    {
        string deep = new string('(', 100_000) + "1" + new string(')', 100_000);
        string unary = new string('-', 100_000) + "1";

        Assert.Throws<FormulaException>(() => FormulaParser.Parse(deep, r1c1: false, default));
        Assert.Throws<FormulaException>(() => FormulaParser.Parse(unary, r1c1: false, default));
    }

    [Fact]
    public void OperatorsBindAsInExcel()
    {
        BinaryNode sum = Assert.IsType<BinaryNode>(FormulaParser.Parse("1+2*3^4%", r1c1: false, default).Root);
        Assert.Equal(BinaryOperator.Add, sum.Operator);
        BinaryNode product = Assert.IsType<BinaryNode>(sum.Right);
        Assert.Equal(BinaryOperator.Multiply, product.Operator);
        BinaryNode power = Assert.IsType<BinaryNode>(product.Right);
        Assert.Equal(BinaryOperator.Power, power.Operator);
        Assert.IsType<PostfixNode>(power.Right);

        // Negation binds tighter than ^, and ^ groups to the left.
        BinaryNode negated = Assert.IsType<BinaryNode>(FormulaParser.Parse("-2^2^3", r1c1: false, default).Root);
        Assert.IsType<BinaryNode>(negated.Left);
        Assert.IsType<UnaryNode>(((BinaryNode)negated.Left).Left);

        // The space between two references is an intersection; before an operator it is only space.
        Assert.Equal(BinaryOperator.Intersection, Assert.IsType<BinaryNode>(FormulaParser.Parse("A1:B2 B1:C3", r1c1: false, default).Root).Operator);
        Assert.Equal(BinaryOperator.Add, Assert.IsType<BinaryNode>(FormulaParser.Parse("A1 +B1", r1c1: false, default).Root).Operator);
    }

    [Fact]
    public void ReferencesAreReadWithTheirSheetsAndFlags()
    {
        ReferenceNode cell = Assert.IsType<ReferenceNode>(FormulaParser.Parse("'My sheet'!$B4", r1c1: false, default).Root);
        Assert.Equal(new SheetPrefix("'My sheet'!", null, "My sheet", "My sheet"), cell.Prefix);
        Assert.Equal(new Area(AreaKind.Cell, 3, 1, 3, 1, AreaFlags.FirstColumnAbsolute | AreaFlags.LastColumnAbsolute), cell.Area);

        ReferenceNode threeD = Assert.IsType<ReferenceNode>(FormulaParser.Parse("[1]Jan:Mar!A1:C$9", r1c1: false, default).Root);
        Assert.Equal(("1", "Jan", "Mar"), (threeD.Prefix!.Book, threeD.Prefix.FirstSheet, threeD.Prefix.LastSheet));
        Assert.Equal(new Area(AreaKind.Range, 0, 0, 8, 2, AreaFlags.LastRowAbsolute), threeD.Area);

        // A word shaped like a cell starts a range, not a 3D prefix.
        Assert.Equal(BinaryOperator.Range, Assert.IsType<BinaryNode>(FormulaParser.Parse("A1:Data!B2", r1c1: false, default).Root).Operator);

        Assert.Equal(AreaKind.Columns, Assert.IsType<ReferenceNode>(FormulaParser.Parse("$A:C", r1c1: false, default).Root).Area.Kind);
        Assert.Equal(AreaKind.Rows, Assert.IsType<ReferenceNode>(FormulaParser.Parse("Sheet1!$1:3", r1c1: false, default).Root).Area.Kind);

        // Past column XFD a word is a name, and a word before ( is a function.
        Assert.IsType<NameNode>(FormulaParser.Parse("XFE1", r1c1: false, default).Root);
        Assert.IsType<FunctionNode>(FormulaParser.Parse("LOG10(1)", r1c1: false, default).Root);
    }

    [Theory]
    [InlineData("Sales[Amount]", "Sales", (int)(TableItems.None), "Amount", null)]
    [InlineData("Sales[]", "Sales", (int)(TableItems.None), null, null)]
    [InlineData("Sales[#Totals]", "Sales", (int)(TableItems.Totals), null, null)]
    [InlineData("Sales[[#Headers],[#Data],[Region]:[Amount]]", "Sales", (int)(TableItems.Headers | TableItems.Data), "Region", "Amount")]
    [InlineData("[@Amount]", null, (int)(TableItems.ThisRow), "Amount", null)]
    [InlineData("Sales[@[Unit price]:[Amount]]", "Sales", (int)(TableItems.ThisRow), "Unit price", "Amount")]
    [InlineData("Sales[[#This Row],[Amount]]", "Sales", (int)(TableItems.ThisRow), "Amount", null)]
    [InlineData("Sales[Col'[1'] '#2]", "Sales", (int)(TableItems.None), "Col[1] #2", null)]
    public void StructuredReferencesNameTheirParts(string formula, string? table, int items, string? first, string? last)
    {
        StructuredNode node = Assert.IsType<StructuredNode>(FormulaParser.Parse(formula, r1c1: false, default).Root);

        Assert.Equal((table, (TableItems)items, first, last), (node.Table, node.Items, node.FirstColumn, node.LastColumn));
    }

    [Theory]
    [InlineData("SUM($A$1:B3)", "SUM(R1C1:R[-1]C)")]
    [InlineData("A:A+1:1", "C[-1]+R[-3]")]
    [InlineData("$B:$C+$2:$2", "C2:C3+R2")]
    [InlineData("Sheet2!B4+'Q 1'!$A1", "Sheet2!RC+'Q 1'!R[-3]C1")]
    [InlineData("Sales[Amount]+#REF!+Sheet1!#REF!", "Sales[Amount]+#REF!+Sheet1!#REF!")]
    [InlineData("A1048576+XFD4", "R[1048572]C[-1]+RC[16382]")]
    public void A1FormulasTurnIntoR1C1(string a1, string r1c1)
    {
        Workbook book = Workbook.Create();
        Cell cell = book.Worksheets[0]["B4"];
        cell.Formula = a1;

        Assert.Equal(r1c1, cell.FormulaR1C1);

        cell.FormulaR1C1 = r1c1;
        Assert.Equal(a1, cell.Formula);
    }

    [Fact]
    public void R1C1FormulasAreReadForTheirCell()
    {
        Workbook book = Workbook.Create();
        Cell cell = book.Worksheets[0]["C3"];

        cell.FormulaR1C1 = "=R[-1]C+RC[-1]*R1C1+SUM(R:R[1])+C+rc";
        Assert.Equal("C2+B3*$A$1+SUM(3:4)+C:C+C3", cell.Formula);

        // Relative references wrap around the sheet.
        cell.FormulaR1C1 = "R[-3]C";
        Assert.Equal("C1048576", cell.Formula);

        Assert.Throws<FormulaException>(() => cell.FormulaR1C1 = "R0C1");
        Assert.Throws<FormulaException>(() => cell.FormulaR1C1 = "R1C16385");
    }

    [Fact]
    public void RewrittenFormulasReadBackInEitherStyle()
    {
        // A sheet named like a reference is quoted, and a name before ':' is kept apart from what follows.
        ParsedFormula a1 = FormulaParser.Parse("R!A1+'C2:Data'!B2+(1:A1)", r1c1: false, default);
        Assert.Equal("'R'!RC+'C2:Data'!R[1]C[1]+((1):RC)", FormulaPrinter.Print(a1, ReferenceStyle.R1C1));

        ParsedFormula r1c1 = FormulaParser.Parse("a:S+1:R2", r1c1: true, default);
        string written = FormulaPrinter.Print(r1c1, ReferenceStyle.A1);
        Assert.Equal("(a):S+(1):$2:$2", written);
        Assert.Equal(written, FormulaPrinter.Print(FormulaParser.Parse(written, r1c1: false, default), ReferenceStyle.A1));
    }

    [Fact]
    public void A1FormulasMoveTheirRelativeReferences()
    {
        ParsedFormula parsed = FormulaParser.Parse("A1+$B$2+A$3+$C4+SUM(A:A,1:1)", r1c1: false, default);

        Assert.Equal("C3+$B$2+C$3+$C6+SUM(C:C,3:3)", FormulaPrinter.Print(parsed, ReferenceStyle.A1, new CellAddress(2, 2)));
        Assert.Equal("#REF!", FormulaPrinter.Print(FormulaParser.Parse("A1", r1c1: false, new CellAddress(1, 1)), ReferenceStyle.A1, new CellAddress(0, 0)));
    }

    [Fact]
    public void SettingABrokenFormulaThrowsAndKeepsTheCell()
    {
        Workbook book = Workbook.Create();
        Cell cell = book.Worksheets[0]["A1"];
        cell.Formula = "1+1";

        FormulaException error = Assert.Throws<FormulaException>(() => cell.Formula = "=SUM(A2");

        Assert.Equal("SUM(A2", error.Formula);
        Assert.Equal("1+1", cell.Formula);
        Assert.Null(book.Worksheets[0]["A2"].FormulaR1C1);
    }
}
