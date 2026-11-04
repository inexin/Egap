using System.Globalization;

namespace Egap.Spreadsheets;

/// <summary>What kind of value a cell holds.</summary>
public enum CellValueKind
{
    /// <summary>No value: an empty cell, or a formula not calculated yet.</summary>
    Empty,

    /// <summary>A number. Dates and times are numbers too, shown as dates by their number format.</summary>
    Number,

    /// <summary>Text.</summary>
    Text,

    /// <summary>TRUE or FALSE.</summary>
    Boolean,

    /// <summary>An error value such as <c>#DIV/0!</c>.</summary>
    Error,
}

/// <summary>The error values a cell can hold, as Excel shows them.</summary>
public enum CellError
{
    /// <summary><c>#NULL!</c>: two ranges that do not meet.</summary>
    Null,

    /// <summary><c>#DIV/0!</c>: division by zero.</summary>
    DivisionByZero,

    /// <summary><c>#VALUE!</c>: a value of the wrong type.</summary>
    Value,

    /// <summary><c>#REF!</c>: a reference to a cell that is gone.</summary>
    Reference,

    /// <summary><c>#NAME?</c>: a name or function nobody knows.</summary>
    Name,

    /// <summary><c>#NUM!</c>: a number out of range.</summary>
    Number,

    /// <summary><c>#N/A</c>: a value that is not available.</summary>
    NotAvailable,

    /// <summary><c>#GETTING_DATA</c>: data still being fetched.</summary>
    GettingData,

    /// <summary><c>#SPILL!</c>: an array result with no room to spill.</summary>
    Spill,

    /// <summary><c>#CALC!</c>: a calculation the engine cannot do, such as an empty array.</summary>
    Calc,
}

/// <summary>
/// A cell's value: empty, a number, text, TRUE or FALSE, or an error. Numbers, text, booleans and errors convert to it
/// implicitly, so <c>cell.Value = 42</c> and <c>cell.Value = "Total"</c> work.
/// </summary>
public readonly struct CellValue : IEquatable<CellValue>
{
    /// <summary>The longest text a cell holds, 32,767 characters, as in Excel.</summary>
    public const int MaxTextLength = short.MaxValue;

    private static readonly string[] ErrorTexts =
        ["#NULL!", "#DIV/0!", "#VALUE!", "#REF!", "#NAME?", "#NUM!", "#N/A", "#GETTING_DATA", "#SPILL!", "#CALC!"];

    private readonly double number;
    private readonly string? text;

    private CellValue(CellValueKind kind, double number, string? text)
    {
        Kind = kind;
        this.number = number;
        this.text = text;
    }

    /// <summary>No value.</summary>
    public static CellValue Empty => default;

    /// <summary>What kind of value this is.</summary>
    public CellValueKind Kind { get; }

    /// <summary>Whether there is no value.</summary>
    public bool IsEmpty => Kind == CellValueKind.Empty;

    /// <summary>The number.</summary>
    /// <exception cref="InvalidOperationException">The value is not a number.</exception>
    public double Number => Kind == CellValueKind.Number ? number : throw WrongKind(CellValueKind.Number);

    /// <summary>The text.</summary>
    /// <exception cref="InvalidOperationException">The value is not text.</exception>
    public string Text => Kind == CellValueKind.Text ? text! : throw WrongKind(CellValueKind.Text);

    /// <summary>TRUE or FALSE.</summary>
    /// <exception cref="InvalidOperationException">The value is not a boolean.</exception>
    public bool Boolean => Kind == CellValueKind.Boolean ? number != 0 : throw WrongKind(CellValueKind.Boolean);

    /// <summary>The error.</summary>
    /// <exception cref="InvalidOperationException">The value is not an error.</exception>
    public CellError Error => Kind == CellValueKind.Error ? (CellError)(int)number : throw WrongKind(CellValueKind.Error);

    /// <summary>A number. Excel holds finite numbers only.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The number is NaN or infinite.</exception>
    public static CellValue FromNumber(double number)
    {
        return double.IsFinite(number)
            ? new CellValue(CellValueKind.Number, number == 0 ? 0 : number, null)
            : throw new ArgumentOutOfRangeException(nameof(number), number, "A cell holds finite numbers only.");
    }

    /// <summary>Text of up to 32,767 characters; <see langword="null"/> gives an empty value.</summary>
    /// <exception cref="ArgumentException">The text is longer than <see cref="MaxTextLength"/>.</exception>
    public static CellValue FromText(string? text)
    {
        if (text is null)
        {
            return Empty;
        }

        return text.Length <= MaxTextLength
            ? new CellValue(CellValueKind.Text, 0, text)
            : throw new ArgumentException($"A cell holds at most {MaxTextLength} characters of text.", nameof(text));
    }

    /// <summary>TRUE or FALSE.</summary>
    public static CellValue FromBoolean(bool value) => new(CellValueKind.Boolean, value ? 1 : 0, null);

    /// <summary>An error value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The error is not one of <see cref="CellError"/>'s.</exception>
    public static CellValue FromError(CellError error)
    {
        return (uint)error < (uint)ErrorTexts.Length
            ? new CellValue(CellValueKind.Error, (int)error, null)
            : throw new ArgumentOutOfRangeException(nameof(error), error, "Not a cell error.");
    }

    /// <summary>The error's text as Excel shows it, such as <c>#DIV/0!</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The error is not one of <see cref="CellError"/>'s.</exception>
    public static string ErrorText(CellError error)
    {
        return (uint)error < (uint)ErrorTexts.Length
            ? ErrorTexts[(int)error]
            : throw new ArgumentOutOfRangeException(nameof(error), error, "Not a cell error.");
    }

    /// <summary>Reads an error's text, such as <c>#N/A</c>, case ignored; false when the text is not an error.</summary>
    public static bool TryParseError(ReadOnlySpan<char> text, out CellError error)
    {
        for (int i = 0; i < ErrorTexts.Length; i++)
        {
            if (text.Equals(ErrorTexts[i], StringComparison.OrdinalIgnoreCase))
            {
                error = (CellError)i;
                return true;
            }
        }

        error = default;
        return false;
    }

    /// <summary>A number; see <see cref="FromNumber"/>.</summary>
    public static implicit operator CellValue(double number) => FromNumber(number);

    /// <summary>Text; see <see cref="FromText"/>.</summary>
    public static implicit operator CellValue(string? text) => FromText(text);

    /// <summary>TRUE or FALSE.</summary>
    public static implicit operator CellValue(bool value) => FromBoolean(value);

    /// <summary>An error value.</summary>
    public static implicit operator CellValue(CellError error) => FromError(error);

    /// <summary>Whether two values are the same kind and the same value; text compares by ordinal.</summary>
    public static bool operator ==(CellValue left, CellValue right) => left.Equals(right);

    /// <summary>Whether two values differ.</summary>
    public static bool operator !=(CellValue left, CellValue right) => !left.Equals(right);

    /// <summary>Whether <paramref name="other"/> is the same kind and the same value; text compares by ordinal.</summary>
    public bool Equals(CellValue other) =>
        Kind == other.Kind && number.Equals(other.number) && string.Equals(text, other.text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CellValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, number, text);

    /// <summary>
    /// The value as plain text: the number in invariant round-trip form, the text itself, <c>TRUE</c> or <c>FALSE</c>, the
    /// error's text, or an empty string. Number formats do not apply.
    /// </summary>
    public override string ToString() => Kind switch
    {
        CellValueKind.Number => number.ToString("R", CultureInfo.InvariantCulture),
        CellValueKind.Text => text!,
        CellValueKind.Boolean => number != 0 ? "TRUE" : "FALSE",
        CellValueKind.Error => ErrorTexts[(int)number],
        _ => string.Empty,
    };

    internal static CellValue NumberUnchecked(double number) => new(CellValueKind.Number, number, null);

    internal static CellValue TextUnchecked(string text) => new(CellValueKind.Text, 0, text);

    private InvalidOperationException WrongKind(CellValueKind wanted) =>
        new($"The value is {Kind}, not {wanted}.");
}
