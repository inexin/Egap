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

    /// <summary>
    /// Each move of <c>tools/excel-move-overlap-oracle.ps1</c> (range&gt;cell), then probe=formula for each probe formula
    /// as Excel had it after the move.
    /// </summary>
    private static readonly string[][] Overlaps = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "move-overlap.excel.tsv")).Skip(1).Select(line => line.Split('	'))];

    public static TheoryData<int, int> OverlapCases()
    {
        TheoryData<int, int> cases = [];
        for (int move = 0; move < Overlaps.Length; move++)
        {
            for (int probe = 1; probe < Overlaps[move].Length; probe++)
            {
                cases.Add(move, probe);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(OverlapCases))]
    public void ARangeBesideOrUnderAShortMoveChangesAsInExcel(int move, int probe)
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int row = 0; row < 40; row++)
        {
            for (int column = 0; column < 11; column++)
            {
                sheet.Cells[row, column].Value = ((row + 1) * 100) + column + 1;
            }
        }

        string[] line = Overlaps[move];
        for (int each = 1; each < line.Length; each++)
        {
            sheet.Cells[18 + each, 13].Formula = line[each].Split('=')[0];
        }

        string[] paste = line[0].Split('>');
        sheet.Range(paste[0]).MoveTo(sheet[paste[1]]);
        Assert.Equal(line[probe].Split('=')[1], sheet.Cells[18 + probe, 13].Formula);
    }

    /// <summary>
    /// Each case of <c>tools/excel-merge-oracle.ps1</c>: the merges, Copy or Cut, range&gt;cell, whether Excel made the paste
    /// and the merges after.
    /// </summary>
    private static readonly string[][] Merges = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "merge.excel.tsv")).Skip(1).Select(line => line.Split('	'))];

    public static TheoryData<int> MergeCases()
    {
        TheoryData<int> cases = [];
        for (int each = 0; each < Merges.Length; each++)
        {
            cases.Add(each);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(MergeCases))]
    public void APasteAroundMergedCellsDoesWhatExcelDoes(int index)
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int row = 0; row < 30; row++)
        {
            for (int column = 0; column < 12; column++)
            {
                sheet.Cells[row, column].Value = ((row + 1) * 100) + column + 1;
            }
        }

        string[] line = Merges[index];
        foreach (string merge in line[0].Split(' '))
        {
            sheet.MergedCells.Add(RangeAddress.Parse(merge));
        }

        string[] paste = line[2].Split('>');
        CellRange range = sheet.Range(paste[0]);
        void Paste()
        {
            if (line[1] == "Cut")
            {
                range.MoveTo(sheet[paste[1]]);
            }
            else
            {
                range.CopyTo(sheet[paste[1]]);
            }
        }

        if (line[3] == "refused")
        {
            Assert.Throws<InvalidOperationException>(Paste);
        }
        else
        {
            Paste();
        }

        string[] expected = line.Length > 4 ? line[4].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        Assert.Equal(expected.Order(StringComparer.Ordinal), sheet.MergedCells.Select(m => m.ToString()).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Each case of <c>tools/excel-link-oracle.ps1</c>: the links (cell=address), Copy or Cut, range&gt;cell and the links
    /// after.
    /// </summary>
    private static readonly string[][] Links = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "link.excel.tsv")).Skip(1).Select(line => line.Split('	'))];

    public static TheoryData<int> LinkCases()
    {
        TheoryData<int> cases = [];
        for (int each = 0; each < Links.Length; each++)
        {
            cases.Add(each);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(LinkCases))]
    public void APasteOverLinksLeavesTheLinksExcelLeaves(int index)
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int row = 0; row < 10; row++)
        {
            for (int column = 0; column < 10; column++)
            {
                sheet.Cells[row, column].Value = ((row + 1) * 100) + column + 1;
            }
        }

        string[] line = Links[index];
        foreach (string link in line[0].Split(' '))
        {
            string[] parts = link.Split('=');
            sheet.Hyperlinks.Add(parts[0], parts[1]);
        }

        string[] paste = line[2].Split('>');
        if (line[1] == "Cut")
        {
            sheet.Range(paste[0]).MoveTo(sheet[paste[1]]);
        }
        else
        {
            sheet.Range(paste[0]).CopyTo(sheet[paste[1]]);
        }

        string[] expected = line.Length > 3 ? line[3].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        Assert.Equal(expected.Order(StringComparer.Ordinal), sheet.Hyperlinks.Select(l => l.Range + "=" + l.Address).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A filtered sheet: k, v and f over rows 2 to 10, k one in the odd rows and zero in the even ones, which the filter
    /// hides; f is v plus one. The expectations below are what Excel 16.0 build 20527 did with the same sheet.
    /// </summary>
    private static (Workbook Book, Worksheet Sheet) Filtered()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "k";
        sheet["B1"].Value = "v";
        sheet["C1"].Value = "f";
        for (int row = 2; row <= 10; row++)
        {
            sheet.Cells[row - 1, 0].Value = row % 2;
            sheet.Cells[row - 1, 1].Value = row * 100;
            sheet.Cells[row - 1, 2].Formula = $"B{row}+1";
            sheet.Rows[row - 1].Hidden = row % 2 == 0;
        }

        sheet.HasFilterCriteria = true;
        book.Calculate();
        return (book, sheet);
    }

    [Fact]
    public void ACopyOfAFilteredRangeTakesTheRowsItShowsAsExcelDoes()
    {
        (_, Worksheet sheet) = Filtered();
        sheet.Range("A1:C6").CopyTo(sheet["F1"]);
        Assert.Equal("k", sheet["F1"].Value.ToString());
        Assert.Equal(300, sheet["G2"].Value.Number);
        Assert.Null(sheet["H2"].Formula);
        Assert.Equal(301, sheet["H2"].Value.Number);
        Assert.Equal(501, sheet["H3"].Value.Number);
        Assert.Equal(CellValueKind.Empty, sheet["F4"].ValueKind);

        // One run keeps its formulas; a copy onto itself is refused; a cut takes every row.
        sheet.Range("A4:C6").CopyTo(sheet["J1"]);
        Assert.Equal("K1+1", sheet["L1"].Formula);
        Assert.Throws<InvalidOperationException>(() => sheet.Range("A1:C6").CopyTo(sheet["B3"]));
        sheet.Range("A1:C6").MoveTo(sheet["F11"]);
        Assert.Equal("G16+1", sheet["H16"].Formula);
    }

    [Fact]
    public void ACopyOverTheFirstCellOfASharedFormulaLeavesTheOthersTheirFormulas()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet sheet = book.Worksheets[0];
        Assert.Equal("B3*C3", sheet["D3"].Formula);

        sheet.Range("F6").CopyTo(sheet["D2"]);
        Assert.Null(sheet["D2"].Formula);
        Assert.Equal("B3*C3", sheet["D3"].Formula);
        Assert.Equal("B3*C3", Saved(book).Worksheets[0]["D3"].Formula);
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
