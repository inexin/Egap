namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// A node of a parsed formula. Every node keeps the white space written before its first token in <see cref="Lead"/>, and
/// the nodes that hold more than one token keep the white space before each of the others, so a formula prints back exactly
/// as it was written.
/// </summary>
internal abstract class FormulaNode
{
    /// <summary>The white space written before the node's first token.</summary>
    public string Lead = "";
}

/// <summary>A number, with its text as written (<c>1.50</c>, <c>1E3</c>).</summary>
internal sealed class NumberNode(string text, double value) : FormulaNode
{
    public string Text { get; } = text;

    public double Value { get; } = value;
}

/// <summary>A string constant: its text as written, quotes and doubled quotes included, and its value.</summary>
internal sealed class StringNode(string text, string value) : FormulaNode
{
    public string Text { get; } = text;

    public string Value { get; } = value;
}

/// <summary>TRUE or FALSE as written.</summary>
internal sealed class BooleanNode(string text, bool value) : FormulaNode
{
    public string Text { get; } = text;

    public bool Value { get; } = value;
}

/// <summary>An error constant such as <c>#N/A</c>.</summary>
internal sealed class ErrorNode(string text, CellError error) : FormulaNode
{
    public string Text { get; } = text;

    public CellError Error { get; } = error;
}

/// <summary>An array constant such as <c>{1,2;3,4}</c>: its text as written and its values row by row.</summary>
internal sealed class ArrayNode(string text, int rows, int columns, CellValue[] values) : FormulaNode
{
    public string Text { get; } = text;

    public int Rows { get; } = rows;

    public int Columns { get; } = columns;

    public CellValue[] Values { get; } = values;
}

/// <summary>An argument left out, as the second one of <c>IF(A1,,2)</c>.</summary>
internal sealed class MissingNode : FormulaNode
{
}

/// <summary>
/// What comes before the <c>!</c> of a reference or name: a book (<c>[1]</c>), a sheet, or a range of sheets
/// (<c>Sheet1:Sheet3</c>), with its text as written, the <c>!</c> included.
/// </summary>
internal sealed record SheetPrefix(string Text, string? Book, string? FirstSheet, string? LastSheet);

/// <summary>What a reference covers.</summary>
internal enum AreaKind : byte
{
    /// <summary>One cell, <c>A1</c>.</summary>
    Cell,

    /// <summary>A rectangle, <c>A1:B2</c>.</summary>
    Range,

    /// <summary>Whole rows, <c>1:3</c>.</summary>
    Rows,

    /// <summary>Whole columns, <c>A:C</c>.</summary>
    Columns,

    /// <summary>A reference that is gone, <c>#REF!</c>.</summary>
    Error,
}

/// <summary>Which coordinates of an <see cref="Area"/> are absolute (<c>$</c> in A1, a plain number in R1C1).</summary>
[Flags]
internal enum AreaFlags : byte
{
    None = 0,
    FirstRowAbsolute = 1,
    FirstColumnAbsolute = 2,
    LastRowAbsolute = 4,
    LastColumnAbsolute = 8,
}

/// <summary>
/// The cells a reference covers, as zero-based rows and columns resolved against the cell the formula was read for. A
/// relative coordinate moves with the formula: printing it for another cell, or evaluating it with a delta, shifts it.
/// </summary>
internal readonly record struct Area(AreaKind Kind, int FirstRow, int FirstColumn, int LastRow, int LastColumn, AreaFlags Flags)
{
    public bool FirstRowAbsolute => (Flags & AreaFlags.FirstRowAbsolute) != 0;

    public bool FirstColumnAbsolute => (Flags & AreaFlags.FirstColumnAbsolute) != 0;

    public bool LastRowAbsolute => (Flags & AreaFlags.LastRowAbsolute) != 0;

    public bool LastColumnAbsolute => (Flags & AreaFlags.LastColumnAbsolute) != 0;
}

/// <summary>A cell or range reference, with an optional sheet or book prefix.</summary>
internal sealed class ReferenceNode(SheetPrefix? prefix, string text, Area area) : FormulaNode
{
    public SheetPrefix? Prefix { get; } = prefix;

    /// <summary>The reference as written, without the prefix.</summary>
    public string Text { get; } = text;

    public Area Area { get; } = area;
}

/// <summary>A defined name, a table name or a name nobody knows, with an optional prefix.</summary>
internal sealed class NameNode(SheetPrefix? prefix, string name) : FormulaNode
{
    public SheetPrefix? Prefix { get; } = prefix;

    public string Name { get; } = name;
}

/// <summary>The parts of a table a structured reference names.</summary>
[Flags]
internal enum TableItems : byte
{
    None = 0,
    All = 1,
    Data = 2,
    Headers = 4,
    Totals = 8,
    ThisRow = 16,
}

/// <summary>
/// A structured reference such as <c>Sales[Amount]</c>, <c>Sales[[#Headers],[Region]:[Amount]]</c> or <c>[@Amount]</c>:
/// its text as written, the table (null inside a table) and what it names.
/// </summary>
internal sealed class StructuredNode(string text, string? table, TableItems items, string? firstColumn, string? lastColumn) : FormulaNode
{
    public string Text { get; } = text;

    public string? Table { get; } = table;

    public TableItems Items { get; } = items;

    public string? FirstColumn { get; } = firstColumn;

    public string? LastColumn { get; } = lastColumn;
}

/// <summary>An expression and the white space written after it, before the next comma or the closing parenthesis.</summary>
internal readonly record struct Argument(FormulaNode Node, string Trail);

/// <summary>A function call: its name as written (<c>_xlfn.</c> prefixes included) and its arguments.</summary>
internal sealed class FunctionNode(string name, Argument[] arguments) : FormulaNode
{
    public string Name { get; } = name;

    public Argument[] Arguments { get; } = arguments;
}

/// <summary>
/// An expression in parentheses. With more than one item it is a union of references, <c>(A1:B2,D4)</c>.
/// </summary>
internal sealed class ParenthesesNode(Argument[] items) : FormulaNode
{
    public Argument[] Items { get; } = items;
}

/// <summary>A prefix operator: <c>-</c>, <c>+</c> or <c>@</c> (implicit intersection).</summary>
internal sealed class UnaryNode(char op, FormulaNode operand) : FormulaNode
{
    public char Operator { get; } = op;

    public FormulaNode Operand { get; } = operand;
}

/// <summary>A postfix operator: <c>%</c> or <c>#</c> (the spill range of a cell).</summary>
internal sealed class PostfixNode(FormulaNode operand, char op, string operatorLead) : FormulaNode
{
    public FormulaNode Operand { get; } = operand;

    public char Operator { get; } = op;

    public string OperatorLead { get; } = operatorLead;
}

/// <summary>The binary operators, in Excel's grammar.</summary>
internal enum BinaryOperator : byte
{
    Range,
    Intersection,
    Power,
    Multiply,
    Divide,
    Add,
    Subtract,
    Concatenate,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>
/// A binary operator. <see cref="OperatorText"/> is the operator as written; for an intersection it is the white space that
/// is the operator.
/// </summary>
internal sealed class BinaryNode(BinaryOperator op, string operatorText, string operatorLead, FormulaNode left, FormulaNode right) : FormulaNode
{
    public BinaryOperator Operator { get; } = op;

    public string OperatorText { get; } = operatorText;

    public string OperatorLead { get; } = operatorLead;

    public FormulaNode Left { get; } = left;

    public FormulaNode Right { get; } = right;
}

/// <summary>A parsed formula: the expression and the white space after it.</summary>
internal sealed class ParsedFormula(FormulaNode root, string trail, CellAddress origin)
{
    public FormulaNode Root { get; } = root;

    public string Trail { get; } = trail;

    /// <summary>The cell the formula was read for; relative references are resolved against it.</summary>
    public CellAddress Origin { get; } = origin;
}
