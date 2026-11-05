namespace Egap.Spreadsheets.Recalculation;

/// <summary>Calculates one formula cell and tells how far its value moved: 0 when it did not change, infinity when its kind did.</summary>
internal delegate double CellCalculation(CellKey cell);

/// <summary>What a run of a <see cref="CalculationPlan"/> did.</summary>
/// <param name="Calculated">How many formula calculations ran, each pass of an iteration counted.</param>
/// <param name="Circular">The cycles found; with iteration off their formulas kept the values they had.</param>
/// <param name="Iterations">The most passes any cycle took; 0 when there was none or iteration is off.</param>
/// <param name="Settled">Whether every iterated cycle settled under the largest change allowed before the pass limit.</param>
internal sealed record CalculationOutcome(long Calculated, IReadOnlyList<CellKey[]> Circular, int Iterations, bool Settled);

/// <summary>Runs a <see cref="CalculationPlan"/> step by step, in parallel where a step allows, iterating cycles when asked.</summary>
internal static class Recalculator
{
    /// <summary>A step with fewer formulas than this is calculated on the calling thread: handing out work costs more.</summary>
    public const int ParallelThreshold = 256;

    /// <summary>
    /// Calculates the plan's formulas through <paramref name="calculate"/>. Formulas of a step that is not a cycle read none of
    /// each other, so with <paramref name="parallel"/> on, a large step is spread over threads; <paramref name="calculate"/>
    /// must then be safe to call from several threads for different cells. A cycle is calculated in cell order, over and over
    /// up to <see cref="CalculationSettings.MaxIterations"/> times until no value moves by more than
    /// <see cref="CalculationSettings.MaxChange"/>, when <see cref="CalculationSettings.Iterate"/> is on, and left as it was
    /// when it is off.
    /// </summary>
    public static CalculationOutcome Run(CalculationPlan plan, CellCalculation calculate, CalculationSettings settings, bool parallel)
    {
        long calculated = 0;
        int iterations = 0;
        bool settled = true;
        List<CellKey[]> circular = [];
        foreach (CalculationStep step in plan.Steps)
        {
            if (step.Cycle)
            {
                CellKey[] ring = [.. step.Cells.OrderBy(c => c.Packed)];
                circular.Add(ring);
                if (!settings.Iterate)
                {
                    continue;
                }

                int passes = 0;
                double change;
                do
                {
                    change = 0;
                    foreach (CellKey cell in ring)
                    {
                        change = Math.Max(change, calculate(cell));
                    }

                    passes++;
                    calculated += ring.Length;
                }
                while (passes < settings.MaxIterations && !(change < settings.MaxChange));

                iterations = Math.Max(iterations, passes);
                settled &= change < settings.MaxChange;
                continue;
            }

            CellKey[] cells = step.Cells;
            if (parallel && cells.Length >= ParallelThreshold)
            {
                Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, cells.Length, ParallelThreshold / 4), range =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        calculate(cells[i]);
                    }
                });
            }
            else
            {
                foreach (CellKey cell in cells)
                {
                    calculate(cell);
                }
            }

            calculated += cells.Length;
        }

        return new CalculationOutcome(calculated, circular, iterations, settled);
    }
}
