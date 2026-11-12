using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// A regular expression as Excel's REGEX functions read it: PCRE2 syntax in UTF and Unicode-property mode, with CR, LF and
/// CRLF as newlines. The pattern is rewritten to a .NET <see cref="Regex"/> that matches the same text: groups are numbered
/// left to right whether named or not, <c>.</c>, <c>^</c>, <c>$</c>, <c>\Z</c>, <c>\v</c>, <c>\h</c>, <c>\R</c>, <c>\w</c>,
/// POSIX classes, possessive quantifiers, <c>\Q...\E</c>, <c>\K</c>, <c>(?U)</c> and the other PCRE2 forms .NET does not have
/// are spelled in .NET terms, and a character beyond U+FFFF is one character. What .NET cannot do (recursion, subroutine
/// calls, branch reset, backtracking verbs other than <c>(*FAIL)</c>) makes the pattern invalid; see the function guide for
/// the differences that remain.
/// </summary>
internal sealed partial class PcreRegex
{
    /// <summary>How long one match may run before the function gives up; PCRE2 has a match limit in its place.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private const int MaxCached = 256;

    private const string KeepGroup = "egapKeep";

    private static readonly ConcurrentDictionary<(string Pattern, bool IgnoreCase), PcreRegex?> Cache = new();

    private readonly string translated;
    private readonly RegexOptions options;
    private Regex? nonEmpty;

    private PcreRegex(Regex regex, string translated, RegexOptions options, int groups, Dictionary<string, int> names, bool keeps)
    {
        Regex = regex;
        this.translated = translated;
        this.options = options;
        GroupCount = groups;
        Names = names;
        Keeps = keeps;
    }

    /// <summary>The .NET expression.</summary>
    public Regex Regex { get; }

    /// <summary>The capturing groups, as PCRE2 counts them.</summary>
    public int GroupCount { get; }

    /// <summary>The named groups and their numbers.</summary>
    public Dictionary<string, int> Names { get; }

    /// <summary>Whether the pattern has <c>\K</c>, which moves where a match starts.</summary>
    private bool Keeps { get; }

    /// <summary>The expression for a pattern, or null when the pattern is not valid; expressions are kept for the next call.</summary>
    public static PcreRegex? Get(string pattern, bool ignoreCase)
    {
        if (Cache.TryGetValue((pattern, ignoreCase), out PcreRegex? cached))
        {
            return cached;
        }

        PcreRegex? made = Make(pattern, ignoreCase);
        if (Cache.Count >= MaxCached)
        {
            Cache.Clear();
        }

        Cache[(pattern, ignoreCase)] = made;
        return made;
    }

    private static PcreRegex? Make(string pattern, bool ignoreCase)
    {
        Translator first = new(pattern, null);
        if (!first.Run())
        {
            return null;
        }

        Translator second = new(pattern, first);
        if (!second.Run())
        {
            return null;
        }

        RegexOptions options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        string text = second.Output;
        Regex regex;
        try
        {
            regex = new Regex(text, options, Timeout);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // The engine without backtracking finds the same matches in time linear in the text, where it can run the pattern.
        try
        {
            regex = new Regex(text, options | RegexOptions.NonBacktracking, Timeout);
        }
        catch (NotSupportedException)
        {
        }
        catch (ArgumentException)
        {
        }

        return new PcreRegex(regex, text, options, second.Groups, second.NameMap, second.Keeps);
    }

    /// <summary>Where a match starts once <c>\K</c> is counted.</summary>
    public int Start(Match match)
    {
        if (Keeps)
        {
            Group keep = match.Groups[KeepGroup];
            if (keep.Success)
            {
                return keep.Index;
            }
        }

        return match.Index;
    }

    /// <summary>The text a match stands for, from <see cref="Start"/> to its end.</summary>
    public string Value(Match match, string text)
    {
        int start = Start(match);
        int end = match.Index + match.Length;
        return start <= end ? text[start..end] : "";
    }

    /// <summary>
    /// Every match in order, as Excel finds them one after another: after an empty match the next must not be empty where
    /// that one was, and failing that the search moves on by one UTF-16 unit. False when that step lands inside a surrogate
    /// pair, where Excel gives #VALUE!.
    /// </summary>
    public bool TryMatches(string text, List<Match> matches)
    {
        int position = 0;
        bool notEmpty = false;
        while (position <= text.Length)
        {
            Match match = notEmpty ? NonEmpty.Match(text, position) : Regex.Match(text, position);
            if (!match.Success)
            {
                if (!notEmpty)
                {
                    return true;
                }

                notEmpty = false;
                position++;
                if (position < text.Length && char.IsLowSurrogate(text[position]) && char.IsHighSurrogate(text[position - 1]))
                {
                    return false;
                }

                continue;
            }

            matches.Add(match);
            if (matches.Count > EvalContext.MaxArrayCells)
            {
                return false;
            }

            position = match.Index + match.Length;
            notEmpty = match.Length == 0;
        }

        return true;
    }

    /// <summary>The expression anchored where the search starts and kept from matching nothing there, made when first needed.</summary>
    private Regex NonEmpty => nonEmpty ??= new Regex(@"\G(?:" + translated + @")(?!\G)", options, Timeout);

    /// <summary>
    /// Writes the PCRE2 pattern as a .NET one. It runs twice: the first time counts the groups and finds their names, which
    /// the second needs to tell a back reference from an octal escape and to number a reference to a group named later.
    /// </summary>
    private sealed class Translator(string pattern, Translator? counted)
    {
        /// <summary>The high surrogates, which a single unit never matches alone: they start a pair.</summary>
        private const string Highs = @"\uD800-\uDBFF";

        private const string HighLow = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";

        // \h and \v as PCRE2 has them; .NET's \v is the vertical tab alone.
        private static readonly string HorizontalItems = @"\t\x20\xA0" + U(0x1680) + U(0x180E) + U(0x2000) + "-" + U(0x200A) + U(0x202F) + U(0x205F) + U(0x3000);
        private static readonly string VerticalItems = @"\n\x0B\f\r\x85" + U(0x2028) + U(0x2029);

        // \w with Unicode properties, as PCRE2 10.43 and later have it: letters, numbers, non-spacing marks and connectors.
        private const string WordItems = @"\p{L}\p{N}\p{Mn}\p{Pc}";

        private readonly StringBuilder output = new();
        private readonly Stack<(Flags Flags, int Start, bool Lookbehind)> open = new();
        private int index;
        private Flags flags;
        private int lookbehinds;

        /// <summary>The flags PCRE2 has that this translator works out itself; <c>i</c> is left to .NET.</summary>
        [Flags]
        private enum Flags
        {
            None = 0,
            Multiline = 1,
            DotAll = 2,
            Extended = 4,
            NoAutoCapture = 8,
            Ungreedy = 16,
        }

        public string Output => output.ToString();

        public int Groups { get; private set; }

        public Dictionary<string, int> NameMap { get; } = new(StringComparer.Ordinal);

        public bool Keeps { get; private set; }

        private bool AtEnd => index >= pattern.Length;

        private char Current => pattern[index];

        public bool Run()
        {
            SkipStartVerbs();
            int atom = -1;
            while (!AtEnd)
            {
                if (flags.HasFlag(Flags.Extended) && SkipExtended())
                {
                    continue;
                }

                char c = Current;
                int before = output.Length;
                switch (c)
                {
                    case '\\':
                        index++;
                        if (AtEnd)
                        {
                            return false;
                        }

                        if (Current == 'Q')
                        {
                            index++;
                            if (!Quoted(ref atom))
                            {
                                return false;
                            }

                            continue;
                        }

                        if (Current == 'E')
                        {
                            index++;
                            continue;
                        }

                        if (!Escape(out bool repeatable))
                        {
                            return false;
                        }

                        atom = repeatable ? before : -1;
                        continue;
                    case '[':
                        index++;
                        if (!Class())
                        {
                            return false;
                        }

                        atom = before;
                        continue;
                    case '(':
                        index++;
                        if (!OpenGroup(ref atom))
                        {
                            return false;
                        }

                        continue;
                    case ')':
                        index++;
                        if (open.Count == 0)
                        {
                            return false;
                        }

                        (Flags outer, int start, bool lookbehind) = open.Pop();
                        lookbehinds -= lookbehind ? 1 : 0;
                        flags = outer;
                        output.Append(')');
                        atom = start;
                        continue;
                    case '|':
                        index++;
                        output.Append('|');
                        atom = -1;
                        continue;
                    case '.':
                        index++;
                        output.Append(flags.HasFlag(Flags.DotAll) ? "(?:" + HighLow + "|[^" + Highs + "])" : "(?:" + HighLow + @"|[^\r\n" + Highs + "])");
                        atom = before;
                        continue;
                    case '^':
                        index++;
                        output.Append(flags.HasFlag(Flags.Multiline) ? @"(?:(?<![^\r\n])(?<!\r(?=\n))(?:\A|(?!\z)))" : @"\A");
                        atom = -1;
                        continue;
                    case '$':
                        index++;
                        output.Append(flags.HasFlag(Flags.Multiline) ? @"(?:(?=[\r\n]|\z)(?<!\r(?=\n)))" : @"(?=(?:\r\n|\r|\n)?\z)");
                        atom = -1;
                        continue;
                    case '*' or '+' or '?':
                        if (atom < 0)
                        {
                            return false;
                        }

                        index++;
                        if (!Quantifier(c.ToString(), atom))
                        {
                            return false;
                        }

                        atom = -1;
                        continue;
                    case '{':
                        if (Repeat(out string? repeat, out bool bad))
                        {
                            if (atom < 0)
                            {
                                return false;
                            }

                            if (!Quantifier(repeat!, atom))
                            {
                                return false;
                            }

                            atom = -1;
                            continue;
                        }

                        if (bad)
                        {
                            return false;
                        }

                        index++;
                        Literal('{');
                        atom = before;
                        continue;
                    default:
                        int code = CodeAt(pattern, index);
                        index += code > 0xFFFF ? 2 : 1;
                        Literal(code);
                        atom = before;
                        continue;
                }
            }

            return open.Count == 0;
        }

        /// <summary>The verbs that can start a pattern and set how it is compiled; Excel's settings already are UTF and Unicode properties.</summary>
        private void SkipStartVerbs()
        {
            while (pattern.AsSpan(index).StartsWith("(*", StringComparison.Ordinal))
            {
                int close = pattern.IndexOf(')', index);
                if (close < 0)
                {
                    return;
                }

                string verb = pattern[(index + 2)..close];
                if (verb is "UTF" or "UTF8" or "UCP" or "CR" or "LF" or "CRLF" or "ANYCRLF" or "ANY" or "NUL" or "BSR_ANYCRLF" or "BSR_UNICODE"
                    or "NO_AUTO_POSSESS" or "NO_START_OPT" or "NO_DOTSTAR_ANCHOR" or "NO_JIT" or "NOTEMPTY" or "NOTEMPTY_ATSTART"
                    || verb.StartsWith("LIMIT_", StringComparison.Ordinal))
                {
                    index = close + 1;
                    continue;
                }

                return;
            }
        }

        /// <summary>White space and comments, which extended mode leaves out of the pattern.</summary>
        private bool SkipExtended()
        {
            char c = Current;
            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v' || c == '\x85' || c == (char)0x2028 || c == (char)0x2029 || c == (char)0x200E || c == (char)0x200F)
            {
                index++;
                return true;
            }

            if (c == '#')
            {
                while (!AtEnd && Current is not ('\n' or '\r'))
                {
                    index++;
                }

                return true;
            }

            return false;
        }

        /// <summary>The text from <c>\Q</c> to <c>\E</c>, each character as itself.</summary>
        private bool Quoted(ref int atom)
        {
            while (!AtEnd)
            {
                if (Current == '\\' && index + 1 < pattern.Length && pattern[index + 1] == 'E')
                {
                    index += 2;
                    return true;
                }

                atom = output.Length;
                int code = CodeAt(pattern, index);
                index += code > 0xFFFF ? 2 : 1;
                Literal(code);
            }

            return true;
        }

        /// <summary>A quantifier after the atom that starts at <paramref name="atom"/> in the output, with its lazy or possessive mark.</summary>
        private bool Quantifier(string quantifier, int atom)
        {
            // PCRE2 needs a lookbehind to have a longest length.
            if (lookbehinds > 0 && (quantifier is "*" or "+" || quantifier.EndsWith(",}", StringComparison.Ordinal)))
            {
                return false;
            }

            bool lazy = false, possessive = false;
            if (!AtEnd && Current == '?')
            {
                lazy = true;
                index++;
            }
            else if (!AtEnd && Current == '+')
            {
                possessive = true;
                index++;
            }

            if (flags.HasFlag(Flags.Ungreedy) && !possessive)
            {
                lazy = !lazy;
            }

            if (possessive)
            {
                output.Insert(atom, "(?>");
                output.Append(quantifier).Append(')');
                return true;
            }

            output.Append(quantifier);
            if (lazy)
            {
                output.Append('?');
            }

            return true;
        }

        /// <summary>
        /// A counted repeat at the current <c>{</c>: <c>{n}</c>, <c>{n,}</c>, <c>{n,m}</c> or <c>{,m}</c>, spaces allowed
        /// around the numbers. False when the brace starts no repeat and is a literal; <paramref name="bad"/> when it is a
        /// repeat PCRE2 refuses (a count over 65535, or the counts out of order).
        /// </summary>
        private bool Repeat(out string? repeat, out bool bad)
        {
            repeat = null;
            bad = false;
            int close = pattern.IndexOf('}', index);
            if (close < 0)
            {
                return false;
            }

            string inside = pattern[(index + 1)..close].Replace(" ", "", StringComparison.Ordinal);
            Match form = RepeatForm().Match(inside);
            if (!form.Success || (form.Groups[1].Length == 0 && form.Groups[3].Length == 0))
            {
                return false;
            }

            if (!form.Groups[2].Success && form.Groups[1].Length == 0)
            {
                return false;
            }

            if (!ReadCount(form.Groups[1].Value, out int low) || !ReadCount(form.Groups[3].Value, out int high))
            {
                bad = true;
                return false;
            }

            index = close + 1;
            if (!form.Groups[2].Success)
            {
                repeat = "{" + low.ToString(CultureInfo.InvariantCulture) + "}";
                return true;
            }

            if (form.Groups[3].Length == 0)
            {
                repeat = "{" + low.ToString(CultureInfo.InvariantCulture) + ",}";
                return true;
            }

            if (high < low)
            {
                bad = true;
                return false;
            }

            repeat = "{" + low.ToString(CultureInfo.InvariantCulture) + "," + high.ToString(CultureInfo.InvariantCulture) + "}";
            return true;
        }

        private static bool ReadCount(string digits, out int count)
        {
            count = 0;
            if (digits.Length == 0)
            {
                return true;
            }

            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count <= 65535;
        }

        /// <summary>A group, an assertion, an option setting or a comment, after its <c>(</c>.</summary>
        private bool OpenGroup(ref int atom)
        {
            int start = output.Length;
            if (AtEnd)
            {
                return false;
            }

            if (Current == '*')
            {
                return Verb(ref atom);
            }

            if (Current != '?')
            {
                if (flags.HasFlag(Flags.NoAutoCapture))
                {
                    Push("(?:", start, capturing: false);
                }
                else
                {
                    Groups++;
                    Push("(", start, capturing: true);
                }

                atom = -1;
                return true;
            }

            index++;
            if (AtEnd)
            {
                return false;
            }

            char c = Current;
            switch (c)
            {
                case '#':
                    int close = pattern.IndexOf(')', index);
                    if (close < 0)
                    {
                        return false;
                    }

                    index = close + 1;
                    return true;
                case ':':
                    index++;
                    Push("(?:", start, capturing: false);
                    atom = -1;
                    return true;
                case '>':
                    index++;
                    Push("(?>", start, capturing: false);
                    atom = -1;
                    return true;
                case '=' or '!':
                    index++;
                    Push("(?" + c, start, capturing: false);
                    atom = -1;
                    return true;
                case '<' when index + 1 < pattern.Length && pattern[index + 1] is '=' or '!':
                    Push("(?<" + pattern[index + 1], start, capturing: false);
                    index += 2;
                    atom = -1;
                    return true;
                case '<' or '\'':
                    index++;
                    return NamedGroup(c == '<' ? '>' : '\'', start, ref atom);
                case 'P':
                    index++;
                    if (AtEnd)
                    {
                        return false;
                    }

                    if (Current == '<')
                    {
                        index++;
                        return NamedGroup('>', start, ref atom);
                    }

                    if (Current == '=')
                    {
                        index++;
                        int end = pattern.IndexOf(')', index);
                        if (end < 0)
                        {
                            return false;
                        }

                        string name = pattern[index..end];
                        index = end + 1;
                        atom = output.Length;
                        return BackReferenceByName(name);
                    }

                    return false;
                case '(':
                    return Condition(start, ref atom);
                case '|' or 'R' or '&' or '+':
                    return false;
                default:
                    if (char.IsAsciiDigit(c) || c == '-' && index + 1 < pattern.Length && char.IsAsciiDigit(pattern[index + 1]))
                    {
                        return false;
                    }

                    return Options(start, ref atom);
            }
        }

        private void Push(string text, int start, bool capturing)
        {
            _ = capturing;
            bool lookbehind = text is "(?<=" or "(?<!";
            lookbehinds += lookbehind ? 1 : 0;
            open.Push((flags, start, lookbehind));
            output.Append(text);
        }

        /// <summary>A group named <c>(?&lt;name&gt;</c>, <c>(?'name'</c> or <c>(?P&lt;name&gt;</c>: a numbered group to .NET.</summary>
        private bool NamedGroup(char end, int start, ref int atom)
        {
            int close = pattern.IndexOf(end, index);
            if (close < 0)
            {
                return false;
            }

            string name = pattern[index..close];
            if (!IsName(name))
            {
                return false;
            }

            index = close + 1;
            Groups++;
            if (!NameMap.ContainsKey(name))
            {
                NameMap[name] = Groups;
            }

            Push("(", start, capturing: true);
            atom = -1;
            return true;
        }

        private static bool IsName(string name) =>
            name.Length is > 0 and <= 128 && !char.IsAsciiDigit(name[0]) && name.All(ch => ch == '_' || char.IsLetterOrDigit(ch));

        /// <summary>A conditional group: on a group's number or name, or on an assertion.</summary>
        private bool Condition(int start, ref int atom)
        {
            index++;
            if (AtEnd)
            {
                return false;
            }

            if (Current == '?')
            {
                // An assertion: .NET reads (?(?=...)yes|no) as PCRE2 does.
                open.Push((flags, start, false));
                output.Append("(?");
                atom = -1;
                return OpenAssertion();
            }

            int close = pattern.IndexOf(')', index);
            if (close < 0)
            {
                return false;
            }

            string condition = pattern[index..close];
            index = close + 1;
            int number;
            if (condition.Length > 0 && condition.All(char.IsAsciiDigit))
            {
                if (!int.TryParse(condition, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                {
                    return false;
                }
            }
            else
            {
                string name = condition.StartsWith('<') && condition.EndsWith('>') || condition.StartsWith('\'') && condition.EndsWith('\'')
                    ? condition[1..^1]
                    : condition;
                if (name is "DEFINE" || name.StartsWith('R') && (name.Length == 1 || name[1] == '&' || char.IsAsciiDigit(name[1])) && !NameMap.ContainsKey(name))
                {
                    return false;
                }

                if (counted is null)
                {
                    number = 1;
                }
                else if (!counted.NameMap.TryGetValue(name, out number))
                {
                    return false;
                }
            }

            if (counted is not null && (number < 1 || number > counted.Groups))
            {
                return false;
            }

            open.Push((flags, start, false));
            output.Append("(?(").Append(number.ToString(CultureInfo.InvariantCulture)).Append(')');
            atom = -1;
            return true;
        }

        /// <summary>The assertion that is a condition, after <c>(?(</c>: it is written as a group of its own.</summary>
        private bool OpenAssertion()
        {
            index++;
            if (AtEnd)
            {
                return false;
            }

            if (Current is '=' or '!')
            {
                Push("(?" + Current, output.Length, capturing: false);
                index++;
                return true;
            }

            if (Current == '<' && index + 1 < pattern.Length && pattern[index + 1] is '=' or '!')
            {
                Push("(?<" + pattern[index + 1], output.Length, capturing: false);
                index += 2;
                return true;
            }

            return false;
        }

        /// <summary>Option letters: <c>(?imnsxU-imnsxU)</c> for the rest of the group, or <c>(?...:</c> for a group of its own.</summary>
        private bool Options(int start, ref int atom)
        {
            Flags set = flags;
            bool on = true;
            string caseLetters = "";
            if (Current == '^')
            {
                set &= ~(Flags.Multiline | Flags.DotAll | Flags.Extended | Flags.NoAutoCapture);
                caseLetters = "-i";
                index++;
            }

            while (!AtEnd && Current is not (')' or ':'))
            {
                char letter = Current;
                index++;
                Flags flag;
                switch (letter)
                {
                    case '-':
                        if (!on)
                        {
                            return false;
                        }

                        on = false;
                        continue;
                    case 'i':
                        caseLetters = on ? "i" : "-i";
                        continue;
                    case 'J':
                        continue;
                    case 'm':
                        flag = Flags.Multiline;
                        break;
                    case 's':
                        flag = Flags.DotAll;
                        break;
                    case 'x':
                        flag = Flags.Extended;
                        if (!AtEnd && Current == 'x')
                        {
                            index++;
                        }

                        break;
                    case 'n':
                        flag = Flags.NoAutoCapture;
                        break;
                    case 'U':
                        flag = Flags.Ungreedy;
                        break;
                    default:
                        return false;
                }

                set = on ? set | flag : set & ~flag;
            }

            if (AtEnd)
            {
                return false;
            }

            bool scoped = Current == ':';
            index++;
            if (scoped)
            {
                open.Push((flags, start, false));
                flags = set;
                output.Append("(?").Append(caseLetters).Append(':');
            }
            else
            {
                flags = set;
                if (caseLetters.Length > 0)
                {
                    output.Append("(?").Append(caseLetters).Append(')');
                }
            }

            atom = -1;
            return true;
        }

        /// <summary>A verb in the pattern: <c>(*FAIL)</c>, a mark, or one of the alphabetic assertion names.</summary>
        private bool Verb(ref int atom)
        {
            int start = output.Length;
            int close = pattern.IndexOfAny([')', ':'], index);
            if (close < 0)
            {
                return false;
            }

            string name = pattern[(index + 1)..close];
            if (pattern[close] == ':')
            {
                string? group = name switch
                {
                    "pla" or "positive_lookahead" => "(?=",
                    "nla" or "negative_lookahead" => "(?!",
                    "plb" or "positive_lookbehind" => "(?<=",
                    "nlb" or "negative_lookbehind" => "(?<!",
                    "atomic" => "(?>",
                    _ => null,
                };
                if (group is not null)
                {
                    index = close + 1;
                    Push(group, start, capturing: false);
                    atom = -1;
                    return true;
                }

                if (name is "MARK" or "")
                {
                    int end = pattern.IndexOf(')', close);
                    if (end < 0)
                    {
                        return false;
                    }

                    index = end + 1;
                    return true;
                }

                return false;
            }

            index = close + 1;
            switch (name)
            {
                case "FAIL" or "F":
                    output.Append("(?!)");
                    atom = -1;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>An escape outside a class, after its backslash; <paramref name="repeatable"/> when a quantifier may follow it.</summary>
        private bool Escape(out bool repeatable)
        {
            repeatable = true;
            char c = Current;
            index++;
            switch (c)
            {
                case 'd':
                    output.Append(@"\d");
                    return true;
                case 'D':
                    output.Append(@"\D");
                    return true;
                case 's':
                    output.Append(@"\s");
                    return true;
                case 'S':
                    output.Append(@"\S");
                    return true;
                case 'w':
                    output.Append('[').Append(WordItems).Append(']');
                    return true;
                case 'W':
                    output.Append("[^").Append(WordItems).Append(']');
                    return true;
                case 'h':
                    output.Append('[').Append(HorizontalItems).Append(']');
                    return true;
                case 'H':
                    output.Append("(?:" + HighLow + "|[^" + Highs).Append(HorizontalItems).Append("])");
                    return true;
                case 'v':
                    output.Append('[').Append(VerticalItems).Append(']');
                    return true;
                case 'V':
                    output.Append("(?:" + HighLow + "|[^" + Highs).Append(VerticalItems).Append("])");
                    return true;
                case 'R':
                    output.Append(@"(?:\r\n|[").Append(VerticalItems).Append("])");
                    return true;
                case 'N':
                    if (!AtEnd && Current == '{')
                    {
                        return false;
                    }

                    output.Append("(?:" + HighLow + @"|[^\r\n" + Highs + "])");
                    return true;
                case 'X':
                    output.Append(@"(?:\r\n|(?:" + HighLow + "|[^" + Highs + @"])\p{M}*)");
                    return true;
                case 'b' or 'B' or 'A' or 'z' or 'G':
                    output.Append('\\').Append(c);
                    repeatable = false;
                    return true;
                case 'Z':
                    output.Append(@"(?=(?:\r\n|\r|\n)?\z)");
                    repeatable = false;
                    return true;
                case 'K':
                    Keeps = true;
                    output.Append("(?<" + KeepGroup + ">)");
                    repeatable = false;
                    return true;
                case 'p' or 'P':
                    return Property(c == 'P', inClass: false);
                case 'g':
                    return GroupReference();
                case 'k':
                    return NamedReference();
                case 'C' or 'L' or 'l' or 'U' or 'u':
                    return false;
                default:
                    if (char.IsAsciiDigit(c))
                    {
                        index--;
                        return Digits(inClass: false);
                    }

                    if (!CharacterEscape(c, out int code))
                    {
                        return false;
                    }

                    Literal(code);
                    return true;
            }
        }

        /// <summary>
        /// The escapes that stand for one character: <c>\a \e \f \n \r \t</c>, <c>\cX</c>, <c>\xhh</c>, <c>\x{...}</c>,
        /// <c>\o{...}</c>, and a backslash before a character that is not a letter or digit.
        /// </summary>
        private bool CharacterEscape(char c, out int code)
        {
            code = 0;
            switch (c)
            {
                case 'a':
                    code = 7;
                    return true;
                case 'e':
                    code = 27;
                    return true;
                case 'f':
                    code = 12;
                    return true;
                case 'n':
                    code = 10;
                    return true;
                case 'r':
                    code = 13;
                    return true;
                case 't':
                    code = 9;
                    return true;
                case 'c':
                    if (AtEnd || Current > 127)
                    {
                        return false;
                    }

                    code = char.ToUpperInvariant(Current) ^ 0x40;
                    index++;
                    return true;
                case 'x':
                    if (!AtEnd && Current == '{')
                    {
                        return Braced(16, out code);
                    }

                    int digits = 0;
                    while (digits < 2 && !AtEnd && char.IsAsciiHexDigit(Current))
                    {
                        code = (code * 16) + Convert.ToInt32(Current.ToString(), 16);
                        index++;
                        digits++;
                    }

                    return true;
                case 'o':
                    return !AtEnd && Current == '{' && Braced(8, out code);
                default:
                    if (char.IsAsciiLetterOrDigit(c))
                    {
                        return false;
                    }

                    code = c;
                    if (char.IsHighSurrogate(c) && !AtEnd && char.IsLowSurrogate(Current))
                    {
                        code = char.ConvertToUtf32(c, Current);
                        index++;
                    }

                    return true;
            }
        }

        /// <summary>A code point in braces, <c>{...}</c>, in hexadecimal or octal.</summary>
        private bool Braced(int radix, out int code)
        {
            code = 0;
            int close = pattern.IndexOf('}', index);
            if (close < 0 || close == index + 1)
            {
                return false;
            }

            string digits = pattern[(index + 1)..close].Trim();
            index = close + 1;
            long value = 0;
            foreach (char d in digits)
            {
                int digit = radix == 16 ? (char.IsAsciiHexDigit(d) ? Convert.ToInt32(d.ToString(), 16) : -1) : (d is >= '0' and <= '7' ? d - '0' : -1);
                if (digit < 0)
                {
                    return false;
                }

                value = (value * radix) + digit;
                if (value > 0x10FFFF)
                {
                    return false;
                }
            }

            if (value is >= 0xD800 and <= 0xDFFF)
            {
                return false;
            }

            code = (int)value;
            return true;
        }

        /// <summary>
        /// A backslash and digits: a back reference when under 10 or no more than the groups, otherwise up to three octal
        /// digits; <c>\0</c> starts octal always.
        /// </summary>
        private bool Digits(bool inClass)
        {
            int start = index;
            while (!AtEnd && char.IsAsciiDigit(Current))
            {
                index++;
            }

            string digits = pattern[start..index];
            if (!inClass && digits[0] != '0' && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && (number < 10 || number <= (counted?.Groups ?? int.MaxValue)))
            {
                return BackReference(number);
            }

            index = start;
            if (pattern[index] is '8' or '9')
            {
                return false;
            }

            int code = 0, count = 0;
            while (count < 3 && !AtEnd && Current is >= '0' and <= '7')
            {
                code = (code * 8) + (Current - '0');
                index++;
                count++;
            }

            Literal(code);
            return true;
        }

        private bool BackReference(int number)
        {
            if (counted is not null && (number < 1 || number > counted.Groups))
            {
                return false;
            }

            output.Append(@"\k<").Append(number.ToString(CultureInfo.InvariantCulture)).Append('>');
            return true;
        }

        private bool BackReferenceByName(string name)
        {
            if (counted is null)
            {
                output.Append(@"\k<1>");
                return IsName(name);
            }

            return counted.NameMap.TryGetValue(name, out int number) && BackReference(number);
        }

        /// <summary><c>\g</c>: a back reference by number, relative number or name; <c>\g&lt;...&gt;</c> calls a subroutine, which .NET cannot.</summary>
        private bool GroupReference()
        {
            if (AtEnd)
            {
                return false;
            }

            string text;
            if (Current == '{')
            {
                int close = pattern.IndexOf('}', index);
                if (close < 0)
                {
                    return false;
                }

                text = pattern[(index + 1)..close];
                index = close + 1;
            }
            else
            {
                int start = index;
                if (Current is '-' or '+')
                {
                    index++;
                }

                while (!AtEnd && char.IsAsciiDigit(Current))
                {
                    index++;
                }

                text = pattern[start..index];
            }

            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
            {
                if (text.StartsWith('+'))
                {
                    return false;
                }

                if (number < 0)
                {
                    number = Groups + 1 + number;
                }

                return number >= 1 && BackReference(number);
            }

            return BackReferenceByName(text);
        }

        /// <summary><c>\k&lt;name&gt;</c>, <c>\k'name'</c> or <c>\k{name}</c>.</summary>
        private bool NamedReference()
        {
            if (AtEnd)
            {
                return false;
            }

            char end = Current switch
            {
                '<' => '>',
                '\'' => '\'',
                '{' => '}',
                _ => '\0',
            };
            int close = end == '\0' ? -1 : pattern.IndexOf(end, index + 1);
            if (close < 0)
            {
                return false;
            }

            string name = pattern[(index + 1)..close];
            index = close + 1;
            return BackReferenceByName(name);
        }

        /// <summary>
        /// A character class, after its <c>[</c>. Excel has PCRE2's extended classes: a class may hold classes, and
        /// <c>||</c>, <c>&amp;&amp;</c>, <c>--</c> and <c>~~</c> take the union, intersection, difference and symmetric difference of what is
        /// on either side (one kind of operator per class, unless in brackets).
        /// </summary>
        private bool Class()
        {
            if (!ClassExpression(out ClassNode? node))
            {
                return false;
            }

            output.Append(Render(node!));
            return true;
        }

        /// <summary>What a class matches: items, a class it negates, a union, or an operator on two.</summary>
        private abstract class ClassNode;

        private sealed class ItemsNode : ClassNode
        {
            public StringBuilder Items { get; } = new();

            public List<string> Wide { get; } = [];
        }

        private sealed class NotNode(ClassNode inner) : ClassNode
        {
            public ClassNode Inner { get; } = inner;
        }

        private sealed class UnionNode(List<ClassNode> parts) : ClassNode
        {
            public List<ClassNode> Parts { get; } = parts;
        }

        private sealed class OperatorNode(char op, ClassNode left, ClassNode right) : ClassNode
        {
            public char Operator { get; } = op;

            public ClassNode Left { get; } = left;

            public ClassNode Right { get; } = right;
        }

        private const string AnyCharacter = "(?:" + HighLow + "|[^" + Highs + "])";

        /// <summary>A class from after its <c>[</c> to its <c>]</c>.</summary>
        private bool ClassExpression(out ClassNode? node)
        {
            node = null;
            bool negated = false;
            if (!AtEnd && Current == '^')
            {
                negated = true;
                index++;
            }

            if (!ClassOperand(first: true, out ClassNode? left))
            {
                return false;
            }

            char op = '\0';
            while (true)
            {
                if (AtEnd)
                {
                    return false;
                }

                if (Current == ']')
                {
                    index++;
                    break;
                }

                char next = Current;
                index += 2;
                if (op != '\0' && next != op)
                {
                    return false;
                }

                op = next;
                if (!ClassOperand(first: false, out ClassNode? right))
                {
                    return false;
                }

                left = new OperatorNode(op, left!, right!);
            }

            node = negated ? new NotNode(left!) : left;
            return true;
        }

        private bool AtOperator => index + 1 < pattern.Length && Current is '&' or '-' or '~' or '|' && pattern[index + 1] == Current;

        /// <summary>The items between a class's start or an operator and the next operator or the end: their union. None at all is an error.</summary>
        private bool ClassOperand(bool first, out ClassNode? node)
        {
            node = null;
            ItemsNode items = new();
            List<ClassNode> parts = [items];
            int previous = -1;
            bool any = false;
            bool firstItem = first;
            while (true)
            {
                if (AtEnd)
                {
                    return false;
                }

                char c = Current;
                if (c == ']' && !firstItem)
                {
                    break;
                }

                if (AtOperator)
                {
                    break;
                }

                firstItem = false;
                any = true;
                if (c == '[')
                {
                    if (index + 1 < pattern.Length && pattern[index + 1] == ':')
                    {
                        int close = pattern.IndexOf(":]", index + 2, StringComparison.Ordinal);
                        if (close >= 0)
                        {
                            string name = pattern[(index + 2)..close];
                            index = close + 2;
                            if (!Posix(name, items.Items))
                            {
                                return false;
                            }

                            previous = -1;
                            continue;
                        }
                    }

                    index++;
                    if (!ClassExpression(out ClassNode? nested))
                    {
                        return false;
                    }

                    parts.Add(nested!);
                    previous = -1;
                    continue;
                }

                int code;
                if (c == '\\')
                {
                    index++;
                    if (AtEnd)
                    {
                        return false;
                    }

                    char e = Current;
                    index++;
                    switch (e)
                    {
                        case 'd' or 'D' or 's' or 'S':
                            items.Items.Append('\\').Append(e);
                            previous = -1;
                            continue;
                        case 'w':
                            items.Items.Append(WordItems);
                            previous = -1;
                            continue;
                        case 'W':
                            items.Items.Append(@"\W");
                            previous = -1;
                            continue;
                        case 'h':
                            items.Items.Append(HorizontalItems);
                            previous = -1;
                            continue;
                        case 'v':
                            items.Items.Append(VerticalItems);
                            previous = -1;
                            continue;
                        case 'p' or 'P':
                            {
                                string saved = output.ToString();
                                output.Clear();
                                bool read = Property(e == 'P', inClass: true);
                                items.Items.Append(output);
                                output.Clear().Append(saved);
                                if (!read)
                                {
                                    return false;
                                }

                                previous = -1;
                                continue;
                            }

                        case 'Q':
                            while (!AtEnd && !(Current == '\\' && index + 1 < pattern.Length && pattern[index + 1] == 'E'))
                            {
                                int quoted = CodeAt(pattern, index);
                                index += quoted > 0xFFFF ? 2 : 1;
                                ClassLiteral(quoted, items.Items, items.Wide);
                            }

                            if (!AtEnd)
                            {
                                index += 2;
                            }

                            previous = -1;
                            continue;
                        case 'E':
                            continue;
                        default:
                            index--;
                            if (!ClassCharacterEscape(out code))
                            {
                                return false;
                            }

                            break;
                    }
                }
                else
                {
                    code = CodeAt(pattern, index);
                    index += code > 0xFFFF ? 2 : 1;
                }

                // A range: the character before, a hyphen, and this one; a hyphen before an operator, a class or the end is itself.
                if (code == '-' && c == '-' && previous >= 0 && !AtEnd && Current != ']' && !AtOperator && Current != '[' && !BeforeClassEscape())
                {
                    int end;
                    if (Current == '\\')
                    {
                        index++;
                        if (!ClassCharacterEscape(out end))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        end = CodeAt(pattern, index);
                        index += end > 0xFFFF ? 2 : 1;
                    }

                    if (end < previous)
                    {
                        return false;
                    }

                    // The start of the range went in as a literal; the range takes its place.
                    RemoveLast(items, previous);
                    AddRange(items, previous, end);
                    previous = -1;
                    continue;
                }

                ClassLiteral(code, items.Items, items.Wide);
                previous = code;
            }

            if (!any)
            {
                return false;
            }

            node = parts.Count == 1 ? items : new UnionNode(parts);
            return true;
        }

        /// <summary>Whether a backslash here starts an escape for a set of characters, which cannot end a range.</summary>
        private bool BeforeClassEscape() =>
            Current == '\\' && index + 1 < pattern.Length && pattern[index + 1] is 'd' or 'D' or 's' or 'S' or 'w' or 'W' or 'h' or 'v' or 'p' or 'P' or 'Q' or 'E';

        /// <summary>An escape for one character inside a class, at the character after the backslash.</summary>
        private bool ClassCharacterEscape(out int code)
        {
            code = 0;
            if (AtEnd)
            {
                return false;
            }

            char e = Current;
            index++;
            if (e == 'b')
            {
                code = 8;
                return true;
            }

            if (char.IsAsciiDigit(e))
            {
                if (e is '8' or '9')
                {
                    return false;
                }

                index--;
                int count = 0;
                while (count < 3 && !AtEnd && Current is >= '0' and <= '7')
                {
                    code = (code * 8) + (Current - '0');
                    index++;
                    count++;
                }

                return true;
            }

            return CharacterEscape(e, out code);
        }

        private static void RemoveLast(ItemsNode items, int code)
        {
            if (code > 0xFFFF)
            {
                items.Wide.RemoveAt(items.Wide.Count - 1);
                return;
            }

            items.Items.Length -= ClassChar(code).Length;
        }

        private static void AddRange(ItemsNode items, int low, int high)
        {
            if (low <= 0xFFFF)
            {
                items.Items.Append(ClassChar(low)).Append('-').Append(ClassChar(Math.Min(high, 0xFFFF)));
                low = 0x10000;
            }

            if (high >= low)
            {
                items.Wide.Add(WideRange(low, high));
            }
        }

        /// <summary>A class as .NET pattern text that matches one character: a plain class where it can be, lookarounds for the operators.</summary>
        private static string Render(ClassNode node)
        {
            switch (node)
            {
                case ItemsNode items:
                    return RenderItems(items, negated: false);
                case NotNode { Inner: ItemsNode items }:
                    return RenderItems(items, negated: true);
                case NotNode not:
                    return "(?:(?!" + Render(not.Inner) + ")" + AnyCharacter + ")";
                case UnionNode union:
                    {
                        ItemsNode merged = new();
                        List<string> others = [];
                        foreach (ClassNode part in union.Parts)
                        {
                            if (part is ItemsNode simple)
                            {
                                merged.Items.Append(simple.Items);
                                merged.Wide.AddRange(simple.Wide);
                            }
                            else
                            {
                                others.Add(Render(part));
                            }
                        }

                        if (merged.Items.Length > 0 || merged.Wide.Count > 0)
                        {
                            others.Insert(0, RenderItems(merged, negated: false));
                        }

                        return others.Count == 1 ? others[0] : "(?:" + string.Join("|", others) + ")";
                    }

                case OperatorNode op:
                    {
                        string left = Render(op.Left), right = Render(op.Right);
                        return op.Operator switch
                        {
                            '&' => "(?:(?=" + left + ")" + right + ")",
                            '|' => "(?:" + left + "|" + right + ")",
                            '-' => "(?:(?!" + right + ")" + left + ")",
                            _ => "(?:(?!" + right + ")" + left + "|(?!" + left + ")" + right + ")",
                        };
                    }

                default:
                    return "(?!)";
            }
        }

        /// <summary>
        /// Simple items as a .NET class. Characters beyond U+FFFF are pairs of UTF-16 units to .NET, so they are alternatives
        /// of their own, and a negated class takes a whole pair as one character.
        /// </summary>
        private static string RenderItems(ItemsNode node, bool negated)
        {
            string items = node.Items.ToString();
            string pairs = string.Join("|", node.Wide);
            if (!negated)
            {
                if (node.Wide.Count == 0)
                {
                    return items.Length > 0 ? "[" + items + "]" : "(?!)";
                }

                return "(?:" + pairs + (items.Length > 0 ? "|[" + items + "]" : "") + ")";
            }

            string bmp = "[^" + Highs + items + "]";
            return node.Wide.Count == 0
                ? "(?:" + HighLow + "|" + bmp + ")"
                : "(?:(?!" + pairs + ")" + HighLow + "|" + bmp + ")";
        }

        private static void ClassLiteral(int code, StringBuilder items, List<string> wide)
        {
            if (code > 0xFFFF)
            {
                wide.Add(Pair(code));
                return;
            }

            items.Append(ClassChar(code));
        }

        private static string ClassChar(int code) => code < 128 && char.IsAsciiLetterOrDigit((char)code) ? ((char)code).ToString() : U(code);

        private static string Pair(int code)
        {
            string s = char.ConvertFromUtf32(code);
            return U(s[0]) + U(s[1]);
        }

        /// <summary>A range of characters beyond U+FFFF, as surrogate pairs.</summary>
        private static string WideRange(int low, int high)
        {
            List<string> parts = [];
            for (int lead = 0xD800 + ((low - 0x10000) >> 10); lead <= 0xD800 + ((high - 0x10000) >> 10); lead++)
            {
                int from = lead == 0xD800 + ((low - 0x10000) >> 10) ? 0xDC00 + ((low - 0x10000) & 0x3FF) : 0xDC00;
                int to = lead == 0xD800 + ((high - 0x10000) >> 10) ? 0xDC00 + ((high - 0x10000) & 0x3FF) : 0xDFFF;
                parts.Add(U(lead) + "[" + U(from) + "-" + U(to) + "]");
            }

            return string.Join("|", parts);
        }

        /// <summary>A POSIX class inside a class, with Unicode properties as PCRE2 gives them in UCP mode.</summary>
        private static bool Posix(string name, StringBuilder items)
        {
            bool negated = name.StartsWith('^');
            string bare = negated ? name[1..] : name;
            string? positive = bare switch
            {
                "alpha" => @"\p{L}",
                "lower" => @"\p{Ll}",
                "upper" => @"\p{Lu}",
                "digit" => @"\p{Nd}",
                "alnum" => @"\p{L}\p{N}",
                "space" => @"\s",
                "word" => WordItems,
                "blank" => HorizontalItems,
                "cntrl" => @"\p{Cc}",
                "xdigit" => "0-9A-Fa-f",
                "punct" => @"\p{P}$+<=>^`|~",
                "graph" => @"\p{L}\p{M}\p{N}\p{P}\p{S}\p{Cf}\p{Co}",
                "print" => @"\p{L}\p{M}\p{N}\p{P}\p{S}\p{Cf}\p{Co}\p{Zs}",
                "ascii" => @"\x00-\x7F",
                _ => null,
            };
            if (positive is null)
            {
                return false;
            }

            if (!negated)
            {
                items.Append(positive);
                return true;
            }

            string? single = bare switch
            {
                "alpha" => @"\P{L}",
                "lower" => @"\P{Ll}",
                "upper" => @"\P{Lu}",
                "digit" => @"\P{Nd}",
                "space" => @"\S",
                "cntrl" => @"\P{Cc}",
                _ => null,
            };
            if (single is null)
            {
                return false;
            }

            items.Append(single);
            return true;
        }

        /// <summary><c>\p{...}</c> or <c>\P{...}</c>: a general category, one of PCRE2's special properties, or a script.</summary>
        private bool Property(bool negated, bool inClass)
        {
            if (AtEnd)
            {
                return false;
            }

            string name;
            if (Current == '{')
            {
                int close = pattern.IndexOf('}', index);
                if (close < 0)
                {
                    return false;
                }

                name = pattern[(index + 1)..close];
                index = close + 1;
            }
            else
            {
                name = Current.ToString();
                index++;
            }

            if (name.StartsWith('^'))
            {
                negated = !negated;
                name = name[1..];
            }

            string loose = string.Concat(name.Where(ch => ch is not (' ' or '_' or '-'))).ToLowerInvariant();
            string? category = Categories.FirstOrDefault(k => k.Equals(loose, StringComparison.OrdinalIgnoreCase));
            if (category is not null)
            {
                output.Append(negated ? @"\P{" : @"\p{").Append(category).Append('}');
                return true;
            }

            string? items = loose switch
            {
                "l&" or "lc" => @"\p{Lu}\p{Ll}\p{Lt}",
                "any" => @"\s\S",
                "xan" => @"\p{L}\p{N}",
                "xsp" or "xps" => @"\s",
                "xwd" => WordItems,
                _ => null,
            };
            if (items is null && Scripts.TryGetValue(loose, out string? blocks))
            {
                // A script is near enough the letters and marks of its blocks; .NET has blocks, not scripts.
                if (inClass)
                {
                    items = blocks;
                }
                else
                {
                    output.Append(negated ? "[^" : "[").Append(blocks).Append(@"-[^\p{L}\p{M}]]");
                    return true;
                }
            }

            if (items is null)
            {
                return false;
            }

            if (inClass)
            {
                if (negated)
                {
                    return false;
                }

                output.Append(items);
                return true;
            }

            output.Append(negated ? "[^" : "[").Append(items).Append(']');
            return true;
        }

        private static readonly string[] Categories =
        [
            "L", "Lu", "Ll", "Lt", "Lm", "Lo", "M", "Mn", "Mc", "Me", "N", "Nd", "Nl", "No", "P", "Pc", "Pd", "Ps", "Pe", "Pi",
            "Pf", "Po", "S", "Sm", "Sc", "Sk", "So", "Z", "Zs", "Zl", "Zp", "C", "Cc", "Cf", "Cs", "Co", "Cn",
        ];

        private static readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal)
        {
            ["latin"] = @"\p{IsBasicLatin}\p{IsLatin-1Supplement}\p{IsLatinExtended-A}\p{IsLatinExtended-B}\p{IsIPAExtensions}\p{IsLatinExtendedAdditional}",
            ["greek"] = @"\p{IsGreekandCoptic}\p{IsGreekExtended}",
            ["cyrillic"] = @"\p{IsCyrillic}\p{IsCyrillicSupplement}",
            ["armenian"] = @"\p{IsArmenian}",
            ["hebrew"] = @"\p{IsHebrew}",
            ["arabic"] = @"\p{IsArabic}\p{IsArabicPresentationForms-A}\p{IsArabicPresentationForms-b}",
            ["devanagari"] = @"\p{IsDevanagari}",
            ["bengali"] = @"\p{IsBengali}",
            ["thai"] = @"\p{IsThai}",
            ["georgian"] = @"\p{IsGeorgian}",
            ["hangul"] = @"\p{IsHangulJamo}\p{IsHangulCompatibilityJamo}\p{IsHangulSyllables}",
            ["hiragana"] = @"\p{IsHiragana}",
            ["katakana"] = @"\p{IsKatakana}\p{IsKatakanaPhoneticExtensions}",
            ["han"] = @"\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}",
        };

        /// <summary>One character, written so that .NET reads it as itself; one beyond U+FFFF is a group, so a quantifier takes both halves.</summary>
        private void Literal(int code)
        {
            if (code > 0xFFFF)
            {
                output.Append("(?:").Append(Pair(code)).Append(')');
                return;
            }

            output.Append(code < 128 && char.IsAsciiLetterOrDigit((char)code) ? ((char)code).ToString() : U(code));
        }

        /// <summary>The character at a position, as a code point when it is a surrogate pair.</summary>
        private static int CodeAt(string text, int at) =>
            char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]) ? char.ConvertToUtf32(text[at], text[at + 1]) : text[at];

        private static string U(int code) => @"\u" + code.ToString("X4", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^(\d*)(,)?(\d*)$")]
    private static partial Regex RepeatForm();
}
