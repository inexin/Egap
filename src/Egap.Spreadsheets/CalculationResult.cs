namespace Egap.Spreadsheets;

/// <summary>What a calculation of a workbook did: how many formulas it calculated and which circular references it met.</summary>
public sealed class CalculationResult
{
    internal CalculationResult(long calculated, IReadOnlyList<IReadOnlyList<Cell>> circularReferences, int iterations, bool converged)
    {
        Calculated = calculated;
        CircularReferences = circularReferences;
        Iterations = iterations;
        Converged = converged;
    }

    /// <summary>How many formula calculations ran; each pass over a circular reference counts its formulas again.</summary>
    public long Calculated { get; }

    /// <summary>
    /// The circular references met: each a list of the formula cells that read each other, directly or in a ring, in row
    /// order. With <see cref="CalculationSettings.Iterate"/> off, their formulas keep the values they had; Excel reports
    /// them the same way.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Cell>> CircularReferences { get; }

    /// <summary>The most passes any circular reference took when iterating; 0 when there was none or iteration is off.</summary>
    public int Iterations { get; }

    /// <summary>
    /// Whether every circular reference iterated settled: no value moved by more than
    /// <see cref="CalculationSettings.MaxChange"/> before <see cref="CalculationSettings.MaxIterations"/> passes ran out.
    /// </summary>
    public bool Converged { get; }
}
