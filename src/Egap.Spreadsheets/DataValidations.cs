using System.Collections;
using System.Globalization;
using System.Xml.Linq;

using Egap.Spreadsheets.Formulas;

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

    /// <summary>The rule's formulas compiled, by their text, so checking many cells compiles each once.</summary>
    internal Dictionary<string, CompiledFormula?>? Compiled { get; set; }
}

/// <summary>A sheet's data validation rules.</summary>
public sealed class DataValidationCollection : IReadOnlyList<DataValidation>
{
    private readonly List<DataValidation> rules = [];
    private readonly Worksheet sheet;

    internal DataValidationCollection(Worksheet sheet)
    {
        this.sheet = sheet;
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

    /// <summary>
    /// Whether a cell's value passes the rule that applies to it, as Excel's Circle Invalid Data checks it; true when no rule
    /// applies. Formula cells are checked by their last results.
    /// </summary>
    public bool IsValid(CellAddress cell) => IsValid(cell, sheet.GetValue(cell.Row, cell.Column));

    /// <summary>
    /// Whether a value would pass the rule that applies to a cell, were it entered there, as Excel checks a value typed in;
    /// true when no rule applies. Numbers, dates and times pass by comparison with the rule's formulas, a whole number only
    /// when it has no fraction; a logical compares above every number, as in Excel; text and errors fail them. A list
    /// rule's fixed items match with case, its cells' values without. Text length counts the value as text. An empty value
    /// passes when the rule allows blanks.
    /// </summary>
    public bool IsValid(CellAddress cell, CellValue value) => Find(cell) is not { } rule || Check(rule, cell, value);

    /// <summary>The cells in the rules' ranges whose values do not pass their rules, as Excel's Circle Invalid Data marks them, by rows.</summary>
    public IReadOnlyList<CellAddress> FindInvalid()
    {
        List<CellAddress> invalid = [];
        HashSet<CellAddress> seen = [];
        foreach (DataValidation rule in rules)
        {
            foreach (RangeAddress range in rule.Ranges)
            {
                foreach ((int row, int column) in sheet.Store.Occupied(range))
                {
                    CellAddress cell = new(row, column);
                    if (sheet.GetKind(row, column) != CellValueKind.Empty && seen.Add(cell) && Find(cell) == rule && !Check(rule, cell, sheet.GetValue(row, column)))
                    {
                        invalid.Add(cell);
                    }
                }
            }
        }

        invalid.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));
        return invalid;
    }

    /// <summary>
    /// The items a list rule offers in a cell's drop-down: its fixed items, or the values of its range's cells; empty when no
    /// list rule applies.
    /// </summary>
    public IReadOnlyList<CellValue> ListItems(CellAddress cell) =>
        Find(cell) is { Type: DataValidationType.List } rule ? Items(rule, cell, out _) : [];

    /// <summary>Removes a rule.</summary>
    /// <returns>Whether the rule was on the sheet.</returns>
    public bool Remove(DataValidation rule) => rules.Remove(rule);

    /// <summary>Removes every rule.</summary>
    public void Clear() => rules.Clear();

    /// <inheritdoc/>
    public IEnumerator<DataValidation> GetEnumerator() => rules.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(DataValidation rule) => rules.Add(rule);

    private bool Check(DataValidation rule, CellAddress cell, CellValue value)
    {
        if (value.Kind == CellValueKind.Empty)
        {
            return rule.AllowBlank;
        }

        switch (rule.Type)
        {
            case DataValidationType.Any:
                return true;
            case DataValidationType.Custom:
                return Evaluate(rule.Formula1, rule, cell) is { } result && result.Kind switch
                {
                    CellValueKind.Boolean => result.Boolean,
                    CellValueKind.Number => result.Number != 0,
                    _ => false,
                };
            case DataValidationType.List:
                IReadOnlyList<CellValue> items = Items(rule, cell, out bool fixedItems);
                foreach (CellValue item in items)
                {
                    if (Same(item, value, fixedItems))
                    {
                        return true;
                    }
                }

                return false;
            case DataValidationType.TextLength:
                if (value.Kind == CellValueKind.Error || !ExcelValues.TryText(EvalValue.FromCell(value), sheet.Workbook.Culture, out string text, out _))
                {
                    return false;
                }

                return Compare(rule, cell, CellValue.FromNumber(text.Length));
            default:
                if (value.Kind is CellValueKind.Text or CellValueKind.Error)
                {
                    return false;
                }

                if (rule.Type == DataValidationType.WholeNumber && value.Kind == CellValueKind.Number && value.Number != Math.Floor(value.Number))
                {
                    return false;
                }

                return Compare(rule, cell, value);
        }
    }

    /// <summary>Whether a value compares with the rule's formulas as its operator asks; false when a formula gives no value.</summary>
    private bool Compare(DataValidation rule, CellAddress cell, CellValue value)
    {
        if (Evaluate(rule.Formula1, rule, cell) is not { } one || one.Kind is CellValueKind.Error or CellValueKind.Empty)
        {
            return false;
        }

        EvalContext context = new(sheet, cell.Row, cell.Column);
        EvalValue checkedValue = EvalValue.FromCell(value);
        int a = ExcelValues.Compare(checkedValue, EvalValue.FromCell(one), context);
        if (rule.Operator is ComparisonOperator.Between or ComparisonOperator.NotBetween)
        {
            if (Evaluate(rule.Formula2, rule, cell) is not { } two || two.Kind is CellValueKind.Error or CellValueKind.Empty)
            {
                return false;
            }

            int b = ExcelValues.Compare(checkedValue, EvalValue.FromCell(two), context);
            return (a >= 0 && b <= 0) == (rule.Operator == ComparisonOperator.Between);
        }

        return rule.Operator switch
        {
            ComparisonOperator.Equal => a == 0,
            ComparisonOperator.NotEqual => a != 0,
            ComparisonOperator.LessThan => a < 0,
            ComparisonOperator.LessThanOrEqual => a <= 0,
            ComparisonOperator.GreaterThan => a > 0,
            _ => a >= 0,
        };
    }

    /// <summary>A list rule's items: the quoted list's, split at commas, or the values of the cells its formula refers to.</summary>
    private List<CellValue> Items(DataValidation rule, CellAddress cell, out bool fixedItems)
    {
        string formula = (rule.Formula1 ?? "").Trim();
        if (formula.StartsWith('='))
        {
            formula = formula[1..];
        }

        fixedItems = formula.Length >= 2 && formula[0] == '"' && formula[^1] == '"';
        if (fixedItems)
        {
            return [.. formula[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal).Split(',').Select(item => CellValue.FromText(item))];
        }

        List<CellValue> items = [];
        if (formula.Length == 0 || Parse(formula, rule) is not { } compiled)
        {
            return items;
        }

        EvalContext context = Context(rule, cell);
        EvalValue result;
        try
        {
            result = FormulaEvaluator.Run(compiled, context);
        }
        catch (FormulaException)
        {
            return items;
        }

        if (result.Kind == EvalKind.Reference)
        {
            foreach (SheetArea area in result.Areas)
            {
                RangeAddress range = area.Range;
                if ((long)range.RowCount * range.ColumnCount > EvalContext.MaxArrayCells)
                {
                    continue;
                }

                for (int r = range.First.Row; r <= range.Last.Row; r++)
                {
                    for (int c = range.First.Column; c <= range.Last.Column; c++)
                    {
                        items.Add(area.Sheet.GetValue(r, c));
                    }
                }
            }
        }
        else if (result.Kind == EvalKind.Array)
        {
            items.AddRange(result.Array.Items.Select(item => item.ToCell()));
        }
        else
        {
            items.Add(result.ToCell());
        }

        return items;
    }

    /// <summary>Whether a list item is the value: fixed items as text with case, cells' values as Excel compares them.</summary>
    private bool Same(CellValue item, CellValue value, bool fixedItem)
    {
        if (fixedItem)
        {
            return value.Kind != CellValueKind.Error
                && ExcelValues.TryText(EvalValue.FromCell(value), sheet.Workbook.Culture, out string text, out _)
                && value.Kind == CellValueKind.Text
                && string.Equals(text, item.Text, StringComparison.Ordinal);
        }

        if (item.Kind != value.Kind || item.Kind is CellValueKind.Empty or CellValueKind.Error)
        {
            return false;
        }

        return ExcelValues.Compare(EvalValue.FromCell(item), EvalValue.FromCell(value), new EvalContext(sheet, 0, 0)) == 0;
    }

    /// <summary>A formula of the rule evaluated for a cell, its relative references counted from the top-left cell of the rule's first range.</summary>
    private CellValue? Evaluate(string? formula, DataValidation rule, CellAddress cell)
    {
        if (string.IsNullOrWhiteSpace(formula) || Parse(formula.StartsWith('=') ? formula[1..] : formula, rule) is not { } compiled)
        {
            return null;
        }

        EvalContext context = Context(rule, cell);
        try
        {
            return FormulaEvaluator.Result(FormulaEvaluator.Run(compiled, context), context);
        }
        catch (FormulaException)
        {
            return null;
        }
    }

    private EvalContext Context(DataValidation rule, CellAddress cell) =>
        EvalContext.ForCell(sheet, cell.Row, cell.Column, Origin(rule), [], arrayMode: true);

    private static CellAddress Origin(DataValidation rule) => rule.Ranges.Count > 0 ? rule.Ranges[0].First : default;

    private static CompiledFormula? Parse(string formula, DataValidation rule)
    {
        rule.Compiled ??= [];
        if (rule.Compiled.TryGetValue(formula, out CompiledFormula? known))
        {
            return known;
        }

        CompiledFormula? compiled;
        try
        {
            compiled = FormulaCompiler.Compile(FormulaParser.Parse(formula, r1c1: false, Origin(rule)));
        }
        catch (FormulaException)
        {
            compiled = null;
        }

        rule.Compiled[formula] = compiled;
        return compiled;
    }
}
