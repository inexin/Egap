using System.Globalization;
using System.Text;

namespace Egap.Documents;

/// <summary>
/// Computes list labels (Phase 6 design Q6) the way Word numbers: in document order, the section bodies as one story and
/// each header and footer on its own; lists that share a definition share their counters; a list's start override restarts
/// a level where the list is first used; a level restarts after a higher level as its <see cref="ListLevel.RestartAfterLevel"/>
/// says. Results are kept until the document changes.
/// </summary>
internal static class Numbering
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, State> States = new();

    public static ListLabel? GetLabel(Paragraph paragraph)
    {
        Document document = paragraph.Document;
        State state = States.GetValue(document, _ => new State());
        if (state.Version != document.Version || state.Labels is null)
        {
            state.Labels = Compute(document);
            state.Version = document.Version;
        }

        return state.Labels.GetValueOrDefault(paragraph);
    }

    /// <summary>A number written in <paramref name="style"/>.</summary>
    public static string Format(int value, NumberStyle style)
    {
        return style switch
        {
            NumberStyle.Decimal => value.ToString(CultureInfo.InvariantCulture),
            NumberStyle.DecimalZero => value is >= 0 and < 10 ? "0" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture),
            NumberStyle.UpperRoman => Roman(value),
            NumberStyle.LowerRoman => Roman(value).ToLowerInvariant(),
            NumberStyle.UpperLetter => Letter(value),
            NumberStyle.LowerLetter => Letter(value).ToLowerInvariant(),
            NumberStyle.Ordinal => value.ToString(CultureInfo.InvariantCulture) + OrdinalSuffix(value),
            NumberStyle.CardinalText => Capitalize(Words(value, ordinal: false)),
            NumberStyle.OrdinalText => Capitalize(Words(value, ordinal: true)),
            NumberStyle.None or NumberStyle.Bullet => "",
            _ => value.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static Dictionary<Paragraph, ListLabel> Compute(Document document)
    {
        Dictionary<Paragraph, ListLabel> labels = [];
        Counters main = new();
        foreach (Section section in document.Sections)
        {
            Number(section.Body, main, labels);
        }

        foreach (Section section in document.Sections)
        {
            foreach (HeaderFooter story in section.HeadersFooters)
            {
                Number(story, new Counters(), labels);
            }

            foreach (MarginBox box in section.MarginBoxes)
            {
                Number(box, new Counters(), labels);
            }
        }

        return labels;
    }

    private static void Number(Story story, Counters counters, Dictionary<Paragraph, ListLabel> labels)
    {
        foreach (Paragraph paragraph in story.Descendants<Paragraph>())
        {
            ResolvedParagraphFormat format = paragraph.GetEffectiveFormat();
            if (format.List is not { } list || !paragraph.Document.Lists.Contains(list))
            {
                continue;
            }

            int levelIndex = format.ListLevel;
            ListLevel level = list.GetLevel(levelIndex);
            int?[] values = counters.For(list.Definition);
            if (list.Overrides[levelIndex].StartAt is int start && counters.StartedOverrides.Add((list, levelIndex)))
            {
                values[levelIndex] = start - 1;
            }

            values[levelIndex] = (values[levelIndex] ?? (level.StartAt - 1)) + 1;
            for (int deeper = levelIndex + 1; deeper < 9; deeper++)
            {
                int? restart = list.GetLevel(deeper).RestartAfterLevel;
                if (restart is null || (restart >= 0 && levelIndex <= restart))
                {
                    values[deeper] = null;
                }
            }

            labels[paragraph] = new ListLabel(Text(list, level, values), list, levelIndex, level);
        }
    }

    private static string Text(List list, ListLevel level, int?[] values)
    {
        if (level.NumberStyle == NumberStyle.Bullet)
        {
            return level.LevelText;
        }

        StringBuilder text = new();
        string template = level.LevelText;
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '%' && i + 1 < template.Length && template[i + 1] is >= '1' and <= '9')
            {
                int index = template[i + 1] - '1';
                ListLevel referenced = list.GetLevel(index);
                int value = values[index] ?? referenced.StartAt;
                text.Append(Format(value, level.IsLegal && index < level.Index ? NumberStyle.Decimal : referenced.NumberStyle));
                i++;
            }
            else
            {
                text.Append(template[i]);
            }
        }

        return text.ToString();
    }

    private static string Roman(int value)
    {
        if (value is <= 0 or >= 4000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        (int Value, string Text)[] numerals = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        StringBuilder text = new();
        foreach ((int n, string s) in numerals)
        {
            while (value >= n)
            {
                text.Append(s);
                value -= n;
            }
        }

        return text.ToString();
    }

    /// <summary>A, B ... Z, then AA, BB ... ZZ, then AAA, as Word counts letters.</summary>
    private static string Letter(int value)
    {
        if (value <= 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        char letter = (char)('A' + ((value - 1) % 26));
        return new string(letter, Math.Min(((value - 1) / 26) + 1, 1000));
    }

    private static string OrdinalSuffix(int value)
    {
        int last2 = Math.Abs(value) % 100;
        return last2 is >= 11 and <= 13 ? "th" : (Math.Abs(value) % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
    }

    private static string Capitalize(string text)
    {
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>A number in English words, cardinal (twenty-one) or ordinal (twenty-first), as Word's text styles.</summary>
    private static string Words(int value, bool ordinal)
    {
        if (value is < 0 or > 999_999)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        string[] ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        string[] tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
        string Cardinal(int n)
        {
            if (n < 20)
            {
                return ones[n];
            }

            if (n < 100)
            {
                return tens[n / 10] + (n % 10 == 0 ? "" : "-" + ones[n % 10]);
            }

            if (n < 1000)
            {
                return ones[n / 100] + " hundred" + (n % 100 == 0 ? "" : " " + Cardinal(n % 100));
            }

            return Cardinal(n / 1000) + " thousand" + (n % 1000 == 0 ? "" : " " + Cardinal(n % 1000));
        }

        string words = Cardinal(value);
        if (!ordinal)
        {
            return words;
        }

        // The last word becomes ordinal: one to first, twenty to twentieth, and so on.
        int split = Math.Max(words.LastIndexOf(' '), words.LastIndexOf('-')) + 1;
        string head = words[..split], last = words[split..];
        string tail = last switch
        {
            "one" => "first",
            "two" => "second",
            "three" => "third",
            "five" => "fifth",
            "eight" => "eighth",
            "nine" => "ninth",
            "twelve" => "twelfth",
            _ when last.EndsWith('y') => last[..^1] + "ieth",
            _ => last + "th",
        };
        return head + tail;
    }

    private sealed class State
    {
        public long Version { get; set; } = -1;

        public Dictionary<Paragraph, ListLabel>? Labels { get; set; }
    }

    private sealed class Counters
    {
        private readonly Dictionary<ListDefinition, int?[]> values = [];

        public HashSet<(List, int)> StartedOverrides { get; } = [];

        public int?[] For(ListDefinition definition)
        {
            if (!values.TryGetValue(definition, out int?[]? counters))
            {
                counters = new int?[9];
                values[definition] = counters;
            }

            return counters;
        }
    }
}
