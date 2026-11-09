using System.Globalization;
using System.Xml.Linq;

using Egap.Documents.NumberFormats;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Spreadsheets.Ods;

/// <summary>What kind of value a cell with a number format holds in an ODS file (ODF 1.3 Part 3, 19.385).</summary>
internal enum OdsValueKind : byte
{
    Float,
    Percentage,
    Date,
    Time,
}

/// <summary>
/// Excel number format codes as ODF data styles (ODF 1.3 Part 3, 16.29): each section a number, percentage, date, time or text
/// style, the first sections applied through <c>style:map</c> conditions to the last, as Excel picks a code's section by
/// the value's sign or the section's condition.
/// </summary>
internal sealed class OdsDataStyles
{
    private readonly Dictionary<string, (string Name, OdsValueKind Kind)> made = new(StringComparer.Ordinal);

    /// <summary>The data styles made so far, in order, for <c>office:automatic-styles</c>.</summary>
    public List<XElement> Elements { get; } = [];

    /// <summary>The data style for a format code and the kind of value its cells hold; a null name for General.</summary>
    public (string? Name, OdsValueKind Kind) For(string code)
    {
        if (code.Length == 0 || code.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            return (null, OdsValueKind.Float);
        }

        if (made.TryGetValue(code, out (string Name, OdsValueKind Kind) known))
        {
            return known;
        }

        FormatSection[] sections = FormatParser.Parse(code);
        string name = "N" + (made.Count + 1).ToString(CultureInfo.InvariantCulture);
        OdsValueKind kind = sections[0].IsDate
            ? sections[0].Tokens.Any(t => t.Kind is TokenKind.Year or TokenKind.Month or TokenKind.Day) ? OdsValueKind.Date : OdsValueKind.Time
            : sections[0].Percents > 0 ? OdsValueKind.Percentage : OdsValueKind.Float;

        // Excel's sections without conditions: positive and zero, negative; or positive, negative, zero. A last section that
        // shows text (the fourth, or one with @ after one to three number sections) is the text section. A text section of
        // just @ shows text as ODF does by default and is left out; one with more becomes the main text style, which maps
        // every number to its section.
        FormatSection? textSection = sections.Length >= 4 ? sections[3]
            : sections.Length >= 2 && IsText(sections[^1]) ? sections[^1]
            : null;
        FormatSection[] numeric = sections.Length >= 4 ? sections[..3] : textSection is not null ? sections[..^1] : sections;
        if (sections.Length == 1 && IsText(sections[0]))
        {
            Elements.Add(Section(name, sections[0], OdsValueKind.Float, text: true));
        }
        else if (textSection is not null && !textSection.Tokens.All(t => t.Kind == TokenKind.Text)
            && numeric.All(n => n.Condition is null))
        {
            string[] conditions = numeric.Length switch
            {
                1 => [">=0", "<0"],
                2 => [">=0", "<0"],
                _ => [">0", "<0", "=0"],
            };
            XElement own = Section(name, textSection, OdsValueKind.Float, text: true);
            for (int i = 0; i < conditions.Length; i++)
            {
                int section = Math.Min(i, numeric.Length - 1);
                string part = name + "P" + section.ToString(CultureInfo.InvariantCulture);
                if (i == section)
                {
                    Elements.Add(Section(part, numeric[section], kind, text: false));
                }

                own.Add(new XElement(StyleNs + "map", new XAttribute(StyleNs + "condition", "value()" + conditions[i]), new XAttribute(StyleNs + "apply-style-name", part)));
            }

            Elements.Add(own);
        }
        else if (numeric.Length == 1)
        {
            Elements.Add(Section(name, numeric[0], kind, text: false));
        }
        else
        {
            List<(string Condition, string Style)> maps = [];
            bool conditional = numeric.Any(s => s.Condition is not null);
            for (int i = 0; i < numeric.Length - 1; i++)
            {
                string part = name + "P" + i.ToString(CultureInfo.InvariantCulture);
                Elements.Add(Section(part, numeric[i], kind, text: false));
                string condition = numeric[i].Condition is { } given
                    ? given.Operator + given.Value.ToString("R", CultureInfo.InvariantCulture)
                    : conditional ? (i == 0 ? ">=0" : "<0")
                    : numeric.Length == 2 ? ">=0" : i == 0 ? ">0" : "<0";
                maps.Add(("value()" + condition, part));
            }

            XElement own = Section(name, numeric[^1], kind, text: false);
            foreach ((string condition, string style) in maps)
            {
                own.Add(new XElement(StyleNs + "map", new XAttribute(StyleNs + "condition", condition), new XAttribute(StyleNs + "apply-style-name", style)));
            }

            Elements.Add(own);
        }

        made[code] = (name, kind);
        return (name, kind);
    }

    private static bool IsText(FormatSection section) => section.HasText && !section.HasDigits && !section.IsDate;

    /// <summary>The boolean style that shows a boolean cell as TRUE or FALSE.</summary>
    public string Boolean()
    {
        const string Key = "\u0001boolean";
        if (!made.TryGetValue(Key, out (string Name, OdsValueKind Kind) known))
        {
            known = ("N" + (made.Count + 1).ToString(CultureInfo.InvariantCulture), OdsValueKind.Float);
            Elements.Add(new XElement(NumberNs + "boolean-style", new XAttribute(StyleNs + "name", known.Name), new XElement(NumberNs + "boolean")));
            made[Key] = known;
        }

        return known.Name;
    }

    private static XElement Section(string name, FormatSection section, OdsValueKind kind, bool text)
    {
        bool timeOnly = kind == OdsValueKind.Time;
        string element = text ? "text-style"
            : section.IsDate ? (timeOnly ? "time-style" : "date-style")
            : section.Percents > 0 ? "percentage-style"
            : "number-style";
        XElement style = new(NumberNs + element, new XAttribute(StyleNs + "name", name));
        if (section.Color is { Index: null } color)
        {
            style.Add(new XElement(StyleNs + "text-properties", new XAttribute(FoNs + "color", $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}")));
        }

        if (section.IsDate)
        {
            DateParts(style, section);
        }
        else if (text)
        {
            foreach (FormatToken token in section.Tokens)
            {
                Text(style, token);
            }
        }
        else
        {
            NumberParts(style, section);
        }

        return style;
    }

    /// <summary>Literal text, a space for <c>_x</c>, or the cell's text for <c>@</c>.</summary>
    private static void Text(XElement style, FormatToken token)
    {
        switch (token.Kind)
        {
            case TokenKind.Literal when !token.Text.StartsWith('\u0001'):
                AddText(style, token.Text);
                break;
            case TokenKind.Space:
                AddText(style, " ");
                break;
            case TokenKind.Text:
                style.Add(new XElement(NumberNs + "text-content"));
                break;
            case TokenKind.Fill:
                style.Add(new XElement(NumberNs + "fill-character", token.Text));
                break;
        }
    }

    private static void AddText(XElement style, string text)
    {
        if (style.LastNode is XElement last && last.Name == NumberNs + "text")
        {
            last.Value += text;
        }
        else
        {
            style.Add(new XElement(NumberNs + "text", text));
        }
    }

    private static void DateParts(XElement style, FormatSection section)
    {
        bool elapsed = false;
        foreach (FormatToken token in section.Tokens)
        {
            string? form = null;
            XElement? part = token.Kind switch
            {
                TokenKind.Year => new XElement(NumberNs + "year", Long(token.Count > 2)),
                TokenKind.Month when token.Count >= 3 => new XElement(NumberNs + "month", Long(token.Count == 4), new XAttribute(NumberNs + "textual", "true")),
                TokenKind.Month => new XElement(NumberNs + "month", Long(token.Count >= 2)),
                TokenKind.Day when token.Count >= 3 => new XElement(NumberNs + "day-of-week", Long(token.Count >= 4)),
                TokenKind.Day => new XElement(NumberNs + "day", Long(token.Count >= 2)),
                TokenKind.Hour => new XElement(NumberNs + "hours", Long(token.Count >= 2)),
                TokenKind.Minute => new XElement(NumberNs + "minutes", Long(token.Count >= 2)),
                TokenKind.Second => new XElement(NumberNs + "seconds", Long(token.Count >= 2)),
                TokenKind.ElapsedHours => new XElement(NumberNs + "hours", Long(token.Count >= 2)),
                TokenKind.ElapsedMinutes => new XElement(NumberNs + "minutes", Long(token.Count >= 2)),
                TokenKind.ElapsedSeconds => new XElement(NumberNs + "seconds", Long(token.Count >= 2)),
                TokenKind.AmPm => new XElement(NumberNs + "am-pm"),
                _ => null,
            };
            elapsed |= token.Kind is TokenKind.ElapsedHours or TokenKind.ElapsedMinutes or TokenKind.ElapsedSeconds;
            if (token.Kind == TokenKind.SubSecond)
            {
                // Fractions of a second belong to the seconds before them, as number:decimal-places.
                if (style.Elements(NumberNs + "seconds").LastOrDefault() is { } seconds)
                {
                    seconds.SetAttributeValue(NumberNs + "decimal-places", token.Count.ToString(CultureInfo.InvariantCulture));
                }

                continue;
            }

            if (token.Kind == TokenKind.DecimalPoint)
            {
                // The point of 's.00' belongs to the seconds; its digits come next as a sub-second run.
                continue;
            }

            if (part is not null)
            {
                style.Add(part);
            }
            else
            {
                form = token.Kind switch
                {
                    TokenKind.Literal when !token.Text.StartsWith('\u0001') => token.Text,
                    TokenKind.Space => " ",
                    TokenKind.Comma => ",",
                    TokenKind.Slash => "/",
                    TokenKind.Digit when token.Placeholder == '0' => "0",
                    _ => null,
                };
                if (form is not null)
                {
                    AddText(style, form);
                }
            }
        }

        if (elapsed)
        {
            style.SetAttributeValue(NumberNs + "truncate-on-overflow", "false");
        }
    }

    private static XAttribute? Long(bool value) => value ? new XAttribute(NumberNs + "style", "long") : null;

    /// <summary>
    /// A number's parts: text before, the number (a plain, scientific or fraction number), text after. Literals among the
    /// integer digits, as in <c>000-00-0000</c>, are number:embedded-text at their place counted from the decimal point.
    /// </summary>
    private static void NumberParts(XElement style, FormatSection section)
    {
        List<FormatToken> tokens = section.Tokens;
        int firstDigit = tokens.FindIndex(t => t.Kind == TokenKind.Digit);
        if (firstDigit < 0)
        {
            // No digits, such as a code of text alone: the literals and, for General, the number as it is.
            foreach (FormatToken token in tokens)
            {
                if (token.Kind == TokenKind.General)
                {
                    style.Add(new XElement(NumberNs + "number", new XAttribute(NumberNs + "min-integer-digits", "1")));
                }
                else if (token.Kind == TokenKind.Percent)
                {
                    AddText(style, "%");
                }
                else
                {
                    Text(style, token);
                }
            }

            return;
        }

        int lastDigit = tokens.FindLastIndex(t => t.Kind == TokenKind.Digit);
        for (int i = 0; i < firstDigit; i++)
        {
            Before(style, tokens[i]);
        }

        if (section.Slash >= 0)
        {
            style.Add(Fraction(tokens, firstDigit, section.Slash, out int end));
            lastDigit = Math.Max(lastDigit, end);
        }
        else
        {
            style.Add(Number(section, tokens, firstDigit, lastDigit));
        }

        for (int i = lastDigit + 1; i < tokens.Count; i++)
        {
            if (section.Exponent >= 0 && i <= section.Exponent)
            {
                continue;
            }

            Before(style, tokens[i]);
        }
    }

    /// <summary>A token outside the number: literal text, a percent sign, a space or a fill.</summary>
    private static void Before(XElement style, FormatToken token)
    {
        switch (token.Kind)
        {
            case TokenKind.Percent:
                AddText(style, "%");
                break;
            case TokenKind.Comma or TokenKind.DecimalPoint or TokenKind.Exponent:
                break;
            default:
                Text(style, token);
                break;
        }
    }

    private static XElement Number(FormatSection section, List<FormatToken> tokens, int first, int last)
    {
        int point = section.DecimalPoint >= 0 ? section.DecimalPoint : int.MaxValue;
        int exponent = section.Exponent;
        int mantissaEnd = exponent >= 0 ? exponent - 1 : last;
        int integers = 0, minIntegers = 0, decimals = 0, minDecimals = 0, exponentDigits = 0;
        List<(int Index, string Text)> embedded = [];
        for (int i = first; i <= last; i++)
        {
            FormatToken token = tokens[i];
            if (exponent >= 0 && i > exponent)
            {
                exponentDigits += token.Kind == TokenKind.Digit ? 1 : 0;
                continue;
            }

            if (i > mantissaEnd)
            {
                continue;
            }

            if (token.Kind == TokenKind.Digit)
            {
                if (i < point)
                {
                    integers++;
                    minIntegers += token.Placeholder == '0' ? 1 : 0;
                }
                else
                {
                    decimals++;
                    minDecimals += token.Placeholder == '0' ? 1 : 0;
                }
            }
            else if (i < point && token.Kind is TokenKind.Literal or TokenKind.Space && !(token.Kind == TokenKind.Literal && token.Text.StartsWith('\u0001')))
            {
                embedded.Add((i, token.Kind == TokenKind.Space ? " " : token.Text));
            }
        }

        XElement number = new(NumberNs + (exponent >= 0 ? "scientific-number" : "number"));
        number.Add(new XAttribute(NumberNs + "decimal-places", decimals.ToString(CultureInfo.InvariantCulture)));
        if (decimals != minDecimals && exponent < 0)
        {
            number.Add(new XAttribute(NumberNs + "min-decimal-places", minDecimals.ToString(CultureInfo.InvariantCulture)));
        }

        number.Add(new XAttribute(NumberNs + "min-integer-digits", minIntegers.ToString(CultureInfo.InvariantCulture)));
        if (section.Grouping && exponent < 0)
        {
            number.Add(new XAttribute(NumberNs + "grouping", "true"));
        }

        if (section.Scale > 0 && exponent < 0)
        {
            number.Add(new XAttribute(NumberNs + "display-factor", Math.Pow(1000, section.Scale).ToString("R", CultureInfo.InvariantCulture)));
        }

        if (exponent >= 0)
        {
            number.Add(new XAttribute(NumberNs + "min-exponent-digits", Math.Max(1, exponentDigits).ToString(CultureInfo.InvariantCulture)));
        }

        foreach ((int index, string text) in embedded)
        {
            // Its place: the integer digits after it, up to the decimal point.
            int after = 0;
            for (int i = index + 1; i <= Math.Min(mantissaEnd, point - 1); i++)
            {
                after += tokens[i].Kind == TokenKind.Digit ? 1 : 0;
            }

            number.Add(new XElement(NumberNs + "embedded-text", new XAttribute(NumberNs + "position", after.ToString(CultureInfo.InvariantCulture)), text));
        }

        return number;
    }

    private static XElement Fraction(List<FormatToken> tokens, int first, int slash, out int end)
    {
        // The numerator is the run of digits just before the slash; digits before a break ahead of it are the whole part.
        int numeratorStart = slash;
        while (numeratorStart - 1 >= first && tokens[numeratorStart - 1].Kind == TokenKind.Digit)
        {
            numeratorStart--;
        }

        int whole = 0;
        bool hasWhole = false;
        for (int i = first; i < numeratorStart; i++)
        {
            if (tokens[i].Kind == TokenKind.Digit)
            {
                hasWhole = true;
                whole += tokens[i].Placeholder == '0' ? 1 : 0;
            }
        }

        int numerator = slash - numeratorStart;
        int denominatorDigits = 0;
        string fixedDenominator = "";
        end = slash;
        for (int i = slash + 1; i < tokens.Count; i++)
        {
            end = i;
            if (tokens[i].Kind == TokenKind.Digit && fixedDenominator.Length == 0)
            {
                denominatorDigits++;
            }
            else if (tokens[i].Kind == TokenKind.Literal && denominatorDigits == 0 && tokens[i].Text.All(char.IsAsciiDigit))
            {
                fixedDenominator += tokens[i].Text;
            }
            else
            {
                end = i - 1;
                break;
            }
        }

        XElement fraction = new(NumberNs + "fraction");
        if (hasWhole)
        {
            fraction.Add(new XAttribute(NumberNs + "min-integer-digits", whole.ToString(CultureInfo.InvariantCulture)));
        }

        fraction.Add(new XAttribute(NumberNs + "min-numerator-digits", Math.Max(1, numerator).ToString(CultureInfo.InvariantCulture)));
        if (fixedDenominator.Length > 0 && int.TryParse(fixedDenominator, NumberStyles.None, CultureInfo.InvariantCulture, out int denominator) && denominator > 0)
        {
            fraction.Add(new XAttribute(NumberNs + "denominator-value", denominator.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            fraction.Add(new XAttribute(NumberNs + "min-denominator-digits", Math.Max(1, denominatorDigits).ToString(CultureInfo.InvariantCulture)));
        }

        return fraction;
    }
}
