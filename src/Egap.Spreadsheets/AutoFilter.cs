using System.Globalization;
using System.Xml.Linq;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets;

/// <summary>
/// The filter of a sheet or table, as a file holds it: its range and the criteria of its columns. <see cref="Apply"/> hides the
/// rows that fail the criteria, as Excel's Reapply does; Excel itself only hides rows when a filter is set or reapplied, and
/// keeps them as they are when it opens a file.
/// </summary>
/// <example>
/// <code>
/// Workbook book = Workbook.Load("report.xlsx");
/// if (book.Worksheets[0].AutoFilter is { } filter)
/// {
///     int shown = filter.Apply();
/// }
/// </code>
/// </example>
public sealed class AutoFilter
{
    private readonly Worksheet sheet;
    private readonly XElement element;
    private readonly SheetTable? table;

    internal AutoFilter(Worksheet sheet, XElement element, SheetTable? table = null)
    {
        this.sheet = sheet;
        this.element = element;
        this.table = table;
    }

    /// <summary>The filter's range: its header row and the data rows under it; for a table, the table without its totals row.</summary>
    public RangeAddress Range
    {
        get
        {
            if (table is { } t)
            {
                RangeAddress range = t.Range;
                return t.ShowTotalsRow && range.RowCount > 1 ? new RangeAddress(range.First, new CellAddress(range.Last.Row - 1, range.Last.Column)) : range;
            }

            return RangeAddress.TryParse((string?)element.Attribute("ref"), out RangeAddress parsed) ? parsed : default;
        }
    }

    /// <summary>Whether any column has criteria, so that the filter may hide rows.</summary>
    public bool HasCriteria => Columns().Any(c => c.Elements().Any());

    /// <summary>
    /// Shows the data rows that meet every column's criteria and hides the rest, as Excel's Reapply does. Values in a list
    /// match the text a cell shows, without case; custom criteria compare numbers with numbers and text with text, and their
    /// wildcards match text only; top and bottom items and above or below average take the column's numbers; date periods
    /// count from today. Colour and icon criteria are not evaluated: a row passes them.
    /// </summary>
    /// <returns>How many data rows show.</returns>
    /// <remarks>Formulas filter by their last results, so calculate the workbook first when they have changed.</remarks>
    public int Apply()
    {
        RangeAddress range = Range;
        if (range.RowCount < 2)
        {
            return 0;
        }

        List<Func<int, bool>> tests = [];
        foreach (XElement column in Columns())
        {
            int index = XlsxInt((string?)column.Attribute("colId"));
            if (index < 0 || index >= range.ColumnCount || column.Elements().FirstOrDefault() is not { } criteria)
            {
                continue;
            }

            if (Test(criteria, range.First.Column + index, range.First.Row + 1, range.Last.Row) is { } test)
            {
                tests.Add(test);
            }
        }

        int shown = 0;
        for (int row = range.First.Row + 1; row <= range.Last.Row; row++)
        {
            bool pass = tests.TrueForAll(t => t(row));
            if (sheet.Rows[row].Hidden == pass)
            {
                sheet.Rows[row].Hidden = !pass;
            }

            shown += pass ? 1 : 0;
        }

        return shown;
    }

    /// <summary>Clears every column's criteria and shows the filter's data rows, as Excel's Clear does; the filter and its buttons stay.</summary>
    public void ShowAll()
    {
        foreach (XElement column in Columns().ToList())
        {
            column.RemoveNodes();
            if (column.Attribute("hiddenButton") is null && column.Attribute("showButton") is null)
            {
                column.Remove();
            }
        }

        RangeAddress range = Range;
        for (int row = range.First.Row + 1; row <= range.Last.Row && range.RowCount > 1; row++)
        {
            if (sheet.Rows[row].Hidden)
            {
                sheet.Rows[row].Hidden = false;
            }
        }

        sheet.HasFilterCriteria = sheet.AutoFilter?.HasCriteria == true;
    }

    private IEnumerable<XElement> Columns() => element.Elements().Where(e => e.Name.LocalName == "filterColumn");

    private static int XlsxInt(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;

    private static bool Flag(XElement element, string name, bool fallback) => (string?)element.Attribute(name) switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => fallback,
    };

    private static bool TryNumber(string? text, out double number) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    /// <summary>The test a column's criteria make, or null when Egap does not evaluate them.</summary>
    private Func<int, bool>? Test(XElement criteria, int column, int firstRow, int lastRow) => criteria.Name.LocalName switch
    {
        "filters" => Values(criteria, column),
        "customFilters" => Custom(criteria, column),
        "top10" => Top(criteria, column, firstRow, lastRow),
        "dynamicFilter" => Dynamic(criteria, column, firstRow, lastRow),
        _ => null,
    };

    /// <summary>A list of values to show, with blanks and date groups: a cell passes when it shows one of them.</summary>
    private Func<int, bool> Values(XElement filters, int column)
    {
        bool blank = Flag(filters, "blank", false);
        HashSet<string> texts = new(StringComparer.OrdinalIgnoreCase);
        List<double> numbers = [];
        List<XElement> groups = [];
        foreach (XElement item in filters.Elements())
        {
            if (item.Name.LocalName == "filter" && (string?)item.Attribute("val") is { } value)
            {
                texts.Add(value);
                if (TryNumber(value, out double number))
                {
                    numbers.Add(number);
                }
            }
            else if (item.Name.LocalName == "dateGroupItem")
            {
                groups.Add(item);
            }
        }

        bool date1904 = sheet.Workbook.Date1904;
        return row =>
        {
            CellValue value = sheet.GetValue(row, column);
            if (IsBlank(value))
            {
                return blank;
            }

            if (value.Kind == CellValueKind.Number && (numbers.Exists(n => ExcelValues.CompareNumbers(n, value.Number) == 0) || groups.Exists(g => InGroup(g, value.Number, date1904))))
            {
                return true;
            }

            return texts.Count > 0 && texts.Contains(sheet[row, column].Text);
        };
    }

    private static bool IsBlank(CellValue value) => value.Kind == CellValueKind.Empty || (value.Kind == CellValueKind.Text && value.Text.Length == 0);

    /// <summary>Whether a date falls in a date group: the same year, and month, day, hour, minute and second down to its grouping.</summary>
    private static bool InGroup(XElement group, double serial, bool date1904)
    {
        if (ExcelValues.ToDateTime(serial, date1904) is not { } date)
        {
            return false;
        }

        int depth = (string?)group.Attribute("dateTimeGrouping") switch
        {
            "year" => 1,
            "month" => 2,
            "day" => 3,
            "hour" => 4,
            "minute" => 5,
            _ => 6,
        };
        int[] parts = [date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second];
        string[] names = ["year", "month", "day", "hour", "minute", "second"];
        for (int i = 0; i < depth; i++)
        {
            if (XlsxInt((string?)group.Attribute(names[i])) is int wanted && wanted >= 0 && wanted != parts[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One or two custom criteria, joined by and or or.</summary>
    private Func<int, bool>? Custom(XElement filters, int column)
    {
        List<Func<CellValue, bool>> parts = [.. filters.Elements().Where(e => e.Name.LocalName == "customFilter").Take(2).Select(Criterion)];
        if (parts.Count == 0)
        {
            return null;
        }

        bool and = Flag(filters, "and", false);
        return row =>
        {
            CellValue value = sheet.GetValue(row, column);
            return and ? parts.TrueForAll(p => p(value)) : parts.Exists(p => p(value));
        };
    }

    /// <summary>
    /// One custom criterion as Excel tests it: a number compares only with numbers and text only with text; wildcards match
    /// text only; a single space means blank; an empty value matches nothing.
    /// </summary>
    private Func<CellValue, bool> Criterion(XElement filter)
    {
        string op = (string?)filter.Attribute("operator") ?? "equal";
        string text = (string?)filter.Attribute("val") ?? "";
        bool equal = op == "equal", notEqual = op == "notEqual";
        if (text.Length == 0)
        {
            return _ => false;
        }

        if (text == " " && (equal || notEqual))
        {
            return value => IsBlank(value) == equal;
        }

        CompareInfo compare = sheet.Workbook.Culture.CompareInfo;
        if (TryNumber(text, out double number))
        {
            return value => value.Kind == CellValueKind.Number ? Holds(op, ExcelValues.CompareNumbers(value.Number, number)) : notEqual;
        }

        if ((equal || notEqual) && Wildcard.Has(text))
        {
            return value => (value.Kind == CellValueKind.Text && Wildcard.IsMatch(text, value.Text)) == equal;
        }

        if (equal || notEqual)
        {
            return value => (value.Kind == CellValueKind.Text && compare.Compare(value.Text, text, CompareOptions.IgnoreCase) == 0) == equal;
        }

        return value => value.Kind == CellValueKind.Text && Holds(op, compare.Compare(value.Text, text, CompareOptions.IgnoreCase));
    }

    private static bool Holds(string op, int order) => op switch
    {
        "lessThan" => order < 0,
        "lessThanOrEqual" => order <= 0,
        "greaterThan" => order > 0,
        "greaterThanOrEqual" => order >= 0,
        "notEqual" => order != 0,
        _ => order == 0,
    };

    private List<double> Numbers(int column, int firstRow, int lastRow)
    {
        List<double> numbers = [];
        for (int row = firstRow; row <= lastRow; row++)
        {
            CellValue value = sheet.GetValue(row, column);
            if (value.Kind == CellValueKind.Number)
            {
                numbers.Add(value.Number);
            }
        }

        return numbers;
    }

    /// <summary>The top or bottom items, or percent of items, of the column's numbers; ties with the last one pass too.</summary>
    private Func<int, bool> Top(XElement top10, int column, int firstRow, int lastRow)
    {
        bool top = Flag(top10, "top", true), percent = Flag(top10, "percent", false);
        List<double> numbers = Numbers(column, firstRow, lastRow);
        double wanted = TryNumber((string?)top10.Attribute("val"), out double v) ? v : 10;
        int count = percent ? (int)Math.Floor(numbers.Count * wanted / 100) : (int)wanted;
        if (numbers.Count == 0 || count < 1)
        {
            return _ => false;
        }

        numbers.Sort();
        double limit = top ? numbers[Math.Max(0, numbers.Count - count)] : numbers[Math.Min(numbers.Count, count) - 1];
        return row => sheet.GetValue(row, column) is { Kind: CellValueKind.Number } value && (top ? value.Number >= limit : value.Number <= limit);
    }

    /// <summary>Above or below the column's average, or a date period counted from today.</summary>
    private Func<int, bool>? Dynamic(XElement dynamic, int column, int firstRow, int lastRow)
    {
        string type = (string?)dynamic.Attribute("type") ?? "";
        if (type is "aboveAverage" or "belowAverage")
        {
            List<double> numbers = Numbers(column, firstRow, lastRow);
            double average = numbers.Count == 0 ? 0 : numbers.Average();
            bool above = type == "aboveAverage";
            return row => sheet.GetValue(row, column) is { Kind: CellValueKind.Number } value && (above ? value.Number > average : value.Number < average);
        }

        bool date1904 = sheet.Workbook.Date1904;
        if (type.Length >= 2 && type[0] is 'Q' or 'M' && int.TryParse(type.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int part))
        {
            // Q1 to Q4 and M1 to M12 take that quarter or month of any year.
            bool quarters = type[0] == 'Q';
            return row => sheet.GetValue(row, column) is { Kind: CellValueKind.Number } value && ExcelValues.ToDateTime(value.Number, date1904) is { } date
                && (quarters ? ((date.Month - 1) / 3) + 1 : date.Month) == part;
        }

        if (Period(type, Clock.Today) is not { } period)
        {
            return null;
        }

        return row => sheet.GetValue(row, column) is { Kind: CellValueKind.Number } value && ExcelValues.ToDateTime(value.Number, date1904) is { } date
            && date >= period.First && date < period.End;
    }

    /// <summary>The days a dynamic date filter takes, from the first to before the end; weeks start on Sunday, as in Excel.</summary>
    internal static (DateTime First, DateTime End)? Period(string type, DateTime today)
    {
        DateTime week = today.AddDays(-(int)today.DayOfWeek), month = new(today.Year, today.Month, 1), year = new(today.Year, 1, 1);
        DateTime quarter = new(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1);
        switch (type)
        {
            case "today": return (today, today.AddDays(1));
            case "yesterday": return (today.AddDays(-1), today);
            case "tomorrow": return (today.AddDays(1), today.AddDays(2));
            case "thisWeek": return (week, week.AddDays(7));
            case "lastWeek": return (week.AddDays(-7), week);
            case "nextWeek": return (week.AddDays(7), week.AddDays(14));
            case "thisMonth": return (month, month.AddMonths(1));
            case "lastMonth": return (month.AddMonths(-1), month);
            case "nextMonth": return (month.AddMonths(1), month.AddMonths(2));
            case "thisQuarter": return (quarter, quarter.AddMonths(3));
            case "lastQuarter": return (quarter.AddMonths(-3), quarter);
            case "nextQuarter": return (quarter.AddMonths(3), quarter.AddMonths(6));
            case "thisYear": return (year, year.AddYears(1));
            case "lastYear": return (year.AddYears(-1), year);
            case "nextYear": return (year.AddYears(1), year.AddYears(2));
            case "yearToDate": return (year, today.AddDays(1));
        }

        return null;
    }
}
