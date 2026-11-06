using System.Globalization;

namespace Egap.Spreadsheets.Formulas;

/// <summary>Where and how a formula is evaluated.</summary>
internal sealed class EvalContext
{
    /// <summary>The most cells a reference may turn into as an array, so a whole-column operation stays bounded.</summary>
    public const long MaxArrayCells = 1 << 22;

    /// <summary>How deeply names may refer to names before the evaluator gives up with #NAME?.</summary>
    private const int MaxNameDepth = 32;

    public EvalContext(Worksheet sheet, int row, int column)
    {
        Sheet = sheet;
        Book = sheet.Workbook;
        Row = row;
        Column = column;
        Culture = Book.Culture;
        Date1904 = Book.Date1904;
    }

    public Workbook Book { get; }

    /// <summary>The sheet the formula is on; references without a sheet name are to it.</summary>
    public Worksheet Sheet { get; private init; }

    /// <summary>The formula's cell, for implicit intersection, ROW() and [#This Row].</summary>
    public int Row { get; }

    public int Column { get; }

    public CultureInfo Culture { get; }

    public bool Date1904 { get; }

    /// <summary>
    /// Whether references become arrays when an operator or a value argument takes them, as in an array formula; otherwise a
    /// range meets the formula's cell by implicit intersection, as in an ordinary formula.
    /// </summary>
    public bool ArrayMode { get; init; }

    /// <summary>How far relative references move: the formula's cell from the cell its text was written for.</summary>
    public int RowDelta { get; private init; }

    public int ColumnDelta { get; private init; }

    /// <summary>Whether references that move off the sheet wrap around, as in a defined name, rather than become #REF!.</summary>
    public bool Wrap { get; private init; }

    public int NameDepth { get; private init; }

    /// <summary>Compiled defined names, shared by the contexts of one evaluation.</summary>
    public Dictionary<DefinedName, CompiledFormula?> Names { get; private init; } = [];

    /// <summary>
    /// The context a LAMBDA's body runs in: where it was written, in array mode as a formula that calls one is. The count of
    /// names starts again: a LAMBDA in a name may call itself, which only <see cref="Lambdas"/> limits.
    /// </summary>
    public EvalContext ForLambda() => ArrayMode && NameDepth == 0 ? this : new EvalContext(Sheet, Row, Column)
    {
        ArrayMode = true,
        RowDelta = RowDelta,
        ColumnDelta = ColumnDelta,
        Wrap = Wrap,
        Names = Names,
    };

    /// <summary>This context in array mode, for an array argument; itself when it is in array mode already.</summary>
    public EvalContext InArrayMode() => ArrayMode ? this : new EvalContext(Sheet, Row, Column)
    {
        ArrayMode = true,
        RowDelta = RowDelta,
        ColumnDelta = ColumnDelta,
        Wrap = Wrap,
        NameDepth = NameDepth,
        Names = Names,
    };

    /// <summary>
    /// The context for a reference written as text, as INDIRECT reads one: references are where they say, counted from the
    /// formula's own cell, not moved; null when references made from text nest too deeply.
    /// </summary>
    public EvalContext? ForText() => NameDepth >= MaxNameDepth
        ? null
        : new EvalContext(Sheet, Row, Column) { ArrayMode = ArrayMode, NameDepth = NameDepth + 1, Names = Names };

    /// <summary>
    /// The context for a cell's formula whose text was written for another cell, as a shared formula's cells share the text
    /// of its first: relative references move by the distance between the two. Compiled names come from
    /// <paramref name="names"/>, so contexts calculating at the same time can share them when every name is in it already.
    /// </summary>
    public static EvalContext ForCell(Worksheet sheet, int row, int column, CellAddress origin, Dictionary<DefinedName, CompiledFormula?> names, bool arrayMode) =>
        new(sheet, row, column)
        {
            RowDelta = row - origin.Row,
            ColumnDelta = column - origin.Column,
            Names = names,
            ArrayMode = arrayMode,
        };

    /// <summary>The context for a formula that stays in its cell while its relative references move down, as a database criterion's do.</summary>
    public static EvalContext ForMovedDown(Worksheet sheet, int row, int column, int rows, Dictionary<DefinedName, CompiledFormula?> names) =>
        new(sheet, row, column)
        {
            RowDelta = rows,
            Names = names,
        };

    /// <summary>
    /// The context for a defined name's formula: relative references in a name count from A1, so they move by the
    /// formula's cell and wrap around the sheet.
    /// </summary>
    public EvalContext? ForName(Worksheet? scope) => NameDepth >= MaxNameDepth
        ? null
        : new EvalContext(scope ?? Sheet, Row, Column)
        {
            ArrayMode = ArrayMode,
            RowDelta = Row,
            ColumnDelta = Column,
            Wrap = true,
            NameDepth = NameDepth + 1,
            Names = Names,
        };
}

/// <summary>
/// Runs a <see cref="CompiledFormula"/>. Values follow Excel: errors are values that flow through operators, text meets
/// arithmetic as a number when it reads as one, ranges meet the formula's cell by implicit intersection (or become arrays in
/// array mode), arrays combine element by element, and a result that is an addition or subtraction close to zero next to
/// its operands is 0.
/// </summary>
internal static class FormulaEvaluator
{
    /// <summary>Evaluates a formula as the formula of a cell and gives the cell's value.</summary>
    public static CellValue Evaluate(CompiledFormula formula, EvalContext context) => Result(Run(formula, context), context);

    /// <summary>The value a cell shows for what its formula gave: the cell a range meets, an array's first element, 0 for blank.</summary>
    public static CellValue Result(in EvalValue value, EvalContext context)
    {
        EvalValue result = value;
        if (result.Kind == EvalKind.Reference)
        {
            result = Deref(IntersectReference(result, context));
        }

        if (result.Kind == EvalKind.Array)
        {
            result = result.Array.Items[0];
        }

        return result.ToCell();
    }

    /// <summary>Runs the formula; the result may be a reference or an array.</summary>
    /// <param name="formula">The formula.</param>
    /// <param name="context">Where and how it runs.</param>
    /// <param name="locals">For a LAMBDA's body, its parameters and what it keeps.</param>
    public static EvalValue Run(CompiledFormula formula, EvalContext context, EvalValue[]? locals = null)
    {
        Instruction[] code = formula.Code;
        if (locals is null && formula.LocalCount > 0)
        {
            locals = new EvalValue[formula.LocalCount];
        }

        EvalValue[] stack = new EvalValue[formula.MaxStack + 2];
        List<int>? pending = null;
        List<EvalContext>? outer = null;
        EvalContext? arrayContext = null;
        int sp = 0;
        for (int pc = 0; pc < code.Length; pc++)
        {
            Instruction instruction = code[pc];
            switch (instruction.Op)
            {
                case OpCode.Constant:
                    stack[sp++] = formula.Constants[instruction.A];
                    break;
                case OpCode.Reference:
                    stack[sp++] = Reference((ReferenceNode)formula.Operands[instruction.A], context);
                    break;
                case OpCode.Name:
                    stack[sp++] = Name((NameNode)formula.Operands[instruction.A], context);
                    break;
                case OpCode.Structured:
                    stack[sp++] = Structured((StructuredNode)formula.Operands[instruction.A], context);
                    break;
                case OpCode.Negate:
                    stack[sp - 1] = Map(Operand(stack[sp - 1], context), context, static (v, c) =>
                        ExcelValues.TryNumber(v, c, out double n, out EvalValue e) ? EvalValue.FromNumber(-n) : e);
                    break;
                case OpCode.Percent:
                    stack[sp - 1] = Map(Operand(stack[sp - 1], context), context, static (v, c) =>
                        ExcelValues.TryNumber(v, c, out double n, out EvalValue e) ? EvalValue.FromNumber(n / 100) : e);
                    break;
                case OpCode.Intersect:
                    stack[sp - 1] = Intersect(stack[sp - 1], context);
                    break;
                case OpCode.Spill:
                    stack[sp - 1] = Spill(stack[sp - 1]);
                    break;
                case OpCode.Binary:
                    sp--;
                    stack[sp - 1] = Binary((BinaryOperator)instruction.A, stack[sp - 1], stack[sp], context, last: instruction.B == 1);
                    break;
                case OpCode.Union:
                    sp -= instruction.A - 1;
                    stack[sp - 1] = Union(stack.AsSpan(sp - 1, instruction.A));
                    break;
                case OpCode.Call:
                    FunctionCall call = (FunctionCall)formula.Operands[instruction.A];
                    sp -= call.Count;
                    EvalValue result = Call(call.Function, stack.AsSpan(sp, call.Count), context);
                    stack[sp++] = result;
                    break;
                case OpCode.IfTest:
                    {
                        EvalValue condition = Operand(stack[--sp], context);
                        if (condition.Kind == EvalKind.Array)
                        {
                            stack[sp++] = condition;
                            (pending ??= []).Add(pc);
                        }
                        else if (!ExcelValues.TryBoolean(condition, out bool test, out EvalValue error))
                        {
                            stack[sp++] = error;
                            pc = instruction.B - 1;
                        }
                        else if (!test)
                        {
                            pc = instruction.A - 1;
                        }

                        break;
                    }

                case OpCode.IfJump:
                    if (pending is not { Count: > 0 } || pending[^1] != instruction.B)
                    {
                        pc = instruction.A - 1;
                    }

                    break;
                case OpCode.IfEnd:
                    if (pending is { Count: > 0 } && pending[^1] == instruction.A)
                    {
                        pending.RemoveAt(pending.Count - 1);
                        sp -= 2;
                        stack[sp - 1] = ChooseElements(stack[sp - 1], Operand(stack[sp], context), Operand(stack[sp + 1], context), context);
                    }

                    break;
                case OpCode.ErrorTest:
                    {
                        EvalValue value = Operand(stack[--sp], context);
                        if (value.Kind == EvalKind.Array)
                        {
                            stack[sp++] = value;
                            (pending ??= []).Add(pc);
                        }
                        else if (!Catches(value, instruction.B == 1))
                        {
                            stack[sp++] = value;
                            pc = instruction.A - 1;
                        }

                        break;
                    }

                case OpCode.ArrayBegin:
                    (outer ??= []).Add(context);
                    context = context.ArrayMode ? context : arrayContext ??= context.InArrayMode();
                    break;
                case OpCode.ArrayEnd:
                    context = outer![^1];
                    outer.RemoveAt(outer.Count - 1);
                    break;
                case OpCode.ErrorEnd:
                    if (pending is { Count: > 0 } && pending[^1] == instruction.A)
                    {
                        bool onlyNotAvailable = code[instruction.A].B == 1;
                        pending.RemoveAt(pending.Count - 1);
                        sp--;
                        stack[sp - 1] = Broadcast(stack[sp - 1], Operand(stack[sp], context), context, onlyNotAvailable
                            ? static (v, alternative, _) => Catches(v, true) ? alternative : v
                            : static (v, alternative, _) => Catches(v, false) ? alternative : v);
                    }

                    break;
                case OpCode.LocalGet:
                    stack[sp++] = locals![instruction.A];
                    break;
                case OpCode.LocalSet:
                    locals![instruction.A] = stack[--sp];
                    break;
                case OpCode.MakeLambda:
                    stack[sp++] = Lambdas.Make((LambdaCode)formula.Operands[instruction.A], locals, context);
                    break;
                case OpCode.Invoke:
                    sp -= instruction.A;
                    stack[sp - 1] = Lambdas.Apply(stack[sp - 1], stack.AsSpan(sp, instruction.A), context);
                    break;
            }
        }

        return sp == 1 ? stack[0] : EvalValue.FromError(CellError.Value);
    }

    private static bool Catches(in EvalValue value, bool onlyNotAvailable) =>
        value.IsError && (!onlyNotAvailable || value.Error == CellError.NotAvailable);

    /// <summary>
    /// A value an operator or a value argument takes: a reference becomes the cell it meets the formula's cell at (or an
    /// array in array mode); a missing argument is blank.
    /// </summary>
    public static EvalValue Operand(in EvalValue value, EvalContext context)
    {
        switch (value.Kind)
        {
            case EvalKind.Reference:
                return context.ArrayMode ? ToArray(value) : Deref(IntersectReference(value, context));
            case EvalKind.Missing:
                return EvalValue.Blank;
            case EvalKind.Lambda:
                return EvalValue.FromError(CellError.Value);
            default:
                return value;
        }
    }

    /// <summary>The value of a reference to one cell; any other reference gives #VALUE!.</summary>
    public static EvalValue Deref(in EvalValue value)
    {
        if (value.Kind != EvalKind.Reference)
        {
            return value;
        }

        SheetArea[] areas = value.Areas;
        if (areas.Length != 1 || areas[0].Range.CellCount != 1)
        {
            return EvalValue.FromError(CellError.Value);
        }

        SheetArea area = areas[0];
        return EvalValue.FromCell(area.Sheet.GetValue(area.Range.First.Row, area.Range.First.Column));
    }

    /// <summary>The cells of a one-area reference as an array; a single cell stays a scalar.</summary>
    public static EvalValue ToArray(in EvalValue value)
    {
        SheetArea[] areas = value.Areas;
        if (areas.Length != 1)
        {
            return EvalValue.FromError(CellError.Value);
        }

        SheetArea area = areas[0];
        RangeAddress range = area.Range;
        if (range.CellCount == 1)
        {
            return Deref(value);
        }

        if (range.CellCount > EvalContext.MaxArrayCells)
        {
            return EvalValue.FromError(CellError.Value);
        }

        EvalValue[] items = new EvalValue[range.CellCount];
        foreach ((int row, int column) in area.Sheet.Store.Occupied(range))
        {
            items[((row - range.First.Row) * range.ColumnCount) + column - range.First.Column] = EvalValue.FromCell(area.Sheet.GetValue(row, column));
        }

        return EvalValue.FromArray(new EvalArray(range.RowCount, range.ColumnCount, items));
    }

    /// <summary>
    /// Implicit intersection of a reference with the formula's cell: a single cell is itself; a column meets the formula's row,
    /// a row its column. Anything else gives #VALUE!.
    /// </summary>
    public static EvalValue IntersectReference(in EvalValue value, EvalContext context)
    {
        SheetArea[] areas = value.Areas;
        if (areas.Length != 1)
        {
            return EvalValue.FromError(CellError.Value);
        }

        SheetArea area = areas[0];
        RangeAddress range = area.Range;
        if (range.CellCount == 1)
        {
            return value;
        }

        if (range.ColumnCount == 1 && context.Row >= range.First.Row && context.Row <= range.Last.Row)
        {
            return EvalValue.FromArea(area.Sheet, new RangeAddress(context.Row, range.First.Column, context.Row, range.First.Column));
        }

        if (range.RowCount == 1 && context.Column >= range.First.Column && context.Column <= range.Last.Column)
        {
            return EvalValue.FromArea(area.Sheet, new RangeAddress(range.First.Row, context.Column, range.First.Row, context.Column));
        }

        return EvalValue.FromError(CellError.Value);
    }

    private static EvalValue Intersect(in EvalValue value, EvalContext context) => value.Kind switch
    {
        EvalKind.Reference => IntersectReference(value, context),
        EvalKind.Array => value.Array.Items[0],
        _ => value,
    };

    /// <summary>The spill range of a dynamic array formula whose first cell is the reference; #REF! when the cell has none.</summary>
    private static EvalValue Spill(in EvalValue value)
    {
        if (value.Kind != EvalKind.Reference)
        {
            return value.IsError ? value : EvalValue.FromError(CellError.Reference);
        }

        SheetArea[] areas = value.Areas;
        if (areas.Length != 1 || areas[0].Range.CellCount != 1)
        {
            return EvalValue.FromError(CellError.Reference);
        }

        Worksheet sheet = areas[0].Sheet;
        CellAddress cell = areas[0].Range.First;
        ref readonly Storage.CellRecord record = ref sheet.Store.Get(cell.Row, cell.Column);
        if (record.Kind == Storage.CellKind.Formula && sheet.Formulas[(int)record.Payload].Attributes is { } attributes
            && attributes.FirstOrDefault(a => a.Name.LocalName == "t")?.Value == "array"
            && RangeAddress.TryParse(attributes.FirstOrDefault(a => a.Name.LocalName == "ref")?.Value, out RangeAddress spill)
            && spill.First == cell)
        {
            return EvalValue.FromArea(sheet, spill);
        }

        return EvalValue.FromError(CellError.Reference);
    }

    private static EvalValue Union(ReadOnlySpan<EvalValue> items)
    {
        List<SheetArea> areas = [];
        foreach (EvalValue item in items)
        {
            if (item.IsError)
            {
                return item;
            }

            if (item.Kind != EvalKind.Reference)
            {
                return EvalValue.FromError(CellError.Value);
            }

            areas.AddRange(item.Areas);
        }

        return EvalValue.FromAreas([.. areas]);
    }

    /// <summary>A reference resolved against the context: sheets found by name, relative coordinates moved by its delta.</summary>
    internal static EvalValue Reference(ReferenceNode node, EvalContext context)
    {
        Area area = node.Area;
        if (area.Kind == AreaKind.Error)
        {
            return EvalValue.FromError(CellError.Reference);
        }

        int firstRow = Move(area.FirstRow, area.FirstRowAbsolute, context.RowDelta, CellAddress.MaxRows, context.Wrap);
        int firstColumn = Move(area.FirstColumn, area.FirstColumnAbsolute, context.ColumnDelta, CellAddress.MaxColumns, context.Wrap);
        int lastRow = Move(area.LastRow, area.LastRowAbsolute, context.RowDelta, CellAddress.MaxRows, context.Wrap);
        int lastColumn = Move(area.LastColumn, area.LastColumnAbsolute, context.ColumnDelta, CellAddress.MaxColumns, context.Wrap);
        if (firstRow < 0 || firstColumn < 0 || lastRow < 0 || lastColumn < 0)
        {
            return EvalValue.FromError(CellError.Reference);
        }

        RangeAddress range = new(Math.Min(firstRow, lastRow), Math.Min(firstColumn, lastColumn), Math.Max(firstRow, lastRow), Math.Max(firstColumn, lastColumn));
        if (Sheets(node.Prefix, context) is not { } sheets)
        {
            return EvalValue.FromError(CellError.Reference);
        }

        // A linked book keeps no blanks, so a cell it doesn't hold is blank, unless Excel could not refresh the sheet at all.
        if (sheets[0].LinkRefreshFailed && range.First == range.Last && sheets[0].GetKind(range.First.Row, range.First.Column) == CellValueKind.Empty)
        {
            return EvalValue.FromError(CellError.Reference);
        }

        SheetArea[] areas = new SheetArea[sheets.Count];
        for (int i = 0; i < areas.Length; i++)
        {
            areas[i] = new SheetArea(sheets[i], range);
        }

        return EvalValue.FromAreas(areas);
    }

    private static int Move(int value, bool absolute, int delta, int count, bool wrap)
    {
        if (absolute || delta == 0)
        {
            return value;
        }

        int moved = value + delta;
        if (wrap)
        {
            return ((moved % count) + count) % count;
        }

        return moved >= 0 && moved < count ? moved : -1;
    }

    private static Xlsx.ExternalBook? ExternalBook(SheetPrefix prefix, EvalContext context)
    {
        return int.TryParse(prefix.Book, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) && number >= 1 && number <= context.Book.ExternalBooks.Count
            ? context.Book.ExternalBooks[number - 1]
            : null;
    }

    /// <summary>The sheets a prefix names: one, or every sheet from the first to the last for a 3D reference; null when one is missing.</summary>
    private static List<Worksheet>? Sheets(SheetPrefix? prefix, EvalContext context)
    {
        if (prefix is null)
        {
            return [context.Sheet];
        }

        // A linked book's sheet is the values the file keeps for it; [0] is the workbook itself.
        if (prefix.Book is not (null or "0"))
        {
            return prefix.FirstSheet is not null && (prefix.LastSheet is null || string.Equals(prefix.LastSheet, prefix.FirstSheet, StringComparison.OrdinalIgnoreCase))
                && ExternalBook(prefix, context)?.Sheet(prefix.FirstSheet) is { } cached ? [cached] : null;
        }

        if (prefix.FirstSheet is null)
        {
            return null;
        }

        WorksheetCollection worksheets = context.Book.Worksheets;
        if (!worksheets.TryGet(prefix.FirstSheet, out Worksheet? first))
        {
            return null;
        }

        if (prefix.LastSheet is null || string.Equals(prefix.LastSheet, prefix.FirstSheet, StringComparison.OrdinalIgnoreCase))
        {
            return [first];
        }

        if (!worksheets.TryGet(prefix.LastSheet, out Worksheet? last))
        {
            return null;
        }

        int from = Math.Min(first.Index, last.Index), to = Math.Max(first.Index, last.Index);
        List<Worksheet> sheets = [];
        for (int i = from; i <= to; i++)
        {
            sheets.Add(worksheets[i]);
        }

        return sheets;
    }

    /// <summary>What a name stands for: a defined name's formula evaluated, or a table's data; #NAME? for a name nobody knows.</summary>
    private static EvalValue Name(NameNode node, EvalContext context)
    {
        SheetPrefix? prefix = node.Prefix;
        if (prefix is not null && prefix.Book is not (null or "0"))
        {
            if (ExternalBook(prefix, context) is not { } linked)
            {
                return EvalValue.FromError(CellError.Reference);
            }

            if (linked.DdeItem(node.Name) is { } item)
            {
                return EvalValue.FromCell(item);
            }

            return linked.Name(node.Name) is { Prefix: { } target } reference
                ? Reference(new ReferenceNode(target with { Book = prefix.Book }, reference.Text, reference.Area), context)
                : EvalValue.FromError(linked.MissingName(node.Name));
        }

        Worksheet? scope = context.Sheet;
        if (prefix?.FirstSheet is not null)
        {
            if (!context.Book.Worksheets.TryGet(prefix.FirstSheet, out scope))
            {
                return EvalValue.FromError(CellError.Reference);
            }
        }

        DefinedNameCollection names = context.Book.Names;
        if (names.TryGet(node.Name, scope, out DefinedName? name) || (prefix?.FirstSheet is null && names.TryGet(node.Name, null, out name)))
        {
            if (context.ForName(name.Sheet) is not { } inner)
            {
                return EvalValue.FromError(CellError.Name);
            }

            if (!context.Names.TryGetValue(name, out CompiledFormula? compiled))
            {
                compiled = FormulaParser.TryParse(name.Formula, r1c1: false, default) is { } parsed ? FormulaCompiler.Compile(parsed) : null;
                context.Names[name] = compiled;
            }

            return compiled is null ? EvalValue.FromError(CellError.Name) : Run(compiled, inner);
        }

        if (prefix?.Book is null && FindTable(context.Book, node.Name) is { } table)
        {
            return TableArea(table, TableItems.None, null, null, context);
        }

        // A function's name as a value, as in BYROW(A1:B3,SUM); a file stores it with _xleta.
        string word = node.Name.StartsWith("_xleta.", StringComparison.OrdinalIgnoreCase) ? node.Name[7..] : node.Name;
        return prefix is null && FormulaFunctions.TryGet(FormulaCompiler.FunctionName(word), out FormulaFunction? function)
            ? EvalValue.FromLambda(new LambdaValue(function))
            : EvalValue.FromError(CellError.Name);
    }

    internal static SheetTable? FindTable(Workbook book, string name)
    {
        foreach (Worksheet sheet in book.Worksheets)
        {
            foreach (SheetTable table in sheet.Tables)
            {
                if (string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return table;
                }
            }
        }

        return null;
    }

    internal static EvalValue Structured(StructuredNode node, EvalContext context)
    {
        SheetTable? table = node.Table is null
            ? context.Sheet.Tables.Find(new CellAddress(context.Row, context.Column))
            : FindTable(context.Book, node.Table);
        return table is null
            ? EvalValue.FromError(CellError.Reference)
            : TableArea(table, node.Items, node.FirstColumn, node.LastColumn, context);
    }

    /// <summary>
    /// The cells of a table that items and columns name: the data rows by default, the header and totals rows when named
    /// (#REF! when the table shows none), the formula's row for [#This Row]; all columns, or one column or a range of them.
    /// </summary>
    private static EvalValue TableArea(SheetTable table, TableItems items, string? firstColumn, string? lastColumn, EvalContext context)
    {
        RangeAddress range = table.Range;
        int headerRow = range.First.Row;
        int dataFirst = range.First.Row + (table.ShowHeaderRow ? 1 : 0);
        int dataLast = range.Last.Row - (table.ShowTotalsRow ? 1 : 0);
        int totalsRow = range.Last.Row;
        if ((items.HasFlag(TableItems.Headers) && !table.ShowHeaderRow) || (items.HasFlag(TableItems.Totals) && !table.ShowTotalsRow))
        {
            return EvalValue.FromError(CellError.Reference);
        }

        int first, last;
        if (items == TableItems.None || items == TableItems.Data)
        {
            (first, last) = (dataFirst, dataLast);
        }
        else if (items.HasFlag(TableItems.All))
        {
            (first, last) = (range.First.Row, range.Last.Row);
        }
        else if (items == TableItems.ThisRow)
        {
            if (context.Row < dataFirst || context.Row > dataLast)
            {
                return EvalValue.FromError(CellError.Value);
            }

            (first, last) = (context.Row, context.Row);
        }
        else if (items == TableItems.Headers)
        {
            (first, last) = (headerRow, headerRow);
        }
        else if (items == TableItems.Totals)
        {
            (first, last) = (totalsRow, totalsRow);
        }
        else if (items == (TableItems.Headers | TableItems.Data))
        {
            (first, last) = (headerRow, dataLast);
        }
        else if (items == (TableItems.Data | TableItems.Totals))
        {
            (first, last) = (dataFirst, totalsRow);
        }
        else
        {
            return EvalValue.FromError(CellError.Reference);
        }

        if (last < first)
        {
            return EvalValue.FromError(CellError.Reference);
        }

        int fromColumn = range.First.Column, toColumn = range.Last.Column;
        if (firstColumn is not null)
        {
            int a = ColumnIndex(table, firstColumn), b = lastColumn is null ? a : ColumnIndex(table, lastColumn);
            if (a < 0 || b < 0)
            {
                return EvalValue.FromError(CellError.Reference);
            }

            (fromColumn, toColumn) = (range.First.Column + Math.Min(a, b), range.First.Column + Math.Max(a, b));
        }

        return EvalValue.FromArea(table.Worksheet, new RangeAddress(first, fromColumn, last, toColumn));
    }

    private static int ColumnIndex(SheetTable table, string name)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i < table.Range.ColumnCount ? i : -1;
            }
        }

        return -1;
    }

    private static EvalValue Binary(BinaryOperator op, in EvalValue left, in EvalValue right, EvalContext context, bool last)
    {
        if (op is BinaryOperator.Range or BinaryOperator.Intersection)
        {
            return ReferenceOperator(op, left, right);
        }

        EvalValue a = Operand(left, context), b = Operand(right, context);
        if (a.Kind == EvalKind.Array || b.Kind == EvalKind.Array)
        {
            return Broadcast(a, b, context, (x, y, c) => Scalar(op, x, y, c, last: false));
        }

        return Scalar(op, a, b, context, last);
    }

    private static EvalValue ReferenceOperator(BinaryOperator op, in EvalValue left, in EvalValue right)
    {
        if (left.IsError)
        {
            return left;
        }

        if (right.IsError)
        {
            return right;
        }

        if (left.Kind != EvalKind.Reference || right.Kind != EvalKind.Reference || left.Areas.Length != 1 || right.Areas.Length != 1
            || !ReferenceEquals(left.Areas[0].Sheet, right.Areas[0].Sheet))
        {
            return EvalValue.FromError(CellError.Value);
        }

        RangeAddress a = left.Areas[0].Range, b = right.Areas[0].Range;
        if (op == BinaryOperator.Range)
        {
            return EvalValue.FromArea(left.Areas[0].Sheet, new RangeAddress(
                Math.Min(a.First.Row, b.First.Row), Math.Min(a.First.Column, b.First.Column),
                Math.Max(a.Last.Row, b.Last.Row), Math.Max(a.Last.Column, b.Last.Column)));
        }

        if (!a.Intersects(b))
        {
            return EvalValue.FromError(CellError.Null);
        }

        return EvalValue.FromArea(left.Areas[0].Sheet, new RangeAddress(
            Math.Max(a.First.Row, b.First.Row), Math.Max(a.First.Column, b.First.Column),
            Math.Min(a.Last.Row, b.Last.Row), Math.Min(a.Last.Column, b.Last.Column)));
    }

    /// <summary>A binary operator on two scalars.</summary>
    private static EvalValue Scalar(BinaryOperator op, in EvalValue a, in EvalValue b, EvalContext context, bool last)
    {
        if (op == BinaryOperator.Concatenate)
        {
            if (!ExcelValues.TryText(a, context.Culture, out string x, out EvalValue error) || !ExcelValues.TryText(b, context.Culture, out string y, out error))
            {
                return error;
            }

            return EvalValue.FromText(string.Concat(x, y));
        }

        if (op >= BinaryOperator.Equal)
        {
            if (a.IsError)
            {
                return a;
            }

            if (b.IsError)
            {
                return b;
            }

            int order = ExcelValues.Compare(a, b, context);
            return EvalValue.FromBoolean(op switch
            {
                BinaryOperator.Equal => order == 0,
                BinaryOperator.NotEqual => order != 0,
                BinaryOperator.Less => order < 0,
                BinaryOperator.LessOrEqual => order <= 0,
                BinaryOperator.Greater => order > 0,
                _ => order >= 0,
            });
        }

        if (!ExcelValues.TryNumber(a, context, out double left, out EvalValue leftError))
        {
            return leftError;
        }

        if (!ExcelValues.TryNumber(b, context, out double right, out EvalValue rightError))
        {
            return rightError;
        }

        switch (op)
        {
            case BinaryOperator.Add:
            case BinaryOperator.Subtract:
                double addend = op == BinaryOperator.Add ? right : -right;
                return EvalValue.FromNumber(last ? ExcelValues.AddDroppingNoise(left, addend) : left + addend);
            case BinaryOperator.Multiply:
                return EvalValue.FromNumber(left * right);
            case BinaryOperator.Divide:
                return right == 0 ? EvalValue.FromError(CellError.DivisionByZero) : EvalValue.FromNumber(left / right);
            default:
                return FormulaFunctions.Power(left, right);
        }
    }

    /// <summary>Applies a function to each element of an array, or to a scalar.</summary>
    private static EvalValue Map(in EvalValue value, EvalContext context, Func<EvalValue, EvalContext, EvalValue> function)
    {
        if (value.Kind != EvalKind.Array)
        {
            return function(value, context);
        }

        EvalArray array = value.Array;
        EvalValue[] items = new EvalValue[array.Items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = function(array.Items[i], context);
        }

        return EvalValue.FromArray(new EvalArray(array.Rows, array.Columns, items));
    }

    /// <summary>
    /// Combines two values element by element: a scalar, a single row or a single column stretches to the other's size;
    /// elements past a shorter side are #N/A.
    /// </summary>
    public static EvalValue Broadcast(in EvalValue left, in EvalValue right, EvalContext context, Func<EvalValue, EvalValue, EvalContext, EvalValue> function)
    {
        (int leftRows, int leftColumns) = Size(left);
        (int rightRows, int rightColumns) = Size(right);
        int rows = Math.Max(leftRows, rightRows), columns = Math.Max(leftColumns, rightColumns);
        if ((long)rows * columns > EvalContext.MaxArrayCells)
        {
            return EvalValue.FromError(CellError.Value);
        }

        EvalValue[] items = new EvalValue[rows * columns];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                EvalValue x = Element(left, leftRows, leftColumns, r, c), y = Element(right, rightRows, rightColumns, r, c);
                items[(r * columns) + c] = function(x, y, context);
            }
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }

    public static (int Rows, int Columns) Size(in EvalValue value) =>
        value.Kind == EvalKind.Array ? (value.Array.Rows, value.Array.Columns) : (1, 1);

    /// <summary>The element at a row and column of a stretched value, or #N/A past its end.</summary>
    public static EvalValue Element(in EvalValue value, int rows, int columns, int row, int column)
    {
        if (value.Kind != EvalKind.Array)
        {
            return value;
        }

        int r = rows == 1 ? 0 : row, c = columns == 1 ? 0 : column;
        return r < rows && c < columns ? value.Array[r, c] : EvalValue.FromError(CellError.NotAvailable);
    }

    /// <summary>IF with an array condition: each element picks from the TRUE or the FALSE value.</summary>
    private static EvalValue ChooseElements(in EvalValue condition, in EvalValue whenTrue, in EvalValue whenFalse, EvalContext context)
    {
        (int rows, int columns) = Size(condition);
        (int trueRows, int trueColumns) = Size(whenTrue);
        (int falseRows, int falseColumns) = Size(whenFalse);
        rows = Math.Max(rows, Math.Max(trueRows, falseRows));
        columns = Math.Max(columns, Math.Max(trueColumns, falseColumns));
        (int conditionRows, int conditionColumns) = Size(condition);
        if ((long)rows * columns > EvalContext.MaxArrayCells)
        {
            return EvalValue.FromError(CellError.Value);
        }

        EvalValue[] items = new EvalValue[rows * columns];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                EvalValue test = Element(condition, conditionRows, conditionColumns, r, c);
                items[(r * columns) + c] = !ExcelValues.TryBoolean(test, out bool value, out EvalValue error)
                    ? error
                    : value ? Element(whenTrue, trueRows, trueColumns, r, c) : Element(whenFalse, falseRows, falseColumns, r, c);
            }
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }

    /// <summary>
    /// Calls a function. Value arguments are taken as operands; when one of them is an array, the function runs for each
    /// element and the results make an array.
    /// </summary>
    internal static EvalValue Call(FormulaFunction function, ReadOnlySpan<EvalValue> given, EvalContext context)
    {
        EvalValue[] arguments = given.ToArray();
        int rows = 1, columns = 1;
        bool lift = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].Kind == EvalKind.Lambda)
            {
                if (function.TakesFunctions)
                {
                    continue;
                }

                return Lambdas.GivenToOther(function);
            }

            if (function.TakesValue(i))
            {
                arguments[i] = arguments[i].Kind == EvalKind.Missing ? arguments[i] : Operand(arguments[i], context);
                if (arguments[i].Kind == EvalKind.Array)
                {
                    lift = true;
                    rows = Math.Max(rows, arguments[i].Array.Rows);
                    columns = Math.Max(columns, arguments[i].Array.Columns);
                }
            }
        }

        if (!lift)
        {
            return function.Body(arguments, context);
        }

        if ((long)rows * columns > EvalContext.MaxArrayCells)
        {
            return EvalValue.FromError(CellError.Value);
        }

        EvalValue[] items = new EvalValue[rows * columns];
        EvalValue[] element = new EvalValue[arguments.Length];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    element[i] = function.TakesValue(i) && arguments[i].Kind == EvalKind.Array
                        ? Element(arguments[i], arguments[i].Array.Rows, arguments[i].Array.Columns, r, c)
                        : arguments[i];
                }

                EvalValue result = function.Body(element, context);
                items[(r * columns) + c] = result.Kind switch
                {
                    EvalKind.Array => result.Array.Items[0],
                    EvalKind.Reference => Deref(IntersectReference(result, context)),
                    _ => result,
                };
            }
        }

        return EvalValue.FromArray(new EvalArray(rows, columns, items));
    }
}
