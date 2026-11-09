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
    /// <param name="sheets">The workbook's sheet names, to tell a name local to a sheet, written <c>Sheet.Name</c>, from a
    /// name with a dot in it.</param>
    /// <param name="books">The number of the linked book for another file and one of its sheets, as Excel's <c>[1]Sheet!A1</c>
    /// numbers it; without it, such a reference becomes <c>#REF!</c>.</param>
    public static string? ToExcel(string formula, out bool exact, IReadOnlySet<string>? sheets = null, Func<string, string, int>? books = null)
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

        return excel ? text.ToString() : Translate(text, ref exact, sheets, books);
    }

    private static string Translate(ReadOnlySpan<char> text, ref bool exact, IReadOnlySet<string>? sheets, Func<string, string, int>? books)
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
                        if (end > i + 1 && end + 1 < text.Length && text[end + 1] == '!' && text[(i + 1)..end].IndexOfAnyExceptInRange('0', '9') < 0)
                        {
                            // A name in a linked book, [1]!Rate, which office suites keep as Excel wrote it.
                            int open = i;
                            i = end + 2;
                            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '\\'))
                            {
                                i++;
                            }

                            output.Append(text[open..i]);
                            continue;
                        }

                        output.Append(Reference(text[(i + 1)..Math.Max(i + 1, end)], ref exact, books));
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
                        // An error constant: #N/A, #DIV/0!, #REF! and the rest, in capitals as Excel writes them (LibreOffice
                        // can write #ref!).
                        int end = i + 1;
                        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '/' or '_'))
                        {
                            end++;
                        }

                        if (end < text.Length && text[end] is '!' or '?')
                        {
                            end++;
                        }

                        for (; i < end; i++)
                        {
                            output.Append(char.ToUpperInvariant(text[i]));
                        }

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

                        // A name local to a sheet: Sheet.Name in OpenFormula, Sheet!Name in Excel.
                        int dot = word.IndexOf('.');
                        if (dot > 0 && dot < word.Length - 1 && sheets is not null && sheets.Contains(word[..dot]))
                        {
                            output.Append(SheetNames.Quoted(word[..dot])).Append('!').Append(word, dot + 1, word.Length - dot - 1);
                        }
                        else
                        {
                            output.Append(word);
                        }

                        i = end;
                        continue;
                    }

                    if (c == '\'' && sheets is not null && QuotedSheetName(text, i, sheets) is ({ } sheet, int nameStart, int nameEnd))
                    {
                        output.Append(SheetNames.Quoted(sheet)).Append('!').Append(text[nameStart..nameEnd]);
                        i = nameEnd;
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

    /// <summary>A quoted sheet name followed by a dot and a name, as in <c>'My sheet'.Rate</c>: the sheet and where the name lies.</summary>
    private static (string Sheet, int NameStart, int NameEnd)? QuotedSheetName(ReadOnlySpan<char> text, int at, IReadOnlySet<string> sheets)
    {
        StringBuilder sheet = new();
        int i = at + 1;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    sheet.Append('\'');
                    i += 2;
                    continue;
                }

                break;
            }

            sheet.Append(text[i++]);
        }

        int start = i + 2;
        if (i + 1 >= text.Length || text[i + 1] != '.' || start >= text.Length || !(char.IsLetter(text[start]) || text[start] == '_'))
        {
            return null;
        }

        int end = start;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '.'))
        {
            end++;
        }

        return sheets.Contains(sheet.ToString()) ? (sheet.ToString(), start, end) : null;
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
    private static string Reference(ReadOnlySpan<char> inside, ref bool exact, Func<string, string, int>? books = null)
    {
        inside = inside.Trim();
        if (inside.Length > 0 && inside[0] == '\'' && LooksExternal(inside, out int close))
        {
            // Another file's sheet: 'file:///c:/other.ods'#$Sheet1.A1, which Excel writes [1]Sheet1!A1 for its first linked book.
            string file = inside[1..close].ToString().Replace("''", "'", StringComparison.Ordinal);
            ReadOnlySpan<char> rest = inside[(close + 2)..];

            // A name defined in the other file: 'file:///c:/other.ods'#$$'Rates', Excel's [1]!Rates.
            if (rest is ['$', '$', _, ..] && books is not null)
            {
                string name = rest[2..].Trim().ToString();
                if (name is ['\'', .., '\''])
                {
                    name = name[1..^1].Replace("''", "'", StringComparison.Ordinal);
                }

                if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '\\'))
                {
                    return "[" + books(file, "").ToString(System.Globalization.CultureInfo.InvariantCulture) + "]!" + name;
                }
            }

            bool inner = true;
            string local = Reference(rest, ref inner);
            int bang = local.LastIndexOf('!');
            if (books is null || !inner || bang <= 0 || local.IndexOf(':') is int colon && colon >= 0 && colon < bang)
            {
                exact = false;
                return "#REF!";
            }

            string sheet = local[0] == '\'' ? local[1..(bang - 1)].Replace("''", "'", StringComparison.Ordinal) : local[..bang];
            string number = "[" + books(file, sheet).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

            // After the book's number a sheet named like a cell, such as Q1, needs no quotes; other names quote as they do.
            bool word = sheet.Length > 0 && (char.IsLetter(sheet[0]) || sheet[0] == '_') && sheet.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');
            return local[0] != '\'' ? number + local : word ? number + sheet + local[bang..] : "'" + number + local[1..];
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
    private static bool LooksExternal(ReadOnlySpan<char> inside, out int close)
    {
        int i = 1;
        while (i < inside.Length && !(inside[i] == '\'' && (i + 1 >= inside.Length || inside[i + 1] != '\'')))
        {
            i += inside[i] == '\'' ? 2 : 1;
        }

        close = i;
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
