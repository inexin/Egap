using System.Globalization;
using System.Text;

namespace Egap.Documents.Markdown;

/// <summary>
/// Renders the syntax tree as the HTML the CommonMark and GFM specs give for each example, so the compare repo can run the
/// spec examples (CC BY-SA, kept out of Egap) against the parser. Not the HTML writer: that one works from the model.
/// </summary>
internal static class MdHtmlRenderer
{
    public static string Render(string markdown, MarkdownExtensions extensions)
    {
        MdDocument document = BlockParser.Parse(markdown, extensions);
        StringBuilder html = new();
        RenderBlocks(document, html, tight: false);
        List<MdFootnoteDefinition> notes = [.. document.Footnotes.Values.Where(n => n.Number > 0).OrderBy(n => n.Number)];
        if (notes.Count > 0)
        {
            html.Append("<section class=\"footnotes\" data-footnotes>\n<ol>\n");
            foreach (MdFootnoteDefinition note in notes)
            {
                html.Append("<li id=\"fn-").Append(Escape(note.Label)).Append("\">\n");
                StringBuilder inner = new();
                RenderBlocks(note, inner, tight: false);
                string backrefs = string.Concat(Enumerable.Range(1, note.ReferenceCount).Select(i =>
                    $" <a href=\"#fnref-{Escape(note.Label)}{(i > 1 ? "-" + i.ToString(CultureInfo.InvariantCulture) : "")}\" class=\"footnote-backref\" data-footnote-backref data-footnote-backref-idx=\"{note.Number}{(i > 1 ? "-" + i.ToString(CultureInfo.InvariantCulture) : "")}\" aria-label=\"Back to reference {note.Number}{(i > 1 ? "-" + i.ToString(CultureInfo.InvariantCulture) : "")}\">↩{(i > 1 ? "<sup class=\"footnote-ref\">" + i.ToString(CultureInfo.InvariantCulture) + "</sup>" : "")}</a>")).TrimStart();
                string text = inner.ToString();
                int lastP = text.LastIndexOf("</p>", StringComparison.Ordinal);
                html.Append(lastP >= 0 ? text[..lastP] + " " + backrefs + text[lastP..] : text + backrefs + "\n");
                html.Append("</li>\n");
            }

            html.Append("</ol>\n</section>\n");
        }

        return html.ToString();
    }

    private static void RenderBlocks(MdNode parent, StringBuilder html, bool tight)
    {
        foreach (MdNode node in parent.Children)
        {
            RenderBlock(node, html, tight);
        }
    }

    private static void RenderBlock(MdNode node, StringBuilder html, bool tight)
    {
        switch (node)
        {
            case MdParagraph p:
                if (tight)
                {
                    RenderInlines(p, html);
                    html.Append('\n');
                }
                else
                {
                    html.Append("<p>");
                    RenderInlines(p, html);
                    html.Append("</p>\n");
                }

                break;
            case MdHeading h:
                html.Append("<h").Append(h.Level).Append('>');
                RenderInlines(h, html);
                html.Append("</h").Append(h.Level).Append(">\n");
                break;
            case MdThematicBreak:
                html.Append("<hr />\n");
                break;
            case MdBlockQuote q:
                html.Append("<blockquote>\n");
                RenderBlocks(q, html, tight: false);
                html.Append("</blockquote>\n");
                break;
            case MdCodeBlock code:
                string info = code.Info.Split([' ', '\t'], 2)[0];
                html.Append("<pre><code");
                if (info.Length > 0)
                {
                    html.Append(" class=\"language-").Append(Escape(info)).Append('"');
                }

                html.Append('>').Append(Escape(code.Literal)).Append("</code></pre>\n");
                break;
            case MdHtmlBlock block:
                html.Append(block.Literal).Append('\n');
                break;
            case MdList list:
                string tag = list.Ordered ? "ol" : "ul";
                html.Append('<').Append(tag);
                if (list.Ordered && list.Start != 1)
                {
                    html.Append(" start=\"").Append(list.Start.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                html.Append(">\n");
                foreach (MdNode child in list.Children)
                {
                    MdListItem item = (MdListItem)child;
                    html.Append("<li>");
                    if (item.TaskChecked is bool done)
                    {
                        html.Append(done ? "<input type=\"checkbox\" checked=\"\" disabled=\"\" /> " : "<input type=\"checkbox\" disabled=\"\" /> ");
                    }

                    if (item.Children.Count > 0 && !(list.Tight && item.Children[0] is MdParagraph))
                    {
                        html.Append('\n');
                    }

                    RenderBlocks(item, html, list.Tight);
                    if (html.Length > 0 && html[^1] == '\n' && list.Tight && item.Children.Count > 0 && item.Children[^1] is MdParagraph)
                    {
                        html.Length--;
                    }

                    html.Append("</li>\n");
                }

                html.Append("</").Append(tag).Append(">\n");
                break;
            case MdTable table:
                html.Append("<table>\n");
                bool body = false;
                foreach (MdNode rowNode in table.Children)
                {
                    MdTableRow row = (MdTableRow)rowNode;
                    if (row.Header)
                    {
                        html.Append("<thead>\n");
                    }
                    else if (!body)
                    {
                        html.Append("<tbody>\n");
                        body = true;
                    }

                    html.Append("<tr>\n");
                    foreach (MdNode cellNode in row.Children)
                    {
                        MdTableCell cell = (MdTableCell)cellNode;
                        string cellTag = row.Header ? "th" : "td";
                        html.Append('<').Append(cellTag);
                        if (cell.Align != MdAlign.None)
                        {
                            html.Append(" align=\"").Append(cell.Align.ToString().ToLowerInvariant()).Append('"');
                        }

                        html.Append('>');
                        RenderInlines(cell, html);
                        html.Append("</").Append(cellTag).Append(">\n");
                    }

                    html.Append("</tr>\n");
                    if (row.Header)
                    {
                        html.Append("</thead>\n");
                    }
                }

                if (body)
                {
                    html.Append("</tbody>\n");
                }

                html.Append("</table>\n");
                break;
            case MdFootnoteDefinition:
                break;
        }
    }

    private static void RenderInlines(MdNode parent, StringBuilder html)
    {
        foreach (MdNode node in parent.Children)
        {
            switch (node)
            {
                case MdText text:
                    html.Append(Escape(text.Literal));
                    break;
                case MdSoftBreak:
                    html.Append('\n');
                    break;
                case MdHardBreak:
                    html.Append("<br />\n");
                    break;
                case MdCode code:
                    html.Append("<code>").Append(Escape(code.Literal)).Append("</code>");
                    break;
                case MdEmphasis:
                    html.Append("<em>");
                    RenderInlines(node, html);
                    html.Append("</em>");
                    break;
                case MdStrong:
                    html.Append("<strong>");
                    RenderInlines(node, html);
                    html.Append("</strong>");
                    break;
                case MdStrikethrough:
                    html.Append("<del>");
                    RenderInlines(node, html);
                    html.Append("</del>");
                    break;
                case MdLink link:
                    html.Append("<a href=\"").Append(Escape(NormalizeUri(link.Destination))).Append('"');
                    if (!string.IsNullOrEmpty(link.Title))
                    {
                        html.Append(" title=\"").Append(Escape(link.Title)).Append('"');
                    }

                    html.Append('>');
                    RenderInlines(link, html);
                    html.Append("</a>");
                    break;
                case MdImage image:
                    html.Append("<img src=\"").Append(Escape(NormalizeUri(image.Destination))).Append("\" alt=\"");
                    html.Append(Escape(PlainText(image)));
                    html.Append('"');
                    if (!string.IsNullOrEmpty(image.Title))
                    {
                        html.Append(" title=\"").Append(Escape(image.Title)).Append('"');
                    }

                    html.Append(" />");
                    break;
                case MdHtmlInline raw:
                    html.Append(raw.Literal);
                    break;
                case MdFootnoteReference reference:
                    string suffix = reference.Ordinal > 1 ? "-" + reference.Ordinal.ToString(CultureInfo.InvariantCulture) : "";
                    html.Append("<sup class=\"footnote-ref\"><a href=\"#fn-").Append(Escape(reference.Definition.Label)).Append("\" id=\"fnref-")
                        .Append(Escape(reference.Definition.Label)).Append(suffix).Append("\" data-footnote-ref>")
                        .Append(reference.Definition.Number.ToString(CultureInfo.InvariantCulture)).Append("</a></sup>");
                    break;
            }
        }
    }

    /// <summary>The text of an image's description, as its alt attribute: markup dropped.</summary>
    internal static string PlainText(MdNode node)
    {
        StringBuilder text = new();
        foreach (MdNode child in node.Children)
        {
            switch (child)
            {
                case MdText t:
                    text.Append(t.Literal);
                    break;
                case MdCode c:
                    text.Append(c.Literal);
                    break;
                case MdSoftBreak or MdHardBreak:
                    text.Append('\n');
                    break;
                default:
                    text.Append(PlainText(child));
                    break;
            }
        }

        return text.ToString();
    }

    private static string Escape(string text)
    {
        StringBuilder result = new(text.Length);
        foreach (char c in text)
        {
            result.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => c.ToString(),
            });
        }

        return result.ToString();
    }

    /// <summary>Percent-encodes what a URL may not hold as it is, keeping existing escapes, as the CommonMark reference does.</summary>
    internal static string NormalizeUri(string uri)
    {
        StringBuilder result = new();
        byte[] bytes = Encoding.UTF8.GetBytes(uri);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b == '%' && i + 2 < bytes.Length && Uri.IsHexDigit((char)bytes[i + 1]) && Uri.IsHexDigit((char)bytes[i + 2]))
            {
                result.Append('%').Append((char)bytes[i + 1]).Append((char)bytes[i + 2]);
                i += 2;
            }
            else if (b < 128 && (char.IsAsciiLetterOrDigit((char)b) || ";/?:@&=+$,-_.!~*'()#".Contains((char)b)))
            {
                result.Append((char)b);
            }
            else
            {
                result.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return result.ToString();
    }
}
