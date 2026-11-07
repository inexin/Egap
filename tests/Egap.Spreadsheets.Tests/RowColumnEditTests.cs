using Egap.Documents.Charts;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Putting rows and columns into a sheet and taking them out, with the formulas, values and places read from Excel after
/// the same edit: tools in the pull request's description.
/// </summary>
public sealed class RowColumnEditTests
{
    /// <summary>The edits of <see cref="Excel"/>, in its order: the kind, the zero-based first row or column and the count.</summary>
    private static readonly (string Kind, int At, int Count)[] Edits =
    [
        ("InsertRows", 4, 2), ("DeleteRows", 4, 2), ("InsertRows", 0, 1), ("DeleteRows", 6, 1),
        ("InsertColumns", 2, 2), ("DeleteColumns", 2, 2), ("DeleteColumns", 1, 1),
    ];

    /// <summary>
    /// Each formula's sheet and text, then where Excel had it after each edit, its formula there and the value it showed.
    /// On sheet S the formulas go from H20 down beside numbers in A1:E30 (row times 10 plus column); on Other from A1 down.
    /// </summary>
    private static readonly string[][] Excel =
    [
        ["S", "A1", "H22 A1 = 11", "H18 A1 = 11", "H21 A2 = 11", "H19 A1 = 11", "J20 A1 = 11", "F20 A1 = 11", "G20 A1 = 11"],
        ["S", "A4", "H23 A4 = 41", "H19 A4 = 41", "H22 A5 = 41", "H20 A4 = 41", "J21 A4 = 41", "F21 A4 = 41", "G21 A4 = 41"],
        ["S", "A5", "H24 A7 = 51", "H20 #REF! = #REF!", "H23 A6 = 51", "H21 A5 = 51", "J22 A5 = 51", "F22 A5 = 51", "G22 A5 = 51"],
        ["S", "A6", "H25 A8 = 61", "H21 #REF! = #REF!", "H24 A7 = 61", "H22 A6 = 61", "J23 A6 = 61", "F23 A6 = 61", "G23 A6 = 61"],
        ["S", "A7", "H26 A9 = 71", "H22 A5 = 71", "H25 A8 = 71", "H23 #REF! = #REF!", "J24 A7 = 71", "F24 A7 = 71", "G24 A7 = 71"],
        ["S", "A8", "H27 A10 = 81", "H23 A6 = 81", "H26 A9 = 81", "H24 A7 = 81", "J25 A8 = 81", "F25 A8 = 81", "G25 A8 = 81"],
        ["S", "$A$7", "H28 $A$9 = 71", "H24 $A$5 = 71", "H27 $A$8 = 71", "H25 #REF! = #REF!", "J26 $A$7 = 71", "F26 $A$7 = 71", "G26 $A$7 = 71"],
        ["S", "A$7", "H29 A$9 = 71", "H25 A$5 = 71", "H28 A$8 = 71", "H26 #REF! = #REF!", "J27 A$7 = 71", "F27 A$7 = 71", "G27 A$7 = 71"],
        ["S", "$A7", "H30 $A9 = 71", "H26 $A5 = 71", "H29 $A8 = 71", "H27 #REF! = #REF!", "J28 $A7 = 71", "F28 $A7 = 71", "G28 $A7 = 71"],
        ["S", "A4:A8", "H31 A4:A10 = #SPILL!", "H27 A4:A6 = #SPILL!", "H30 A5:A9 = #SPILL!", "H28 A4:A7 = #SPILL!", "J29 A4:A8 = #SPILL!", "F29 A4:A8 = #SPILL!", "G29 A4:A8 = #SPILL!"],
        ["S", "A5:A6", "H32 A7:A8 = #SPILL!", "H28 #REF! = #REF!", "H31 A6:A7 = #SPILL!", "H29 A5:A6 = #SPILL!", "J30 A5:A6 = #SPILL!", "F30 A5:A6 = #SPILL!", "G30 A5:A6 = #SPILL!"],
        ["S", "A5:A7", "H33 A7:A9 = #SPILL!", "H29 A5:A5 = 71", "H32 A6:A8 = #SPILL!", "H30 A5:A6 = #SPILL!", "J31 A5:A7 = #SPILL!", "F31 A5:A7 = #SPILL!", "G31 A5:A7 = #SPILL!"],
        ["S", "A3:A5", "H34 A3:A7 = #SPILL!", "H30 A3:A4 = #SPILL!", "H33 A4:A6 = #SPILL!", "H31 A3:A5 = #SPILL!", "J32 A3:A5 = #SPILL!", "F32 A3:A5 = #SPILL!", "G32 A3:A5 = #SPILL!"],
        ["S", "A6:A9", "H35 A8:A11 = #SPILL!", "H31 A5:A7 = #SPILL!", "H34 A7:A10 = #SPILL!", "H32 A6:A8 = #SPILL!", "J33 A6:A9 = #SPILL!", "F33 A6:A9 = #SPILL!", "G33 A6:A9 = #SPILL!"],
        ["S", "A7:A9", "H36 A9:A11 = #SPILL!", "H32 A5:A7 = #SPILL!", "H35 A8:A10 = #SPILL!", "H33 A7:A8 = #SPILL!", "J34 A7:A9 = #SPILL!", "F34 A7:A9 = #SPILL!", "G34 A7:A9 = #SPILL!"],
        ["S", "A5:A5", "H37 A7:A7 = 51", "H33 #REF! = #REF!", "H36 A6:A6 = 51", "H34 A5:A5 = 51", "J35 A5:A5 = 51", "F35 A5:A5 = 51", "G35 A5:A5 = 51"],
        ["S", "5:6", "H38 7:8 = #SPILL!", "H34 #REF! = #REF!", "H37 6:7 = #SPILL!", "H35 5:6 = #SPILL!", "J36 5:6 = #SPILL!", "F36 5:6 = #SPILL!", "G36 5:6 = #SPILL!"],
        ["S", "4:8", "H39 4:10 = #SPILL!", "H35 4:6 = #SPILL!", "H38 5:9 = #SPILL!", "H36 4:7 = #SPILL!", "J37 4:8 = #SPILL!", "F37 4:8 = #SPILL!", "G37 4:8 = #SPILL!"],
        ["S", "$5:$6", "H40 $7:$8 = #SPILL!", "H36 #REF! = #REF!", "H39 $6:$7 = #SPILL!", "H37 $5:$6 = #SPILL!", "J38 $5:$6 = #SPILL!", "F38 $5:$6 = #SPILL!", "G38 $5:$6 = #SPILL!"],
        ["S", "A:A", "H41 A:A = #SPILL!", "H37 A:A = #SPILL!", "H40 A:A = #SPILL!", "H38 A:A = #SPILL!", "J39 A:A = #SPILL!", "F39 A:A = #SPILL!", "G39 A:A = #SPILL!"],
        ["S", "B3:C10", "H42 B3:C12 = #SPILL!", "H38 B3:C8 = #SPILL!", "H41 B4:C11 = #SPILL!", "H39 B3:C9 = #SPILL!", "J40 B3:E10 = #SPILL!", "F40 B3:B10 = #SPILL!", "G40 B3:B10 = #SPILL!"],
        ["S", "SUM(A1:A100)", "H43 SUM(A1:A102) = 4680", "H39 SUM(A1:A98) = 4568", "H42 SUM(A2:A101) = 4680", "H40 SUM(A1:A99) = 4609", "J41 SUM(A1:A100) = 4680", "F41 SUM(A1:A100) = 4680", "G41 SUM(A1:A100) = 4680"],
        ["S", "A1048576", "H44 #REF! = #REF!", "H40 A1048574 = 0", "H43 #REF! = #REF!", "H41 A1048575 = 0", "J42 A1048576 = 0", "F42 A1048576 = 0", "G42 A1048576 = 0"],
        ["S", "A1048575:A1048576", "H45 #REF! = #REF!", "H41 A1048573:A1048576 = #SPILL!", "H44 A1048576:A1048576 = 0", "H42 A1048574:A1048576 = #SPILL!", "J43 A1048575:A1048576 = #SPILL!", "F43 A1048575:A1048576 = #SPILL!", "G43 A1048575:A1048576 = #SPILL!"],
        ["S", "A2:A1048576", "H46 A2:A1048576 = #SPILL!", "H42 A2:A1048576 = #SPILL!", "H45 A3:A1048576 = #SPILL!", "H43 A2:A1048576 = #SPILL!", "J44 A2:A1048576 = #SPILL!", "F44 A2:A1048576 = #SPILL!", "G44 A2:A1048576 = #SPILL!"],
        ["S", "Other!A5", "H47 Other!A5 = #SPILL!", "H43 Other!A5 = #REF!", "H46 Other!A5 = #SPILL!", "H44 Other!A5 = #SPILL!", "J45 Other!A5 = #SPILL!", "F45 Other!A5 = #SPILL!", "G45 Other!A5 = #SPILL!"],
        ["S", "INDEX(A:A,5)", "H48 INDEX(A:A,5) = 0", "H44 INDEX(A:A,5) = 71", "H47 INDEX(A:A,5) = 41", "H45 INDEX(A:A,5) = 51", "J46 INDEX(A:A,5) = 51", "F46 INDEX(A:A,5) = 51", "G46 INDEX(A:A,5) = 51"],
        ["S", "\"A5\"&A5", "H49 \"A5\"&A7 = A551", "H45 \"A5\"&#REF! = #REF!", "H48 \"A5\"&A6 = A551", "H46 \"A5\"&A5 = A551", "J47 \"A5\"&A5 = A551", "F47 \"A5\"&A5 = A551", "G47 \"A5\"&A5 = A551"],
        ["S", "SUM(A5,A6,A7)", "H50 SUM(A7,A8,A9) = 183", "H46 SUM(#REF!,#REF!,A5) = #REF!", "H49 SUM(A6,A7,A8) = 183", "H47 SUM(A5,A6,#REF!) = #REF!", "J48 SUM(A5,A6,A7) = 183", "F48 SUM(A5,A6,A7) = 183", "G48 SUM(A5,A6,A7) = 183"],
        ["S", "A5:A6 A6:A7", "H51 A7:A8 A8:A9 = 61", "H47 #REF! A5:A5 = #REF!", "H50 A6:A7 A7:A8 = 61", "H48 A5:A6 A6:A6 = 61", "J49 A5:A6 A6:A7 = 61", "F49 A5:A6 A6:A7 = 61", "G49 A5:A6 A6:A7 = 61"],
        ["S", "C5", "H52 C7 = 53", "H48 #REF! = #REF!", "H51 C6 = 53", "H49 C5 = 53", "J50 E5 = 53", "F50 #REF! = #REF!", "G50 B5 = 53"],
        ["S", "D5", "H53 D7 = 54", "H49 #REF! = #REF!", "H52 D6 = 54", "H50 D5 = 54", "J51 F5 = 54", "F51 #REF! = #REF!", "G51 C5 = 54"],
        ["S", "E5", "H54 E7 = 55", "H50 #REF! = #REF!", "H53 E6 = 55", "H51 E5 = 55", "J52 G5 = 55", "F52 C5 = 55", "G52 D5 = 55"],
        ["S", "B5:E5", "H55 B7:E7 = 52", "H51 #REF! = #REF!", "H54 B6:E6 = 52", "H52 B5:E5 = 52", "J53 B5:G5 = 52", "F53 B5:C5 = 52", "G53 B5:D5 = 53"],
        ["S", "C5:D5", "H56 C7:D7 = 53", "H52 #REF! = #REF!", "H55 C6:D6 = 53", "H53 C5:D5 = 53", "J54 E5:F5 = 53", "F54 #REF! = #REF!", "G54 B5:C5 = 53"],
        ["S", "C:D", "H57 C:D = #SPILL!", "H53 C:D = #SPILL!", "H56 C:D = #SPILL!", "H54 C:D = #SPILL!", "J55 E:F = #SPILL!", "F55 #REF! = #REF!", "G55 B:C = #SPILL!"],
        ["S", "B:C", "H58 B:C = #SPILL!", "H54 B:C = #SPILL!", "H57 B:C = #SPILL!", "H55 B:C = #SPILL!", "J56 B:E = #SPILL!", "F56 B:B = #SPILL!", "G56 B:B = #SPILL!"],
        ["S", "$C$5", "H59 $C$7 = 53", "H55 #REF! = #REF!", "H58 $C$6 = 53", "H56 $C$5 = 53", "J57 $E$5 = 53", "F57 #REF! = #REF!", "G57 $B$5 = 53"],
        ["S", "XFD1", "H60 XFD1 = 0", "H56 XFD1 = 0", "H59 XFD2 = 0", "H57 XFD1 = 0", "J58 #REF! = #REF!", "F58 XFB1 = 0", "G58 XFC1 = 0"],
        ["S", "SUM(S!A5:A8)", "H61 SUM(S!A7:A10) = 264", "H57 SUM(S!A5:A6) = 152", "H60 SUM(S!A6:A9) = 264", "H58 SUM(S!A5:A7) = 193", "J59 SUM(S!A5:A8) = 264", "F59 SUM(S!A5:A8) = 264", "G59 SUM(S!A5:A8) = 264"],
        ["S", "S!A6", "H62 S!A8 = 61", "H58 S!#REF! = #REF!", "H61 S!A7 = 61", "H59 S!A6 = 61", "J60 S!A6 = 61", "F60 S!A6 = 61", "G60 S!A6 = 61"],
        ["S", "A6:B6:C8", "H63 A8:B8:C10 = #SPILL!", "H59 #REF!:C6 = #REF!", "H62 A7:B7:C9 = #SPILL!", "H60 A6:B6:C7 = #SPILL!", "J61 A6:B6:E8 = #SPILL!", "F61 A6:B6:#REF! = #REF!", "G61 A6:A6:B8 = #SPILL!"],
        ["S", "ROW(A7)", "H64 ROW(A9) = 9", "H60 ROW(A5) = 5", "H63 ROW(A8) = 8", "H61 ROW(#REF!) = #REF!", "J62 ROW(A7) = 7", "F62 ROW(A7) = 7", "G62 ROW(A7) = 7"],
        ["S", "(A5:A6,A7)", "H65 (A7:A8,A9) = #VALUE!", "H61 (#REF!,A5) = #REF!", "H64 (A6:A7,A8) = #VALUE!", "H62 (A5:A6,#REF!) = #REF!", "J63 (A5:A6,A7) = #VALUE!", "F63 (A5:A6,A7) = #VALUE!", "G63 (A5:A6,A7) = #VALUE!"],
        ["Other", "S!A5", "A1 S!A7 = 51", "A1 S!#REF! = #REF!", "A1 S!A6 = 51", "A1 S!A5 = 51", "A1 S!A5 = 51", "A1 S!A5 = 51", "A1 S!A5 = 51"],
        ["Other", "S!A4:A8", "A2 S!A4:A10 = #SPILL!", "A2 S!A4:A6 = #SPILL!", "A2 S!A5:A9 = #SPILL!", "A2 S!A4:A7 = #SPILL!", "A2 S!A4:A8 = #SPILL!", "A2 S!A4:A8 = #SPILL!", "A2 S!A4:A8 = #SPILL!"],
        ["Other", "'S'!A6", "A3 S!A8 = 61", "A3 S!#REF! = #REF!", "A3 S!A7 = 61", "A3 S!A6 = 61", "A3 S!A6 = 61", "A3 S!A6 = 61", "A3 S!A6 = 61"],
        ["Other", "S!A:A", "A4 S!A:A = #SPILL!", "A4 S!A:A = #SPILL!", "A4 S!A:A = #SPILL!", "A4 S!A:A = #SPILL!", "A4 S!A:A = #SPILL!", "A4 S!A:A = #SPILL!", "A4 S!A:A = #SPILL!"],
        ["Other", "S!5:6", "A5 S!7:8 = #SPILL!", "A5 S!#REF! = #REF!", "A5 S!6:7 = #SPILL!", "A5 S!5:6 = #SPILL!", "A5 S!5:6 = #SPILL!", "A5 S!5:6 = #SPILL!", "A5 S!5:6 = #SPILL!"],
        ["Other", "A5", "A6 A5 = #SPILL!", "A6 A5 = #REF!", "A6 A5 = #SPILL!", "A6 A5 = #SPILL!", "A6 A5 = #SPILL!", "A6 A5 = #SPILL!", "A6 A5 = #SPILL!"],
        ["Other", "S!C5", "A7 S!C7 = 53", "A7 S!#REF! = #REF!", "A7 S!C6 = 53", "A7 S!C5 = 53", "A7 S!E5 = 53", "A7 S!#REF! = #REF!", "A7 S!B5 = 53"],
        ["Other", "S!B5:E5", "A8 S!B7:E7 = 52", "A8 S!#REF! = #REF!", "A8 S!B6:E6 = 52", "A8 S!B5:E5 = 52", "A8 S!B5:G5 = 52", "A8 S!B5:C5 = 52", "A8 S!B5:D5 = 53"],
        ["Other", "S!C:D", "A9 S!C:D = #SPILL!", "A9 S!C:D = #SPILL!", "A9 S!C:D = #SPILL!", "A9 S!C:D = #SPILL!", "A9 S!E:F = #SPILL!", "A9 S!#REF! = #REF!", "A9 S!B:C = #SPILL!"],
        ["Other", "SUM(S!A5,S!A6)", "A10 SUM(S!A7,S!A8) = 112", "A10 SUM(S!#REF!,S!#REF!) = #REF!", "A10 SUM(S!A6,S!A7) = 112", "A10 SUM(S!A5,S!A6) = 112", "A10 SUM(S!A5,S!A6) = 112", "A10 SUM(S!A5,S!A6) = 112", "A10 SUM(S!A5,S!A6) = 112"],
        ["Other", "S!$A$5:$B$9", "A11 S!$A$7:$B$11 = 51", "A11 S!$A$5:$B$7 = 71", "A11 S!$A$6:$B$10 = 51", "A11 S!$A$5:$B$8 = 51", "A11 S!$A$5:$B$9 = 51", "A11 S!$A$5:$B$9 = 51", "A11 S!$A$5:$A$9 = 51"],
    ];

    public static TheoryData<int, int> Cases()
    {
        TheoryData<int, int> cases = [];
        for (int formula = 0; formula < Excel.Length; formula++)
        {
            for (int edit = 0; edit < Edits.Length; edit++)
            {
                cases.Add(formula, edit);
            }
        }

        return cases;
    }

    private static (Workbook Book, Worksheet S, Worksheet Other) Book()
    {
        Workbook book = Workbook.CreateEmpty();
        Worksheet s = book.Worksheets.Add("S");
        Worksheet other = book.Worksheets.Add("Other");
        for (int r = 0; r < 30; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                s.Cells[r, c].Value = ((r + 1) * 10) + c + 1;
            }
        }

        return (book, s, other);
    }

    private static void Apply(Worksheet sheet, (string Kind, int At, int Count) edit)
    {
        switch (edit.Kind)
        {
            case "InsertRows":
                sheet.InsertRows(edit.At, edit.Count);
                break;
            case "DeleteRows":
                sheet.DeleteRows(edit.At, edit.Count);
                break;
            case "InsertColumns":
                sheet.InsertColumns(edit.At, edit.Count);
                break;
            default:
                sheet.DeleteColumns(edit.At, edit.Count);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AFormulaMovesAsInExcel(int formula, int edit)
    {
        (Workbook book, Worksheet s, Worksheet other) = Book();
        string[] row = Excel[formula];
        Worksheet home = row[0] == "S" ? s : other;
        int onS = 0, onOther = 0;
        foreach (string[] each in Excel)
        {
            // All the formulas are in, as they were in Excel: a dynamic array's spill depends on the cells around it.
            Cell target = each[0] == "S" ? s.Cells[19 + onS++, 7] : other.Cells[onOther++, 0];
            target.Formula = each[1];
        }

        Apply(s, Edits[edit]);
        book.Calculate();

        string[] expected = row[2 + edit].Split(' ', 2);
        string[] answer = expected[1].Split(" = ", 2);
        Cell cell = home[expected[0]];
        Assert.Equal(answer[0], cell.Formula?.Replace("'S'!", "S!", StringComparison.Ordinal));
        Assert.Equal(answer[1], cell.Text);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(CellAddress.MaxRows, 1)]
    [InlineData(0, 0)]
    [InlineData(CellAddress.MaxRows - 1, 2)]
    public void AnEditOffTheSheetIsRefused(int row, int count)
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.InsertRows(row, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.DeleteRows(row, count));
    }

    [Fact]
    public void CellsAreNotPushedOffTheSheet()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet.Cells[CellAddress.MaxRows - 1, 0].Value = 1;
        Assert.Throws<InvalidOperationException>(() => sheet.InsertRows(0));
        Assert.Equal(1, sheet.Cells[CellAddress.MaxRows - 1, 0].Value.Number);
    }

    [Theory]
    [InlineData("InsertRows", 3, 1)]
    [InlineData("DeleteRows", 3, 1)]
    [InlineData("DeleteRows", 1, 2)]
    [InlineData("InsertColumns", 2, 1)]
    public void PartOfAnArrayFormulaIsNotChanged(string kind, int at, int count)
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet.Range("B3:C5").SetArrayFormula("A1:B3*2");
        Assert.Throws<InvalidOperationException>(() => Apply(sheet, (kind, at, count)));
        Assert.Equal("A1:B3*2", sheet["B3"].Formula);
    }

    [Fact]
    public void AWholeArrayFormulaMoves()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = 1;
        sheet["A2"].Value = 2;
        sheet.Range("B3:B4").SetArrayFormula("A1:A2*2");
        sheet.InsertRows(0, 2);
        sheet.Workbook.Calculate();
        Assert.Equal("A3:A4*2", sheet["B5"].Formula);
        Assert.Equal(4, sheet["B6"].Value.Number);
    }

    [Fact]
    public void TheSeedWorkbookFollowsAnInsert()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet data = book.Worksheets["Data"];
        data.InsertRows(1, 2);

        // The shared formula's cells each have their own now, and the array formula moved with its range.
        Assert.Equal("B4*C4", data["D4"].Formula);
        Assert.Equal("B5*C5", data["D5"].Formula);
        Assert.Equal("SUM(B4:B5*C4:C5)", data["D6"].Formula);
        Assert.Equal(["A11:C11"], data.MergedCells.Select(m => m.ToString()));
        Assert.Equal("B4:B8", data.ConditionalFormats.Single().Ranges.Single().ToString());
        Assert.Equal("C4:C8", data.DataValidations.Single().Ranges.Single().ToString());
        Assert.Equal("A14", data.Hyperlinks.Single().Range.ToString());
        Assert.Equal(new CellAddress(3, 2), data.Comments.Single().Cell);
        Assert.Equal("Data!$A$4:$A$8", book.Names.Single(n => n.Name == "LocalRange").Formula);
        Assert.Equal("Data!$A$1:$E$9", book.Names.Single(n => n.Name == "_xlnm.Print_Area").Formula);
        Assert.Equal(1, data.View.FrozenRows);

        (Workbook back, _) = XlsxSeed.RoundTrip(book);
        Worksheet read = back.Worksheets["Data"];
        Assert.Equal("B5*C5", read["D5"].Formula);
        Assert.Equal("SUM(B4:B5*C4:C5)", read["D6"].Formula);
        Assert.Equal(35, read["D6"].Value.Number);
        Assert.Equal("A note", read.Comments.Single().Text);
        Assert.Equal(new CellAddress(3, 2), read.Comments.Single().Cell);
    }

    [Fact]
    public void TheSeedWorkbookFollowsADelete()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet data = book.Worksheets["Data"];
        data.DeleteRows(2);
        book.Calculate();

        // The shared formula's first cell stays and the second goes; the array formula reads the rows left.
        Assert.Equal("B2*C2", data["D2"].Formula);
        Assert.Equal("SUM(B2:B2*C2:C2)", data["D3"].Formula);
        Assert.Equal(12.5, data["D3"].Value.Number);
        Assert.Equal(["A8:C8"], data.MergedCells.Select(m => m.ToString()));
        Assert.Equal("B2:B5", data.ConditionalFormats.Single().Ranges.Single().ToString());
        Assert.Equal("Data!$A$2:$A$5", book.Names.Single(n => n.Name == "LocalRange").Formula);
        Assert.Equal(new CellAddress(1, 2), data.Comments.Single().Cell);
    }

    [Fact]
    public void ANoteOnARowTakenOutGoes()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet.Comments.Add("B3", "gone");
        sheet.Comments.Add("B5", "kept");
        sheet.DeleteRows(2);
        Assert.Equal("kept", sheet.Comments.Single().Text);
        Assert.Equal(new CellAddress(3, 1), sheet.Comments.Single().Cell);
    }

    [Fact]
    public void ATableGrowsAndLosesColumns()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        string[] headers = ["Region", "Qty", "Price"];
        for (int c = 0; c < 3; c++)
        {
            sheet.Cells[0, c].Value = headers[c];
            sheet.Cells[1, c].Value = c + 1;
            sheet.Cells[2, c].Value = c + 4;
        }

        SheetTable table = sheet.Tables.Add("A1:C3", "Sales");
        sheet["E1"].Formula = "SUM(Sales[Price])";
        sheet["E2"].Formula = "SUM(Sales[Qty])";

        sheet.InsertRows(2);
        Assert.Equal("A1:C4", table.Range.ToString());

        sheet.InsertColumns(1);
        Assert.Equal("A1:D4", table.Range.ToString());
        Assert.Equal(["Region", "Column1", "Qty", "Price"], table.Columns.Select(c => c.Name));
        Assert.Equal("Column1", sheet["B1"].Value.Text);

        sheet.DeleteColumns("C");
        Assert.Equal(["Region", "Column1", "Price"], table.Columns.Select(c => c.Name));
        Assert.Equal("SUM(Sales[Price])", sheet["E1"].Formula);
        Assert.Equal("SUM(#REF!)", sheet["E2"].Formula);

        sheet.DeleteColumns(0, 3);
        Assert.Empty(sheet.Tables);
        Assert.Equal("SUM(#REF!)", sheet["B1"].Formula);
    }

    [Fact]
    public void AHeaderRowIsNotTakenAlone()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["A1"].Value = "Name";
        sheet["A2"].Value = "x";
        sheet.Tables.Add("A1:A2");
        Assert.Throws<InvalidOperationException>(() => sheet.DeleteRows(0));
    }

    [Fact]
    public void RulesLinksAndNamesFollow()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        Worksheet other = book.Worksheets.Add("Other");
        sheet.DataValidations.Add(RangeAddress.Parse("B5:B9"), DataValidationType.WholeNumber, "A5", "$A$10");
        sheet.ConditionalFormats.Add("C5:C9", ConditionalFormatRule.Expression("C5>A$1", new CellStyle()));
        sheet.Hyperlinks.AddInternal(RangeAddress.Parse("D5"), "Sheet1!A8");
        other.Hyperlinks.AddInternal(RangeAddress.Parse("A1"), "Sheet1!A8");
        book.Names.Add("Picked", "Sheet1!$A$5:$A$9");

        sheet.InsertRows(0, 2);
        DataValidation rule = sheet.DataValidations.Single();
        Assert.Equal(("B7:B11", "A7", "$A$12"), (rule.Ranges.Single().ToString(), rule.Formula1, rule.Formula2));
        ConditionalFormat format = sheet.ConditionalFormats.Single();
        Assert.Equal(("C7:C11", "C7>A$3"), (format.Ranges.Single().ToString(), format.Rules.Single().Formulas.Single()));
        Assert.Equal(("D7", "Sheet1!A10"), (sheet.Hyperlinks.Single().Range.ToString(), sheet.Hyperlinks.Single().Location));
        Assert.Equal("Sheet1!A10", other.Hyperlinks.Single().Location);
        Assert.Equal("Sheet1!$A$7:$A$11", book.Names.Single(n => n.Name == "Picked").Formula);

        // Taking the rules' first row writes their formulas for the cell that comes first after it.
        sheet.DeleteRows(6);
        Assert.Equal(("B7:B10", "A7"), (rule.Ranges.Single().ToString(), rule.Formula1));
        Assert.Equal(("C7:C10", "C7>A$3"), (format.Ranges.Single().ToString(), format.Rules.Single().Formulas.Single()));
    }

    [Fact]
    public void AChartFollowsItsData()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int r = 0; r < 4; r++)
        {
            sheet.Cells[r, 0].Value = "C" + r;
            sheet.Cells[r, 1].Value = r;
        }

        SheetDrawing drawing = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("D2:J12"), RangeAddress.Parse("A1:B4"));
        sheet.InsertRows(0);
        Assert.Contains("$B$3:$B$5", drawing.Chart!.Series[0].Values.Reference, StringComparison.Ordinal);
        Assert.Equal(new CellAddress(2, 3), drawing.From.Cell);

        sheet.DeleteColumns(1);
        Assert.Contains("#REF!", drawing.Chart.Series[0].Values.Reference, StringComparison.Ordinal);
        Assert.Equal(new CellAddress(2, 2), drawing.From.Cell);
    }

    [Fact]
    public void NewRowsTakeTheFormattingAbove()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["B2"].Style = sheet["B2"].Style with { Font = CellFont.Default with { Bold = true } };
        sheet["B2"].Value = 1;
        sheet.InsertRows(2, 2);
        Assert.True(sheet["B3"].Style.Font.Bold);
        Assert.True(sheet["B4"].Style.Font.Bold);
        Assert.True(sheet["B3"].IsEmpty);
        Assert.False(sheet["B5"].Style.Font.Bold);
    }

    [Fact]
    public void FrozenRowsFollow()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet.View.FreezePanes(new CellAddress(3, 0));
        sheet.InsertRows(1);
        Assert.Equal(4, sheet.View.FrozenRows);
        sheet.DeleteRows(0, 2);
        Assert.Equal(2, sheet.View.FrozenRows);
    }
}
