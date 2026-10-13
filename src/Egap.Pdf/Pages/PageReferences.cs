namespace Egap.Pdf.Pages;

/// <summary>
/// Cleans up what points at a page that was removed (Phase 2 Q7): outline items lose their destination but keep their
/// title, links that lead only to the page are deleted, and named destinations to it are removed.
/// </summary>
internal static class PageReferences
{
    public static void RemoveTo(PdfDocument document, PdfReference page, PdfDictionary dictionary)
    {
        bool Leads(PdfDestination? destination)
        {
            return destination?.PageReference is PdfReference to && (to.Equals(page) || ReferenceEquals(to.Resolve(), dictionary));
        }

        // In a damaged file one dictionary can be both a page tree node and something cleaned up here; those are left alone,
        // since editing them would break the page tree.
        (HashSet<PdfDictionary> tree, HashSet<PdfArray> arrays) = PageTreeParts(document);
        tree.Add(dictionary);

        int outline = 0;
        foreach (PdfOutlineItem item in OutlineShares(document, tree) ? [] : document.Outline.GetAllItems())
        {
            if (!tree.Contains(item.Dictionary) && Leads(item.Destination))
            {
                item.Dictionary.Remove("Dest");
                if (item.Action is PdfGoToAction)
                {
                    item.Dictionary.Remove("A");
                }

                outline++;
            }
        }

        int links = 0;
        foreach (PdfPage remaining in document.Pages)
        {
            if (remaining.Dictionary.Get<PdfArray>("Annots") is PdfArray annots && arrays.Contains(annots))
            {
                continue;
            }

            foreach (PdfLinkAnnotation link in remaining.Annotations.OfType<PdfLinkAnnotation>().ToList())
            {
                if (!tree.Contains(link.Dictionary) && Leads(link.Destination))
                {
                    remaining.Annotations.Remove(link);
                    links++;
                }
            }
        }

        int names = 0;
        bool shared = (document.Catalog.Get<PdfDictionary>("Dests") is PdfDictionary old && tree.Contains(old))
            || TreeShares(document.Catalog.Get<PdfDictionary>("Names")?.Get<PdfDictionary>("Dests"), tree, arrays);
        foreach (string name in shared ? [] : document.NamedDestinations.GetNames())
        {
            if (document.NamedDestinations.TryGet(name, out PdfDestination destination) && Leads(destination))
            {
                document.NamedDestinations.Remove(name);
                names++;
            }
        }

        if (outline + links + names > 0)
        {
            document.Log.Add(PdfWarningCode.PageReferencesRemoved, $"A removed page was the destination of {outline} outline items (kept without a destination), {links} links (deleted) and {names} named destinations (removed).", objectNumber: page.ObjectNumber);
        }
    }

    /// <summary>
    /// Whether the outline, read as stored, shares a dictionary with the page tree; reading it through the outline view would
    /// repair its links in place, which would break the page tree.
    /// </summary>
    private static bool OutlineShares(PdfDocument document, HashSet<PdfDictionary> tree)
    {
        Stack<PdfDictionary> pending = [];
        if (document.Catalog.Get<PdfDictionary>("Outlines") is PdfDictionary root)
        {
            pending.Push(root);
        }

        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out PdfDictionary? node) && seen.Count < 100_000)
        {
            if (!seen.Add(node))
            {
                continue;
            }

            if (tree.Contains(node))
            {
                return true;
            }

            foreach (PdfName key in new PdfName[] { "First", "Next", "Last", "Prev" })
            {
                if (node.Get<PdfDictionary>(key) is PdfDictionary linked)
                {
                    pending.Push(linked);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The page tree's dictionaries (pages, their ancestors, the root) and the arrays they hold other than a page's /Annots,
    /// such as /Kids and boxes: what edits elsewhere must not touch when a damaged file shares objects with the tree.
    /// </summary>
    internal static (HashSet<PdfDictionary> Tree, HashSet<PdfArray> Arrays) PageTreeParts(PdfDocument document)
    {
        HashSet<PdfDictionary> tree = new(ReferenceEqualityComparer.Instance);
        foreach (PdfPage remaining in document.Pages)
        {
            HashSet<PdfDictionary> path = new(ReferenceEqualityComparer.Instance);
            for (PdfDictionary? node = remaining.Dictionary; node is not null && path.Add(node) && path.Count < 1024; node = node.Get<PdfDictionary>("Parent"))
            {
                tree.Add(node);
            }
        }

        if (document.Catalog.Get<PdfDictionary>("Pages") is PdfDictionary root)
        {
            tree.Add(root);
        }

        HashSet<PdfArray> arrays = new(tree.SelectMany(d => d.Keys.Where(k => k.Value != "Annots").Select(k => d.Get<PdfArray>(k)).OfType<PdfArray>()), ReferenceEqualityComparer.Instance);
        return (tree, arrays);
    }

    /// <summary>
    /// Whether a name or number tree shares a node, or an array its nodes edit (/Names, /Nums, /Kids, /Limits), with the
    /// page tree.
    /// </summary>
    internal static bool TreeShares(PdfDictionary? root, HashSet<PdfDictionary> tree, HashSet<PdfArray> arrays)
    {
        Stack<PdfDictionary> pending = [];
        if (root is not null)
        {
            pending.Push(root);
        }

        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out PdfDictionary? node) && seen.Count < 100_000)
        {
            if (!seen.Add(node))
            {
                continue;
            }

            if (tree.Contains(node) || new PdfName[] { "Names", "Nums", "Kids", "Limits" }.Any(key => node.Get<PdfArray>(key) is PdfArray array && arrays.Contains(array)))
            {
                return true;
            }

            if (node.Get<PdfArray>("Kids") is PdfArray kids)
            {
                for (int i = 0; i < kids.Count; i++)
                {
                    if (kids.Get(i) is PdfDictionary kid)
                    {
                        pending.Push(kid);
                    }
                }
            }
        }

        return false;
    }
}
