using System.Globalization;
using System.Text;

namespace Egap.Documents;

/// <summary>
/// Word's numeric picture (the <c>\#</c> switch): 0 shows a digit or a zero, # a digit or a space, x drops the digits left
/// of it (or rounds at it after the decimal point); the decimal symbol and the digit grouping symbol; - and + signs;
/// sections for positive, negative and zero values split by semicolons; text in apostrophes and any other character as it
/// is.
/// </summary>
internal static class NumberPicture
{
    private enum Item
    {
        Literal,
        Zero,
        Hash,
        Drop,
        Point,
        Minus,
        Plus,
    }

    /// <summary>The number in <paramref name="picture"/>, with <paramref name="decimalSymbol"/> as the decimal point.</summary>
    public static string Format(double value, string picture, char decimalSymbol = '.')
    {
        if (!double.IsFinite(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        char group = decimalSymbol == ',' ? '.' : ',';
        List<string> sections = Sections(picture);
        string section = sections[0];
        bool signed = true, literalSigns = false;
        if (value < 0 && sections.Count >= 2)
        {
            section = sections[1];
            value = -value;
            signed = false;
            literalSigns = true;
        }
        else if (value == 0 && sections.Count >= 3)
        {
            section = sections[2];
            literalSigns = true;
        }

        List<(Item Kind, string Text)> items = Parse(section, decimalSymbol, group, literalSigns, out bool grouping);
        int point = items.FindIndex(i => i.Kind == Item.Point);
        int integerPlaces = 0, fractionPlaces = 0, keep = -1;
        bool hasSign = false;
        for (int i = 0; i < items.Count; i++)
        {
            Item kind = items[i].Kind;
            if (kind is Item.Zero or Item.Hash or Item.Drop)
            {
                if (point >= 0 && i > point)
                {
                    fractionPlaces++;
                }
                else
                {
                    if (kind == Item.Drop && keep < 0)
                    {
                        keep = 0;
                    }

                    if (keep >= 0)
                    {
                        keep++;
                    }

                    integerPlaces++;
                }
            }

            hasSign |= kind is Item.Minus or Item.Plus;
        }

        bool negative = value < 0;
        string digits = Digits(Math.Abs(value), fractionPlaces);
        int dot = digits.IndexOf('.', StringComparison.Ordinal);
        string whole = dot < 0 ? digits : digits[..dot];
        string fraction = dot < 0 ? "" : digits[(dot + 1)..];
        if (whole == "0")
        {
            whole = "";
        }

        if (whole.Length == 0 && fraction.Trim('0').Length == 0)
        {
            negative = false;
        }

        if (keep >= 0 && whole.Length > keep)
        {
            whole = whole[^keep..];
        }

        // The places from the first 0 on must show a digit.
        int required = 0;
        for (int i = 0, seen = 0; i < items.Count && (point < 0 || i < point); i++)
        {
            if (items[i].Kind is Item.Zero or Item.Hash or Item.Drop)
            {
                if (items[i].Kind == Item.Zero && required == 0)
                {
                    required = integerPlaces - seen;
                }

                seen++;
            }
        }

        whole = whole.PadLeft(required, '0');
        StringBuilder text = new();
        if (negative && signed && !hasSign)
        {
            text.Append('-');
        }

        int placed = 0, fractionIndex = 0, wholeIndex = 0;
        bool numberShown = false;
        for (int i = 0; i < items.Count; i++)
        {
            (Item kind, string literal) = items[i];
            switch (kind)
            {
                case Item.Literal:
                    text.Append(literal);
                    break;
                case Item.Minus:
                    text.Append(negative ? '-' : ' ');
                    break;
                case Item.Plus:
                    text.Append(negative ? '-' : whole.Length == 0 && fraction.Trim('0').Length == 0 ? ' ' : '+');
                    break;
                case Item.Point:
                    if (!numberShown && integerPlaces == 0)
                    {
                        AppendWhole(text, whole, 0, whole.Length, grouping, group);
                    }

                    numberShown = true;
                    text.Append(decimalSymbol);
                    break;
                case Item.Zero or Item.Hash or Item.Drop when point >= 0 && i > point:
                    text.Append(fractionIndex < fraction.Length ? fraction[fractionIndex] : '0');
                    fractionIndex++;
                    break;
                default:
                    // An integer place: the first takes the digits that do not fit, an empty # shows a space.
                    numberShown = true;
                    int empty = Math.Max(0, integerPlaces - whole.Length);
                    int place = placed++;
                    if (place < empty)
                    {
                        text.Append(' ');
                        break;
                    }

                    int count = place == 0 ? whole.Length - integerPlaces + 1 : 1;
                    AppendWhole(text, whole, wholeIndex, count, grouping, group);
                    wholeIndex += count;
                    break;
            }
        }

        if (!numberShown && integerPlaces == 0 && fractionPlaces == 0 && point < 0 && items.Count == 0)
        {
            text.Append(whole.Length == 0 ? "0" : whole);
        }

        return text.ToString();
    }

    /// <summary>Digits <paramref name="start"/> on of <paramref name="whole"/>, with group symbols every three from the right.</summary>
    private static void AppendWhole(StringBuilder text, string whole, int start, int count, bool grouping, char group)
    {
        for (int i = start; i < start + count; i++)
        {
            text.Append(whole[i]);
            int right = whole.Length - i - 1;
            if (grouping && right > 0 && right % 3 == 0)
            {
                text.Append(group);
            }
        }
    }

    /// <summary>The value rounded half away from zero to <paramref name="places"/> decimals, as plain digits.</summary>
    private static string Digits(double value, int places)
    {
        if (value < 7.9e27)
        {
            decimal exact = (decimal)value;
            exact = Math.Round(exact, Math.Min(places, 28), MidpointRounding.AwayFromZero);
            return exact.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        return value.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static List<string> Sections(string picture)
    {
        List<string> sections = [];
        int from = 0;
        char quote = '\0';
        for (int i = 0; i < picture.Length; i++)
        {
            char c = picture[i];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '\'' or '`')
            {
                quote = c;
            }
            else if (c == ';' && sections.Count < 2)
            {
                sections.Add(picture[from..i]);
                from = i + 1;
            }
        }

        sections.Add(picture[from..]);
        return sections;
    }

    private static List<(Item, string)> Parse(string section, char decimalSymbol, char group, bool literalSigns, out bool grouping)
    {
        List<(Item, string)> items = [];
        grouping = false;
        bool point = false;
        for (int i = 0; i < section.Length; i++)
        {
            char c = section[i];
            if (c is '\'' or '`')
            {
                int end = section.IndexOf(c, i + 1);
                end = end < 0 ? section.Length : end;
                if (c == '\'')
                {
                    items.Add((Item.Literal, section[(i + 1)..end]));
                }

                i = end;
            }
            else if (c == '0')
            {
                items.Add((Item.Zero, ""));
            }
            else if (c == '#')
            {
                items.Add((Item.Hash, ""));
            }
            else if (c is 'x' or 'X')
            {
                items.Add((Item.Drop, ""));
            }
            else if (c == decimalSymbol && !point)
            {
                point = true;
                items.Add((Item.Point, ""));
            }
            else if (c == group && !point && items.Exists(item => item.Item1 is Item.Zero or Item.Hash or Item.Drop))
            {
                grouping = true;
            }
            else if (c == '-' && !literalSigns)
            {
                items.Add((Item.Minus, ""));
            }
            else if (c == '+' && !literalSigns)
            {
                items.Add((Item.Plus, ""));
            }
            else
            {
                items.Add((Item.Literal, c.ToString()));
            }
        }

        return items;
    }
}
