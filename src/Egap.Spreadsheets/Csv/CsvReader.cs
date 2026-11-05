using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Csv;

/// <summary>Reads RFC 4180 text into a new workbook's single sheet, one row at a time.</summary>
internal static class CsvReader
{
    public static Workbook Read(Stream stream, string? path, CsvOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new CsvOptions();
        using StreamReader text = new(stream, options.Encoding ?? new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);
        CsvParser parser = new(text);
        // The hint line is always read, so it never shows as a row, but a separator the options name wins over it.
        char? hint = parser.ReadHint();
        char separator = options.Separator ?? hint ?? options.SeparatorFor(path) ?? parser.DetectSeparator();
        parser.Separator = separator;

        Workbook book = Workbook.CreateEmpty();
        Worksheet sheet = book.Worksheets.Add(options.SheetName);
        TypeDetector? detector = options.DetectTypes ? new TypeDetector(options.Culture, book) : null;
        List<string> fields = [];
        int row = 0;
        while (parser.ReadRow(fields))
        {
            if (row >= CellAddress.MaxRows)
            {
                throw new InvalidDataException($"The text has more than {CellAddress.MaxRows} rows, the most a sheet holds.");
            }

            if (fields.Count > CellAddress.MaxColumns)
            {
                throw new InvalidDataException($"Row {row + 1} has more than {CellAddress.MaxColumns} fields, the most a sheet holds.");
            }

            for (int column = 0; column < fields.Count; column++)
            {
                string field = fields[column];
                if (field.Length == 0)
                {
                    continue;
                }

                if (field.Length > CellValue.MaxTextLength)
                {
                    throw new InvalidDataException($"Row {row + 1}, field {column + 1} is longer than the {CellValue.MaxTextLength} characters a cell holds.");
                }

                if (detector is not null && detector.Detect(field) is ({ } value, int style))
                {
                    sheet.SetValue(row, column, value);
                    if (style != 0)
                    {
                        sheet.SetStyleIndex(row, column, style);
                    }
                }
                else
                {
                    sheet.SetValue(row, column, CellValue.TextUnchecked(field));
                }
            }

            row++;
        }

        return book;
    }
}

/// <summary>Splits text into rows of fields: quoted fields may hold separators, doubled quotes and line breaks.</summary>
internal sealed class CsvParser(TextReader reader)
{
    private readonly char[] buffer = new char[65536];
    private readonly StringBuilder field = new();
    private int position;
    private int length;

    public char Separator { get; set; } = ',';

    /// <summary>Reads a <c>sep=X</c> first line, as Excel writes, and returns X; otherwise reads nothing.</summary>
    public char? ReadHint()
    {
        Fill(6);
        ReadOnlySpan<char> start = buffer.AsSpan(position, length - position);
        if (start.Length >= 5 && start.StartsWith("sep=", StringComparison.OrdinalIgnoreCase)
            && (start.Length == 5 || start[5] is '\r' or '\n'))
        {
            char separator = start[4];
            position += 5;
            if (Peek() == '\r')
            {
                position++;
            }

            if (Peek() == '\n')
            {
                position++;
            }

            return separator;
        }

        return null;
    }

    /// <summary>Whichever of comma, semicolon and tab the first line holds most of outside quotes; a comma when none.</summary>
    public char DetectSeparator()
    {
        Fill(buffer.Length);
        int commas = 0, semicolons = 0, tabs = 0;
        bool quoted = false;
        for (int i = position; i < length; i++)
        {
            char c = buffer[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted)
            {
                if (c is '\r' or '\n')
                {
                    break;
                }

                commas += c == ',' ? 1 : 0;
                semicolons += c == ';' ? 1 : 0;
                tabs += c == '\t' ? 1 : 0;
            }
        }

        return tabs > commas && tabs >= semicolons ? '\t' : semicolons > commas ? ';' : ',';
    }

    /// <summary>Reads the next row's fields; false at the end of the text.</summary>
    public bool ReadRow(List<string> fields)
    {
        fields.Clear();
        if (Peek() < 0)
        {
            return false;
        }

        while (true)
        {
            field.Clear();
            int c = Peek();
            if (c == '"')
            {
                position++;
                ReadQuoted();
            }

            // An unquoted field, or what follows a closing quote, up to the separator or the line's end.
            while (true)
            {
                c = Peek();
                if (c < 0)
                {
                    fields.Add(field.ToString());
                    return true;
                }

                position++;
                if (c == Separator)
                {
                    fields.Add(field.ToString());
                    break;
                }

                if (c == '\n')
                {
                    fields.Add(field.ToString());
                    return true;
                }

                if (c == '\r')
                {
                    if (Peek() == '\n')
                    {
                        position++;
                    }

                    fields.Add(field.ToString());
                    return true;
                }

                field.Append((char)c);
            }
        }
    }

    private void ReadQuoted()
    {
        while (true)
        {
            if (position >= length && !Fill(1))
            {
                return;
            }

            int end = Array.IndexOf(buffer, '"', position, length - position);
            if (end < 0)
            {
                field.Append(buffer, position, length - position);
                position = length;
                continue;
            }

            field.Append(buffer, position, end - position);
            position = end + 1;
            if (Peek() == '"')
            {
                field.Append('"');
                position++;
                continue;
            }

            return;
        }
    }

    private int Peek() => position < length || Fill(1) ? buffer[position] : -1;

    /// <summary>Makes at least <paramref name="count"/> characters available if the text has them; false at its end.</summary>
    private bool Fill(int count)
    {
        if (length - position >= count)
        {
            return true;
        }

        if (position > 0)
        {
            Array.Copy(buffer, position, buffer, 0, length - position);
            length -= position;
            position = 0;
        }

        while (length < count)
        {
            int read = reader.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return length > position;
    }
}

/// <summary>Turns a field into the value Excel would make of it, with the style index of a format that shows it as written.</summary>
internal sealed class TypeDetector
{
    private readonly CultureInfo culture;
    private readonly Workbook book;
    private readonly Dictionary<string, int> styles = new(StringComparer.Ordinal);
    private readonly string[] shortDates;

    public TypeDetector(CultureInfo culture, Workbook book)
    {
        this.culture = culture;
        this.book = book;
        string pattern = culture.Equals(CultureInfo.InvariantCulture) ? "M/d/yyyy" : culture.DateTimeFormat.ShortDatePattern;
        shortDates = [.. new[] { pattern, pattern.Replace("MM", "M", StringComparison.Ordinal).Replace("dd", "d", StringComparison.Ordinal) }.Distinct()];
    }

    public (CellValue Value, int Style)? Detect(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim(' ');
        if (span.Length == 0 || span.Length > 64)
        {
            return null;
        }

        char first = span[0];
        if (first == '#')
        {
            return CellValue.TryParseError(span, out CellError error) ? (CellValue.FromError(error), 0) : null;
        }

        if (span.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return (CellValue.FromBoolean(true), 0);
        }

        if (span.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return (CellValue.FromBoolean(false), 0);
        }

        if (char.IsAsciiDigit(first) || first is '-' or '+' || span.StartsWith(culture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal))
        {
            return Number(span) ?? Date(span);
        }

        return null;
    }

    private (CellValue, int)? Number(ReadOnlySpan<char> span)
    {
        NumberFormatInfo format = culture.NumberFormat;
        bool percent = span.EndsWith(format.PercentSymbol, StringComparison.Ordinal);
        if (percent)
        {
            span = span[..^format.PercentSymbol.Length].TrimEnd(' ');
        }

        ReadOnlySpan<char> digits = span.TrimStart("+-");
        int point = digits.IndexOf(format.NumberDecimalSeparator, StringComparison.Ordinal);
        int exponent = digits.IndexOfAny('e', 'E');
        int integerEnd = point < 0 ? exponent : exponent < 0 ? point : Math.Min(point, exponent);
        ReadOnlySpan<char> integer = integerEnd < 0 ? digits : digits[..integerEnd];

        // A leading zero keeps the field text, as in codes and phone numbers: 007, 0123.
        if (integer.Length > 1 && integer[0] == '0' && char.IsAsciiDigit(integer[1]))
        {
            return null;
        }

        bool grouped = integer.Contains(format.NumberGroupSeparator, StringComparison.Ordinal);
        if (grouped && !GroupedRightly(integer, format.NumberGroupSeparator))
        {
            return null;
        }

        NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent | (grouped ? NumberStyles.AllowThousands : 0);
        if (!double.TryParse(span, styles, culture, out double number) || !double.IsFinite(number))
        {
            return null;
        }

        int decimals = point < 0 ? 0 : digits[(point + format.NumberDecimalSeparator.Length)..].IndexOfAnyExceptInRange('0', '9') is int stop and >= 0 ? stop : digits.Length - point - format.NumberDecimalSeparator.Length;
        string fraction = decimals > 0 ? "." + new string('0', Math.Min(decimals, 30)) : "";
        if (percent)
        {
            return (CellValue.FromNumber(number / 100), Style(decimals > 0 ? "0.00%" : "0%"));
        }

        if (exponent >= 0)
        {
            return (CellValue.FromNumber(number), Style("0.00E+00"));
        }

        return (CellValue.FromNumber(number), grouped ? Style("#,##0" + fraction) : 0);
    }

    private static bool GroupedRightly(ReadOnlySpan<char> integer, string separator)
    {
        int count = 0;
        bool firstGroup = true;
        foreach (Range part in integer.Split(separator))
        {
            ReadOnlySpan<char> group = integer[part];
            if (group.IsEmpty || group.ContainsAnyExceptInRange('0', '9') || (firstGroup ? group.Length > 3 : group.Length != 3))
            {
                return false;
            }

            firstGroup = false;
            count++;
        }

        return count > 1;
    }

    private static readonly string[] IsoDates = ["yyyy-MM-dd"];
    private static readonly string[] IsoDateTimes = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"];
    private static readonly string[] IsoMinutes = ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm"];
    private static readonly string[] Times = ["H:mm:ss", "H:mm"];

    private (CellValue, int)? Date(ReadOnlySpan<char> span)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(span, IsoDates, invariant, DateTimeStyles.None, out DateTime date))
        {
            return Serial(date, "yyyy-mm-dd");
        }

        if (DateTime.TryParseExact(span, IsoDateTimes, invariant, DateTimeStyles.None, out date))
        {
            return Serial(date, "yyyy-mm-dd hh:mm:ss");
        }

        if (DateTime.TryParseExact(span, IsoMinutes, invariant, DateTimeStyles.None, out date))
        {
            return Serial(date, "yyyy-mm-dd hh:mm");
        }

        if (DateTime.TryParseExact(span, shortDates, culture, DateTimeStyles.None, out date))
        {
            return Serial(date, "mm-dd-yy");
        }

        if (DateTime.TryParseExact(span, Times, invariant, DateTimeStyles.NoCurrentDateDefault, out date))
        {
            return (CellValue.FromNumber(date.TimeOfDay.TotalDays), Style(span.Count(':') == 2 ? "h:mm:ss" : "h:mm"));
        }

        return null;
    }

    private (CellValue, int)? Serial(DateTime date, string code) =>
        DateSerial.From(date) is { } serial ? (CellValue.FromNumber(serial), Style(code)) : null;

    private int Style(string code)
    {
        if (!styles.TryGetValue(code, out int index))
        {
            index = book.Styles.Add(CellStyle.Default with { NumberFormat = code });
            styles[code] = index;
        }

        return index;
    }
}

/// <summary>Excel's serial date numbers in the 1900 system.</summary>
internal static class DateSerial
{
    private static readonly DateTime Base = new(1899, 12, 30);
    private static readonly DateTime LeapBug = new(1900, 3, 1);
    private static readonly DateTime First = new(1900, 1, 1);

    /// <summary>The serial of a date and time, counting the 29 February 1900 Excel keeps; null before 1900.</summary>
    public static double? From(DateTime date)
    {
        if (date < First)
        {
            return null;
        }

        double days = (date - Base).TotalDays;
        return date < LeapBug ? days - 1 : days;
    }
}
