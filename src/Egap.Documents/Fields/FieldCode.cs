using System.Globalization;
using System.Text;

namespace Egap.Documents;

/// <summary>
/// A field code taken apart as Word reads it: the instruction, its arguments (quotes removed) and its switches with their
/// values, such as <c>DATE \@ "d MMMM yyyy" \* MERGEFORMAT</c>.
/// </summary>
internal sealed class FieldCode
{
    private FieldCode(string instruction, List<string> arguments, List<(string Name, string? Value)> switches)
    {
        Instruction = instruction;
        Arguments = arguments;
        Switches = switches;
    }

    public string Instruction { get; }

    public List<string> Arguments { get; }

    public List<(string Name, string? Value)> Switches { get; }

    public static FieldCode Parse(string code)
    {
        List<string> tokens = Tokenize(code);
        string instruction = tokens.Count > 0 ? tokens[0].ToUpperInvariant() : "";
        List<string> arguments = [];
        List<(string, string?)> switches = [];
        for (int i = 1; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token.Length >= 2 && token[0] == '\\' && token[1] != '\\')
            {
                string name = token[..2].ToUpperInvariant();
                string? value = token.Length > 2 ? token[2..] : null;
                if (value is null && name is "\\@" or "\\#" or "\\*" or "\\R" or "\\S" or "\\O" or "\\T" or "\\L" or "\\B" or "\\F" or "\\C" && i + 1 < tokens.Count && !tokens[i + 1].StartsWith('\\'))
                {
                    value = tokens[++i];
                }

                switches.Add((name, value));
            }
            else
            {
                arguments.Add(token);
            }
        }

        return new FieldCode(instruction, arguments, switches);
    }

    public string? Switch(string name)
    {
        foreach ((string n, string? v) in Switches)
        {
            if (n == name)
            {
                return v ?? "";
            }
        }

        return null;
    }

    public bool Has(string name)
    {
        return Switch(name) is not null;
    }

    /// <summary>Applies the \* text and number format switches (other than MERGEFORMAT and CHARFORMAT) to a result.</summary>
    public string ApplyGeneralFormat(string text)
    {
        foreach ((string name, string? value) in Switches)
        {
            if (name != "\\*" || value is null)
            {
                continue;
            }

            switch (value.ToUpperInvariant())
            {
                case "UPPER":
                    text = text.ToUpperInvariant();
                    break;
                case "LOWER":
                    text = text.ToLowerInvariant();
                    break;
                case "FIRSTCAP":
                    text = text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
                    break;
                case "CAPS":
                    text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
                    break;
                default:
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && NumberStyleOf(value) is { } style)
                    {
                        text = Numbering.Format(number, style);
                    }

                    break;
            }
        }

        return text;
    }

    /// <summary>A number in the \# picture, such as "0.00" or "#,##0", in the invariant culture.</summary>
    public static string FormatNumber(double value, string picture)
    {
        try
        {
            return value.ToString(picture.Replace("'", "\"", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A date in Word's \@ picture, turned into .NET's format: am/pm to tt, quoted text kept.</summary>
    public static string FormatDate(DateTimeOffset value, string picture)
    {
        StringBuilder format = new();
        for (int i = 0; i < picture.Length; i++)
        {
            char c = picture[i];
            if (c == '\'')
            {
                int end = picture.IndexOf('\'', i + 1);
                end = end < 0 ? picture.Length : end;
                format.Append('"').Append(picture, i + 1, end - i - 1).Append('"');
                i = end;
            }
            else if (picture.AsSpan(i).StartsWith("am/pm", StringComparison.OrdinalIgnoreCase))
            {
                format.Append("tt");
                i += 4;
            }
            else if (c is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 'm' or 's' or 'S' or 'M')
            {
                format.Append(c switch { 'Y' => 'y', 'D' => 'd', 'S' => 's', _ => c });
            }
            else if (char.IsLetter(c))
            {
                format.Append('\\').Append(c);
            }
            else
            {
                format.Append(c is '/' or ':' ? "\\" + c : c.ToString());
            }
        }

        try
        {
            return value.ToString(format.ToString(), CultureInfo.GetCultureInfo("en-US"));
        }
        catch (FormatException)
        {
            return value.ToString("d", CultureInfo.GetCultureInfo("en-US"));
        }
    }

    private static NumberStyle? NumberStyleOf(string name)
    {
        return name switch
        {
            "ARABIC" or "Arabic" => NumberStyle.Decimal,
            "ROMAN" => NumberStyle.UpperRoman,
            "roman" => NumberStyle.LowerRoman,
            "ALPHABETIC" => NumberStyle.UpperLetter,
            "alphabetic" => NumberStyle.LowerLetter,
            "Ordinal" or "ORDINAL" => NumberStyle.Ordinal,
            "CardText" or "CARDTEXT" => NumberStyle.CardinalText,
            "OrdText" or "ORDTEXT" => NumberStyle.OrdinalText,
            _ => null,
        };
    }

    private static List<string> Tokenize(string code)
    {
        List<string> tokens = [];
        int i = 0;
        while (i < code.Length)
        {
            if (char.IsWhiteSpace(code[i]))
            {
                i++;
                continue;
            }

            StringBuilder token = new();
            if (code[i] == '"')
            {
                i++;
                while (i < code.Length && code[i] != '"')
                {
                    if (code[i] == '\\' && i + 1 < code.Length && code[i + 1] is '"' or '\\')
                    {
                        i++;
                    }

                    token.Append(code[i++]);
                }

                i++;
                tokens.Add(token.ToString());
                continue;
            }

            while (i < code.Length && !char.IsWhiteSpace(code[i]) && code[i] != '"')
            {
                token.Append(code[i++]);
            }

            tokens.Add(token.ToString());
        }

        return tokens;
    }
}
