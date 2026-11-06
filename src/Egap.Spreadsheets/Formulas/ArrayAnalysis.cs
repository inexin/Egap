namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// Tells whether a formula typed through the API is a dynamic array formula, the way Excel decides when it saves one: its
/// result can be more than one value, or worked out as an ordinary formula, a range in it would meet the formula's cell where
/// the typed formula takes the whole range. Other formulas stay ordinary, and calculate and save as they always did.
/// </summary>
internal static class ArrayAnalysis
{
    private enum Shape : byte
    {
        Scalar,
        Array,
        Range,
    }

    /// <summary>Functions that can give an array or a range of more than one cell, whatever they are given.</summary>
    private static readonly HashSet<string> ArrayResults = new(
    [
        "CHOOSECOLS", "CHOOSEROWS", "DROP", "EXPAND", "FILTER", "FREQUENCY", "GROWTH", "HSTACK", "INDIRECT", "LINEST", "LOGEST",
        "MINVERSE", "MMULT", "MUNIT", "OFFSET", "RANDARRAY", "SEQUENCE", "SORT", "SORTBY", "TAKE", "TEXTSPLIT", "TOCOL", "TOROW", "TRANSPOSE",
        "TREND", "UNIQUE", "VSTACK", "WRAPCOLS", "WRAPROWS", "XLOOKUP",
    ], StringComparer.Ordinal);

    /// <summary>Functions that hand on one of their arguments.</summary>
    private static readonly HashSet<string> Choosers = new(["IF", "IFERROR", "IFNA", "IFS", "SWITCH", "CHOOSE"], StringComparer.Ordinal);

    /// <summary>Whether the formula spills.</summary>
    public static bool Spills(ParsedFormula formula)
    {
        bool lifted = false;
        return Walk(formula.Root, ref lifted, arrayArgument: false) != Shape.Scalar || lifted;
    }

    private static Shape Walk(FormulaNode node, ref bool lifted, bool arrayArgument)
    {
        switch (node)
        {
            case ArrayNode array:
                return array.Rows * array.Columns > 1 ? Shape.Array : Shape.Scalar;
            case ReferenceNode reference:
                return reference.Area.Kind is AreaKind.Cell or AreaKind.Error ? Shape.Scalar : Shape.Range;
            case NameNode:
                return Shape.Range;
            case StructuredNode structured:
                return structured.Text.Contains('@', StringComparison.Ordinal) ? Shape.Scalar : Shape.Range;
            case ParenthesesNode parentheses:
                {
                    Shape shape = Shape.Scalar;
                    foreach (Argument item in parentheses.Items)
                    {
                        shape = Max(shape, Walk(item.Node, ref lifted, arrayArgument));
                    }

                    return parentheses.Items.Length > 1 ? Shape.Range : shape;
                }

            case UnaryNode unary:
                {
                    Shape shape = Walk(unary.Operand, ref lifted, arrayArgument);
                    return unary.Operator == '@' ? Shape.Scalar : Operated(shape, ref lifted, arrayArgument);
                }

            case PostfixNode postfix:
                {
                    Shape shape = Walk(postfix.Operand, ref lifted, arrayArgument);
                    return postfix.Operator == '#' ? Shape.Range : Operated(shape, ref lifted, arrayArgument);
                }

            case BinaryNode binary:
                {
                    Shape left = Walk(binary.Left, ref lifted, arrayArgument), right = Walk(binary.Right, ref lifted, arrayArgument);
                    return binary.Operator switch
                    {
                        BinaryOperator.Range => Shape.Range,
                        BinaryOperator.Intersection => Shape.Range,
                        _ => Operated(Max(left, right), ref lifted, arrayArgument),
                    };
                }

            case FunctionNode function:
                return Call(function, ref lifted, arrayArgument);
            default:
                return Shape.Scalar;
        }
    }

    private static Shape Call(FunctionNode function, ref bool lifted, bool arrayArgument)
    {
        string name = FormulaCompiler.FunctionName(function.Name);
        if (name == "SINGLE")
        {
            return Shape.Scalar;
        }

        if (name == "ANCHORARRAY")
        {
            return Shape.Range;
        }

        bool known = FormulaFunctions.TryGet(name, out FormulaFunction? body);
        bool chooser = Choosers.Contains(name);
        Shape result = ArrayResults.Contains(name) ? Shape.Array : Shape.Scalar;
        for (int i = 0; i < function.Arguments.Length; i++)
        {
            bool takesArray = known && body!.TakesArray(i);
            Shape shape = Walk(function.Arguments[i].Node, ref lifted, arrayArgument || takesArray);
            if (chooser)
            {
                // The condition of IF decides per element; the rest are handed on.
                result = Max(result, i == 0 && name is "IF" or "IFS" or "SWITCH" or "CHOOSE" ? Operated(shape, ref lifted, arrayArgument) : shape);
            }
            else if (known && body!.TakesValue(i))
            {
                result = Max(result, Operated(shape, ref lifted, arrayArgument));
            }
        }

        if (name == "INDEX" && function.Arguments.Length > 1 && function.Arguments.Skip(1).Any(a => a.Node is MissingNode or NumberNode { Value: 0 }))
        {
            result = Shape.Range;
        }

        return result;
    }

    /// <summary>What an operator or a function taking a single value makes of an operand: a range would meet the formula's cell in an ordinary formula.</summary>
    private static Shape Operated(Shape shape, ref bool lifted, bool arrayArgument)
    {
        if (shape == Shape.Range && !arrayArgument)
        {
            lifted = true;
        }

        return shape == Shape.Scalar ? Shape.Scalar : Shape.Array;
    }

    private static Shape Max(Shape a, Shape b) => a > b ? a : b;
}
