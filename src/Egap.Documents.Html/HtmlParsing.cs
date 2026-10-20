using System.Collections.Concurrent;
using System.Text;
using System.Text.Unicode;

using AngleSharp;
using AngleSharp.Browser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Text;

using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>
/// HTML parsed by AngleSharp as browsers parse it (WHATWG 13.2), then copied into the small tree the reader and the cascade
/// work on (<see cref="HtmlNode"/>, whose elements are Egami.Css's <see cref="ICssElement"/>). AngleSharp's own loaders stay
/// off, so parsing never fetches anything; images and style sheets go through <see cref="HtmlLoadOptions.ResourceResolver"/>.
/// </summary>
internal static class HtmlParsing
{
    // A parser context per default encoding: the default is what bytes without a byte order mark or a meta charset are read as.
    private static readonly ConcurrentDictionary<string, IBrowsingContext> Contexts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HtmlParserOptions Options = new() { IsScripting = false, DisableElementPositionTracking = true };

    /// <summary>Parses HTML text.</summary>
    public static HtmlDocument Parse(string html)
    {
        IHtmlDocument parsed = Parser("UTF-8").ParseDocument(HtmlNestingGuard.Limit(html));
        return Copy(parsed);
    }

    /// <summary>
    /// Parses HTML bytes, decoded as their byte order mark or meta charset says, else as <paramref name="encoding"/>, else as
    /// UTF-8 when they are valid UTF-8 and windows-1252 when not (the Encoding Standard's fallback).
    /// </summary>
    public static HtmlDocument Parse(ReadOnlySpan<byte> bytes, string? encoding)
    {
        string fallback = encoding is not null && TextEncoding.Resolve(encoding) is { } named ? named.WebName : Utf8.IsValid(bytes) ? "UTF-8" : "windows-1252";
        byte[] limited = HtmlNestingGuard.Limit(bytes);
        using MemoryStream stream = new(limited, writable: false);
        IHtmlDocument parsed = Parser(fallback).ParseDocument(stream);
        return Copy(parsed);
    }

    private static HtmlParser Parser(string fallback)
    {
        IBrowsingContext context = Contexts.GetOrAdd(fallback, name => BrowsingContext.New(Configuration.Default.With<IEncodingProvider>(_ => new FixedEncoding(TextEncoding.Resolve(name) ?? TextEncoding.Utf8))));
        return new HtmlParser(Options, context);
    }

    /// <summary>The parsed tree as the reader's nodes, copied with an explicit stack so depth costs no call stack.</summary>
    private static HtmlDocument Copy(IHtmlDocument parsed)
    {
        HtmlDocument document = new() { QuirksMode = parsed.CompatMode == "BackCompat" ? HtmlQuirksMode.Quirks : HtmlQuirksMode.NoQuirks };
        Stack<(INode From, HtmlNode To)> work = [];
        work.Push((parsed, document));
        while (work.Count > 0)
        {
            (INode from, HtmlNode to) = work.Pop();
            INodeList children = from is IHtmlTemplateElement template ? template.Content.ChildNodes : from.ChildNodes;
            HtmlNode target = to;
            if (from is IHtmlTemplateElement)
            {
                HtmlFragment content = new();
                ((HtmlElement)to).Content = content;
                target = content;
            }

            List<(INode, HtmlNode)> nested = [];
            foreach (INode child in children)
            {
                HtmlNode? copy = child switch
                {
                    IElement element => Element(element),
                    IText text => new HtmlText(text.Data),
                    IComment comment => new HtmlComment(comment.Data),
                    IDocumentType type => new HtmlDocumentType(type.Name, type.PublicIdentifier, type.SystemIdentifier),
                    IProcessingInstruction instruction => new HtmlProcessingInstruction(instruction.Target, instruction.Data),
                    _ => null,
                };
                if (copy is null)
                {
                    continue;
                }

                target.Append(copy);
                if (child is IElement && (child.HasChildNodes || child is IHtmlTemplateElement))
                {
                    nested.Add((child, copy));
                }
            }

            // Children are taken in order, so their own children go on the stack last first.
            for (int i = nested.Count - 1; i >= 0; i--)
            {
                work.Push(nested[i]);
            }
        }

        return document;
    }

    private static HtmlElement Element(IElement element)
    {
        List<CssAttribute> attributes = new(element.Attributes.Length);
        foreach (IAttr attribute in element.Attributes)
        {
            attributes.Add(new CssAttribute(attribute.NamespaceUri, attribute.LocalName, attribute.Value));
        }

        return new HtmlElement(element.LocalName, element.NamespaceUri ?? HtmlElement.HtmlNamespace, attributes);
    }

    private sealed class FixedEncoding(Encoding encoding) : IEncodingProvider
    {
        public Encoding Suggest(string locale) => encoding;
    }
}
