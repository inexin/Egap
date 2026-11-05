using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets;

/// <summary>
/// An Excel number format code, parsed: up to four sections (positive, negative, zero, text) with colours, conditions,
/// digit placeholders, thousands separators and scaling, percent, scientific notation, fractions, dates and times, elapsed
/// times, text and literal characters, and locale tags such as <c>[$-414]</c>. <see cref="Format"/> shows a value as Excel
/// shows it in a cell, for a given culture; <see cref="Cell.Text"/> uses it.
/// </summary>
/// <example>
/// <code>
/// NumberFormatCode code = NumberFormatCode.Parse("#,##0.00;[Red]-#,##0.00");
/// string text = code.Format(-1234.567);                 // "-1,234.57"
/// </code>
/// </example>
public sealed class NumberFormatCode
{
    private static readonly ConcurrentDictionary<string, NumberFormatCode> Cache = new(StringComparer.Ordinal);

    private readonly FormatSection[] sections;

    private NumberFormatCode(string code, FormatSection[] sections)
    {
        Code = code;
        this.sections = sections;
    }

    /// <summary>The format code as given.</summary>
    public string Code { get; }

    /// <summary>Whether the code shows numbers as dates or times: its first section has date or time parts.</summary>
    public bool IsDateTime => sections[0].IsDate;

    /// <summary>The <c>General</c> format, which shows numbers in up to 11 characters and text as it is.</summary>
    public static NumberFormatCode General { get; } = Parse("General");

    /// <summary>
    /// Parses a format code. Parsing never fails: a part Excel would refuse is shown as literal text, as a reader of
    /// someone else's file wants.
    /// </summary>
    /// <exception cref="ArgumentNullException">The code is null.</exception>
    public static NumberFormatCode Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new NumberFormatCode(code, FormatParser.Parse(code.Length == 0 ? "General" : code));
    }

    /// <summary>The parsed code, kept once per code for the cells that share it.</summary>
    internal static NumberFormatCode Get(string code)
    {
        if (Cache.TryGetValue(code, out NumberFormatCode? found))
        {
            return found;
        }

        if (Cache.Count > 4096)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(code, Parse);
    }

    /// <summary>
    /// The value as Excel shows it with this format: numbers through the section that applies, text through the text section,
    /// TRUE and FALSE, and errors as their text. Separators, month and day names and AM and PM come from
    /// <paramref name="culture"/> (the invariant culture, which matches Excel in English, when null), or from the locale tag a
    /// section gives. A date before the start of the date system shows as <c>#</c> signs, as Excel shows it.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="culture">The culture to show the value in, or null for the invariant culture.</param>
    /// <param name="date1904">Whether dates count from 1904-01-01 rather than 1900-01-01.</param>
    public string Format(CellValue value, CultureInfo? culture = null, bool date1904 = false)
    {
        string text = FormatWithFill(value, culture, date1904);
        int mark = text.IndexOf(FillMark, StringComparison.Ordinal);
        if (mark < 0)
        {
            return text;
        }

        StringBuilder plain = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == FillMark)
            {
                i++;
            }
            else
            {
                plain.Append(text[i]);
            }
        }

        return plain.ToString();
    }

    /// <summary>
    /// Marks where a <c>*</c> fill goes in <see cref="FormatWithFill"/>'s text: the mark, then the character that repeats to
    /// fill the cell's width.
    /// </summary>
    internal const char FillMark = '\u0001';

    /// <summary>The value as <see cref="Format"/> shows it, with each <c>*</c> fill left in as <see cref="FillMark"/> and its character.</summary>
    internal string FormatWithFill(CellValue value, CultureInfo? culture, bool date1904)
    {
        culture ??= CultureInfo.InvariantCulture;
        switch (value.Kind)
        {
            case CellValueKind.Empty:
                return "";
            case CellValueKind.Boolean:
                return value.Boolean ? "TRUE" : "FALSE";
            case CellValueKind.Error:
                return CellValue.ErrorText(value.Error);
            case CellValueKind.Text:
                return FormatText(value.Text, culture);
            default:
                // The culture's short date stands in for built-in 14 and 22; formatting it does not look for a stand-in again,
                // so a culture whose short date is the same code cannot loop.
                NumberFormatCode shown = SystemDate(culture) is { } local ? Get(local) : this;
                return shown.FormatNumber(value.Number, culture, date1904);
        }
    }

    private string FormatNumber(double value, CultureInfo culture, bool date1904)
    {
        (FormatSection? section, bool keepSign) = Choose(value);
        if (section is null)
        {
            return new string('#', 10);
        }

        double number = keepSign ? value : Math.Abs(value);
        return NumberFormatter.Format(section, number, section.Culture ?? culture, date1904);
    }

    /// <summary>
    /// The culture's own code for the two built-in formats that follow the system's short date, as Excel's do: <c>mm-dd-yy</c>
    /// (built-in 14) and <c>m/d/yy h:mm</c> (built-in 22). The invariant culture gives Excel's English <c>m/d/yyyy</c>.
    /// </summary>
    private string? SystemDate(CultureInfo culture)
    {
        bool withTime = Code == "m/d/yy h:mm";
        if (Code != "mm-dd-yy" && !withTime)
        {
            return null;
        }

        string date = culture.Equals(CultureInfo.InvariantCulture) ? "m/d/yyyy" : DateCode.FromDotNet(culture.DateTimeFormat.ShortDatePattern);
        return withTime ? date + " h:mm" : date;
    }

    /// <summary>The colour the section that shows <paramref name="value"/> gives, such as red for <c>[Red]</c>, or null for none.</summary>
    public CellColor? GetColor(CellValue value)
    {
        FormatSection? section = value.Kind switch
        {
            CellValueKind.Number => Choose(value.Number).Section,
            CellValueKind.Text => TextSection(),
            _ => null,
        };
        return section?.Color;
    }

    /// <summary>The code.</summary>
    public override string ToString() => Code;

    private string FormatText(string text, CultureInfo culture)
    {
        if (TextSection() is not { } section)
        {
            return text;
        }

        StringBuilder output = new();
        foreach (FormatToken token in section.Tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Text:
                    output.Append(text);
                    break;
                case TokenKind.Literal:
                    output.Append(token.Text);
                    break;
                case TokenKind.Space:
                    output.Append(' ');
                    break;
                case TokenKind.Fill:
                    output.Append(FillMark).Append(token.Text.Length > 0 ? token.Text[0] : ' ');
                    break;
                case TokenKind.General:
                    output.Append(text);
                    break;
            }
        }

        _ = culture;
        return output.ToString();
    }

    /// <summary>The section text shows through: the fourth, or a lone section with an <c>@</c>; null to show text as it is.</summary>
    private FormatSection? TextSection()
    {
        if (sections.Length >= 4)
        {
            return sections[3];
        }

        return sections.Length == 1 && sections[0].HasText ? sections[0] : null;
    }

    /// <summary>The section a number shows through, and whether its own sign shows; null when no section applies.</summary>
    private (FormatSection? Section, bool KeepSign) Choose(double number)
    {
        FormatSection[] numeric = sections.Length >= 4 ? sections[..3] : sections;
        if (numeric.Any(s => s.Condition is not null))
        {
            for (int i = 0; i < numeric.Length; i++)
            {
                FormatSection section = numeric[i];
                if (section.Condition is { } condition)
                {
                    if (condition.Matches(number))
                    {
                        return (section, !condition.IsNegativeTest);
                    }
                }
                else
                {
                    // The first section without a condition takes what the conditions before it did not; Excel shows the
                    // second section's numbers without their sign when the first section tests for positive numbers only.
                    bool negativeDefault = i == 1 && numeric[0].Condition is { IsPositiveTest: true } && number < 0;
                    return (section, !negativeDefault);
                }
            }

            return (null, true);
        }

        return numeric.Length switch
        {
            1 => (numeric[0], true),
            2 => number < 0 ? (numeric[1], false) : (numeric[0], true),
            _ => number < 0 ? (numeric[1], false) : number == 0 ? (numeric[2], true) : (numeric[0], true),
        };
    }
}

/// <summary>The kinds of part a format section is made of.</summary>
internal enum TokenKind
{
    Literal,
    Space,
    Fill,
    Digit,
    DecimalPoint,
    Comma,
    Percent,
    Exponent,
    Slash,
    Text,
    General,
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
    SubSecond,
    AmPm,
    ElapsedHours,
    ElapsedMinutes,
    ElapsedSeconds,

    /// <summary>An <c>m</c> run whose meaning, month or minute, the parser settles once it has the whole section.</summary>
    MonthOrMinute,
}

/// <summary>One part of a section: its kind, its text for literals and AM/PM, its placeholder character or its run length.</summary>
internal sealed class FormatToken(TokenKind kind, string text = "", int count = 0, char placeholder = '\0')
{
    public TokenKind Kind { get; set; } = kind;

    public string Text { get; } = text;

    /// <summary>The length of a date or time run (<c>yyyy</c> is 4), or the digits of fractional seconds.</summary>
    public int Count { get; } = count;

    /// <summary>The placeholder: <c>0</c>, <c>#</c> or <c>?</c>; for an exponent, <c>+</c> or <c>-</c>.</summary>
    public char Placeholder { get; } = placeholder;

    /// <summary>For a comma: whether it groups thousands (true) or scales by a thousand (false).</summary>
    public bool Groups { get; set; }
}

/// <summary>A condition such as <c>[&gt;=100]</c>.</summary>
internal sealed record FormatCondition(string Operator, double Value)
{
    public bool IsNegativeTest => Operator is "<" or "<=" && Value <= 0;

    public bool IsPositiveTest => Operator is ">" or ">=" && Value >= 0;

    public bool Matches(double number) => Operator switch
    {
        "<" => number < Value,
        "<=" => number <= Value,
        ">" => number > Value,
        ">=" => number >= Value,
        "=" => number == Value,
        "<>" => number != Value,
        _ => false,
    };
}

/// <summary>One section of a code, with what the formatter needs to know about it worked out once.</summary>
internal sealed class FormatSection
{
    public List<FormatToken> Tokens { get; } = [];

    public CellColor? Color { get; set; }

    public FormatCondition? Condition { get; set; }

    /// <summary>The culture a locale tag such as <c>[$-414]</c> names, or null.</summary>
    public CultureInfo? Culture { get; set; }

    public bool IsDate { get; set; }

    public bool HasText { get; set; }

    public bool HasDigits { get; set; }

    public bool IsGeneral { get; set; }

    /// <summary>Whether hours show on a 12-hour clock: the section has AM/PM.</summary>
    public bool TwelveHour { get; set; }

    /// <summary>How many times the number is multiplied by 100: one per <c>%</c>.</summary>
    public int Percents { get; set; }

    /// <summary>How many times the number is divided by 1,000: one per comma after the last integer digit.</summary>
    public int Scale { get; set; }

    public bool Grouping { get; set; }

    /// <summary>The index of the exponent token, or -1.</summary>
    public int Exponent { get; set; } = -1;

    /// <summary>The index of the fraction's slash, or -1.</summary>
    public int Slash { get; set; } = -1;

    /// <summary>The index of the decimal point, or -1.</summary>
    public int DecimalPoint { get; set; } = -1;

    /// <summary>The digits of fractional seconds shown, 0 for none.</summary>
    public int SubSecondDigits { get; set; }
}

/// <summary>Splits a code into sections and each section into tokens (ECMA-376 Part 1, 18.8.31, and Excel's number format reference).</summary>
internal static class FormatParser
{
    private static readonly Dictionary<string, CellColor> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Black"] = CellColor.FromRgb(0, 0, 0),
        ["Blue"] = CellColor.FromRgb(0, 0, 255),
        ["Cyan"] = CellColor.FromRgb(0, 255, 255),
        ["Green"] = CellColor.FromRgb(0, 255, 0),
        ["Magenta"] = CellColor.FromRgb(255, 0, 255),
        ["Red"] = CellColor.FromRgb(255, 0, 0),
        ["White"] = CellColor.FromRgb(255, 255, 255),
        ["Yellow"] = CellColor.FromRgb(255, 255, 0),
    };

    public static FormatSection[] Parse(string code)
    {
        List<FormatSection> sections = [];
        FormatSection section = new();
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            switch (c)
            {
                case ';':
                    sections.Add(Finish(section));
                    section = new FormatSection();
                    i++;
                    continue;
                case '"':
                    int close = code.IndexOf('"', i + 1);
                    if (close < 0)
                    {
                        close = code.Length;
                    }

                    Literal(section, code[(i + 1)..close]);
                    i = close + 1;
                    continue;
                case '\\':
                    if (i + 1 < code.Length)
                    {
                        Literal(section, code[i + 1].ToString());
                    }

                    i += 2;
                    continue;
                case '_':
                    section.Tokens.Add(new FormatToken(TokenKind.Space));
                    i += 2;
                    continue;
                case '*':
                    section.Tokens.Add(new FormatToken(TokenKind.Fill, i + 1 < code.Length ? code[i + 1].ToString() : ""));
                    i += 2;
                    continue;
                case '[':
                    int end = code.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        Literal(section, code[i..]);
                        i = code.Length;
                        continue;
                    }

                    Bracket(section, code[(i + 1)..end]);
                    i = end + 1;
                    continue;
                case '0' or '#' or '?':
                    section.Tokens.Add(new FormatToken(TokenKind.Digit, placeholder: c));
                    i++;
                    continue;
                case '.':
                    section.Tokens.Add(new FormatToken(TokenKind.DecimalPoint));
                    i++;
                    continue;
                case ',':
                    section.Tokens.Add(new FormatToken(TokenKind.Comma));
                    i++;
                    continue;
                case '%':
                    section.Tokens.Add(new FormatToken(TokenKind.Percent));
                    i++;
                    continue;
                case '/':
                    section.Tokens.Add(new FormatToken(TokenKind.Slash));
                    i++;
                    continue;
                case '@':
                    section.Tokens.Add(new FormatToken(TokenKind.Text));
                    i++;
                    continue;
                case 'E' or 'e' when i + 1 < code.Length && code[i + 1] is '+' or '-':
                    section.Tokens.Add(new FormatToken(TokenKind.Exponent, placeholder: code[i + 1]));
                    i += 2;
                    continue;
            }

            if (Matches(code, i, "General"))
            {
                section.Tokens.Add(new FormatToken(TokenKind.General));
                i += 7;
                continue;
            }

            if (Matches(code, i, "AM/PM"))
            {
                section.Tokens.Add(new FormatToken(TokenKind.AmPm, "AM/PM"));
                i += 5;
                continue;
            }

            if (Matches(code, i, "A/P"))
            {
                section.Tokens.Add(new FormatToken(TokenKind.AmPm, code.Substring(i, 3)));
                i += 3;
                continue;
            }

            TokenKind? run = c == 'e' ? TokenKind.Year : char.ToLowerInvariant(c) switch
            {
                'y' => TokenKind.Year,
                'm' => TokenKind.MonthOrMinute,
                'd' => TokenKind.Day,
                'h' => TokenKind.Hour,
                's' => TokenKind.Second,
                _ => null,
            };
            if (run is { } kind)
            {
                int length = 1;
                while (i + length < code.Length && char.ToLowerInvariant(code[i + length]) == char.ToLowerInvariant(c))
                {
                    length++;
                }

                section.Tokens.Add(new FormatToken(kind, count: length));
                i += length;
                continue;
            }

            Literal(section, c.ToString());
            i++;
        }

        sections.Add(Finish(section));
        return [.. sections];
    }

    private static bool Matches(string code, int at, string word) =>
        at + word.Length <= code.Length && string.Compare(code, at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static void Literal(FormatSection section, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (section.Tokens.Count > 0 && section.Tokens[^1] is { Kind: TokenKind.Literal } last)
        {
            section.Tokens[^1] = new FormatToken(TokenKind.Literal, last.Text + text);
        }
        else
        {
            section.Tokens.Add(new FormatToken(TokenKind.Literal, text));
        }
    }

    private static void Bracket(FormatSection section, string content)
    {
        if (Colors.TryGetValue(content, out CellColor color))
        {
            section.Color = color;
            return;
        }

        if (content.StartsWith("Color", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(content.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index is >= 1 and <= 56)
        {
            section.Color = CellColor.FromIndex(index + 7);
            return;
        }

        if (content.Length > 0 && content[0] is '<' or '>' or '=')
        {
            int length = content.Length > 1 && content[1] is '=' or '>' ? 2 : 1;
            if (double.TryParse(content.AsSpan(length), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                section.Condition = new FormatCondition(content[..length], value);
            }

            return;
        }

        if (content.Length > 0 && content[0] == '$')
        {
            int dash = content.IndexOf('-', 1);
            string symbol = dash < 0 ? content[1..] : content[1..dash];
            Literal(section, symbol);
            if (dash >= 0 && int.TryParse(content.AsSpan(dash + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int locale))
            {
                section.Culture = CultureOf(locale);
                if ((locale & 0xFFFF) == 0xF800)
                {
                    section.Tokens.Add(new FormatToken(TokenKind.Literal, "\u0001LongDate"));
                }
                else if ((locale & 0xFFFF) == 0xF400)
                {
                    section.Tokens.Add(new FormatToken(TokenKind.Literal, "\u0001LongTime"));
                }
            }

            return;
        }

        string lower = content.ToLowerInvariant();
        if (lower.Length > 0 && lower.All(ch => ch == lower[0]) && lower[0] is 'h' or 'm' or 's')
        {
            TokenKind kind = lower[0] switch
            {
                'h' => TokenKind.ElapsedHours,
                'm' => TokenKind.ElapsedMinutes,
                _ => TokenKind.ElapsedSeconds,
            };
            section.Tokens.Add(new FormatToken(kind, count: lower.Length));
        }

        // Other tags (DBNum, calendar and the like) change nothing this formatter shows.
    }

    private static CultureInfo? CultureOf(int locale)
    {
        // Values below 0x401 are not locales: Excel writes [$€-1] and [$€-2] for where the euro sign goes.
        int lcid = locale & 0xFFFF;
        if (lcid < 0x401 || lcid is 0xF800 or 0xF400)
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(lcid);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Works out what the section is: date or number, which commas group or scale, where its parts are.</summary>
    private static FormatSection Finish(FormatSection section)
    {
        List<FormatToken> tokens = section.Tokens;
        if (tokens.Any(t => t.Kind == TokenKind.Literal && t.Text.StartsWith('\u0001')))
        {
            ExpandSystemFormat(section);
        }

        section.IsDate = tokens.Any(t => t.Kind is TokenKind.Year or TokenKind.MonthOrMinute or TokenKind.Day or TokenKind.Hour or TokenKind.Second
            or TokenKind.AmPm or TokenKind.ElapsedHours or TokenKind.ElapsedMinutes or TokenKind.ElapsedSeconds);
        section.HasText = tokens.Any(t => t.Kind == TokenKind.Text);
        section.IsGeneral = tokens.Any(t => t.Kind == TokenKind.General);
        if (section.IsDate)
        {
            FinishDate(section);
            return section;
        }

        section.HasDigits = tokens.Any(t => t.Kind == TokenKind.Digit);
        section.Percents = tokens.Count(t => t.Kind == TokenKind.Percent);
        section.Exponent = tokens.FindIndex(t => t.Kind == TokenKind.Exponent);
        int slash = tokens.FindIndex(t => t.Kind == TokenKind.Slash);
        if (slash > 0 && section.Exponent < 0 && tokens.Take(slash).Any(t => t.Kind == TokenKind.Digit)
            && tokens.Skip(slash + 1).Any(t => t.Kind == TokenKind.Digit || (t.Kind == TokenKind.Literal && t.Text.Length > 0 && char.IsAsciiDigit(t.Text[0]))))
        {
            section.Slash = slash;
        }
        else if (slash >= 0)
        {
            tokens[slash] = new FormatToken(TokenKind.Literal, "/");
        }

        section.DecimalPoint = section.Slash < 0 ? tokens.FindIndex(t => t.Kind == TokenKind.DecimalPoint) : -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.DecimalPoint && i != section.DecimalPoint)
            {
                tokens[i] = new FormatToken(TokenKind.Literal, ".");
            }
        }

        // A comma with integer digits on both sides groups thousands; a comma after the digits it follows scales by 1,000.
        int integerEnd = section.DecimalPoint >= 0 ? section.DecimalPoint : section.Exponent >= 0 ? section.Exponent
            : section.Slash >= 0 ? section.Slash : tokens.Count;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Comma)
            {
                continue;
            }

            bool digitBefore = tokens.Take(i).Any(t => t.Kind == TokenKind.Digit);
            bool digitAfter = i < integerEnd
                ? tokens.Skip(i + 1).Take(integerEnd - i - 1).Any(t => t.Kind == TokenKind.Digit)
                : tokens.Skip(i + 1).Any(t => t.Kind == TokenKind.Digit);
            if (digitBefore && digitAfter && i < integerEnd)
            {
                tokens[i].Groups = true;
                section.Grouping = true;
            }
            else if (digitBefore && !digitAfter && section.Slash < 0 && section.Exponent < 0)
            {
                section.Scale++;
                tokens[i] = new FormatToken(TokenKind.Literal, "");
            }
            else
            {
                tokens[i] = new FormatToken(TokenKind.Literal, ",");
            }
        }

        return section;
    }

    private static void FinishDate(FormatSection section)
    {
        List<FormatToken> tokens = section.Tokens;
        section.TwelveHour = tokens.Any(t => t.Kind == TokenKind.AmPm);
        for (int i = 0; i < tokens.Count; i++)
        {
            FormatToken token = tokens[i];
            switch (token.Kind)
            {
                case TokenKind.MonthOrMinute:
                    // m is minutes right after hours or right before seconds, and months anywhere else.
                    TokenKind? before = Neighbour(tokens, i, -1);
                    TokenKind? after = Neighbour(tokens, i, 1);
                    bool minutes = token.Count <= 2 && (before is TokenKind.Hour or TokenKind.ElapsedHours || after is TokenKind.Second or TokenKind.ElapsedSeconds);
                    token.Kind = minutes ? TokenKind.Minute : TokenKind.Month;
                    break;
                case TokenKind.DecimalPoint:
                    int digits = 0;
                    while (i + 1 + digits < tokens.Count && tokens[i + 1 + digits] is { Kind: TokenKind.Digit, Placeholder: '0' })
                    {
                        digits++;
                    }

                    if (digits > 0 && Neighbour(tokens, i, -1) is TokenKind.Second or TokenKind.ElapsedSeconds)
                    {
                        tokens[i] = new FormatToken(TokenKind.SubSecond, count: Math.Min(digits, 3));
                        tokens.RemoveRange(i + 1, digits);
                        section.SubSecondDigits = Math.Min(digits, 3);
                    }
                    else
                    {
                        tokens[i] = new FormatToken(TokenKind.Literal, ".");
                    }

                    break;
                case TokenKind.Slash:
                    tokens[i] = new FormatToken(TokenKind.Literal, "/");
                    break;
                case TokenKind.Comma:
                    tokens[i] = new FormatToken(TokenKind.Literal, ",");
                    break;
                case TokenKind.Percent:
                    tokens[i] = new FormatToken(TokenKind.Literal, "%");
                    break;
                case TokenKind.Digit:
                    tokens[i] = new FormatToken(TokenKind.Literal, token.Placeholder == '0' ? "0" : "");
                    break;
            }
        }
    }

    /// <summary>The kind of the nearest date or time token before or after <paramref name="at"/>, past literals.</summary>
    private static TokenKind? Neighbour(List<FormatToken> tokens, int at, int step)
    {
        for (int i = at + step; i >= 0 && i < tokens.Count; i += step)
        {
            if (tokens[i].Kind is not (TokenKind.Literal or TokenKind.Space or TokenKind.Fill or TokenKind.DecimalPoint or TokenKind.Digit or TokenKind.Comma or TokenKind.Slash))
            {
                return tokens[i].Kind;
            }
        }

        return null;
    }

    /// <summary>A system long date or long time tag (<c>[$-F800]</c>, <c>[$-F400]</c>): the culture's own pattern takes the section's place.</summary>
    private static void ExpandSystemFormat(FormatSection section)
    {
        bool date = section.Tokens.Any(t => t.Text == "\u0001LongDate");
        CultureInfo culture = section.Culture ?? CultureInfo.InvariantCulture;
        string pattern = date ? culture.DateTimeFormat.LongDatePattern : culture.DateTimeFormat.LongTimePattern;
        FormatSection expanded = Parse(DateCode.FromDotNet(pattern))[0];
        section.Tokens.Clear();
        section.Tokens.AddRange(expanded.Tokens);
    }
}

/// <summary>Turns .NET date patterns into Excel date codes, for the formats that follow the culture's own date.</summary>
internal static class DateCode
{
    public static string FromDotNet(string pattern)
    {
        StringBuilder code = new();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            int run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c)
            {
                run++;
            }

            switch (c)
            {
                case 'd':
                    code.Append('d', run);
                    break;
                case 'M':
                    code.Append('m', run);
                    break;
                case 'y':
                    code.Append(run <= 2 ? "yy" : "yyyy");
                    break;
                case 'h' or 'H':
                    code.Append('h', Math.Min(run, 2));
                    break;
                case 'm':
                    code.Append('m', Math.Min(run, 2));
                    break;
                case 's':
                    code.Append('s', Math.Min(run, 2));
                    break;
                case 't':
                    code.Append("AM/PM");
                    break;
                case 'f' or 'F':
                    code.Append('0', Math.Min(run, 3));
                    break;
                case '\'' or '"':
                    int close = pattern.IndexOf(c, i + 1);
                    if (close < 0)
                    {
                        close = pattern.Length;
                    }

                    code.Append('"').Append(pattern, i + 1, close - i - 1).Append('"');
                    i = close;
                    continue;
                case '\\':
                    if (i + 1 < pattern.Length)
                    {
                        code.Append('\\').Append(pattern[i + 1]);
                    }

                    i++;
                    continue;
                case 'g' or 'z' or 'K':
                    break;
                default:
                    code.Append(char.IsLetterOrDigit(c) ? "\\" + c : c.ToString());
                    break;
            }

            i += run - 1;
        }

        return code.ToString();
    }
}
