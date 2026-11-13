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
        "BYCOL", "BYROW", "MAKEARRAY", "MAP", "REDUCE", "SCAN",
        "MINVERSE", "MMULT", "MUNIT", "OFFSET", "RANDARRAY", "SEQUENCE", "SORT", "SORTBY", "TAKE", "TEXTSPLIT", "TOCOL", "TOROW", "TRANSPOSE",
        "TREND", "UNIQUE", "VSTACK", "WRAPCOLS", "WRAPROWS", "XLOOKUP", "REGEXEXTRACT", "FILTERXML", "GROUPBY", "PIVOTBY",
    ], StringComparer.Ordinal);

    /// <summary>Functions that hand on one of their arguments.</summary>
    private static readonly HashSet<string> Choosers = new(["IF", "IFERROR", "IFNA", "IFS", "SWITCH", "CHOOSE"], StringComparer.Ordinal);

    /// <summary>Whether the formula spills.</summary>
    public static bool Spills(ParsedFormula formula)
    {
        bool lifted = false;
        return Walk(formula.Root, ref lifted, arrayArgument: false) != Shape.Scalar || lifted;
    }

    private static Shape Walk(FormulaNode node, ref bool lifted, bool arrayArgument, Dictionary<string, Shape>? locals = null)
    {
        switch (node)
        {
            case ArrayNode array:
                return array.Rows * array.Columns > 1 ? Shape.Array : Shape.Scalar;
            case ReferenceNode reference when locals is not null && FormulaCompiler.LocalName(reference).Name is { } cellName && locals.TryGetValue(cellName, out Shape cellLocal):
                return cellLocal;
            case ReferenceNode reference:
                return reference.Area.Kind is AreaKind.Cell or AreaKind.Error ? Shape.Scalar : Shape.Range;
            case NameNode word when locals is not null && FormulaCompiler.LocalName(word).Name is { } localName && locals.TryGetValue(localName, out Shape local):
                return local;
            case NameNode:
                return Shape.Range;
            case InvokeNode invoke:
                Walk(invoke.Callee, ref lifted, arrayArgument, locals);
                return Shape.Array;
            case StructuredNode structured:
                return structured.FirstColumn is not null && structured.LastColumn is null && structured.Items is TableItems.ThisRow or TableItems.Headers or TableItems.Totals
                    ? Shape.Scalar
                    : Shape.Range;
            case ParenthesesNode parentheses:
                {
                    Shape shape = Shape.Scalar;
                    foreach (Argument item in parentheses.Items)
                    {
                        shape = Max(shape, Walk(item.Node, ref lifted, arrayArgument, locals));
                    }

                    return parentheses.Items.Length > 1 ? Shape.Range : shape;
                }

            case UnaryNode unary:
                {
                    Shape shape = Walk(unary.Operand, ref lifted, arrayArgument, locals);
                    return unary.Operator == '@' ? Shape.Scalar : Operated(shape, ref lifted, arrayArgument);
                }

            case PostfixNode postfix:
                {
                    Shape shape = Walk(postfix.Operand, ref lifted, arrayArgument, locals);
                    return postfix.Operator == '#' ? Shape.Range : Operated(shape, ref lifted, arrayArgument);
                }

            case BinaryNode binary:
                {
                    Shape left = Walk(binary.Left, ref lifted, arrayArgument, locals), right = Walk(binary.Right, ref lifted, arrayArgument, locals);
                    return binary.Operator switch
                    {
                        BinaryOperator.Range => Shape.Range,
                        BinaryOperator.Intersection => Shape.Range,
                        _ => Operated(Max(left, right), ref lifted, arrayArgument),
                    };
                }

            case FunctionNode function:
                return Call(function, ref lifted, arrayArgument, locals);
            default:
                return Shape.Scalar;
        }
    }

    private static Shape Call(FunctionNode function, ref bool lifted, bool arrayArgument, Dictionary<string, Shape>? locals = null)
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

        if (name == "LET" && function.Arguments.Length % 2 == 1)
        {
            Dictionary<string, Shape> inner = locals is null ? new(StringComparer.Ordinal) : new(locals, StringComparer.Ordinal);
            for (int i = 0; i + 1 < function.Arguments.Length; i += 2)
            {
                Shape value = Walk(function.Arguments[i + 1].Node, ref lifted, arrayArgument, inner);
                if (FormulaCompiler.LocalName(function.Arguments[i].Node).Name is { } local)
                {
                    inner[local] = value;
                }
            }

            return Walk(function.Arguments[^1].Node, ref lifted, arrayArgument, inner);
        }

        // A LAMBDA is one value, and its parameters may be given ranges: an operator on one makes the formula dynamic.
        if (name == "LAMBDA")
        {
            Dictionary<string, Shape> inner = locals is null ? new(StringComparer.Ordinal) : new(locals, StringComparer.Ordinal);
            for (int i = 0; i + 1 < function.Arguments.Length; i++)
            {
                if (FormulaCompiler.LocalName(function.Arguments[i].Node).Name is { } parameter)
                {
                    inner[parameter] = Shape.Range;
                }
            }

            if (function.Arguments.Length > 0)
            {
                Walk(function.Arguments[^1].Node, ref lifted, arrayArgument: false, inner);
            }

            return Shape.Scalar;
        }

        bool known = FormulaFunctions.TryGet(name, out FormulaFunction? body);

        // A name may stand for a LAMBDA, so Excel treats what it does not know as able to give an array.
        Shape unknown = known || FormulaFunctions.TryGetAddIn(name, out _) ? Shape.Scalar : Shape.Array;
        bool chooser = Choosers.Contains(name);
        Shape result = ArrayResults.Contains(name) ? Shape.Array : unknown;
        for (int i = 0; i < function.Arguments.Length; i++)
        {
            bool takesArray = known && body!.TakesArray(i);
            Shape shape = Walk(function.Arguments[i].Node, ref lifted, arrayArgument || takesArray, locals);
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
