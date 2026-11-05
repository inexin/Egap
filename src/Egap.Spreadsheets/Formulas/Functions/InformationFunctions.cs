namespace Egap.Spreadsheets.Formulas;

/// <summary>Information functions: what kind of value a cell or an expression holds.</summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> InformationFunctions() =>
    [
        Is("ISBLANK", v => v.Kind is EvalKind.Blank or EvalKind.Missing),
        Is("ISERR", v => v.IsError && v.Error != CellError.NotAvailable),
        Is("ISERROR", v => v.IsError),
        Is("ISNA", v => v.IsError && v.Error == CellError.NotAvailable),
        Is("ISLOGICAL", v => v.Kind == EvalKind.Boolean),
        Is("ISNUMBER", v => v.Kind == EvalKind.Number),
        Is("ISTEXT", v => v.Kind == EvalKind.Text),
        Is("ISNONTEXT", v => v.Kind != EvalKind.Text),
        new("ISEVEN", 1, 1, Kinds.Value, (a, c) => Parity(a, c, odd: false)),
        new("ISODD", 1, 1, Kinds.Value, (a, c) => Parity(a, c, odd: true)),
        new("ISREF", 1, 1, Kinds.Any, static (a, _) => EvalValue.FromBoolean(a[0].Kind == EvalKind.Reference)),
        new("ISFORMULA", 1, 1, Kinds.Any, IsFormula),
        new("N", 1, 1, Kinds.Value, static (a, _) => a[0].Kind switch
        {
            EvalKind.Number or EvalKind.Boolean => Number(a[0].Number),
            EvalKind.Error => a[0],
            _ => Number(0),
        }),
        new("TYPE", 1, 1, Kinds.Any, TypeOf),
        new("ERROR.TYPE", 1, 1, Kinds.Value, static (a, _) => !a[0].IsError ? Error(CellError.NotAvailable)
            : Number(a[0].Error == CellError.Calc ? 14 : (int)a[0].Error + 1)),
    ];

    private static FormulaFunction Is(string name, Func<EvalValue, bool> test) =>
        new(name, 1, 1, Kinds.Value, (a, _) => EvalValue.FromBoolean(test(a[0])));

    private static EvalValue Parity(EvalValue[] arguments, EvalContext context, bool odd)
    {
        if (!Num(arguments[0], context, out double number, out EvalValue error))
        {
            return error;
        }

        bool isOdd = Math.Abs(Math.Truncate(number)) % 2 == 1;
        return EvalValue.FromBoolean(isOdd == odd);
    }

    private static EvalValue IsFormula(EvalValue[] arguments, EvalContext context)
    {
        EvalValue value = arguments[0];
        if (value.IsError)
        {
            return value;
        }

        if (value.Kind != EvalKind.Reference)
        {
            return Error(CellError.Value);
        }

        SheetArea area = value.Areas[0];
        return EvalValue.FromBoolean(area.Sheet.HasFormula(area.Range.First.Row, area.Range.First.Column));
    }

    /// <summary>TYPE: 1 for a number (or blank), 2 text, 4 a boolean, 16 an error, 64 an array or a range of several cells.</summary>
    private static EvalValue TypeOf(EvalValue[] arguments, EvalContext context)
    {
        EvalValue value = arguments[0];
        if (value.Kind == EvalKind.Reference)
        {
            if (value.Areas.Length > 1 || value.Areas[0].Range.CellCount > 1)
            {
                return Number(64);
            }

            value = FormulaEvaluator.Deref(value);
        }

        return Number(value.Kind switch
        {
            EvalKind.Text => 2,
            EvalKind.Boolean => 4,
            EvalKind.Error => 16,
            EvalKind.Array => 64,
            _ => 1,
        });
    }
}
