using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Egap.Spreadsheets.Formulas;

/// <summary>How a function takes an argument.</summary>
internal enum ArgumentKind : byte
{
    /// <summary>A single value: a range meets the formula's cell, and an array makes the function run per element.</summary>
    Value,

    /// <summary>Anything as given: a reference, an array or a scalar, as SUM and ROW take theirs.</summary>
    Any,

    /// <summary>
    /// An array, as given: the expression that makes it is worked out in array mode even in an ordinary formula, so
    /// <c>SUMPRODUCT(A1:A3*B1:B3)</c> multiplies the ranges whole, as in Excel.
    /// </summary>
    Array,
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
    public bool TakesValue(int index) => Kind(index) == ArgumentKind.Value;

    /// <summary>Whether argument <paramref name="index"/> is an array argument, worked out in array mode.</summary>
    public bool TakesArray(int index) => Kind(index) == ArgumentKind.Array;

    private ArgumentKind Kind(int index) => kinds.Length > 0 ? kinds[Math.Min(index, kinds.Length - 1)] : ArgumentKind.Any;
}

/// <summary>
/// The worksheet functions the evaluator knows, by name, and the functions added through <see cref="FunctionRegistry"/>.
/// IF, IFERROR and IFNA are compiled to jumps, and SINGLE and ANCHORARRAY (Excel's stored forms of <c>@</c> and <c>#</c>)
/// to operators. Each category of functions is in a file of its own; Microsoft's function reference is the source for
/// what each one does.
/// </summary>
internal static partial class FormulaFunctions
{
    private static readonly ConcurrentDictionary<string, FormulaFunction> UserFunctions = new(StringComparer.Ordinal);
    private static FrozenDictionary<string, FormulaFunction>? builtIn;

    private static FrozenDictionary<string, FormulaFunction> BuiltIn => builtIn ??= Build();

    /// <summary>The names of the built-in functions.</summary>
    public static IEnumerable<string> BuiltInNames => BuiltIn.Keys;

    /// <summary>The names of the functions added through <see cref="FunctionRegistry"/>.</summary>
    public static IEnumerable<string> UserNames => UserFunctions.Keys;

    /// <summary>The function with a name already stripped of prefixes and in upper case.</summary>
    public static bool TryGet(string name, [NotNullWhen(true)] out FormulaFunction? function) =>
        BuiltIn.TryGetValue(name, out function) || UserFunctions.TryGetValue(name, out function);

    public static bool IsBuiltIn(string name) => BuiltIn.ContainsKey(name);

    private static int userVersion;

    /// <summary>Counts changes to the added functions, so a workbook's calculation compiles its formulas again after one.</summary>
    public static int UserVersion => Volatile.Read(ref userVersion);

    public static void AddUser(FormulaFunction function)
    {
        UserFunctions[function.Name] = function;
        Interlocked.Increment(ref userVersion);
    }

    public static bool RemoveUser(string name)
    {
        bool removed = UserFunctions.TryRemove(name, out _);
        Interlocked.Increment(ref userVersion);
        return removed;
    }

    private static FrozenDictionary<string, FormulaFunction> Build()
    {
        List<FormulaFunction> all = [.. Core(), .. MathFunctions(), .. TextFunctions(), .. DateFunctions(), .. LookupFunctions(),
            .. StatisticalFunctions(), .. InformationFunctions()];
        return all.ToFrozenDictionary(f => f.Name, StringComparer.Ordinal);
    }

    private static IEnumerable<FormulaFunction> Core() =>
    [
        new("SUM", 1, 255, Kinds.Any, Sum),
        new("AND", 1, 255, Kinds.Any, (a, c) => Logical(a, Logic.And)),
        new("OR", 1, 255, Kinds.Any, (a, c) => Logical(a, Logic.Or)),
        new("XOR", 1, 255, Kinds.Any, (a, c) => Logical(a, Logic.Xor)),
        new("NOT", 1, 1, Kinds.Value, Not),
        new("TRUE", 0, 0, Kinds.Value, static (_, _) => EvalValue.True),
        new("FALSE", 0, 0, Kinds.Value, static (_, _) => EvalValue.False),
        new("NA", 0, 0, Kinds.Value, static (_, _) => Error(CellError.NotAvailable)),
        new("CHOOSE", 2, 255, Kinds.ValueThenAny, Choose),
        new("ROW", 0, 1, Kinds.Any, (a, c) => Position(a, c, row: true)),
        new("COLUMN", 0, 1, Kinds.Any, (a, c) => Position(a, c, row: false)),
        new("IFS", 2, 254, Kinds.Value, Ifs),
        new("SWITCH", 3, 254, Kinds.Value, Switch),
    ];

    /// <summary>The argument kinds most functions use, kept apart so they exist before any function list is made.</summary>
    private static class Kinds
    {
        public static readonly ArgumentKind[] Value = [ArgumentKind.Value];
        public static readonly ArgumentKind[] Any = [ArgumentKind.Any];
        public static readonly ArgumentKind[] Array = [ArgumentKind.Array];

        /// <summary>The first argument an array, the rest values, as INDEX takes them.</summary>
        public static readonly ArgumentKind[] ArrayThenValues = [ArgumentKind.Array, ArgumentKind.Value];

        /// <summary>The first argument a value, the rest arrays, as LOOKUP and FORECAST take them.</summary>
        public static readonly ArgumentKind[] ValueThenArrays = [ArgumentKind.Value, ArgumentKind.Array];

        /// <summary>The first argument as given, the rest values.</summary>
        public static readonly ArgumentKind[] AnyThenValues = [ArgumentKind.Any, ArgumentKind.Value];

        /// <summary>The first argument a value, the rest as given.</summary>
        public static readonly ArgumentKind[] ValueThenAny = [ArgumentKind.Value, ArgumentKind.Any];

        /// <summary>A value, a range as given, then values, as RANK takes them.</summary>
        public static readonly ArgumentKind[] ValueThenAnyThenValue = [ArgumentKind.Value, ArgumentKind.Any, ArgumentKind.Value];

        /// <summary>Pairs of a range as given and a condition taken as a value, as COUNTIFS takes them.</summary>
        public static readonly ArgumentKind[] Conditions = Pairs(0);

        /// <summary>A target range, then pairs of a range and a condition, as SUMIFS takes them.</summary>
        public static readonly ArgumentKind[] TargetThenConditions = Pairs(1);

        private static ArgumentKind[] Pairs(int skip)
        {
            ArgumentKind[] kinds = new ArgumentKind[256];
            for (int i = 0; i < kinds.Length; i++)
            {
                kinds[i] = i >= skip && (i - skip) % 2 == 1 ? ArgumentKind.Value : ArgumentKind.Any;
            }

            return kinds;
        }
    }

    private static EvalValue Error(CellError error) => EvalValue.FromError(error);

    private static EvalValue Number(double number) => EvalValue.FromNumber(number);

    private static bool Num(in EvalValue value, EvalContext context, out double number, out EvalValue error) =>
        ExcelValues.TryNumber(value, context, out number, out error);

    /// <summary>An optional number argument: <paramref name="fallback"/> when left off; an empty argument is 0, as in Excel.</summary>
    private static bool Optional(EvalValue[] arguments, int index, double fallback, EvalContext context, out double number, out EvalValue error)
    {
        if (index >= arguments.Length)
        {
            number = fallback;
            error = default;
            return true;
        }

        return Num(arguments[index], context, out number, out error);
    }

    /// <summary>An optional boolean argument: <paramref name="fallback"/> when left off; an empty argument is FALSE.</summary>
    private static bool OptionalFlag(EvalValue[] arguments, int index, bool fallback, out bool flag, out EvalValue error)
    {
        if (index >= arguments.Length)
        {
            flag = fallback;
            error = default;
            return true;
        }

        return ExcelValues.TryBoolean(arguments[index], out flag, out error);
    }

    private static bool Str(in EvalValue value, EvalContext context, out string text, out EvalValue error) =>
        ExcelValues.TryText(value, context.Culture, out text, out error);

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

    /// <summary>
    /// The numbers of aggregate arguments, as AVERAGE, MAX and the statistics take them: numbers in references and arrays,
    /// with text and booleans there skipped (or, with <paramref name="countAll"/> for the A functions, text as 0 and booleans
    /// as 1 or 0); numbers, booleans and text that reads as a number typed as arguments. Gives the first error met, or null.
    /// </summary>
    private static EvalValue? Gather(EvalValue[] arguments, EvalContext context, List<double> numbers, bool countAll = false, int first = 0, int end = -1)
    {
        end = end < 0 ? arguments.Length : end;
        for (int i = first; i < end; i++)
        {
            foreach ((EvalValue value, bool direct) in Scalars(arguments[i]))
            {
                switch (value.Kind)
                {
                    case EvalKind.Error:
                        return value;
                    case EvalKind.Number:
                        numbers.Add(value.Number);
                        break;
                    case EvalKind.Boolean when direct || countAll:
                        numbers.Add(value.Number);
                        break;
                    case EvalKind.Text when direct:
                        if (!ExcelValues.TryParse(value.Text, context, out double number))
                        {
                            return Error(CellError.Value);
                        }

                        numbers.Add(number);
                        break;
                    case EvalKind.Text when countAll:
                        numbers.Add(0);
                        break;
                    case EvalKind.Blank or EvalKind.Missing when direct:
                        numbers.Add(0);
                        break;
                }
            }
        }

        return null;
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
                        if (!Num(value, context, out double number, out EvalValue error))
                        {
                            return error;
                        }

                        total += number;
                        break;
                }
            }
        }

        return Number(total);
    }

    private enum Logic
    {
        And,
        Or,
        Xor,
    }

    /// <summary>AND, OR and XOR: booleans and numbers; text in references is skipped, typed text must read TRUE or FALSE.</summary>
    private static EvalValue Logical(EvalValue[] arguments, Logic logic)
    {
        bool any = false, result = logic == Logic.And;
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
                result = logic switch
                {
                    Logic.And => result && test,
                    Logic.Or => result || test,
                    _ => result ^ test,
                };
            }
        }

        return any ? EvalValue.FromBoolean(result) : Error(CellError.Value);
    }

    private static EvalValue Not(EvalValue[] arguments, EvalContext context) =>
        ExcelValues.TryBoolean(arguments[0], out bool value, out EvalValue error) ? EvalValue.FromBoolean(!value) : error;

    /// <summary>CHOOSE: the argument the index names, as given (a reference stays one).</summary>
    private static EvalValue Choose(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double index, out EvalValue error))
        {
            return error;
        }

        int chosen = (int)Math.Clamp(Math.Truncate(index), 0, 256);
        if (chosen < 1 || chosen >= arguments.Length)
        {
            return Error(CellError.Value);
        }

        EvalValue value = arguments[chosen];
        return value.Kind == EvalKind.Missing ? EvalValue.Blank : value;
    }

    /// <summary>ROW and COLUMN: of the reference's first cell, or of the formula's cell when none is given.</summary>
    private static EvalValue Position(EvalValue[] arguments, EvalContext context, bool row)
    {
        if (arguments.Length == 0 || arguments[0].Kind == EvalKind.Missing)
        {
            return Number((row ? context.Row : context.Column) + 1);
        }

        EvalValue argument = arguments[0];
        if (argument.IsError)
        {
            return argument;
        }

        if (argument.Kind != EvalKind.Reference)
        {
            return Error(CellError.Value);
        }

        CellAddress first = argument.Areas[0].Range.First;
        return Number((row ? first.Row : first.Column) + 1);
    }

    /// <summary>IFS: the value after the first condition that is TRUE; #N/A when none is.</summary>
    private static EvalValue Ifs(EvalValue[] arguments, EvalContext context)
    {
        if (arguments.Length % 2 != 0)
        {
            return Error(CellError.Value);
        }

        for (int i = 0; i < arguments.Length; i += 2)
        {
            if (!ExcelValues.TryBoolean(arguments[i], out bool test, out EvalValue error))
            {
                return error;
            }

            if (test)
            {
                return Result(arguments[i + 1]);
            }
        }

        return Error(CellError.NotAvailable);
    }

    /// <summary>SWITCH: the value paired with the first case equal to the expression, the default, or #N/A.</summary>
    private static EvalValue Switch(EvalValue[] arguments, EvalContext context)
    {
        EvalValue expression = arguments[0];
        if (expression.IsError)
        {
            return expression;
        }

        int pairs = (arguments.Length - 1) / 2;
        for (int i = 0; i < pairs; i++)
        {
            EvalValue match = arguments[1 + (2 * i)];
            if (match.IsError)
            {
                return match;
            }

            if (Same(expression, match, context))
            {
                return Result(arguments[2 + (2 * i)]);
            }
        }

        return arguments.Length % 2 == 0 ? Result(arguments[^1]) : Error(CellError.NotAvailable);
    }

    /// <summary>
    /// A cell a function hands back as its result: a blank stays blank, as with VLOOKUP, INDEX or SWITCH in Excel, so it equals ""
    /// as well as 0 and ISBLANK is TRUE, and the cell itself shows 0. An argument left empty is 0.
    /// </summary>
    private static EvalValue Result(in EvalValue value) => value.Kind == EvalKind.Missing ? Number(0) : value;

    /// <summary>A value for a function that doesn't hand back blanks, HYPERLINK and an array's elements: a blank becomes 0.</summary>
    private static EvalValue ZeroIfBlank(in EvalValue value) => value.Kind is EvalKind.Blank or EvalKind.Missing ? Number(0) : value;

    /// <summary>Whether two scalars are equal as <c>=</c> finds them: errors only equal the same error.</summary>
    private static bool Same(in EvalValue a, in EvalValue b, EvalContext context)
    {
        if (a.IsError || b.IsError)
        {
            return a.IsError && b.IsError && a.Error == b.Error;
        }

        return ExcelValues.Compare(a, b, context) == 0;
    }
}
