using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Setting and getting cell values in the block store: a dense 1,000 rows by 100 columns (100,000 cells) by handle and by the
/// handle-free calls, text through the shared string table, styles over a range, and sparse cells spread over the sheet.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs instance methods.")]
public class CellStoreBenchmarks
{
    private const int Rows = 1000;
    private const int Columns = 100;

    private readonly string[] words = Enumerable.Range(0, 1000).Select(i => "word" + i).ToArray();
    private readonly (int Row, int Column)[] scattered = new (int, int)[10_000];
    private Worksheet filled = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(1);
        for (int i = 0; i < scattered.Length; i++)
        {
            scattered[i] = (random.Next(CellAddress.MaxRows), random.Next(CellAddress.MaxColumns));
        }

        filled = Workbook.Create().Worksheets[0];
        Fill(filled);
    }

    [Benchmark(Description = "Set 100k numbers, sheet[r, c].Value")]
    public Worksheet SetNumbersByHandle()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                sheet[row, column].Value = row + column;
            }
        }

        return sheet;
    }

    [Benchmark(Description = "Set 100k numbers, Cells.SetValue")]
    public Worksheet SetNumbers()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        Fill(sheet);
        return sheet;
    }

    [Benchmark(Description = "Get 100k numbers, Cells.GetValue")]
    public double GetNumbers()
    {
        WorksheetCells cells = filled.Cells;
        double sum = 0;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                sum += cells.GetValue(row, column).Number;
            }
        }

        return sum;
    }

    [Benchmark(Description = "Get 100k numbers, sheet[r, c].Value")]
    public double GetNumbersByHandle()
    {
        double sum = 0;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                sum += filled[row, column].Value.Number;
            }
        }

        return sum;
    }

    [Benchmark(Description = "Set 100k texts (1,000 distinct)")]
    public Worksheet SetTexts()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        WorksheetCells cells = sheet.Cells;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                cells.SetValue(row, column, words[(row * Columns + column) % words.Length]);
            }
        }

        return sheet;
    }

    [Benchmark(Description = "Set 10k scattered numbers")]
    public Worksheet SetScattered()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        WorksheetCells cells = sheet.Cells;
        foreach ((int row, int column) in scattered)
        {
            cells.SetValue(row, column, 1.0);
        }

        return sheet;
    }

    [Benchmark(Description = "Enumerate 100k cells")]
    public long EnumerateCells()
    {
        long count = 0;
        foreach (Cell cell in filled.Cells)
        {
            count += cell.Row;
        }

        return count;
    }

    [Benchmark(Description = "UpdateStyle on 100k cells")]
    public Worksheet UpdateStyle()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        sheet.Range(0, 0, Rows - 1, Columns - 1).UpdateStyle(style => style with { NumberFormat = "0.00" });
        return sheet;
    }

    private static void Fill(Worksheet sheet)
    {
        WorksheetCells cells = sheet.Cells;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                cells.SetValue(row, column, row + column);
            }
        }
    }
}
