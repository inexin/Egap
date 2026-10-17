namespace Egap.Pdf.Layout;

/// <summary>
/// One structure element as layout analysis needs it: its type (a PDF 2.0 type such as Title kept as written, anything
/// else resolved through the role map), its parent, depth and place in document order, and its alternate text.
/// </summary>
internal sealed class TagNode(PdfStructureElement element, string type, TagNode? parent, int order)
{
    public PdfStructureElement Element { get; } = element;

    public string Type { get; } = type;

    public TagNode? Parent { get; } = parent;

    /// <summary>The element's place in a depth-first walk of the tree: document order.</summary>
    public int Order { get; } = order;

    public List<TagNode> Children { get; } = [];

    public IEnumerable<TagNode> Ancestors()
    {
        for (TagNode? node = this; node is not null; node = node.Parent)
        {
            yield return node;
        }
    }

    public IEnumerable<TagNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (TagNode child in Children)
        {
            foreach (TagNode node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }

    public override string ToString()
    {
        return $"{Type} #{Order}";
    }
}

/// <summary>
/// A document's structure tree read once (design Q2, Build Plan task 3): every element with its parent and document order,
/// and for each page the element that owns each marked-content id, so a glyph's MCID leads to its element without walking
/// the tree again.
/// </summary>
internal sealed class TagIndex
{
    /// <summary>Types PDF 2.0 adds that a role map often points elsewhere; kept as written.</summary>
    private static readonly HashSet<string> Pdf20Types = ["Title", "FENote", "Aside", "Em", "Strong", "Sub", "DocumentFragment"];

    /// <summary>The deepest tree walked; deeper elements are left out, as broken files can nest without end.</summary>
    private const int MaxDepth = 256;

    private readonly Dictionary<PdfDictionary, Dictionary<int, TagNode>> byPage = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TagNode> marking = [];

    private TagIndex()
    {
    }

    /// <summary>The elements in document order.</summary>
    public List<TagNode> Nodes { get; } = [];

    /// <summary>The document's structure tree, or null when it has none.</summary>
    public static TagIndex? Read(PdfDocument document)
    {
        PdfStructureTree tree = document.Structure;
        if (!tree.Exists)
        {
            return null;
        }

        TagIndex index = new();
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        foreach (PdfStructureElement element in tree.Children)
        {
            index.Visit(element, null, seen, 0);
        }

        return index.Nodes.Count == 0 ? null : index;
    }

    /// <summary>The element that owns marked content <paramref name="mcid"/> on <paramref name="page"/>, or null.</summary>
    public TagNode? Find(PdfPage page, int mcid)
    {
        return mcid >= 0 && byPage.TryGetValue(page.Dictionary, out Dictionary<int, TagNode>? ids) && ids.TryGetValue(mcid, out TagNode? node) ? node : null;
    }

    /// <summary>The elements that own marked content on <paramref name="page"/>, glyphs or not (an empty paragraph has some).</summary>
    public IEnumerable<TagNode> OnPage(PdfPage page)
    {
        return byPage.TryGetValue(page.Dictionary, out Dictionary<int, TagNode>? ids) ? ids.Values : [];
    }

    /// <summary>Whether <paramref name="node"/> or an element inside it owns marked content on any page.</summary>
    public bool HasContent(TagNode node)
    {
        return node.DescendantsAndSelf().Any(marking.Contains);
    }

    /// <summary>Whether any marked content on <paramref name="page"/> belongs to the tree.</summary>
    public bool HasPage(PdfPage page)
    {
        return byPage.ContainsKey(page.Dictionary);
    }

    private void Visit(PdfStructureElement element, TagNode? parent, HashSet<PdfDictionary> seen, int depth)
    {
        if (depth > MaxDepth || !seen.Add(element.Dictionary))
        {
            return;
        }

        string raw = element.Type.Name;
        string type = Pdf20Types.Contains(raw) ? raw : element.StandardType.Name;
        TagNode node = new(element, type, parent, Nodes.Count);
        Nodes.Add(node);
        parent?.Children.Add(node);
        foreach (PdfStructureNode child in element.Children)
        {
            switch (child)
            {
                case PdfStructureElement kid:
                    Visit(kid, node, seen, depth + 1);
                    break;
                case PdfMarkedContentReference marked when marked.Stream is null && (marked.PageDictionary ?? element.PageDictionary) is PdfDictionary page:
                    if (!byPage.TryGetValue(page, out Dictionary<int, TagNode>? ids))
                    {
                        ids = [];
                        byPage[page] = ids;
                    }

                    ids.TryAdd(marked.Mcid, node);
                    marking.Add(node);
                    break;
            }
        }
    }
}
