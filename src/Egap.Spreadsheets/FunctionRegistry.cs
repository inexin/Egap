using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets;

/// <summary>
/// The worksheet functions formulas can call: the built-in ones, and functions you add for your own formulas, as a
/// user-defined function in Excel. A function you add is known to every workbook in the process from then on; formulas read
/// it when they are next calculated or evaluated.
/// </summary>
/// <example>
/// <code>
/// FunctionRegistry.Add("DOUBLE", 1, 1, arguments => arguments[0].Value.Number * 2);
/// CellValue result = sheet.Evaluate("DOUBLE(21)");     // 42
/// </code>
/// </example>
public static class FunctionRegistry
{
    /// <summary>The names of the built-in functions, in alphabetical order.</summary>
    public static IReadOnlyList<string> BuiltInFunctions { get; } = [.. FormulaFunctions.BuiltInNames.Order(StringComparer.Ordinal)];

    /// <summary>The names of the functions added with <see cref="Add"/>, in alphabetical order.</summary>
    public static IReadOnlyList<string> AddedFunctions => [.. FormulaFunctions.UserNames.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Adds a function, or replaces one added before under the same name. Its arguments come as given: a range stays a range,
    /// and its <see cref="FunctionArgument.Values"/> are the cells. An exception the function throws gives <c>#VALUE!</c>
    /// in the cell, as an error in a user-defined function does in Excel.
    /// </summary>
    /// <param name="name">The function's name, without case: letters, digits, <c>.</c> and <c>_</c>, starting with a letter or <c>_</c>.</param>
    /// <param name="minArguments">The fewest arguments a call may give, 0 to 255.</param>
    /// <param name="maxArguments">The most arguments a call may give, <paramref name="minArguments"/> to 255; a call outside gives <c>#VALUE!</c>.</param>
    /// <param name="function">The function's code: given the arguments, gives the value.</param>
    /// <exception cref="ArgumentNullException">The name or the function is null.</exception>
    /// <exception cref="ArgumentException">The name is not a function name, or names a built-in function.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The argument counts are out of range.</exception>
    public static void Add(string name, int minArguments, int maxArguments, Func<IReadOnlyList<FunctionArgument>, CellValue> function)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(function);
        string key = Key(name);
        if (FormulaFunctions.IsBuiltIn(key) || key is "IF" or "IFERROR" or "IFNA" or "SINGLE" or "ANCHORARRAY")
        {
            throw new ArgumentException($"{key} is a built-in function.", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minArguments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minArguments, 255);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArguments, minArguments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxArguments, 255);
        FormulaFunctions.AddUser(new FormulaFunction(key, minArguments, maxArguments, [ArgumentKind.Any], (arguments, context) => Call(function, arguments, context)));
    }

    /// <summary>Removes a function added with <see cref="Add"/>; false when there is none by that name.</summary>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    public static bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return FormulaFunctions.RemoveUser(name.ToUpperInvariant());
    }

    /// <summary>Whether formulas can call a function by this name, built in or added.</summary>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    public static bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return FormulaFunctions.TryGet(FormulaCompiler.FunctionName(name), out _)
            || FormulaCompiler.FunctionName(name) is "IF" or "IFERROR" or "IFNA" or "SINGLE" or "ANCHORARRAY";
    }

    private static string Key(string name)
    {
        bool valid = name.Length is > 0 and <= 255 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');
        if (!valid)
        {
            throw new ArgumentException("A function name is letters, digits, '.' and '_', starting with a letter or '_'.", nameof(name));
        }

        return name.ToUpperInvariant();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failing user function gives #VALUE! in its cell, as in Excel, rather than stopping the calculation.")]
    private static EvalValue Call(Func<IReadOnlyList<FunctionArgument>, CellValue> function, EvalValue[] arguments, EvalContext context)
    {
        FunctionArgument[] given = new FunctionArgument[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            given[i] = new FunctionArgument(arguments[i], context);
        }

        try
        {
            return EvalValue.FromCell(function(given));
        }
        catch (Exception)
        {
            return EvalValue.FromError(CellError.Value);
        }
    }
}

/// <summary>An argument given to a function added with <see cref="FunctionRegistry.Add"/>.</summary>
public sealed class FunctionArgument
{
    private readonly EvalValue value;
    private readonly EvalContext context;
    private CellValue[,]? values;

    internal FunctionArgument(EvalValue value, EvalContext context)
    {
        this.value = value;
        this.context = context;
    }

    /// <summary>Whether the argument was left empty, as the second in <c>F(1,,3)</c>.</summary>
    public bool IsMissing => value.Kind == EvalKind.Missing;

    /// <summary>Whether the argument is a reference to cells, such as <c>A1:B3</c>, rather than a value worked out.</summary>
    public bool IsReference => value.Kind == EvalKind.Reference;

    /// <summary>
    /// The argument as one value: a reference to one cell gives the cell's value, a range the cell it meets in the formula's
    /// row or column (or <c>#VALUE!</c>), and an array its first element. A missing argument is empty.
    /// </summary>
    public CellValue Value
    {
        get
        {
            EvalValue scalar = value.Kind switch
            {
                EvalKind.Reference => FormulaEvaluator.Deref(FormulaEvaluator.IntersectReference(value, context)),
                EvalKind.Array => value.Array.Items[0],
                _ => value,
            };
            return scalar.Kind is EvalKind.Blank or EvalKind.Missing ? CellValue.Empty : scalar.ToCell();
        }
    }

    /// <summary>
    /// Every value of the argument by row and column: a range's cells (empty ones as <see cref="CellValue.Empty"/>), an
    /// array's elements, or the single value as one row of one. A range of more than 4,194,304 cells gives one <c>#VALUE!</c>.
    /// </summary>
    public CellValue[,] Values => values ??= Read();

    private CellValue[,] Read()
    {
        EvalValue source = value.Kind == EvalKind.Reference ? FormulaEvaluator.ToArray(value) : value;
        if (source.Kind != EvalKind.Array)
        {
            return new[,] { { source.Kind is EvalKind.Blank or EvalKind.Missing ? CellValue.Empty : source.ToCell() } };
        }

        EvalArray array = source.Array;
        CellValue[,] result = new CellValue[array.Rows, array.Columns];
        for (int row = 0; row < array.Rows; row++)
        {
            for (int column = 0; column < array.Columns; column++)
            {
                EvalValue item = array[row, column];
                result[row, column] = item.Kind is EvalKind.Blank or EvalKind.Missing ? CellValue.Empty : item.ToCell();
            }
        }

        return result;
    }
}
