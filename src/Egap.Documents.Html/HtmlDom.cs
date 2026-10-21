using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>
/// A node of the small tree the reader and the cascade work on, copied from AngleSharp's parse: built by appending only, so
/// each node knows its place among its siblings.
/// </summary>
internal abstract class HtmlNode
{
    private readonly List<HtmlNode> children = [];

    /// <summary>The parent node, or null for a document or a fragment.</summary>
    public HtmlNode? Parent { get; private set; }

    /// <summary>The child nodes in order.</summary>
    public IReadOnlyList<HtmlNode> Children => children;

    /// <summary>The node's index among its parent's children, or -1 when it has no parent.</summary>
    public int Index { get; private set; } = -1;

    /// <summary>Adds <paramref name="node"/>, which has no parent yet, as the last child.</summary>
    public void Append(HtmlNode node)
    {
        node.Parent = this;
        node.Index = children.Count;
        children.Add(node);
    }

    /// <summary>The parent, its parent and so on up to the root.</summary>
    public IEnumerable<HtmlNode> Ancestors()
    {
        for (HtmlNode? node = Parent; node is not null; node = node.Parent)
        {
            yield return node;
        }
    }

    /// <summary>The nodes below this one in document order.</summary>
    public IEnumerable<HtmlNode> Descendants()
    {
        Stack<(HtmlNode Node, int Next)> stack = new();
        stack.Push((this, 0));
        while (stack.Count > 0)
        {
            (HtmlNode node, int next) = stack.Pop();
            if (next < node.children.Count)
            {
                stack.Push((node, next + 1));
                HtmlNode child = node.children[next];
                yield return child;
                stack.Push((child, 0));
            }
        }
    }
}

/// <summary>Whether a document renders in quirks mode, limited quirks or no quirks (WHATWG 13.2.6.4.1).</summary>
internal enum HtmlQuirksMode
{
    NoQuirks,
    LimitedQuirks,
    Quirks,
}

/// <summary>A parsed HTML document.</summary>
internal sealed class HtmlDocument : HtmlNode
{
    /// <summary>The quirks mode the doctype chose.</summary>
    public HtmlQuirksMode QuirksMode { get; set; }

    /// <summary>The root element (html), or null.</summary>
    public HtmlElement? DocumentElement => Children.OfType<HtmlElement>().FirstOrDefault();

    /// <summary>The body element, or null.</summary>
    public HtmlElement? Body => DocumentElement?.Children.OfType<HtmlElement>().FirstOrDefault(e => e.IsHtml("body"));

    /// <summary>The head element, or null.</summary>
    public HtmlElement? Head => DocumentElement?.Children.OfType<HtmlElement>().FirstOrDefault(e => e.IsHtml("head"));
}

/// <summary>A document fragment: a template's contents.</summary>
internal sealed class HtmlFragment : HtmlNode
{
}

/// <summary>A doctype.</summary>
internal sealed class HtmlDocumentType(string name, string publicId, string systemId) : HtmlNode
{
    public string Name { get; } = name;

    public string PublicId { get; } = publicId;

    public string SystemId { get; } = systemId;
}

/// <summary>A comment.</summary>
internal sealed class HtmlComment(string data) : HtmlNode
{
    public string Data { get; } = data;
}

/// <summary>A processing instruction, such as &lt;?php ... ?&gt;.</summary>
internal sealed class HtmlProcessingInstruction(string target, string data) : HtmlNode
{
    public string Target { get; } = target;

    public string Data { get; } = data;
}

/// <summary>Text.</summary>
internal sealed class HtmlText(string data) : HtmlNode
{
    public string Data { get; } = data;
}

/// <summary>An element: HTML, SVG or MathML, with its attributes; it is what Egami.Css's selectors match.</summary>
internal sealed class HtmlElement : HtmlNode, ICssElement
{
    public const string HtmlNamespace = "http://www.w3.org/1999/xhtml";
    public const string SvgNamespace = "http://www.w3.org/2000/svg";
    public const string MathMLNamespace = "http://www.w3.org/1998/Math/MathML";
    public const string XLinkNamespace = "http://www.w3.org/1999/xlink";
    public const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    public HtmlElement(string localName, string namespaceUri, List<CssAttribute>? attributes = null)
    {
        LocalName = localName;
        NamespaceUri = namespaceUri;
        AttributeList = attributes ?? [];
    }

    public string LocalName { get; }

    public string NamespaceUri { get; }

    /// <summary>The attributes in source order, each name once.</summary>
    public List<CssAttribute> AttributeList { get; }

    /// <summary>A template's contents.</summary>
    public HtmlFragment? Content { get; set; }

    string? ICssElement.NamespaceUri => NamespaceUri;

    ICssElement? ICssElement.Parent => Parent as HtmlElement;

    ICssElement? ICssElement.PreviousSibling => Sibling(-1);

    ICssElement? ICssElement.NextSibling => Sibling(1);

    ICssElement? ICssElement.FirstChild => Children.OfType<HtmlElement>().FirstOrDefault();

    bool ICssElement.IsEmpty => Children.All(c => c is HtmlComment || (c is HtmlText t && t.Data.Length == 0));

    public string? Id => GetAttribute("id", null);

    bool ICssElement.IsHtml => NamespaceUri == HtmlNamespace;

    IEnumerable<CssAttribute> ICssElement.Attributes => AttributeList;

    /// <summary>Whether this is the HTML element <paramref name="name"/>.</summary>
    public bool IsHtml(string name)
    {
        return NamespaceUri == HtmlNamespace && LocalName == name;
    }

    public bool HasClass(string name)
    {
        if (GetAttribute("class", null) is not { } classes)
        {
            return false;
        }

        foreach (Range range in classes.AsSpan().SplitAny(" \t\n\f\r"))
        {
            if (classes.AsSpan()[range].SequenceEqual(name))
            {
                return true;
            }
        }

        return false;
    }

    public string? GetAttribute(string localName, string? namespaceUri)
    {
        foreach (CssAttribute attribute in AttributeList)
        {
            if (attribute.LocalName == localName && attribute.NamespaceUri == namespaceUri)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private HtmlElement? Sibling(int step)
    {
        if (Parent is not { } parent)
        {
            return null;
        }

        IReadOnlyList<HtmlNode> siblings = parent.Children;
        for (int i = Index + step; i >= 0 && i < siblings.Count; i += step)
        {
            if (siblings[i] is HtmlElement element)
            {
                return element;
            }
        }

        return null;
    }
}
