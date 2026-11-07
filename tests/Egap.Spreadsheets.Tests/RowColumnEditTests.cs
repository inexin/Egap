using Egap.Documents.Charts;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Putting rows and columns into a sheet and taking them out, against Excel: where each formula in <c>Oracle/edits.txt</c>
/// goes, what it reads and what it shows after each edit, as <c>tools/excel-edit-oracle.ps1</c> recorded them in
/// <c>Oracle/edits.excel.tsv</c>.
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
    /// Each formula's sheet and text, then where Excel had it after each edit, its formula there and the text it showed. On
    /// sheet S the formulas go from H20 down beside numbers in A1:E30 (row times 10 plus column); on Other from A1 down.
    /// </summary>
    private static readonly string[][] Excel = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "edits.excel.tsv")).Skip(1).Select(line => line.Split('	'))];

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

    [Fact]
    public void AGoneIntersectionOrSpillIsWrittenAsExcelWritesIt()
    {
        // Excel 16.0: after deleting row 2 these read M2 #REF!, A1:C1 #REF! and #REF!# (stored ANCHORARRAY(#REF!)).
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["H1"].Formula = "M3 A2";
        sheet["H3"].Formula = "A1:C1 A2";
        sheet["H5"].Formula = "SEQUENCE(1)";
        sheet["H7"].Formula = "A2#";
        sheet.DeleteRows(1);
        book.Calculate();
        MemoryStream saved = new();
        book.Save(saved);
        saved.Position = 0;
        Worksheet reloaded = Workbook.Load(saved).Worksheets[0];
        foreach (Worksheet each in (Worksheet[])[sheet, reloaded])
        {
            Assert.Equal("M2 #REF!", each["H1"].Formula);
            Assert.Equal("A1:C1 #REF!", each["H2"].Formula);
            Assert.Equal(each == sheet ? "#REF!#" : "_xlfn.ANCHORARRAY(#REF!)", each["H6"].Formula);
            Assert.Equal("#REF! #REF! #REF!", $"{each["H1"].Text} {each["H2"].Text} {each["H6"].Text}");
        }
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

    [Theory]
    [InlineData("column", "A1:B3", "D1:D3", "F5:F8")]
    [InlineData("row", "A1:A4", "C1:C4", "E6:E9")]
    [InlineData("row at the start", "A1:A3", "C1:C3", "E6:E9")]
    public void RulesGrowOverRowsAndColumnsPutInJustAfterThem(string edit, string validation, string format, string moved)
    {
        // Excel 16.0: the new cells take the rules of the cells before them, as they take their formatting.
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.DataValidations.Add(RangeAddress.Parse("A1:A3"), DataValidationType.WholeNumber, "1", "9");
        sheet.ConditionalFormats.Add("C1:C3", ConditionalFormatRule.Expression("TRUE", new CellStyle()));
        sheet.DataValidations.Add(RangeAddress.Parse("E5:E8"), DataValidationType.WholeNumber, "1", "9");
        switch (edit)
        {
            case "column": sheet.InsertColumns(1); break;
            case "row": sheet.InsertRows(3); break;
            default: sheet.InsertRows(4); break;
        }

        Assert.Equal([validation, moved], sheet.DataValidations.Select(d => d.Ranges.Single().ToString()));
        Assert.Equal(format, sheet.ConditionalFormats.Single().Ranges.Single().ToString());
    }

    [Fact]
    public void ANameThatIsAUnionFollows()
    {
        // Print titles are a union without parentheses, as Excel stores them.
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        book.Names.Add("Titles", "Sheet1!$A:$F,Sheet1!$1:$2");
        sheet.InsertColumns(1);
        Assert.Equal("Sheet1!$A:$G,Sheet1!$1:$2", book.Names.Single().Formula);
        sheet.DeleteRows(0);
        Assert.Equal("Sheet1!$A:$G,Sheet1!$1:$1", book.Names.Single().Formula);
    }

    [Fact]
    public void AnEditThatWouldMoveAPivotTableIsRefused()
    {
        Workbook made = Workbook.Create();
        made.Worksheets[0]["A1"].Value = 1;
        MemoryStream file = new();
        made.Save(file);
        using (System.IO.Compression.ZipArchive zip = new(file, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            Write(zip, "xl/pivotTables/pivotTable1.xml", "<pivotTableDefinition xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" name=\"P\"><location ref=\"D3:E6\" firstHeaderRow=\"1\" firstDataRow=\"1\" firstDataCol=\"1\"/></pivotTableDefinition>");
            zip.GetEntry("xl/worksheets/_rels/sheet1.xml.rels")?.Delete();
            Write(zip, "xl/worksheets/_rels/sheet1.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdP\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/pivotTable\" Target=\"../pivotTables/pivotTable1.xml\"/></Relationships>");
        }

        file.Position = 0;
        Worksheet sheet = Workbook.Load(file).Worksheets[0];
        Assert.Throws<InvalidOperationException>(() => sheet.InsertRows(2));
        Assert.Throws<InvalidOperationException>(() => sheet.DeleteColumns(4));
        sheet.InsertRows(6);
        sheet.DeleteColumns(5);
        Assert.Equal(1, sheet["A1"].Value.Number);

        static void Write(System.IO.Compression.ZipArchive zip, string name, string xml)
        {
            using StreamWriter writer = new(zip.CreateEntry(name).Open());
            writer.Write(xml);
        }
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
