using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// GETPIVOTDATA on the pivot tables of <c>Oracle/pivots.xlsx</c>, against Excel: <c>tools/excel-formula-sheet-oracle.ps1</c>
/// writes the formulas of <c>Oracle/getpivotdata.txt</c> into a sheet and saves the workbook with Excel's values as
/// <c>Oracle/getpivotdata.xlsx</c>; the test calculates them again and compares.
/// </summary>
public sealed class GetPivotDataOracleTests
{
    [Fact]
    public void GetPivotDataMatchesExcel()
    {
        Workbook book = Workbook.Load(Path.Combine(AppContext.BaseDirectory, "Oracle", "getpivotdata.xlsx"));
        Worksheet sheet = book.Worksheets["Formulas"];
        List<(Cell Cell, CellValue Saved)> formulas = [.. sheet.UsedRange!.Value.UsedCells.Where(c => c.Formula is not null).Select(c => (c, c.Value))];
        book.CalculateFull();
        StringBuilder failures = new();
        foreach ((Cell cell, CellValue saved) in formulas)
        {
            if (!cell.Value.Equals(saved))
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{cell.Address} {cell.Formula}: Excel {saved}, Egap {cell.Value}");
            }
        }

        Assert.True(formulas.Count > 90, $"only {formulas.Count} formulas");
        Assert.True(failures.Length == 0, failures.ToString());
    }
}
