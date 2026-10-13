namespace Egap.Pdf;

/// <summary>
/// Edits the page tree in place (Phase 2 Q3): an insert, remove or move touches the page, the node that lists it and the
/// <c>/Count</c> of each node above, so an incremental save writes a handful of objects. A node that passes
/// <see cref="MaxKids"/> kids is split in two. A tree too broken to edit in place (loops, nodes or pages listed twice, no
/// tree at all) is rebuilt first: every page gets the attributes it inherited and a place in a new, balanced tree.
/// </summary>
internal sealed class PageTreeEditor(PdfDocument document, List<PdfPage> pages)
{
    /// <summary>The most kids a node holds before it is split in two.</summary>
    public const int MaxKids = 128;

    /// <summary>The kids per node a rebuild makes: half full, so inserts do not split them at once.</summary>
    private const int RebuildKids = MaxKids / 2;

    /// <summary>How far up /Parent links are followed, against loops.</summary>
    private const int MaxDepth = 1024;

    private static readonly PdfName Kids = "Kids";
    private static readonly PdfName Parent = "Parent";
    private static readonly PdfName Count = "Count";
    private static readonly PdfName Type = "Type";

    /// <summary>The attributes a page inherits from the nodes above it (7.7.3.4).</summary>
    private static readonly PdfName[] Inheritable = ["Resources", "MediaBox", "CropBox", "Rotate"];

    /// <summary>Puts a page dictionary that is not in the tree at an index; returns its view.</summary>
    public PdfPage Insert(int index, PdfDictionary page)
    {
        (PdfDictionary parent, PdfReference parentReference, int position) = Slot(index);
        page[Type] = PdfName.Get("Page");
        page[Parent] = parentReference;
        PdfReference reference = document.Objects.Add(page);
        Kid(parent).Insert(position, reference);
        AddToCounts(parent, 1);
        PdfPage view = new((PdfDictionary)document.Objects[reference], reference, document);
        pages.Insert(index, view);
        SplitUp(parent);
        return view;
    }

    /// <summary>Takes the page at an index out of the tree. The page's object stays, unreachable unless referenced elsewhere.</summary>
    public void RemoveAt(int index)
    {
        PdfPage page = pages[index];
        (PdfDictionary parent, int position) = Locate(page);
        Kid(parent).RemoveAt(position);
        AddToCounts(parent, -1);
        pages.RemoveAt(index);
        PruneEmpty(parent);
    }

    /// <summary>Moves the page at <paramref name="from"/> so it ends up at <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        if (from == to)
        {
            return;
        }

        PdfPage page = pages[from];
        (PdfDictionary oldParent, int oldPosition) = Locate(page);
        Kid(oldParent).RemoveAt(oldPosition);
        AddToCounts(oldParent, -1);
        pages.RemoveAt(from);

        (PdfDictionary parent, PdfReference parentReference, int position) = Slot(to);
        if (!ReferenceEquals(parent, oldParent))
        {
            // Under another node it could inherit other attributes: it keeps the ones it has now.
            Materialize(page.Dictionary, document);
            page.Dictionary[Parent] = parentReference;
        }

        Kid(parent).Insert(position, page.Reference!);
        AddToCounts(parent, 1);
        pages.Insert(to, page);
        PruneEmpty(oldParent);
        SplitUp(parent);
    }

    /// <summary>
    /// Rebuilds a broken tree from its pages: each page gets the attributes it inherited, a page listed twice becomes a copy
    /// of its own, a direct page becomes an indirect object, and the catalog's /Pages gets a balanced tree of new nodes. Views
    /// of copied pages are replaced.
    /// </summary>
    public void Rebuild()
    {
        document.Log.Add(PdfWarningCode.MalformedPageTree, $"The page tree could not be edited in place, so it was rebuilt over its {pages.Count} pages.");
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        List<PdfReference> references = new(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            PdfPage page = pages[i];
            Materialize(page.Dictionary, document);
            if (page.Reference is null && seen.Add(page.Dictionary))
            {
                // A direct page moves out of the old tree and becomes an indirect object itself, so views of it stay valid.
                Detach(page.Dictionary);
                PdfReference added = document.Objects.Add(page.Dictionary);
                page = new PdfPage(page.Dictionary, added, document);
                pages[i] = page;
            }
            else if (page.Reference is null || !seen.Add(page.Dictionary))
            {
                PdfDictionary copy = page.Dictionary.Clone();
                PdfReference added = document.Objects.Add(copy);
                page = new PdfPage((PdfDictionary)document.Objects[added], added, document);
                pages[i] = page;
                seen.Add(page.Dictionary);
            }

            // A page found by scanning may carry /Kids, which would make it a node of the new tree.
            page.Dictionary.Remove(Kids);
            page.Dictionary[Type] = PdfName.Get("Page");
            references.Add(page.Reference!);
        }

        // A new root replaces the old one, which may be a page, missing, or part of a loop.
        PdfDictionary root = new() { [Type] = PdfName.Get("Pages") };
        PdfReference rootReference = document.Objects.Add(root);
        document.Catalog["Pages"] = rootReference;
        List<(PdfReference Reference, int Count)> level = [.. references.Select(r => (r, 1))];
        while (level.Count > MaxKids)
        {
            List<(PdfReference, int)> nodes = [];
            foreach ((PdfReference Reference, int Count)[] chunk in level.Chunk(RebuildKids))
            {
                PdfReference node = document.Objects.Add(new PdfDictionary { [Type] = PdfName.Get("Pages") });
                Fill((PdfDictionary)document.Objects[node], node, chunk);
                nodes.Add((node, chunk.Sum(k => k.Count)));
            }

            level = nodes;
        }

        Fill(root, rootReference, level);
    }

    /// <summary>Makes a node hold the given kids, pointing their /Parent at it and setting its /Count.</summary>
    private void Fill(PdfDictionary node, PdfReference reference, IReadOnlyCollection<(PdfReference Reference, int Count)> kids)
    {
        foreach ((PdfReference kid, _) in kids)
        {
            ((PdfDictionary)document.Objects[kid])[Parent] = reference;
        }

        node[Kids] = new PdfArray(kids.Select(k => (PdfObject)k.Reference));
        node[Count] = new PdfInteger(kids.Sum(k => k.Count));
    }

    /// <summary>
    /// Where a page inserted at <paramref name="index"/> goes: before the page now there, or after the last page, or as
    /// the first kid of the root in an empty document.
    /// </summary>
    private (PdfDictionary Parent, PdfReference ParentReference, int Position) Slot(int index)
    {
        if (pages.Count == 0)
        {
            PdfReference rootReference = (PdfReference)document.Catalog["Pages"]!;
            PdfDictionary root = (PdfDictionary)rootReference.Resolve();
            if (root.Get<PdfArray>(Kids) is null)
            {
                root[Kids] = new PdfArray();
            }

            return (root, rootReference, 0);
        }

        PdfPage anchor = pages[Math.Min(index, pages.Count - 1)];
        (PdfDictionary parent, int position) = Locate(anchor);
        return (parent, (PdfReference)anchor.Dictionary[Parent]!, index < pages.Count ? position : position + 1);
    }

    /// <summary>The node that lists a page, and where in its /Kids.</summary>
    private static (PdfDictionary Parent, int Position) Locate(PdfPage page)
    {
        PdfDictionary parent = page.Dictionary.Get<PdfDictionary>(Parent) ?? throw new InvalidOperationException("A page of the tree has no /Parent.");
        int position = Kid(parent).IndexOf(page.Reference!);
        return position >= 0 ? (parent, position) : throw new InvalidOperationException("A page's /Parent does not list it.");
    }

    private static PdfArray Kid(PdfDictionary node)
    {
        return node.Get<PdfArray>(Kids) ?? throw new InvalidOperationException("A page tree node has no /Kids.");
    }

    /// <summary>Adds to the /Count of a node and every node above it.</summary>
    private static void AddToCounts(PdfDictionary node, int change)
    {
        PdfDictionary? at = node;
        for (int depth = 0; at is not null && depth < MaxDepth; depth++)
        {
            at[Count] = new PdfInteger((at.GetInt(Count) ?? 0) + change);
            at = at.Get<PdfDictionary>(Parent);
        }
    }

    /// <summary>Removes nodes left with no kids, from <paramref name="node"/> up, the root excepted.</summary>
    private static void PruneEmpty(PdfDictionary node)
    {
        PdfDictionary at = node;
        for (int depth = 0; depth < MaxDepth && Kid(at).Count == 0 && at.Get<PdfDictionary>(Parent) is PdfDictionary parent; depth++)
        {
            PdfArray siblings = Kid(parent);
            for (int i = 0; i < siblings.Count; i++)
            {
                if (ReferenceEquals(siblings.Get(i), at))
                {
                    siblings.RemoveAt(i);
                    break;
                }
            }

            at = parent;
        }
    }

    /// <summary>
    /// Splits a node with more than <see cref="MaxKids"/> kids, and then its parent if that overflows in turn. The upper
    /// half of the kids moves to a new sibling that copies the node's inheritable attributes; a root that overflows keeps
    /// its place and gets the two halves as its kids.
    /// </summary>
    private void SplitUp(PdfDictionary node)
    {
        for (int depth = 0; depth < MaxDepth && Kid(node).Count > MaxKids; depth++)
        {
            PdfArray kids = Kid(node);
            int half = kids.Count / 2;
            List<PdfObject> upper = [.. kids.Skip(half)];
            for (int i = kids.Count - 1; i >= half; i--)
            {
                kids.RemoveAt(i);
            }

            PdfDictionary? parent = node.Get<PdfDictionary>(Parent);
            if (parent is null)
            {
                List<PdfObject> lower = [.. kids];
                kids.Clear();
                PdfReference self = (PdfReference)document.Catalog["Pages"]!;
                kids.Add(NewNode(lower, self, attributesFrom: null));
                kids.Add(NewNode(upper, self, attributesFrom: null));
                return;
            }

            node[Count] = new PdfInteger(CountOf(kids));
            PdfArray siblings = Kid(parent);
            int at = IndexOf(siblings, node);
            siblings.Insert(at + 1, NewNode(upper, (PdfReference)node[Parent]!, attributesFrom: node));
            node = parent;
        }
    }

    /// <summary>A new intermediate node holding <paramref name="kids"/>, which are moved under it.</summary>
    private PdfReference NewNode(List<PdfObject> kids, PdfReference parent, PdfDictionary? attributesFrom)
    {
        PdfDictionary node = new() { [Type] = PdfName.Get("Pages"), [Parent] = parent };
        foreach (PdfName key in attributesFrom is null ? [] : Inheritable)
        {
            if (attributesFrom![key] is PdfObject value)
            {
                node[key] = Copy(value);
            }
        }

        PdfReference reference = document.Objects.Add(node);
        PdfDictionary added = (PdfDictionary)document.Objects[reference];
        foreach (PdfObject kid in kids)
        {
            ((PdfDictionary)kid.Resolve())[Parent] = reference;
        }

        PdfArray array = new(kids);
        added[Kids] = array;
        added[Count] = new PdfInteger(CountOf(array));
        return reference;
    }

    /// <summary>The pages below the kids: one for each page, a node's /Count for each node.</summary>
    private static int CountOf(PdfArray kids)
    {
        int count = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            // As the reader sees it: a node with /Kids holds /Count pages, an empty /Pages node none, anything else is a page.
            count += kids.Get<PdfDictionary>(i) switch
            {
                PdfDictionary kid when kid.Get<PdfArray>(Kids) is not null => (int)(kid.GetInt(Count) ?? 0),
                PdfDictionary kid when kid.GetName(Type) == PdfName.Get("Pages") => 0,
                _ => 1,
            };
        }

        return count;
    }

    private static int IndexOf(PdfArray kids, PdfDictionary node)
    {
        for (int i = 0; i < kids.Count; i++)
        {
            if (ReferenceEquals(kids.Get(i), node))
            {
                return i;
            }
        }

        throw new InvalidOperationException("A page tree node's /Parent does not list it.");
    }

    /// <summary>Takes a direct dictionary out of the array or dictionary that holds it.</summary>
    private static void Detach(PdfDictionary value)
    {
        switch (((Objects.IContainer)value).Parent)
        {
            case PdfArray array:
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(array[i], value))
                    {
                        array.RemoveAt(i);
                    }
                }

                break;
            case PdfDictionary holder:
                foreach (PdfName key in holder.Keys.Where(k => ReferenceEquals(holder[k], value)).ToList())
                {
                    holder.Remove(key);
                }

                break;
        }
    }

    /// <summary>
    /// Writes the attributes a page inherits onto the page itself, so it keeps them wherever it goes. An attribute it has
    /// from nowhere gets its default written, so it cannot pick up another node's either.
    /// </summary>
    private static void Materialize(PdfDictionary page, PdfDocument document)
    {
        PdfPage view = new(page, null, document);
        foreach (PdfName key in Inheritable)
        {
            // A value that resolves to null, such as a reference to a missing object, counts as absent (7.3.7).
            if (page.Get(key) is null or PdfNull)
            {
                page[key] = view.InheritedValue(key) is PdfObject value ? Copy(value) : key.Value switch
                {
                    "Resources" => new PdfDictionary(),
                    "MediaBox" => view.MediaBox.ToArray(),
                    "CropBox" => view.CropBox.ToArray(),
                    _ => new PdfInteger(0),
                };
            }
        }
    }

    /// <summary>A value that can go in a second place: references and scalars as they are, containers copied.</summary>
    private static PdfObject Copy(PdfObject value)
    {
        return value switch
        {
            PdfArray array => array.Clone(),
            PdfDictionary dictionary => dictionary.Clone(),
            _ => value,
        };
    }
}
