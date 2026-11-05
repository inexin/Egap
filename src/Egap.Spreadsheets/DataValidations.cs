using System.Collections;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>What a data validation rule allows in its cells.</summary>
public enum DataValidationType
{
    /// <summary>Any value; the rule only shows its input message.</summary>
    Any,

    /// <summary>Whole numbers compared with the formulas.</summary>
    WholeNumber,

    /// <summary>Numbers compared with the formulas.</summary>
    DecimalNumber,

    /// <summary>One of a list: a quoted, comma-separated list such as <c>"Yes,No"</c>, or a range such as <c>$A$1:$A$9</c>.</summary>
    List,

    /// <summary>Dates compared with the formulas.</summary>
    Date,

    /// <summary>Times compared with the formulas.</summary>
    Time,

    /// <summary>Text whose length is compared with the formulas.</summary>
    TextLength,

    /// <summary>Values for which the formula is true.</summary>
    Custom,
}

/// <summary>How a value is compared with a rule's formulas.</summary>
public enum ComparisonOperator
{
    /// <summary>From the first formula to the second, both included.</summary>
    Between,

    /// <summary>Outside the first formula to the second.</summary>
    NotBetween,

    /// <summary>Equal to the first formula.</summary>
    Equal,

    /// <summary>Not equal to the first formula.</summary>
    NotEqual,

    /// <summary>Less than the first formula.</summary>
    LessThan,

    /// <summary>Less than or equal to the first formula.</summary>
    LessThanOrEqual,

    /// <summary>Greater than the first formula.</summary>
    GreaterThan,

    /// <summary>Greater than or equal to the first formula.</summary>
    GreaterThanOrEqual,
}

/// <summary>What Excel does with a value a rule does not allow.</summary>
public enum DataValidationErrorStyle
{
    /// <summary>Refuses the value.</summary>
    Stop,

    /// <summary>Warns, and keeps the value if the user says so.</summary>
    Warning,

    /// <summary>Tells the user, and keeps the value.</summary>
    Information,
}

/// <summary>
/// A data validation rule: what its cells allow, the drop-down list and the messages Excel shows. Formulas are written
/// without the leading <c>=</c>, in English, as in a cell's formula.
/// </summary>
public sealed class DataValidation
{
    internal DataValidation(IEnumerable<RangeAddress> ranges, DataValidationType type)
    {
        Ranges = [.. ranges];
        Type = type;
    }

    /// <summary>The cells the rule applies to.</summary>
    public List<RangeAddress> Ranges { get; }

    /// <summary>What the cells allow.</summary>
    public DataValidationType Type { get; set; }

    /// <summary>How values are compared with the formulas, for numbers, dates, times and text lengths.</summary>
    public ComparisonOperator Operator { get; set; }

    /// <summary>The first formula: the list, the custom formula, or the value compared with; null for none.</summary>
    public string? Formula1 { get; set; }

    /// <summary>The second formula, the upper end of <see cref="ComparisonOperator.Between"/>; null for none.</summary>
    public string? Formula2 { get; set; }

    /// <summary>Whether an empty cell is allowed. Default true, as Excel makes new rules.</summary>
    public bool AllowBlank { get; set; } = true;

    /// <summary>Whether a list rule shows its drop-down arrow in the cell. Default true.</summary>
    public bool ShowDropDown { get; set; } = true;

    /// <summary>Whether the input message shows when a cell is selected. Default true.</summary>
    public bool ShowInputMessage { get; set; } = true;

    /// <summary>Whether the error message shows for a value the rule does not allow. Default true.</summary>
    public bool ShowErrorMessage { get; set; } = true;

    /// <summary>What Excel does with a value the rule does not allow.</summary>
    public DataValidationErrorStyle ErrorStyle { get; set; }

    /// <summary>The input message's title, or null.</summary>
    public string? PromptTitle { get; set; }

    /// <summary>The input message, or null.</summary>
    public string? Prompt { get; set; }

    /// <summary>The error message's title, or null.</summary>
    public string? ErrorTitle { get; set; }

    /// <summary>The error message, or null for Excel's own.</summary>
    public string? Error { get; set; }

    /// <summary>The file's dataValidation element, for the attributes and children the model does not hold.</summary>
    internal XElement? Source { get; set; }
}

/// <summary>A sheet's data validation rules.</summary>
public sealed class DataValidationCollection : IReadOnlyList<DataValidation>
{
    private readonly List<DataValidation> rules = [];

    internal DataValidationCollection()
    {
    }

    /// <summary>How many rules there are.</summary>
    public int Count => rules.Count;

    /// <summary>The rule at a position.</summary>
    public DataValidation this[int index] => rules[index];

    /// <summary>The dataValidations element's own attributes in the file, written back.</summary>
    internal XElement? Source { get; set; }

    /// <summary>Adds a rule for a range, with up to two formulas.</summary>
    public DataValidation Add(RangeAddress range, DataValidationType type, string? formula1 = null, string? formula2 = null)
    {
        DataValidation rule = new([range], type) { Formula1 = formula1, Formula2 = formula2 };
        rules.Add(rule);
        return rule;
    }

    /// <summary>Adds a drop-down list of fixed items for a range.</summary>
    /// <exception cref="ArgumentException">There are no items, an item holds a comma or a quote, or the list is longer than Excel's 255 characters.</exception>
    public DataValidation AddList(RangeAddress range, IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        string[] list = [.. items];
        if (list.Length == 0 || Array.Exists(list, item => item.Contains(',', StringComparison.Ordinal) || item.Contains('"', StringComparison.Ordinal)))
        {
            throw new ArgumentException("A list holds at least one item, and items hold no commas or quotes.", nameof(items));
        }

        string joined = string.Join(",", list);
        if (joined.Length > 255)
        {
            throw new ArgumentException("Excel takes a list of at most 255 characters; put longer lists in cells and give their range.", nameof(items));
        }

        return Add(range, DataValidationType.List, "\"" + joined + "\"");
    }

    /// <summary>The first rule that applies to a cell, or null.</summary>
    public DataValidation? Find(CellAddress cell) => rules.Find(rule => rule.Ranges.Exists(range => range.Contains(cell)));

    /// <summary>Removes a rule.</summary>
    /// <returns>Whether the rule was on the sheet.</returns>
    public bool Remove(DataValidation rule) => rules.Remove(rule);

    /// <summary>Removes every rule.</summary>
    public void Clear() => rules.Clear();

    /// <inheritdoc/>
    public IEnumerator<DataValidation> GetEnumerator() => rules.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(DataValidation rule) => rules.Add(rule);
}
