using System.Text;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// Word's date and time pictures (the <c>\@</c> switch) as ODF's date and time styles (ODF 1.3 Part 3, 16.29), and back.
/// </summary>
internal static class OdfDatePictures
{
    private const char Quote = (char)39;

    /// <summary>
    /// Whether a picture shows only the time of day: hours, minutes or seconds and no day, month or year. Quoted text and the
    /// AM/PM and A/P markers are not letters of the picture (the M of AM/PM is no month).
    /// </summary>
    public static bool IsTime(string? picture)
    {
        if (picture is not { Length: > 0 })
        {
            return false;
        }

        bool time = false;
        int i = 0;
        while (i < picture.Length)
        {
            char c = picture[i];
            if (c == Quote)
            {
                int end = picture.IndexOf(Quote, i + 1);
                i = end < 0 ? picture.Length : end + 1;
                continue;
            }

            if (Match(picture, i, "AM/PM") || Match(picture, i, "A/P"))
            {
                i += Match(picture, i, "AM/PM") ? 5 : 3;
                continue;
            }

            if (c is 'd' or 'M' or 'y')
            {
                return false;
            }

            time |= c is 'H' or 'h' or 'm' or 's';
            i++;
        }

        return time;
    }

    private static bool Match(string text, int at, string token) =>
        at + token.Length <= text.Length && string.Compare(text, at, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>A date style, or a time style when the picture shows only the time, with an empty name; null for an empty picture.</summary>
    public static XElement? Style(string picture)
    {
        List<XElement> parts = [];
        StringBuilder literal = new();
        bool twelveHours = picture.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) || picture.Contains("A/P", StringComparison.OrdinalIgnoreCase);
        int i = 0;
        while (i < picture.Length)
        {
            char c = picture[i];
            if (c == '\'')
            {
                int end = picture.IndexOf('\'', i + 1);
                end = end < 0 ? picture.Length : end;
                literal.Append(picture.AsSpan(i + 1, end - i - 1));
                i = end + 1;
                continue;
            }

            if (Match(picture, i, "AM/PM") || Match(picture, i, "A/P"))
            {
                Flush();
                parts.Add(new XElement(NumberNs + "am-pm"));
                i += Match(picture, i, "AM/PM") ? 5 : 3;
                continue;
            }

            if (c is not ('d' or 'M' or 'y' or 'H' or 'h' or 'm' or 's'))
            {
                literal.Append(c);
                i++;
                continue;
            }

            int count = 1;
            while (i + count < picture.Length && picture[i + count] == c)
            {
                count++;
            }

            Flush();
            parts.Add(c switch
            {
                'd' when count >= 3 => Part("day-of-week", count >= 4),
                'd' => Part("day", count >= 2),
                'M' when count >= 3 => Part("month", count >= 4, new XAttribute(NumberNs + "textual", "true")),
                'M' => Part("month", count >= 2),
                'y' => Part("year", count >= 3),
                'H' or 'h' => Part("hours", count >= 2),
                'm' => Part("minutes", count >= 2),
                _ => Part("seconds", count >= 2),
            });
            i += count;
        }

        Flush();
        if (parts.Count == 0)
        {
            return null;
        }

        if (twelveHours && !parts.Any(p => p.Name == NumberNs + "am-pm"))
        {
            parts.Add(new XElement(NumberNs + "am-pm"));
        }

        return new XElement(NumberNs + (IsTime(picture) ? "time-style" : "date-style"), new XAttribute(StyleNs + "name", ""), parts);

        void Flush()
        {
            if (literal.Length > 0)
            {
                parts.Add(new XElement(NumberNs + "text", literal.ToString()));
                literal.Clear();
            }
        }

        static XElement Part(string name, bool isLong, params object[] attributes) =>
            new(NumberNs + name, isLong ? new XAttribute(NumberNs + "style", "long") : null, attributes);

    }

    /// <summary>Word's picture for a date or time style; null when it shows nothing Word can.</summary>
    public static string? Picture(XElement style)
    {
        bool twelveHours = style.Elements(NumberNs + "am-pm").Any();
        StringBuilder picture = new();
        bool shows = false;
        foreach (XElement part in style.Elements())
        {
            shows |= part.Name.LocalName is not ("text" or "am-pm");
            bool isLong = part.Attr(NumberNs, "style") == "long";
            switch (part.Name.LocalName)
            {
                case "day":
                    picture.Append(isLong ? "dd" : "d");
                    break;
                case "day-of-week":
                    picture.Append(isLong ? "dddd" : "ddd");
                    break;
                case "month":
                    picture.Append(part.Attr(NumberNs, "textual") == "true" ? (isLong ? "MMMM" : "MMM") : (isLong ? "MM" : "M"));
                    break;
                case "year":
                    picture.Append(isLong ? "yyyy" : "yy");
                    break;
                case "hours":
                    picture.Append(twelveHours ? (isLong ? "hh" : "h") : (isLong ? "HH" : "H"));
                    break;
                case "minutes":
                    picture.Append(isLong ? "mm" : "m");
                    break;
                case "seconds":
                    picture.Append(isLong ? "ss" : "s");
                    break;
                case "am-pm":
                    picture.Append("AM/PM");
                    break;
                case "text":
                    string text = part.Value.Replace("\"", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
                    // Letters would read as parts of the picture: Word takes them quoted.
                    int first = text.ToList().FindIndex(char.IsLetter);
                    if (first < 0)
                    {
                        picture.Append(text);
                        break;
                    }

                    int last = text.ToList().FindLastIndex(char.IsLetter);
                    picture.Append(text, 0, first).Append(Quote).Append(text, first, last - first + 1).Append(Quote).Append(text, last + 1, text.Length - last - 1);
                    break;
            }
        }

        return shows ? picture.ToString() : null;
    }
}
