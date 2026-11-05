using System.Globalization;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// Excel's rules for turning values into one another and comparing them: text read as a number or a date, numbers written
/// as text with 15 significant digits, comparisons that treat numbers equal to 15 significant digits, and the 1900 and 1904
/// date systems with the 29 February 1900 Excel keeps.
/// </summary>
internal static class ExcelValues
{
    private static readonly DateTime Base1900 = new(1899, 12, 31);
    private static readonly DateTime Base1904 = new(1904, 1, 1);
    private static readonly DateTime First1900 = new(1900, 1, 1);
    private static readonly DateTime LeapBug = new(1900, 3, 1);

    /// <summary>The serial of 9999-12-31, the last date Excel has, in the 1900 system.</summary>
    public const double LastSerial1900 = 2958465;

    /// <summary>The serial of 9999-12-31 in the 1904 system.</summary>
    public const double LastSerial1904 = LastSerial1900 - 1462;

    /// <summary>A scalar as a number: blank and missing are 0, TRUE is 1, text is read as a number or a date.</summary>
    public static bool TryNumber(in EvalValue value, EvalContext context, out double number, out EvalValue error)
    {
        error = default;
        switch (value.Kind)
        {
            case EvalKind.Number:
            case EvalKind.Boolean:
                number = value.Number;
                return true;
            case EvalKind.Text:
                if (TryParse(value.Text, context, out number))
                {
                    return true;
                }

                error = EvalValue.FromError(CellError.Value);
                return false;
            case EvalKind.Error:
                number = 0;
                error = value;
                return false;
            case EvalKind.Blank:
            case EvalKind.Missing:
                number = 0;
                return true;
            default:
                number = 0;
                error = EvalValue.FromError(CellError.Value);
                return false;
        }
    }

    /// <summary>
    /// A scalar as text: numbers with up to 15 significant digits and the culture's decimal separator, TRUE or FALSE,
    /// blank as empty text.
    /// </summary>
    public static bool TryText(in EvalValue value, CultureInfo culture, out string text, out EvalValue error)
    {
        error = default;
        switch (value.Kind)
        {
            case EvalKind.Text:
                text = value.Text;
                return true;
            case EvalKind.Number:
                text = NumberText(value.Number, culture);
                return true;
            case EvalKind.Boolean:
                text = value.Boolean ? "TRUE" : "FALSE";
                return true;
            case EvalKind.Error:
                text = "";
                error = value;
                return false;
            case EvalKind.Blank:
            case EvalKind.Missing:
                text = "";
                return true;
            default:
                text = "";
                error = EvalValue.FromError(CellError.Value);
                return false;
        }
    }

    /// <summary>A scalar as TRUE or FALSE: a number is TRUE unless 0, text must read TRUE or FALSE, blank is FALSE.</summary>
    public static bool TryBoolean(in EvalValue value, out bool result, out EvalValue error)
    {
        error = default;
        switch (value.Kind)
        {
            case EvalKind.Number:
            case EvalKind.Boolean:
                result = value.Number != 0;
                return true;
            case EvalKind.Text when value.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase):
                result = true;
                return true;
            case EvalKind.Text when value.Text.Equals("FALSE", StringComparison.OrdinalIgnoreCase):
                result = false;
                return true;
            case EvalKind.Error:
                result = false;
                error = value;
                return false;
            case EvalKind.Blank:
            case EvalKind.Missing:
                result = false;
                return true;
            default:
                result = false;
                error = EvalValue.FromError(CellError.Value);
                return false;
        }
    }

    /// <summary>
    /// A number as Excel writes it into text: up to 15 significant digits, in E notation when it is very large or small,
    /// with the culture's decimal separator (<c>=1.5&amp;""</c> is <c>1,5</c> where the decimal separator is a comma).
    /// </summary>
    public static string NumberText(double number, CultureInfo culture)
    {
        string text = number.ToString("G15", CultureInfo.InvariantCulture);
        string separator = culture.NumberFormat.NumberDecimalSeparator;
        return separator == "." ? text : text.Replace(".", separator, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads text as a number the way Excel does when text meets arithmetic: a number in the workbook's culture with
    /// optional group separators, currency symbol, percent sign or parentheses for negative, or a date or a time.
    /// </summary>
    public static bool TryParse(string text, EvalContext context, out double number)
    {
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        number = 0;
        if (trimmed.IsEmpty)
        {
            return false;
        }

        CultureInfo culture = context.Culture;
        bool percent = false;
        if (trimmed[^1] == '%')
        {
            percent = true;
            trimmed = trimmed[..^1].TrimEnd();
        }

        const NumberStyles Styles = NumberStyles.Float | NumberStyles.AllowThousands | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowParentheses;
        if (double.TryParse(trimmed, Styles, culture, out number) && double.IsFinite(number))
        {
            number = percent ? number / 100 : number;
            return true;
        }

        if (percent)
        {
            return false;
        }

        if (DateTime.TryParse(trimmed, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out DateTime date))
        {
            if (date.Date == DateTime.MinValue.Date)
            {
                number = date.TimeOfDay.TotalDays;
                return true;
            }

            if (Serial(date.Date, context.Date1904) is double serial)
            {
                number = serial + date.TimeOfDay.TotalDays;
                return true;
            }
        }

        number = 0;
        return false;
    }

    /// <summary>The serial number of a date, or null before the first day of the date system.</summary>
    public static double? Serial(DateTime date, bool date1904)
    {
        if (date1904)
        {
            return date < Base1904 ? null : (date - Base1904).TotalDays;
        }

        if (date < First1900)
        {
            return null;
        }

        double days = (date - Base1900).TotalDays;
        return date >= LeapBug ? days + 1 : days;
    }

    /// <summary>
    /// The serial number of a year, month and day as DATE gives it: years 0 to 1899 count from 1900, months and days out of
    /// range roll into the next or previous year and month, and DATE(1900,2,29) is day 60. Null when the date is before the
    /// date system's first day or after 9999-12-31.
    /// </summary>
    public static double? DateSerial(double year, double month, double day, bool date1904)
    {
        year = Math.Truncate(year);
        if (year < 0 || year >= 10000)
        {
            return null;
        }

        if (year < 1900)
        {
            year += 1900;
        }

        double months = (year * 12) + Math.Truncate(month) - 1;
        double y = Math.Floor(months / 12);
        int m = (int)(months - (y * 12)) + 1;
        if (y < (date1904 ? 1904 : 1900) || y > 9999)
        {
            return null;
        }

        double? first = Serial(new DateTime((int)y, m, 1), date1904);
        if (first is null)
        {
            return null;
        }

        double serial = first.Value + Math.Truncate(day) - 1;
        return serial < 0 || serial > (date1904 ? LastSerial1904 : LastSerial1900) ? null : serial;
    }

    /// <summary>
    /// Compares two scalars that are not errors as Excel's comparison operators do: a blank is 0, empty text or FALSE to
    /// match the other side; numbers sort before text and text before booleans; text compares without case; numbers equal to
    /// 15 significant digits are equal.
    /// </summary>
    public static int Compare(in EvalValue left, in EvalValue right, EvalContext context)
    {
        EvalValue a = Fill(left, right), b = Fill(right, left);
        int rankA = Rank(a.Kind), rankB = Rank(b.Kind);
        if (rankA != rankB)
        {
            return rankA.CompareTo(rankB);
        }

        return a.Kind switch
        {
            EvalKind.Text => Math.Sign(context.Culture.CompareInfo.Compare(a.Text, b.Text, CompareOptions.IgnoreCase)),
            EvalKind.Blank => 0,
            _ => CompareNumbers(a.Number, b.Number),
        };
    }

    private static EvalValue Fill(in EvalValue value, in EvalValue other)
    {
        if (value.Kind is not (EvalKind.Blank or EvalKind.Missing))
        {
            return value;
        }

        return other.Kind switch
        {
            EvalKind.Text => EvalValue.FromText(""),
            EvalKind.Boolean => EvalValue.False,
            EvalKind.Number => EvalValue.FromNumber(0),
            _ => EvalValue.Blank,
        };
    }

    private static int Rank(EvalKind kind) => kind switch
    {
        EvalKind.Number => 0,
        EvalKind.Text => 1,
        EvalKind.Boolean => 2,
        _ => -1,
    };

    /// <summary>Compares numbers as Excel does: two numbers that agree to 15 significant digits are equal.</summary>
    public static int CompareNumbers(double a, double b)
    {
        if (a == b)
        {
            return 0;
        }

        return Round15(a) == Round15(b) ? 0 : a.CompareTo(b);
    }

    /// <summary>The number rounded to 15 significant digits.</summary>
    public static double Round15(double value)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return value;
        }

        double magnitude = Math.Floor(Math.Log10(Math.Abs(value)));
        int digits = 14 - (int)magnitude;
        if (digits is >= 0 and <= 15)
        {
            return Math.Round(value, digits, MidpointRounding.AwayFromZero);
        }

        return double.Parse(value.ToString("E14", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
