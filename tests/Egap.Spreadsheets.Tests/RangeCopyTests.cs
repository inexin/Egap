namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Copying and moving ranges, against Excel: where each formula in <c>Oracle/edits.txt</c> goes, what it reads and what it
/// shows after each copy and move, as <c>tools/excel-copy-oracle.ps1</c> recorded them in <c>Oracle/copy.excel.tsv</c>.
/// </summary>
public sealed class RangeCopyTests
{
    /// <summary>The copies of <see cref="Excel"/>, in its order: rows and columns on, and whether the copy goes to Other.</summary>
    private static readonly (int Rows, int Columns, bool Other)[] Copies = [(0, 3, false), (10, 0, false), (-19, 0, false), (0, 0, true)];

    /// <summary>The moves of <see cref="Excel"/>, in its order: the range on S and where its first cell goes.</summary>
    private static readonly (string Range, string Sheet, string To)[] Moves =
    [
        ("A5:A6", "S", "C40"), ("A4:A8", "S", "G2"), ("A5", "S", "A6"), ("H20:H30", "S", "J50"), ("A5:A6", "Other", "B2"),
        ("A5:A6", "S", "A40"), ("E5", "S", "F5"), ("B5", "S", "B6"), ("A7:A8", "S", "A3"),
        ("H20:H30", "Other", "J50"),
    ];

    /// <summary>
    /// Each formula's sheet and text, then where Excel had it after each copy (S formulas only) and each move, its formula
    /// there and the text it showed. On sheet S the formulas go from H20 down beside numbers in A1:E30 (row times 10 plus
    /// column); on Other from A1 down.
    /// </summary>
    private static readonly string[][] Excel = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "copy.excel.tsv")).Skip(1).Select(line => line.Split('	'))];

    public static TheoryData<int, int> CopyCases()
    {
        TheoryData<int, int> cases = [];
        for (int formula = 0; formula < Excel.Length; formula++)
        {
            for (int copy = 0; copy < Copies.Length; copy++)
            {
                if (Excel[formula][0] == "S")
                {
                    cases.Add(formula, copy);
                }
            }
        }

        return cases;
    }

    public static TheoryData<int, int> MoveCases()
    {
        TheoryData<int, int> cases = [];
        for (int formula = 0; formula < Excel.Length; formula++)
        {
            for (int move = 0; move < Moves.Length; move++)
            {
                cases.Add(formula, move);
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

    private static void Check(Worksheet home, string recorded)
    {
        string[] expected = recorded.Split(' ', 2);
        string[] answer = expected[1].Split(" = ", 2);
        string place = expected[0];
        Worksheet sheet = home;
        if (place.Contains('!', StringComparison.Ordinal))
        {
            sheet = home.Workbook.Worksheets[place[..place.IndexOf('!', StringComparison.Ordinal)]];
            place = place[(place.IndexOf('!', StringComparison.Ordinal) + 1)..];
        }

        Cell cell = sheet[place];
        Assert.Equal(answer[0], cell.Formula?.Replace("'S'!", "S!", StringComparison.Ordinal));
        Assert.Equal(answer[1], cell.Text);
    }

    [Theory]
    [MemberData(nameof(CopyCases))]
    public void ACopiedFormulaIsWrittenAsInExcel(int formula, int copy)
    {
        (Workbook book, Worksheet s, Worksheet other) = Book();
        string[] row = Excel[formula];
        Cell from = s.Cells[19 + formula, 7];
        from.Formula = row[1];
        (int rows, int columns, bool toOther) = Copies[copy];
        Cell to = toOther ? other.Cells[from.Row, from.Column] : s.Cells[from.Row + rows, from.Column + columns];
        s.Range(from.Address.ToString()).CopyTo(to);
        book.Calculate();
        Check(toOther ? other : s, row[2 + copy]);
    }

    [Theory]
    [MemberData(nameof(MoveCases))]
    public void AFormulaFollowsAMoveAsInExcel(int formula, int move)
    {
        (Workbook book, Worksheet s, Worksheet other) = Book();
        string[] row = Excel[formula];
        int onS = 0, onOther = 0;
        foreach (string[] each in Excel)
        {
            Cell target = each[0] == "S" ? s.Cells[19 + onS++, 7] : other.Cells[onOther++, 0];
            target.Formula = each[1];
        }

        (string range, string sheet, string at) = Moves[move];
        s.Range(range).MoveTo(book.Worksheets[sheet][at]);
        book.Calculate();
        Check(row[0] == "S" ? s : other, row[2 + Copies.Length + move]);
    }

    private static Workbook Saved(Workbook book)
    {
        MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Workbook.Load(stream);
    }

    private static readonly CellStyle Bold = CellStyle.Default with { Font = CellStyle.Default.Font with { Bold = true } };

    [Fact]
    public void ACopyTakesValuesStylesMergesNotesAndLinks()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "head";
        sheet["A1"].Style = Bold;
        sheet["B2"].Value = 2;
        sheet["B3"].Formula = "B2*2";
        sheet.MergedCells.Add(RangeAddress.Parse("A1:B1"));
        sheet.Comments.Add("B2", "two", "me");
        sheet.Hyperlinks.Add("A1", "https://example.com/");
        sheet["E5"].Value = "old";
        sheet.Comments.Add("E5", "goes");

        sheet.Range("A1:B3").CopyTo(sheet["D5"]);
        book.Calculate();

        foreach (Worksheet each in (Worksheet[])[sheet, Saved(book).Worksheets[0]])
        {
            Assert.Equal("head", each["D5"].Text);
            Assert.True(each["D5"].Style.Font.Bold);
            Assert.True(each["E5"].IsEmpty);
            Assert.Equal("E6*2", each["E7"].Formula);
            Assert.Equal("4", each["E7"].Text);
            Assert.Contains(RangeAddress.Parse("D5:E5"), each.MergedCells);
            Assert.Contains(RangeAddress.Parse("A1:B1"), each.MergedCells);
            Assert.Equal(["B2 two", "E6 two"], each.Comments.Select(c => $"{c.Cell} {c.Text}").Order());
            Assert.Equal("https://example.com/", each.Hyperlinks.Find(CellAddress.Parse("D5"))?.Address);
            Assert.Equal("B2*2", each["B3"].Formula);
        }
    }

    [Fact]
    public void ACopyOverItselfReadsTheCellsAsTheyWere()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int r = 0; r < 4; r++)
        {
            sheet.Cells[r, 0].Value = r + 1;
        }

        sheet.Range("A1:A4").CopyTo(sheet["A3"]);
        Assert.Equal("1 2 1 2 3 4", string.Join(' ', Enumerable.Range(0, 6).Select(r => sheet.Cells[r, 0].Text)));
    }

    [Fact]
    public void ACopiedRuleMovesWithTheCellsAndTheOldRuleThereGoes()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        DataValidation rule = sheet.DataValidations.Add(RangeAddress.Parse("B2:B5"), DataValidationType.WholeNumber, "A2", "A2+10");
        rule.Operator = ComparisonOperator.Between;
        sheet.DataValidations.Add(RangeAddress.Parse("D1:D20"), DataValidationType.DecimalNumber, "0", "1");
        sheet.ConditionalFormats.Add("B2:B5", ConditionalFormatRule.Expression("B2>A2", Bold));

        sheet.Range("B3:B4").CopyTo(sheet["D10"]);

        foreach (Worksheet each in (Worksheet[])[sheet, Saved(book).Worksheets[0]])
        {
            DataValidation copied = each.DataValidations.Find(CellAddress.Parse("D10"))!;
            Assert.Equal(DataValidationType.WholeNumber, copied.Type);
            Assert.Equal("D10:D11", string.Join(' ', copied.Ranges));
            Assert.Equal("C10", copied.Formula1);
            Assert.Equal("C10+10", copied.Formula2);
            Assert.Equal("D1:D9 D12:D20", string.Join(' ', each.DataValidations.Find(CellAddress.Parse("D1"))!.Ranges));
            Assert.Equal("B2:B5", string.Join(' ', each.DataValidations.Find(CellAddress.Parse("B2"))!.Ranges));
            ConditionalFormat format = each.ConditionalFormats.Single(f => f.Ranges.Exists(r => r.Contains(CellAddress.Parse("D10"))));
            Assert.Equal("D10>C10", format.Rules[0].Formulas[0]);
        }
    }

    [Fact]
    public void AMovedRuleKeepsToTheCellsItNamed()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        Worksheet other = book.Worksheets.Add("Other");
        sheet.DataValidations.Add(RangeAddress.Parse("B2:B5"), DataValidationType.WholeNumber, "A2");
        sheet.ConditionalFormats.Add("B2:B5", ConditionalFormatRule.Expression("B2>A2", Bold));
        other.DataValidations.Add(RangeAddress.Parse("C1:C9"), DataValidationType.DecimalNumber, "1");

        sheet.Range("B3:B4").MoveTo(other["C4"]);

        foreach (Workbook each in (Workbook[])[book, Saved(book)])
        {
            Worksheet s = each.Worksheets[0];
            Worksheet o = each.Worksheets["Other"];
            DataValidation kept = s.DataValidations.Find(CellAddress.Parse("B2"))!;
            Assert.Equal("B2 B5", string.Join(' ', kept.Ranges));
            Assert.Equal("A2", kept.Formula1);
            Assert.Null(s.DataValidations.Find(CellAddress.Parse("B3")));
            DataValidation moved = o.DataValidations.Find(CellAddress.Parse("C4"))!;
            Assert.Equal(DataValidationType.WholeNumber, moved.Type);
            Assert.Equal("C4:C5", string.Join(' ', moved.Ranges));
            Assert.Equal("Sheet1!A3", moved.Formula1);
            Assert.Equal("C1:C3 C6:C9", string.Join(' ', o.DataValidations.Find(CellAddress.Parse("C1"))!.Ranges));
            Assert.Equal("Other!C4>Sheet1!A3", o.ConditionalFormats.Single().Rules[0].Formulas[0]);
            Assert.Equal("B2>A2", s.ConditionalFormats.Single().Rules[0].Formulas[0]);
        }
    }

    [Fact]
    public void AMoveTakesNotesLinksMergesAndATable()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "Name";
        sheet["B1"].Value = "Amount";
        sheet["A2"].Value = "x";
        sheet["B2"].Value = 5;
        sheet.Tables.Add("A1:B2", "Sales");
        sheet["D1"].Formula = "SUM(Sales[Amount])+B2";
        sheet.Comments.Add("A2", "note");
        sheet.Hyperlinks.Add("B2", "https://example.com/");
        sheet.MergedCells.Add(RangeAddress.Parse("A4:B4"));

        sheet.Range("A1:B4").MoveTo(sheet["F10"]);
        book.Calculate();

        foreach (Worksheet each in (Worksheet[])[sheet, Saved(book).Worksheets[0]])
        {
            Assert.Equal("F10:G11", each.Tables["Sales"].Range.ToString());
            Assert.Equal("SUM(Sales[Amount])+G11", each["D1"].Formula);
            Assert.Equal("10", each["D1"].Text);
            Assert.Equal("F11", each.Comments.Single().Cell.ToString());
            Assert.Equal("G11", each.Hyperlinks.Single().Range.ToString());
            Assert.Equal("F13:G13", each.MergedCells.Single().ToString());
            Assert.True(each["A1"].IsEmpty);
        }
    }

    [Fact]
    public void AMoveToAnotherSheetNamesTheSheetTheFormulasLeft()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        Worksheet other = book.Worksheets.Add("My Sheet");
        sheet["A1"].Value = 3;
        sheet["B1"].Formula = "A1*2+SUM(E:E)";
        sheet["C5"].Formula = "B1";
        other["A1"].Formula = "Sheet1!B1";

        sheet.Range("B1").MoveTo(other["D4"]);
        book.Calculate();

        Assert.Equal("Sheet1!A1*2+SUM(Sheet1!E:E)", other["D4"].Formula);
        Assert.Equal("'My Sheet'!D4", sheet["C5"].Formula);
        Assert.Equal("'My Sheet'!D4", other["A1"].Formula);
        Assert.Equal("6 6 6", $"{other["D4"].Text} {sheet["C5"].Text} {other["A1"].Text}");
    }

    [Fact]
    public void ANameFollowsAMove()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        book.Names.Add("Rate", "Sheet1!$B$2");
        book.Names.Add("Both", "Sheet1!$B$2,Sheet1!$A$9");
        sheet.Range("B2").MoveTo(sheet["E7"]);
        Assert.Equal("Sheet1!$E$7", book.Names["Rate"].Formula);
        Assert.Equal("Sheet1!$E$7,Sheet1!$A$9", book.Names["Both"].Formula);
    }

    [Fact]
    public void ACopyOrMoveExcelRefusesIsRefused()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        Worksheet other = Workbook.Create().Worksheets[0];
        sheet.Range("A1:A2").SetArrayFormula("B1:B2*2");
        sheet.MergedCells.Add(RangeAddress.Parse("D1:E1"));
        sheet["G1"].Value = "Name";
        sheet["G2"].Value = 1;
        sheet.Tables.Add("G1:G2", "T");
        Worksheet second = book.Worksheets.Add("Two");

        Assert.Throws<InvalidOperationException>(() => sheet.Range("A1").CopyTo(sheet["C5"]));
        Assert.Throws<InvalidOperationException>(() => sheet.Range("C5").CopyTo(sheet["A2"]));
        Assert.Throws<InvalidOperationException>(() => sheet.Range("D1").MoveTo(sheet["C5"]));
        Assert.Throws<InvalidOperationException>(() => sheet.Range("C5").CopyTo(sheet["E1"]));
        Assert.Throws<InvalidOperationException>(() => sheet.Range("G2").MoveTo(sheet["K5"]));
        Assert.Throws<InvalidOperationException>(() => sheet.Range("G1:G2").MoveTo(second["A1"]));
        Assert.Throws<ArgumentException>(() => sheet.Range("C5").CopyTo(other["A1"]));
        Assert.Throws<ArgumentException>(() => sheet.Range("C5:C6").MoveTo(sheet.Cells[CellAddress.MaxRows - 1, 0]));

        sheet.Range("A1:A2").MoveTo(sheet["J1"]);
        Assert.Equal("J1:J2", sheet["J1"].ArrayRange?.Address.ToString());
        sheet.Range("D1:E1").CopyTo(sheet["D9"]);
        Assert.Contains(RangeAddress.Parse("D9:E9"), sheet.MergedCells);
    }

    [Fact]
    public void TheGuideSampleDoesWhatItSays()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = 10;
        sheet["A2"].Value = 20;
        sheet["B1"].Formula = "A1*2";
        sheet["C1"].Formula = "B1+$A$2";

        sheet.Range("B1").CopyTo(sheet["B2"]);
        Assert.Equal("A2*2", sheet["B2"].Formula);
        sheet.Range("A1:A2").MoveTo(sheet["E5"]);
        Assert.Equal("E5*2 E6*2 B1+$E$6", $"{sheet["B1"].Formula} {sheet["B2"].Formula} {sheet["C1"].Formula}");
        Worksheet totals = book.Worksheets.Add("Totals");
        sheet.Range("C1").MoveTo(totals["A1"]);
        book.Calculate();
        Assert.Equal("Sheet1!B1+Sheet1!$E$6", totals["A1"].Formula);
        Assert.Equal("40", totals["A1"].Text);
    }
}
