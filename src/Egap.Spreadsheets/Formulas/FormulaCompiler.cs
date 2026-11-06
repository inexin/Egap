namespace Egap.Spreadsheets.Formulas;

/// <summary>What an <see cref="Instruction"/> does.</summary>
internal enum OpCode : byte
{
    /// <summary>Push <c>Constants[A]</c>.</summary>
    Constant,

    /// <summary>Push the reference <c>Operands[A]</c> (a <see cref="ReferenceNode"/>).</summary>
    Reference,

    /// <summary>Push what the name <c>Operands[A]</c> (a <see cref="NameNode"/>) stands for.</summary>
    Name,

    /// <summary>Push the cells the structured reference <c>Operands[A]</c> names.</summary>
    Structured,

    /// <summary>Negate the top value.</summary>
    Negate,

    /// <summary>Divide the top value by 100.</summary>
    Percent,

    /// <summary>The implicit intersection of the top value (<c>@</c>).</summary>
    Intersect,

    /// <summary>The spill range of the cell on top (<c>#</c>).</summary>
    Spill,

    /// <summary>Apply the binary operator <c>(BinaryOperator)A</c> to the two top values; <c>B</c> is 1 for a + or - that is the whole formula, where Excel drops rounding noise.</summary>
    Binary,

    /// <summary>The union of the <c>A</c> references on top.</summary>
    Union,

    /// <summary>Call the function <c>Operands[A]</c> (a <see cref="FunctionCall"/>).</summary>
    Call,

    /// <summary>IF's test: pop the condition; on FALSE jump to <c>A</c>, on an error push it and jump to <c>B</c>.</summary>
    IfTest,

    /// <summary>The end of IF's TRUE branch: jump to <c>A</c>, unless the IF at <c>B</c> takes both branches for an array.</summary>
    IfJump,

    /// <summary>The end of the IF at <c>A</c>: for an array condition, choose between the two branches element by element.</summary>
    IfEnd,

    /// <summary>IFERROR's or IFNA's test: unless the value is an error (only #N/A when <c>B</c> is 1), push it and jump to <c>A</c>.</summary>
    ErrorTest,

    /// <summary>The end of the IFERROR at <c>A</c>: for an array value, replace its errors with the other value's elements.</summary>
    ErrorEnd,

    /// <summary>Work out what follows in array mode, up to the matching <see cref="ArrayEnd"/>: an array argument.</summary>
    ArrayBegin,

    /// <summary>Back to the mode before the matching <see cref="ArrayBegin"/>.</summary>
    ArrayEnd,
}

/// <summary>One step of a compiled formula.</summary>
internal readonly record struct Instruction(OpCode Op, int A = 0, int B = 0);

/// <summary>A function and how many arguments a call passes.</summary>
internal sealed record FunctionCall(FormulaFunction Function, int Count);

/// <summary>
/// A formula compiled to a list of instructions for a stack machine: operands are pushed, operators and functions pop
/// theirs and push the result. IF, IFERROR and IFNA jump, so only the branch taken is evaluated.
/// </summary>
internal sealed class CompiledFormula(Instruction[] code, EvalValue[] constants, object[] operands, int maxStack)
{
    public Instruction[] Code { get; } = code;

    public EvalValue[] Constants { get; } = constants;

    public object[] Operands { get; } = operands;

    /// <summary>The most values the stack holds at once.</summary>
    public int MaxStack { get; } = maxStack;
}

/// <summary>Compiles a <see cref="ParsedFormula"/> into a <see cref="CompiledFormula"/>.</summary>
internal sealed class FormulaCompiler
{
    private readonly List<Instruction> code = [];
    private readonly List<EvalValue> constants = [];
    private readonly List<object> operands = [];
    private int depth;
    private int maxDepth;

    public static CompiledFormula Compile(ParsedFormula formula)
    {
        FormulaCompiler compiler = new();
        compiler.Emit(formula.Root);
        if (formula.Root is BinaryNode { Operator: BinaryOperator.Add or BinaryOperator.Subtract })
        {
            compiler.code[^1] = compiler.code[^1] with { B = 1 };
        }

        return new CompiledFormula([.. compiler.code], [.. compiler.constants], [.. compiler.operands], Math.Max(1, compiler.maxDepth));
    }

    /// <summary>
    /// A function's name without the <c>_xlfn.</c> and <c>_xlws.</c> prefixes Excel stores newer functions with and the
    /// <c>_xludf.</c> it stores user-defined ones with, upper case.
    /// </summary>
    public static string FunctionName(string name)
    {
        ReadOnlySpan<char> plain = name;
        while (true)
        {
            if (plain.StartsWith("_xlfn.", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("_xlws.", StringComparison.OrdinalIgnoreCase))
            {
                plain = plain[6..];
                continue;
            }

            if (plain.StartsWith("_xludf.", StringComparison.OrdinalIgnoreCase))
            {
                plain = plain[7..];
                continue;
            }

            return plain.ToString().ToUpperInvariant();
        }
    }

    private void Push(int count = 1)
    {
        depth += count;
        maxDepth = Math.Max(maxDepth, depth);
    }

    private void Constant(EvalValue value)
    {
        constants.Add(value);
        code.Add(new Instruction(OpCode.Constant, constants.Count - 1));
        Push();
    }

    private int Operand(object operand)
    {
        operands.Add(operand);
        return operands.Count - 1;
    }

    private void Emit(FormulaNode node)
    {
        switch (node)
        {
            case NumberNode number:
                Constant(EvalValue.FromNumber(number.Value));
                break;
            case StringNode text:
                Constant(EvalValue.FromText(text.Value));
                break;
            case BooleanNode boolean:
                Constant(EvalValue.FromBoolean(boolean.Value));
                break;
            case ErrorNode error:
                Constant(EvalValue.FromError(error.Error));
                break;
            case ArrayNode array:
                Constant(EvalValue.FromArray(new EvalArray(array.Rows, array.Columns, [.. array.Values.Select(EvalValue.FromCell)])));
                break;
            case MissingNode:
                Constant(EvalValue.Missing);
                break;
            case ReferenceNode:
                code.Add(new Instruction(OpCode.Reference, Operand(node)));
                Push();
                break;
            case NameNode:
                code.Add(new Instruction(OpCode.Name, Operand(node)));
                Push();
                break;
            case StructuredNode:
                code.Add(new Instruction(OpCode.Structured, Operand(node)));
                Push();
                break;
            case ParenthesesNode parentheses:
                foreach (Argument item in parentheses.Items)
                {
                    Emit(item.Node);
                }

                if (parentheses.Items.Length > 1)
                {
                    code.Add(new Instruction(OpCode.Union, parentheses.Items.Length));
                    depth -= parentheses.Items.Length - 1;
                }

                break;
            case UnaryNode unary:
                Emit(unary.Operand);
                if (unary.Operator != '+')
                {
                    code.Add(new Instruction(unary.Operator == '-' ? OpCode.Negate : OpCode.Intersect));
                }

                break;
            case PostfixNode postfix:
                Emit(postfix.Operand);
                code.Add(new Instruction(postfix.Operator == '%' ? OpCode.Percent : OpCode.Spill));
                break;
            case BinaryNode binary:
                Emit(binary.Left);
                Emit(binary.Right);
                code.Add(new Instruction(OpCode.Binary, (int)binary.Operator));
                depth--;
                break;
            case FunctionNode function:
                Function(function);
                break;
        }
    }

    private void Function(FunctionNode function)
    {
        string name = FunctionName(function.Name);
        Argument[] arguments = function.Arguments;
        if (arguments is [{ Node: MissingNode }] && function.Arguments[0].Node.Lead.Length > 0 && function.Arguments[0].Trail.Length == 0)
        {
            arguments = [];
        }

        switch (name)
        {
            case "SINGLE" when arguments.Length == 1:
                Emit(arguments[0].Node);
                code.Add(new Instruction(OpCode.Intersect));
                return;
            case "ANCHORARRAY" when arguments.Length == 1:
                Emit(arguments[0].Node);
                code.Add(new Instruction(OpCode.Spill));
                return;
            case "IF" when arguments.Length is 2 or 3:
                If(arguments);
                return;
            case "IFERROR" when arguments.Length == 2:
            case "IFNA" when arguments.Length == 2:
                IfError(arguments, name == "IFNA");
                return;
        }

        if (!FormulaFunctions.TryGet(name, out FormulaFunction? info) && !FormulaFunctions.TryGetAddIn(name, out info))
        {
            // A function nobody knows (or one stage B does not have yet) gives #NAME? without its arguments evaluated.
            Constant(EvalValue.FromError(CellError.Name));
            return;
        }

        if (arguments.Length < info.MinArguments || arguments.Length > info.MaxArguments)
        {
            Constant(EvalValue.FromError(CellError.Value));
            return;
        }

        for (int i = 0; i < arguments.Length; i++)
        {
            // A bare reference is passed as it is either way; only an expression over ranges needs array mode.
            bool array = info.TakesArray(i) && arguments[i].Node is not (ReferenceNode or MissingNode);
            if (array)
            {
                code.Add(new Instruction(OpCode.ArrayBegin));
            }

            Emit(arguments[i].Node);
            if (array)
            {
                code.Add(new Instruction(OpCode.ArrayEnd));
            }
        }

        code.Add(new Instruction(OpCode.Call, Operand(new FunctionCall(info, arguments.Length))));
        depth -= arguments.Length;
        Push();
    }

    private void Branch(FormulaNode node)
    {
        if (node is MissingNode)
        {
            // IF(TRUE,,1) gives a blank as Excel does: 0 in a cell or a sum, empty text beside text.
            Constant(EvalValue.Blank);
            return;
        }

        Emit(node);
    }

    /// <summary>
    /// IF as jumps. With an array condition both branches run: the condition stays on the stack while the TRUE branch runs,
    /// and the condition and the TRUE result while the FALSE branch runs, so each branch is compiled with those slots held
    /// and nested IFs add theirs on top.
    /// </summary>
    private void If(Argument[] arguments)
    {
        Emit(arguments[0].Node);
        int test = code.Count;
        code.Add(default);
        int start = depth - 1;
        depth = start + 1;
        Branch(arguments[1].Node);
        int jump = code.Count;
        code.Add(default);
        int otherwise = code.Count;
        depth = start + 2;
        if (arguments.Length == 3)
        {
            Branch(arguments[2].Node);
        }
        else
        {
            Constant(EvalValue.False);
        }

        int end = code.Count;
        code.Add(new Instruction(OpCode.IfEnd, test));
        code[test] = new Instruction(OpCode.IfTest, otherwise, end);
        code[jump] = new Instruction(OpCode.IfJump, end, test);
        depth = start + 1;
    }

    /// <summary>IFERROR and IFNA as jumps; an array value stays on the stack while the alternative runs.</summary>
    private void IfError(Argument[] arguments, bool onlyNotAvailable)
    {
        Branch(arguments[0].Node);
        int test = code.Count;
        code.Add(default);
        int start = depth - 1;
        depth = start + 1;
        Branch(arguments[1].Node);
        int end = code.Count;
        code.Add(new Instruction(OpCode.ErrorEnd, test));
        code[test] = new Instruction(OpCode.ErrorTest, end, onlyNotAvailable ? 1 : 0);
        depth = start + 1;
    }
}
