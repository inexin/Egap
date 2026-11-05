using System.Globalization;
using System.Text;


namespace Egap.Spreadsheets.Formulas;

/// <summary>Text functions. Positions and lengths count UTF-16 code units, as Excel's do.</summary>
internal static partial class FormulaFunctions
{
    /// <summary>The Windows-1252 characters at 0x80 to 0x9F, which CHAR and CODE use as Excel on Windows does.</summary>
    private const string Windows1252High =
        "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F" +
        "\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    private static IEnumerable<FormulaFunction> TextFunctions() =>
    [
        Text1("LEN", (s, c) => Number(s.Length)),
        Text1("LOWER", (s, c) => EvalValue.FromText(c.Culture.TextInfo.ToLower(s))),
        Text1("UPPER", (s, c) => EvalValue.FromText(c.Culture.TextInfo.ToUpper(s))),
        Text1("PROPER", (s, c) => EvalValue.FromText(Proper(s, c.Culture))),
        Text1("TRIM", (s, c) => EvalValue.FromText(Trim(s))),
        Text1("CLEAN", (s, c) => EvalValue.FromText(string.Concat(s.Where(ch => ch >= 32)))),
        Text1("CODE", (s, c) => Code(s)),
        Text1("UNICODE", (s, c) => s.Length == 0 ? Error(CellError.Value)
            : Number(s.Length > 1 && char.IsSurrogatePair(s[0], s[1]) ? char.ConvertToUtf32(s[0], s[1]) : s[0])),
        new("CHAR", 1, 1, Kinds.Value, Char),
        new("UNICHAR", 1, 1, Kinds.Value, UniChar),
        new("LEFT", 1, 2, Kinds.Value, (a, c) => Side(a, c, left: true)),
        new("RIGHT", 1, 2, Kinds.Value, (a, c) => Side(a, c, left: false)),
        new("MID", 3, 3, Kinds.Value, Mid),
        new("REPLACE", 4, 4, Kinds.Value, Replace),
        new("REPT", 2, 2, Kinds.Value, Repeat),
        new("SUBSTITUTE", 3, 4, Kinds.Value, Substitute),
        new("FIND", 2, 3, Kinds.Value, (a, c) => Find(a, c, search: false)),
        new("SEARCH", 2, 3, Kinds.Value, (a, c) => Find(a, c, search: true)),
        new("EXACT", 2, 2, Kinds.Value, Exact),
        new("T", 1, 1, Kinds.Value, static (a, _) => a[0].Kind switch
        {
            EvalKind.Text => a[0],
            EvalKind.Error => a[0],
            _ => EvalValue.FromText(""),
        }),
        new("VALUE", 1, 1, Kinds.Value, Value),
        new("NUMBERVALUE", 1, 3, Kinds.Value, NumberValue),
        new("TEXT", 2, 2, Kinds.Value, TextFormat),
        new("FIXED", 1, 3, Kinds.Value, Fixed),
        new("DOLLAR", 1, 2, Kinds.Value, Dollar),
        new("CONCATENATE", 1, 255, Kinds.Value, Concatenate),
        new("CONCAT", 1, 254, Kinds.Any, (a, c) => Join(a, 0, "", ignoreEmpty: false, c)),
        new("TEXTJOIN", 3, 252, [ArgumentKind.Any, ArgumentKind.Value, ArgumentKind.Any], TextJoin),
    ];

    private static FormulaFunction Text1(string name, Func<string, EvalContext, EvalValue> function) =>
        new(name, 1, 1, Kinds.Value, (a, c) => Str(a[0], c, out string s, out EvalValue e) ? function(s, c) : e);

    private static string Proper(string text, CultureInfo culture)
    {
        StringBuilder result = new(text.Length);
        bool afterLetter = false;
        foreach (char ch in text)
        {
            result.Append(afterLetter ? culture.TextInfo.ToLower(ch) : culture.TextInfo.ToUpper(ch));
            afterLetter = char.IsLetter(ch);
        }

        return result.ToString();
    }

    /// <summary>TRIM: spaces at either end gone and runs of spaces inside made one; other white space stays.</summary>
    private static string Trim(string text)
    {
        StringBuilder result = new(text.Length);
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Length > 0)
            {
                result.Append(' ');
            }

            result.Append(word);
        }

        return result.ToString();
    }

    private static EvalValue Code(string text)
    {
        if (text.Length == 0)
        {
            return Error(CellError.Value);
        }

        char first = text[0];
        if (first < 0x80 || (first >= 0xA0 && first <= 0xFF))
        {
            return Number(first);
        }

        int high = Windows1252High.IndexOf(first, StringComparison.Ordinal);
        return Number(high >= 0 ? 0x80 + high : '?');
    }

    private static EvalValue Char(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double code, out EvalValue error))
        {
            return error;
        }

        int n = (int)Math.Truncate(Math.Clamp(code, -1, 256));
        if (n is < 1 or > 255)
        {
            return Error(CellError.Value);
        }

        char ch = n is >= 0x80 and < 0xA0 ? Windows1252High[n - 0x80] : (char)n;
        return EvalValue.FromText(ch.ToString());
    }

    private static EvalValue UniChar(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double code, out EvalValue error))
        {
            return error;
        }

        double n = Math.Truncate(code);
        if (n < 1 || n > 0x10FFFF || (n >= 0xD800 && n <= 0xDFFF))
        {
            return Error(CellError.Value);
        }

        return EvalValue.FromText(char.ConvertFromUtf32((int)n));
    }

    private static EvalValue Side(EvalValue[] arguments, EvalContext context, bool left)
    {
        if (!Str(arguments[0], context, out string text, out EvalValue error) || !Optional(arguments, 1, 1, context, out double count, out error))
        {
            return error;
        }

        if (count < 0)
        {
            return Error(CellError.Value);
        }

        int n = (int)Math.Min(Math.Truncate(count), text.Length);
        return EvalValue.FromText(left ? text[..n] : text[^n..]);
    }

    private static EvalValue Mid(EvalValue[] arguments, EvalContext context)
    {
        if (!Str(arguments[0], context, out string text, out EvalValue error)
            || !Num(arguments[1], context, out double start, out error)
            || !Num(arguments[2], context, out double count, out error))
        {
            return error;
        }

        if (start < 1 || count < 0)
        {
            return Error(CellError.Value);
        }

        int from = (int)Math.Min(Math.Truncate(start) - 1, text.Length);
        int n = (int)Math.Min(Math.Truncate(count), text.Length - from);
        return EvalValue.FromText(text.Substring(from, n));
    }

    private static EvalValue Replace(EvalValue[] arguments, EvalContext context)
    {
        if (!Str(arguments[0], context, out string text, out EvalValue error)
            || !Num(arguments[1], context, out double start, out error)
            || !Num(arguments[2], context, out double count, out error)
            || !Str(arguments[3], context, out string with, out error))
        {
            return error;
        }

        if (start < 1 || count < 0)
        {
            return Error(CellError.Value);
        }

        int from = (int)Math.Min(Math.Truncate(start) - 1, text.Length);
        int n = (int)Math.Min(Math.Truncate(count), text.Length - from);
        return EvalValue.FromText(string.Concat(text.AsSpan(0, from), with, text.AsSpan(from + n)));
    }

    private static EvalValue Repeat(EvalValue[] arguments, EvalContext context)
    {
        if (!Str(arguments[0], context, out string text, out EvalValue error) || !Num(arguments[1], context, out double times, out error))
        {
            return error;
        }

        if (times < 0 || text.Length * Math.Truncate(times) > CellValue.MaxTextLength)
        {
            return Error(CellError.Value);
        }

        return EvalValue.FromText(new StringBuilder().Insert(0, text, (int)Math.Truncate(times)).ToString());
    }

    private static EvalValue Substitute(EvalValue[] arguments, EvalContext context)
    {
        if (!Str(arguments[0], context, out string text, out EvalValue error)
            || !Str(arguments[1], context, out string old, out error)
            || !Str(arguments[2], context, out string with, out error))
        {
            return error;
        }

        if (arguments.Length < 4)
        {
            return EvalValue.FromText(old.Length == 0 ? text : text.Replace(old, with, StringComparison.Ordinal));
        }

        if (!Num(arguments[3], context, out double instance, out error))
        {
            return error;
        }

        if (instance < 1)
        {
            return Error(CellError.Value);
        }

        if (old.Length == 0)
        {
            return EvalValue.FromText(text);
        }

        int at = -1;
        for (double i = 0; i < Math.Truncate(instance); i++)
        {
            at = text.IndexOf(old, at + (at < 0 ? 1 : old.Length), StringComparison.Ordinal);
            if (at < 0)
            {
                return EvalValue.FromText(text);
            }
        }

        return EvalValue.FromText(string.Concat(text.AsSpan(0, at), with, text.AsSpan(at + old.Length)));
    }

    /// <summary>FIND (case kept, no wildcards) and SEARCH (case ignored, wildcards): the 1-based position, or #VALUE!.</summary>
    private static EvalValue Find(EvalValue[] arguments, EvalContext context, bool search)
    {
        if (!Str(arguments[0], context, out string find, out EvalValue error)
            || !Str(arguments[1], context, out string within, out error)
            || !Optional(arguments, 2, 1, context, out double start, out error))
        {
            return error;
        }

        start = Math.Truncate(start);
        if (start < 1 || start > within.Length + 1)
        {
            return Error(CellError.Value);
        }

        int from = (int)start - 1;
        if (find.Length == 0)
        {
            return Number(start);
        }

        int at;
        if (!search)
        {
            at = within.IndexOf(find, from, StringComparison.Ordinal);
        }
        else if (Wildcard.Has(find))
        {
            at = Wildcard.Find(find, within, from).Index;
        }
        else
        {
            at = within.IndexOf(find, from, StringComparison.OrdinalIgnoreCase);
        }

        return at < 0 ? Error(CellError.Value) : Number(at + 1);
    }

    private static EvalValue Exact(EvalValue[] arguments, EvalContext context)
    {
        if (!Str(arguments[0], context, out string a, out EvalValue error) || !Str(arguments[1], context, out string b, out error))
        {
            return error;
        }

        return EvalValue.FromBoolean(string.Equals(a, b, StringComparison.Ordinal));
    }

    private static EvalValue Value(EvalValue[] arguments, EvalContext context)
    {
        EvalValue value = arguments[0];
        return value.Kind switch
        {
            EvalKind.Number => value,
            EvalKind.Error => value,
            EvalKind.Blank or EvalKind.Missing => Number(0),
            EvalKind.Text => ExcelValues.TryParse(value.Text, context, out double n) ? Number(n) : Error(CellError.Value),
            _ => Error(CellError.Value),
        };
    }

    /// <summary>NUMBERVALUE: text read as a number with the given decimal and group separators, or the culture's when left off.</summary>
    private static EvalValue NumberValue(EvalValue[] arguments, EvalContext context)
    {
        string decimalSeparator = context.Culture.NumberFormat.NumberDecimalSeparator;
        string groupSeparator = context.Culture.NumberFormat.NumberGroupSeparator;
        if (!Str(arguments[0], context, out string text, out EvalValue error))
        {
            return error;
        }

        if (arguments.Length > 1)
        {
            if (!Str(arguments[1], context, out decimalSeparator, out error))
            {
                return error;
            }

            decimalSeparator = decimalSeparator.Length > 0 ? decimalSeparator[..1] : "";
        }

        if (arguments.Length > 2)
        {
            if (!Str(arguments[2], context, out groupSeparator, out error))
            {
                return error;
            }

            groupSeparator = groupSeparator.Length > 0 ? groupSeparator[..1] : "";
        }

        if (decimalSeparator.Length == 0 || decimalSeparator == groupSeparator)
        {
            return Error(CellError.Value);
        }

        string compact = string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)));
        if (compact.Length == 0)
        {
            return Number(0);
        }

        int percents = 0;
        while (compact.EndsWith('%'))
        {
            percents++;
            compact = compact[..^1];
        }

        int point = compact.IndexOf(decimalSeparator, StringComparison.Ordinal);
        string whole = point < 0 ? compact : compact[..point];
        string fraction = point < 0 ? "" : compact[(point + 1)..];
        if (groupSeparator.Length > 0)
        {
            whole = whole.Replace(groupSeparator, "", StringComparison.Ordinal);
        }

        if (fraction.Contains(groupSeparator, StringComparison.Ordinal) && groupSeparator.Length > 0)
        {
            return Error(CellError.Value);
        }

        string invariant = fraction.Length > 0 || point >= 0 ? whole + "." + fraction : whole;
        bool stray = whole.AsSpan().IndexOfAny('.', ',') >= 0 || fraction.AsSpan().IndexOfAny('.', ',') >= 0;
        if (stray || !double.TryParse(invariant, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out double number))
        {
            // Without separators given, text that is no plain number is read as VALUE reads it, a date or a time too.
            return arguments.Length == 1 && ExcelValues.TryParse(compact, context, out number)
                ? Number(number / Math.Pow(100, percents))
                : Error(CellError.Value);
        }

        return Number(number / Math.Pow(100, percents));
    }

    /// <summary>TEXT: a value shown through a number format code, as a cell with that format shows it.</summary>
    private static EvalValue TextFormat(EvalValue[] arguments, EvalContext context)
    {
        EvalValue value = arguments[0];
        if (value.IsError)
        {
            return value;
        }

        if (!Str(arguments[1], context, out string code, out EvalValue error))
        {
            return error;
        }

        CellValue cell = value.Kind switch
        {
            EvalKind.Number => CellValue.FromNumber(value.Number),
            EvalKind.Boolean => CellValue.FromBoolean(value.Boolean),
            EvalKind.Text when ExcelValues.TryParse(value.Text, context, out double n) => CellValue.FromNumber(n),
            EvalKind.Text => CellValue.FromText(value.Text),
            _ => CellValue.FromNumber(0),
        };
        return EvalValue.FromText(NumberFormatCode.Get(code).Format(cell, context.Culture, context.Date1904));
    }

    /// <summary>FIXED: a number rounded and written with the culture's separators, with or without grouping.</summary>
    private static EvalValue Fixed(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double number, out EvalValue error)
            || !Optional(arguments, 1, 2, context, out double places, out error)
            || !OptionalFlag(arguments, 2, false, out bool noCommas, out error))
        {
            return error;
        }

        places = Math.Truncate(places);
        if (places > 127)
        {
            return Error(CellError.Value);
        }

        double rounded = Round(number, places, Rounding.Nearest);
        int shown = (int)Math.Clamp(places, 0, 127);
        return EvalValue.FromText(rounded.ToString((noCommas ? "F" : "N") + shown.ToString(CultureInfo.InvariantCulture), Plain(context.Culture)));
    }

    /// <summary>DOLLAR: a number rounded and written as currency in the culture, a negative one with a minus in front.</summary>
    private static EvalValue Dollar(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double number, out EvalValue error) || !Optional(arguments, 1, 2, context, out double places, out error))
        {
            return error;
        }

        places = Math.Truncate(places);
        if (places > 127)
        {
            return Error(CellError.Value);
        }

        double rounded = Round(number, places, Rounding.Nearest);
        NumberFormatInfo format = (NumberFormatInfo)Plain(context.Culture).Clone();
        format.CurrencyNegativePattern = format.CurrencyPositivePattern switch
        {
            0 => 1,     // -$n
            1 => 5,     // -n$
            2 => 9,     // -$ n
            _ => 8,     // -n $
        };
        return EvalValue.FromText(rounded.ToString("C" + ((int)Math.Clamp(places, 0, 127)).ToString(CultureInfo.InvariantCulture), format));
    }

    /// <summary>The culture's number format with a plain hyphen for minus, as Excel writes it, not a Unicode minus sign.</summary>
    private static NumberFormatInfo Plain(CultureInfo culture)
    {
        NumberFormatInfo format = culture.NumberFormat;
        if (format.NegativeSign == "-")
        {
            return format;
        }

        format = (NumberFormatInfo)format.Clone();
        format.NegativeSign = "-";
        return format;
    }

    private static EvalValue Concatenate(EvalValue[] arguments, EvalContext context)
    {
        StringBuilder result = new();
        foreach (EvalValue argument in arguments)
        {
            if (!Str(argument, context, out string text, out EvalValue error))
            {
                return error;
            }

            result.Append(text);
            if (result.Length > CellValue.MaxTextLength)
            {
                return Error(CellError.Value);
            }
        }

        return EvalValue.FromText(result.ToString());
    }

    private static EvalValue TextJoin(EvalValue[] arguments, EvalContext context)
    {
        List<string> delimiters = [];
        foreach ((EvalValue value, bool _) in Scalars(arguments[0]))
        {
            if (!Str(value, context, out string text, out EvalValue error))
            {
                return error;
            }

            delimiters.Add(text);
        }

        if (!ExcelValues.TryBoolean(arguments[1], out bool ignoreEmpty, out EvalValue flagError))
        {
            return flagError;
        }

        return Join(arguments, 2, delimiters.Count == 0 ? [""] : [.. delimiters], ignoreEmpty, context);
    }

    private static EvalValue Join(EvalValue[] arguments, int first, string delimiter, bool ignoreEmpty, EvalContext context) =>
        Join(arguments, first, [delimiter], ignoreEmpty, context);

    /// <summary>CONCAT and TEXTJOIN: every value of the arguments, references and arrays cell by cell, joined.</summary>
    private static EvalValue Join(EvalValue[] arguments, int first, string[] delimiters, bool ignoreEmpty, EvalContext context)
    {
        StringBuilder result = new();
        int count = 0;
        for (int i = first; i < arguments.Length; i++)
        {
            foreach (EvalValue value in Cells(arguments[i]))
            {
                if (!Str(value, context, out string text, out EvalValue error))
                {
                    return error;
                }

                if (ignoreEmpty && text.Length == 0)
                {
                    continue;
                }

                if (count > 0)
                {
                    result.Append(delimiters[(count - 1) % delimiters.Length]);
                }

                result.Append(text);
                count++;
                if (result.Length > CellValue.MaxTextLength)
                {
                    return Error(CellError.Value);
                }
            }
        }

        return EvalValue.FromText(result.ToString());
    }

    /// <summary>Every cell of an argument in row order, blanks included, as CONCAT and TEXTJOIN walk them.</summary>
    private static IEnumerable<EvalValue> Cells(EvalValue argument)
    {
        if (argument.Kind == EvalKind.Reference)
        {
            foreach (SheetArea area in argument.Areas)
            {
                Grid grid = Grid.Of(EvalValue.FromArea(area.Sheet, area.Range))!.Value;
                HashSet<(int, int)> filled = [.. grid.Occupied()];
                for (int row = 0; row < grid.Rows; row++)
                {
                    for (int column = 0; column < grid.Columns; column++)
                    {
                        yield return filled.Contains((row, column)) ? grid[row, column] : EvalValue.Blank;
                    }
                }
            }

            yield break;
        }

        foreach ((EvalValue value, bool _) in Scalars(argument))
        {
            yield return value;
        }
    }
}
