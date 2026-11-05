using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// The formula engine's baselines: parsing a mix of typical formulas, printing them in R1C1 style, compiling them, and
/// evaluating compiled formulas over a sheet of 1,000 rows (a SUM of a column, cell arithmetic, an IF chain), and the
/// functions that walk ranges: an exact VLOOKUP, COUNTIF with a condition, and SUMPRODUCT of two columns.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs instance methods.")]
public class FormulaBenchmarks
{
    private static readonly string[] Formulas =
    [
        "SUM(B2:B1001)",
        "A2*B2+C2/2-1",
        "IF(B2>500,\"high\",IF(B2>100,\"mid\",\"low\"))",
        "IFERROR(B2/C2,0)",
        "SUM('Other sheet'!$A$1:$C$10,Totals)*Rate",
        "AND(B2>0,C2<>\"\",OR(D2,E2))",
        "Sales[[#This Row],[Amount]]*(1+Sales[@Tax])",
        "DATE(2024,MONTH(A2),1)+ROW()-1",
        "{1,2,3;4,5,6}*2&\"x\"",
        "_xlfn.XLOOKUP(A2,Prices[Item],Prices[Price],\"none\")",
    ];

    private ParsedFormula[] parsed = [];
    private CompiledFormula sum = null!;
    private CompiledFormula arithmetic = null!;
    private CompiledFormula conditional = null!;
    private CompiledFormula lookup = null!;
    private CompiledFormula countIf = null!;
    private CompiledFormula sumProduct = null!;
    private Worksheet sheet = null!;

    [GlobalSetup]
    public void Setup()
    {
        parsed = [.. Formulas.Select(f => FormulaParser.Parse(f, r1c1: false, new CellAddress(1, 5)))];
        sheet = Workbook.Create().Worksheets[0];
        for (int row = 0; row < 1000; row++)
        {
            sheet.Cells.SetValue(row + 1, 0, row);
            sheet.Cells.SetValue(row + 1, 1, row * 1.5);
            sheet.Cells.SetValue(row + 1, 2, (row % 7) + 1);
        }

        sum = Compile("SUM(B2:B1001)");
        arithmetic = Compile("A2*B2+C2/2-1");
        conditional = Compile("IF(B2>500,\"high\",IF(B2>100,\"mid\",\"low\"))");
        lookup = Compile("VLOOKUP(990,A2:C1001,3,FALSE)");
        countIf = Compile("COUNTIF(B2:B1001,\">500\")");
        sumProduct = Compile("SUMPRODUCT((C2:C1001>3)*B2:B1001)");
    }

    private static CompiledFormula Compile(string formula) => FormulaCompiler.Compile(FormulaParser.Parse(formula, r1c1: false, default));

    [Benchmark(Description = "Parse 10 formulas")]
    public int Parse()
    {
        int nodes = 0;
        foreach (string formula in Formulas)
        {
            nodes += FormulaParser.Parse(formula, r1c1: false, new CellAddress(1, 5)).Trail.Length + 1;
        }

        return nodes;
    }

    [Benchmark(Description = "Print 10 formulas as R1C1")]
    public int PrintR1C1()
    {
        int length = 0;
        foreach (ParsedFormula formula in parsed)
        {
            length += FormulaPrinter.Print(formula, ReferenceStyle.R1C1).Length;
        }

        return length;
    }

    [Benchmark(Description = "Compile 10 formulas")]
    public int CompileAll()
    {
        int instructions = 0;
        foreach (ParsedFormula formula in parsed)
        {
            instructions += FormulaCompiler.Compile(formula).Code.Length;
        }

        return instructions;
    }

    [Benchmark(Description = "Evaluate SUM of 1,000 cells")]
    public double EvaluateSum() => FormulaEvaluator.Evaluate(sum, new EvalContext(sheet, 0, 5)).Number;

    [Benchmark(Description = "Evaluate cell arithmetic 1,000 times")]
    public double EvaluateArithmetic()
    {
        double total = 0;
        for (int row = 1; row <= 1000; row++)
        {
            total += FormulaEvaluator.Evaluate(arithmetic, new EvalContext(sheet, row, 5)).Number;
        }

        return total;
    }

    [Benchmark(Description = "Evaluate an IF chain 1,000 times")]
    public int EvaluateConditional()
    {
        int length = 0;
        for (int row = 1; row <= 1000; row++)
        {
            length += FormulaEvaluator.Evaluate(conditional, new EvalContext(sheet, row, 5)).Text.Length;
        }

        return length;
    }

    [Benchmark(Description = "Evaluate VLOOKUP exact in 1,000 rows")]
    public double EvaluateLookup() => FormulaEvaluator.Evaluate(lookup, new EvalContext(sheet, 0, 5)).Number;

    [Benchmark(Description = "Evaluate COUNTIF over 1,000 cells")]
    public double EvaluateCountIf() => FormulaEvaluator.Evaluate(countIf, new EvalContext(sheet, 0, 5)).Number;

    [Benchmark(Description = "Evaluate SUMPRODUCT of 1,000 rows")]
    public double EvaluateSumProduct() => FormulaEvaluator.Evaluate(sumProduct, new EvalContext(sheet, 0, 5)).Number;
}
