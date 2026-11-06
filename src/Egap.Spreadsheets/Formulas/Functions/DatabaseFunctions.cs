namespace Egap.Spreadsheets.Formulas;

/// <summary>The D functions: aggregates over a table's rows that meet a criteria range.</summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> DatabaseFunctions() =>
    [
        new("DAVERAGE", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, n => n.Count == 0 ? Error(CellError.DivisionByZero) : Number(n.Sum() / n.Count))),
        new("DCOUNT", 3, 3, Kinds.Any, (a, c) => Database(a, c, true, values => Number(values.Count(v => v.Kind == EvalKind.Number)))),
        new("DCOUNTA", 3, 3, Kinds.Any, (a, c) => Database(a, c, true, values => Number(values.Count(v => v.Kind != EvalKind.Blank)))),
        new("DGET", 3, 3, Kinds.Any, (a, c) => Database(a, c, false, DatabaseGet)),
        new("DMAX", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, n => Number(n.Count == 0 ? 0 : n.Max()))),
        new("DMIN", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, n => Number(n.Count == 0 ? 0 : n.Min()))),
        new("DPRODUCT", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, DatabaseProduct)),
        new("DSTDEV", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, n => DatabaseSpread(n, sample: true, root: true))),
        new("DSTDEVP", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, n => DatabaseSpread(n, sample: false, root: true))),
        new("DSUM", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, n => Number(n.Sum()))),
        new("DVAR", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, n => DatabaseSpread(n, sample: true, root: false))),
        new("DVARP", 3, 3, Kinds.Any, (a, c) => DatabaseNumbers(a, c, n => DatabaseSpread(n, sample: false, root: false))),
    ];

    /// <summary>Variance from the sum of squares less the square of the sum over the count, whose last digits are the ones Excel's D functions have.</summary>
    private static EvalValue DatabaseSpread(List<double> numbers, bool sample, bool root)
    {
        int count = numbers.Count;
        if (count == 0 || (sample && count == 1))
        {
            return Error(CellError.DivisionByZero);
        }

        double sum = 0, squares = 0;
        foreach (double n in numbers)
        {
            sum += n;
            squares += n * n;
        }

        double variance = (squares - (sum * sum / count)) / (sample ? count - 1 : count);
        return Number(root ? Math.Sqrt(variance) : variance);
    }

    private static EvalValue DatabaseProduct(List<double> numbers)
    {
        if (numbers.Count == 0)
        {
            return Number(0);
        }

        double product = 1;
        foreach (double n in numbers)
        {
            product *= n;
        }

        return Number(product);
    }

    private static EvalValue DatabaseGet(List<EvalValue> values)
    {
        if (values.Count == 0)
        {
            return Error(CellError.Value);
        }

        if (values.Count > 1)
        {
            return Error(CellError.Number);
        }

        return values[0].Kind == EvalKind.Blank ? Error(CellError.Value) : values[0];
    }

    private static EvalValue DatabaseNumbers(EvalValue[] arguments, EvalContext context, Func<List<double>, EvalValue> body) =>
        Database(arguments, context, false, values =>
        {
            List<double> numbers = [];
            foreach (EvalValue value in values)
            {
                if (value.IsError)
                {
                    return value;
                }

                if (value.Kind == EvalKind.Number)
                {
                    numbers.Add(value.Number);
                }
            }

            return body(numbers);
        });

    /// <summary>The values of the field column in the records that meet the criteria, handed to <paramref name="body"/>.</summary>
    private static EvalValue Database(EvalValue[] arguments, EvalContext context, bool fieldOptional, Func<List<EvalValue>, EvalValue> body)
    {
        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        if (arguments[2].IsError)
        {
            return arguments[2];
        }

        if (arguments[0].Kind != EvalKind.Reference || arguments[0].Areas.Length != 1
            || arguments[2].Kind != EvalKind.Reference || arguments[2].Areas.Length != 1)
        {
            return Error(CellError.Value);
        }

        SheetArea table = arguments[0].Areas[0], criteria = arguments[2].Areas[0];
        RangeAddress range = table.Range;
        if (range.RowCount < 2 || criteria.Range.RowCount < 2)
        {
            return Error(CellError.Value);
        }

        // An error given as the field stays itself; one read from a cell does not name a field.
        if (arguments[1].IsError)
        {
            return arguments[1];
        }

        EvalValue named = arguments[1].Kind == EvalKind.Reference
            ? FormulaEvaluator.Deref(FormulaEvaluator.IntersectReference(arguments[1], context))
            : arguments[1];
        int? field = DatabaseField(named, table);
        if (field is null && (arguments[1].Kind != EvalKind.Missing || !fieldOptional))
        {
            return Error(CellError.Value);
        }

        DatabaseRow[] rows = DatabaseCriteria(table, criteria, context);
        List<EvalValue> values = [];
        for (int row = range.First.Row + 1; row <= range.Last.Row; row++)
        {
            bool met = false;
            foreach (DatabaseRow test in rows)
            {
                if (test.Meets(table.Sheet, row))
                {
                    met = true;
                    break;
                }
            }

            if (met)
            {
                values.Add(field is int column ? EvalValue.FromCell(table.Sheet.GetValue(row, range.First.Column + column)) : EvalValue.FromNumber(1));
            }
        }

        return body(values);
    }

    /// <summary>The column a field names, by its heading without case or by its number from 1; null when it names none.</summary>
    private static int? DatabaseField(in EvalValue field, in SheetArea table)
    {
        switch (field.Kind)
        {
            case EvalKind.Number:
            case EvalKind.Boolean:
                double number = Math.Truncate(field.Number);
                return number >= 1 && number <= table.Range.ColumnCount ? (int)number - 1 : null;
            case EvalKind.Text:
                return DatabaseColumn(field.Text, table);
            default:
                return null;
        }
    }

    private static int? DatabaseColumn(string heading, in SheetArea table)
    {
        RangeAddress range = table.Range;
        for (int column = 0; column < range.ColumnCount; column++)
        {
            CellValue cell = table.Sheet.GetValue(range.First.Row, range.First.Column + column);
            if (cell.Kind == CellValueKind.Text && string.Equals(cell.Text, heading, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return null;
    }

    /// <summary>The criteria range read into tests, one per row under its headings.</summary>
    private static DatabaseRow[] DatabaseCriteria(in SheetArea table, in SheetArea criteria, EvalContext context)
    {
        RangeAddress range = criteria.Range;
        List<DatabaseRow> rows = [];
        for (int row = range.First.Row + 1; row <= range.Last.Row; row++)
        {
            List<DatabaseTest> tests = [];
            for (int column = range.First.Column; column <= range.Last.Column; column++)
            {
                CellValue head = criteria.Sheet.GetValue(range.First.Row, column);
                int? target = head.Kind == CellValueKind.Text ? DatabaseColumn(head.Text, table) : null;
                if (target is int index)
                {
                    CellValue cell = criteria.Sheet.GetValue(row, column);
                    if (!cell.IsEmpty)
                    {
                        tests.Add(new DatabaseTest(table.Range.First.Column + index, DatabaseCondition(EvalValue.FromCell(cell), context), null, true));
                    }
                }
                else if (criteria.Sheet.GetFormula(row, column) is { } formula)
                {
                    tests.Add(new DatabaseTest(0, null, new DatabaseComputed(formula, criteria.Sheet, row, column, table.Range.First.Row + 1), true));
                }
                else
                {
                    // A value under a heading no field has: TRUE or a number that is not 0 passes every record, anything else none.
                    CellValue cell = criteria.Sheet.GetValue(row, column);
                    if (!cell.IsEmpty)
                    {
                        bool passes = cell.Kind == CellValueKind.Boolean ? cell.Boolean : cell.Kind == CellValueKind.Number && cell.Number != 0;
                        tests.Add(new DatabaseTest(0, null, null, passes));
                    }
                }
            }

            rows.Add(new DatabaseRow(tests));
        }

        return [.. rows];
    }

    /// <summary>A criteria cell as a condition: plain text means text that begins with it, so empty text means any text.</summary>
    private static Criterion DatabaseCondition(in EvalValue value, EvalContext context)
    {
        if (value.Kind == EvalKind.Text && (value.Text.Length == 0 || value.Text[0] is not ('<' or '>' or '='))
            && !ExcelValues.TryParse(value.Text, context, out _)
            && !value.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) && !value.Text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
            && !CellValue.TryParseError(value.Text, out _))
        {
            return Criterion.Parse(EvalValue.FromText(value.Text + "*"), context);
        }

        return Criterion.Parse(value, context);
    }

    /// <summary>A computed criterion: a cell's formula, whose relative references move down with each record.</summary>
    private sealed class DatabaseComputed(string formula, Worksheet sheet, int row, int column, int firstRecord)
    {
        private readonly CompiledFormula? compiled = Compile(formula, new CellAddress(row, column));
        private readonly Dictionary<DefinedName, CompiledFormula?> names = [];

        private static CompiledFormula? Compile(string formula, CellAddress at)
        {
            ParsedFormula? parsed = FormulaParser.TryParse(formula.StartsWith('=') ? formula[1..] : formula, r1c1: false, at);
            return parsed is null ? null : FormulaCompiler.Compile(parsed);
        }

        public bool Meets(int record)
        {
            if (compiled is null)
            {
                return false;
            }

            // The formula is written as for the first record, and stays in its cell, so only its relative references move.
            EvalContext context = EvalContext.ForMovedDown(sheet, row, column, record - firstRecord, names);
            CellValue value = FormulaEvaluator.Evaluate(compiled, context);
            return value.Kind == CellValueKind.Boolean ? value.Boolean : value.Kind == CellValueKind.Number && value.Number != 0;
        }
    }

    /// <summary>A test of a record: a condition on a column, a computed criterion, or neither, which gives <paramref name="Passes"/> for every record.</summary>
    private readonly record struct DatabaseTest(int Column, Criterion? Condition, DatabaseComputed? Computed, bool Passes);

    /// <summary>One row of the criteria range: a record must meet every test in it.</summary>
    private readonly struct DatabaseRow(List<DatabaseTest> tests)
    {
        public bool Meets(Worksheet sheet, int record)
        {
            foreach (DatabaseTest test in tests)
            {
                bool met = test.Computed is { } computed
                    ? computed.Meets(record)
                    : test.Condition is { } condition ? condition.Matches(EvalValue.FromCell(sheet.GetValue(record, test.Column))) : test.Passes;
                if (!met)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
