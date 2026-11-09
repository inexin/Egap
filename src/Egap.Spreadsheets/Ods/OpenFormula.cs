using System.Text;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Ods;

/// <summary>
/// Translates OpenFormula (ODF 1.3 Part 4), as ODS files store formulas, into Excel's formula text: bracketed references
/// such as <c>[.A1:.B2]</c> and <c>[$'My sheet'.A1]</c> become <c>A1:B2</c> and <c>'My sheet'!A1</c>, semicolons
/// between arguments become commas, inline arrays take Excel's separators, the union and intersection operators become
/// Excel's, <c>TRUE()</c> and <c>FALSE()</c> become constants, and function names lose the namespaces ODF gives
/// Excel's newer and legacy functions.
/// </summary>
internal static class OpenFormula
{
    /// <summary>ODF names of functions whose Excel names differ, beyond the prefixes <see cref="FunctionName"/> drops.</summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FORMULA"] = "FORMULATEXT",
        ["ORG.OPENOFFICE.ERRORTYPE"] = "ERROR.TYPE",
        ["ERRORTYPE"] = "ERROR.TYPE",
    };

    /// <summary>
    /// Excel's text for a formula an ODS cell or name holds: with the <c>of:</c> namespace, no namespace, or Excel's own
    /// <c>msoxl:</c>, with or without the leading <c>=</c>. Null for a formula of another syntax.
    /// </summary>
    /// <param name="formula">The formula as the file holds it.</param>
    /// <param name="exact">False when a part had no Excel form and became <c>#REF!</c> or <c>#NAME?</c>: a reference to
    /// another file, or one Excel cannot write.</param>
    public static string? ToExcel(string formula, out bool exact)
    {
        exact = true;
        ReadOnlySpan<char> text = formula;
        int colon = text.IndexOf(':');
        bool excel = false;
        if (colon > 0 && colon < 12 && text[..colon].IndexOfAny("=[('\"") < 0)
        {
            string ns = formula[..colon];
            if (ns.Equals("msoxl", StringComparison.OrdinalIgnoreCase))
            {
                excel = true;
            }
            else if (!(ns.Equals("of", StringComparison.OrdinalIgnoreCase) || ns.Equals("oooc", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            text = text[(colon + 1)..];
        }

        text = text.TrimStart();
        if (text.StartsWith("="))
        {
            text = text[1..];
        }

        return excel ? text.ToString() : Translate(text, ref exact);
    }

    private static string Translate(ReadOnlySpan<char> text, ref bool exact)
    {
        StringBuilder output = new(text.Length);
        int arrayDepth = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            switch (c)
            {
                case '"':
                    {
                        int end = i + 1;
                        while (end < text.Length && !(text[end] == '"' && (end + 1 >= text.Length || text[end + 1] != '"')))
                        {
                            end += text[end] == '"' ? 2 : 1;
                        }

                        end = Math.Min(end + 1, text.Length);
                        output.Append(text[i..end]);
                        i = end;
                        continue;
                    }

                case '[':
                    {
                        int end = ReferenceEnd(text, i);
                        output.Append(Reference(text[(i + 1)..Math.Max(i + 1, end)], ref exact));
                        i = end + 1;
                        continue;
                    }

                case '{':
                    arrayDepth++;
                    output.Append(c);
                    break;
                case '}':
                    arrayDepth = Math.Max(0, arrayDepth - 1);
                    output.Append(c);
                    break;
                case ';':
                    output.Append(arrayDepth > 0 ? ',' : ',');
                    break;
                case '|':
                    output.Append(arrayDepth > 0 ? ';' : '|');
                    break;
                case '~':
                    output.Append(',');
                    break;
                case '!':
                    output.Append(' ');
                    break;
                case '#':
                    {
                        // An error constant, kept as it is: #N/A, #DIV/0!, #REF! and the rest.
                        int end = i + 1;
                        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '/' or '_'))
                        {
                            end++;
                        }

                        if (end < text.Length && text[end] is '!' or '?')
                        {
                            end++;
                        }

                        output.Append(text[i..end]);
                        i = end;
                        continue;
                    }

                default:
                    if (char.IsLetter(c) || c == '_' || c == '$')
                    {
                        int end = i;
                        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '.' or '$'))
                        {
                            end++;
                        }

                        int next = end;
                        while (next < text.Length && text[next] == ' ')
                        {
                            next++;
                        }

                        string word = text[i..end].ToString();
                        if (next < text.Length && text[next] == '(')
                        {
                            string name = FunctionName(word);
                            if (name is "TRUE" or "FALSE" && CloseFollows(text, next + 1, out int close))
                            {
                                output.Append(name);
                                i = close + 1;
                                continue;
                            }

                            output.Append(FutureFunctions.StoredName(name));
                            i = end;
                            continue;
                        }

                        output.Append(word);
                        i = end;
                        continue;
                    }

                    if (char.IsAsciiDigit(c) || c == '.')
                    {
                        // A number, its exponent's sign included, so that 1E-3 is not read as 1E minus 3.
                        int end = i;
                        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'
                            || ((text[end] is 'e' or 'E') && end + 1 < text.Length && (char.IsAsciiDigit(text[end + 1]) || text[end + 1] is '+' or '-'))
                            || (text[end] is '+' or '-' && end > i && text[end - 1] is 'e' or 'E')))
                        {
                            end++;
                        }

                        output.Append(text[i..Math.Max(end, i + 1)]);
                        i = Math.Max(end, i + 1);
                        continue;
                    }

                    output.Append(c);
                    break;
            }

            i++;
        }

        return output.ToString();
    }

    /// <summary>Excel's name for an ODF function name: without the <c>COM.MICROSOFT.</c> and <c>LEGACY.</c> prefixes, upper case.</summary>
    public static string FunctionName(string name)
    {
        string upper = name.ToUpperInvariant();
        if (Renamed.TryGetValue(upper, out string? renamed))
        {
            return renamed;
        }

        foreach (string prefix in (string[])["COM.MICROSOFT.", "LEGACY.", "_XLFN._XLWS.", "_XLFN."])
        {
            if (upper.StartsWith(prefix, StringComparison.Ordinal))
            {
                return upper[prefix.Length..];
            }
        }

        return upper;
    }

    private static bool CloseFollows(ReadOnlySpan<char> text, int at, out int close)
    {
        close = at;
        while (close < text.Length && text[close] == ' ')
        {
            close++;
        }

        return close < text.Length && text[close] == ')';
    }

    /// <summary>The index of the bracket that closes the reference opened at <paramref name="open"/>, past quoted names.</summary>
    private static int ReferenceEnd(ReadOnlySpan<char> text, int open)
    {
        int i = open + 1;
        while (i < text.Length && text[i] != ']')
        {
            if (text[i] == '\'')
            {
                i++;
                while (i < text.Length && !(text[i] == '\'' && (i + 1 >= text.Length || text[i + 1] != '\'')))
                {
                    i += text[i] == '\'' ? 2 : 1;
                }
            }

            i++;
        }

        return Math.Min(i, text.Length);
    }

    /// <summary>A bracketed reference's inside, such as <c>$Sheet1.A1:.B$2</c>, as Excel writes it.</summary>
    private static string Reference(ReadOnlySpan<char> inside, ref bool exact)
    {
        inside = inside.Trim();
        if (inside.Length > 0 && inside[0] == '\'' && LooksExternal(inside))
        {
            exact = false;
            return "#REF!";
        }

        if (inside.IndexOf("#REF!") >= 0)
        {
            return "#REF!";
        }

        int split = TopColon(inside);
        ReadOnlySpan<char> first = split < 0 ? inside : inside[..split];
        ReadOnlySpan<char> second = split < 0 ? [] : inside[(split + 1)..];
        if (!Part(first, out string? sheet1, out string cell1) || (split >= 0 && !Part(second, out _, out _)))
        {
            exact = false;
            return "#REF!";
        }

        string? sheet2 = null;
        string cell2 = "";
        if (split >= 0)
        {
            Part(second, out sheet2, out cell2);
        }

        string prefix = "";
        if (sheet1 is not null)
        {
            prefix = sheet2 is not null && sheet2 != sheet1
                ? (SheetNames.IsPlain(sheet1) && SheetNames.IsPlain(sheet2) ? sheet1 + ":" + sheet2 : "'" + (sheet1 + ":" + sheet2).Replace("'", "''", StringComparison.Ordinal) + "'") + "!"
                : SheetNames.Quoted(sheet1) + "!";
        }

        return split < 0 ? prefix + cell1 : prefix + cell1 + ":" + cell2;
    }

    /// <summary>Whether a reference that starts with a quote names another file: the quote is followed by <c>#</c>.</summary>
    private static bool LooksExternal(ReadOnlySpan<char> inside)
    {
        int i = 1;
        while (i < inside.Length && !(inside[i] == '\'' && (i + 1 >= inside.Length || inside[i + 1] != '\'')))
        {
            i += inside[i] == '\'' ? 2 : 1;
        }

        return i + 1 < inside.Length && inside[i + 1] == '#';
    }

    /// <summary>The colon that joins the two ends of a range, outside quoted sheet names; -1 for none.</summary>
    private static int TopColon(ReadOnlySpan<char> inside)
    {
        bool quoted = false;
        for (int i = 0; i < inside.Length; i++)
        {
            if (inside[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (inside[i] == ':' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>One end of a reference: an optional sheet (with <c>$</c>, perhaps quoted), a dot, and a cell, column or row.</summary>
    private static bool Part(ReadOnlySpan<char> part, out string? sheet, out string cell)
    {
        sheet = null;
        cell = "";
        part = part.Trim();
        int dot;
        if (part.Length > 0 && part[0] == '$')
        {
            part = part[1..];
        }

        if (part.Length > 0 && part[0] == '\'')
        {
            StringBuilder name = new();
            int i = 1;
            while (i < part.Length)
            {
                if (part[i] == '\'')
                {
                    if (i + 1 < part.Length && part[i + 1] == '\'')
                    {
                        name.Append('\'');
                        i += 2;
                        continue;
                    }

                    break;
                }

                name.Append(part[i]);
                i++;
            }

            sheet = name.ToString();
            dot = i + 1;
            if (dot >= part.Length || part[dot] != '.')
            {
                return false;
            }
        }
        else
        {
            dot = part.LastIndexOf('.');
            if (dot < 0)
            {
                return false;
            }

            if (dot > 0)
            {
                sheet = part[..dot].ToString();
            }
        }

        ReadOnlySpan<char> address = part[(dot + 1)..];
        if (address.Length == 0)
        {
            return false;
        }

        foreach (char ch in address)
        {
            if (!(char.IsAsciiLetter(ch) || char.IsAsciiDigit(ch) || ch == '$'))
            {
                return false;
            }
        }

        cell = address.ToString().ToUpperInvariant();
        return true;
    }
}
