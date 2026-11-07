using System.Text;

namespace Egap.Spreadsheets.Formulas;

/// <summary>Writes a sheet's new name into the text of a formula, as Excel does when a sheet is renamed.</summary>
internal static class SheetRename
{
    /// <summary>
    /// The formula with every reference to the sheet named <paramref name="from"/> naming <paramref name="to"/>. Text in
    /// quotes, table columns and sheets of other books stay as they are.
    /// </summary>
    public static string Apply(string formula, string from, string to)
    {
        if (!formula.Contains('!', StringComparison.Ordinal))
        {
            return formula;
        }

        StringBuilder? changed = null;
        int copied = 0;
        int i = 0;
        while (i < formula.Length)
        {
            char c = formula[i];
            if (c == '"')
            {
                i = AfterQuoted(formula, i, '"');
            }
            else if (c == '[')
            {
                // A table's column, or a book: what follows a book belongs to that book.
                i = AfterBracket(formula, i);
                while (i < formula.Length && IsWordChar(formula[i]))
                {
                    i++;
                }
            }
            else if (c == '\'')
            {
                int end = AfterQuoted(formula, i, '\'');
                if (end < formula.Length && formula[end] == '!')
                {
                    string inner = formula[(i + 1)..(end - 1)].Replace("''", "'", StringComparison.Ordinal);
                    if (!inner.StartsWith('[') && Renamed(inner, from, to) is { } name)
                    {
                        (changed ??= new StringBuilder(formula.Length + 16)).Append(formula, copied, i - copied).Append(name);
                        copied = end;
                    }
                }

                i = end;
            }
            else if (IsWordChar(c))
            {
                int end = AfterWord(formula, i);
                if (end < formula.Length && formula[end] == ':' && end + 1 < formula.Length && IsWordChar(formula[end + 1]))
                {
                    int last = AfterWord(formula, end + 1);
                    if (last < formula.Length && formula[last] == '!')
                    {
                        end = last;
                    }
                }

                if (end < formula.Length && formula[end] == '!' && Renamed(formula[i..end], from, to) is { } name)
                {
                    (changed ??= new StringBuilder(formula.Length + 16)).Append(formula, copied, i - copied).Append(name);
                    copied = end;
                }

                i = end;
            }
            else
            {
                i++;
            }
        }

        return changed is null ? formula : changed.Append(formula, copied, formula.Length - copied).ToString();
    }

    /// <summary>The prefix's new text, without its <c>!</c>, or null when it does not name the sheet.</summary>
    private static string? Renamed(string prefix, string from, string to)
    {
        int colon = prefix.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return prefix.Equals(from, StringComparison.OrdinalIgnoreCase) ? SheetNames.Quoted(to) : null;
        }

        string first = prefix[..colon];
        string last = prefix[(colon + 1)..];
        bool firstMatches = first.Equals(from, StringComparison.OrdinalIgnoreCase);
        bool lastMatches = last.Equals(from, StringComparison.OrdinalIgnoreCase);
        if (!firstMatches && !lastMatches)
        {
            return null;
        }

        first = firstMatches ? to : first;
        last = lastMatches ? to : last;

        // Excel quotes the two names together.
        return SheetNames.IsPlain(first) && SheetNames.IsPlain(last)
            ? first + ":" + last
            : "'" + (first + ":" + last).Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '\\' or '$' or '?';

    private static int AfterWord(string text, int i)
    {
        while (i < text.Length && IsWordChar(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int AfterQuoted(string text, int i, char quote)
    {
        i++;
        while (i < text.Length)
        {
            if (text[i] != quote)
            {
                i++;
            }
            else if (i + 1 < text.Length && text[i + 1] == quote)
            {
                i += 2;
            }
            else
            {
                return i + 1;
            }
        }

        return i;
    }

    private static int AfterBracket(string text, int i)
    {
        int depth = 0;
        while (i < text.Length)
        {
            char c = text[i++];
            if (c == '\'' && i < text.Length)
            {
                // A table column's escape character.
                i++;
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']' && --depth == 0)
            {
                return i;
            }
        }

        return i;
    }
}
