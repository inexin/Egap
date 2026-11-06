using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Egap.Spreadsheets.Formulas;

/// <summary>A LAMBDA's compiled form: its body, and where the values it keeps from around it go in its locals.</summary>
/// <param name="Body">The body; its first locals are the parameters.</param>
/// <param name="Required">How many parameters a call must give; the rest are optional.</param>
/// <param name="Count">How many parameters it has.</param>
/// <param name="Captures">Which locals of the formula around it are kept, and the local of the body each one becomes.</param>
internal sealed record LambdaCode(CompiledFormula Body, int Required, int Count, (int Outer, int Inner)[] Captures);

/// <summary>A value a formula can call: a LAMBDA with what it keeps from around it, or a function named as a value.</summary>
internal sealed class LambdaValue
{
    public LambdaValue(LambdaCode code, EvalValue[] captured, EvalContext context)
    {
        Code = code;
        Captured = captured;
        Context = context;
    }

    public LambdaValue(FormulaFunction function) => Function = function;

    public LambdaCode? Code { get; }

    public EvalValue[]? Captured { get; }

    /// <summary>Where the LAMBDA was written, which its body's references count from.</summary>
    public EvalContext? Context { get; }

    /// <summary>The function a function's name stands for, as in <c>BYROW(A1:B3,SUM)</c>.</summary>
    public FormulaFunction? Function { get; }

    /// <summary>Whether a call may pass this many arguments.</summary>
    public bool Accepts(int count) => Code is { } code
        ? count >= code.Required && count <= code.Count
        : count >= Function!.MinArguments && count <= Function.MaxArguments;
}

/// <summary>Makes and calls <see cref="LambdaValue"/>s.</summary>
internal static class Lambdas
{
    /// <summary>
    /// How deep calls may go before they give #NUM!. Excel's own limit depends on the formula, as it is its stack that runs
    /// out: <c>1+F(n-1)</c> stopped between 5,000 and 5,500 calls and <c>F(n-1)</c> between 8,000 and 9,000.
    /// </summary>
    private const int MaxDepth = 8192;

    /// <summary>How many calls one call at the top may lead to before it gives #NUM!, so a recursion that branches ends.</summary>
    private const long MaxCalls = 5_000_000;

    /// <summary>The stack for calls that go deeper than the thread's own stack allows.</summary>
    private const int LargeStack = 1 << 28;

    [ThreadStatic]
    private static int depth;

    [ThreadStatic]
    private static long calls;

    /// <summary>The value of a LAMBDA where it is written: it keeps the values it reads from around it.</summary>
    public static EvalValue Make(LambdaCode code, EvalValue[]? locals, EvalContext context)
    {
        EvalValue[] captured = code.Captures.Length == 0 ? [] : new EvalValue[code.Captures.Length];
        for (int i = 0; i < captured.Length; i++)
        {
            captured[i] = locals![code.Captures[i].Outer];
        }

        return EvalValue.FromLambda(new LambdaValue(code, captured, context.ForLambda()));
    }

    /// <summary>
    /// Calls a value: a LAMBDA runs its body, with arguments left out being blank to the body and omitted to ISOMITTED. An
    /// error stays itself, a reference gives #REF! and any other value #VALUE!, as in Excel.
    /// </summary>
    public static EvalValue Apply(in EvalValue callee, ReadOnlySpan<EvalValue> arguments, EvalContext context)
    {
        switch (callee.Kind)
        {
            case EvalKind.Lambda:
                break;
            case EvalKind.Error:
                return callee;
            case EvalKind.Reference:
                return EvalValue.FromError(CellError.Reference);
            default:
                return EvalValue.FromError(CellError.Value);
        }

        LambdaValue lambda = callee.Lambda;
        if (!lambda.Accepts(arguments.Length))
        {
            return EvalValue.FromError(CellError.Value);
        }

        if (lambda.Function is { } function)
        {
            return FormulaEvaluator.Call(function, arguments, context.InArrayMode());
        }

        LambdaCode code = lambda.Code!;
        EvalValue[] locals = new EvalValue[code.Body.LocalCount];
        arguments.CopyTo(locals);
        for (int i = arguments.Length; i < code.Count; i++)
        {
            locals[i] = EvalValue.Missing;
        }

        for (int i = 0; i < code.Captures.Length; i++)
        {
            locals[code.Captures[i].Inner] = lambda.Captured![i];
        }

        if (depth == 0)
        {
            calls = 0;
        }

        if (depth >= MaxDepth || ++calls > MaxCalls)
        {
            return EvalValue.FromError(CellError.Number);
        }

        depth++;
        try
        {
            return RuntimeHelpers.TryEnsureSufficientExecutionStack()
                ? FormulaEvaluator.Run(code.Body, lambda.Context!, locals)
                : OnLargerStack(code.Body, lambda.Context!, locals);
        }
        finally
        {
            depth--;
        }
    }

    private static EvalValue OnLargerStack(CompiledFormula body, EvalContext context, EvalValue[] locals)
    {
        EvalValue result = default;
        ExceptionDispatchInfo? failure = null;
        (int depthNow, long callsNow) = (depth, calls);
        Thread thread = new(
            () =>
            {
                (depth, calls) = (depthNow, callsNow);
                try
                {
                    result = FormulaEvaluator.Run(body, context, locals);
                    callsNow = calls;
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            },
            LargeStack);
        thread.Start();
        thread.Join();
        calls = callsNow;
        failure?.Throw();
        return result;
    }

    /// <summary>What a function that takes no LAMBDA makes of one: most give #VALUE!, and these are what Excel gives.</summary>
    public static EvalValue GivenToOther(FormulaFunction function) => function.Name switch
    {
        "TYPE" => EvalValue.FromNumber(128),
        "ROWS" or "COLUMNS" or "COUNTA" => EvalValue.FromNumber(1),
        "COUNT" => EvalValue.FromNumber(0),
        "ERROR.TYPE" => EvalValue.FromError(CellError.NotAvailable),
        "INDEX" or "VSTACK" or "HSTACK" => EvalValue.FromError(CellError.Calc),
        "ISNONTEXT" => EvalValue.True,
        "ISBLANK" or "ISERR" or "ISERROR" or "ISLOGICAL" or "ISNA" or "ISNUMBER" or "ISREF" or "ISTEXT" => EvalValue.False,
        _ => EvalValue.FromError(CellError.Value),
    };
}
