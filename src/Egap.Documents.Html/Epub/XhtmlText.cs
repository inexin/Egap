using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Egap.Documents.Html;

/// <summary>
/// XHTML as EPUB holds it, parsed as XML (so self-closed elements such as &lt;a id="p1"/&gt; close where XML says) and
/// written back as HTML text for the HTML reader; HTML's named entities, which EPUB 2's DTD allows, are read too.
/// </summary>
internal static partial class XhtmlText
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    /// <summary>The bytes as XML, or null when they are not well-formed even with HTML's named entities.</summary>
    public static XDocument? TryLoad(byte[] bytes, DocumentLimits limits)
    {
        try
        {
            return EpubContainer.LoadXml(bytes, limits);
        }
        catch (DocumentException)
        {
        }

        // Named entities from EPUB 2's XHTML 1.1 DTD, which the reader does not load: written as character references.
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        string replaced = NamedEntity().Replace(text, m => m.Groups[1].Value switch
        {
            "amp" or "lt" or "gt" or "quot" or "apos" => m.Value,
            string name => AngleSharp.Html.HtmlEntityProvider.Resolver.GetSymbol(name) is { Length: > 0 } symbol
                ? string.Concat(symbol.EnumerateRunes().Select(r => $"&#{r.Value};"))
                : m.Value,
        });

        try
        {
            return EpubContainer.LoadXml(Encoding.UTF8.GetBytes(replaced.TrimStart('﻿')), limits);
        }
        catch (DocumentException)
        {
            return null;
        }
    }

    /// <summary>A chapter's text for the HTML reader: the XHTML written as HTML, or the bytes read as HTML when not XML.</summary>
    public static HtmlDocument Parse(byte[] bytes, DocumentLimits limits)
    {
        return TryLoad(bytes, limits) is { Root: { } root } ? HtmlParsing.Parse(ToHtml(root)) : HtmlParsing.Parse(bytes, null);
    }

    /// <summary>The element tree as HTML text, with a doctype so it is read in standards mode.</summary>
    public static string ToHtml(XElement root)
    {
        StringBuilder html = new("<!DOCTYPE html>\n");
        Stack<(XNode Node, bool Close)> work = new();
        work.Push((root, false));
        while (work.Count > 0)
        {
            (XNode node, bool close) = work.Pop();
            switch (node)
            {
                case XElement element when close:
                    html.Append("</").Append(element.Name.LocalName).Append('>');
                    break;
                case XElement element:
                    string name = element.Name.LocalName;
                    html.Append('<').Append(name);
                    foreach (XAttribute attribute in element.Attributes())
                    {
                        if (attribute.IsNamespaceDeclaration)
                        {
                            continue;
                        }

                        html.Append(' ').Append(AttributeName(attribute)).Append("=\"")
                            .Append(attribute.Value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)).Append('"');
                    }

                    html.Append('>');
                    if (VoidElements.Contains(name) && element.Name.Namespace == EpubPackage.Xhtml)
                    {
                        break;
                    }

                    if (name is "style" or "script")
                    {
                        // Raw text: written as it is, as the HTML parser reads it to the end tag.
                        html.Append(string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value))).Append("</").Append(name).Append('>');
                        break;
                    }

                    work.Push((element, true));
                    foreach (XNode child in element.Nodes().Reverse())
                    {
                        work.Push((child, false));
                    }

                    break;
                case XText text:
                    html.Append(text.Value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal));
                    break;
            }
        }

        return html.ToString();
    }

    private static string AttributeName(XAttribute attribute)
    {
        XNamespace ns = attribute.Name.Namespace;
        string local = attribute.Name.LocalName;
        if (ns == XNamespace.None)
        {
            return local;
        }

        if (ns == XNamespace.Xml)
        {
            return local == "lang" ? "lang" : "xml:" + local;
        }

        if (ns == "http://www.w3.org/1999/xlink")
        {
            return "xlink:" + local;
        }

        return ns == EpubPackage.Ops ? "epub:" + local : local;
    }

    [GeneratedRegex("&([A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex NamedEntity();
}
