using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Egap.Documents.Markdown;

/// <summary>
/// What GitHub's extended autolinks (GFM spec 6.9) do and Markdig's do not, applied after Markdig has parsed the document:
/// email addresses without mailto: (a run of letters, digits, '.', '-', '_' or '+', an '@', and a domain of letters, digits,
/// '-' or '_' with at least one '.', not ending in '-' or '_', a final '.' left outside) become mailto: links, and a link's
/// parentheses are balanced over the whole link, not cut at the first unmatched ')'.
/// </summary>
internal static class GfmAutolinks
{
    public static void Apply(MarkdownDocument document)
    {
        List<LiteralInline> literals = [.. document.Descendants<LiteralInline>().Where(l => l.Content.IndexOf('@') >= 0 && l.FirstParentOfType<LinkInline>() is null)];
        foreach (LiteralInline literal in literals)
        {
            if (literal.Parent is not null)
            {
                Split(Join(literal));
            }
        }

        // Markdig ends an extended autolink at its first unbalanced ')'; GitHub leaves out only unbalanced ones at the end.
        foreach (LinkInline link in document.Descendants<LinkInline>().Where(l => l.IsAutoLink && l.FirstChild is LiteralInline && l.NextSibling is LiteralInline).ToList())
        {
            ExtendPath(link);
        }
    }

    private static void ExtendPath(LinkInline link)
    {
        LiteralInline label = (LiteralInline)link.FirstChild!;
        LiteralInline next = (LiteralInline)link.NextSibling!;
        string shown = label.Content.ToString();
        string after = next.Content.ToString();
        if (after.Length == 0 || after[0] != ')' || link.Url is not { } url || !url.EndsWith(shown, StringComparison.Ordinal))
        {
            return;
        }

        int stop = after.AsSpan().IndexOfAny(" \t\r\n<");
        string candidate = shown + (stop < 0 ? after : after[..stop]);
        int end = PathEnd(candidate);
        if (end <= shown.Length)
        {
            return;
        }

        link.Url = url[..^shown.Length] + candidate[..end];
        label.Content = new StringSlice(candidate[..end]);
        string rest = after[(end - shown.Length)..];
        if (rest.Length == 0)
        {
            next.Remove();
        }
        else
        {
            next.Content = new StringSlice(rest);
        }
    }

    /// <summary>Where an extended autolink ends, by GitHub's rules for trailing punctuation, parentheses and entities.</summary>
    private static int PathEnd(string link)
    {
        int end = link.Length;
        while (end > 0)
        {
            char last = link[end - 1];
            if (last is '?' or '!' or '.' or ',' or ':' or '*' or '_' or '~')
            {
                end--;
            }
            else if (last == ')' && link.AsSpan(0, end).Count(')') > link.AsSpan(0, end).Count('('))
            {
                end--;
            }
            else if (last == ';' && link.LastIndexOf('&', end - 1) is int amp and >= 0 && amp < end - 2 && link.AsSpan(amp + 1, end - amp - 2).ContainsAnyExcept(Alphanumerics) is false)
            {
                end = amp;
            }
            else
            {
                break;
            }
        }

        return end;
    }

    private static readonly System.Buffers.SearchValues<char> Alphanumerics = System.Buffers.SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// The literal joined with the literals beside it: Markdig ends a literal at each '_' or '*' that could have been
    /// emphasis, and an address may hold them.
    /// </summary>
    private static LiteralInline Join(LiteralInline literal)
    {
        LiteralInline first = literal;
        while (first.PreviousSibling is LiteralInline previous)
        {
            first = previous;
        }

        if (first.NextSibling is not LiteralInline)
        {
            return first;
        }

        System.Text.StringBuilder text = new();
        List<LiteralInline> run = [];
        for (MdInline? node = first; node is LiteralInline part; node = node.NextSibling)
        {
            text.Append(part.Content.AsSpan());
            run.Add(part);
        }

        LiteralInline joined = new(text.ToString());
        first.InsertBefore(joined);
        foreach (LiteralInline part in run)
        {
            part.Remove();
        }

        return joined;
    }

    private static void Split(LiteralInline literal)
    {
        StringSlice content = literal.Content;
        string text = content.ToString();
        MdInline last = literal;
        int written = 0;
        for (int at = text.IndexOf('@', StringComparison.Ordinal); at >= 0; at = at + 1 < text.Length ? text.IndexOf('@', at + 1) : -1)
        {
            int start = at;
            while (start > written && IsLocal(text[start - 1]))
            {
                start--;
            }

            int end = at + 1;
            while (end < text.Length && IsDomain(text[end]))
            {
                end++;
            }

            if (end > at + 1 && text[end - 1] == '.')
            {
                end--;
            }

            ReadOnlySpan<char> domain = text.AsSpan(at + 1, end - at - 1);
            if (start == at || domain.IsEmpty || !domain.Contains('.') || domain[^1] is '-' or '_' || domain.Contains("..", StringComparison.Ordinal) || domain[0] == '.')
            {
                continue;
            }

            string address = text[start..end];
            LinkInline link = new("mailto:" + address, "") { IsAutoLink = true };
            link.AppendChild(new LiteralInline(address));
            if (start > written)
            {
                last = Append(last, new LiteralInline(text[written..start]));
            }

            last = Append(last, link);
            written = end;
            at = end - 1;
        }

        if (last == literal)
        {
            return;
        }

        if (written < text.Length)
        {
            Append(last, new LiteralInline(text[written..]));
        }

        literal.Remove();
    }

    private static MdInline Append(MdInline after, MdInline inline)
    {
        after.InsertAfter(inline);
        return inline;
    }

    private static bool IsLocal(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+';

    private static bool IsDomain(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';
}
