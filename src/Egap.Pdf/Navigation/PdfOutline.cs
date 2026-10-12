using Egap.Pdf.IO;

namespace Egap.Pdf;

/// <summary>
/// A node of the document outline (12.3.3): the outline itself or one of its items. Children are read through
/// <c>/First</c> and <c>/Next</c>; adding, removing and moving keep <c>/First</c>, <c>/Last</c>, <c>/Prev</c>, <c>/Next</c>,
/// <c>/Parent</c> and the <c>/Count</c> of every node above in step, touching only the nodes involved.
/// </summary>
public abstract class PdfOutlineNode
{
    private protected static readonly PdfName FirstKey = "First";
    private protected static readonly PdfName LastKey = "Last";
    private protected static readonly PdfName NextKey = "Next";
    private protected static readonly PdfName PrevKey = "Prev";
    private protected static readonly PdfName ParentKey = "Parent";
    private protected static readonly PdfName CountKey = "Count";

    /// <summary>The most nodes walked through /Parent or /Next links, against loops the repair pass did not see.</summary>
    private protected const int MaxLinks = 1 << 20;

    private protected PdfOutlineNode(PdfDocument document)
    {
        Document = document;
    }

    /// <summary>The node's dictionary; <see langword="null"/> for the outline of a document that has none yet.</summary>
    public abstract PdfDictionary? Dictionary { get; }

    /// <summary>The direct children, in order.</summary>
    public IReadOnlyList<PdfOutlineItem> Children
    {
        get
        {
            OutlineReader.Check(Document);
            List<PdfOutlineItem> children = [];
            HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
            PdfObject? link = Dictionary?[FirstKey];
            while (link is PdfReference reference && reference.Resolve() is PdfDictionary item && seen.Add(item) && children.Count < MaxLinks)
            {
                children.Add(new PdfOutlineItem(Document, item, reference));
                link = item[NextKey];
            }

            return children;
        }
    }

    private protected PdfDocument Document { get; }

    /// <summary>The reference children name as their /Parent.</summary>
    private protected abstract PdfReference SelfReference { get; }

    /// <summary>Adds an item at the end of the children, going to a destination.</summary>
    public PdfOutlineItem Add(string title, PdfDestination? destination = null)
    {
        return Insert(Children.Count, title, destination);
    }

    /// <summary>Inserts an item among the children at an index (0 to their count), going to a destination.</summary>
    public PdfOutlineItem Insert(int index, string title, PdfDestination? destination = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        _ = SelfReference;
        IReadOnlyList<PdfOutlineItem> children = Children;
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)children.Count, nameof(index));
        PdfDictionary dictionary = new() { ["Title"] = PdfString.FromText(title) };
        if (destination is not null)
        {
            dictionary["Dest"] = destination.ToArray();
        }

        PdfReference reference = Document.Objects.Add(dictionary);
        PdfOutlineItem item = new(Document, (PdfDictionary)Document.Objects[reference], reference);
        LinkChild(item, index, children);
        return item;
    }

    /// <summary>Puts an item, not linked anywhere, among the children at an index, and fixes the counts above.</summary>
    internal void LinkChild(PdfOutlineItem item, int index, IReadOnlyList<PdfOutlineItem> children)
    {
        PdfDictionary self = Dictionary!;
        PdfOutlineItem? previous = index > 0 ? children[index - 1] : null;
        PdfOutlineItem? next = index < children.Count ? children[index] : null;
        item.Dictionary[ParentKey] = SelfReference;
        item.Dictionary[PrevKey] = previous?.Reference;
        item.Dictionary[NextKey] = next?.Reference;
        if (previous is null)
        {
            self[FirstKey] = item.Reference;
        }
        else
        {
            previous.Dictionary[NextKey] = item.Reference;
        }

        if (next is null)
        {
            self[LastKey] = item.Reference;
        }
        else
        {
            next.Dictionary[PrevKey] = item.Reference;
        }

        UpdateCountsFromHere();
    }

    /// <summary>Takes a child out of the chain of children and fixes the counts above.</summary>
    internal void UnlinkChild(PdfOutlineItem item)
    {
        PdfDictionary self = Dictionary!;
        PdfObject? previous = item.Dictionary[PrevKey];
        PdfObject? next = item.Dictionary[NextKey];
        if (previous?.Resolve() is PdfDictionary before)
        {
            before[NextKey] = next;
        }
        else
        {
            self[FirstKey] = next;
        }

        if (next?.Resolve() is PdfDictionary after)
        {
            after[PrevKey] = previous;
        }
        else
        {
            self[LastKey] = previous;
        }

        item.Dictionary.Remove(PrevKey);
        item.Dictionary.Remove(NextKey);
        item.Dictionary.Remove(ParentKey);
        UpdateCountsFromHere();
    }

    /// <summary>
    /// Sets /Count on this node and every node above it (12.3.3): the outline's is the number of items shown; an open item's
    /// the number of its descendants shown, a closed item's that number negated as if it were open, none without children.
    /// </summary>
    internal void UpdateCountsFromHere()
    {
        PdfDictionary? root = Document.Catalog.Get<PdfDictionary>("Outlines");
        PdfDictionary? node = Dictionary;
        for (int depth = 0; node is not null && depth < MaxLinks; depth++)
        {
            bool isRoot = ReferenceEquals(node, root);
            int visible = Visible(node);
            long? count = isRoot ? visible : visible == 0 ? null : (node.GetInt(CountKey) is < 0 ? -visible : visible);
            if (node.GetInt(CountKey) != count || (count is null && node[CountKey] is not null))
            {
                node[CountKey] = count is long value ? new PdfInteger(value) : null;
            }

            // An item without /Parent sits under an outline written directly in the catalog.
            node = isRoot ? null : node.Get<PdfDictionary>(ParentKey) ?? root;
        }
    }

    /// <summary>The items shown below a node as if it were open: its children, and theirs below each open child.</summary>
    private protected static int Visible(PdfDictionary node)
    {
        int visible = 0;
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Stack<PdfDictionary> pending = new([node]);
        while (pending.TryPop(out PdfDictionary? at) && seen.Add(at))
        {
            for (PdfDictionary? child = at.Get<PdfDictionary>(FirstKey); child is not null && !seen.Contains(child) && visible < MaxLinks; child = child.Get<PdfDictionary>(NextKey))
            {
                visible++;
                if (child.GetInt(CountKey) > 0)
                {
                    pending.Push(child);
                }

                if (ReferenceEquals(child.Get<PdfDictionary>(NextKey), child))
                {
                    break;
                }
            }
        }

        return visible;
    }
}

/// <summary>
/// The document outline (bookmarks, 12.3.3), from the catalog's <c>/Outlines</c>. A broken outline (chains that loop, wrong
/// <c>/Prev</c>, <c>/Parent</c> or <c>/Last</c>, kids that are not dictionaries) is repaired in memory on first use with
/// <see cref="PdfWarningCode.MalformedOutline"/> warnings, as pdf.js and qpdf read it. The outline dictionary is created by
/// the first item added.
/// </summary>
public sealed class PdfOutline : PdfOutlineNode
{
    internal PdfOutline(PdfDocument document)
        : base(document)
    {
    }

    /// <inheritdoc/>
    public override PdfDictionary? Dictionary => Document.Catalog.Get<PdfDictionary>("Outlines");

    private protected override PdfReference SelfReference
    {
        get
        {
            // The outline dictionary is created, or made an indirect object, so that items can name it as their /Parent.
            if (Document.Catalog["Outlines"] is PdfReference reference && reference.Resolve() is PdfDictionary)
            {
                return reference;
            }

            PdfDictionary root = Dictionary?.Clone() ?? new() { ["Type"] = PdfName.Get("Outlines") };
            PdfReference added = Document.Objects.Add(root);
            Document.Catalog["Outlines"] = added;
            return added;
        }
    }

    /// <summary>Every item, depth first, in the order a viewer lists them.</summary>
    public IReadOnlyList<PdfOutlineItem> GetAllItems()
    {
        List<PdfOutlineItem> items = [];
        Stack<PdfOutlineItem> pending = new(Children.Reverse());
        while (pending.TryPop(out PdfOutlineItem? item) && items.Count < MaxLinks)
        {
            items.Add(item);
            foreach (PdfOutlineItem child in item.Children.Reverse())
            {
                pending.Push(child);
            }
        }

        return items;
    }
}

/// <summary>
/// An outline item (12.3.3): a thin view over its dictionary, equal to another view of the same dictionary. It goes to a
/// destination (<c>/Dest</c>) or runs an action (<c>/A</c>), and has a title, colour, style and open state.
/// </summary>
public sealed class PdfOutlineItem : PdfOutlineNode, IEquatable<PdfOutlineItem>
{
    private readonly PdfDictionary dictionary;

    internal PdfOutlineItem(PdfDocument document, PdfDictionary dictionary, PdfReference reference)
        : base(document)
    {
        this.dictionary = dictionary;
        Reference = reference;
    }

    /// <inheritdoc/>
    public override PdfDictionary Dictionary => dictionary;

    /// <summary>The reference to the item's dictionary.</summary>
    public PdfReference Reference { get; }

    /// <summary>The node the item is under: the outline or another item.</summary>
    public PdfOutlineNode Parent
    {
        get
        {
            PdfObject? parent = Dictionary[ParentKey];
            return parent is PdfReference reference && reference.Resolve() is PdfDictionary node && !ReferenceEquals(node, Document.Catalog.Get<PdfDictionary>("Outlines"))
                ? new PdfOutlineItem(Document, node, reference)
                : Document.Outline;
        }
    }

    /// <summary>The title shown.</summary>
    public string Title
    {
        get => Dictionary.GetText("Title") ?? "";
        set => Dictionary["Title"] = PdfString.FromText(value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Where the item goes: its <c>/Dest</c>, or the destination of its go-to action, named destinations looked up. Setting
    /// one replaces any action.
    /// </summary>
    public PdfDestination? Destination
    {
        get => Dictionary["Dest"] is PdfObject dest ? Document.NamedDestinations.Resolve(dest) : (Action as PdfGoToAction)?.ResolveDestination(Document);
        set
        {
            Dictionary["Dest"] = value?.ToArray();
            if (value is not null)
            {
                Dictionary.Remove("A");
            }
        }
    }

    /// <summary>The action the item runs (<c>/A</c>), or <see langword="null"/>. Setting one replaces any destination.</summary>
    public PdfAction? Action
    {
        get => Dictionary.Get<PdfDictionary>("A") is PdfDictionary action ? PdfAction.FromDictionary(action) : null;
        set
        {
            Dictionary["A"] = value?.Dictionary;
            if (value is not null)
            {
                Dictionary.Remove("Dest");
            }
        }
    }

    /// <summary>The title's colour (<c>/C</c>, PDF 1.4); black when missing. Setting black removes the entry.</summary>
    public PdfRgbColor Color
    {
        get => PdfRgbColor.FromArray(Dictionary.Get<PdfArray>("C")) ?? PdfRgbColor.Black;
        set => Dictionary["C"] = value == PdfRgbColor.Black ? null : value.ToArray();
    }

    /// <summary>Whether the title is italic (<c>/F</c> bit 1).</summary>
    public bool IsItalic
    {
        get => (Flags & 1) != 0;
        set => Flags = value ? Flags | 1 : Flags & ~1;
    }

    /// <summary>Whether the title is bold (<c>/F</c> bit 2).</summary>
    public bool IsBold
    {
        get => (Flags & 2) != 0;
        set => Flags = value ? Flags | 2 : Flags & ~2;
    }

    /// <summary>
    /// Whether the item's children are shown (a positive <c>/Count</c>). An item without children is never open; opening or
    /// closing one updates the counts above it.
    /// </summary>
    public bool IsOpen
    {
        get => Dictionary.GetInt(CountKey) > 0;
        set
        {
            int visible = Visible(Dictionary);
            if (visible == 0 || IsOpen == value)
            {
                return;
            }

            Dictionary[CountKey] = new PdfInteger(value ? visible : -visible);
            Parent.UpdateCountsFromHere();
        }
    }

    private int Flags
    {
        get => (int)(Dictionary.GetInt("F") ?? 0);
        set => Dictionary["F"] = value == 0 ? null : new PdfInteger(value);
    }

    private protected override PdfReference SelfReference => Reference;

    /// <summary>Removes the item, and its children with it, from the outline.</summary>
    public void Remove()
    {
        PdfOutlineNode parent = Parent;
        parent.UnlinkChild(this);
    }

    /// <summary>
    /// Moves the item, children and all, to an index among <paramref name="parent"/>'s children (counted without this item).
    /// The new parent must not be the item or one of its descendants.
    /// </summary>
    public void MoveTo(PdfOutlineNode parent, int index)
    {
        ArgumentNullException.ThrowIfNull(parent);
        PdfOutlineNode? at = parent;
        for (int depth = 0; at is PdfOutlineItem ancestor && depth < MaxLinks; depth++, at = ancestor.Parent)
        {
            if (ancestor.Equals(this))
            {
                throw new ArgumentException("An outline item cannot be moved under itself.", nameof(parent));
            }
        }

        Parent.UnlinkChild(this);
        IReadOnlyList<PdfOutlineItem> children = parent.Children;
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)children.Count, nameof(index));
        parent.LinkChild(this, index, children);
    }

    /// <summary>Whether both views wrap the same item dictionary.</summary>
    public bool Equals(PdfOutlineItem? other)
    {
        return other is not null && ReferenceEquals(Dictionary, other.Dictionary);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfOutlineItem);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Dictionary);
    }

    /// <summary>The title, for debugging.</summary>
    public override string ToString()
    {
        return Title;
    }
}
