using System.Globalization;
using System.Text;

namespace Egap.Documents.NumberFormats;

/// <summary>A colour a format section names: red, green and blue, or an index into the palette for <c>[Color n]</c>.</summary>
internal readonly record struct FormatColor(byte Red, byte Green, byte Blue, int? Index = null);

/// <summary>Picks the section of a format code that shows a number.</summary>
internal static class FormatSections
{
    /// <summary>The character before a character that fills a cell's width (<c>*x</c>).</summary>
    internal const char FillMark = '';

    /// <summary>The section a number shows through, and whether its own sign shows; null when no section applies.</summary>
    public static (FormatSection? Section, bool KeepSign) Choose(FormatSection[] sections, double number)
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

    public FormatColor? Color { get; set; }

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
    private static readonly Dictionary<string, FormatColor> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Black"] = new FormatColor(0, 0, 0),
        ["Blue"] = new FormatColor(0, 0, 255),
        ["Cyan"] = new FormatColor(0, 255, 255),
        ["Green"] = new FormatColor(0, 255, 0),
        ["Magenta"] = new FormatColor(255, 0, 255),
        ["Red"] = new FormatColor(255, 0, 0),
        ["White"] = new FormatColor(255, 255, 255),
        ["Yellow"] = new FormatColor(255, 255, 0),
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
        if (Colors.TryGetValue(content, out FormatColor color))
        {
            section.Color = color;
            return;
        }

        if (content.StartsWith("Color", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(content.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index is >= 1 and <= 56)
        {
            section.Color = new FormatColor(0, 0, 0, index + 7);
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
