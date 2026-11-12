using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// CELL, INFO, SHEET and SHEETS, from Microsoft's function reference: what Excel knows about a cell, the workbook and where
/// it is calculated.
/// </summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> CellInfoFunctions() =>
    [
        new("CELL", 1, 2, Kinds.ValueThenAny, CellInfo),
        new("INFO", 1, 1, Kinds.Value, Info),
        new("SHEET", 0, 1, Kinds.Any, SheetNumber),
        new("SHEETS", 0, 1, Kinds.Any, SheetCount),
    ];

    /// <summary>
    /// CELL: a fact about the first cell of a reference; without one, about the formula's own cell (Excel takes the cell
    /// changed last, which a file does not record).
    /// </summary>
    private static EvalValue CellInfo(EvalValue[] arguments, EvalContext context)
    {
        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        if (arguments[0].Kind != EvalKind.Text)
        {
            return Error(CellError.Value);
        }

        Worksheet sheet = context.Sheet;
        int row = context.Row, column = context.Column;
        if (arguments.Length > 1 && arguments[1].Kind != EvalKind.Missing)
        {
            EvalValue reference = arguments[1];
            if (reference.IsError)
            {
                return reference;
            }

            if (reference.Kind != EvalKind.Reference)
            {
                return Error(CellError.Value);
            }

            SheetArea area = reference.Areas[0];
            (sheet, row, column) = (area.Sheet, area.Range.First.Row, area.Range.First.Column);
        }

        CellValue value = sheet.GetValue(row, column);
        switch (arguments[0].Text.ToUpperInvariant())
        {
            case "ADDRESS":
                {
                    string address = "$" + CellAddress.ColumnName(column) + "$" + (row + 1).ToString(CultureInfo.InvariantCulture);
                    return EvalValue.FromText(sheet == context.Sheet ? address : SheetPrefix(sheet) + address);
                }

            case "COL":
                return Number(column + 1);
            case "ROW":
                return Number(row + 1);
            case "CONTENTS":
                return EvalValue.FromCell(value);
            case "TYPE":
                return EvalValue.FromText(value.Kind switch
                {
                    CellValueKind.Empty => "b",
                    CellValueKind.Text => "l",
                    _ => "v",
                });
            case "FILENAME":
                return EvalValue.FromText(sheet.Workbook.FilePath is { } path
                    ? Path.GetDirectoryName(path) + Path.DirectorySeparatorChar + "[" + Path.GetFileName(path) + "]" + sheet.Name
                    : "");
            case "FORMAT":
                return EvalValue.FromText(FormatCode(sheet[row, column].NumberFormat, context.Culture, out _, out _));
            case "COLOR":
                FormatCode(sheet[row, column].NumberFormat, context.Culture, out bool color, out _);
                return Number(color ? 1 : 0);
            case "PARENTHESES":
                FormatCode(sheet[row, column].NumberFormat, context.Culture, out _, out bool parentheses);
                return Number(parentheses ? 1 : 0);
            case "PREFIX":
                return EvalValue.FromText(value.Kind != CellValueKind.Text ? "" : sheet[row, column].Style.Alignment.Horizontal switch
                {
                    CellHorizontalAlignment.Right => "\"",
                    CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous => "^",
                    CellHorizontalAlignment.Fill => "\\",
                    _ => "'",
                });
            case "PROTECT":
                return Number(sheet[row, column].Style.Protection.Locked ? 1 : 0);
            case "WIDTH":
                {
                    SheetColumn line = sheet.Columns[column];
                    double width = line.Hidden ? 0 : ShownWidth(line.Width ?? sheet.DefaultColumnWidth);
                    return Result(1, 2, [Number(Math.Round(width, MidpointRounding.AwayFromZero)), EvalValue.FromBoolean(line.Width is null && !line.Hidden)]);
                }

            default:
                return Error(CellError.Value);
        }
    }

    /// <summary>
    /// A column's width in characters as Excel shows it, for a file's width (which counts the cell padding) or null for the
    /// default: pixels at the 7-pixel digit of Calibri 11, less 5 pixels of padding.
    /// </summary>
    private static double ShownWidth(double? width)
    {
        const int Digit = 7;
        int pixels = width is { } w
            ? (int)Math.Truncate(((256 * w) + Math.Truncate(128.0 / Digit)) / 256 * Digit)
            : (((8 * Digit) + 5 + 7) / 8) * 8;
        return Math.Max(0, pixels - 5) / (double)Digit;
    }

    /// <summary>The sheet as an address names one on another sheet: the file in brackets when it has one, quoted where needed.</summary>
    private static string SheetPrefix(Worksheet sheet)
    {
        string name = sheet.Workbook.FilePath is { } path ? "[" + Path.GetFileName(path) + "]" + sheet.Name : sheet.Name;
        return SheetNames.IsPlain(name) ? name + "!" : "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'!";
    }

    /// <summary>
    /// CELL's code for a number format, as Excel gives it: G for General, text, fractions and the formats it has no code for;
    /// F, a comma, C, P or S and the decimals; D1 to D9 for the dates and times of Excel's built-in formats; H for a format
    /// that shows nothing. A minus follows when negative numbers have a color, "()" when positive ones are in parentheses.
    /// </summary>
    internal static string FormatCode(string format, CultureInfo culture, out bool color, out bool parentheses)
    {
        List<string> sections = SplitSections(format);
        color = false;
        parentheses = false;
        string first = sections[0];
        if (sections.All(s => s.Length == 0))
        {
            return "H";
        }

        Tokens tokens = Read(first, culture);
        if (sections.Count > 1 && (Read(sections[0], culture).Color || Read(sections[1], culture).Color))
        {
            color = true;
        }

        parentheses = tokens.Parentheses;
        string suffix = (color ? "-" : "") + (parentheses ? "()" : "");
        if (tokens.General || tokens.Text || tokens.Fraction)
        {
            return "G";
        }

        if (tokens.Dates.Length > 0)
        {
            return DateCode(tokens.Dates, tokens.Elapsed, tokens.Slash) is { } date ? date + suffix : "G";
        }

        if (!tokens.Digits)
        {
            return "G";
        }

        int decimals = Math.Min(tokens.Decimals, 15);
        string kind = tokens.Percent ? "P" : tokens.Exponent ? "S" : tokens.Currency ? "C" : tokens.Thousands ? "," : "F";
        return kind + decimals.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>The date and time code for the parts of a format in order: d, m (month), y, h, n (minute), s and a for AM/PM, a letter per part.</summary>
    private static string? DateCode(string parts, bool elapsed, bool slash)
    {
        bool hasDate = parts.IndexOfAny(['d', 'm', 'y']) >= 0;
        bool hasTime = parts.IndexOfAny(['h', 'n', 's']) >= 0;
        if (elapsed)
        {
            return null;
        }

        if (hasDate && hasTime)
        {
            return "D1";
        }

        if (hasTime)
        {
            string clock = parts.Replace("a", "", StringComparison.Ordinal);
            bool ampm = parts.Contains('a', StringComparison.Ordinal);
            return clock switch
            {
                "hn" => ampm ? "D7" : "D9",
                "hns" => ampm ? "D6" : "D8",
                _ => null,
            };
        }

        string order = parts.Replace("D", "d", StringComparison.Ordinal);
        return order switch
        {
            "dMy" or "dmy" => "D1",
            "mdy" => slash ? "D4" : "D1",
            "dM" or "Mdy" or "dm" => "D2",
            "My" => "D3",
            "md" or "Md" => "D5",
            _ => null,
        };
    }

    /// <summary>What CELL looks at in a format section.</summary>
    private sealed class Tokens
    {
        public bool General;
        public bool Text;
        public bool Fraction;
        public bool Percent;
        public bool Exponent;
        public bool Currency;
        public bool Thousands;
        public bool Digits;
        public bool Color;
        public bool Parentheses;
        public bool Elapsed;

        /// <summary>Whether a slash separates the parts of a date.</summary>
        public bool Slash;
        public int Decimals;

        /// <summary>The date and time parts in order: d, m (a numeric month), M (a month by name), y, h, n (minutes), s, a; D for a weekday.</summary>
        public string Dates = "";
    }

    private static List<string> SplitSections(string format)
    {
        List<string> sections = [];
        StringBuilder current = new();
        bool quoted = false;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\\' or '_' or '*' && i + 1 < format.Length)
            {
                current.Append(c).Append(format[++i]);
                continue;
            }
            else if (!quoted && c == ';')
            {
                sections.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        sections.Add(current.ToString());
        return sections;
    }

    private static Tokens Read(string section, CultureInfo culture)
    {
        Tokens tokens = new();
        StringBuilder parts = new();
        string currency = culture.NumberFormat.CurrencySymbol;
        bool afterPoint = false;
        bool lastWasHour = false;
        for (int i = 0; i < section.Length; i++)
        {
            char c = section[i];
            switch (c)
            {
                case '"':
                    {
                        int close = section.IndexOf('"', i + 1);
                        string literal = close < 0 ? section[(i + 1)..] : section[(i + 1)..close];
                        tokens.Currency |= literal.Trim() == currency && currency.Length > 0;
                        tokens.Parentheses |= literal.Contains('(', StringComparison.Ordinal);
                        i = close < 0 ? section.Length : close;
                        continue;
                    }

                case '\\':
                    if (i + 1 < section.Length)
                    {
                        tokens.Parentheses |= section[i + 1] == '(';
                        i++;
                    }

                    continue;
                case '_' or '*':
                    i++;
                    continue;
                case '[':
                    {
                        int close = section.IndexOf(']', i + 1);
                        string inside = close < 0 ? section[(i + 1)..] : section[(i + 1)..close];
                        i = close < 0 ? section.Length : close;
                        if (inside.Length > 0 && char.ToLowerInvariant(inside[0]) is 'h' or 'm' or 's' && inside.All(ch => char.ToLowerInvariant(ch) == char.ToLowerInvariant(inside[0])))
                        {
                            tokens.Elapsed = true;
                            parts.Append(char.ToLowerInvariant(inside[0]) == 'm' ? 'n' : char.ToLowerInvariant(inside[0]));
                        }
                        else if (!inside.StartsWith('$') && !inside.StartsWith('<') && !inside.StartsWith('>') && !inside.StartsWith('='))
                        {
                            tokens.Color = true;
                        }

                        continue;
                    }

                case '(':
                    tokens.Parentheses = true;
                    continue;
                case '@':
                    tokens.Text = true;
                    continue;
                case '%':
                    tokens.Percent = true;
                    continue;
                case '/' when !tokens.Digits && !section.Contains('?', StringComparison.Ordinal):
                    tokens.Slash = true;
                    continue;
                case '/' when tokens.Digits || section.Contains('?', StringComparison.Ordinal):
                    tokens.Fraction |= section.Contains('?', StringComparison.Ordinal) || char.IsAsciiDigit(At(section, i + 1)) || At(section, i + 1) == '#';
                    continue;
                case '.':
                    afterPoint = true;
                    continue;
                case ',':
                    tokens.Thousands |= tokens.Digits && !afterPoint;
                    continue;
                case '0' or '#' or '?':
                    tokens.Digits = true;
                    tokens.Decimals += afterPoint && !tokens.Exponent ? 1 : 0;
                    continue;
                case 'E' or 'e' when At(section, i + 1) is '+' or '-':
                    tokens.Exponent = true;
                    i++;
                    continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (lower == 'g' && section.AsSpan(i).StartsWith("general", StringComparison.OrdinalIgnoreCase))
            {
                tokens.General = true;
                i += 6;
                continue;
            }

            if (section.AsSpan(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase) || section.AsSpan(i).StartsWith("A/P", StringComparison.OrdinalIgnoreCase))
            {
                parts.Append('a');
                i += section.AsSpan(i).StartsWith("A/P", StringComparison.OrdinalIgnoreCase) ? 2 : 4;
                continue;
            }

            if (lower is 'd' or 'm' or 'y' or 'h' or 's')
            {
                int run = 1;
                while (i + run < section.Length && char.ToLowerInvariant(section[i + run]) == lower)
                {
                    run++;
                }

                char part = lower switch
                {
                    'd' => run >= 3 ? 'D' : 'd',
                    'm' when lastWasHour || NextIsSeconds(section, i + run) => 'n',
                    'm' => run >= 3 ? 'M' : 'm',
                    'y' => 'y',
                    'h' => 'h',
                    _ => 's',
                };
                lastWasHour = part == 'h';
                parts.Append(part);
                i += run - 1;
                continue;
            }

            if (c.ToString() == currency || (currency.Length > 1 && section.AsSpan(i).StartsWith(currency, StringComparison.Ordinal)))
            {
                tokens.Currency = true;
                i += currency.Length - 1;
            }
        }

        // A weekday alone, or with other parts, has no code.
        tokens.Dates = parts.ToString().Contains('D', StringComparison.Ordinal) ? "D" : parts.ToString();
        if (tokens.Dates.Length > 0 && tokens.Dates.All(p => p == 'D'))
        {
            tokens.Dates = "D?";
        }

        return tokens;
    }

    private static char At(string text, int index) => index < text.Length ? text[index] : '\0';

    private static bool NextIsSeconds(string section, int from)
    {
        for (int i = from; i < section.Length; i++)
        {
            char c = char.ToLowerInvariant(section[i]);
            if (c == 's')
            {
                return true;
            }

            if (char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// INFO: what Excel says about where it calculates. The memory types Excel no longer has give #N/A; the directory is the
    /// workbook's, or the process's current one.
    /// </summary>
    private static EvalValue Info(EvalValue[] arguments, EvalContext context)
    {
        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        if (arguments[0].Kind != EvalKind.Text)
        {
            return Error(CellError.Value);
        }

        Workbook book = context.Book;
        switch (arguments[0].Text.ToUpperInvariant())
        {
            case "NUMFILE":
                return Number(book.Worksheets.Count + book.ChartSheets.Count);
            case "RECALC":
                return EvalValue.FromText(book.Calculation.Mode == CalculationMode.Manual ? "Manual" : "Automatic");
            case "RELEASE":
                return EvalValue.FromText("16.0");
            case "SYSTEM":
                return EvalValue.FromText(OperatingSystem.IsMacOS() ? "mac" : "pcdos");
            case "OSVERSION":
                return EvalValue.FromText(OperatingSystem.IsWindows()
                    ? string.Create(CultureInfo.InvariantCulture, $"Windows ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit) NT {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor:00}")
                    : RuntimeInformation.OSDescription);
            case "ORIGIN":
                return EvalValue.FromText("$A:$A$1");
            case "DIRECTORY":
                {
                    string folder = book.FilePath is { } path ? Path.GetDirectoryName(path) ?? "" : Environment.CurrentDirectory;
                    return EvalValue.FromText(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar);
                }

            case "MEMAVAIL" or "MEMUSED" or "TOTMEM":
                return Error(CellError.NotAvailable);
            default:
                return Error(CellError.Value);
        }
    }

    /// <summary>SHEET: the number of the formula's sheet, of a reference's sheet, or of the sheet a text names; #N/A for anything else.</summary>
    private static EvalValue SheetNumber(EvalValue[] arguments, EvalContext context)
    {
        if (arguments.Length == 0 || arguments[0].Kind == EvalKind.Missing)
        {
            return Number(context.Book.Worksheets.IndexOf(context.Sheet) + 1);
        }

        EvalValue value = arguments[0];
        switch (value.Kind)
        {
            case EvalKind.Error:
                return value;
            case EvalKind.Reference:
                return Number(context.Book.Worksheets.IndexOf(value.Areas[0].Sheet) + 1);
            case EvalKind.Text:
                return context.Book.Worksheets.TryGet(value.Text, out Worksheet? sheet)
                    ? Number(context.Book.Worksheets.IndexOf(sheet) + 1)
                    : Error(CellError.NotAvailable);
            default:
                return Error(CellError.NotAvailable);
        }
    }

    /// <summary>SHEETS: the sheets of the workbook, or the sheets a reference spans.</summary>
    private static EvalValue SheetCount(EvalValue[] arguments, EvalContext context)
    {
        if (arguments.Length == 0 || arguments[0].Kind == EvalKind.Missing)
        {
            return Number(context.Book.Worksheets.Count + context.Book.ChartSheets.Count);
        }

        EvalValue value = arguments[0];
        return value.Kind switch
        {
            EvalKind.Error => value,
            EvalKind.Reference => Number(value.Areas.Select(a => a.Sheet).Distinct().Count()),
            _ => Error(CellError.NotAvailable),
        };
    }
}
