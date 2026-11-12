using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// The functions made for dynamic arrays, from Microsoft's function reference: FILTER, SORT, SORTBY, UNIQUE, XLOOKUP, XMATCH,
/// the text splitters, and those that stack, cut and reshape arrays.
/// </summary>
internal static partial class FormulaFunctions
{
    private static readonly ArgumentKind[] TakesLookup = [ArgumentKind.Value, ArgumentKind.Array, ArgumentKind.Value];
    private static readonly ArgumentKind[] TakesXLookup =
        [ArgumentKind.Value, ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Any, ArgumentKind.Value, ArgumentKind.Value];

    private static readonly ArgumentKind[] TakesFilter = [ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Any];
    private static readonly ArgumentKind[] TakesWrap = [ArgumentKind.Array, ArgumentKind.Value, ArgumentKind.Any];
    private static readonly ArgumentKind[] TakesExpand = [ArgumentKind.Array, ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Any];
    private static readonly ArgumentKind[] TakesTextAround =
        [ArgumentKind.Value, ArgumentKind.Array, ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Any];

    private static readonly ArgumentKind[] TakesTextSplit =
        [ArgumentKind.Value, ArgumentKind.Array, ArgumentKind.Array, ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Any];

    private static IEnumerable<FormulaFunction> DynamicArrayFunctions() =>
    [
        new("ARRAYTOTEXT", 1, 2, [ArgumentKind.Array, ArgumentKind.Value], ArrayToText),
        new("CHOOSECOLS", 2, 255, [ArgumentKind.Array], (a, c) => Choose(a, c, columns: true)),
        new("CHOOSEROWS", 2, 255, [ArgumentKind.Array], (a, c) => Choose(a, c, columns: false)),
        new("DROP", 2, 3, [ArgumentKind.Array, ArgumentKind.Value], Drop),
        new("EXPAND", 2, 4, TakesExpand, Expand),
        new("FILTER", 2, 3, TakesFilter, Filter),
        new("HSTACK", 1, 254, Kinds.Array, (a, c) => Stack(a, vertical: false), takesFunctions: true),
        new("SORT", 1, 4, Kinds.Array, Sort),
        new("SORTBY", 2, 255, Kinds.Array, SortBy),
        new("TAKE", 2, 3, [ArgumentKind.Array, ArgumentKind.Value], Take),
        new("TEXTAFTER", 2, 6, TakesTextAround, (a, c) => TextAround(a, c, before: false)),
        new("TEXTBEFORE", 2, 6, TakesTextAround, (a, c) => TextAround(a, c, before: true)),
        new("TEXTSPLIT", 2, 6, TakesTextSplit, TextSplit),
        new("TOCOL", 1, 3, [ArgumentKind.Array, ArgumentKind.Value], (a, c) => Flatten(a, c, column: true)),
        new("TOROW", 1, 3, [ArgumentKind.Array, ArgumentKind.Value], (a, c) => Flatten(a, c, column: false)),
        new("TRIMRANGE", 1, 3, [ArgumentKind.Any, ArgumentKind.Value], TrimRange),
        new("UNIQUE", 1, 3, [ArgumentKind.Array, ArgumentKind.Value], Unique),
        new("VALUETOTEXT", 1, 2, Kinds.Value, ValueToText),
        new("VSTACK", 1, 254, Kinds.Array, (a, c) => Stack(a, vertical: true), takesFunctions: true),
        new("WRAPCOLS", 2, 3, TakesWrap, (a, c) => Wrap(a, c, rows: false)),
        new("WRAPROWS", 2, 3, TakesWrap, (a, c) => Wrap(a, c, rows: true)),
        new("XLOOKUP", 3, 6, TakesXLookup, XLookup),
        new("XMATCH", 2, 4, TakesLookup, XMatch),
    ];

    /// <summary>An argument as a rectangle of values; a scalar is one by one, and an error in place of the array is given back.</summary>
    private static bool Rectangle(in EvalValue value, out EvalArray array, out EvalValue error)
    {
        error = default;
        array = null!;
        EvalValue full = value.Kind == EvalKind.Reference ? FormulaEvaluator.ToArray(value) : value;
        if (full.Kind == EvalKind.Array)
        {
            array = full.Array;
            return true;
        }

        if (full.IsError)
        {
            error = full;
            return false;
        }

        array = new EvalArray(1, 1, [full]);
        return true;
    }

    /// <summary>An optional number: the fallback when left off or left empty, as these functions take an empty argument.</summary>
    private static bool Given(EvalValue[] arguments, int index, double fallback, EvalContext context, out double number, out EvalValue error)
    {
        if (index >= arguments.Length || arguments[index].Kind == EvalKind.Missing)
        {
            number = fallback;
            error = default;
            return true;
        }

        return Num(Scalar(arguments[index]), context, out number, out error);
    }

    private static EvalValue Result(int rows, int columns, EvalValue[] items) => EvalValue.FromArray(new EvalArray(rows, columns, items));

    private static EvalValue Filter(EvalValue[] arguments, EvalContext context)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error) || !Rectangle(arguments[1], out EvalArray include, out error))
        {
            return error;
        }

        bool byRow = include.Columns == 1 && include.Rows == array.Rows;
        bool byColumn = include.Rows == 1 && include.Columns == array.Columns;
        if (!byRow && !byColumn)
        {
            return Error(CellError.Value);
        }

        // A one by one flag over a one by one array is either.
        int count = byRow ? array.Rows : array.Columns;
        List<int> kept = [];
        for (int i = 0; i < count; i++)
        {
            EvalValue flag = include.Items[i];
            if (flag.IsError)
            {
                return flag;
            }

            if (flag.Kind == EvalKind.Text)
            {
                return Error(CellError.Value);
            }

            if (flag.Kind != EvalKind.Blank && flag.Number != 0)
            {
                kept.Add(i);
            }
        }

        if (kept.Count == 0)
        {
            return arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing ? arguments[2] : Error(CellError.Calc);
        }

        return byRow ? Rows(array, kept) : Columns(array, kept);
    }

    private static EvalValue Rows(EvalArray array, List<int> rows)
    {
        EvalValue[] items = new EvalValue[rows.Count * array.Columns];
        for (int i = 0; i < rows.Count; i++)
        {
            System.Array.Copy(array.Items, rows[i] * array.Columns, items, i * array.Columns, array.Columns);
        }

        return Result(rows.Count, array.Columns, items);
    }

    private static EvalValue Columns(EvalArray array, List<int> columns)
    {
        EvalValue[] items = new EvalValue[array.Rows * columns.Count];
        for (int r = 0; r < array.Rows; r++)
        {
            for (int c = 0; c < columns.Count; c++)
            {
                items[(r * columns.Count) + c] = array[r, columns[c]];
            }
        }

        return Result(array.Rows, columns.Count, items);
    }

    /// <summary>The place of a value in sorted order: numbers, text, booleans, errors. Blanks go last either way, by the caller.</summary>
    private static int SortRank(in EvalValue value) => value.Kind switch
    {
        EvalKind.Number => 0,
        EvalKind.Text => 1,
        EvalKind.Boolean => 2,
        _ => 3,
    };

    private static int SortCompare(in EvalValue a, in EvalValue b, bool descending, EvalContext context)
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
                EvalKind.Text => context.Culture.CompareInfo.Compare(a.Text, b.Text, CompareOptions.IgnoreCase),
                EvalKind.Error => 0,
                _ => a.Number.CompareTo(b.Number),
            };
        return descending ? -order : order;
    }

    private static EvalValue Sort(EvalValue[] arguments, EvalContext context)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error))
        {
            return error;
        }

        bool byColumn = false;
        if (arguments.Length > 3 && arguments[3].Kind != EvalKind.Missing && !Bool(Scalar(arguments[3]), out byColumn, out error))
        {
            return error;
        }

        int[] indexes = [1];
        if (arguments.Length > 1 && arguments[1].Kind != EvalKind.Missing && !WholeNumbers(arguments[1], context, out indexes, out error))
        {
            return error;
        }

        int[] orders = [1];
        if (arguments.Length > 2 && arguments[2].Kind is not EvalKind.Missing && !WholeNumbers(arguments[2], context, out orders, out error))
        {
            return error;
        }

        int keys = byColumn ? array.Rows : array.Columns, count = byColumn ? array.Columns : array.Rows;
        if (orders.Length != indexes.Length && orders.Length != 1)
        {
            return Error(CellError.Value);
        }

        foreach (int index in indexes)
        {
            if (index < 1 || index > keys)
            {
                return Error(CellError.Value);
            }
        }

        foreach (int order in orders)
        {
            if (order is not (1 or -1))
            {
                return Error(CellError.Value);
            }
        }

        int[] sequence = [.. Enumerable.Range(0, count)];
        Comparison<int> compare = (x, y) =>
        {
            for (int k = 0; k < indexes.Length; k++)
            {
                int key = indexes[k] - 1;
                bool descending = orders[orders.Length == 1 ? 0 : k] == -1;
                int result = byColumn
                    ? SortCompare(array[key, x], array[key, y], descending, context)
                    : SortCompare(array[x, key], array[y, key], descending, context);
                if (result != 0)
                {
                    return result;
                }
            }

            return x.CompareTo(y);
        };
        System.Array.Sort(sequence, compare);
        return byColumn ? Columns(array, [.. sequence]) : Rows(array, [.. sequence]);
    }

    private static EvalValue SortBy(EvalValue[] arguments, EvalContext context)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error))
        {
            return error;
        }

        List<(EvalArray By, bool Descending)> keys = [];
        bool? byColumn = null;
        for (int i = 1; i < arguments.Length; i += 2)
        {
            if (!Rectangle(arguments[i], out EvalArray by, out error))
            {
                return error;
            }

            bool column = by.Rows == 1 && by.Columns > 1;
            if ((by.Rows > 1 && by.Columns > 1) || (byColumn is { } known && known != column) || (column ? by.Columns != array.Columns : by.Rows != array.Rows))
            {
                return Error(CellError.Value);
            }

            byColumn = column;
            double order = 1;
            if (i + 1 < arguments.Length && arguments[i + 1].Kind != EvalKind.Missing && !Rectangle(arguments[i + 1], out EvalArray orders, out error))
            {
                return error;
            }
            else if (i + 1 < arguments.Length && arguments[i + 1].Kind != EvalKind.Missing)
            {
                Rectangle(arguments[i + 1], out EvalArray given, out _);
                if (given.Items.Length != 1 || !Num(given.Items[0], context, out order, out error))
                {
                    return given.Items.Length != 1 ? Error(CellError.Value) : error;
                }
            }

            if (order is not (1 or -1))
            {
                return Error(CellError.Value);
            }

            keys.Add((by, order == -1));
        }

        bool columns = byColumn ?? false;
        int[] sequence = [.. Enumerable.Range(0, columns ? array.Columns : array.Rows)];
        System.Array.Sort(sequence, (x, y) =>
        {
            foreach ((EvalArray by, bool descending) in keys)
            {
                int result = SortCompare(by.Items[x], by.Items[y], descending, context);
                if (result != 0)
                {
                    return result;
                }
            }

            return x.CompareTo(y);
        });
        return columns ? Columns(array, [.. sequence]) : Rows(array, [.. sequence]);
    }

    private static bool Bool(in EvalValue value, out bool result, out EvalValue error) => ExcelValues.TryBoolean(value, out result, out error);

    /// <summary>A number or an array of numbers, each cut to a whole number.</summary>
    private static bool WholeNumbers(in EvalValue value, EvalContext context, out int[] numbers, out EvalValue error)
    {
        numbers = [];
        if (!Rectangle(value, out EvalArray array, out error))
        {
            return false;
        }

        numbers = new int[array.Items.Length];
        for (int i = 0; i < numbers.Length; i++)
        {
            if (!Num(array.Items[i], context, out double number, out error))
            {
                return false;
            }

            numbers[i] = (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue);
        }

        return true;
    }

    /// <summary>A key for a row or column of values: text without case, and each kind apart.</summary>
    private static string UniqueKey(in EvalValue value, CultureInfo culture) => value.Kind switch
    {
        EvalKind.Number => "n" + ExcelValues.Round15(value.Number).ToString("R", CultureInfo.InvariantCulture),
        EvalKind.Text => "t" + value.Text.ToUpper(culture),
        EvalKind.Boolean => value.Boolean ? "b1" : "b0",
        EvalKind.Error => "e" + (int)value.Error,
        _ => "x",
    };

    private static EvalValue Unique(EvalValue[] arguments, EvalContext context)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error))
        {
            return error;
        }

        bool byColumn = false, once = false;
        if ((arguments.Length > 1 && arguments[1].Kind != EvalKind.Missing && !Bool(arguments[1], out byColumn, out error))
            || (arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing && !Bool(arguments[2], out once, out error)))
        {
            return error;
        }

        int count = byColumn ? array.Columns : array.Rows, width = byColumn ? array.Rows : array.Columns;
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        List<(string Key, int Index)> order = [];
        StringBuilder key = new();
        for (int i = 0; i < count; i++)
        {
            key.Clear();
            for (int j = 0; j < width; j++)
            {
                key.Append(UniqueKey(byColumn ? array[j, i] : array[i, j], context.Culture)).Append('\u0001');
            }

            string text = key.ToString();
            if (seen.TryGetValue(text, out int times))
            {
                seen[text] = times + 1;
            }
            else
            {
                seen[text] = 1;
                order.Add((text, i));
            }
        }

        List<int> kept = [.. order.Where(o => !once || seen[o.Key] == 1).Select(o => o.Index)];
        if (kept.Count == 0)
        {
            return Error(CellError.Calc);
        }

        return byColumn ? Columns(array, kept) : Rows(array, kept);
    }

    /// <summary>The position in a line of values that a lookup with a match mode and a search mode finds, or -1.</summary>
    private static int LookupIndex(Func<int, EvalValue> item, int count, in EvalValue wanted, int matchMode, int searchMode, EvalContext context)
    {
        if (searchMode is 2 or -2)
        {
            return BinaryIndex(item, count, wanted, matchMode, searchMode == -2, context);
        }

        if (matchMode == 3)
        {
            return RegexIndex(item, count, wanted, searchMode, context);
        }

        bool wildcards = matchMode == 2 && wanted.Kind == EvalKind.Text && Wildcard.Has(wanted.Text);
        bool blank = wanted.Kind is EvalKind.Blank or EvalKind.Missing;
        EvalValue goal = wildcards ? EvalValue.FromText(LoseLastTilde(wanted.Text)) : wanted;
        int best = -1;
        EvalValue bestValue = default;
        for (int n = 0; n < count; n++)
        {
            int i = searchMode == -1 ? count - 1 - n : n;
            EvalValue value = item(i);
            if (blank ? value.Kind == EvalKind.Blank : LookupEquals(value, goal, wildcards))
            {
                return i;
            }

            if (matchMode is -1 or 1 && value.Kind == goal.Kind)
            {
                int order = ExcelValues.Compare(value, goal, context);
                if (order == (matchMode == 1 ? 1 : -1) && (best < 0 || ExcelValues.Compare(value, bestValue, context) == (matchMode == 1 ? -1 : 1)))
                {
                    best = i;
                    bestValue = value;
                }
            }
        }

        return best;
    }

    /// <summary>A pattern's <c>~</c> with nothing after it to escape counts for nothing.</summary>
    private static string LoseLastTilde(string pattern)
    {
        int run = pattern.Length - pattern.TrimEnd('~').Length;
        return run % 2 == 1 ? pattern[..^1] : pattern;
    }

    /// <summary>The first item whose text a regular expression finds a match in, the match mode newer Excel has; -1 when none, -2 when the pattern is not one.</summary>
    private static int RegexIndex(Func<int, EvalValue> item, int count, in EvalValue wanted, int searchMode, EvalContext context)
    {
        if (!ExcelValues.TryText(wanted, context.Culture, out string pattern, out _))
        {
            return -1;
        }

        if (PcreRegex.Get(pattern, ignoreCase: false)?.Regex is not { } regex)
        {
            return -2;
        }

        try
        {
            for (int n = 0; n < count; n++)
            {
                int i = searchMode == -1 ? count - 1 - n : n;
                EvalValue value = item(i);
                if (!value.IsError && ExcelValues.TryText(value, context.Culture, out string text, out _) && regex.IsMatch(text))
                {
                    return i;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return -1;
        }

        return -1;
    }

    /// <summary>A search by bisection of a line in order, ascending or descending.</summary>
    private static int BinaryIndex(Func<int, EvalValue> item, int count, in EvalValue wanted, int matchMode, bool descending, EvalContext context)
    {
        int low = 0, high = count - 1, below = -1, above = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            EvalValue value = item(middle);
            int order = value.IsError ? 1 : ExcelValues.Compare(value, wanted, context);
            if (order == 0 && value.Kind == wanted.Kind)
            {
                return middle;
            }

            if ((order < 0) != descending)
            {
                below = middle;
                low = middle + 1;
            }
            else
            {
                above = middle;
                high = middle - 1;
            }
        }

        return matchMode switch
        {
            -1 => descending ? above : below,
            1 => descending ? below : above,
            _ => -1,
        };
    }

    /// <summary>Reads the match and search modes of XLOOKUP and XMATCH.</summary>
    private static bool LookupModes(EvalValue[] arguments, int matchAt, EvalContext context, out int matchMode, out int searchMode, out EvalValue error)
    {
        matchMode = 0;
        searchMode = 1;
        if (!Given(arguments, matchAt, 0, context, out double match, out error) || !Given(arguments, matchAt + 1, 1, context, out double search, out error))
        {
            return false;
        }

        matchMode = (int)match;
        searchMode = (int)search;
        if (matchMode is not (-1 or 0 or 1 or 2 or 3) || searchMode is not (-2 or -1 or 1 or 2) || (matchMode >= 2 && searchMode is 2 or -2))
        {
            error = Error(CellError.Value);
            return false;
        }

        return true;
    }

    private static EvalValue XMatch(EvalValue[] arguments, EvalContext context)
    {
        if (!LookupModes(arguments, 2, context, out int matchMode, out int searchMode, out EvalValue error))
        {
            return error;
        }

        if (Grid.Of(arguments[1]) is not { } line || (line.Rows > 1 && line.Columns > 1))
        {
            return Error(CellError.Value);
        }

        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        bool byRow = line.Columns == 1;
        int index = LookupIndex(i => byRow ? line[i, 0] : line[0, i], byRow ? line.Rows : line.Columns, arguments[0], matchMode, searchMode, context);
        return index == -2 ? Error(CellError.Value) : index < 0 ? Error(CellError.NotAvailable) : Number(index + 1);
    }

    private static EvalValue XLookup(EvalValue[] arguments, EvalContext context)
    {
        if (!LookupModes(arguments, 4, context, out int matchMode, out int searchMode, out EvalValue error))
        {
            return error;
        }

        if (Grid.Of(arguments[1]) is not { } line || Grid.Of(arguments[2]) is not { } result || (line.Rows > 1 && line.Columns > 1))
        {
            return Error(CellError.Value);
        }

        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        bool byRow = line.Columns == 1;
        if (byRow ? line.Rows != result.Rows : line.Columns != result.Columns)
        {
            return Error(CellError.Value);
        }

        int index = LookupIndex(i => byRow ? line[i, 0] : line[0, i], byRow ? line.Rows : line.Columns, arguments[0], matchMode, searchMode, context);
        if (index == -2)
        {
            return Error(CellError.Value);
        }

        if (index < 0)
        {
            return arguments.Length > 3 && arguments[3].Kind != EvalKind.Missing ? arguments[3] : Error(CellError.NotAvailable);
        }

        // A reference stays one, so the result can be the end of a range, or be read by what takes cells.
        if (result.Area is { } area)
        {
            RangeAddress range = area.Range;
            RangeAddress found = byRow
                ? new RangeAddress(range.First.Row + index, range.First.Column, range.First.Row + index, range.Last.Column)
                : new RangeAddress(range.First.Row, range.First.Column + index, range.Last.Row, range.First.Column + index);
            return EvalValue.FromArea(area.Sheet, found);
        }

        int rows = byRow ? 1 : result.Rows, columns = byRow ? result.Columns : 1;
        EvalValue[] items = new EvalValue[rows * columns];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = byRow ? result[index, i] : result[i, index];
        }

        return items.Length == 1 ? items[0] : Result(rows, columns, items);
    }

    /// <summary>Delimiters: text or an array of text; none are empty.</summary>
    private static bool Delimiters(in EvalValue value, EvalContext context, out string[] delimiters, out EvalValue error)
    {
        delimiters = [];
        if (value.Kind is EvalKind.Missing)
        {
            error = default;
            return true;
        }

        if (!Rectangle(value, out EvalArray array, out error))
        {
            return false;
        }

        delimiters = new string[array.Items.Length];
        for (int i = 0; i < delimiters.Length; i++)
        {
            if (!ExcelValues.TryText(array.Items[i], context.Culture, out delimiters[i], out error))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Text between the delimiters.</summary>
    private static List<string> SplitBy(string text, string[] delimiters, StringComparison comparison, bool ignoreEmpty)
    {
        List<string> parts = [];
        int start = 0;
        while (true)
        {
            int at = -1, length = 0;
            foreach (string delimiter in delimiters)
            {
                int found = text.IndexOf(delimiter, start, comparison);
                if (found >= 0 && (at < 0 || found < at || (found == at && delimiter.Length > length)))
                {
                    at = found;
                    length = delimiter.Length;
                }
            }

            string part = at < 0 ? text[start..] : text[start..at];
            if (part.Length > 0 || !ignoreEmpty)
            {
                parts.Add(part);
            }

            if (at < 0)
            {
                return parts;
            }

            start = at + length;
        }
    }

    private static EvalValue TextSplit(EvalValue[] arguments, EvalContext context)
    {
        if (!ExcelValues.TryText(arguments[0], context.Culture, out string text, out EvalValue error)
            || !Delimiters(arguments[1], context, out string[] columns, out error)
            || !Delimiters(arguments.Length > 2 ? arguments[2] : EvalValue.Missing, context, out string[] rows, out error))
        {
            return error;
        }

        if ((columns.Length == 0 && rows.Length == 0) || columns.Contains("") || rows.Contains(""))
        {
            return Error(CellError.Value);
        }

        bool ignoreEmpty = false;
        if (arguments.Length > 3 && arguments[3].Kind != EvalKind.Missing && !Bool(arguments[3], out ignoreEmpty, out error))
        {
            return error;
        }

        if (!Given(arguments, 4, 0, context, out double mode, out error))
        {
            return error;
        }

        if (mode is not (0 or 1))
        {
            return Error(CellError.Value);
        }

        StringComparison comparison = mode == 1 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        EvalValue pad = arguments.Length > 5 && arguments[5].Kind != EvalKind.Missing ? Scalar(arguments[5]) : Error(CellError.NotAvailable);
        List<List<string>> lines = [];
        foreach (string line in rows.Length > 0 ? SplitBy(text, rows, comparison, ignoreEmpty) : [text])
        {
            lines.Add(columns.Length > 0 ? SplitBy(line, columns, comparison, ignoreEmpty) : [line]);
        }

        lines.RemoveAll(l => l.Count == 0);
        if (lines.Count == 0)
        {
            return Error(CellError.Calc);
        }

        int width = lines.Max(l => l.Count);
        EvalValue[] items = new EvalValue[lines.Count * width];
        for (int r = 0; r < lines.Count; r++)
        {
            for (int c = 0; c < width; c++)
            {
                items[(r * width) + c] = c < lines[r].Count ? EvalValue.FromText(lines[r][c]) : pad;
            }
        }

        return Result(lines.Count, width, items);
    }

    private static EvalValue Scalar(in EvalValue value) => value.Kind switch
    {
        EvalKind.Reference => FormulaEvaluator.ToArray(value) is { Kind: EvalKind.Array } array ? array.Array.Items[0] : FormulaEvaluator.ToArray(value),
        EvalKind.Array => value.Array.Items[0],
        _ => value,
    };

    private static EvalValue TextAround(EvalValue[] arguments, EvalContext context, bool before)
    {
        if (!ExcelValues.TryText(arguments[0], context.Culture, out string text, out EvalValue error)
            || !Delimiters(arguments[1], context, out string[] delimiters, out error)
            || !Given(arguments, 2, 1, context, out double instanceNumber, out error)
            || !Given(arguments, 3, 0, context, out double mode, out error)
            || !Given(arguments, 4, 0, context, out double end, out error))
        {
            return error;
        }

        int instance = (int)Math.Clamp(Math.Truncate(instanceNumber), int.MinValue, int.MaxValue);
        if (instance == 0 || Math.Abs((long)instance) > text.Length || mode is not (0 or 1) || end is not (0 or 1))
        {
            return Error(CellError.Value);
        }

        StringComparison comparison = mode == 1 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // An empty delimiter is met at once: at the start counting forward, at the end counting back.
        if (delimiters.Contains(""))
        {
            return EvalValue.FromText((instance > 0) == before ? "" : text);
        }

        (int At, int Length)? found = Delimiter(text, delimiters, instance, comparison);
        if (found is null && end == 1 && Math.Abs(instance) == Count(text, delimiters, comparison) + 1)
        {
            found = instance > 0 ? (text.Length, 0) : (0, 0);
        }

        if (found is not { } place)
        {
            return arguments.Length > 5 && arguments[5].Kind != EvalKind.Missing ? arguments[5] : Error(CellError.NotAvailable);
        }

        return EvalValue.FromText(before ? text[..place.At] : text[(place.At + place.Length)..]);
    }

    private static int Count(string text, string[] delimiters, StringComparison comparison)
    {
        int count = 0;
        while (Delimiter(text, delimiters, count + 1, comparison) is not null)
        {
            count++;
        }

        return count;
    }

    /// <summary>Where the given occurrence of any delimiter is, counted from the start, or from the end when negative.</summary>
    private static (int At, int Length)? Delimiter(string text, string[] delimiters, int instance, StringComparison comparison)
    {
        List<(int At, int Length)> all = [];
        int start = 0;
        while (start <= text.Length)
        {
            int at = -1, length = 0;
            foreach (string delimiter in delimiters)
            {
                int found = text.IndexOf(delimiter, start, comparison);
                if (found >= 0 && (at < 0 || found < at))
                {
                    at = found;
                    length = delimiter.Length;
                }
            }

            if (at < 0)
            {
                break;
            }

            all.Add((at, length));
            start = at + length;
        }

        int index = instance > 0 ? instance - 1 : all.Count + instance;
        return index >= 0 && index < all.Count ? all[index] : null;
    }

    private static EvalValue Stack(EvalValue[] arguments, bool vertical)
    {
        // Functions stack only with functions, for GROUPBY and PIVOTBY: HSTACK(SUM,AVERAGE).
        int lambdas = arguments.Count(a => a.Kind == EvalKind.Lambda);
        if (lambdas > 0)
        {
            return lambdas < arguments.Length
                ? Error(CellError.Calc)
                : Result(vertical ? arguments.Length : 1, vertical ? 1 : arguments.Length, [.. arguments]);
        }

        List<EvalArray> parts = [];
        foreach (EvalValue argument in arguments)
        {
            if (argument.Kind == EvalKind.Missing)
            {
                parts.Add(new EvalArray(1, 1, [EvalValue.Blank]));
            }
            else if (Rectangle(argument, out EvalArray array, out _))
            {
                parts.Add(array);
            }
            else
            {
                parts.Add(new EvalArray(1, 1, [argument.Kind == EvalKind.Reference ? FormulaEvaluator.ToArray(argument) : argument]));
            }
        }

        long rows = vertical ? parts.Sum(p => (long)p.Rows) : parts.Max(p => p.Rows);
        long columns = vertical ? parts.Max(p => p.Columns) : parts.Sum(p => (long)p.Columns);
        if (rows * columns > EvalContext.MaxArrayCells)
        {
            return Error(CellError.Value);
        }

        EvalValue pad = Error(CellError.NotAvailable);
        EvalValue[] items = new EvalValue[rows * columns];
        System.Array.Fill(items, pad);
        int offset = 0;
        foreach (EvalArray part in parts)
        {
            for (int r = 0; r < part.Rows; r++)
            {
                for (int c = 0; c < part.Columns; c++)
                {
                    items[vertical ? ((offset + r) * columns) + c : (r * columns) + offset + c] = part[r, c];
                }
            }

            offset += vertical ? part.Rows : part.Columns;
        }

        return Result((int)rows, (int)columns, items);
    }

    private static EvalValue Flatten(EvalValue[] arguments, EvalContext context, bool column)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error)
            || !Given(arguments, 1, 0, context, out double ignore, out error))
        {
            return error;
        }

        if (ignore is not (0 or 1 or 2 or 3))
        {
            return Error(CellError.Value);
        }

        bool byColumn = false;
        if (arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing && !Bool(arguments[2], out byColumn, out error))
        {
            return error;
        }

        List<EvalValue> kept = [];
        int outer = byColumn ? array.Columns : array.Rows, inner = byColumn ? array.Rows : array.Columns;
        for (int i = 0; i < outer; i++)
        {
            for (int j = 0; j < inner; j++)
            {
                EvalValue value = byColumn ? array[j, i] : array[i, j];
                if (!((ignore is 1 or 3 && value.Kind == EvalKind.Blank) || (ignore is 2 or 3 && value.IsError)))
                {
                    kept.Add(value);
                }
            }
        }

        if (kept.Count == 0)
        {
            return Error(CellError.Calc);
        }

        return column ? Result(kept.Count, 1, [.. kept]) : Result(1, kept.Count, [.. kept]);
    }

    private static EvalValue Wrap(EvalValue[] arguments, EvalContext context, bool rows)
    {
        if (!Rectangle(arguments[0], out EvalArray vector, out EvalValue error) || !Num(arguments[1], context, out double count, out error))
        {
            return error;
        }

        if (vector.Rows > 1 && vector.Columns > 1)
        {
            return Error(CellError.Value);
        }

        int wrap = (int)Math.Clamp(Math.Truncate(count), int.MinValue, int.MaxValue);
        if (wrap < 1)
        {
            return Error(CellError.Number);
        }

        EvalValue pad = arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing ? Scalar(arguments[2]) : Error(CellError.NotAvailable);
        int total = vector.Items.Length;
        int across = Math.Min(wrap, total);
        int lines = (int)(((long)total + wrap - 1) / wrap);
        if ((long)lines * across > EvalContext.MaxArrayCells)
        {
            return Error(CellError.Number);
        }

        EvalValue[] items = new EvalValue[lines * across];
        System.Array.Fill(items, pad);
        for (int i = 0; i < total; i++)
        {
            int line = i / wrap, place = i % wrap;
            items[rows ? (line * across) + place : (place * lines) + line] = vector.Items[i];
        }

        return rows ? Result(lines, across, items) : Result(across, lines, items);
    }

    /// <summary>Reads the row and column counts of TAKE, DROP and EXPAND.</summary>
    private static bool Counts(EvalValue[] arguments, int rowsDefault, int columnsDefault, EvalContext context, out int rows, out int columns, out EvalValue error)
    {
        rows = columns = 0;
        if (!Given(arguments, 1, rowsDefault, context, out double r, out error) || !Given(arguments, 2, columnsDefault, context, out double c, out error))
        {
            return false;
        }

        rows = (int)Math.Clamp(Math.Truncate(r), int.MinValue, int.MaxValue);
        columns = (int)Math.Clamp(Math.Truncate(c), int.MinValue, int.MaxValue);
        return true;
    }

    private static EvalValue Part(EvalArray array, int firstRow, int rows, int firstColumn, int columns)
    {
        if (rows <= 0 || columns <= 0)
        {
            return Error(CellError.Calc);
        }

        EvalValue[] items = new EvalValue[rows * columns];
        for (int r = 0; r < rows; r++)
        {
            System.Array.Copy(array.Items, ((firstRow + r) * array.Columns) + firstColumn, items, r * columns, columns);
        }

        return Result(rows, columns, items);
    }

    private static EvalValue Take(EvalValue[] arguments, EvalContext context)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error)
            || !Counts(arguments, array.Rows, array.Columns, context, out int rows, out int columns, out error))
        {
            return error;
        }

        int r = (int)Math.Min(Math.Abs((long)rows), array.Rows), c = (int)Math.Min(Math.Abs((long)columns), array.Columns);
        return Part(array, rows < 0 ? array.Rows - r : 0, r, columns < 0 ? array.Columns - c : 0, c);
    }

    private static EvalValue Drop(EvalValue[] arguments, EvalContext context)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error)
            || !Counts(arguments, 0, 0, context, out int rows, out int columns, out error))
        {
            return error;
        }

        int r = (int)Math.Max(0, array.Rows - Math.Abs((long)rows)), c = (int)Math.Max(0, array.Columns - Math.Abs((long)columns));
        return Part(array, rows > 0 ? array.Rows - r : 0, r, columns > 0 ? array.Columns - c : 0, c);
    }

    private static EvalValue Expand(EvalValue[] arguments, EvalContext context)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error)
            || !Counts(arguments, array.Rows, array.Columns, context, out int rows, out int columns, out error))
        {
            return error;
        }

        if (rows < array.Rows || columns < array.Columns)
        {
            return Error(CellError.Value);
        }

        if ((long)rows * columns > EvalContext.MaxArrayCells)
        {
            return Error(CellError.Number);
        }

        EvalValue pad = arguments.Length > 3 && arguments[3].Kind != EvalKind.Missing ? Scalar(arguments[3]) : Error(CellError.NotAvailable);
        EvalValue[] items = new EvalValue[rows * columns];
        System.Array.Fill(items, pad);
        for (int r = 0; r < array.Rows; r++)
        {
            System.Array.Copy(array.Items, r * array.Columns, items, r * columns, array.Columns);
        }

        return Result(rows, columns, items);
    }

    private static EvalValue Choose(EvalValue[] arguments, EvalContext context, bool columns)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error))
        {
            return error;
        }

        int size = columns ? array.Columns : array.Rows;
        List<int> chosen = [];
        for (int i = 1; i < arguments.Length; i++)
        {
            if (!WholeNumbers(arguments[i], context, out int[] numbers, out error))
            {
                return error;
            }

            foreach (int number in numbers)
            {
                if (number == 0 || Math.Abs((long)number) > size)
                {
                    return Error(CellError.Value);
                }

                chosen.Add(number > 0 ? number - 1 : size + number);
            }
        }

        return columns ? Columns(array, chosen) : Rows(array, chosen);
    }

    /// <summary>A value as ARRAYTOTEXT and VALUETOTEXT write it: in the strict form, text in quotes.</summary>
    private static string ValueText(in EvalValue value, bool strict, EvalContext context) => value.Kind switch
    {
        EvalKind.Text => strict ? '"' + value.Text.Replace("\"", "\"\"", StringComparison.Ordinal) + '"' : value.Text,
        EvalKind.Number => ExcelValues.NumberText(value.Number, context.Culture),
        EvalKind.Boolean => value.Boolean ? "TRUE" : "FALSE",
        EvalKind.Error => CellValue.ErrorText(value.Error),
        _ => "",
    };

    private static EvalValue ValueToText(EvalValue[] arguments, EvalContext context)
    {
        if (!Given(arguments, 1, 0, context, out double format, out EvalValue error))
        {
            return error;
        }

        return format is 0 or 1 ? EvalValue.FromText(ValueText(arguments[0], format == 1, context)) : Error(CellError.Value);
    }

    private static EvalValue ArrayToText(EvalValue[] arguments, EvalContext context)
    {
        if (!Rectangle(arguments[0], out EvalArray array, out EvalValue error))
        {
            return error;
        }

        if (!Given(arguments, 1, 0, context, out double format, out error))
        {
            return error;
        }

        if (format is not (0 or 1))
        {
            return Error(CellError.Value);
        }

        bool strict = format == 1;
        string list = context.Culture.TextInfo.ListSeparator + " ";
        (string columns, string rows) = ArraySeparators(context.Culture);
        StringBuilder text = new(strict ? "{" : "");
        for (int r = 0; r < array.Rows; r++)
        {
            for (int c = 0; c < array.Columns; c++)
            {
                if (r > 0 || c > 0)
                {
                    text.Append(!strict ? list : c > 0 ? columns : rows);
                }

                text.Append(ValueText(array[r, c], strict, context));
            }
        }

        if (strict)
        {
            text.Append('}');
        }

        return EvalValue.FromText(text.ToString());
    }

    /// <summary>
    /// What separates the columns and rows of an array in text: a comma and a semicolon, or, where the comma is the decimal
    /// point, what Excel writes for nb-NO.
    /// </summary>
    private static (string Columns, string Rows) ArraySeparators(CultureInfo culture) =>
        culture.NumberFormat.NumberDecimalSeparator == "," ? ("\\", ";") : (",", ";");

    private static EvalValue TrimRange(EvalValue[] arguments, EvalContext context)
    {
        if (arguments[0].Kind != EvalKind.Reference || arguments[0].Areas.Length != 1)
        {
            return arguments[0].IsError ? arguments[0] : Error(CellError.Value);
        }

        if (!Given(arguments, 1, 3, context, out double rowMode, out EvalValue error) || !Given(arguments, 2, 3, context, out double columnMode, out error))
        {
            return error;
        }

        if (rowMode is not (0 or 1 or 2 or 3) || columnMode is not (0 or 1 or 2 or 3))
        {
            return Error(CellError.Value);
        }

        SheetArea area = arguments[0].Areas[0];
        int firstRow = int.MaxValue, lastRow = -1, firstColumn = int.MaxValue, lastColumn = -1;
        foreach ((int row, int column) in area.Sheet.Store.Occupied(area.Range))
        {
            if (area.Sheet.HasFormula(row, column) || area.Sheet.GetKind(row, column) != CellValueKind.Empty)
            {
                firstRow = Math.Min(firstRow, row);
                lastRow = Math.Max(lastRow, row);
                firstColumn = Math.Min(firstColumn, column);
                lastColumn = Math.Max(lastColumn, column);
            }
        }

        if (lastRow < 0)
        {
            return Error(CellError.Reference);
        }

        RangeAddress range = area.Range;
        return EvalValue.FromArea(area.Sheet, new RangeAddress(
            rowMode is 1 or 3 ? firstRow : range.First.Row,
            columnMode is 1 or 3 ? firstColumn : range.First.Column,
            rowMode is 2 or 3 ? lastRow : range.Last.Row,
            columnMode is 2 or 3 ? lastColumn : range.Last.Column));
    }
}
