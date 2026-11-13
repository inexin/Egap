using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// REGEXTEST, REGEXEXTRACT and REGEXREPLACE, from Microsoft's function reference: patterns in PCRE2 syntax, read by
/// <see cref="PcreRegex"/>. A pattern that is not valid gives #VALUE!, as does a match that runs too long.
/// </summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> RegexFunctions() =>
    [
        new("REGEXTEST", 2, 3, Kinds.Value, RegexTest),
        new("REGEXEXTRACT", 2, 4, Kinds.Value, RegexExtract),
        new("REGEXREPLACE", 3, 5, Kinds.Value, RegexReplace),
    ];

    /// <summary>The text and pattern of a REGEX function, and its case sensitivity: 0 (the default) to match case, 1 to ignore it.</summary>
    private static bool RegexArguments(EvalValue[] arguments, int caseAt, EvalContext context, out string text, out PcreRegex? regex, out EvalValue error)
    {
        regex = null;
        if (!Str(arguments[0], context, out text, out error) || !Str(arguments[1], context, out string pattern, out error))
        {
            return false;
        }

        if (!Given(arguments, caseAt, 0, context, out double sensitivity, out error))
        {
            return false;
        }

        sensitivity = Math.Truncate(sensitivity);
        if (sensitivity is not (0 or 1))
        {
            error = Error(CellError.Value);
            return false;
        }

        regex = PcreRegex.Get(pattern, ignoreCase: sensitivity == 1);
        if (regex is null)
        {
            error = Error(CellError.Value);
            return false;
        }

        return true;
    }

    private static EvalValue RegexTest(EvalValue[] arguments, EvalContext context)
    {
        if (!RegexArguments(arguments, 2, context, out string text, out PcreRegex? regex, out EvalValue error))
        {
            return error;
        }

        try
        {
            return EvalValue.FromBoolean(regex!.For(text).IsMatch(text));
        }
        catch (RegexMatchTimeoutException)
        {
            return Error(CellError.Value);
        }
    }

    /// <summary>REGEXEXTRACT: return mode 0 gives the first match, 1 every match in a row, 2 the first match's groups in a row.</summary>
    private static EvalValue RegexExtract(EvalValue[] arguments, EvalContext context)
    {
        if (!RegexArguments(arguments, 3, context, out string text, out PcreRegex? regex, out EvalValue error))
        {
            return error;
        }

        if (!Given(arguments, 2, 0, context, out double modeNumber, out error))
        {
            return error;
        }

        int mode = (int)Math.Clamp(Math.Truncate(modeNumber), -1, 3);
        try
        {
            switch (mode)
            {
                case 0:
                    {
                        Match match = regex!.For(text).Match(text);
                        return match.Success ? EvalValue.FromText(regex.Value(match, text)) : Error(CellError.NotAvailable);
                    }

                case 1:
                    {
                        List<Match> matches = [];
                        if (!regex!.TryMatches(text, matches))
                        {
                            return Error(CellError.Value);
                        }

                        return matches.Count == 0
                            ? Error(CellError.NotAvailable)
                            : Result(1, matches.Count, [.. matches.Select(m => EvalValue.FromText(regex.Value(m, text)))]);
                    }

                case 2:
                    {
                        Match match = regex!.For(text).Match(text);
                        if (!match.Success || regex.GroupCount == 0)
                        {
                            return Error(CellError.Value);
                        }

                        EvalValue[] groups = new EvalValue[regex.GroupCount];
                        for (int i = 0; i < groups.Length; i++)
                        {
                            Group group = match.Groups[i + 1];
                            groups[i] = group.Success ? EvalValue.FromText(group.Value) : Error(CellError.NotAvailable);
                        }

                        return Result(1, groups.Length, groups);
                    }

                default:
                    return Error(CellError.Value);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Error(CellError.Value);
        }
    }

    /// <summary>REGEXREPLACE: every match replaced, or with an occurrence only that one, counted from the end when negative.</summary>
    private static EvalValue RegexReplace(EvalValue[] arguments, EvalContext context)
    {
        if (!RegexArguments(arguments, 4, context, out string text, out PcreRegex? regex, out EvalValue error))
        {
            return error;
        }

        if (!Str(arguments[2], context, out string replacement, out error) || !Given(arguments, 3, 0, context, out double occurrenceNumber, out error))
        {
            return error;
        }

        double occurrence = Math.Truncate(occurrenceNumber);
        try
        {
            List<Match> matches = [];
            if (!regex!.TryMatches(text, matches))
            {
                return Error(CellError.Value);
            }

            int only = -1;
            if (occurrence != 0)
            {
                double at = occurrence > 0 ? occurrence - 1 : matches.Count + occurrence;
                if (at < 0 || at >= matches.Count)
                {
                    return EvalValue.FromText(text);
                }

                only = (int)at;
            }

            StringBuilder result = new(text.Length);
            int copied = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                if (only >= 0 && i != only)
                {
                    continue;
                }

                Match match = matches[i];
                int start = Math.Max(regex.Start(match), copied);
                result.Append(text, copied, start - copied);
                if (!Substitute(regex, match, text, replacement, result))
                {
                    return Error(CellError.Value);
                }

                copied = Math.Max(copied, match.Index + match.Length);
                if (result.Length > CellValue.MaxTextLength)
                {
                    return Error(CellError.Value);
                }
            }

            result.Append(text, copied, text.Length - copied);
            return EvalValue.FromText(result.ToString());
        }
        catch (RegexMatchTimeoutException)
        {
            return Error(CellError.Value);
        }
    }

    /// <summary>
    /// Writes a replacement for one match, as PCRE2's extended substitution reads it: <c>$n</c>, <c>${n}</c>, <c>$name</c>,
    /// <c>${name}</c>, <c>$&amp;</c>, <c>$`</c>, <c>$'</c>, <c>$_</c> and <c>$$</c>; <c>${n:-default}</c> and
    /// <c>${n:+set:unset}</c>; backslash escapes, <c>\n</c> for a group, and <c>\U \L \u \l \E</c> for case. False when the
    /// replacement names a group that does not exist or is not well formed.
    /// </summary>
    private static bool Substitute(PcreRegex regex, Match match, string text, string replacement, StringBuilder result)
    {
        CaseForcing forcing = new();
        return Expand(regex, match, text, replacement, 0, replacement.Length, result, ref forcing);
    }

    /// <summary>The case changes in force while a replacement is written.</summary>
    private struct CaseForcing
    {
        /// <summary>'U' or 'L' until <c>\E</c>, or none.</summary>
        public char Run;

        /// <summary>'u' or 'l' for the next character only, or none.</summary>
        public char Next;

        public readonly void Append(StringBuilder result, string text) => AppendForced(result, text, Run, Next);

        public void Wrote(string text)
        {
            if (text.Length > 0)
            {
                Next = '\0';
            }
        }

        private static void AppendForced(StringBuilder result, string text, char run, char next)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char mode = i == 0 && next != '\0' ? next : run;
                result.Append(mode switch
                {
                    'U' or 'u' => char.ToUpperInvariant(c),
                    'L' or 'l' => char.ToLowerInvariant(c),
                    _ => c,
                });
            }
        }
    }

    private static bool Expand(PcreRegex regex, Match match, string text, string replacement, int from, int to, StringBuilder result, ref CaseForcing forcing)
    {
        int i = from;
        while (i < to)
        {
            char c = replacement[i];
            if (c == '$')
            {
                if (!Dollar(regex, match, text, replacement, ref i, to, result, ref forcing))
                {
                    return false;
                }

                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= to)
                {
                    return false;
                }

                char e = replacement[i + 1];
                i += 2;
                switch (e)
                {
                    case 'U' or 'L':
                        forcing.Run = e;
                        forcing.Next = '\0';
                        continue;
                    case 'u' or 'l':
                        forcing.Next = e;
                        continue;
                    case 'E':
                        forcing.Run = '\0';
                        forcing.Next = '\0';
                        continue;
                }

                string? piece;
                if (char.IsAsciiDigit(e))
                {
                    int start = i - 1;
                    while (i < to && char.IsAsciiDigit(replacement[i]))
                    {
                        i++;
                    }

                    if (!int.TryParse(replacement.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || number > regex.GroupCount)
                    {
                        return false;
                    }

                    Group group = match.Groups[number];
                    piece = group.Success ? group.Value : "";
                }
                else
                {
                    piece = ReplacementEscape(e, replacement, ref i, to);
                    if (piece is null)
                    {
                        return false;
                    }
                }

                forcing.Append(result, piece);
                forcing.Wrote(piece);
                continue;
            }

            string literal = c.ToString();
            forcing.Append(result, literal);
            forcing.Wrote(literal);
            i++;
        }

        return true;
    }

    /// <summary>A backslash escape for one character in a replacement; null when it is not one PCRE2 knows.</summary>
    private static string? ReplacementEscape(char e, string replacement, ref int i, int to)
    {
        switch (e)
        {
            case 'a':
                return "\a";
            case 'e':
                return "\x1B";
            case 'f':
                return "\f";
            case 'n':
                return "\n";
            case 'r':
                return "\r";
            case 't':
                return "\t";
            case 'x':
                {
                    int code = 0;
                    if (i < to && replacement[i] == '{')
                    {
                        int close = replacement.IndexOf('}', i);
                        // Read as a long and checked for sign, so no count of digits wraps to a negative code; past U+10FFFF
                        // is #VALUE!, as in Excel.
                        if (close < 0 || close >= to
                            || !long.TryParse(replacement.AsSpan(i + 1, close - i - 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long wide)
                            || wide is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                        {
                            return null;
                        }

                        i = close + 1;
                        return char.ConvertFromUtf32((int)wide);
                    }

                    int digits = 0;
                    while (digits < 2 && i < to && char.IsAsciiHexDigit(replacement[i]))
                    {
                        code = (code * 16) + Convert.ToInt32(replacement[i].ToString(), 16);
                        i++;
                        digits++;
                    }

                    return ((char)code).ToString();
                }

            default:
                return char.IsAsciiLetterOrDigit(e) ? null : e.ToString();
        }
    }

    /// <summary>A <c>$</c> item in a replacement, at <paramref name="i"/>.</summary>
    private static bool Dollar(PcreRegex regex, Match match, string text, string replacement, ref int i, int to, StringBuilder result, ref CaseForcing forcing)
    {
        if (i + 1 >= to)
        {
            return false;
        }

        char c = replacement[i + 1];
        string piece;
        switch (c)
        {
            case '$':
                piece = "$";
                i += 2;
                break;
            case '&':
                piece = regex.Value(match, text);
                i += 2;
                break;
            case '`':
                piece = text[..regex.Start(match)];
                i += 2;
                break;
            case '\'':
                piece = text[(match.Index + match.Length)..];
                i += 2;
                break;
            case '_':
                piece = text;
                i += 2;
                break;
            case '*':
                if (string.CompareOrdinal(replacement, i + 2, "MARK", 0, 4) != 0)
                {
                    return false;
                }

                piece = "";
                i += 6;
                break;
            case '{':
                {
                    int close = FindClose(replacement, i + 2, to);
                    if (close < 0)
                    {
                        return false;
                    }

                    string inside = replacement[(i + 2)..close];
                    int colon = inside.IndexOf(':', StringComparison.Ordinal);
                    string name = colon < 0 ? inside : inside[..colon];
                    if (!GroupOf(regex, match, text, name, out bool set, out piece))
                    {
                        return false;
                    }

                    if (colon >= 0)
                    {
                        if (colon + 1 >= inside.Length)
                        {
                            return false;
                        }

                        int body = i + 2 + colon + 2;
                        if (inside[colon + 1] == '-')
                        {
                            i = close + 1;
                            if (set)
                            {
                                break;
                            }

                            return Expand(regex, match, text, replacement, body, close, result, ref forcing);
                        }

                        if (inside[colon + 1] != '+')
                        {
                            return false;
                        }

                        int split = FindSplit(replacement, body, close);
                        i = close + 1;
                        return split < 0
                            ? set && Expand(regex, match, text, replacement, body, close, result, ref forcing) || !set
                            : set
                                ? Expand(regex, match, text, replacement, body, split, result, ref forcing)
                                : Expand(regex, match, text, replacement, split + 1, close, result, ref forcing);
                    }

                    i = close + 1;
                    break;
                }

            default:
                {
                    int start = i + 1, end = start;
                    if (char.IsAsciiDigit(c))
                    {
                        while (end < to && char.IsAsciiDigit(replacement[end]))
                        {
                            end++;
                        }
                    }
                    else
                    {
                        while (end < to && (replacement[end] == '_' || char.IsLetterOrDigit(replacement[end])))
                        {
                            end++;
                        }
                    }

                    if (end == start || !GroupOf(regex, match, text, replacement[start..end], out _, out piece))
                    {
                        return false;
                    }

                    i = end;
                    break;
                }
        }

        forcing.Append(result, piece);
        forcing.Wrote(piece);
        return true;
    }

    /// <summary>A group by number or name: its text, or empty when it took no part in the match. False when there is no such group.</summary>
    private static bool GroupOf(PcreRegex regex, Match match, string text, string name, out bool set, out string value)
    {
        set = false;
        value = "";
        int number;
        if (name.Length > 0 && name.All(char.IsAsciiDigit))
        {
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number > regex.GroupCount)
            {
                return false;
            }

            if (number == 0)
            {
                set = true;
                value = regex.Value(match, text);
                return true;
            }
        }
        else if (!regex.Names.TryGetValue(name, out number))
        {
            return false;
        }

        Group group = match.Groups[number];
        set = group.Success;
        value = set ? group.Value : "";
        return true;
    }

    /// <summary>The <c>}</c> that closes a <c>${</c>, past any nested <c>${...}</c> and escapes.</summary>
    private static int FindClose(string replacement, int from, int to)
    {
        int depth = 0;
        for (int i = from; i < to; i++)
        {
            char c = replacement[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '$' && i + 1 < to && replacement[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
        }

        return -1;
    }

    /// <summary>The <c>:</c> between the two texts of <c>${n:+set:unset}</c>, or -1.</summary>
    private static int FindSplit(string replacement, int from, int to)
    {
        int depth = 0;
        for (int i = from; i < to; i++)
        {
            char c = replacement[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '$' && i + 1 < to && replacement[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == ':' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
