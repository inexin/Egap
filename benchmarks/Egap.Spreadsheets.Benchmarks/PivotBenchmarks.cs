using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Refreshing the eleven pivot tables of the Excel oracle book (every layout and function, page fields, hidden items and
/// dates) over a source grown to 10,000 rows of seven columns.
/// </summary>
[MemoryDiagnoser]
public class PivotBenchmarks
{
    private const int Rows = 10_000;

    private Workbook book = null!;

    [GlobalSetup]
    public void Setup()
    {
        book = Workbook.Load(Path.Combine(AppContext.BaseDirectory, "pivots.xlsx"));
        Worksheet data = book.Worksheets["Data"];
        string[] regions = ["North", "Central", "West", "South"];
        string[] reps = ["Ann", "Bob", "Cy", "Di", "Ed", "Flo", "Gus", "Hal"];
        string[] products = ["Apples", "Pears", "Plums", "Figs", "Kiwis", "Limes"];
        data.InsertRows(60, Rows - 60);
        for (int r = 1; r <= Rows; r++)
        {
            data[r, 0].Value = regions[r % 4];
            data[r, 1].Value = reps[r * 7 % 8];
            data[r, 2].Value = products[r * 5 % 6];
            data[r, 3].Value = 2020 + (r % 3);
            data[r, 4].Value = 44000 + (r % 400);
            data[r, 5].Value = (r % 9) + 2;
            data[r, 6].Value = r * 2.5 % 1000;
        }

        book.RefreshPivotTables();
    }

    [Benchmark(Description = "Refresh 11 pivot tables, 10k rows")]
    public int Refresh() => book.RefreshPivotTables();

    [Benchmark(Description = "Save refreshed pivot book")]
    public long Save()
    {
        using MemoryStream stream = new();
        book.Save(stream);
        return stream.Length;
    }

    [Benchmark(Description = "Print the compact pivot sheet to PDF")]
    public long Print()
    {
        using MemoryStream stream = new();
        book.Worksheets["Compact"].SaveAsPdf(stream);
        return stream.Length;
    }
}
