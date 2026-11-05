using System.Globalization;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// Date and time functions on serial numbers: whole days since the date system's start, with the time of day as the
/// fraction. In the 1900 system serial 60 is 29 February 1900, a day that never was, which Excel keeps for Lotus 1-2-3.
/// </summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> DateFunctions() =>
    [
        new("DATE", 3, 3, Kinds.Value, Date),
        new("TIME", 3, 3, Kinds.Value, Time),
        DatePart("YEAR", (y, m, d) => y),
        DatePart("MONTH", (y, m, d) => m),
        DatePart("DAY", (y, m, d) => d),
        TimePart("HOUR", seconds => seconds / 3600),
        TimePart("MINUTE", seconds => seconds / 60 % 60),
        TimePart("SECOND", seconds => seconds % 60),
        new("DATEVALUE", 1, 1, Kinds.Value, (a, c) => ParseDate(a, c, time: false)),
        new("TIMEVALUE", 1, 1, Kinds.Value, (a, c) => ParseDate(a, c, time: true)),
        new("NOW", 0, 0, Kinds.Value, static (_, c) => Number(ExcelValues.Serial(DateTime.Now.Date, c.Date1904) is double d ? d + DateTime.Now.TimeOfDay.TotalDays : 0)),
        new("TODAY", 0, 0, Kinds.Value, static (_, c) => Number(ExcelValues.Serial(DateTime.Today, c.Date1904) ?? 0)),
        new("WEEKDAY", 1, 2, Kinds.Value, Weekday),
        new("WEEKNUM", 1, 2, Kinds.Value, WeekNumber),
        new("ISOWEEKNUM", 1, 1, Kinds.Value, IsoWeekNumber),
        new("EDATE", 2, 2, Kinds.Value, (a, c) => AddMonths(a, c, endOfMonth: false)),
        new("EOMONTH", 2, 2, Kinds.Value, (a, c) => AddMonths(a, c, endOfMonth: true)),
        new("DAYS", 2, 2, Kinds.Value, Days),
        new("DAYS360", 2, 3, Kinds.Value, Days360),
        new("DATEDIF", 3, 3, Kinds.Value, DateDif),
        new("YEARFRAC", 2, 3, Kinds.Value, YearFrac),
        new("NETWORKDAYS", 2, 3, [ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Any], (a, c) => NetworkDays(a, c, intl: false)),
        new("NETWORKDAYS.INTL", 2, 4, [ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Any], (a, c) => NetworkDays(a, c, intl: true)),
        new("WORKDAY", 2, 3, [ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Any], (a, c) => Workday(a, c, intl: false)),
        new("WORKDAY.INTL", 2, 4, [ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Value, ArgumentKind.Any], (a, c) => Workday(a, c, intl: true)),
    ];

    private static double LastSerial(EvalContext context) => context.Date1904 ? ExcelValues.LastSerial1904 : ExcelValues.LastSerial1900;

    /// <summary>A serial number argument as whole days: #NUM! when negative or past 9999-12-31, #VALUE! when not a number.</summary>
    private static bool Day(in EvalValue value, EvalContext context, out double day, out EvalValue error)
    {
        if (!Num(value, context, out double serial, out error))
        {
            day = 0;
            return false;
        }

        day = Math.Floor(serial);
        if (serial < 0 || day > LastSerial(context))
        {
            error = Error(CellError.Number);
            return false;
        }

        return true;
    }

    /// <summary>The year, month and day of a whole serial, with Excel's day 0 (0 January 1900) and day 60 (29 February 1900).</summary>
    private static (int Year, int Month, int Day) Parts(double day, bool date1904)
    {
        if (!date1904)
        {
            if (day == 0)
            {
                return (1900, 1, 0);
            }

            if (day == 60)
            {
                return (1900, 2, 29);
            }
        }

        DateTime date = ToDate(day, date1904);
        return (date.Year, date.Month, date.Day);
    }

    /// <summary>The calendar date of a whole serial; day 60 of the 1900 system is taken as 28 February, day 0 as 31 December 1899.</summary>
    private static DateTime ToDate(double day, bool date1904)
    {
        if (date1904)
        {
            return new DateTime(1904, 1, 1).AddDays(day);
        }

        return day < 61 ? new DateTime(1899, 12, 31).AddDays(day == 60 ? 59 : day) : new DateTime(1899, 12, 30).AddDays(day);
    }

    private static double Serial(DateTime date, EvalContext context) => ExcelValues.Serial(date, context.Date1904) ?? -1;

    private static FormulaFunction DatePart(string name, Func<int, int, int, int> part) =>
        new(name, 1, 1, Kinds.Value, (a, c) =>
        {
            if (!Day(a[0], c, out double day, out EvalValue error))
            {
                return error;
            }

            (int y, int m, int d) = Parts(day, c.Date1904);
            return Number(part(y, m, d));
        });

    /// <summary>HOUR, MINUTE and SECOND: of the time of day, rounded to the nearest second.</summary>
    private static FormulaFunction TimePart(string name, Func<long, long> part) =>
        new(name, 1, 1, Kinds.Value, (a, c) =>
        {
            if (!Num(a[0], c, out double serial, out EvalValue error))
            {
                return error;
            }

            if (serial < 0 || serial > LastSerial(c) + 1)
            {
                return Error(CellError.Number);
            }

            long seconds = (long)Math.Round((serial - Math.Floor(serial)) * 86400, MidpointRounding.AwayFromZero) % 86400;
            return Number(part(seconds));
        });

    private static EvalValue Date(EvalValue[] arguments, EvalContext context)
    {
        Span<double> parts = stackalloc double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!Num(arguments[i], context, out parts[i], out EvalValue error))
            {
                return error;
            }
        }

        return ExcelValues.DateSerial(parts[0], parts[1], parts[2], context.Date1904) is double serial ? Number(serial) : Error(CellError.Number);
    }

    private static EvalValue Time(EvalValue[] arguments, EvalContext context)
    {
        Span<double> parts = stackalloc double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!Num(arguments[i], context, out double part, out EvalValue error))
            {
                return error;
            }

            parts[i] = Math.Truncate(part);
        }

        if (parts[0] > 32767 || parts[1] > 32767 || parts[2] > 32767)
        {
            return Error(CellError.Number);
        }

        double seconds = (parts[0] * 3600) + (parts[1] * 60) + parts[2];
        return seconds < 0 ? Error(CellError.Number) : Number(seconds % 86400 / 86400);
    }

    private static EvalValue ParseDate(EvalValue[] arguments, EvalContext context, bool time)
    {
        EvalValue value = arguments[0];
        if (value.IsError)
        {
            return value;
        }

        if (value.Kind != EvalKind.Text || !ExcelValues.TryParse(value.Text, context, out double serial) || serial < 0
            || !LooksLikeDate(value.Text, context))
        {
            return Error(CellError.Value);
        }

        return Number(time ? serial - Math.Floor(serial) : Math.Floor(serial));
    }

    /// <summary>Whether text reads as a date or a time rather than a plain number, as DATEVALUE and TIMEVALUE ask.</summary>
    private static bool LooksLikeDate(string text, EvalContext context) =>
        !double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Any, context.Culture, out _);

    /// <summary>WEEKDAY: the day of the week, numbered as the return type says.</summary>
    private static EvalValue Weekday(EvalValue[] arguments, EvalContext context)
    {
        if (!Day(arguments[0], context, out double day, out EvalValue error) || !Optional(arguments, 1, 1, context, out double type, out error))
        {
            return error;
        }

        int sunday0 = DayOfWeek(day, context.Date1904);
        int monday0 = (sunday0 + 6) % 7;
        return (int)Math.Truncate(type) switch
        {
            1 or 17 => Number(sunday0 + 1),
            2 or 11 => Number(monday0 + 1),
            3 => Number(monday0),
            int t and >= 12 and <= 16 => Number(((sunday0 - (t - 10) + 7) % 7) + 1),
            _ => Error(CellError.Number),
        };
    }

    /// <summary>The day of the week of a whole serial, 0 for Sunday; in the 1900 system day 1 is a Sunday, as Excel counts.</summary>
    private static int DayOfWeek(double day, bool date1904) => date1904 ? (int)((day + 5) % 7) : (int)((day + 6) % 7);

    /// <summary>WEEKNUM: the week of the year, the week holding 1 January being week 1, starting on the day the type says; 21 is ISO.</summary>
    private static EvalValue WeekNumber(EvalValue[] arguments, EvalContext context)
    {
        if (!Day(arguments[0], context, out double day, out EvalValue error) || !Optional(arguments, 1, 1, context, out double type, out error))
        {
            return error;
        }

        int start = (int)Math.Truncate(type) switch
        {
            1 or 17 => 0,
            2 or 11 => 1,
            int t and >= 12 and <= 16 => t - 10,
            21 => -1,
            _ => -2,
        };
        if (start == -2)
        {
            return Error(CellError.Number);
        }

        if (start == -1)
        {
            return Number(ISOWeek.GetWeekOfYear(ToDate(day, context.Date1904)));
        }

        (int year, _, _) = Parts(day, context.Date1904);
        double january1 = ExcelValues.DateSerial(year, 1, 1, context.Date1904) ?? 0;
        int offset = (DayOfWeek(january1, context.Date1904) - start + 7) % 7;
        return Number(Math.Floor((day - january1 + offset) / 7) + 1);
    }

    private static EvalValue IsoWeekNumber(EvalValue[] arguments, EvalContext context) =>
        Day(arguments[0], context, out double day, out EvalValue error) ? Number(ISOWeek.GetWeekOfYear(ToDate(day, context.Date1904))) : error;

    /// <summary>EDATE and EOMONTH: the date some months on (the day kept, or cut to the month's end), or the month's last day.</summary>
    private static EvalValue AddMonths(EvalValue[] arguments, EvalContext context, bool endOfMonth)
    {
        if (!Day(arguments[0], context, out double day, out EvalValue error) || !Num(arguments[1], context, out double months, out error))
        {
            return error;
        }

        (int y, int m, int d) = Parts(day, context.Date1904);
        double total = (y * 12) + m - 1 + Math.Truncate(months);
        int year = (int)Math.Floor(total / 12), month = (int)(total - (year * 12)) + 1;
        if (year < (context.Date1904 ? 1904 : 1900) || year > 9999)
        {
            return Error(CellError.Number);
        }

        int last = DateTime.DaysInMonth(year, month);
        double serial = Serial(new DateTime(year, month, endOfMonth ? last : Math.Min(Math.Max(d, 1), last)), context);
        return serial < 0 ? Error(CellError.Number) : Number(serial);
    }

    private static EvalValue Days(EvalValue[] arguments, EvalContext context)
    {
        if (!Day(arguments[0], context, out double end, out EvalValue error) || !Day(arguments[1], context, out double start, out error))
        {
            return error;
        }

        return Number(end - start);
    }

    /// <summary>DAYS360: days between two dates in a year of twelve 30-day months, by the US (NASD) or the European method.</summary>
    private static EvalValue Days360(EvalValue[] arguments, EvalContext context)
    {
        if (!Day(arguments[0], context, out double start, out EvalValue error)
            || !Day(arguments[1], context, out double end, out error)
            || !OptionalFlag(arguments, 2, false, out bool european, out error))
        {
            return error;
        }

        (int y1, int m1, int d1) = Parts(start, context.Date1904);
        (int y2, int m2, int d2) = Parts(end, context.Date1904);
        if (european)
        {
            d1 = Math.Min(d1, 30);
            d2 = Math.Min(d2, 30);
        }
        else
        {
            if (IsLastOfFebruary(y1, m1, d1))
            {
                d1 = 30;
            }

            if (d1 == 31)
            {
                d1 = 30;
            }

            if (d2 == 31 && d1 >= 30)
            {
                d2 = 30;
            }
        }

        return Number(((y2 - y1) * 360) + ((m2 - m1) * 30) + (d2 - d1));
    }

    private static bool IsLastOfFebruary(int year, int month, int day) => month == 2 && day >= DateTime.DaysInMonth(year, 2);

    /// <summary>DATEDIF: whole years, months or days between two dates, or the days or months left over.</summary>
    private static EvalValue DateDif(EvalValue[] arguments, EvalContext context)
    {
        if (!Day(arguments[0], context, out double start, out EvalValue error)
            || !Day(arguments[1], context, out double end, out error)
            || !Str(arguments[2], context, out string unit, out error))
        {
            return error;
        }

        if (start > end)
        {
            return Error(CellError.Number);
        }

        (int y1, int m1, int d1) = Parts(start, context.Date1904);
        (int y2, int m2, int d2) = Parts(end, context.Date1904);
        int months = ((y2 - y1) * 12) + (m2 - m1) - (d2 < d1 ? 1 : 0);
        switch (unit.ToUpperInvariant())
        {
            case "Y":
                return Number(months / 12);
            case "M":
                return Number(months);
            case "D":
                return Number(end - start);
            case "YM":
                return Number(months % 12);
            case "MD":
                if (d2 >= d1)
                {
                    return Number(d2 - d1);
                }

                (int py, int pm) = m2 == 1 ? (y2 - 1, 12) : (y2, m2 - 1);
                return Number(DateTime.DaysInMonth(py, pm) - d1 + d2);
            case "YD":
                // The days from the start's month and day to the end, in the end's year or the year before.
                int year = (m2 > m1 || (m2 == m1 && d2 >= d1)) ? y2 : y2 - 1;
                DateTime from = new(year, m1, Math.Min(d1 == 0 ? 1 : d1, DateTime.DaysInMonth(year, m1)));
                return Number(Math.Round((ToDate(end, context.Date1904) - from).TotalDays));
            default:
                return Error(CellError.Number);
        }
    }

    /// <summary>YEARFRAC: the part of a year between two dates, on one of five day-count bases.</summary>
    private static EvalValue YearFrac(EvalValue[] arguments, EvalContext context)
    {
        if (!Day(arguments[0], context, out double start, out EvalValue error)
            || !Day(arguments[1], context, out double end, out error)
            || !Optional(arguments, 2, 0, context, out double basisValue, out error))
        {
            return error;
        }

        int basis = (int)Math.Truncate(basisValue);
        if (basis is < 0 or > 4)
        {
            return Error(CellError.Number);
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        (int y1, int m1, int d1) = Parts(start, context.Date1904);
        (int y2, int m2, int d2) = Parts(end, context.Date1904);
        switch (basis)
        {
            case 0:
                if (IsLastOfFebruary(y1, m1, d1) && IsLastOfFebruary(y2, m2, d2))
                {
                    d2 = 30;
                }

                if (IsLastOfFebruary(y1, m1, d1) || d1 == 31)
                {
                    d1 = 30;
                }

                if (d2 == 31 && d1 >= 30)
                {
                    d2 = 30;
                }

                return Number((((y2 - y1) * 360) + ((m2 - m1) * 30) + (d2 - d1)) / 360.0);
            case 2:
                return Number((end - start) / 360);
            case 3:
                return Number((end - start) / 365);
            case 4:
                return Number((((y2 - y1) * 360) + ((m2 - m1) * 30) + (Math.Min(d2, 30) - Math.Min(d1, 30))) / 360.0);
        }

        // Actual/actual: within a year, a 365 or 366-day year; over longer spans, the average length of the years touched.
        if (start == end)
        {
            return Number(0);
        }

        bool withinYear = y1 == y2 || (y2 == y1 + 1 && (m1 > m2 || (m1 == m2 && d1 >= d2)));
        if (withinYear)
        {
            bool leap = (y1 == y2 && DateTime.IsLeapYear(y1)) || HoldsLeapDay(y1, m1, d1, y2, m2, d2);
            return Number((end - start) / (leap ? 366 : 365));
        }

        double years = y2 - y1 + 1;
        double days = (ExcelValues.DateSerial(y2 + 1, 1, 1, context.Date1904) ?? 0) - (ExcelValues.DateSerial(y1, 1, 1, context.Date1904) ?? 0);
        return Number((end - start) / (days / years));
    }

    /// <summary>Whether a 29 February lies between two dates, both counted.</summary>
    private static bool HoldsLeapDay(int y1, int m1, int d1, int y2, int m2, int d2)
    {
        for (int year = y1; year <= y2; year++)
        {
            if (!DateTime.IsLeapYear(year))
            {
                continue;
            }

            bool afterStart = year > y1 || m1 < 2 || (m1 == 2 && d1 <= 29);
            bool beforeEnd = year < y2 || m2 > 2 || (m2 == 2 && d2 >= 29);
            if (afterStart && beforeEnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The weekend days of the .INTL functions: a code (1 to 7 for two days, 11 to 17 for one) or seven 0s and 1s from Monday.
    /// Null when the argument is not one.
    /// </summary>
    private static bool[]? Weekend(EvalValue[] arguments, int index, EvalContext context, bool intl, bool allWeek, out EvalValue error)
    {
        error = default;
        bool[] weekend = new bool[7]; // Sunday first.
        if (!intl || index >= arguments.Length || arguments[index].Kind is EvalKind.Missing or EvalKind.Blank)
        {
            weekend[0] = weekend[6] = true;
            return weekend;
        }

        EvalValue value = arguments[index];
        if (value.IsError)
        {
            error = value;
            return null;
        }

        if (value.Kind == EvalKind.Text)
        {
            string mask = value.Text;
            if (mask.Length != 7 || mask.Any(ch => ch is not ('0' or '1')) || (mask == "1111111" && !allWeek))
            {
                error = Error(CellError.Value);
                return null;
            }

            for (int i = 0; i < 7; i++)
            {
                weekend[(i + 1) % 7] = mask[i] == '1';
            }

            return weekend;
        }

        if (!Num(value, context, out double code, out error))
        {
            return null;
        }

        switch ((int)Math.Truncate(code))
        {
            case int pair and >= 1 and <= 7:
                // 1 is Saturday and Sunday, 2 Sunday and Monday, ... 7 Friday and Saturday.
                weekend[(pair + 5) % 7] = weekend[(pair + 6) % 7] = true;
                return weekend;
            case int single and >= 11 and <= 17:
                weekend[(single - 11) % 7] = true;
                return weekend;
            default:
                error = Error(CellError.Number);
                return null;
        }
    }

    /// <summary>The whole serials a holidays argument lists.</summary>
    private static HashSet<double>? Holidays(EvalValue[] arguments, int index, EvalContext context, out EvalValue error)
    {
        error = default;
        HashSet<double> days = [];
        if (index >= arguments.Length)
        {
            return days;
        }

        foreach ((EvalValue value, bool _) in Scalars(arguments[index]))
        {
            if (value.Kind is EvalKind.Blank or EvalKind.Missing)
            {
                continue;
            }

            if (!Day(value, context, out double day, out error))
            {
                return null;
            }

            days.Add(day);
        }

        return days;
    }

    private static EvalValue NetworkDays(EvalValue[] arguments, EvalContext context, bool intl)
    {
        if (!Day(arguments[0], context, out double start, out EvalValue error) || !Day(arguments[1], context, out double end, out error))
        {
            return error;
        }

        if (Weekend(arguments, 2, context, intl, allWeek: true, out error) is not { } weekend || Holidays(arguments, intl ? 3 : 2, context, out error) is not { } holidays)
        {
            return error;
        }

        int sign = end >= start ? 1 : -1;
        (double low, double high) = sign > 0 ? (start, end) : (end, start);
        int workdaysPerWeek = weekend.Count(w => !w);
        double span = high - low + 1;
        double weeks = Math.Floor(span / 7);
        double count = weeks * workdaysPerWeek;
        for (double day = low + (weeks * 7); day <= high; day++)
        {
            if (!weekend[DayOfWeek(day, context.Date1904)])
            {
                count++;
            }
        }

        foreach (double holiday in holidays)
        {
            if (holiday >= low && holiday <= high && !weekend[DayOfWeek(holiday, context.Date1904)])
            {
                count--;
            }
        }

        return Number(sign * count);
    }

    private static EvalValue Workday(EvalValue[] arguments, EvalContext context, bool intl)
    {
        if (!Day(arguments[0], context, out double day, out EvalValue error) || !Num(arguments[1], context, out double daysValue, out error))
        {
            return error;
        }

        if (Weekend(arguments, 2, context, intl, allWeek: false, out error) is not { } weekend || Holidays(arguments, intl ? 3 : 2, context, out error) is not { } holidays)
        {
            return error;
        }

        double remaining = Math.Truncate(daysValue);
        int step = remaining >= 0 ? 1 : -1;
        if (Math.Abs(remaining) > 1_000_000)
        {
            return Error(CellError.Number);
        }

        double last = LastSerial(context);
        while (remaining != 0)
        {
            day += step;
            if (day < 0 || day > last)
            {
                return Error(CellError.Number);
            }

            if (!weekend[DayOfWeek(day, context.Date1904)] && !holidays.Contains(day))
            {
                remaining -= step;
            }
        }

        return Number(day);
    }
}
