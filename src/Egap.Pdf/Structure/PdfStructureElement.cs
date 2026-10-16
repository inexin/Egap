namespace Egap.Pdf;

/// <summary>A child of a structure element (ISO 32000-1 14.7.2): another element, a marked-content sequence or an object.</summary>
public abstract class PdfStructureNode
{
    private protected PdfStructureNode()
    {
    }

    /// <summary>The <c>/K</c> entry this node was read from, so removing it finds it.</summary>
    internal PdfObject? Raw { get; init; }
}

/// <summary>A marked-content sequence a structure element owns: content tagged with an MCID on a page or in a form XObject.</summary>
public sealed class PdfMarkedContentReference : PdfStructureNode
{
    internal PdfMarkedContentReference(PdfDictionary? page, int mcid, PdfStream? stream)
    {
        PageDictionary = page;
        Mcid = mcid;
        Stream = stream;
    }

    /// <summary>The marked-content identifier, the <c>/MCID</c> of the <c>BDC</c> that opens the sequence.</summary>
    public int Mcid { get; }

    /// <summary>The form XObject holding the sequence, or null when it is in the page's own content.</summary>
    public PdfStream? Stream { get; }

    /// <summary>The page the sequence is on, as its dictionary.</summary>
    public PdfDictionary? PageDictionary { get; }
}

/// <summary>An object a structure element owns (an OBJR): an annotation, such as a link or form widget, or an XObject.</summary>
public sealed class PdfObjectReference : PdfStructureNode
{
    internal PdfObjectReference(PdfDictionary? page, PdfObject? target)
    {
        PageDictionary = page;
        Target = target;
    }

    /// <summary>The object, usually an annotation dictionary.</summary>
    public PdfObject? Target { get; }

    /// <summary>The page the object is on, as its dictionary.</summary>
    public PdfDictionary? PageDictionary { get; }
}

/// <summary>
/// An element of the structure tree (ISO 32000-1 14.7.2, Phase 5 design Q14): its type, the text properties assistive
/// technology reads (title, language, alternate, actual and expanded text), attributes and classes, and its children.
/// Changes are written to the element's dictionary at once; the parent tree is rebuilt when the document is saved.
/// </summary>
public sealed class PdfStructureElement : PdfStructureNode
{
    private readonly PdfStructureTree tree;

    internal PdfStructureElement(PdfStructureTree tree, PdfDictionary dictionary, PdfReference? reference)
    {
        this.tree = tree;
        Dictionary = dictionary;
        Reference = reference;
    }

    /// <summary>The element's dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The element's indirect reference.</summary>
    public PdfReference? Reference { get; }

    /// <summary>The element's type (<c>/S</c>), as the document names it.</summary>
    public PdfStructureType Type
    {
        get => new(Dictionary.GetName("S")?.Value ?? "");
        set => Set("S", PdfName.Get(value.Name));
    }

    /// <summary>The standard type the element's type maps to through the role map, or its own type when no mapping applies.</summary>
    public PdfStructureType StandardType => tree.Resolve(Type);

    /// <summary>The parent element, or null for a top-level element.</summary>
    public PdfStructureElement? Parent => Dictionary.Get<PdfDictionary>("P") is PdfDictionary parent && parent.GetName("Type")?.Value != "StructTreeRoot"
        ? tree.Wrap(parent, Dictionary["P"] as PdfReference)
        : null;

    /// <summary>The title (<c>/T</c>).</summary>
    public string? Title
    {
        get => Dictionary.GetText("T");
        set => SetText("T", value);
    }

    /// <summary>The natural language (<c>/Lang</c>), such as <c>en-US</c>.</summary>
    public string? Language
    {
        get => Dictionary.GetText("Lang");
        set => SetText("Lang", value);
    }

    /// <summary>The alternate description (<c>/Alt</c>) read in place of the content, as every figure needs.</summary>
    public string? AlternateText
    {
        get => Dictionary.GetText("Alt");
        set => SetText("Alt", value);
    }

    /// <summary>The text the content stands for (<c>/ActualText</c>), such as the word a ligature or a drawn letter shows.</summary>
    public string? ActualText
    {
        get => Dictionary.GetText("ActualText");
        set => SetText("ActualText", value);
    }

    /// <summary>The expansion of an abbreviation or acronym (<c>/E</c>).</summary>
    public string? ExpandedText
    {
        get => Dictionary.GetText("E");
        set => SetText("E", value);
    }

    /// <summary>The element identifier (<c>/ID</c>), unique in the document's ID tree.</summary>
    public string? Id => Dictionary.Get<PdfString>("ID")?.ToText();

    /// <summary>The page the element's content is on by default (<c>/Pg</c>), as its dictionary.</summary>
    public PdfDictionary? PageDictionary => Dictionary.Get<PdfDictionary>("Pg");

    /// <summary>The attribute class names (<c>/C</c>), each resolved through the class map.</summary>
    public IReadOnlyList<string> Classes => Dictionary.Get("C")?.Resolve() switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. array.Select(a => a.Resolve()).OfType<PdfName>().Select(n => n.Value)],
        _ => [],
    };

    /// <summary>The attribute objects (<c>/A</c>), each with its owner (<c>/O</c>), such as Layout, Table or List.</summary>
    public IReadOnlyList<PdfDictionary> Attributes => Dictionary.Get("A")?.Resolve() switch
    {
        PdfStream stream => [stream.Dictionary],
        PdfDictionary dictionary => [dictionary],
        PdfArray array => [.. array.Select(a => a.Resolve() switch { PdfStream s => s.Dictionary, PdfDictionary d => d, _ => null }).OfType<PdfDictionary>()],
        _ => [],
    };

    /// <summary>The children in order: elements, marked-content sequences and objects.</summary>
    public IReadOnlyList<PdfStructureNode> Children => tree.Kids(Dictionary);

    /// <summary>The child elements only.</summary>
    public IEnumerable<PdfStructureElement> Elements => Children.OfType<PdfStructureElement>();

    /// <summary>Adds an attribute object owned by <paramref name="owner"/> (such as <c>Layout</c> or <c>Table</c>) with the given entries.</summary>
    public PdfDictionary AddAttributes(string owner, PdfDictionary entries)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(entries);
        entries["O"] = PdfName.Get(owner);
        if (Dictionary.Get("A")?.Resolve() is PdfArray list)
        {
            list.Add(entries);
            tree.MarkChanged();
            return entries;
        }

        // One attribute object before: the array takes it over.
        PdfObject? single = Dictionary["A"];
        Dictionary.Remove("A");
        list = single is null ? [] : [single];
        list.Add(entries);
        Set("A", list);
        return entries;
    }

    /// <summary>Adds a child element at the end, or at <paramref name="index"/>, and returns it.</summary>
    public PdfStructureElement Add(PdfStructureType type, int? index = null)
    {
        return tree.AddElement(Dictionary, Reference, type, index);
    }

    /// <summary>
    /// Records that the content tagged with <paramref name="mcid"/> on <paramref name="page"/> belongs to this element,
    /// as content drawn between <c>/Tag &lt;&lt;/MCID n&gt;&gt; BDC</c> and <c>EMC</c>.
    /// </summary>
    public PdfMarkedContentReference AddMarkedContent(PdfPage page, int mcid)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(mcid);
        return tree.AddMarkedContent(this, page, mcid);
    }

    /// <summary>Records that an annotation (a link, a form widget) belongs to this element, so it is read in order.</summary>
    public PdfObjectReference AddAnnotation(PdfPage page, PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(annotation);
        return tree.AddObject(this, page, annotation);
    }

    /// <summary>Removes a child; a removed element keeps its own children but is no longer in the tree.</summary>
    /// <returns>Whether it was a child.</returns>
    public bool Remove(PdfStructureNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return tree.RemoveKid(Dictionary, child);
    }

    /// <summary>The element and every element below it, depth first.</summary>
    public IEnumerable<PdfStructureElement> DescendantsAndSelf()
    {
        return tree.Walk([this]);
    }

    /// <summary>The type and title.</summary>
    public override string ToString() => Title is { } title ? $"{Type} \"{title}\"" : Type.ToString();

    private void SetText(string key, string? value)
    {
        Set(key, value is null ? null : PdfString.FromText(value));
    }

    private void Set(string key, PdfObject? value)
    {
        if (value is null)
        {
            Dictionary.Remove(key);
        }
        else
        {
            Dictionary[key] = value;
        }

        tree.MarkChanged();
    }
}
