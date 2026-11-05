using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets.Recalculation;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// The dependency graph's baselines on 100,000 formulas: building it, planning after one change at the head of a chain,
/// planning a column of formulas that each read a cell and a running total of the column above (B(n) = A(n) + SUM(B$1:B(n-1))
/// read as ranges), and a full plan.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs instance methods.")]
public class DependencyGraphBenchmarks
{
    private const int Count = 100_000;

    private DependencyGraph chain = null!;
    private DependencyGraph columns = null!;

    [GlobalSetup]
    public void Setup()
    {
        chain = Chain();
        chain.Plan();
        columns = Columns();
        columns.Plan();
    }

    private static DependencyGraph Chain()
    {
        DependencyGraph graph = new();
        for (int row = 1; row < Count; row++)
        {
            graph.Set(new CellKey(0, row, 1), [new Precedent(new CellKey(0, row, 0)), new Precedent(new CellKey(0, row - 1, 1))], isVolatile: false);
        }

        return graph;
    }

    private static DependencyGraph Columns()
    {
        // C(n) = A(n) * B(n) for every row, and a total in E1 over C:C.
        DependencyGraph graph = new();
        for (int row = 0; row < Count; row++)
        {
            graph.Set(new CellKey(0, row, 2), [new Precedent(new CellKey(0, row, 0)), new Precedent(new CellKey(0, row, 1))], isVolatile: false);
        }

        graph.Set(new CellKey(0, 0, 4), [new Precedent(0, new RangeAddress(0, 2, CellAddress.MaxRows - 1, 2))], isVolatile: false);
        return graph;
    }

    [Benchmark(Description = "Build a graph of 100,000 formulas")]
    public int Build() => Chain().Count;

    [Benchmark(Description = "Plan a 100,000-formula chain after one change")]
    public int PlanChain()
    {
        chain.Changed(new CellKey(0, 1, 0));
        return chain.Plan().Count;
    }

    [Benchmark(Description = "Plan one row of 100,000 after one change")]
    public int PlanOneRow()
    {
        columns.Changed(new CellKey(0, Count / 2, 0));
        return columns.Plan().Count;
    }

    [Benchmark(Description = "Plan all 100,000 formulas")]
    public int PlanAll()
    {
        columns.ChangedAll();
        return columns.Plan().Count;
    }
}
