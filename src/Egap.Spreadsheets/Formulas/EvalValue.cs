namespace Egap.Spreadsheets.Formulas;

/// <summary>What kind of value the evaluator holds.</summary>
internal enum EvalKind : byte
{
    /// <summary>An empty cell.</summary>
    Blank,

    /// <summary>An argument left out.</summary>
    Missing,

    Number,
    Text,
    Boolean,
    Error,

    /// <summary>One or more areas of cells, kept as a reference until a value is needed.</summary>
    Reference,

    /// <summary>A rectangle of scalar values.</summary>
    Array,

    /// <summary>A LAMBDA, or a function named as a value: something to call, which shows as #CALC! in a cell.</summary>
    Lambda,
}

/// <summary>A rectangle of cells on one sheet.</summary>
internal readonly record struct SheetArea(Worksheet Sheet, RangeAddress Range);

/// <summary>A value on the evaluator's stack: a scalar, a reference or an array.</summary>
internal readonly struct EvalValue
{
    private readonly object? item;

    private EvalValue(EvalKind kind, double number, object? item)
    {
        Kind = kind;
        Number = number;
        this.item = item;
    }

    public static EvalValue Blank => default;

    public static EvalValue Missing => new(EvalKind.Missing, 0, null);

    public static EvalValue True => new(EvalKind.Boolean, 1, null);

    public static EvalValue False => new(EvalKind.Boolean, 0, null);

    public EvalKind Kind { get; }

    /// <summary>The number; 1 or 0 for a boolean; the <see cref="CellError"/> for an error.</summary>
    public double Number { get; }

    public string Text => (string)item!;

    public bool Boolean => Number != 0;

    public CellError Error => (CellError)(int)Number;

    public SheetArea[] Areas => (SheetArea[])item!;

    public EvalArray Array => (EvalArray)item!;

    public LambdaValue Lambda => (LambdaValue)item!;

    public bool IsError => Kind == EvalKind.Error;

    public bool IsScalar => Kind is not (EvalKind.Reference or EvalKind.Array);

    public static EvalValue FromNumber(double number) =>
        double.IsFinite(number) ? new(EvalKind.Number, number == 0 ? 0 : number, null) : FromError(CellError.Number);

    public static EvalValue FromText(string text) =>
        text.Length <= CellValue.MaxTextLength ? new(EvalKind.Text, 0, text) : FromError(CellError.Value);

    public static EvalValue FromBoolean(bool value) => new(EvalKind.Boolean, value ? 1 : 0, null);

    public static EvalValue FromError(CellError error) => new(EvalKind.Error, (int)error, null);

    public static EvalValue FromAreas(SheetArea[] areas) => new(EvalKind.Reference, 0, areas);

    public static EvalValue FromArea(Worksheet sheet, RangeAddress range) => new(EvalKind.Reference, 0, new[] { new SheetArea(sheet, range) });

    public static EvalValue FromArray(EvalArray array) => new(EvalKind.Array, 0, array);

    public static EvalValue FromLambda(LambdaValue lambda) => new(EvalKind.Lambda, 0, lambda);

    public static EvalValue FromCell(CellValue value) => value.Kind switch
    {
        CellValueKind.Number => new(EvalKind.Number, value.Number, null),
        CellValueKind.Text => new(EvalKind.Text, 0, value.Text),
        CellValueKind.Boolean => FromBoolean(value.Boolean),
        CellValueKind.Error => FromError(value.Error),
        _ => Blank,
    };

    /// <summary>The scalar as a cell value; a blank or missing value becomes 0, as a formula that gives one shows.</summary>
    public CellValue ToCell() => Kind switch
    {
        EvalKind.Number => CellValue.NumberUnchecked(Number),
        EvalKind.Text => CellValue.TextUnchecked(Text),
        EvalKind.Boolean => CellValue.FromBoolean(Boolean),
        EvalKind.Error => CellValue.FromError(Error),
        EvalKind.Lambda => CellValue.FromError(CellError.Calc),
        _ => CellValue.NumberUnchecked(0),
    };

    public override string ToString() => Kind switch
    {
        EvalKind.Number => Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        EvalKind.Text => '"' + Text + '"',
        EvalKind.Boolean => Boolean ? "TRUE" : "FALSE",
        EvalKind.Error => CellValue.ErrorText(Error),
        EvalKind.Reference => string.Join(",", Areas.Select(a => a.Sheet.Name + "!" + a.Range)),
        EvalKind.Array => $"{{{Array.Rows}x{Array.Columns}}}",
        _ => Kind.ToString(),
    };
}

/// <summary>A rectangle of scalar values, row by row.</summary>
internal sealed class EvalArray(int rows, int columns, EvalValue[] items)
{
    public int Rows { get; } = rows;

    public int Columns { get; } = columns;

    public EvalValue[] Items { get; } = items;

    public EvalValue this[int row, int column] => Items[(row * Columns) + column];
}
