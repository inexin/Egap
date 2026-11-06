using System.Globalization;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Formulas over a linked book's kept values against Excel: <c>Oracle/links.xlsx</c>, written by
/// <c>tools/make-links-oracle.py</c>, recalculated and compared with what Excel calculated from it with links not updated
/// (<c>Oracle/links.excel.tsv</c>, written by <c>tools/excel-links-oracle.ps1</c>).
/// </summary>
public sealed class LinkOracleTests
{
    [Fact]
    public void LinkedBookFormulasMatchExcel()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Oracle");
        Workbook book = Workbook.Load(Path.Combine(folder, "links.xlsx"));
        book.CalculateFull();
        Worksheet sheet = book.Worksheets[0];
        List<string> failures = [];
        foreach (string line in File.ReadAllLines(Path.Combine(folder, "links.excel.tsv")).Skip(1))
        {
            string[] fields = line.Split('\t');
            Cell cell = sheet[fields[0]];
            CellValue value = cell.Value;
            bool same = fields[1] switch
            {
                "number" => value.Kind == CellValueKind.Number && Math.Abs(value.Number - double.Parse(fields[2], CultureInfo.InvariantCulture)) <= 1e-12,
                "text" => value.Kind == CellValueKind.Text && value.Text == fields[2],
                "boolean" => value.Kind == CellValueKind.Boolean && value.Boolean == (fields[2] == "TRUE"),
                "error" => value.Kind == CellValueKind.Error && value.ToString() == fields[2],
                _ => value.Kind == CellValueKind.Empty,
            };
            if (!same)
            {
                failures.Add($"{cell.Formula}: Excel {fields[1]} {fields[2]}, Egap {value.Kind} {value}");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }
}
