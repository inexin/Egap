using System.Globalization;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// GROUPBY and PIVOTBY: values grouped by the keys in one or more fields, each group summed up by a function. Microsoft's
/// function reference is the source; where it is silent (the layout of headers and totals, how headers are found, how a
/// sort by values reaches each level, what a function of two parameters is given) Excel 16.0 build 20527 was measured.
/// </summary>
internal static partial class FormulaFunctions
{
    private static readonly ArgumentKind[] GroupByKinds =
    [
        ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Any, ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Array,
        ArgumentKind.Array, ArgumentKind.Value,
    ];

    private static readonly ArgumentKind[] PivotByKinds =
    [
        ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Any, ArgumentKind.Value, ArgumentKind.Value,
        ArgumentKind.Array, ArgumentKind.Value, ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Value,
    ];

    private static IEnumerable<FormulaFunction> GroupFunctions() =>
    [
        new("GROUPBY", 3, 8, GroupByKinds, GroupBy, takesFunctions: true),
        new("PIVOTBY", 4, 11, PivotByKinds, PivotBy, takesFunctions: true),
    ];

    /// <summary>GROUPBY(row_fields, values, function, [field_headers], [total_depth], [sort_order], [filter_array], [field_relationship]).</summary>
    private static EvalValue GroupBy(EvalValue[] arguments, EvalContext context)
    {
        if (!GroupOption(arguments, 7, 0, 0, 1, context, out int relationship, out EvalValue error))
        {
            return error;
        }

        Grouping grouping = new(context, pivot: false);
        if (!grouping.Read(arguments[0], null, arguments[1], arguments[2], arguments, 3, 6, out error)
            || !GroupOption(arguments, 4, 1, -grouping.RowFields, grouping.RowFields, context, out int depth, out error)
            || !Grouping.SortKeys(arguments, 5, grouping.RowFields, grouping.Vertical ? 1 : grouping.Slots.Length, out var sort, out error))
        {
            return error;
        }

        if (relationship == 1 && Math.Abs(depth) > 1)
        {
            return Error(CellError.Value);
        }

        return grouping.Layout(depth, sort, 0, [], relationship == 1, 0);
    }

    /// <summary>
    /// PIVOTBY(row_fields, col_fields, values, function, [field_headers], [row_total_depth], [row_sort_order],
    /// [col_total_depth], [col_sort_order], [filter_array], [relative_to]).
    /// </summary>
    private static EvalValue PivotBy(EvalValue[] arguments, EvalContext context)
    {
        Grouping grouping = new(context, pivot: true);
        if (!grouping.Read(arguments[0], arguments[1], arguments[2], arguments[3], arguments, 4, 9, out EvalValue error)
            || !GroupOption(arguments, 5, 1, -grouping.RowFields, grouping.RowFields, context, out int rowDepth, out error)
            || !Grouping.SortKeys(arguments, 6, grouping.RowFields, grouping.Vertical ? 1 : grouping.Slots.Length, out var rowSort, out error)
            || !GroupOption(arguments, 7, 1, -grouping.ColumnFields, grouping.ColumnFields, context, out int columnDepth, out error)
            || !Grouping.SortKeys(arguments, 8, grouping.ColumnFields, 1, out var columnSort, out error)
            || !GroupOption(arguments, 10, 0, 0, 4, context, out int relativeTo, out error))
        {
            return error;
        }

        return grouping.Layout(rowDepth, rowSort, columnDepth, columnSort, false, relativeTo);
    }

    /// <summary>A whole-number option: the fallback when left off or empty; anything that is not a whole number in range gives #VALUE!.</summary>
    private static bool GroupOption(EvalValue[] arguments, int index, int fallback, int min, int max, EvalContext context, out int value, out EvalValue error)
    {
        value = fallback;
        error = default;
        if (index >= arguments.Length || arguments[index].Kind == EvalKind.Missing)
        {
            return true;
        }

        if (!Num(arguments[index], context, out double number, out error))
        {
            return false;
        }

        if (number != Math.Floor(number) || number < min || number > max)
        {
            error = Error(CellError.Value);
            return false;
        }

        value = (int)number;
        return true;
    }

    /// <summary>
    /// Where a key sorts: numbers, then text without case, then FALSE and TRUE, then errors by their number; empty keys
    /// last whichever way the sort goes.
    /// </summary>
    private static int GroupKeyCompare(in EvalValue a, in EvalValue b, bool descending, CultureInfo culture)
    {
        bool blankA = a.Kind is EvalKind.Blank or EvalKind.Missing, blankB = b.Kind is EvalKind.Blank or EvalKind.Missing;
        if (blankA || blankB)
        {
            return blankA == blankB ? 0 : blankA ? 1 : -1;
        }

        int rankA = SortRank(a), rankB = SortRank(b);
        int order = rankA != rankB
            ? rankA.CompareTo(rankB)
            : a.Kind switch
            {
                EvalKind.Text => culture.CompareInfo.Compare(a.Text, b.Text, CompareOptions.IgnoreCase),
                EvalKind.Error => ((int)a.Error).CompareTo((int)b.Error),
                EvalKind.Boolean => a.Boolean.CompareTo(b.Boolean),
                EvalKind.Number => a.Number.CompareTo(b.Number),
                _ => 0,
            };
        return descending ? -order : order;
    }

    /// <summary>A group: the records whose first <see cref="Level"/> keys are <see cref="Keys"/>; level 0 is all of them.</summary>
    private sealed class GroupNode(int level, EvalValue[] keys, GroupNode? parent)
    {
        public int Level { get; } = level;

        public EvalValue[] Keys { get; } = keys;

        public GroupNode Parent { get; } = parent ?? null!;

        public bool IsRoot => Level == 0;

        public List<int> Records { get; } = [];

        public List<GroupNode> Children { get; set; } = [];

        public Dictionary<string, GroupNode> ByKey { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>One column of results: which values column and which function make it.</summary>
    private readonly record struct GroupSlot(int Value, int Function);

    /// <summary>The arguments of a GROUPBY or PIVOTBY, read, and the groups and cells worked out from them.</summary>
    private sealed class Grouping(EvalContext context, bool pivot)
    {
        private readonly Dictionary<(GroupNode, GroupNode, int), EvalValue> cells = [];
        private readonly Dictionary<(GroupNode, GroupNode), List<int>> shared = [];
        private EvalArray rows = null!;
        private EvalArray? columns;
        private EvalArray values = null!;
        private LambdaValue[] functions = [];
        private bool[] twoParameters = [];
        private bool hasHeaders;
        private bool showHeaders;
        private int relativeTo;
        private GroupNode rowRoot = null!;
        private GroupNode columnRoot = null!;

        public int RowFields => rows.Columns;

        public int ColumnFields => columns?.Columns ?? 0;

        public bool Vertical { get; private set; }

        public bool Paired { get; private set; }

        public GroupSlot[] Slots { get; private set; } = [];

        /// <summary>Reads the fields, the values, the function, the headers option and the filter, and makes the groups.</summary>
        public bool Read(EvalValue rowFields, EvalValue? columnFields, EvalValue valueFields, EvalValue function, EvalValue[] arguments, int headersIndex, int filterIndex, out EvalValue error)
        {
            if (!Rectangle(rowFields, out rows, out error) || !Rectangle(valueFields, out values, out error))
            {
                return false;
            }

            if (columnFields is { } given)
            {
                if (!Rectangle(given, out EvalArray found, out error))
                {
                    return false;
                }

                columns = found;
            }

            if (!ReadFunctions(function, out error))
            {
                return false;
            }

            int count = rows.Rows;
            if (values.Rows != count || (columns is not null && columns.Rows != count))
            {
                error = Error(CellError.Value);
                return false;
            }

            if (!GroupOption(arguments, headersIndex, -1, 0, 3, context, out int headers, out error))
            {
                return false;
            }

            if (headers == -1 && !FindHeaders(out headers, out error))
            {
                return false;
            }

            hasHeaders = headers is 1 or 3;
            showHeaders = headers is 2 or 3;
            int first = hasHeaders ? 1 : 0;
            if (!Filter(arguments, filterIndex, count, first, out bool[]? keep, out error))
            {
                return false;
            }

            rowRoot = new GroupNode(0, [], null);
            columnRoot = new GroupNode(0, [], null);
            for (int record = first; record < count; record++)
            {
                if (keep is null || keep[record])
                {
                    Add(rowRoot, rows, record);
                    if (columns is not null)
                    {
                        Add(columnRoot, columns, record);
                    }
                    else
                    {
                        columnRoot.Records.Add(record);
                    }
                }
            }

            if (rowRoot.Records.Count == 0)
            {
                error = Error(CellError.Value);
                return false;
            }

            return true;
        }

        private void Add(GroupNode root, EvalArray fields, int record)
        {
            GroupNode node = root;
            node.Records.Add(record);
            for (int field = 0; field < fields.Columns; field++)
            {
                EvalValue key = fields[record, field];
                string name = UniqueKey(key, context.Culture);
                if (!node.ByKey.TryGetValue(name, out GroupNode? child))
                {
                    child = new GroupNode(field + 1, [.. node.Keys, key], node);
                    node.ByKey.Add(name, child);
                    node.Children.Add(child);
                }

                node = child;
                node.Records.Add(record);
            }
        }

        /// <summary>
        /// The function: a LAMBDA or a function's name, or a row or column of them (HSTACK or VSTACK of them), which make
        /// columns or rows of results. With more than one column of values, one function serves each, or each function its own.
        /// </summary>
        private bool ReadFunctions(in EvalValue function, out EvalValue error)
        {
            error = default;
            EvalArray list;
            if (function.Kind == EvalKind.Lambda)
            {
                list = new EvalArray(1, 1, [function]);
            }
            else if (function.Kind == EvalKind.Array)
            {
                list = function.Array;
            }
            else
            {
                error = function.IsError ? function : Error(CellError.Value);
                return false;
            }

            if (list.Rows > 1 && list.Columns > 1)
            {
                error = Error(CellError.Value);
                return false;
            }

            int count = list.Rows * list.Columns;
            functions = new LambdaValue[count];
            twoParameters = new bool[count];
            for (int i = 0; i < count; i++)
            {
                EvalValue item = list.Rows > 1 ? list[i, 0] : list[0, i];
                if (item.Kind != EvalKind.Lambda)
                {
                    error = item.IsError ? item : Error(CellError.Value);
                    return false;
                }

                functions[i] = item.Lambda;
                if (!item.Lambda.Accepts(1))
                {
                    if (!item.Lambda.Accepts(2))
                    {
                        error = Error(CellError.Value);
                        return false;
                    }

                    twoParameters[i] = true;
                }
            }

            Vertical = list.Rows > 1;
            int fields = values.Columns;
            if (count == 1)
            {
                Slots = [.. Enumerable.Range(0, fields).Select(v => new GroupSlot(v, 0))];
            }
            else if (fields == 1)
            {
                Slots = [.. Enumerable.Range(0, count).Select(f => new GroupSlot(0, f))];
            }
            else if (fields == count)
            {
                Paired = Vertical;
                Slots = [.. Enumerable.Range(0, count).Select(f => new GroupSlot(f, f))];
            }
            else
            {
                error = Error(CellError.Value);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether the first row holds headers when the argument leaves it to Excel: it does when the first value is text and
        /// the values below it are not; when they are all text it does not, and a mix gives #VALUE!.
        /// </summary>
        private bool FindHeaders(out int headers, out EvalValue error)
        {
            headers = 0;
            error = default;
            if (values.Rows < 2 || values[0, 0].Kind != EvalKind.Text)
            {
                return true;
            }

            int texts = 0;
            for (int r = 1; r < values.Rows; r++)
            {
                texts += values[r, 0].Kind == EvalKind.Text ? 1 : 0;
            }

            if (texts == 0)
            {
                headers = 1;
            }
            else if (texts != values.Rows - 1)
            {
                error = Error(CellError.Value);
                return false;
            }

            return true;
        }

        /// <summary>The filter: one value for each record (or each record and the header row), TRUE to keep it.</summary>
        private static bool Filter(EvalValue[] arguments, int index, int count, int first, out bool[]? keep, out EvalValue error)
        {
            keep = null;
            error = default;
            if (index >= arguments.Length || arguments[index].Kind == EvalKind.Missing)
            {
                return true;
            }

            if (!Rectangle(arguments[index], out EvalArray filter, out error))
            {
                return false;
            }

            int offset;
            if (filter.Rows == 1 && filter.Columns == 1)
            {
                offset = -1;
            }
            else if (filter.Columns == 1 && filter.Rows == count)
            {
                offset = 0;
            }
            else if (filter.Columns == 1 && first == 1 && filter.Rows == count - 1)
            {
                offset = 1;
            }
            else
            {
                error = Error(CellError.Value);
                return false;
            }

            keep = new bool[count];
            for (int record = first; record < count; record++)
            {
                EvalValue item = offset < 0 ? filter[0, 0] : filter[record - offset, 0];
                if (!ExcelValues.TryBoolean(item, out keep[record], out error))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The sort keys: whole numbers, each a column (negative for descending) among the fields and then the values. A key
        /// on the values must be the only key.
        /// </summary>
        public static bool SortKeys(EvalValue[] arguments, int index, int fields, int valueColumns, out (int Column, bool Descending)[] keys, out EvalValue error)
        {
            keys = [];
            error = default;
            if (index >= arguments.Length || arguments[index].Kind == EvalKind.Missing)
            {
                return true;
            }

            if (!Rectangle(arguments[index], out EvalArray list, out error))
            {
                return false;
            }

            if (list.Rows > 1 && list.Columns > 1)
            {
                error = Error(CellError.Value);
                return false;
            }

            int count = list.Rows * list.Columns;
            keys = new (int, bool)[count];
            HashSet<int> seen = [];
            for (int i = 0; i < count; i++)
            {
                EvalValue item = list.Rows > 1 ? list[i, 0] : list[0, i];
                if (item.IsError)
                {
                    error = item;
                    return false;
                }

                if (item.Kind != EvalKind.Number || item.Number != Math.Floor(item.Number) || item.Number == 0
                    || Math.Abs(item.Number) > fields + valueColumns || !seen.Add((int)Math.Abs(item.Number))
                    || (count > 1 && Math.Abs(item.Number) > fields))
                {
                    error = Error(CellError.Value);
                    return false;
                }

                keys[i] = ((int)Math.Abs(item.Number), item.Number < 0);
            }

            return true;
        }

        /// <summary>The result: headers, then a row for each group shown (or a row for each function and group), columns likewise.</summary>
        public EvalValue Layout(int rowDepth, (int Column, bool Descending)[] rowSort, int columnDepth, (int Column, bool Descending)[] columnSort, bool table, int relative)
        {
            relativeTo = relative;
            int fields = RowFields, columnFields = ColumnFields;
            List<GroupNode> rowOrder = [];
            if (table)
            {
                List<GroupNode> leaves = [];
                Leaves(rowRoot, leaves);
                Comparison<GroupNode> flat = (a, b) => CompareFlat(a, b, rowSort, fields);
                leaves = [.. leaves.Order(Comparer<GroupNode>.Create(flat))];
                if (rowDepth < 0)
                {
                    rowOrder.Add(rowRoot);
                }

                rowOrder.AddRange(leaves);
                if (rowDepth > 0)
                {
                    rowOrder.Add(rowRoot);
                }
            }
            else
            {
                Arrange(rowRoot, rowDepth, fields, rowSort, rows: true, rowOrder);
            }

            List<GroupNode> columnOrder = [];
            if (pivot)
            {
                Arrange(columnRoot, columnDepth, columnFields, columnSort, rows: false, columnOrder);
            }
            else
            {
                columnOrder.Add(columnRoot);
            }

            int functionColumns = Vertical ? 1 + (Paired && showHeaders ? 1 : 0) : 0;
            int left = fields + functionColumns;
            int block = Vertical ? 1 : Slots.Length;
            int width = left + (columnOrder.Count * block);
            bool nameRow = pivot && showHeaders;
            bool functionRow = !Vertical && functions.Length > 1;
            bool mergedHeaders = pivot && Vertical && Paired && showHeaders && columnFields > 0;
            bool headerRow = showHeaders && !mergedHeaders;
            int perGroup = Vertical ? Slots.Length : 1;
            int height = (nameRow ? 1 : 0) + columnFields + (functionRow ? 1 : 0) + (headerRow ? 1 : 0) + (rowOrder.Count * perGroup);
            if ((long)width * height > EvalContext.MaxArrayCells)
            {
                return Error(CellError.Number);
            }

            EvalValue empty = EvalValue.FromText(string.Empty);
            EvalValue[] items = new EvalValue[width * height];
            System.Array.Fill(items, empty);
            int row = 0;
            if (nameRow)
            {
                items[(row * width) + left] = EvalValue.FromText(string.Join("; ", Enumerable.Range(0, columnFields).Select(ColumnHeader)));
                row++;
            }

            string columnTotal = Math.Abs(columnDepth) >= 2 ? "Grand Total" : "Total";
            for (int level = 0; level < columnFields; level++, row++)
            {
                if (mergedHeaders && level == columnFields - 1)
                {
                    for (int f = 0; f < fields; f++)
                    {
                        items[(row * width) + f] = RowHeader(f);
                    }
                }

                for (int c = 0; c < columnOrder.Count; c++)
                {
                    GroupNode node = columnOrder[c];
                    EvalValue key = node.IsRoot ? (level == 0 ? EvalValue.FromText(columnTotal) : empty) : level < node.Level ? node.Keys[level] : empty;
                    for (int s = 0; s < block; s++)
                    {
                        items[(row * width) + left + (c * block) + s] = key;
                    }
                }
            }

            if (functionRow)
            {
                for (int c = 0; c < columnOrder.Count; c++)
                {
                    for (int s = 0; s < block; s++)
                    {
                        items[(row * width) + left + (c * block) + s] = EvalValue.FromText(FunctionName(Slots[s].Function));
                    }
                }

                row++;
            }

            if (headerRow)
            {
                for (int f = 0; f < fields; f++)
                {
                    items[(row * width) + f] = RowHeader(f);
                }

                for (int c = 0; c < columnOrder.Count; c++)
                {
                    for (int s = 0; s < block; s++)
                    {
                        items[(row * width) + left + (c * block) + s] = Paired ? empty : ValueHeader(Slots[s].Value);
                    }
                }

                row++;
            }

            string rowTotal = Math.Abs(rowDepth) >= 2 ? "Grand Total" : "Total";
            bool blankTotals = pivot && functions.Length == 1 && values.Columns > 1;
            foreach (GroupNode node in rowOrder)
            {
                for (int sub = 0; sub < perGroup; sub++, row++)
                {
                    int at = row * width;
                    for (int f = 0; f < fields; f++)
                    {
                        items[at + f] = node.IsRoot ? (f == 0 ? EvalValue.FromText(rowTotal) : empty) : f < node.Level ? node.Keys[f] : empty;
                    }

                    if (Vertical)
                    {
                        items[at + fields] = EvalValue.FromText(FunctionName(Slots[sub].Function));
                        if (Paired && showHeaders)
                        {
                            items[at + fields + 1] = ValueHeader(Slots[sub].Value);
                        }
                    }

                    for (int c = 0; c < columnOrder.Count; c++)
                    {
                        GroupNode column = columnOrder[c];
                        for (int s = 0; s < block; s++)
                        {
                            int slot = Vertical ? sub : s;
                            items[at + left + (c * block) + s] = blankTotals && column.IsRoot ? empty : Cell(node, column, slot);
                        }
                    }
                }
            }

            return Result(height, width, items);
        }

        private static void Leaves(GroupNode node, List<GroupNode> leaves)
        {
            if (node.Children.Count == 0)
            {
                leaves.Add(node);
                return;
            }

            foreach (GroupNode child in node.Children)
            {
                Leaves(child, leaves);
            }
        }

        /// <summary>
        /// The groups in the order they show: each level sorted among its siblings, subtotals after their groups (before
        /// with a negative depth) down to the depth asked for, and the grand total at the end (or the start).
        /// </summary>
        private void Arrange(GroupNode root, int depth, int fields, (int Column, bool Descending)[] sort, bool rows, List<GroupNode> order)
        {
            int shown = Math.Abs(depth);
            if (depth < 0)
            {
                order.Add(root);
            }

            Visit(root);
            if (depth > 0)
            {
                order.Add(root);
            }

            void Visit(GroupNode node)
            {
                int level = node.Level + 1;
                bool byValue = rows || level == fields || shown >= level + 1;
                Comparison<GroupNode> compare = (a, b) => CompareSiblings(a, b, level, fields, sort, rows, byValue);
                node.Children = [.. node.Children.Order(Comparer<GroupNode>.Create(compare))];
                foreach (GroupNode child in node.Children)
                {
                    bool subtotal = child.Level < fields && shown >= child.Level + 1;
                    if (subtotal && depth < 0)
                    {
                        order.Add(child);
                    }

                    if (child.Level < fields)
                    {
                        Visit(child);
                    }
                    else
                    {
                        order.Add(child);
                    }

                    if (subtotal && depth > 0)
                    {
                        order.Add(child);
                    }
                }
            }
        }

        private int CompareSiblings(GroupNode a, GroupNode b, int level, int fields, (int Column, bool Descending)[] sort, bool rows, bool byValue)
        {
            foreach ((int column, bool descending) in sort)
            {
                int order;
                if (column <= fields)
                {
                    if (column != level)
                    {
                        continue;
                    }

                    order = GroupKeyCompare(a.Keys[level - 1], b.Keys[level - 1], descending, context.Culture);
                }
                else
                {
                    if (!byValue)
                    {
                        continue;
                    }

                    int slot = Vertical ? 0 : column - fields - 1;
                    order = GroupKeyCompare(SortValue(a, rows, slot), SortValue(b, rows, slot), descending, context.Culture);
                }

                if (order != 0)
                {
                    return order;
                }
            }

            return GroupKeyCompare(a.Keys[level - 1], b.Keys[level - 1], false, context.Culture);
        }

        private int CompareFlat(GroupNode a, GroupNode b, (int Column, bool Descending)[] sort, int fields)
        {
            foreach ((int column, bool descending) in sort)
            {
                int order = column <= fields
                    ? GroupKeyCompare(a.Keys[column - 1], b.Keys[column - 1], descending, context.Culture)
                    : GroupKeyCompare(SortValue(a, true, Vertical ? 0 : column - fields - 1), SortValue(b, true, Vertical ? 0 : column - fields - 1), descending, context.Culture);
                if (order != 0)
                {
                    return order;
                }
            }

            // Ties after a sort that was asked for go by the last field first, as in Excel.
            for (int i = 0; i < fields; i++)
            {
                int f = sort.Length > 0 ? fields - 1 - i : i;
                int order = GroupKeyCompare(a.Keys[f], b.Keys[f], false, context.Culture);
                if (order != 0)
                {
                    return order;
                }
            }

            return 0;
        }

        /// <summary>What a group sorts by: its total across the columns (or, for a column, down the rows).</summary>
        private EvalValue SortValue(GroupNode node, bool rows, int slot) => rows ? Cell(node, columnRoot, slot) : Cell(rowRoot, node, 0);

        /// <summary>
        /// One result: the function of the group's values; a function of two parameters is given the values the result is
        /// relative to as well. A function that gives more than one value gives #CALC!.
        /// </summary>
        private EvalValue Cell(GroupNode row, GroupNode column, int slot)
        {
            if (cells.TryGetValue((row, column, slot), out EvalValue result))
            {
                return result;
            }

            List<int> records = Records(row, column);
            if (records.Count == 0)
            {
                result = EvalValue.FromText(string.Empty);
            }
            else
            {
                GroupSlot which = Slots[slot];
                EvalValue subset = Column(records, which.Value);
                EvalValue callee = EvalValue.FromLambda(functions[which.Function]);
                if (twoParameters[which.Function])
                {
                    (GroupNode baseRow, GroupNode baseColumn) = relativeTo switch
                    {
                        1 => (row, columnRoot),
                        2 => (rowRoot, columnRoot),
                        3 => (row, column.IsRoot ? column : column.Parent),
                        4 => (row.IsRoot ? row : row.Parent, column),
                        _ => (rowRoot, column),
                    };
                    if (!pivot)
                    {
                        (baseRow, baseColumn) = (rowRoot, columnRoot);
                    }

                    result = Lambdas.Apply(callee, [subset, Column(Records(baseRow, baseColumn), which.Value)], context);
                }
                else
                {
                    result = Lambdas.Apply(callee, [subset], context);
                }

                if (result.Kind is EvalKind.Array or EvalKind.Reference or EvalKind.Lambda)
                {
                    // Excel keeps the array inside the result, which no cell can show.
                    result = Error(CellError.Calc);
                }
                else if (result.Kind is EvalKind.Missing or EvalKind.Blank)
                {
                    result = Number(0);
                }
            }

            cells[(row, column, slot)] = result;
            return result;
        }

        /// <summary>The records in both a row group and a column group.</summary>
        private List<int> Records(GroupNode row, GroupNode column)
        {
            if (column.IsRoot)
            {
                return row.Records;
            }

            if (row.IsRoot)
            {
                return column.Records;
            }

            if (!shared.TryGetValue((row, column), out List<int>? both))
            {
                both = [];
                List<int> a = row.Records, b = column.Records;
                for (int i = 0, j = 0; i < a.Count && j < b.Count;)
                {
                    if (a[i] == b[j])
                    {
                        both.Add(a[i]);
                        i++;
                        j++;
                    }
                    else if (a[i] < b[j])
                    {
                        i++;
                    }
                    else
                    {
                        j++;
                    }
                }

                shared[(row, column)] = both;
            }

            return both;
        }

        private EvalValue Column(List<int> records, int field)
        {
            EvalValue[] items = new EvalValue[records.Count];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = values[records[i], field];
            }

            return EvalValue.FromArray(new EvalArray(items.Length, 1, items));
        }

        private string FunctionName(int function) => functions[function].Function?.Name ?? "CUSTOM";

        private EvalValue RowHeader(int field) => hasHeaders ? rows[0, field] : EvalValue.FromText($"Row Field {field + 1}");

        private EvalValue ValueHeader(int field) => hasHeaders ? values[0, field] : EvalValue.FromText($"Value {field + 1}");

        private string ColumnHeader(int field)
        {
            if (!hasHeaders)
            {
                return $"Column Field {field + 1}";
            }

            return ExcelValues.TryText(columns![0, field], context.Culture, out string text, out _) ? text : string.Empty;
        }
    }
}
