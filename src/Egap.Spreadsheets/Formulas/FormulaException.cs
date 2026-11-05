namespace Egap.Spreadsheets;

/// <summary>A formula that does not follow Excel's grammar, with where in its text the problem is.</summary>
public sealed class FormulaException : FormatException
{
    /// <summary>A formula error with a message.</summary>
    public FormulaException(string message)
        : base(message)
    {
        Formula = "";
    }

    /// <summary>A formula error with a message and the error that caused it.</summary>
    public FormulaException(string message, Exception innerException)
        : base(message, innerException)
    {
        Formula = "";
    }

    /// <summary>A formula error at a position in a formula.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="formula">The formula, without its leading <c>=</c>.</param>
    /// <param name="position">The zero-based index in <paramref name="formula"/> where the problem is.</param>
    public FormulaException(string message, string formula, int position)
        : base($"{message} (at {position + 1} in \"{formula}\")")
    {
        Formula = formula;
        Position = position;
    }

    /// <summary>The formula, without its leading <c>=</c>; empty when the error is not tied to one.</summary>
    public string Formula { get; }

    /// <summary>The zero-based index in <see cref="Formula"/> where the problem is.</summary>
    public int Position { get; }
}
