using Egap.Pdf.IO;

namespace Egap.Pdf.Trees;

/// <summary>
/// A name tree or number tree (ISO 32000-2, 7.9.6 and 7.9.7), read and edited in place over the dictionaries of the file.
/// Lookups walk down by <c>/Limits</c>; inserts and removes touch only the nodes on the path, split a node once it passes
/// <see cref="MaxNodeSize"/> kids or entries, and keep every <c>/Limits</c> on the path right (Phase 2 Q3). A broken tree
/// (loops, missing or wrong <c>/Limits</c>, unsorted or malformed entries) is read as far as it makes sense with a warning,
/// as pdf.js and PDFium do; editing one first rebuilds it from its entries.
/// </summary>
/// <typeparam name="TKey">The key type: a string for name trees, an integer for number trees.</typeparam>
internal abstract class PdfTree<TKey>
{
    /// <summary>The most kids or entries a node holds before it is split in two.</summary>
    public const int MaxNodeSize = 128;

    /// <summary>The size of the leaves and nodes a rebuild makes: half full, so inserts do not split them at once.</summary>
    private const int RebuildNodeSize = MaxNodeSize / 2;

    private static readonly PdfName Kids = "Kids";
    private static readonly PdfName Limits = "Limits";

    private readonly PdfObjectTable objects;
    private readonly WarningLog log;
    private bool warned;
    private bool checkedForEdit;

    private protected PdfTree(PdfDictionary root, PdfObjectTable objects, WarningLog log)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
        this.objects = objects;
        this.log = log;
    }

    /// <summary>The root node, which may be a direct dictionary (as in the catalog's <c>/Names</c>).</summary>
    public PdfDictionary Root { get; }

    /// <summary>The key of a leaf's array: <c>/Names</c> or <c>/Nums</c>.</summary>
    private protected abstract PdfName EntriesKey { get; }

    /// <summary>"name tree" or "number tree", for warnings.</summary>
    private protected abstract string Kind { get; }

    /// <summary>
    /// Every entry in key order, values as stored (references not followed). A broken tree gives what can be read, sorted,
    /// with the first of duplicate keys kept, and one warning.
    /// </summary>
    public List<KeyValuePair<TKey, PdfObject>> Entries()
    {
        List<KeyValuePair<TKey, PdfObject>> entries = Read(out bool broken);
        if (broken)
        {
            Warn("its nodes loop, are malformed, have wrong limits or hold entries out of order");
            List<KeyValuePair<TKey, PdfObject>> sorted = [.. entries.Index().OrderBy(e => e.Item.Key, Comparer<TKey>.Create(Compare)).ThenBy(e => e.Index).Select(e => e.Item)];
            entries.Clear();
            foreach (KeyValuePair<TKey, PdfObject> entry in sorted)
            {
                if (entries.Count == 0 || Compare(entries[^1].Key, entry.Key) != 0)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    /// <summary>Every entry in tree order, and whether anything in the tree is malformed.</summary>
    private List<KeyValuePair<TKey, PdfObject>> Read(out bool broken)
    {
        List<KeyValuePair<TKey, PdfObject>> entries = [];
        broken = false;
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Stack<PdfDictionary> pending = new([Root]);
        seen.Add(Root);
        while (pending.TryPop(out PdfDictionary? node))
        {
            broken |= node != Root && !LimitsMatch(node);
            if (node[EntriesKey] is not null || node[Kids] is null)
            {
                broken |= !ReadLeaf(node, entries, node != Root && node[Kids] is null);
            }

            if (node[Kids] is null)
            {
                continue;
            }

            broken |= node[EntriesKey] is not null;
            if (node.Get<PdfArray>(Kids) is not PdfArray kids)
            {
                broken = true;
                continue;
            }

            for (int i = kids.Count - 1; i >= 0; i--)
            {
                if (kids.Get<PdfDictionary>(i) is not PdfDictionary kid || !seen.Add(kid))
                {
                    broken = true;
                    continue;
                }

                pending.Push(kid);
            }
        }

        for (int i = 1; i < entries.Count && !broken; i++)
        {
            broken = Compare(entries[i - 1].Key, entries[i].Key) >= 0;
        }

        return entries;
    }

    /// <summary>Finds the value stored for a key, as stored (references not followed).</summary>
    public bool TryGet(TKey key, out PdfObject value)
    {
        if (Descend(key, exact: true) is { } path)
        {
            PdfArray leaf = path[^1].Node.Get<PdfArray>(EntriesKey)!;
            int index = Search(leaf, key);
            if (index >= 0)
            {
                value = leaf[(index * 2) + 1];
                return true;
            }

            value = PdfNull.Instance;
            return false;
        }

        foreach (KeyValuePair<TKey, PdfObject> entry in Entries())
        {
            if (Compare(entry.Key, key) == 0)
            {
                value = entry.Value;
                return true;
            }
        }

        value = PdfNull.Instance;
        return false;
    }

    /// <summary>
    /// The entry with the greatest key not above <paramref name="key"/>, as page labels need (12.4.2); false when every key is
    /// above it.
    /// </summary>
    public bool TryGetFloor(TKey key, out KeyValuePair<TKey, PdfObject> entry)
    {
        if (Descend(key, exact: false) is { } path)
        {
            PdfArray leaf = path[^1].Node.Get<PdfArray>(EntriesKey)!;
            int index = Search(leaf, key);
            index = index >= 0 ? index : ~index - 1;
            if (index >= 0)
            {
                entry = new(ReadKey(leaf.Get(index * 2))!.Value.Key, leaf[(index * 2) + 1]);
                return true;
            }

            entry = default;
            return false;
        }

        entry = default;
        bool found = false;
        foreach (KeyValuePair<TKey, PdfObject> candidate in Entries())
        {
            if (Compare(candidate.Key, key) > 0)
            {
                break;
            }

            entry = candidate;
            found = true;
        }

        return found;
    }

    /// <summary>Sets the value for a key, adding the entry or replacing its value.</summary>
    public void Set(TKey key, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        List<(PdfDictionary Node, int Index)> path = DescendForEdit(key);
        PdfDictionary leafNode = path[^1].Node;
        if (leafNode.Get<PdfArray>(EntriesKey) is not PdfArray leaf)
        {
            leaf = [];
            leafNode[EntriesKey] = leaf;
        }

        int index = Search(leaf, key);
        if (index >= 0)
        {
            leaf[(index * 2) + 1] = value;
            return;
        }

        index = ~index;
        leaf.Insert(index * 2, WriteKey(key));
        leaf.Insert((index * 2) + 1, value);

        // Limits first, so a split gives its new node the right ones, then again for the nodes the split changed.
        FixLimits(path);
        SplitUp(path);
        FixLimits(path);
    }

    /// <summary>Removes the entry for a key.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Remove(TKey key)
    {
        List<(PdfDictionary Node, int Index)> path = DescendForEdit(key);
        if (path[^1].Node.Get<PdfArray>(EntriesKey) is not PdfArray leaf || Search(leaf, key) is var index && index < 0)
        {
            return false;
        }

        leaf.RemoveAt((index * 2) + 1);
        leaf.RemoveAt(index * 2);

        // Empty nodes below the root leave their parent; the root stays, as an empty leaf when nothing is left.
        int depth = path.Count - 1;
        while (depth > 0 && IsEmpty(path[depth].Node))
        {
            path[depth - 1].Node.Get<PdfArray>(Kids)!.RemoveAt(path[depth].Index);
            path.RemoveAt(depth);
            depth--;
        }

        if (depth == 0 && Root.Get<PdfArray>(Kids) is { Count: 0 })
        {
            Root.Remove(Kids);
            Root[EntriesKey] = new PdfArray();
        }

        FixLimits(path);
        return true;
    }

    /// <summary>Reads a key; null when the object cannot be one. <c>Repaired</c> says the key was read leniently.</summary>
    private protected abstract (TKey Key, bool Repaired)? ReadKey(PdfObject key);

    private protected abstract PdfObject WriteKey(TKey key);

    private protected abstract int Compare(TKey a, TKey b);

    /// <summary>
    /// The nodes from the root to the leaf that holds <paramref name="key"/> (or, with <paramref name="exact"/> false, the
    /// leaf whose range is the last to start at or below it), each with its index in its parent's <c>/Kids</c>. Null when
    /// the way down is broken, so the caller falls back to reading every entry.
    /// </summary>
    private List<(PdfDictionary Node, int Index)>? Descend(TKey key, bool exact)
    {
        List<(PdfDictionary Node, int Index)> path = [(Root, -1)];
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance) { Root };
        PdfDictionary node = Root;
        while (node[Kids] is not null)
        {
            if (node[EntriesKey] is not null || ReadKids(node) is not { } kids || !IsOrdered(kids))
            {
                return null;
            }

            int chosen = Choose(kids, key, exact);
            if (chosen < 0)
            {
                // The key is outside every kid: a well-formed tree does not hold it.
                return [(EmptyLeaf, -1)];
            }

            node = kids[chosen].Node;
            if (!seen.Add(node))
            {
                return null;
            }

            path.Add((node, chosen));
        }

        return node.Get<PdfArray>(EntriesKey) is PdfArray leaf && IsSortedLeaf(leaf) ? path
            : node[EntriesKey] is null && node == Root ? [(EmptyLeaf, -1)]
            : null;
    }

    /// <summary>A leaf with no entries, for lookups that end outside every kid.</summary>
    private PdfDictionary EmptyLeaf => new() { [EntriesKey] = new PdfArray() };

    /// <summary>
    /// The kid to go down to: for an exact lookup the one whose limits hold the key, or -1; otherwise the last kid starting
    /// at or below the key, or -1 when every kid starts above it.
    /// </summary>
    private int Choose(List<(PdfDictionary Node, TKey Low, TKey High)> kids, TKey key, bool exact)
    {
        int low = 0;
        int high = kids.Count - 1;
        int floor = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (Compare(key, kids[middle].Low) < 0)
            {
                high = middle - 1;
            }
            else
            {
                floor = middle;
                if (Compare(key, kids[middle].High) <= 0)
                {
                    return middle;
                }

                low = middle + 1;
            }
        }

        return exact ? -1 : floor;
    }

    /// <summary>
    /// The path to the leaf where <paramref name="key"/> is or belongs, for an edit. The first edit through this view reads
    /// the whole tree once, since a wrong <c>/Limits</c> anywhere would send keys to the wrong leaf; a broken tree is
    /// rebuilt. Later edits through the same view only walk their path.
    /// </summary>
    private List<(PdfDictionary Node, int Index)> DescendForEdit(TKey key)
    {
        if (!checkedForEdit)
        {
            checkedForEdit = true;
            Read(out bool broken);
            if (broken)
            {
                Rebuild();
            }
        }

        if (TryDescendForEdit(key) is { } path)
        {
            return path;
        }

        Rebuild();
        return TryDescendForEdit(key)!;
    }

    private List<(PdfDictionary Node, int Index)>? TryDescendForEdit(TKey key)
    {
        List<(PdfDictionary Node, int Index)> path = [(Root, -1)];
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance) { Root };
        PdfDictionary node = Root;
        while (node[Kids] is not null)
        {
            if (node[EntriesKey] is not null || ReadKids(node) is not { Count: > 0 } kids || !IsOrdered(kids))
            {
                return null;
            }

            // The first kid whose range ends at or after the key, else the last: the key joins the nearest range.
            int chosen = kids.FindIndex(k => Compare(key, k.High) <= 0);
            chosen = chosen < 0 ? kids.Count - 1 : chosen;
            node = kids[chosen].Node;
            if (!seen.Add(node))
            {
                return null;
            }

            path.Add((node, chosen));
        }

        PdfObject? entries = node[EntriesKey];
        return entries is null ? (node == Root ? path : null)
            : node.Get<PdfArray>(EntriesKey) is PdfArray leaf && entries is PdfArray && IsSortedLeaf(leaf) ? path
            : null;
    }

    /// <summary>
    /// A node's kids with their limits, or null when one is not a dictionary or has no usable <c>/Limits</c>. Kids written
    /// directly rather than as references are accepted.
    /// </summary>
    private List<(PdfDictionary Node, TKey Low, TKey High)>? ReadKids(PdfDictionary node)
    {
        if (node.Get<PdfArray>(Kids) is not PdfArray array)
        {
            return null;
        }

        List<(PdfDictionary, TKey, TKey)> kids = new(array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            if (array.Get<PdfDictionary>(i) is not PdfDictionary kid || kid.Get<PdfArray>(Limits) is not { Count: 2 } limits
                || ReadKey(limits.Get(0)) is not { Repaired: false } low || ReadKey(limits.Get(1)) is not { Repaired: false } high
                || Compare(low.Key, high.Key) > 0)
            {
                return null;
            }

            kids.Add((kid, low.Key, high.Key));
        }

        return kids;
    }

    /// <summary>Whether the kids' ranges are in order and do not overlap, which binary search and edits rely on.</summary>
    private bool IsOrdered(List<(PdfDictionary Node, TKey Low, TKey High)> kids)
    {
        for (int i = 1; i < kids.Count; i++)
        {
            if (Compare(kids[i - 1].High, kids[i].Low) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a leaf array is whole pairs of well-formed keys in strictly increasing order.</summary>
    private bool IsSortedLeaf(PdfArray leaf)
    {
        if (leaf.Count % 2 != 0)
        {
            return false;
        }

        TKey? previous = default;
        for (int i = 0; i < leaf.Count; i += 2)
        {
            if (ReadKey(leaf.Get(i)) is not { Repaired: false } key || (i > 0 && Compare(previous!, key.Key) >= 0))
            {
                return false;
            }

            previous = key.Key;
        }

        return true;
    }

    /// <summary>The pair index of a key in a sorted leaf, or the bitwise complement of where it would go.</summary>
    private int Search(PdfArray leaf, TKey key)
    {
        int low = 0;
        int high = (leaf.Count / 2) - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            int order = Compare(ReadKey(leaf.Get(middle * 2))!.Value.Key, key);
            if (order == 0)
            {
                return middle;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }

    /// <summary>Reads a leaf's pairs into <paramref name="entries"/>; false when anything was malformed.</summary>
    private bool ReadLeaf(PdfDictionary node, List<KeyValuePair<TKey, PdfObject>> entries, bool required)
    {
        if (node.Get<PdfArray>(EntriesKey) is not PdfArray leaf)
        {
            return !required && node[EntriesKey] is null;
        }

        bool ok = leaf.Count % 2 == 0;
        for (int i = 0; i + 1 < leaf.Count; i += 2)
        {
            if (ReadKey(leaf.Get(i)) is { } key)
            {
                ok &= !key.Repaired;
                entries.Add(new(key.Key, leaf[i + 1]));
            }
            else
            {
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>Splits every node on the path that holds more than <see cref="MaxNodeSize"/> kids or entries, leaf first.</summary>
    private void SplitUp(List<(PdfDictionary Node, int Index)> path)
    {
        for (int depth = path.Count - 1; depth >= 0; depth--)
        {
            PdfDictionary node = path[depth].Node;
            bool leaf = node[Kids] is null;
            PdfName key = leaf ? EntriesKey : Kids;
            PdfArray items = node.Get<PdfArray>(key)!;
            int width = leaf ? 2 : 1;
            if (items.Count / width <= MaxNodeSize)
            {
                return;
            }

            // The upper half moves to a new node; items leave the old array before joining the new one.
            int half = items.Count / width / 2 * width;
            List<PdfObject> upper = [.. items.Skip(half)];
            for (int i = items.Count - 1; i >= half; i--)
            {
                items.RemoveAt(i);
            }

            PdfDictionary sibling = new() { [key] = new PdfArray(upper) };
            if (depth == 0)
            {
                // The root stays where it is: its two halves become its kids.
                List<PdfObject> lower = [.. items];
                items.Clear();
                PdfDictionary first = new() { [key] = new PdfArray(lower) };
                Root.Remove(key);
                Root[Kids] = new PdfArray([objects.Add(first), objects.Add(sibling)]);
                SetLimits(first);
                SetLimits(sibling);
                path.Insert(1, (first, 0));
                return;
            }

            SetLimits(sibling);
            PdfArray parentKids = path[depth - 1].Node.Get<PdfArray>(Kids)!;
            parentKids.Insert(path[depth].Index + 1, objects.Add(sibling));
        }
    }

    /// <summary>Sets <c>/Limits</c> on every node of the path below the root, from the bottom up.</summary>
    private void FixLimits(List<(PdfDictionary Node, int Index)> path)
    {
        for (int depth = path.Count - 1; depth > 0; depth--)
        {
            SetLimits(path[depth].Node);
        }
    }

    /// <summary>
    /// What a node's <c>/Limits</c> should be: its own first and last keys, or its first kid's low and last kid's high
    /// limit. Null for an empty node.
    /// </summary>
    private (PdfObject Low, PdfObject High)? ExpectedLimits(PdfDictionary node)
    {
        PdfObject? low;
        PdfObject? high;
        if (node.Get<PdfArray>(Kids) is PdfArray kids)
        {
            low = kids.Count > 0 ? kids.Get<PdfDictionary>(0)?.Get<PdfArray>(Limits) is { Count: 2 } first ? first.Get(0) : null : null;
            high = kids.Count > 0 ? kids.Get<PdfDictionary>(kids.Count - 1)?.Get<PdfArray>(Limits) is { Count: 2 } last ? last.Get(1) : null : null;
        }
        else
        {
            PdfArray leaf = node.Get<PdfArray>(EntriesKey) ?? [];
            low = leaf.Count >= 2 ? leaf.Get(0) : null;
            high = leaf.Count >= 2 ? leaf.Get(leaf.Count - 2) : null;
        }

        return low is null || high is null ? null : (low, high);
    }

    /// <summary>Whether a node below the root has the <c>/Limits</c> its contents call for.</summary>
    private bool LimitsMatch(PdfDictionary node)
    {
        return ExpectedLimits(node) is { } expected && node.Get<PdfArray>(Limits) is { Count: 2 } limits
            && ReadKey(limits.Get(0)) is { Repaired: false } low && ReadKey(expected.Low) is { } expectedLow && Compare(low.Key, expectedLow.Key) == 0
            && ReadKey(limits.Get(1)) is { Repaired: false } high && ReadKey(expected.High) is { } expectedHigh && Compare(high.Key, expectedHigh.Key) == 0;
    }

    /// <summary>Sets a node's <c>/Limits</c> from its own entries or its kids' limits; an empty node gets none.</summary>
    private void SetLimits(PdfDictionary node)
    {
        if (ExpectedLimits(node) is not (PdfObject low, PdfObject high))
        {
            node.Remove(Limits);
            return;
        }

        // Keys are strings or numbers, never containers, so they can sit in both places.
        if (node.Get<PdfArray>(Limits) is { Count: 2 } limits && limits[0].Equals(low) && limits[1].Equals(high))
        {
            return;
        }

        node[Limits] = new PdfArray([low, high]);
    }

    private bool IsEmpty(PdfDictionary node)
    {
        return node.Get<PdfArray>(node[Kids] is null ? EntriesKey : Kids) is not { Count: > 0 };
    }

    /// <summary>
    /// Rebuilds a broken tree from its entries: the root is kept and filled again, as one leaf or over new half-full nodes.
    /// The old nodes are no longer reachable and drop out of a full rewrite.
    /// </summary>
    private void Rebuild()
    {
        List<KeyValuePair<TKey, PdfObject>> entries = Entries();
        log.Add(PdfWarningCode.MalformedTree, $"A {Kind} could not be edited in place, so it was rebuilt from its {entries.Count} entries.");

        // Values leave their old arrays first: a direct array or dictionary belongs to one place.
        List<(PdfObject Key, PdfObject Value)> pairs = [.. entries.Select(e => (WriteKey(e.Key), Detach(e.Value)))];
        Root.Remove(Kids);
        Root.Remove(EntriesKey);
        Root.Remove(Limits);
        if (pairs.Count <= MaxNodeSize)
        {
            Root[EntriesKey] = new PdfArray(pairs.SelectMany(p => new[] { p.Key, p.Value }));
            return;
        }

        List<PdfDictionary> level = [];
        foreach (var chunk in pairs.Chunk(RebuildNodeSize))
        {
            PdfDictionary leaf = new() { [EntriesKey] = new PdfArray(chunk.SelectMany(p => new[] { p.Key, p.Value })) };
            SetLimits(leaf);
            level.Add(leaf);
        }

        while (level.Count > MaxNodeSize)
        {
            List<PdfDictionary> parents = [];
            foreach (PdfDictionary[] chunk in level.Chunk(RebuildNodeSize))
            {
                PdfDictionary parent = new() { [Kids] = new PdfArray(chunk.Select(n => (PdfObject)objects.Add(n))) };
                SetLimits(parent);
                parents.Add(parent);
            }

            level = parents;
        }

        Root[Kids] = new PdfArray(level.Select(n => (PdfObject)objects.Add(n)));
    }

    /// <summary>A value that can be put in a new place: a direct array or dictionary is taken out of its old one.</summary>
    private static PdfObject Detach(PdfObject value)
    {
        return value switch
        {
            PdfArray array => array.Clone(),
            PdfDictionary dictionary => dictionary.Clone(),
            _ => value,
        };
    }

    private void Warn(string what)
    {
        if (!warned)
        {
            warned = true;
            log.Add(PdfWarningCode.MalformedTree, $"A {Kind} was malformed: {what}.");
        }
    }
}
