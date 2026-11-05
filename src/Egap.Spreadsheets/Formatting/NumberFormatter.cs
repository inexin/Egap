using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets;

/// <summary>Shows a number through one parsed section: as a date or time, a fraction, scientific notation or a plain number.</summary>
internal static class NumberFormatter
{
    /// <summary>The last day a date can show, 9999-12-31, in the 1900 and the 1904 date systems.</summary>
    private const long LastDay1900 = 2958465;
    private const long LastDay1904 = LastDay1900 - 1462;

    public static string Format(FormatSection section, double number, CultureInfo culture, bool date1904)
    {
        if (section.IsDate)
        {
            // Before or past the calendar a date shows as # signs, as in Excel; FormatDate checks the day once rounded.
            return number < 0 || number >= LastDay1900 + 2 ? new string('#', 10) : FormatDate(section, number, culture, date1904);
        }

        if (section.HasText)
        {
            // A text section, such as "@" (built-in 49), shows a number as General, without the literals around the @, as Excel
            // does: @ formats text, and a number is not text.
            return General(number, culture);
        }

        if (section.IsGeneral && !section.HasDigits)
        {
            StringBuilder general = new();
            foreach (FormatToken token in section.Tokens)
            {
                AppendLiteral(general, token, () => General(number, culture));
            }

            return general.ToString();
        }

        if (!section.HasDigits)
        {
            // A section of literals only, such as "zero" for zeros: the number does not show, its sign does.
            StringBuilder literal = new();
            if (number < 0)
            {
                literal.Append(culture.NumberFormat.NegativeSign);
            }

            foreach (FormatToken token in section.Tokens)
            {
                AppendLiteral(literal, token, null);
            }

            return literal.ToString();
        }

        double value = Math.Abs(number);
        for (int i = 0; i < section.Percents; i++)
        {
            value *= 100;
        }

        for (int i = 0; i < section.Scale; i++)
        {
            value /= 1000;
        }

        if (!double.IsFinite(value))
        {
            // Percent signs scaled the number past what a double holds.
            return new string('#', 10);
        }

        StringBuilder output = new();
        if (section.Exponent >= 0)
        {
            Scientific(output, section, value, culture);
        }
        else if (section.Slash >= 0)
        {
            Fraction(output, section, value, culture);
        }
        else
        {
            Plain(output, section, value, culture);
        }

        // The sign shows when the number rounds to something other than zero, before everything else.
        if (number < 0 && output.ToString().Any(c => c is >= '1' and <= '9'))
        {
            output.Insert(0, culture.NumberFormat.NegativeSign);
        }

        return output.ToString();
    }

    /// <summary>The General format: up to 11 characters, switching to scientific notation for very large and small numbers.</summary>
    public static string General(double number, CultureInfo culture)
    {
        if (number == 0)
        {
            return "0";
        }

        string sign = number < 0 ? culture.NumberFormat.NegativeSign : "";
        double value = Math.Abs(number);
        string separator = culture.NumberFormat.NumberDecimalSeparator;

        // Both forms fit in 11 characters: the fixed one with as many decimals as fit, the scientific one with up to six
        // significant digits. The fixed form is shown unless it does not fit or shows fewer significant digits.
        int exponent = Magnitude(value);
        decimal mantissa = Round(Shift(value, exponent), 5);
        if (mantissa >= 10)
        {
            mantissa /= 10;
            exponent++;
        }

        string scientific = Trim(mantissa.ToString("0.#####", CultureInfo.InvariantCulture));
        string? fixedText = null;
        if (exponent < 11)
        {
            int integerDigits = Math.Max(exponent + 1, 1);
            int decimals = Math.Max(0, 10 - integerDigits);
            string text = Trim(Round(value, decimals).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            if (text.Length <= 11 && Significant(text) >= Significant(scientific))
            {
                fixedText = text;
            }
        }

        return fixedText is not null
            ? sign + fixedText.Replace(".", separator, StringComparison.Ordinal)
            : sign + scientific.Replace(".", separator, StringComparison.Ordinal) + "E" + (exponent < 0 ? "-" : "+")
                + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>The significant digits a plain number shows: its digits without the leading and trailing zeros.</summary>
    private static int Significant(string text)
    {
        ReadOnlySpan<char> digits = text.Replace(".", "", StringComparison.Ordinal).AsSpan().Trim('0');
        return digits.Length;
    }

    /// <summary>The power of ten of a positive finite number's first digit, exactly, for any double.</summary>
    private static int Magnitude(double value)
    {
        string text = value.ToString("E16", CultureInfo.InvariantCulture);
        return int.Parse(text.AsSpan(text.IndexOf('E', StringComparison.Ordinal) + 1), CultureInfo.InvariantCulture);
    }

    /// <summary>A number divided by ten to <paramref name="exponent"/>, without overflowing for subnormal and huge doubles.</summary>
    private static double Shift(double value, int exponent)
    {
        string text = value.ToString("E16", CultureInfo.InvariantCulture);
        int e = text.IndexOf('E', StringComparison.Ordinal);
        int power = int.Parse(text.AsSpan(e + 1), CultureInfo.InvariantCulture) - exponent;
        return double.Parse(string.Concat(text.AsSpan(0, e), "E", power.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    }

    private static string Trim(string text) => text.Contains('.', StringComparison.Ordinal) ? text.TrimEnd('0').TrimEnd('.') : text;

    /// <summary>
    /// A number rounded half away from zero, as Excel shows it, after keeping the 15 significant digits Excel keeps: so 2.675
    /// with two decimals shows 2.68, as in Excel, not the 2.67 its binary value would give.
    /// </summary>
    private static decimal Round(double value, int decimals)
    {
        decimal exact = value < 7.9e27 ? (decimal)value : decimal.MaxValue;
        return decimal.Round(exact, Math.Min(decimals, 28), MidpointRounding.AwayFromZero);
    }

    /// <summary>The digits of a rounded number's integer part, empty for zero; past decimal's range, the double's own digits.</summary>
    private static (string Integer, string Fraction) Digits(double value, int decimals)
    {
        if (value >= 7.9e27)
        {
            string big = value.ToString("F0", CultureInfo.InvariantCulture);
            return (big, new string('0', decimals));
        }

        decimal rounded = Round(value, decimals);
        string text = rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int dot = text.IndexOf('.', StringComparison.Ordinal);
        string integer = dot < 0 ? text : text[..dot];
        string fraction = dot < 0 ? "" : text[(dot + 1)..];
        return (integer == "0" ? "" : integer, fraction);
    }

    private static void AppendLiteral(StringBuilder output, FormatToken token, Func<string>? general)
    {
        switch (token.Kind)
        {
            case TokenKind.Literal:
                output.Append(token.Text);
                break;
            case TokenKind.Space:
                output.Append(' ');
                break;
            case TokenKind.General when general is not null:
                output.Append(general());
                break;
        }
    }

    /// <summary>A number with integer and decimal placeholders, grouping, percent and literals.</summary>
    private static void Plain(StringBuilder output, FormatSection section, double value, CultureInfo culture)
    {
        List<FormatToken> tokens = section.Tokens;
        int point = section.DecimalPoint;
        int end = tokens.Count;
        List<int> integerSlots = [];
        List<int> fractionSlots = [];
        for (int i = 0; i < end; i++)
        {
            if (tokens[i].Kind == TokenKind.Digit)
            {
                (point >= 0 && i > point ? fractionSlots : integerSlots).Add(i);
            }
        }

        (string integer, string fraction) = Digits(value, fractionSlots.Count);
        string[] integerText = IntegerSlots(tokens, integerSlots, integer, section.Grouping, culture);
        string[] fractionText = FractionSlots(tokens, fractionSlots, fraction);
        int integerIndex = 0;
        int fractionIndex = 0;
        bool integerShown = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            FormatToken token = tokens[i];
            switch (token.Kind)
            {
                case TokenKind.Digit when point >= 0 && i > point:
                    output.Append(fractionText[fractionIndex++]);
                    break;
                case TokenKind.Digit:
                    output.Append(integerText[integerIndex++]);
                    integerShown = true;
                    break;
                case TokenKind.DecimalPoint:
                    if (!integerShown && integer.Length > 0)
                    {
                        // No integer placeholder: the integer digits show before the point all the same.
                        output.Append(integer);
                        integerShown = true;
                    }

                    output.Append(culture.NumberFormat.NumberDecimalSeparator);
                    break;
                case TokenKind.Percent:
                    output.Append(culture.NumberFormat.PercentSymbol);
                    break;
                case TokenKind.Comma:
                    break;
                case TokenKind.General:
                    output.Append(General(value, culture));
                    break;
                default:
                    AppendLiteral(output, token, null);
                    break;
            }
        }
    }

    /// <summary>
    /// What each integer placeholder shows, left to right: digits fill from the right, a digit too many goes to the first
    /// placeholder, and an unfilled <c>0</c> shows 0, <c>?</c> a space and <c>#</c> nothing. With grouping, the whole
    /// integer, separators in, shows at the first placeholder.
    /// </summary>
    private static string[] IntegerSlots(List<FormatToken> tokens, List<int> slots, string digits, bool grouping, CultureInfo culture)
    {
        string[] text = new string[slots.Count];
        if (slots.Count == 0)
        {
            return text;
        }

        if (grouping)
        {
            // Zeros to the left of the digits pad to the leftmost 0 placeholder; question marks pad with spaces.
            int zeros = 0;
            int spaces = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                char placeholder = tokens[slots[i]].Placeholder;
                int fromRight = slots.Count - i;
                if (placeholder == '0')
                {
                    zeros = Math.Max(zeros, fromRight);
                }
                else if (placeholder == '?')
                {
                    spaces = Math.Max(spaces, fromRight);
                }
            }

            string padded = digits.PadLeft(zeros, '0');
            StringBuilder grouped = new();
            string separator = culture.NumberFormat.NumberGroupSeparator;
            for (int i = 0; i < padded.Length; i++)
            {
                if (i > 0 && (padded.Length - i) % 3 == 0)
                {
                    grouped.Append(separator);
                }

                grouped.Append(padded[i]);
            }

            text[0] = new string(' ', Math.Max(0, spaces - padded.Length)) + grouped;
            for (int i = 1; i < text.Length; i++)
            {
                text[i] = "";
            }

            return text;
        }

        int next = digits.Length;
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (next > 0)
            {
                text[i] = i == 0 ? digits[..next] : digits[(next - 1)..next];
                next = i == 0 ? 0 : next - 1;
            }
            else
            {
                text[i] = tokens[slots[i]].Placeholder switch
                {
                    '0' => "0",
                    '?' => " ",
                    _ => "",
                };
            }
        }

        return text;
    }

    /// <summary>What each decimal placeholder shows: its digit, with trailing zeros dropped for <c>#</c> and spaced for <c>?</c>.</summary>
    private static string[] FractionSlots(List<FormatToken> tokens, List<int> slots, string digits)
    {
        string[] text = new string[slots.Count];
        bool trailing = true;
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            char digit = i < digits.Length ? digits[i] : '0';
            char placeholder = tokens[slots[i]].Placeholder;
            if (trailing && digit == '0' && placeholder != '0')
            {
                text[i] = placeholder == '?' ? " " : "";
                continue;
            }

            trailing = false;
            text[i] = digit.ToString();
        }

        return text;
    }

    /// <summary>Scientific notation: a mantissa through the placeholders before the E, the exponent through those after it.</summary>
    private static void Scientific(StringBuilder output, FormatSection section, double value, CultureInfo culture)
    {
        List<FormatToken> tokens = section.Tokens;
        int e = section.Exponent;
        int point = section.DecimalPoint >= 0 && section.DecimalPoint < e ? section.DecimalPoint : -1;
        List<int> integerSlots = [];
        List<int> fractionSlots = [];
        List<int> exponentSlots = [];
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.Digit)
            {
                (i > e ? exponentSlots : point >= 0 && i > point ? fractionSlots : integerSlots).Add(i);
            }
        }

        int integerCount = Math.Max(1, integerSlots.Count);
        bool engineering = integerSlots.Count > 1 && integerSlots.Any(i => tokens[i].Placeholder == '#');
        int exponent = 0;
        double mantissa = value;
        if (value != 0)
        {
            int magnitude = Magnitude(value);
            exponent = engineering ? (int)Math.Floor(magnitude / (double)integerCount) * integerCount : magnitude - (integerCount - 1);
            mantissa = Shift(value, exponent);
            if ((double)Round(mantissa, fractionSlots.Count) >= Math.Pow(10, integerCount))
            {
                exponent += engineering ? integerCount : 1;
                mantissa = Shift(value, exponent);
            }
        }

        (string integer, string fraction) = Digits(mantissa, fractionSlots.Count);
        string[] integerText = IntegerSlots(tokens, integerSlots, integer, false, culture);
        string[] fractionText = FractionSlots(tokens, fractionSlots, fraction);
        string exponentDigits = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        string[] exponentText = IntegerSlots(tokens, exponentSlots, exponentDigits == "0" ? "" : exponentDigits, false, culture);
        int integerIndex = 0, fractionIndex = 0, exponentIndex = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            FormatToken token = tokens[i];
            switch (token.Kind)
            {
                case TokenKind.Digit when i > e:
                    output.Append(exponentText[exponentIndex++]);
                    break;
                case TokenKind.Digit when point >= 0 && i > point:
                    output.Append(fractionText[fractionIndex++]);
                    break;
                case TokenKind.Digit:
                    output.Append(integerText[integerIndex++]);
                    break;
                case TokenKind.DecimalPoint:
                    output.Append(culture.NumberFormat.NumberDecimalSeparator);
                    break;
                case TokenKind.Exponent:
                    output.Append(char.IsUpper(section.Tokens[i].Placeholder) ? 'E' : 'E');
                    output.Append(exponent < 0 ? "-" : token.Placeholder == '+' ? "+" : "");
                    break;
                case TokenKind.Percent:
                    output.Append(culture.NumberFormat.PercentSymbol);
                    break;
                case TokenKind.Comma:
                    break;
                default:
                    AppendLiteral(output, token, null);
                    break;
            }
        }
    }

    /// <summary>A fraction such as <c># ?/?</c>: a whole number, then the closest fraction the denominator's digits allow, or one over a fixed denominator.</summary>
    private static void Fraction(StringBuilder output, FormatSection section, double value, CultureInfo culture)
    {
        List<FormatToken> tokens = section.Tokens;
        int slash = section.Slash;

        // The placeholders before the slash: the last run is the numerator, an earlier run the whole number.
        List<List<int>> runs = [];
        bool inRun = false;
        for (int i = 0; i < slash; i++)
        {
            if (tokens[i].Kind == TokenKind.Digit)
            {
                if (!inRun)
                {
                    runs.Add([]);
                }

                runs[^1].Add(i);
                inRun = true;
            }
            else if (tokens[i].Kind != TokenKind.Comma)
            {
                inRun = false;
            }
        }

        List<int> numeratorSlots = runs[^1];
        List<int> wholeSlots = runs.Count > 1 ? [.. runs.Take(runs.Count - 1).SelectMany(r => r)] : [];
        List<int> denominatorSlots = [];
        int fixedDenominator = 0;
        int denominatorEnd = slash + 1;
        for (int i = slash + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.Digit)
            {
                denominatorSlots.Add(i);
                denominatorEnd = i + 1;
            }
            else if (denominatorSlots.Count == 0 && fixedDenominator == 0 && tokens[i].Kind == TokenKind.Literal && tokens[i].Text.Length > 0 && char.IsAsciiDigit(tokens[i].Text[0]))
            {
                string literal = tokens[i].Text;
                int length = 0;
                while (length < literal.Length && char.IsAsciiDigit(literal[length]))
                {
                    length++;
                }

                // A fixed denominator such as 100 lexes as the literal 1 and two 0 placeholders: the zeros (and any digits
                // after them) belong to it and do not show on their own.
                StringBuilder denominatorText = new(literal[..length]);
                tokens = [.. tokens];
                tokens[i] = new FormatToken(TokenKind.Literal, literal[length..]);
                for (int j = i + 1; length == literal.Length && j < tokens.Count; j++)
                {
                    if (tokens[j].Kind == TokenKind.Digit && tokens[j].Placeholder == '0')
                    {
                        denominatorText.Append('0');
                        tokens[j] = new FormatToken(TokenKind.Literal, "");
                    }
                    else if (tokens[j].Kind == TokenKind.Literal && tokens[j].Text is [char digit, ..] && char.IsAsciiDigit(digit))
                    {
                        literal = tokens[j].Text;
                        length = 0;
                        while (length < literal.Length && char.IsAsciiDigit(literal[length]))
                        {
                            length++;
                        }

                        denominatorText.Append(literal.AsSpan(0, length));
                        tokens[j] = new FormatToken(TokenKind.Literal, literal[length..]);
                    }
                    else
                    {
                        break;
                    }
                }

                string fixedDigits = denominatorText.ToString().TrimStart('0');
                fixedDenominator = fixedDigits.Length == 0 ? 0 : int.Parse(fixedDigits.AsSpan(0, Math.Min(fixedDigits.Length, 9)), CultureInfo.InvariantCulture);
                denominatorEnd = i;
                break;
            }
            else if (denominatorSlots.Count > 0)
            {
                break;
            }
        }

        double whole = wholeSlots.Count > 0 ? Math.Floor(value) : 0;
        double rest = value - whole;
        long numerator;
        long denominator;
        if (fixedDenominator > 0)
        {
            denominator = fixedDenominator;
            numerator = (long)Math.Round(rest * denominator, MidpointRounding.AwayFromZero);
        }
        else
        {
            long maxDenominator = (long)Math.Pow(10, Math.Min(Math.Max(denominatorSlots.Count, 1), 7)) - 1;
            (numerator, denominator) = Closest(rest, maxDenominator);
        }

        if (wholeSlots.Count > 0 && numerator == denominator && numerator != 0)
        {
            whole++;
            numerator = 0;
        }

        bool showFraction = numerator != 0 || wholeSlots.Count == 0;
        string wholeDigits = whole == 0 ? "" : whole.ToString("F0", CultureInfo.InvariantCulture);
        if (wholeSlots.Count > 0 && whole == 0 && !showFraction)
        {
            wholeDigits = "0";
        }

        string[] wholeText = IntegerSlots(tokens, wholeSlots, wholeDigits, section.Grouping, culture);
        string[] numeratorText = IntegerSlots(tokens, numeratorSlots, numerator == 0 && wholeSlots.Count == 0 ? "0" : numerator.ToString(CultureInfo.InvariantCulture), false, culture);
        string denominatorDigits = denominator.ToString(CultureInfo.InvariantCulture);
        int wholeIndex = 0, numeratorIndex = 0;
        int fractionStart = numeratorSlots[0];
        for (int i = 0; i < tokens.Count; i++)
        {
            FormatToken token = tokens[i];
            if (i >= fractionStart && i < denominatorEnd && !showFraction)
            {
                // A whole number with no fraction: spaces where the fraction would be, so columns line up.
                output.Append(token.Kind switch
                {
                    TokenKind.Digit or TokenKind.Slash => " ",
                    TokenKind.Literal => new string(' ', token.Text.Length),
                    _ => "",
                });
                continue;
            }

            switch (token.Kind)
            {
                case TokenKind.Digit when numeratorSlots.Contains(i):
                    output.Append(numeratorText[numeratorIndex++]);
                    break;
                case TokenKind.Digit when denominatorSlots.Contains(i):
                    int at = denominatorSlots.IndexOf(i);
                    if (at == 0)
                    {
                        string padded = denominatorDigits.PadRight(denominatorSlots.Count, ' ');
                        if (denominatorSlots.All(s => tokens[s].Placeholder == '#'))
                        {
                            padded = denominatorDigits;
                        }

                        output.Append(padded);
                    }

                    break;
                case TokenKind.Digit when wholeIndex < wholeText.Length:
                    output.Append(wholeText[wholeIndex++]);
                    break;
                case TokenKind.Digit:
                    break;
                case TokenKind.Slash:
                    output.Append('/');
                    if (fixedDenominator > 0)
                    {
                        output.Append(denominatorDigits);
                    }

                    break;
                case TokenKind.Percent:
                    output.Append(culture.NumberFormat.PercentSymbol);
                    break;
                case TokenKind.Comma:
                case TokenKind.DecimalPoint:
                    break;
                default:
                    AppendLiteral(output, token, null);
                    break;
            }
        }

        if (wholeSlots.Count == 0 && whole == 0 && value >= 1 && fixedDenominator > 0)
        {
            // An improper fraction over a fixed denominator: the numerator holds the whole number too.
        }
    }

    /// <summary>The fraction closest to <paramref name="value"/> (0 to 1) with a denominator up to <paramref name="maxDenominator"/>, by continued fractions.</summary>
    private static (long Numerator, long Denominator) Closest(double value, long maxDenominator)
    {
        if (value <= 0)
        {
            return (0, 1);
        }

        long whole = (long)Math.Floor(value);
        double fraction = value - whole;
        long p0 = 0, q0 = 1, p1 = 1, q1 = 0;
        double x = fraction;
        long bestP = 0, bestQ = 1;
        double bestError = fraction;
        for (int step = 0; step < 64; step++)
        {
            long a = (long)Math.Floor(x);
            long p2 = (a * p1) + p0;
            long q2 = (a * q1) + q0;
            if (q2 > maxDenominator)
            {
                // The best semiconvergent within the limit.
                long k = (maxDenominator - q0) / q1;
                long p = (k * p1) + p0;
                long q = (k * q1) + q0;
                if (q > 0 && Math.Abs(fraction - ((double)p / q)) < bestError)
                {
                    bestP = p;
                    bestQ = q;
                }

                break;
            }

            if (q2 > 0)
            {
                double error = Math.Abs(fraction - ((double)p2 / q2));
                if (error < bestError || (bestQ == 1 && bestP == 0 && error <= bestError))
                {
                    bestP = p2;
                    bestQ = q2;
                    bestError = error;
                }
            }

            p0 = p1;
            q0 = q1;
            p1 = p2;
            q1 = q2;
            double remainder = x - a;
            if (remainder < 1e-12)
            {
                break;
            }

            x = 1 / remainder;
        }

        return ((whole * bestQ) + bestP, bestQ);
    }

    /// <summary>A date or time: the serial rounded to the smallest unit shown, then split into calendar and clock parts.</summary>
    private static string FormatDate(FormatSection section, double serial, CultureInfo culture, bool date1904)
    {
        int subSecond = section.SubSecondDigits;
        double unitsPerSecond = Math.Pow(10, subSecond);
        long units = (long)Math.Round(serial * 86400 * unitsPerSecond, MidpointRounding.AwayFromZero);
        long totalSeconds = units / (long)unitsPerSecond;
        long fractionUnits = units % (long)unitsPerSecond;
        long days = totalSeconds / 86400;
        if (days > (date1904 ? LastDay1904 : LastDay1900))
        {
            return new string('#', 10);
        }

        long secondOfDay = totalSeconds % 86400;
        int hour = (int)(secondOfDay / 3600);
        int minute = (int)(secondOfDay / 60 % 60);
        int second = (int)(secondOfDay % 60);
        (int year, int month, int day, int weekday) = Calendar(days, date1904);
        DateTimeFormatInfo names = culture.DateTimeFormat;

        StringBuilder output = new();
        foreach (FormatToken token in section.Tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Year:
                    output.Append(token.Count <= 2
                        ? (year % 100).ToString("00", CultureInfo.InvariantCulture)
                        : year.ToString("0000", CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Month:
                    output.Append(token.Count switch
                    {
                        1 => month.ToString(CultureInfo.InvariantCulture),
                        2 => month.ToString("00", CultureInfo.InvariantCulture),
                        3 => names.AbbreviatedMonthNames[month - 1],
                        4 => names.MonthNames[month - 1],
                        _ => names.MonthNames[month - 1][..1],
                    });
                    break;
                case TokenKind.Day:
                    output.Append(token.Count switch
                    {
                        1 => day.ToString(CultureInfo.InvariantCulture),
                        2 => day.ToString("00", CultureInfo.InvariantCulture),
                        3 => names.AbbreviatedDayNames[weekday],
                        _ => names.DayNames[weekday],
                    });
                    break;
                case TokenKind.Hour:
                    int shown = section.TwelveHour ? (hour % 12 == 0 ? 12 : hour % 12) : hour;
                    output.Append(shown.ToString(token.Count >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Minute:
                    output.Append(minute.ToString(token.Count >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Second:
                    output.Append(second.ToString(token.Count >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case TokenKind.SubSecond:
                    output.Append(names.TimeSeparator == "." ? "," : culture.NumberFormat.NumberDecimalSeparator);
                    string subSecondDigits = fractionUnits.ToString(new string('0', Math.Max(subSecond, 1)), CultureInfo.InvariantCulture);
                    output.Append(subSecondDigits.AsSpan(0, Math.Min(token.Count, subSecondDigits.Length)));
                    break;
                case TokenKind.AmPm:
                    bool pm = hour >= 12;
                    if (token.Text.Length == 3)
                    {
                        char letter = pm ? token.Text[2] : token.Text[0];
                        output.Append(letter);
                    }
                    else
                    {
                        string designator = pm ? names.PMDesignator : names.AMDesignator;
                        output.Append(designator.Length > 0 ? designator : pm ? "PM" : "AM");
                    }

                    break;
                case TokenKind.ElapsedHours:
                    output.Append((totalSeconds / 3600).ToString(new string('0', token.Count), CultureInfo.InvariantCulture));
                    break;
                case TokenKind.ElapsedMinutes:
                    output.Append((totalSeconds / 60).ToString(new string('0', token.Count), CultureInfo.InvariantCulture));
                    break;
                case TokenKind.ElapsedSeconds:
                    output.Append(totalSeconds.ToString(new string('0', token.Count), CultureInfo.InvariantCulture));
                    break;
                case TokenKind.General:
                    output.Append(General(serial, culture));
                    break;
                default:
                    AppendLiteral(output, token, null);
                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// The date of a day number, with weekday (0 is Sunday). In the 1900 system day 1 is 1900-01-01, day 0 shows as
    /// 1900-01-00 and day 60 as 1900-02-29, the day Excel keeps for Lotus 1-2-3's sake; in the 1904 system day 0 is
    /// 1904-01-01.
    /// </summary>
    internal static (int Year, int Month, int Day, int Weekday) Calendar(long days, bool date1904)
    {
        if (date1904)
        {
            DateTime date = new DateTime(1904, 1, 1).AddDays(days);
            return (date.Year, date.Month, date.Day, (int)date.DayOfWeek);
        }

        int weekday = (int)((days + 6) % 7);
        if (days == 0)
        {
            return (1900, 1, 0, weekday);
        }

        if (days == 60)
        {
            return (1900, 2, 29, weekday);
        }

        DateTime real = new DateTime(1899, 12, 31).AddDays(days < 60 ? days : days - 1);
        return (real.Year, real.Month, real.Day, weekday);
    }
}
