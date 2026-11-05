using System.Diagnostics.CodeAnalysis;

namespace Egap.Spreadsheets.Formulas;

/// <summary>How a function takes an argument.</summary>
internal enum ArgumentKind : byte
{
    /// <summary>A single value: a range meets the formula's cell, and an array makes the function run per element.</summary>
    Value,

    /// <summary>Anything as given: a reference, an array or a scalar, as SUM and ROW take theirs.</summary>
    Any,
}

/// <summary>A function's code: given its arguments (value arguments already turned into scalars), gives its result.</summary>
internal delegate EvalValue FunctionBody(EvalValue[] arguments, EvalContext context);

/// <summary>A worksheet function: its name, how many arguments it takes and how.</summary>
internal sealed class FormulaFunction(string name, int minArguments, int maxArguments, ArgumentKind[] kinds, FunctionBody body)
{
    public string Name { get; } = name;

    public int MinArguments { get; } = minArguments;

    public int MaxArguments { get; } = maxArguments;

    public FunctionBody Body { get; } = body;

    /// <summary>Whether argument <paramref name="index"/> is a value argument; the last kind given repeats.</summary>
    public bool TakesValue(int index) => kinds.Length > 0 && kinds[Math.Min(index, kinds.Length - 1)] == ArgumentKind.Value;
}

/// <summary>
/// The worksheet functions the evaluator knows, by name. Stage B starts with the core that the evaluator itself needs; IF,
/// IFERROR and IFNA are compiled to jumps, and SINGLE and ANCHORARRAY (Excel's stored forms of <c>@</c> and <c>#</c>) to
/// operators. The full set of common functions and the public registry come with task 15.
/// </summary>
internal static class FormulaFunctions
{
    private static readonly ArgumentKind[] Values = [ArgumentKind.Value];
    private static readonly ArgumentKind[] Anything = [ArgumentKind.Any];

    private static readonly Dictionary<string, FormulaFunction> Functions = new FormulaFunction[]
    {
        new("SUM", 1, 255, Anything, Sum),
        new("AND", 1, 255, Anything, (a, c) => Logical(a, c, and: true)),
        new("OR", 1, 255, Anything, (a, c) => Logical(a, c, and: false)),
        new("NOT", 1, 1, Values, Not),
        new("TRUE", 0, 0, Values, static (_, _) => EvalValue.True),
        new("FALSE", 0, 0, Values, static (_, _) => EvalValue.False),
        new("NA", 0, 0, Values, static (_, _) => EvalValue.FromError(CellError.NotAvailable)),
        new("CHOOSE", 2, 255, [ArgumentKind.Value, ArgumentKind.Any], Choose),
        new("ROW", 0, 1, Anything, (a, c) => Position(a, c, row: true)),
        new("COLUMN", 0, 1, Anything, (a, c) => Position(a, c, row: false)),
        new("DATE", 3, 3, Values, Date),
    }.ToDictionary(f => f.Name, StringComparer.Ordinal);

    /// <summary>The function with a name already stripped of prefixes and in upper case.</summary>
    public static bool TryGet(string name, [NotNullWhen(true)] out FormulaFunction? function) => Functions.TryGetValue(name, out function);

    /// <summary>
    /// Every scalar an argument holds, as an aggregate sees it: the cells of a reference that hold a value, an array's
    /// elements, or the scalar given. Direct says whether the value was typed as an argument, where Excel
    /// reads text and booleans as numbers.
    /// </summary>
    private static IEnumerable<(EvalValue Value, bool Direct)> Scalars(EvalValue argument)
    {
        switch (argument.Kind)
        {
            case EvalKind.Reference:
                foreach (SheetArea area in argument.Areas)
                {
                    foreach ((int row, int column) in area.Sheet.Store.Occupied(area.Range))
                    {
                        CellValue value = area.Sheet.GetValue(row, column);
                        if (!value.IsEmpty)
                        {
                            yield return (EvalValue.FromCell(value), false);
                        }
                    }
                }

                break;
            case EvalKind.Array:
                foreach (EvalValue item in argument.Array.Items)
                {
                    yield return (item, false);
                }

                break;
            default:
                yield return (argument, true);
                break;
        }
    }

    /// <summary>SUM: numbers in references and arrays; numbers, booleans and text that reads as a number typed as arguments.</summary>
    private static EvalValue Sum(EvalValue[] arguments, EvalContext context)
    {
        double total = 0;
        foreach (EvalValue argument in arguments)
        {
            foreach ((EvalValue value, bool direct) in Scalars(argument))
            {
                switch (value.Kind)
                {
                    case EvalKind.Error:
                        return value;
                    case EvalKind.Number:
                        total += value.Number;
                        break;
                    case EvalKind.Boolean when direct:
                        total += value.Number;
                        break;
                    case EvalKind.Text when direct:
                        if (!ExcelValues.TryNumber(value, context, out double number, out EvalValue error))
                        {
                            return error;
                        }

                        total += number;
                        break;
                }
            }
        }

        return EvalValue.FromNumber(total);
    }

    /// <summary>AND and OR: booleans and numbers; text in references is skipped, typed text must read TRUE or FALSE.</summary>
    private static EvalValue Logical(EvalValue[] arguments, EvalContext context, bool and)
    {
        bool any = false, result = and;
        foreach (EvalValue argument in arguments)
        {
            foreach ((EvalValue value, bool direct) in Scalars(argument))
            {
                bool test;
                switch (value.Kind)
                {
                    case EvalKind.Error:
                        return value;
                    case EvalKind.Number:
                    case EvalKind.Boolean:
                        test = value.Number != 0;
                        break;
                    case EvalKind.Missing:
                        test = false;
                        break;
                    case EvalKind.Text when direct:
                        if (!ExcelValues.TryBoolean(value, out test, out EvalValue error))
                        {
                            return error;
                        }

                        break;
                    default:
                        continue;
                }

                any = true;
                result = and ? result && test : result || test;
            }
        }

        return any ? EvalValue.FromBoolean(result) : EvalValue.FromError(CellError.Value);
    }

    private static EvalValue Not(EvalValue[] arguments, EvalContext context) =>
        ExcelValues.TryBoolean(arguments[0], out bool value, out EvalValue error) ? EvalValue.FromBoolean(!value) : error;

    /// <summary>CHOOSE: the argument the index names, as given (a reference stays one).</summary>
    private static EvalValue Choose(EvalValue[] arguments, EvalContext context)
    {
        if (!ExcelValues.TryNumber(arguments[0], context, out double index, out EvalValue error))
        {
            return error;
        }

        int chosen = (int)Math.Clamp(Math.Truncate(index), 0, 256);
        if (chosen < 1 || chosen >= arguments.Length)
        {
            return EvalValue.FromError(CellError.Value);
        }

        EvalValue value = arguments[chosen];
        return value.Kind == EvalKind.Missing ? EvalValue.FromNumber(0) : value;
    }

    /// <summary>ROW and COLUMN: of the reference's first cell, or of the formula's cell when none is given.</summary>
    private static EvalValue Position(EvalValue[] arguments, EvalContext context, bool row)
    {
        if (arguments.Length == 0 || arguments[0].Kind == EvalKind.Missing)
        {
            return EvalValue.FromNumber((row ? context.Row : context.Column) + 1);
        }

        EvalValue argument = arguments[0];
        if (argument.IsError)
        {
            return argument;
        }

        if (argument.Kind != EvalKind.Reference)
        {
            return EvalValue.FromError(CellError.Value);
        }

        CellAddress first = argument.Areas[0].Range.First;
        return EvalValue.FromNumber((row ? first.Row : first.Column) + 1);
    }

    /// <summary>DATE: the serial number of a year, month and day in the workbook's date system.</summary>
    private static EvalValue Date(EvalValue[] arguments, EvalContext context)
    {
        Span<double> parts = stackalloc double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!ExcelValues.TryNumber(arguments[i], context, out parts[i], out EvalValue error))
            {
                return error;
            }
        }

        return ExcelValues.DateSerial(parts[0], parts[1], parts[2], context.Date1904) is double serial
            ? EvalValue.FromNumber(serial)
            : EvalValue.FromError(CellError.Number);
    }
}
