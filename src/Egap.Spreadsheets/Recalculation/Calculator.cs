using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Recalculation;

/// <summary>
/// A workbook's calculation: its formulas compiled once, the dependency graph between them, and the cells changed since the
/// last calculation. Made by the first calculation and kept up to date from then on: a changed cell is noted, and read
/// again at the next calculation. A change to the sheets, names or tables makes it start again, since any of them can
/// change what a formula reads.
/// </summary>
internal sealed class Calculator
{
    /// <summary>How many times over a calculation goes on for spills that keep moving each other.</summary>
    private const int MaxSpillPasses = 16;

    private readonly HashSet<long> blockedSpills = [];
    private readonly List<CellKey> spillsMoved = [];

    /// <summary>Functions whose result can change with nothing they read changing, or whose references are worked out as they run.</summary>
    private static readonly HashSet<string> VolatileFunctions =
        new(["NOW", "TODAY", "RAND", "RANDBETWEEN", "RANDARRAY", "OFFSET", "INDIRECT", "CELL", "INFO", "SHEET", "SHEETS", "ANCHORARRAY"], StringComparer.Ordinal);

    /// <summary>The most cells an array formula's range may have to be calculated: 4,194,304.</summary>
    private const long MaxArrayCells = 1 << 22;

    private readonly Workbook book;
    private readonly DependencyGraph graph = new();
    private readonly Dictionary<long, FormulaCell> cells = [];
    private readonly Dictionary<Worksheet, int> ids = [];
    private readonly List<Worksheet> sheets = [];
    private readonly HashSet<long> noted = [];
    private readonly Dictionary<DefinedName, CompiledFormula?> names = [];
    private readonly Dictionary<DefinedName, ParsedFormula?> parsedNames = [];
    private readonly Dictionary<(Worksheet, string), (CompiledFormula Compiled, ParsedFormula Parsed, CellAddress Origin)?> shared = [];
    private string signature = "";

    public Calculator(Workbook book)
    {
        this.book = book;
    }

    /// <summary>Notes that a cell's value or formula changes; it is read again at the next calculation.</summary>
    public void Noted(Worksheet sheet, int row, int column)
    {
        if (ids.TryGetValue(sheet, out int id))
        {
            noted.Add(new CellKey(id, row, column).Packed);
        }
    }

    /// <summary>
    /// Calculates the formulas that need it: with <paramref name="full"/>, every formula; otherwise those reading a cell changed
    /// since the last calculation, the volatile ones, and those reading their results. The first calculation of a workbook
    /// changed since it was loaded or made calculates every formula.
    /// </summary>
    public CalculationResult Calculate(bool full)
    {
        string now = Signature();
        if (now != signature || sheets.Count == 0)
        {
            bool first = signature.Length == 0;
            Build();
            signature = now;
            if (!first || book.ChangeCount > 0)
            {
                full = true;
            }
            else
            {
                // A workbook as loaded: its formulas hold the results saved with it.
                graph.ForgetChanges();
            }
        }
        else
        {
            foreach (long packed in noted)
            {
                CellKey key = CellKey.Unpack(packed);
                Register(sheets[key.Sheet], key.Row, key.Column);
                graph.Changed(key);
            }
        }

        noted.Clear();
        if (full)
        {
            graph.ChangedAll();
        }

        CalculationSettings settings = book.Calculation;
        long calculated = 0;
        CalculationOutcome outcome;
        int pass = 0;
        while (true)
        {
            // A formula with no room may find room once anything has changed.
            if (graph.HasChanges)
            {
                foreach (long packed in blockedSpills)
                {
                    graph.Changed(CellKey.Unpack(packed));
                }
            }

            spillsMoved.Clear();
            outcome = Recalculator.Run(graph.Plan(), Calculate, settings, settings.Concurrent, key => cells[key.Packed].Output is not null);
            calculated += outcome.Calculated;

            // A spill that moved has new cells, and what reads them may have run before it: they are calculated again.
            if (spillsMoved.Count == 0 || ++pass == MaxSpillPasses)
            {
                break;
            }

            foreach (CellKey moved in spillsMoved)
            {
                graph.Changed(moved);
            }
        }

        List<IReadOnlyList<Cell>> circular = [.. outcome.Circular.Select(ring => (IReadOnlyList<Cell>)[.. ring.Select(k => sheets[k.Sheet][k.Row, k.Column])])];
        return new CalculationResult(calculated, circular, outcome.Iterations, outcome.Settled);
    }

    /// <summary>What a formula can read besides cells: the sheets in order, the names and their formulas, and the tables.</summary>
    private string Signature()
    {
        StringBuilder text = new();
        text.Append(FormulaFunctions.UserVersion).Append('|');
        foreach (Worksheet sheet in book.Worksheets)
        {
            text.Append(sheet.GetHashCode()).Append(':').Append(sheet.Name).Append('|');
            foreach (SheetTable table in sheet.Tables)
            {
                text.Append(table.Name).Append('@').Append(table.Range).Append(table.ShowHeaderRow).Append(table.ShowTotalsRow);
                foreach (string column in table.Columns.Select(c => c.Name))
                {
                    text.Append(',').Append(column);
                }

                text.Append('|');
            }
        }

        foreach (DefinedName name in book.Names)
        {
            text.Append(name.Sheet?.GetHashCode()).Append('!').Append(name.Name).Append('=').Append(name.Formula).Append('|');
        }

        return text.ToString();
    }

    /// <summary>Starts again: every sheet's formulas compiled and entered into an empty graph.</summary>
    private void Build()
    {
        foreach (long packed in cells.Keys.ToList())
        {
            graph.Remove(CellKey.Unpack(packed));
        }

        cells.Clear();
        blockedSpills.Clear();
        ids.Clear();
        sheets.Clear();
        shared.Clear();
        names.Clear();
        parsedNames.Clear();
        foreach (Worksheet sheet in book.Worksheets)
        {
            ids[sheet] = sheets.Count;
            sheets.Add(sheet);
        }

        // Every name compiled before calculating, so contexts on several threads only read them.
        foreach (DefinedName name in book.Names)
        {
            ParsedFormula? parsed = FormulaParser.TryParse(name.Formula, r1c1: false, default);
            parsedNames[name] = parsed;
            names[name] = parsed is null ? null : FormulaCompiler.Compile(parsed);
        }

        foreach (Worksheet sheet in sheets)
        {
            if (sheet.Store.Bounds() is not { } bounds)
            {
                continue;
            }

            foreach ((int row, int column) in sheet.Store.Occupied(bounds))
            {
                if (sheet.Store.Get(row, column).Kind == CellKind.Formula)
                {
                    Register(sheet, row, column);
                }
            }
        }
    }

    /// <summary>Reads a cell again: its formula compiled and entered into the graph with what it reads, or taken out.</summary>
    private void Register(Worksheet sheet, int row, int column)
    {
        CellKey key = new(ids[sheet], row, column);
        ref readonly CellRecord record = ref sheet.Store.Get(row, column);
        FormulaCell? cell = record.Kind == CellKind.Formula ? Compile(sheet, row, column, sheet.Formulas[(int)record.Payload]) : null;
        if (cell is null)
        {
            if (cells.Remove(key.Packed))
            {
                graph.Remove(key);
            }

            return;
        }

        List<Precedent> precedents = [];
        bool isVolatile = false;
        EvalContext context = EvalContext.ForCell(sheet, row, column, cell.Origin, names, cell.ArrayMode);
        Analyze(cell.Parsed.Root, context, precedents, ref isVolatile);
        cells[key.Packed] = cell;
        graph.Set(key, CollectionsMarshal.AsSpan(precedents), isVolatile, cell.Output);
    }

    /// <summary>
    /// A formula cell ready to calculate, or null for one that is not calculated: a data table's cells, a formula Egap cannot
    /// read, or a cell sharing a formula whose first cell is gone. Those keep the result they have.
    /// </summary>
    private FormulaCell? Compile(Worksheet sheet, int row, int column, FormulaEntry entry)
    {
        string? kind = entry.Attribute("t");
        if (kind == "dataTable")
        {
            return null;
        }

        CellAddress at = new(row, column);
        if (entry.Text.Length == 0)
        {
            if (entry.SharedIndex is not { } si || Shared(sheet, si) is not { } master)
            {
                return null;
            }

            return new FormulaCell(sheet, row, column, entry, master.Compiled, master.Parsed, master.Origin, ArrayMode: false, Output: null);
        }

        if (FormulaParser.TryParse(entry.Text, r1c1: false, at) is not { } parsed)
        {
            return null;
        }

        CompiledFormula compiled = FormulaCompiler.Compile(parsed);
        if (entry.SharedIndex is { } index)
        {
            shared[(sheet, index)] = (compiled, parsed, at);
        }

        RangeAddress? output = entry.ArrayRange is { } range && range.First == at && (range.CellCount > 1 || sheet.IsDynamic(entry)) ? range : null;
        if (output is { CellCount: > MaxArrayCells })
        {
            // A range this large is not something Excel fills either; the cells keep what they hold.
            return null;
        }

        return new FormulaCell(sheet, row, column, entry, compiled, parsed, at, ArrayMode: kind == "array", output);
    }

    /// <summary>A shared formula's first cell, compiled, found on the sheet when not met yet.</summary>
    private (CompiledFormula Compiled, ParsedFormula Parsed, CellAddress Origin)? Shared(Worksheet sheet, string index)
    {
        if (shared.TryGetValue((sheet, index), out var known))
        {
            return known;
        }

        (CompiledFormula, ParsedFormula, CellAddress)? found = null;
        if (sheet.SharedMaster(index) is { } master && FormulaParser.TryParse(master.Text, r1c1: false, master.Cell) is { } parsed)
        {
            found = (FormulaCompiler.Compile(parsed), parsed, master.Cell);
        }

        shared[(sheet, index)] = found;
        return found;
    }

    private readonly HashSet<DefinedName> analyzedNames = [];

    /// <summary>
    /// Adds what a formula reads: the areas its references, names and structured references stand for, as the evaluator
    /// resolves them at the formula's cell, and the box spanned by a range built with ':'. A volatile function or a spill
    /// reference makes it volatile, since what it reads is only known as it runs.
    /// </summary>
    private void Analyze(FormulaNode node, EvalContext context, List<Precedent> precedents, ref bool isVolatile)
    {
        switch (node)
        {
            case ReferenceNode reference:
                Add(FormulaEvaluator.Reference(reference, context), precedents);
                break;
            case StructuredNode structured:
                Add(FormulaEvaluator.Structured(structured, context), precedents);
                break;
            case NameNode name:
                AnalyzeName(name, context, precedents, ref isVolatile);
                break;
            case FunctionNode function:
                string called = FormulaCompiler.FunctionName(function.Name);
                isVolatile |= VolatileFunctions.Contains(called);
                if (!FormulaFunctions.IsBuiltIn(called))
                {
                    // A name that stands for a LAMBDA: what its formula reads, this formula reads.
                    AnalyzeName(new NameNode(null, function.Name), context, precedents, ref isVolatile);
                }

                foreach (Argument argument in function.Arguments)
                {
                    Analyze(argument.Node, context, precedents, ref isVolatile);
                }

                break;
            case InvokeNode invoke:
                Analyze(invoke.Callee, context, precedents, ref isVolatile);
                foreach (Argument argument in invoke.Arguments)
                {
                    Analyze(argument.Node, context, precedents, ref isVolatile);
                }

                break;
            case ParenthesesNode parentheses:
                foreach (Argument item in parentheses.Items)
                {
                    Analyze(item.Node, context, precedents, ref isVolatile);
                }

                break;
            case UnaryNode unary:
                Analyze(unary.Operand, context, precedents, ref isVolatile);
                break;
            case PostfixNode postfix:
                isVolatile |= postfix.Operator == '#';
                Analyze(postfix.Operand, context, precedents, ref isVolatile);
                break;
            case BinaryNode binary:
                int start = precedents.Count;
                Analyze(binary.Left, context, precedents, ref isVolatile);
                Analyze(binary.Right, context, precedents, ref isVolatile);
                if (binary.Operator == BinaryOperator.Range)
                {
                    Span(precedents, start);
                }

                break;
        }
    }

    /// <summary>A name's formula analyzed as the evaluator runs it, or a table's data for a table's name.</summary>
    private void AnalyzeName(NameNode node, EvalContext context, List<Precedent> precedents, ref bool isVolatile)
    {
        SheetPrefix? prefix = node.Prefix;
        if (prefix is not null && prefix.Book is not (null or "0"))
        {
            return;
        }

        Worksheet? scope = context.Sheet;
        if (prefix?.FirstSheet is not null && !book.Worksheets.TryGet(prefix.FirstSheet, out scope))
        {
            return;
        }

        if (book.Names.TryGet(node.Name, scope, out DefinedName? name) || (prefix?.FirstSheet is null && book.Names.TryGet(node.Name, null, out name)))
        {
            // Once for a formula: a LAMBDA in a name may call itself, or names may call each other many times over.
            if (context.ForName(name.Sheet) is { } inner && parsedNames.GetValueOrDefault(name) is { } parsed && analyzedNames.Add(name))
            {
                Analyze(parsed.Root, inner, precedents, ref isVolatile);
                if (context.NameDepth == 0)
                {
                    analyzedNames.Clear();
                }
            }

            return;
        }

        foreach (Worksheet sheet in sheets)
        {
            foreach (SheetTable table in sheet.Tables)
            {
                if (string.Equals(table.Name, node.Name, StringComparison.OrdinalIgnoreCase))
                {
                    precedents.Add(new Precedent(ids[sheet], table.Range));
                    return;
                }
            }
        }
    }

    private void Add(in EvalValue value, List<Precedent> precedents)
    {
        if (value.Kind != EvalKind.Reference)
        {
            return;
        }

        foreach (SheetArea area in value.Areas)
        {
            if (ids.TryGetValue(area.Sheet, out int id))
            {
                precedents.Add(new Precedent(id, area.Range));
            }
        }
    }

    /// <summary>For a range built with ':', the box each sheet's areas span, which holds the cells between them too.</summary>
    private static void Span(List<Precedent> precedents, int start)
    {
        if (precedents.Count - start < 2)
        {
            return;
        }

        foreach (IGrouping<int, Precedent> group in precedents.Skip(start).GroupBy(p => p.Sheet).ToList())
        {
            int firstRow = group.Min(p => p.Range.First.Row), firstColumn = group.Min(p => p.Range.First.Column);
            int lastRow = group.Max(p => p.Range.Last.Row), lastColumn = group.Max(p => p.Range.Last.Column);
            precedents.Add(new Precedent(group.Key, new RangeAddress(firstRow, firstColumn, lastRow, lastColumn)));
        }
    }

    /// <summary>Calculates one formula cell and says how far its value moved.</summary>
    private double Calculate(CellKey key)
    {
        FormulaCell cell = cells[key.Packed];
        EvalContext context = EvalContext.ForCell(cell.Sheet, cell.Row, cell.Column, cell.Origin, names, cell.ArrayMode);
        if (cell.Output is not { } output)
        {
            CellValue result = FormulaEvaluator.Evaluate(cell.Compiled, context);
            return Move(ref cell.Entry.Result, result);
        }

        return cell.Sheet.IsDynamic(cell.Entry) ? Spill(key, cell, context, output) : FillArray(cell, context, output);
    }

    /// <summary>Calculates an array formula, which fills its range: a single row or column stretches across it, and #N/A goes past the array's end.</summary>
    private static double FillArray(FormulaCell cell, EvalContext context, RangeAddress output)
    {
        EvalValue value = FormulaEvaluator.Run(cell.Compiled, context);
        if (value.Kind == EvalKind.Reference)
        {
            value = FormulaEvaluator.ToArray(value);
        }

        (int rows, int columns) = FormulaEvaluator.Size(value);
        double change = 0;
        for (int row = output.First.Row; row <= output.Last.Row; row++)
        {
            for (int column = output.First.Column; column <= output.Last.Column; column++)
            {
                CellValue item = FormulaEvaluator.Element(value, rows, columns, row - output.First.Row, column - output.First.Column).ToCell();
                if (row == cell.Row && column == cell.Column)
                {
                    change = Math.Max(change, Move(ref cell.Entry.Result, item));
                }
                else
                {
                    CellValue old = cell.Sheet.GetValue(row, column);
                    cell.Sheet.SetCalculated(row, column, item);
                    change = Math.Max(change, Distance(old, item));
                }
            }
        }

        return change;
    }

    /// <summary>
    /// Calculates a dynamic array formula: its result fills as many cells as it has, unless a cell in the way, a merged cell or
    /// a table stops it, when the formula's own cell gives #SPILL!. Cells it filled before and does not fill now are emptied.
    /// </summary>
    private double Spill(CellKey key, FormulaCell cell, EvalContext context, RangeAddress old)
    {
        EvalValue value = FormulaEvaluator.Run(cell.Compiled, context);
        if (value.Kind == EvalKind.Reference)
        {
            value = FormulaEvaluator.ToArray(value);
        }

        (int rows, int columns) = FormulaEvaluator.Size(value);
        RangeAddress own = new(cell.Row, cell.Column, cell.Row, cell.Column);
        RangeAddress target = own;
        bool blocked = false;
        if (rows > 1 || columns > 1)
        {
            long lastRow = (long)cell.Row + rows - 1, lastColumn = (long)cell.Column + columns - 1;
            blocked = lastRow >= CellAddress.MaxRows || lastColumn >= CellAddress.MaxColumns;
            if (!blocked)
            {
                target = new RangeAddress(cell.Row, cell.Column, (int)lastRow, (int)lastColumn);
                blocked = InTheWay(cell.Sheet, target, old, own);
            }
        }

        if (blocked)
        {
            target = own;
            value = EvalValue.FromError(CellError.Spill);
            (rows, columns) = (1, 1);
            blockedSpills.Add(key.Packed);
        }
        else
        {
            blockedSpills.Remove(key.Packed);
        }

        double change = 0;
        if (old != target)
        {
            foreach ((int row, int column) in Cells(old))
            {
                if (!target.Contains(new CellAddress(row, column)))
                {
                    cell.Sheet.SetCalculated(row, column, CellValue.Empty);
                    spillsMoved.Add(new CellKey(key.Sheet, row, column));
                }
            }

            foreach ((int row, int column) in Cells(target))
            {
                if (!old.Contains(new CellAddress(row, column)))
                {
                    spillsMoved.Add(new CellKey(key.Sheet, row, column));
                }
            }

            cell.Entry.Attributes = [new XAttribute("t", "array"), new XAttribute("ref", target.ToString())];
            cells[key.Packed] = cell with { Output = target };
            graph.SetOutput(key, target);
            change = double.PositiveInfinity;
        }

        foreach ((int row, int column) in Cells(target))
        {
            CellValue item = FormulaEvaluator.Element(value, rows, columns, row - target.First.Row, column - target.First.Column).ToCell();
            if (row == cell.Row && column == cell.Column)
            {
                change = Math.Max(change, Move(ref cell.Entry.Result, item));
            }
            else
            {
                CellValue before = cell.Sheet.GetValue(row, column);
                cell.Sheet.SetCalculated(row, column, item);
                change = Math.Max(change, Distance(before, item));
            }
        }

        return change;
    }

    /// <summary>Whether a spill cannot go over a range: a cell it does not hold from before has something in it, or a merged cell or table is there.</summary>
    private static bool InTheWay(Worksheet sheet, RangeAddress target, RangeAddress old, RangeAddress own)
    {
        foreach (RangeAddress merged in sheet.MergedCells)
        {
            if (merged.Intersects(target))
            {
                return true;
            }
        }

        foreach (SheetTable table in sheet.Tables)
        {
            if (table.Range.Intersects(target))
            {
                return true;
            }
        }

        foreach ((int row, int column) in sheet.Store.Occupied(target))
        {
            CellAddress at = new(row, column);
            if (at != own.First && !old.Contains(at) && (sheet.HasFormula(row, column) || sheet.GetKind(row, column) != CellValueKind.Empty))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(int Row, int Column)> Cells(RangeAddress range)
    {
        for (int row = range.First.Row; row <= range.Last.Row; row++)
        {
            for (int column = range.First.Column; column <= range.Last.Column; column++)
            {
                yield return (row, column);
            }
        }
    }

    private static double Move(ref CellValue slot, CellValue value)
    {
        double distance = Distance(slot, value);
        slot = value;
        return distance;
    }

    /// <summary>How far a value moved: the difference of two numbers, 0 for an equal value, infinity for another kind.</summary>
    private static double Distance(CellValue old, CellValue value)
    {
        if (old.Kind == CellValueKind.Number && value.Kind == CellValueKind.Number)
        {
            return Math.Abs(old.Number - value.Number);
        }

        return old == value ? 0 : double.PositiveInfinity;
    }

    /// <summary>A formula cell: where it is, its entry, its compiled formula and the cell its text was written for.</summary>
    private sealed record FormulaCell(Worksheet Sheet, int Row, int Column, FormulaEntry Entry, CompiledFormula Compiled, ParsedFormula Parsed,
        CellAddress Origin, bool ArrayMode, RangeAddress? Output);
}
