using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// AutoFit on 10,000 rows by 10 columns (100,000 cells) of text and numbers in Calibri 11: fitting every column, and fitting
/// every row.
/// </summary>
[MemoryDiagnoser]
public class AutoFitBenchmarks
{
    private const int Rows = 10000;
    private const int Columns = 10;

    private Worksheet sheet = null!;

    [GlobalSetup]
    public void Setup()
    {
        sheet = Workbook.Create().Worksheets[0];
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                if (column % 2 == 0)
                {
                    sheet.Cells.SetValue(row, column, "Item " + (row * 7 % 1000) + " of group " + column);
                }
                else
                {
                    sheet.Cells.SetValue(row, column, (row * 31.7) + column);
                }
            }
        }
    }

    [Benchmark(Description = "AutoFit 10 columns, 100k cells")]
    public void FitColumns() => sheet.Range(0, 0, Rows - 1, Columns - 1).AutoFitColumns();

    [Benchmark(Description = "AutoFit 10k rows, 100k cells")]
    public void FitRows() => sheet.Range(0, 0, Rows - 1, Columns - 1).AutoFitRows();
}
