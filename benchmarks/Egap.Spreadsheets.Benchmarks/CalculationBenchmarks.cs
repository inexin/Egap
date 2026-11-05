using BenchmarkDotNet.Attributes;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Workbook calculation on 100,000 rows, C(n) = A(n) * B(n) + 1 with a total over column C: the first calculation (formulas
/// compiled and the graph built), a full calculation of a built workbook, and a calculation after one input changed.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs instance methods.")]
public class CalculationBenchmarks
{
    private const int Count = 100_000;

    private Workbook built = null!;
    private int round;

    [GlobalSetup]
    public void Setup()
    {
        built = Book();
        built.Calculate();
    }

    private static Workbook Book()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int row = 0; row < Count; row++)
        {
            sheet[row, 0].Value = row;
            sheet[row, 1].Value = 2;
            sheet[row, 2].Formula = $"A{row + 1}*B{row + 1}+1";
        }

        sheet["E1"].Formula = "SUM(C:C)";
        return book;
    }

    [Benchmark(Description = "First calculation of 100,001 formulas, setup included")]
    public long First() => Book().Calculate().Calculated;

    [Benchmark(Description = "Full calculation of 100,001 formulas")]
    public long Full() => built.CalculateFull().Calculated;

    [Benchmark(Description = "Calculation after one input changed")]
    public long OneChange()
    {
        built.Worksheets[0][500, 0].Value = ++round;
        return built.Calculate().Calculated;
    }
}
