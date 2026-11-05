using Egap.Spreadsheets.Recalculation;

namespace Egap.Spreadsheets.Tests;

/// <summary>The dependency graph and the plan runner, on graphs made by hand: what a change makes stale, and in what order.</summary>
public sealed class DependencyGraphTests
{
    private static CellKey Key(string cell, int sheet = 0)
    {
        CellAddress address = CellAddress.Parse(cell);
        return new CellKey(sheet, address.Row, address.Column);
    }

    private static Precedent Reads(string range, int sheet = 0) => new(sheet, RangeAddress.Parse(range));

    private static string[][] Steps(CalculationPlan plan) =>
        [.. plan.Steps.Select(s => s.Cells.Select(c => (s.Cycle ? "~" : "") + new CellAddress(c.Row, c.Column)).Order(StringComparer.Ordinal).ToArray())];

    [Fact]
    public void AChangeMakesItsReadersStaleInOrder()
    {
        DependencyGraph graph = new();
        graph.Set(Key("B1"), [Reads("A1")], isVolatile: false);
        graph.Set(Key("C1"), [Reads("B1"), Reads("A2")], isVolatile: false);
        graph.Set(Key("D1"), [Reads("C1:C1")], isVolatile: false);
        graph.Set(Key("E1"), [Reads("Z9")], isVolatile: false);
        graph.Plan();

        graph.Changed(Key("A1"));
        Assert.Equal([["B1"], ["C1"], ["D1"]], Steps(graph.Plan()));

        // Nothing noted since: nothing to do.
        Assert.False(graph.HasChanges);
        Assert.Equal(0, graph.Plan().Count);

        graph.Changed(Key("A2"));
        Assert.Equal([["C1"], ["D1"]], Steps(graph.Plan()));
    }

    [Fact]
    public void IndependentFormulasShareAStep()
    {
        DependencyGraph graph = new();
        graph.Set(Key("B1"), [Reads("A1")], isVolatile: false);
        graph.Set(Key("B2"), [Reads("A1")], isVolatile: false);
        graph.Set(Key("C1"), [Reads("B1:B2")], isVolatile: false);
        graph.Set(Key("C2"), [Reads("B2")], isVolatile: false);
        graph.Set(Key("D1"), [Reads("C1"), Reads("B1")], isVolatile: false);

        Assert.Equal([["B1", "B2"], ["C1", "C2"], ["D1"]], Steps(graph.Plan()));
    }

    [Fact]
    public void RangesOfEverySizeFindTheirReaders()
    {
        DependencyGraph graph = new();
        graph.Set(Key("Z1"), [Reads("A1:C3")], isVolatile: false);
        graph.Set(Key("Z2"), [Reads("A:A")], isVolatile: false);
        graph.Set(Key("Z3"), [Reads("5:5")], isVolatile: false);
        graph.Set(Key("Z4"), [Reads("A1", sheet: 1)], isVolatile: false);
        graph.Set(Key("Z5"), [Reads("B60:B70"), Reads("B65")], isVolatile: false);

        Assert.Equal(["Z1", "Z2"], graph.Dependents(Key("A1")).Select(c => new CellAddress(c.Row, c.Column).ToString()).Order());
        Assert.Equal(["Z2", "Z3"], graph.Dependents(Key("A5")).Select(c => new CellAddress(c.Row, c.Column).ToString()).Order());
        Assert.Equal(["Z4"], graph.Dependents(Key("A1", sheet: 1)).Select(c => new CellAddress(c.Row, c.Column).ToString()));
        Assert.Single(graph.Dependents(Key("B65")));
        Assert.Empty(graph.Dependents(Key("D4")));
    }

    [Fact]
    public void ReplacedAndRemovedFormulasForgetWhatTheyRead()
    {
        DependencyGraph graph = new();
        graph.Set(Key("B1"), [Reads("A1:A100")], isVolatile: false);
        graph.Set(Key("B1"), [Reads("C1")], isVolatile: false);
        Assert.Empty(graph.Dependents(Key("A50")));
        Assert.Single(graph.Dependents(Key("C1")));
        Assert.Equal(1, graph.Count);

        Assert.True(graph.Remove(Key("B1")));
        Assert.False(graph.Remove(Key("B1")));
        Assert.Empty(graph.Dependents(Key("C1")));
        Assert.Equal(0, graph.Count);
    }

    [Fact]
    public void VolatileFormulasRunInEveryPlan()
    {
        DependencyGraph graph = new();
        graph.Set(Key("A1"), [], isVolatile: true);
        graph.Set(Key("B1"), [Reads("A1")], isVolatile: false);
        graph.Set(Key("C1"), [Reads("Z1")], isVolatile: false);
        graph.Plan();

        Assert.True(graph.HasChanges);
        Assert.Equal([["A1"], ["B1"]], Steps(graph.Plan()));

        graph.Set(Key("A1"), [], isVolatile: false);
        graph.Plan();
        Assert.Equal(0, graph.Plan().Count);
    }

    [Fact]
    public void CyclesAreStepsOfTheirOwn()
    {
        DependencyGraph graph = new();
        graph.Set(Key("A1"), [Reads("C1")], isVolatile: false);
        graph.Set(Key("B1"), [Reads("A1")], isVolatile: false);
        graph.Set(Key("C1"), [Reads("B1"), Reads("Z1")], isVolatile: false);
        graph.Set(Key("D1"), [Reads("C1")], isVolatile: false);
        graph.Set(Key("E1"), [Reads("E1")], isVolatile: false);
        graph.Set(Key("F1"), [Reads("Z1")], isVolatile: false);

        string[][] steps = Steps(graph.Plan());

        Assert.Contains(["~A1", "~B1", "~C1"], steps);
        Assert.Contains(["~E1"], steps);
        int ring = Array.FindIndex(steps, s => s.SequenceEqual(["~A1", "~B1", "~C1"]));
        Assert.True(Array.FindIndex(steps, s => s.Contains("D1")) > ring);
    }

    [Fact]
    public void LongChainsDoNotOverflowTheStack()
    {
        const int Length = 200_000;
        DependencyGraph graph = new();
        for (int row = 1; row < Length; row++)
        {
            graph.Set(new CellKey(0, row, 0), [new Precedent(new CellKey(0, row - 1, 0))], isVolatile: false);
        }

        // A ring through the whole chain is one cycle.
        graph.Set(new CellKey(0, 0, 0), [new Precedent(new CellKey(0, Length - 1, 0))], isVolatile: false);
        CalculationPlan plan = graph.Plan();

        CalculationStep step = Assert.Single(plan.Steps);
        Assert.True(step.Cycle);
        Assert.Equal(Length, step.Cells.Length);
    }

    [Fact]
    public void PlansRunInOrderAndInParallel()
    {
        // B(n) = A(n) * 2 for 1,000 rows, then C1 sums them: the 1,000 run as one step, on threads.
        DependencyGraph graph = new();
        double[] a = [.. Enumerable.Range(1, 1000).Select(i => (double)i)];
        double[] b = new double[1000];
        double c = 0;
        for (int row = 0; row < 1000; row++)
        {
            graph.Set(new CellKey(0, row, 1), [new Precedent(new CellKey(0, row, 0))], isVolatile: false);
        }

        graph.Set(Key("C1"), [Reads("B1:B1000")], isVolatile: false);
        int threads = 0;
        HashSet<int> seen = [];
        CalculationOutcome outcome = Recalculator.Run(graph.Plan(), cell =>
        {
            lock (seen)
            {
                threads = seen.Add(Environment.CurrentManagedThreadId) ? threads + 1 : threads;
            }

            if (cell.Column == 1)
            {
                b[cell.Row] = a[cell.Row] * 2;
            }
            else
            {
                c = b.Sum();
            }

            return 0;
        }, new CalculationSettings(), parallel: true);

        Assert.Equal(1001, outcome.Calculated);
        Assert.Equal(1000 * 1001, c);
        Assert.Empty(outcome.Circular);
    }

    [Fact]
    public void CyclesIterateWhenAsked()
    {
        // A1 = (A1 + 10 / A1) / 2 settles on the square root of 10.
        DependencyGraph graph = new();
        graph.Set(Key("A1"), [Reads("A1")], isVolatile: false);
        CalculationPlan plan = graph.Plan();
        double value = 1;
        double Step(CellKey cell)
        {
            double next = (value + (10 / value)) / 2;
            double change = Math.Abs(next - value);
            value = next;
            return change;
        }

        CalculationSettings settings = new() { Iterate = false };
        CalculationOutcome off = Recalculator.Run(plan, Step, settings, parallel: false);
        Assert.Single(off.Circular);
        Assert.Equal(0, off.Calculated);
        Assert.Equal(1, value);

        settings.Iterate = true;
        settings.MaxChange = 1e-12;
        CalculationOutcome on = Recalculator.Run(plan, Step, settings, parallel: false);
        Assert.True(on.Settled);
        Assert.InRange(on.Iterations, 2, 10);
        Assert.Equal(Math.Sqrt(10), value, 12);

        // A ring that never settles stops at the pass limit.
        settings.MaxIterations = 7;
        CalculationOutcome restless = Recalculator.Run(plan, _ => 1, settings, parallel: false);
        Assert.False(restless.Settled);
        Assert.Equal(7, restless.Iterations);
        Assert.Equal(7, restless.Calculated);
    }
}
