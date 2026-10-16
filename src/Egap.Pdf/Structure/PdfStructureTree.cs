using System.Runtime.CompilerServices;

using Egap.Pdf.Trees;
using Egap.Pdf.Writing;

namespace Egap.Pdf;

/// <summary>
/// A document's logical structure (ISO 32000-1 14.7, Phase 5 design Q14): the tree of structure elements, the role map
/// from the document's own types to standard ones, and the class map of shared attributes. Elements are read from and
/// written to the file's objects directly; the parent tree that leads from page content back to elements is rebuilt
/// from the elements when the document is saved after a change. Page import and merge bring the elements of the pages
/// they copy.
/// </summary>
public sealed class PdfStructureTree
{
    private const int MaxDepth = 512;

    private readonly PdfDocument document;
    private readonly ConditionalWeakTable<PdfDictionary, PdfStructureElement> wrappers = [];
    private readonly ConditionalWeakTable<PdfDictionary, StrongBox<int>> nextMcid = [];

    internal PdfStructureTree(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>Whether the document has a structure tree (<c>/StructTreeRoot</c>).</summary>
    public bool Exists => RootDictionary is not null;

    /// <summary>Whether the document says it is tagged (<c>/MarkInfo /Marked true</c>).</summary>
    public bool IsMarked
    {
        get => document.Catalog.Get<PdfDictionary>("MarkInfo")?.GetBool("Marked") == true;
        set
        {
            PdfDictionary info = document.Catalog.Get<PdfDictionary>("MarkInfo") ?? [];
            info["Marked"] = value ? PdfBoolean.True : PdfBoolean.False;
            document.Catalog["MarkInfo"] = info;
        }
    }

    /// <summary>The top-level elements.</summary>
    public IReadOnlyList<PdfStructureElement> Children => RootDictionary is PdfDictionary root ? [.. Kids(root).OfType<PdfStructureElement>()] : [];

    /// <summary>The single top-level element when there is one, usually a Document element; null otherwise.</summary>
    public PdfStructureElement? Root => Children is [PdfStructureElement only] ? only : null;

    /// <summary>The role map: the document's own types and the types they stand for.</summary>
    public IReadOnlyDictionary<string, string> RoleMap => (RootDictionary?.Get<PdfDictionary>("RoleMap")?.Entries ?? [])
        .Where(e => e.Value.Resolve() is PdfName).ToDictionary(e => e.Key.Value, e => ((PdfName)e.Value.Resolve()!).Value, StringComparer.Ordinal);

    /// <summary>The class map: each class name and its attribute objects.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PdfDictionary>> ClassMap => (RootDictionary?.Get<PdfDictionary>("ClassMap")?.Entries ?? [])
        .ToDictionary(e => e.Key.Value, e => (IReadOnlyList<PdfDictionary>)(e.Value.Resolve() switch
        {
            PdfDictionary d => [d],
            PdfArray a => [.. a.Select(i => i.Resolve()).OfType<PdfDictionary>()],
            _ => [],
        }), StringComparer.Ordinal);

    /// <summary>Whether elements changed since the document was opened or last saved, so the parent tree is rebuilt.</summary>
    internal bool Changed { get; private set; }

    private PdfDictionary? RootDictionary => document.Catalog.Get<PdfDictionary>("StructTreeRoot");

    /// <summary>Adds a top-level element, creating the structure tree and marking the document tagged.</summary>
    public PdfStructureElement Add(PdfStructureType type)
    {
        (PdfDictionary root, PdfReference reference) = EnsureRoot();
        return AddElement(root, reference, type, null);
    }

    /// <summary>Maps the document's own type to a standard one (or to another mapped type) in the role map.</summary>
    public void SetRole(string type, PdfStructureType standard)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        (PdfDictionary root, _) = EnsureRoot();
        PdfDictionary map = root.Get<PdfDictionary>("RoleMap") ?? [];
        map[type] = PdfName.Get(standard.Name);
        root["RoleMap"] = map;
        MarkChanged();
    }

    /// <summary>Every element, depth first.</summary>
    public IEnumerable<PdfStructureElement> Descendants()
    {
        return Walk(Children);
    }

    /// <summary>The element that owns the content tagged with <paramref name="mcid"/> on <paramref name="page"/>, or null.</summary>
    public PdfStructureElement? FindMarkedContent(PdfPage page, int mcid)
    {
        ArgumentNullException.ThrowIfNull(page);
        foreach (PdfStructureElement element in Descendants())
        {
            foreach (PdfMarkedContentReference marked in element.Children.OfType<PdfMarkedContentReference>())
            {
                if (marked.Mcid == mcid && marked.Stream is null && ReferenceEquals(marked.PageDictionary, page.Dictionary))
                {
                    return element;
                }
            }
        }

        return null;
    }

    /// <summary>The standard type a type resolves to through the role map (following chains), or the type itself.</summary>
    internal PdfStructureType Resolve(PdfStructureType type)
    {
        PdfDictionary? map = RootDictionary?.Get<PdfDictionary>("RoleMap");
        string name = type.Name ?? "";
        for (int i = 0; i < 32 && !PdfStructureType.StandardNames.Contains(name) && map?.GetName(name)?.Value is string next; i++)
        {
            name = next;
        }

        return new PdfStructureType(name);
    }

    /// <summary>
    /// The next free marked-content id on a page: one past the highest the page's content uses, found once and counted on
    /// from there as tagging hands them out.
    /// </summary>
    internal int NextMcid(PdfPage page)
    {
        StrongBox<int> next = nextMcid.GetValue(page.Dictionary, _ => new StrongBox<int>(HighestMcid(page) + 1));
        return next.Value++;
    }

    private static int HighestMcid(PdfPage page)
    {
        int highest = -1;
        try
        {
            foreach (PdfOperation operation in page.GetContent())
            {
                if (operation.Operator is "BDC" or "DP" && operation.Operands.Count > 1 && operation.Operands[1].Resolve() is PdfDictionary properties
                    && properties.GetInt("MCID") is long mcid && mcid is >= 0 and < int.MaxValue)
                {
                    highest = Math.Max(highest, (int)mcid);
                }
            }
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            // Unreadable content tags nothing Egap can see; the ids start at 0.
        }

        return highest;
    }

    internal void MarkChanged()
    {
        Changed = true;
    }

    /// <summary>The wrapper of an element dictionary, the same object each time.</summary>
    internal PdfStructureElement Wrap(PdfDictionary dictionary, PdfReference? reference)
    {
        return wrappers.GetValue(dictionary, d => new PdfStructureElement(this, d, reference));
    }

    /// <summary>The children an element or the root lists in <c>/K</c>.</summary>
    internal List<PdfStructureNode> Kids(PdfDictionary owner)
    {
        List<PdfStructureNode> nodes = [];
        PdfDictionary? page = owner.Get<PdfDictionary>("Pg");
        foreach (PdfObject raw in KidEntries(owner))
        {
            if (Node(raw, page) is PdfStructureNode node)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    private static List<PdfObject> KidEntries(PdfDictionary owner)
    {
        PdfObject? k = owner["K"];
        if (k is null)
        {
            return [];
        }

        return k.Resolve() is PdfArray array ? [.. array] : [k];
    }

    private PdfStructureNode? Node(PdfObject raw, PdfDictionary? page)
    {
        switch (raw.Resolve())
        {
            case PdfInteger mcid when mcid.Value is >= 0 and <= int.MaxValue:
                return new PdfMarkedContentReference(page, (int)mcid.Value, null) { Raw = raw };
            case PdfDictionary dictionary when dictionary.GetName("Type")?.Value == "MCR":
                return dictionary.GetInt("MCID") is long id and >= 0 and <= int.MaxValue
                    ? new PdfMarkedContentReference(dictionary.Get<PdfDictionary>("Pg") ?? page, (int)id, dictionary.Get<PdfStream>("Stm")) { Raw = raw }
                    : null;
            case PdfDictionary dictionary when dictionary.GetName("Type")?.Value == "OBJR":
                return new PdfObjectReference(dictionary.Get<PdfDictionary>("Pg") ?? page, dictionary.Get("Obj")?.Resolve()) { Raw = raw };
            case PdfDictionary dictionary when dictionary.ContainsKey("S"):
                PdfStructureElement element = Wrap(dictionary, raw as PdfReference);
                return element;
            default:
                return null;
        }
    }

    /// <summary>Elements depth first from the given ones, each once.</summary>
    internal IEnumerable<PdfStructureElement> Walk(IEnumerable<PdfStructureElement> start)
    {
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Stack<(PdfStructureElement Element, int Depth)> stack = new(start.Reverse().Select(e => (e, 0)));
        while (stack.TryPop(out (PdfStructureElement Element, int Depth) next))
        {
            if (!seen.Add(next.Element.Dictionary) || next.Depth > MaxDepth)
            {
                continue;
            }

            yield return next.Element;
            foreach (PdfStructureElement child in Kids(next.Element.Dictionary).OfType<PdfStructureElement>().Reverse())
            {
                stack.Push((child, next.Depth + 1));
            }
        }
    }

    private (PdfDictionary Root, PdfReference Reference) EnsureRoot()
    {
        if (document.Catalog["StructTreeRoot"] is PdfReference existing && existing.Resolve() is PdfDictionary root)
        {
            return (root, existing);
        }

        PdfDictionary created = new() { ["Type"] = PdfName.Get("StructTreeRoot") };
        PdfReference reference = document.Objects.Add(created);
        document.Catalog["StructTreeRoot"] = reference;
        IsMarked = true;
        MarkChanged();
        return ((PdfDictionary)document.Objects[reference], reference);
    }

    internal PdfStructureElement AddElement(PdfDictionary parent, PdfReference? parentReference, PdfStructureType type, int? index)
    {
        ArgumentException.ThrowIfNullOrEmpty(type.Name, nameof(type));
        PdfDictionary dictionary = new()
        {
            ["Type"] = PdfName.Get("StructElem"),
            ["S"] = PdfName.Get(type.Name),
        };
        if (parentReference is not null)
        {
            dictionary["P"] = parentReference;
        }

        PdfReference reference = document.Objects.Add(dictionary);
        Insert(parent, reference, index);
        return Wrap((PdfDictionary)document.Objects[reference], reference);
    }

    internal PdfMarkedContentReference AddMarkedContent(PdfStructureElement element, PdfPage page, int mcid)
    {
        PdfDictionary owner = element.Dictionary;
        PdfObject raw;
        if (owner.Get<PdfDictionary>("Pg") is null && page.Reference is PdfReference pageReference && KidEntries(owner).Count == 0)
        {
            owner["Pg"] = pageReference;
        }

        if (ReferenceEquals(owner.Get<PdfDictionary>("Pg"), page.Dictionary))
        {
            raw = new PdfInteger(mcid);
        }
        else
        {
            PdfDictionary reference = new() { ["Type"] = PdfName.Get("MCR"), ["MCID"] = new PdfInteger(mcid) };
            if (page.Reference is PdfReference pageRef)
            {
                reference["Pg"] = pageRef;
            }

            raw = reference;
        }

        Insert(owner, raw, null);
        return new PdfMarkedContentReference(page.Dictionary, mcid, null) { Raw = raw };
    }

    internal PdfObjectReference AddObject(PdfStructureElement element, PdfPage page, PdfAnnotation annotation)
    {
        PdfObject target = (PdfObject?)annotation.Reference ?? throw new ArgumentException("The annotation must be on a page (an indirect object) to be tagged.", nameof(annotation));
        PdfDictionary objr = new() { ["Type"] = PdfName.Get("OBJR"), ["Obj"] = target };
        if (page.Reference is PdfReference pageReference)
        {
            objr["Pg"] = pageReference;
        }

        Insert(element.Dictionary, objr, null);
        return new PdfObjectReference(page.Dictionary, annotation.Dictionary) { Raw = objr };
    }

    private void Insert(PdfDictionary owner, PdfObject kid, int? index)
    {
        PdfArray array;
        if (owner["K"] is PdfObject existing && existing.Resolve() is PdfArray list)
        {
            array = list;
        }
        else
        {
            PdfObject? single = owner["K"];
            owner.Remove("K");
            array = single is null ? [] : [single];
            owner["K"] = array;
        }

        if (index is int at && at >= 0 && at < array.Count)
        {
            array.Insert(at, kid);
        }
        else
        {
            array.Add(kid);
        }

        MarkChanged();
    }

    internal bool RemoveKid(PdfDictionary owner, PdfStructureNode child)
    {
        PdfObject? k = owner["K"];
        if (k is null || (child.Raw is null && child is not PdfStructureElement))
        {
            return false;
        }

        if (k.Resolve() is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (Same(array[i], child))
                {
                    array.RemoveAt(i);
                    MarkChanged();
                    return true;
                }
            }

            return false;
        }

        if (Same(k, child))
        {
            owner.Remove("K");
            MarkChanged();
            return true;
        }

        return false;
    }

    private static bool Same(PdfObject a, PdfStructureNode node)
    {
        if (node is PdfStructureElement element)
        {
            return ReferenceEquals(a.Resolve(), element.Dictionary);
        }

        PdfObject b = node.Raw!;
        return ReferenceEquals(a, b) || (a is PdfReference x && b is PdfReference y && x.ObjectNumber == y.ObjectNumber && x.Generation == y.Generation)
            || (a is PdfInteger i && b is PdfInteger j && i.Value == j.Value);
    }

    /// <summary>
    /// Rebuilds the parent tree (14.7.4.4) from the elements, before a save after a change: each page's (or form's) marked
    /// content by MCID, each tagged annotation's element, the keys in <c>/StructParents</c> and <c>/StructParent</c>.
    /// Elements written inline are made indirect objects, as parent tree entries must be.
    /// </summary>
    internal void WriteParentTree()
    {
        if (!Changed || RootDictionary is not PdfDictionary root)
        {
            return;
        }

        Changed = false;
        Dictionary<PdfDictionary, SortedDictionary<int, PdfReference>> marked = new(ReferenceEqualityComparer.Instance);
        List<(PdfDictionary Target, PdfReference Element)> objects = [];
        MakeIndirect(root);
        foreach (PdfStructureElement element in Descendants())
        {
            MakeIndirect(element.Dictionary);
            PdfReference? reference = element.Reference ?? FindReference(element);
            if (reference is null)
            {
                continue;
            }

            foreach (PdfStructureNode kid in Kids(element.Dictionary))
            {
                switch (kid)
                {
                    case PdfMarkedContentReference { Stream: PdfStream stream } inForm:
                        Add(stream.Dictionary, inForm.Mcid, reference);
                        break;
                    case PdfMarkedContentReference { PageDictionary: PdfDictionary page } onPage:
                        Add(page, onPage.Mcid, reference);
                        break;
                    case PdfObjectReference { Target: PdfDictionary target }:
                        objects.Add((target, reference));
                        break;
                    case PdfObjectReference { Target: PdfStream xobject }:
                        objects.Add((xobject.Dictionary, reference));
                        break;
                }
            }
        }

        void Add(PdfDictionary owner, int mcid, PdfReference element)
        {
            if (!marked.TryGetValue(owner, out SortedDictionary<int, PdfReference>? byMcid))
            {
                byMcid = [];
                marked[owner] = byMcid;
            }

            byMcid.TryAdd(mcid, element);
        }

        // Keys: an owner's own key stays when no other owner took it; the rest are numbered after the highest.
        HashSet<long> used = [];
        Dictionary<PdfDictionary, long> keys = new(ReferenceEqualityComparer.Instance);
        foreach (PdfDictionary owner in marked.Keys)
        {
            if (owner.GetInt("StructParents") is long own && own >= 0 && used.Add(own))
            {
                keys[owner] = own;
            }
        }

        foreach ((PdfDictionary target, _) in objects)
        {
            if (!keys.ContainsKey(target) && target.GetInt("StructParent") is long own && own >= 0 && used.Add(own))
            {
                keys[target] = own;
            }
        }

        long next = used.Count == 0 ? 0 : used.Max() + 1;
        PdfDictionary treeRoot = [];
        NumberTree tree = new(treeRoot, document.Objects, document.Log);
        foreach ((PdfDictionary owner, SortedDictionary<int, PdfReference> byMcid) in marked)
        {
            long key = keys.TryGetValue(owner, out long assigned) ? assigned : next++;
            owner["StructParents"] = new PdfInteger(key);
            PdfArray array = [];
            int last = byMcid.Keys.Max();
            for (int mcid = 0; mcid <= last; mcid++)
            {
                array.Add(byMcid.TryGetValue(mcid, out PdfReference? element) ? element : PdfNull.Instance);
            }

            tree.Set(key, document.Objects.Add(array));
        }

        HashSet<PdfDictionary> tagged = new(ReferenceEqualityComparer.Instance);
        foreach ((PdfDictionary target, PdfReference element) in objects)
        {
            if (!tagged.Add(target))
            {
                continue;
            }

            long key = keys.TryGetValue(target, out long assigned) ? assigned : next++;
            target["StructParent"] = new PdfInteger(key);
            tree.Set(key, element);
        }

        // Keys left on pages and annotations that no element reaches any more lead nowhere.
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            if (!marked.ContainsKey(page.Dictionary))
            {
                page.Dictionary.Remove("StructParents");
            }

            foreach (PdfAnnotation annotation in page.Annotations)
            {
                if (!tagged.Contains(annotation.Dictionary))
                {
                    annotation.Dictionary.Remove("StructParent");
                }
            }
        }

        root["ParentTree"] = document.Objects.Add(treeRoot);
        root["ParentTreeNextKey"] = new PdfInteger(next);
    }

    /// <summary>Turns elements written inline in an owner's <c>/K</c> into indirect objects with their <c>/P</c> set.</summary>
    private void MakeIndirect(PdfDictionary owner)
    {
        if (owner["K"]?.Resolve() is not PdfArray array)
        {
            if (owner["K"] is PdfDictionary single && single.ContainsKey("S") && single.GetName("Type")?.Value is null or "StructElem")
            {
                owner.Remove("K");
                PdfReference reference = document.Objects.Add(single);
                owner["K"] = reference;
            }

            return;
        }

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is PdfDictionary inline && inline.ContainsKey("S") && inline.GetName("Type")?.Value is null or "StructElem")
            {
                array[i] = PdfNull.Instance;
                array[i] = document.Objects.Add(inline);
            }
        }
    }

    private PdfReference? FindReference(PdfStructureElement element)
    {
        PdfDictionary? parent = element.Dictionary.Get<PdfDictionary>("P") ?? RootDictionary;
        return parent is null ? null : KidEntries(parent).OfType<PdfReference>().FirstOrDefault(r => ReferenceEquals(r.Resolve(), element.Dictionary));
    }

    /// <summary>
    /// Brings the structure of imported pages (replacing Phase 2's drop): each source element that reaches one of the pages
    /// is copied with the part of its subtree that does, its content and annotation references led to the copied pages,
    /// and the copies are placed under this document's single top element, or at the top. Role and class maps are merged,
    /// this document's entries winning over the source's.
    /// </summary>
    internal void Import(ObjectCopier copier, IReadOnlyList<PdfPage> from)
    {
        PdfStructureTree source = copier.Source.Structure;
        if (!source.Exists || from.Count == 0)
        {
            return;
        }

        HashSet<PdfDictionary> pages = new(from.Select(p => p.Dictionary), ReferenceEqualityComparer.Instance);
        List<PdfReference> copies = [];

        // Two documents with one top element of the same kind (a Document each) merge below one: the source's top
        // element's children come, not the element itself.
        IEnumerable<PdfStructureElement> tops = source.Children;
        if (source.Root is PdfStructureElement sourceTop && Root is PdfStructureElement ownTop && sourceTop.StandardType == ownTop.StandardType)
        {
            tops = sourceTop.Elements;
        }

        foreach (PdfStructureElement top in tops)
        {
            if (Copy(top, 0) is PdfReference copy)
            {
                copies.Add(copy);
            }
        }

        if (copies.Count == 0)
        {
            return;
        }

        (PdfDictionary root, PdfReference rootReference) = EnsureRoot();
        (PdfDictionary parent, PdfReference parentReference) = Root is PdfStructureElement single && single.Reference is PdfReference r ? (single.Dictionary, r) : (root, rootReference);
        foreach (PdfReference copy in copies)
        {
            ((PdfDictionary)document.Objects[copy])["P"] = parentReference;
            Insert(parent, copy, null);
        }

        foreach (string key in (string[])["RoleMap", "ClassMap"])
        {
            if (source.RootDictionary?.Get<PdfDictionary>(key) is PdfDictionary theirs)
            {
                PdfDictionary ours = root.Get<PdfDictionary>(key) ?? [];
                foreach ((PdfName name, PdfObject value) in theirs.Entries)
                {
                    if (ours.Get(name) is null)
                    {
                        ours[name] = copier.Copy(value);
                    }
                }

                root[key] = ours;
            }
        }

        IsMarked = true;
        MarkChanged();

        PdfReference? Copy(PdfStructureElement element, int depth)
        {
            if (depth > MaxDepth)
            {
                return null;
            }

            PdfDictionary? sourcePage = element.Dictionary.Get<PdfDictionary>("Pg");
            bool pageKept = sourcePage is not null && pages.Contains(sourcePage);
            PdfArray kids = [];
            List<PdfReference> childCopies = [];
            foreach (PdfObject raw in KidEntries(element.Dictionary))
            {
                switch (source.Node(raw, sourcePage))
                {
                    case PdfStructureElement child when Copy(child, depth + 1) is PdfReference childCopy:
                        kids.Add(childCopy);
                        childCopies.Add(childCopy);
                        break;
                    case PdfMarkedContentReference when raw.Resolve() is PdfInteger mcid && pageKept:
                        kids.Add(new PdfInteger(mcid.Value));
                        break;
                    case PdfMarkedContentReference marked when marked.PageDictionary is PdfDictionary page && pages.Contains(page):
                        kids.Add(copier.Copy(raw));
                        break;
                    case PdfObjectReference objr when objr.PageDictionary is PdfDictionary page && pages.Contains(page):
                        kids.Add(copier.Copy(raw));
                        break;
                }
            }

            if (kids.Count == 0)
            {
                return null;
            }

            PdfDictionary copy = new() { ["Type"] = PdfName.Get("StructElem") };
            foreach ((PdfName key, PdfObject value) in element.Dictionary.Entries)
            {
                if (key.Value is not ("P" or "K" or "Pg" or "Type" or "ID"))
                {
                    copy[key] = copier.Copy(value);
                }
            }

            if (pageKept && element.Dictionary["Pg"] is PdfObject pg)
            {
                copy["Pg"] = copier.Copy(pg);
            }

            copy["K"] = kids;
            PdfReference made = document.Objects.Add(copy);
            foreach (PdfReference childCopy in childCopies)
            {
                ((PdfDictionary)document.Objects[childCopy])["P"] = made;
            }

            return made;
        }
    }
}
