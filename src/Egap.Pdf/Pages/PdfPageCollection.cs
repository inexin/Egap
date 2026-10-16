using System.Collections;

namespace Egap.Pdf;

/// <summary>
/// A document's pages in order, numbered from 0 (Phase 2 Q2). The list is built on first use by walking the page tree from
/// the catalog; only the tree's nodes are loaded, not the pages' content. A broken tree is repaired in memory with
/// <see cref="PdfWarningCode.MalformedPageTree"/> warnings, as pdf.js and PDFium read it.
/// </summary>
/// <remarks>
/// Edits through this collection change the page tree in place (Phase 2 Q3): only the page, the node that lists it and the
/// <c>/Count</c> of the nodes above are written, so an incremental save stays small; a node is split once it passes 128
/// kids. A tree too broken to edit in place (loops, pages or nodes listed twice, no tree at all) is rebuilt before the
/// first edit, with a warning. A page tree changed by hand through its dictionaries is not seen again by this collection.
/// </remarks>
public sealed class PdfPageCollection : IReadOnlyList<PdfPage>
{
    private readonly PdfDocument document;
    private readonly Lock gate = new();
    private List<PdfPage>? pages;
    private bool needsRebuild;
    private Dictionary<PdfDictionary, int>? indexes;

    internal PdfPageCollection(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>The number of pages.</summary>
    public int Count => List.Count;

    /// <summary>The page at an index, from 0.</summary>
    public PdfPage this[int index]
    {
        get
        {
            List<PdfPage> list = List;
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)list.Count, nameof(index));
            return list[index];
        }
    }

    /// <summary>The index of a page in the document, or -1 when it is not one of its pages.</summary>
    public int IndexOf(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (gate)
        {
            List<PdfPage> list = List;
            if (indexes is null)
            {
                indexes = new(ReferenceEqualityComparer.Instance);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    indexes[list[i].Dictionary] = i;
                }
            }

            return indexes.TryGetValue(page.Dictionary, out int index) ? index : -1;
        }
    }

    /// <summary>
    /// Adds a blank page at the end: no content, empty resources, no rotation. Its media box is <paramref name="mediaBox"/>,
    /// or else the last page's, or US Letter in a document without pages.
    /// </summary>
    public PdfPage AddBlank(PdfRectangle? mediaBox = null)
    {
        lock (gate)
        {
            return InsertBlank(List.Count, mediaBox);
        }
    }

    /// <summary>
    /// Inserts a blank page at an index (0 to <see cref="Count"/>): no content, empty resources, no rotation. Its media box is
    /// <paramref name="mediaBox"/>, or else the media box of the page before it (the first page when inserting at 0), or US
    /// Letter in a document without pages.
    /// </summary>
    public PdfPage InsertBlank(int index, PdfRectangle? mediaBox = null)
    {
        lock (gate)
        {
            List<PdfPage> list = List;
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)list.Count, nameof(index));
            PdfRectangle size = mediaBox?.Normalize() ?? (list.Count == 0 ? PdfPage.DefaultMediaBox : list[Math.Max(index - 1, 0)].MediaBox);
            if (size.IsEmpty || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
            {
                throw new ArgumentException($"A page needs a media box with an area, not {size}.", nameof(mediaBox));
            }

            PdfPage page = Editor().Insert(index, new PdfDictionary { ["MediaBox"] = size.ToArray(), ["Resources"] = new PdfDictionary() });

            // The page sits under nodes whose cropping or rotation it would otherwise inherit.
            if (page.InheritedValue("CropBox") is not null)
            {
                page.Dictionary["CropBox"] = size.ToArray();
            }

            if (page.Rotate != 0)
            {
                page.Dictionary["Rotate"] = new PdfInteger(0);
            }

            return page;
        }
    }

    /// <summary>
    /// Imports pages from another document (Phase 2 task 18): each page is copied with everything it reaches, its resources,
    /// content, annotations and what they use, but not the source's page tree; attributes it inherited there are written
    /// on the copy. Objects are copied once per source (Q4), so pages that share a font share the copy. References to
    /// source pages that are not imported become null. Streams are copied as the source holds them and read when this
    /// document is written, so <paramref name="source"/> must stay open until then.
    /// </summary>
    /// <param name="source">The document to copy from; may be this one, which copies pages within it.</param>
    /// <param name="pageIndices">The source pages, in the order to insert them; all of them when null.</param>
    /// <param name="at">Where the first page goes (0 to <see cref="Count"/>); the end when null.</param>
    /// <param name="options">How form fields whose names clash are handled; renamed by default.</param>
    /// <returns>The new pages, in order.</returns>
    /// <remarks>
    /// The pages bring what they need to keep working (Phase 2 Q5, Q6): their annotations; their form fields, the top-level
    /// ones renamed with a suffix and a warning when the name is taken; the named destinations their links use; the layers
    /// they use; and their page labels. The first import from a source also brings its attachments, renamed with a suffix
    /// when the name is taken, and its outline, appended at the top level without the items that lead to pages not
    /// imported. The structure tree and an XFA form stay behind, with a warning.
    /// </remarks>
    public IReadOnlyList<PdfPage> Import(PdfDocument source, IEnumerable<int>? pageIndices = null, int? at = null, PdfImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<PdfPage> sourcePages = pageIndices is null ? [.. source.Pages] : [.. pageIndices.Select(i => source.Pages[i])];
        lock (gate)
        {
            int index = at ?? List.Count;
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)List.Count, nameof(at));
            Writing.ObjectCopier copier = document.CopierFor(source);
            List<(PdfPageLabelStyle, string?, int)>? labelsBefore = Pages.ImportExtras.LabelsOf(document)
                ?? (source.Pages.GetLabelRanges().Count > 0 ? [.. Enumerable.Range(1, List.Count).Select(n => (PdfPageLabelStyle.Arabic, (string?)null, n))] : null);
            List<PdfPage> added = new(sourcePages.Count);
            foreach (PdfPage page in sourcePages)
            {
                PdfPage made = Editor().Insert(index++, []);
                if (page.Reference is PdfReference reference)
                {
                    copier.MapPage(reference, made.Reference!);
                }

                added.Add(made);
            }

            for (int i = 0; i < sourcePages.Count; i++)
            {
                PdfPage from = sourcePages[i];
                PdfDictionary into = added[i].Dictionary;
                foreach ((PdfName key, PdfObject value) in from.Dictionary.Entries)
                {
                    // Page tree keys stay behind; a page found by repair may carry /Kids, which would make the copy a node.
                    if (key.Value is not ("Parent" or "Type" or "Kids" or "Count"))
                    {
                        into[key] = copier.Copy(value);
                    }
                }

                foreach (PdfName key in new PdfName[] { "Resources", "MediaBox", "CropBox", "Rotate" })
                {
                    if (into.Get(key) is null or PdfNull && from.InheritedValue(key) is PdfObject inherited)
                    {
                        into[key] = copier.Copy(inherited);
                    }
                }
            }

            if (string.CompareOrdinal(source.Version, document.Version) > 0)
            {
                document.Catalog["Version"] = PdfName.Get(source.Version);
            }

            // The structure of the copied pages comes along (Phase 5 design Q14), which Phase 2 dropped with a warning.
            if (!ReferenceEquals(source, document))
            {
                document.Structure.Import(copier, sourcePages);
            }

            Pages.ImportExtras.Bring(document, copier, sourcePages, added, labelsBefore, options ?? PdfImportOptions.Default);
            return added;
        }
    }

    /// <summary>Removes the page at an index from the page tree. Its object stays in the document until nothing reaches it.</summary>
    /// <param name="index">The page's index.</param>
    /// <param name="removeReferences">
    /// Whether what points at the page is cleaned up (Phase 2 Q7): outline items keep their title but lose their
    /// destination, links that lead only to the page are deleted, and named destinations to it are removed, each with a
    /// warning giving the count. False leaves them pointing at a page no longer in the document.
    /// </param>
    public void RemoveAt(int index, bool removeReferences = true)
    {
        lock (gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)List.Count, nameof(index));
            PdfPage removed = List[index];
            Editor().RemoveAt(index);
            if (removeReferences && removed.Reference is PdfReference reference)
            {
                Pages.PageReferences.RemoveTo(document, reference, removed.Dictionary);
            }
        }
    }

    /// <summary>Removes a page from the page tree.</summary>
    /// <returns>Whether it was one of the document's pages.</returns>
    /// <param name="page">The page.</param>
    /// <param name="removeReferences">Whether what points at the page is cleaned up, as <see cref="RemoveAt"/> does.</param>
    public bool Remove(PdfPage page, bool removeReferences = true)
    {
        lock (gate)
        {
            int index = IndexOf(page);
            if (index < 0)
            {
                return false;
            }

            RemoveAt(index, removeReferences);
            return true;
        }
    }

    /// <summary>
    /// Moves the page at <paramref name="from"/> so it ends up at index <paramref name="to"/>. A page that moves under another
    /// node of the tree first gets the attributes it inherited written on itself, so it looks the same.
    /// </summary>
    public void Move(int from, int to)
    {
        lock (gate)
        {
            List<PdfPage> list = List;
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)from, (uint)list.Count, nameof(from));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)to, (uint)list.Count, nameof(to));
            Editor().Move(from, to);
        }
    }

    /// <summary>
    /// The label of the page at an index, as a viewer shows it (12.4.2): from the label range the page falls in, or its
    /// number from 1 when the document has no label for it.
    /// </summary>
    public string GetLabel(int index)
    {
        lock (gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)List.Count, nameof(index));
            PdfPageLabelRange? range = null;
            if (LabelTree() is Trees.NumberTree tree && tree.TryGetFloor(index, out KeyValuePair<long, PdfObject> entry))
            {
                // An entry that is not a label dictionary is passed over, so the range before it goes on.
                range = ToRange(entry) ?? tree.Entries().Where(e => e.Key <= index).Select(ToRange).LastOrDefault(r => r is not null);
            }

            return range is null ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : range.Format(index - range.StartIndex);
        }
    }

    /// <summary>The page label ranges of the catalog's <c>/PageLabels</c>, in order; empty when there are none.</summary>
    public IReadOnlyList<PdfPageLabelRange> GetLabelRanges()
    {
        lock (gate)
        {
            return LabelTree() is Trees.NumberTree tree ? [.. tree.Entries().Select(ToRange).OfType<PdfPageLabelRange>()] : [];
        }
    }

    /// <summary>
    /// Adds a page label range, or replaces the one starting at the same index. Labels belong to page indexes: inserting or
    /// removing pages does not move them.
    /// </summary>
    public void SetLabelRange(PdfPageLabelRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentOutOfRangeException.ThrowIfNegative(range.StartIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(range.Start, 1);
        lock (gate)
        {
            PdfDictionary label = [];
            if (range.Style != PdfPageLabelStyle.None)
            {
                label["S"] = PdfName.Get(range.Style switch
                {
                    PdfPageLabelStyle.Arabic => "D",
                    PdfPageLabelStyle.UpperRoman => "R",
                    PdfPageLabelStyle.LowerRoman => "r",
                    PdfPageLabelStyle.UpperLetters => "A",
                    _ => "a",
                });
            }

            if (range.Prefix is not null)
            {
                label["P"] = PdfString.FromText(range.Prefix);
            }

            if (range.Start != 1)
            {
                label["St"] = new PdfInteger(range.Start);
            }

            Trees.NumberTree tree = EditableLabelTree() ?? new(Created(), document.Objects, document.Log);
            tree.Set(range.StartIndex, label);
        }
    }

    /// <summary>Removes the page label range that starts at an index.</summary>
    /// <returns>Whether there was one.</returns>
    public bool RemoveLabelRange(int startIndex)
    {
        lock (gate)
        {
            return EditableLabelTree() is Trees.NumberTree tree && tree.Remove(startIndex);
        }
    }

    /// <summary>The catalog's /PageLabels number tree, or null when it has none.</summary>
    /// <summary>
    /// The label tree, for an edit: when a damaged file has it share objects with the page tree, its entries are first moved
    /// to a new tree of their own, so editing labels cannot break the pages.
    /// </summary>
    private Trees.NumberTree? EditableLabelTree()
    {
        if (LabelTree() is not Trees.NumberTree tree)
        {
            return null;
        }

        (HashSet<PdfDictionary> pageTree, HashSet<PdfArray> arrays) = Pages.PageReferences.PageTreeParts(document);
        if (!Pages.PageReferences.TreeShares(document.Catalog.Get<PdfDictionary>("PageLabels"), pageTree, arrays))
        {
            return tree;
        }

        List<KeyValuePair<long, PdfObject>> entries = tree.Entries();
        Trees.NumberTree fresh = new(Created(), document.Objects, document.Log);
        foreach ((long key, PdfObject value) in entries)
        {
            if (value.Resolve() is PdfDictionary label)
            {
                fresh.Set(key, label.Clone());
            }
        }

        return fresh;
    }

    private Trees.NumberTree? LabelTree()
    {
        return document.Catalog.Get<PdfDictionary>("PageLabels") is PdfDictionary root ? new(root, document.Objects, document.Log) : null;
    }

    /// <summary>A new, empty /PageLabels in the catalog.</summary>
    private PdfDictionary Created()
    {
        PdfDictionary root = new() { ["Nums"] = new PdfArray() };
        document.Catalog["PageLabels"] = root;
        return root;
    }

    /// <summary>
    /// A label range from a number tree entry: an unknown style counts as none, a start below 1 as 1, as pdf.js reads them.
    /// Null when the key is not a page index or the value not a dictionary.
    /// </summary>
    private static PdfPageLabelRange? ToRange(KeyValuePair<long, PdfObject> entry)
    {
        if (entry.Key is < 0 or > int.MaxValue || entry.Value.Resolve() is not PdfDictionary label)
        {
            return null;
        }

        PdfPageLabelStyle style = label.GetName("S")?.Value switch
        {
            "D" => PdfPageLabelStyle.Arabic,
            "R" => PdfPageLabelStyle.UpperRoman,
            "r" => PdfPageLabelStyle.LowerRoman,
            "A" => PdfPageLabelStyle.UpperLetters,
            "a" => PdfPageLabelStyle.LowerLetters,
            _ => PdfPageLabelStyle.None,
        };
        long start = label.GetInt("St") ?? 1;
        return new PdfPageLabelRange((int)entry.Key, style, label.GetText("P"), start is >= 1 and <= int.MaxValue ? (int)start : 1);
    }

    /// <inheritdoc/>
    public IEnumerator<PdfPage> GetEnumerator()
    {
        return List.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private List<PdfPage> List
    {
        get
        {
            lock (gate)
            {
                if (pages is null)
                {
                    (pages, needsRebuild) = PageTreeReader.Read(document);
                }

                return pages;
            }
        }
    }

    /// <summary>
    /// Rebuilds a tree that was repaired only in memory, such as pages found by scanning, before an edit to a page, so the
    /// edited page is reachable from the catalog when the file is written.
    /// </summary>
    /// <returns>The page as it is in the rebuilt tree: a copy when the rebuild had to make one, as for a direct page.</returns>
    internal PdfPage EnsureTree(PdfPage page)
    {
        lock (gate)
        {
            int index = List.IndexOf(page);
            if (!needsRebuild || index < 0)
            {
                return page;
            }

            Editor();
            return List[index];
        }
    }

    /// <summary>Rebuilds a tree that was repaired only in memory, such as pages found by scanning, before a file is written whole.</summary>
    internal void RebuildIfRepaired()
    {
        lock (gate)
        {
            _ = List;
            if (needsRebuild)
            {
                Editor();
            }
        }
    }

    /// <summary>The editor for the next edit, rebuilding a broken tree first; the index cache is dropped.</summary>
    private PageTreeEditor Editor()
    {
        PageTreeEditor editor = new(document, List);
        if (needsRebuild)
        {
            editor.Rebuild();
            needsRebuild = false;
        }

        indexes = null;
        return editor;
    }
}
