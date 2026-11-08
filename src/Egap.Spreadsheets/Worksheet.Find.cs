using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Egap.Spreadsheets.Csv;
using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets;

/// <summary>Finding and replacing text in cells, as Excel's Find and Replace do.</summary>
public sealed partial class Worksheet
{
    /// <summary>
    /// Finds the cells of the sheet whose text matches, as Excel's Find All does: <c>*</c> stands for any run of characters,
    /// <c>?</c> for one, and <c>~</c> before either (or before <c>~</c>) for the character itself. Without
    /// <see cref="FindOptions.MatchEntireCell"/> the text may be anywhere in a cell's.
    /// </summary>
    /// <param name="what">The text to find.</param>
    /// <param name="options">How to match; null for Excel's defaults.</param>
    /// <returns>The matching cells, in the order <see cref="FindOptions.Order"/> gives.</returns>
    /// <exception cref="ArgumentException">The text is null or empty.</exception>
    /// <example>
    /// <code>
    /// IReadOnlyList&lt;Cell&gt; totals = sheet.Find("total*", new FindOptions { MatchEntireCell = true });
    /// </code>
    /// </example>
    public IReadOnlyList<Cell> Find(string what, FindOptions? options = null) =>
        UsedRange is { } used ? FindCells(used.Address, what, options ?? new FindOptions()) : [];

    /// <summary>
    /// Replaces matching text in the sheet's formulas and values, as Excel's Replace All does, and gives how many cells
    /// changed. See <see cref="CellRange.Replace(string, string, FindOptions?)"/>.
    /// </summary>
    /// <param name="what">The text to find.</param>
    /// <param name="replacement">The text to put in its place, taken as it is.</param>
    /// <param name="options">How to match; null for Excel's defaults. Only <see cref="FindLookIn.Formulas"/> can be replaced.</param>
    /// <returns>How many cells changed.</returns>
    /// <exception cref="ArgumentException">The text is null or empty, the replacement is null, or the options look in values or notes.</exception>
    /// <exception cref="InvalidOperationException">A replacement makes a formula Excel's grammar refuses; the cells before it stay replaced, as in Excel.</exception>
    /// <example>
    /// <code>
    /// int changed = sheet.Replace("Sheet1!", "Data!");
    /// </code>
    /// </example>
    public int Replace(string what, string replacement, FindOptions? options = null) =>
        UsedRange is { } used ? ReplaceCells(used.Address, what, replacement, options ?? new FindOptions()) : CheckReplace(what, replacement, options ?? new FindOptions());

    internal IReadOnlyList<Cell> FindCells(RangeAddress range, string what, FindOptions options)
    {
        Regex pattern = Pattern(what, options);
        List<Cell> found = [];
        foreach ((int row, int column) in Visit(range, options))
        {
            if (Searched(row, column, options.LookIn) is { Length: > 0 } text && pattern.IsMatch(text))
            {
                found.Add(this[row, column]);
            }
        }

        return found;
    }

    internal int ReplaceCells(RangeAddress range, string what, string replacement, FindOptions options)
    {
        CheckReplace(what, replacement, options);
        Regex pattern = Pattern(what, options);
        TypeDetector? detector = null;
        int changed = 0;
        foreach ((int row, int column) in Visit(range, options))
        {
            if (Searched(row, column, FindLookIn.Formulas) is not { Length: > 0 } text)
            {
                continue;
            }

            string replaced = pattern.Replace(text, replacement.Replace("$", "$$", StringComparison.Ordinal));
            if (replaced == text)
            {
                continue;
            }

            Cell cell = this[row, column];
            if (replaced.Length > 1 && replaced[0] == '=')
            {
                if (ArrayRangeAt(row, column) is { } array && array.First != array.Last)
                {
                    throw new InvalidOperationException($"Part of the array formula at {array} can't be changed.");
                }

                try
                {
                    cell.Formula = Ordered(replaced[1..], cell.Address);
                }
                catch (FormulaException e)
                {
                    throw new InvalidOperationException($"Replacing in {cell.Address} makes a formula Excel refuses: {replaced}", e);
                }
            }
            else if (NumberFormatCode.Get(cell.NumberFormat).Code == "@")
            {
                SetValue(row, column, CellValue.FromText(replaced));
            }
            else
            {
                detector ??= new TypeDetector(Workbook.Culture, Workbook);
                SetValue(row, column, detector.Detect(replaced)?.Value ?? CellValue.FromText(replaced));
            }

            changed++;
        }

        if (changed > 0)
        {
            Workbook.Recompile();
        }

        return changed;
    }

    private static int CheckReplace(string what, string replacement, FindOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(what);
        ArgumentNullException.ThrowIfNull(replacement);
        if (options.LookIn != FindLookIn.Formulas)
        {
            throw new ArgumentException("Excel replaces only in formulas and values as typed: look in Formulas.", nameof(options));
        }

        return 0;
    }

    /// <summary>A formula with each range's corners put in order, as Excel writes a formula typed with <c>B9:B2</c> as <c>B2:B9</c>.</summary>
    private static string Ordered(string formula, CellAddress cell)
    {
        if (FormulaParser.TryParse(formula, r1c1: false, cell) is not { } parsed)
        {
            return formula;
        }

        return FormulaPrinter.Print(parsed, reference =>
        {
            Area a = reference.Area;
            if (a.Kind == AreaKind.Error || (a.FirstRow <= a.LastRow && a.FirstColumn <= a.LastColumn))
            {
                return null;
            }

            AreaFlags flags = AreaFlags.None;
            bool rows = a.FirstRow > a.LastRow, columns = a.FirstColumn > a.LastColumn;
            flags |= (rows ? a.LastRowAbsolute : a.FirstRowAbsolute) ? AreaFlags.FirstRowAbsolute : 0;
            flags |= (rows ? a.FirstRowAbsolute : a.LastRowAbsolute) ? AreaFlags.LastRowAbsolute : 0;
            flags |= (columns ? a.LastColumnAbsolute : a.FirstColumnAbsolute) ? AreaFlags.FirstColumnAbsolute : 0;
            flags |= (columns ? a.FirstColumnAbsolute : a.LastColumnAbsolute) ? AreaFlags.LastColumnAbsolute : 0;
            return a with
            {
                FirstRow = Math.Min(a.FirstRow, a.LastRow),
                LastRow = Math.Max(a.FirstRow, a.LastRow),
                FirstColumn = Math.Min(a.FirstColumn, a.LastColumn),
                LastColumn = Math.Max(a.FirstColumn, a.LastColumn),
                Flags = flags,
            };
        });
    }

    /// <summary>The cells of a range that hold something to search, in the order asked for.</summary>
    private List<(int Row, int Column)> Visit(RangeAddress range, FindOptions options)
    {
        IEnumerable<(int Row, int Column)> cells = options.LookIn == FindLookIn.Notes
            ? Comments.Where(c => range.Contains(c.Cell)).Select(c => (c.Cell.Row, c.Cell.Column))
            : store.Occupied(range);
        return options.Order == FindOrder.ByColumns
            ? cells.OrderBy(c => c.Column).ThenBy(c => c.Row).ToList()
            : cells.OrderBy(c => c.Row).ThenBy(c => c.Column).ToList();
    }

    /// <summary>The text Excel searches in a cell: its formula bar, what it shows, or its note.</summary>
    private string? Searched(int row, int column, FindLookIn lookIn)
    {
        if (lookIn == FindLookIn.Notes)
        {
            return Comments[new CellAddress(row, column)]?.Text;
        }

        Cell cell = this[row, column];
        if (lookIn == FindLookIn.Values)
        {
            return cell.Value.IsEmpty ? null : cell.Text;
        }

        if (cell.Formula is { } formula)
        {
            return "=" + formula;
        }

        CellValue value = cell.Value;
        return value.Kind switch
        {
            CellValueKind.Empty => null,
            CellValueKind.Number => FormulaBar(value.Number, NumberFormatCode.Get(cell.NumberFormat), Workbook.Culture, Workbook.Date1904),
            CellValueKind.Boolean => value.Boolean ? "TRUE" : "FALSE",
            _ => value.ToString(),
        };
    }

    /// <summary>A number as Excel's formula bar shows it: dates and times in the culture's short form, percents with %, else in full.</summary>
    internal static string FormulaBar(double number, NumberFormatCode format, CultureInfo culture, bool date1904)
    {
        bool invariant = culture.Equals(CultureInfo.InvariantCulture);
        if (format.IsDateTime && number >= 0 && number < 2958466)
        {
            double seconds = Math.Round(number * 86400);
            DateTime date = date1904 ? new DateTime(1904, 1, 1) : number < 61 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30);
            date = date.AddSeconds(seconds);
            string day = date.ToString(invariant ? "M/d/yyyy" : culture.DateTimeFormat.ShortDatePattern, culture);
            string time = date.ToString(invariant ? "h:mm:ss tt" : culture.DateTimeFormat.LongTimePattern, culture);
            return number < 1 ? time : number % 1 == 0 ? day : day + " " + time;
        }

        if (format.Code.Contains('%', StringComparison.Ordinal))
        {
            return (number * 100).ToString("G15", culture) + culture.NumberFormat.PercentSymbol;
        }

        return number.ToString("G15", culture);
    }

    /// <summary>Excel's wildcards as a regular expression: <c>*</c> greedy, <c>?</c> one character, <c>~</c> taking the next literally.</summary>
    private static Regex Pattern(string what, FindOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(what);
        StringBuilder pattern = new(options.MatchEntireCell ? @"\A(?:" : "");
        for (int i = 0; i < what.Length; i++)
        {
            char c = what[i];
            if (c == '~' && i + 1 < what.Length && what[i + 1] is '*' or '?' or '~')
            {
                pattern.Append(Regex.Escape(what[++i].ToString()));
            }
            else
            {
                pattern.Append(c switch
                {
                    '*' => ".*",
                    '?' => ".",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        if (options.MatchEntireCell)
        {
            pattern.Append(@")\z");
        }

        RegexOptions flags = RegexOptions.Singleline | RegexOptions.CultureInvariant | (options.MatchCase ? 0 : RegexOptions.IgnoreCase);
        return new Regex(pattern.ToString(), flags, TimeSpan.FromSeconds(10));
    }
}
