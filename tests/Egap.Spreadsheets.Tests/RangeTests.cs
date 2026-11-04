namespace Egap.Spreadsheets.Tests;

/// <summary>Cell ranges: values, styles and clearing over many cells.</summary>
public sealed class RangeTests
{
    [Fact]
    public void ARangeSetsAndGetsValuesAsAnArray()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        CellRange range = sheet.Range("B2:C3");

        range.SetValues(new CellValue[,] { { 1, "two" }, { true, CellError.NotAvailable } });

        Assert.Equal(2, range.RowCount);
        Assert.Equal(2, range.ColumnCount);
        Assert.Equal("two", sheet["C2"].Value.Text);
        Assert.Equal(sheet["B3"], range[1, 0]);
        CellValue[,] values = sheet.Range("A1:C3").GetValues();
        Assert.True(values[0, 0].IsEmpty);
        Assert.Equal(1, values[1, 1].Number);
        Assert.Equal(CellError.NotAvailable, values[2, 2].Error);
        Assert.Throws<ArgumentException>(() => range.SetValues(new CellValue[3, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => range[2, 0]);
        Assert.Throws<InvalidOperationException>(() => sheet.Range("A:Z").GetValues());
        Assert.Equal("Sheet1!B2:C3", range.ToString());
    }

    [Fact]
    public void EnumeratingARangeGivesEveryCellAndUsedCellsOnlyTheFilledOnes()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet["B2"].Value = 1;
        sheet["Z100"].Value = 2;

        CellRange range = sheet.Range("A1:C3");
        Assert.Equal(["A1", "B1", "C1", "A2", "B2", "C2", "A3", "B3", "C3"], range.Select(cell => cell.Address.ToString()));
        Assert.Equal(["B2"], range.UsedCells.Select(cell => cell.Address.ToString()));
        Assert.Equal(["B2", "Z100"], sheet.Range("A:Z").UsedCells.Select(cell => cell.Address.ToString()));
        Assert.Equal(["Z100"], sheet.Range("C:AZ").UsedCells.Select(cell => cell.Address.ToString()));
    }

    [Fact]
    public void UpdateStyleRunsOncePerDistinctStyle()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.Range("A1:J10").SetValue(5);
        sheet.Range("A1:A10").SetStyle(CellStyle.Default with { NumberFormat = "0.0" });

        int calls = 0;
        sheet.Range("A1:J10").UpdateStyle(style =>
        {
            calls++;
            return style with { Font = style.Font with { Bold = true } };
        });

        Assert.Equal(2, calls);
        Assert.True(sheet["A1"].Style.Font.Bold);
        Assert.Equal("0.0", sheet["A5"].NumberFormat);
        Assert.True(sheet["J10"].Style.Font.Bold);
        Assert.Equal("General", sheet["J10"].NumberFormat);
        Assert.Equal(4, book.Styles.Count);
    }

    [Fact]
    public void ClearContentsKeepsStylesAndClearRemovesEverything()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        CellRange range = sheet.Range("A1:B2");
        range.SetValue("x");
        range.SetStyle(CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromRgb(255, 255, 0)) });
        sheet["C1"].Formula = "A1";

        sheet.Range("A1:C2").ClearContents();
        Assert.Equal(4, sheet.Cells.Count);
        Assert.True(sheet["A1"].IsEmpty);
        Assert.Equal(CellFillPattern.Solid, sheet["A1"].Style.Fill.Pattern);
        Assert.Equal(0, sheet.Formulas.Count);

        range.Clear();
        Assert.Equal(0, sheet.Cells.Count);
        Assert.Null(sheet.UsedRange);
    }

    [Fact]
    public void RangesCompareByAddress()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];

        Assert.Equal(sheet.Range("A1:B2"), sheet.Range("B2:A1"));
        Assert.Equal(sheet.Range("A1:B2"), sheet.Range(0, 0, 1, 1));
        Assert.NotEqual(sheet.Range("A1:B2"), sheet.Range("A1:B3"));
    }
}
