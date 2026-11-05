namespace Egap.Spreadsheets;

/// <summary>When a workbook's formulas are calculated again after a change.</summary>
public enum CalculationMode
{
    /// <summary>After every change, the default.</summary>
    Automatic,

    /// <summary>After every change, except data tables, which wait for a full calculation.</summary>
    AutomaticExceptTables,

    /// <summary>Only when a calculation is asked for.</summary>
    Manual,
}

/// <summary>
/// How a workbook calculates: when, whether circular references iterate, and whether chains of formulas may be calculated in
/// parallel. These are the workbook's own settings, saved with it (the <c>calcPr</c> element, ECMA-376 Part 1, 18.2.2).
/// </summary>
/// <example>
/// <code>
/// Workbook book = Workbook.Create();
/// book.Calculation.Iterate = true;
/// book.Calculation.MaxIterations = 50;
/// book.Calculation.MaxChange = 0.0001;
/// </code>
/// </example>
public sealed class CalculationSettings
{
    /// <summary>The most iterations Excel allows.</summary>
    public const int IterationLimit = 32_767;

    /// <summary>When formulas are calculated again after a change. Automatic by default.</summary>
    public CalculationMode Mode { get; set; }

    /// <summary>
    /// Whether formulas in a circular reference are calculated over and over, up to <see cref="MaxIterations"/> times or until
    /// no value changes by more than <see cref="MaxChange"/>. When false, the default, a circular reference is reported and
    /// its cells keep the values they had.
    /// </summary>
    public bool Iterate { get; set; }

    /// <summary>The most times a circular reference is calculated when <see cref="Iterate"/> is on: 1 to 32,767, 100 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is under 1 or over 32,767.</exception>
    public int MaxIterations
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, IterationLimit);
            field = value;
        }
    } = 100;

    /// <summary>
    /// The change under which iteration stops: once no value in the circular reference moves by more than this, it has
    /// settled. 0.001 by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The change is negative, not a number or infinite.</exception>
    public double MaxChange
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The change must be zero or more.");
            }

            field = value;
        }
    } = 0.001;

    /// <summary>
    /// Whether results keep full precision, the default. When false, Excel's "precision as displayed" is on: a value is
    /// rounded to the number format of its cell. Kept and saved; the calculation does not apply it yet.
    /// </summary>
    public bool FullPrecision { get; set; } = true;

    /// <summary>Whether a full calculation may calculate independent chains of formulas in parallel, the default.</summary>
    public bool Concurrent { get; set; } = true;
}
