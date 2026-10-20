using System.Text;

namespace Egap.Documents.Html;

/// <summary>
/// Keeps hostile nesting from costing time: the HTML tree builder checks scopes up the whole stack of open elements, so a page
/// of 100,000 nested elements takes minutes. A light scan estimates the nesting; only when it passes <see cref="MaxDepth"/>
/// are the start tags deeper than that left out (with their end tags), keeping all text. The reader keeps no structure that
/// deep anyway (<see cref="DocumentLimits.MaxDepth"/>), so a real page is never changed.
/// </summary>
internal static class HtmlNestingGuard
{
    /// <summary>The deepest nesting passed on as it is.</summary>
    public const int MaxDepth = 4096;

    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr", "image", "img", "input", "keygen", "link",
        "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes", "plaintext",
    };

    // Elements whose end tag may be left out: a new one closes the open one, so a long list without end tags is not deep.
    private static readonly HashSet<string> SelfClosingSiblings = new(StringComparer.OrdinalIgnoreCase)
    {
        "li", "p", "dt", "dd", "option", "optgroup", "tr", "td", "th", "thead", "tbody", "tfoot", "rb", "rt", "rp", "rtc",
    };

    /// <summary>The page, with tags past <see cref="MaxDepth"/> left out when it nests that deep; otherwise the same string.</summary>
    public static string Limit(string html)
    {
        return Scan(html, rewrite: null) <= MaxDepth ? html : Rewrite(html);
    }

    /// <summary>
    /// Bytes in any encoding whose tags are ASCII (UTF-8 and the single-byte ones), as <see cref="Limit(string)"/> treats text:
    /// each byte read as the Latin-1 character of the same value, so the bytes come back unchanged apart from tags left out.
    /// </summary>
    public static byte[] Limit(ReadOnlySpan<byte> html)
    {
        string text = Encoding.Latin1.GetString(html);
        string limited = Limit(text);
        return ReferenceEquals(limited, text) ? html.ToArray() : Encoding.Latin1.GetBytes(limited);
    }

    private static string Rewrite(string html)
    {
        StringBuilder output = new(html.Length);
        Scan(html, output);
        return output.ToString();
    }

    /// <summary>The deepest nesting the scan sees; with <paramref name="rewrite"/>, the page is written there without the tags past the limit.</summary>
    private static int Scan(string html, StringBuilder? rewrite)
    {
        List<(string Name, bool Kept)> open = [];
        int kept = 0;
        int deepest = 0;
        int copied = 0;
        int i = 0;
        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= html.Length)
            {
                break;
            }

            i = lt + 1;
            char next = html[i];
            if (next == '!' && string.CompareOrdinal(html, i, "!--", 0, 3) == 0)
            {
                int end = html.IndexOf("-->", i + 3, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (next is '!' or '?')
            {
                int end = html.IndexOf('>', i);
                i = end < 0 ? html.Length : end + 1;
                continue;
            }

            bool closing = next == '/';
            int nameStart = closing ? i + 1 : i;
            if (nameStart >= html.Length || !char.IsAsciiLetter(html[nameStart]))
            {
                continue;
            }

            int nameEnd = nameStart;
            while (nameEnd < html.Length && html[nameEnd] is not (' ' or '\t' or '\n' or '\r' or '\f' or '/' or '>'))
            {
                nameEnd++;
            }

            string name = html[nameStart..nameEnd];
            int tagEnd = TagEnd(html, nameEnd);
            bool selfClosing = tagEnd > 1 && html[tagEnd - 2] == '/';
            bool keep = true;
            if (closing)
            {
                // Only near the top: an end tag for nothing open must not cost the whole stack.
                int at = open.FindLastIndex(open.Count - 1, Math.Min(open.Count, 64), o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    keep = open[at].Kept;
                    for (int k = open.Count - 1; k >= at; k--)
                    {
                        kept -= open[k].Kept ? 1 : 0;
                    }

                    open.RemoveRange(at, open.Count - at);
                }
            }
            else if (!selfClosing && !Void.Contains(name))
            {
                if (SelfClosingSiblings.Contains(name) && open.Count > 0 && string.Equals(open[^1].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    kept -= open[^1].Kept ? 1 : 0;
                    open.RemoveAt(open.Count - 1);
                }

                keep = kept < MaxDepth;
                open.Add((name, keep));
                kept += keep ? 1 : 0;
                deepest = Math.Max(deepest, open.Count);
            }

            if (!keep && rewrite is not null)
            {
                rewrite.Append(html, copied, lt - copied);
                copied = tagEnd;
            }

            i = tagEnd;
            if (!closing && keep && RawText.Contains(name))
            {
                // Raw text runs to its end tag: no tags inside it count.
                int end = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = end < 0 ? html.Length : end;
            }
        }

        rewrite?.Append(html, copied, html.Length - copied);
        return deepest;
    }

    /// <summary>Where a tag ends: past its '>', skipping attribute values quoted right after their '='.</summary>
    private static int TagEnd(string html, int from)
    {
        char quote = '\0';
        char previous = ' ';
        for (int i = from; i < html.Length; i++)
        {
            char c = html[i];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
                continue;
            }

            if (c is '"' or '\'' && previous == '=')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }

            if (!char.IsWhiteSpace(c))
            {
                previous = c;
            }
        }

        return html.Length;
    }
}
