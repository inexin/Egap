using Egap.Pdf.IO;

namespace Egap.Pdf;

/// <summary>
/// Walks the page tree (7.7.3) into the list of pages, repairing a broken tree in memory as pdf.js, PDFium and qpdf do:
/// <list type="bullet">
/// <item>a node with <c>/Kids</c> is an intermediate node and any other is a page, whatever its <c>/Type</c> says (qpdf);
/// a page without <c>/Type /Page</c> gets one;</item>
/// <item>a kid that is not a dictionary, that loops back to a node above it, or that is plainly some other object (another
/// <c>/Type</c>, or no page entries but an outline item's or annotation's), is skipped and left untouched;</item>
/// <item>a page or node listed twice is listed twice, as PDFium and qpdf count it;</item>
/// <item>a missing or wrong <c>/Parent</c> is set to the node that lists the kid, so inherited attributes are found;</item>
/// <item>a wrong <c>/Count</c> is set to the number of pages found below the node;</item>
/// <item>with no usable page tree at all, the pages are found by scanning the objects for <c>/Type /Page</c>, as pdf.js does.</item>
/// </list>
/// Repairs change the dictionaries without marking them changed, like any repair made while reading: an incremental save
/// leaves the file's bytes as they were, and a rewrite writes the repaired tree. Each repair adds a warning.
/// </summary>
internal static class PageTreeReader
{
    /// <summary>The most nodes visited, against trees that list the same subtree many times over.</summary>
    public const int MaxVisits = 1 << 21;

    private static readonly PdfName Kids = "Kids";
    private static readonly PdfName Type = "Type";
    private static readonly PdfName Parent = "Parent";
    private static readonly PdfName Count = "Count";
    private static readonly PdfName PageType = "Page";
    private static readonly PdfName PagesType = "Pages";
    private static readonly PdfName Contents = "Contents";
    private static readonly PdfName[] PageEntries = ["MediaBox", "CropBox", "Resources", "Rotate"];
    private static readonly PdfName[] OtherEntries = ["Title", "First", "Last", "Next", "Prev", "Dest", "Subtype", "Rect"];

    /// <summary>
    /// The pages in order, and whether the tree is too broken to edit in place: it loops, lists a node or page twice or
    /// directly, skips kids, or is missing. Such a tree is rebuilt before its first edit (see <see cref="PageTreeEditor"/>).
    /// </summary>
    public static (List<PdfPage> Pages, bool NeedsRebuild) Read(PdfDocument document)
    {
        WarningLog log = document.Log;
        PdfObject? rootValue = document.Catalog["Pages"];
        if (rootValue?.Resolve() is not PdfDictionary root)
        {
            log.Add(PdfWarningCode.MalformedPageTree, "The catalog has no page tree; the pages were found by scanning the objects.");
            return (Scan(document), true);
        }

        List<PdfPage> pages = [];
        if (root.Get<PdfArray>(Kids) is null && !IsPagesNode(root))
        {
            log.Add(PdfWarningCode.MalformedPageTree, "The catalog's /Pages is a page, not a page tree; it was read as the only page.", objectNumber: (rootValue as PdfReference)?.ObjectNumber);
            pages.Add(AsPage(root, rootValue as PdfReference, document));
            return (pages, true);
        }

        HashSet<PdfDictionary> ancestors = new(ReferenceEqualityComparer.Instance);
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance) { root };
        bool needsRebuild = rootValue is not PdfReference || root[Parent] is not null;
        Stack<Frame> stack = new();
        Enter(root, rootValue as PdfReference, pages, ancestors, stack, log);
        int visits = 1;
        while (stack.TryPeek(out Frame? frame))
        {
            if (frame.Kids is null || frame.Next >= frame.Kids.Count)
            {
                stack.Pop();
                ancestors.Remove(frame.Node);
                CheckCount(frame, pages.Count - frame.FirstPage, log);
                continue;
            }

            int index = frame.Next++;
            PdfObject item = frame.Kids[index];
            PdfReference? reference = item as PdfReference;
            if (item.Resolve() is not PdfDictionary kid)
            {
                log.Add(PdfWarningCode.MalformedPageTree, $"Kid {index} of a page tree node is {Describe(item)}, not a page or node; it was skipped.", objectNumber: frame.Reference?.ObjectNumber);
                needsRebuild = true;
                continue;
            }

            if (ancestors.Contains(kid))
            {
                log.Add(PdfWarningCode.MalformedPageTree, $"Kid {index} of a page tree node loops back to a node above it; it was skipped.", objectNumber: frame.Reference?.ObjectNumber);
                needsRebuild = true;
                continue;
            }

            if (kid.Get<PdfArray>(Kids) is null && IsOtherObject(kid))
            {
                log.Add(PdfWarningCode.MalformedPageTree, $"Kid {index} of a page tree node is not a page or node; it was skipped.", objectNumber: reference?.ObjectNumber ?? frame.Reference?.ObjectNumber);
                needsRebuild = true;
                continue;
            }

            if (++visits > MaxVisits)
            {
                log.Add(PdfWarningCode.MalformedPageTree, $"The page tree lists more than {MaxVisits} nodes; the rest were skipped.");
                needsRebuild = true;
                break;
            }

            // A kid listed twice, or written in place rather than referenced, cannot be edited as one node of a tree.
            needsRebuild |= reference is null || !seen.Add(kid);

            FixParent(kid, reference, frame, seen, log);
            needsRebuild |= kid[Parent] is not PdfReference;
            if (kid.Get<PdfArray>(Kids) is null && !IsPagesNode(kid))
            {
                pages.Add(AsPage(kid, reference, document));
            }
            else
            {
                Enter(kid, reference, pages, ancestors, stack, log);
            }
        }

        return (pages, needsRebuild);
    }

    /// <summary>A node without /Kids that says it is a /Pages node: an empty node, not a page.</summary>
    private static bool IsPagesNode(PdfDictionary node)
    {
        return node.GetName(Type) == PagesType;
    }

    /// <summary>
    /// A kid without /Kids that is plainly not a page: it has none of a page's own entries, and its /Type names something
    /// else or it has another object's entries (an outline item's /Title or chain, an annotation's /Subtype). Taking it for
    /// a page would write /Type and /Parent into that object and break it. A page with a wrong /Type but its own entries stays a page.
    /// </summary>
    private static bool IsOtherObject(PdfDictionary kid)
    {
        if (PageEntries.Any(kid.ContainsKey) || kid[Contents]?.Resolve() is PdfStream or PdfArray)
        {
            return false;
        }

        return (kid[Type]?.Resolve() is PdfName type && type != PageType && type != PagesType) || OtherEntries.Any(kid.ContainsKey);
    }

    private static void Enter(PdfDictionary node, PdfReference? reference, List<PdfPage> pages, HashSet<PdfDictionary> ancestors, Stack<Frame> stack, WarningLog log)
    {
        if (node[Kids] is PdfObject kids && kids.Resolve() is not PdfArray)
        {
            log.Add(PdfWarningCode.MalformedPageTree, $"A page tree node's /Kids is {Describe(kids)}, not an array; it was read as having no kids.", objectNumber: reference?.ObjectNumber);
            node.SetParsed(Kids, new PdfArray());
        }

        ancestors.Add(node);
        stack.Push(new Frame(node, reference, node.Get<PdfArray>(Kids), pages.Count));
    }

    private static PdfPage AsPage(PdfDictionary page, PdfReference? reference, PdfDocument document)
    {
        WarningLog log = document.Log;
        if (page.GetName(Type) != PageType)
        {
            log.Add(PdfWarningCode.MalformedPageTree, $"A page has {(page[Type] is null ? "no /Type" : $"/Type {Describe(page[Type]!)}")}; it was read as /Type /Page.", objectNumber: reference?.ObjectNumber);
            page.SetParsed(Type, PageType);
        }

        return new PdfPage(page, reference, document);
    }

    /// <summary>
    /// Points a kid's /Parent at the node listing it, unless it already names a node of the tree walked so far that lists
    /// it too (a kid listed twice). A /Parent naming a node outside the tree would send inheritance and edits astray.
    /// </summary>
    private static void FixParent(PdfDictionary kid, PdfReference? reference, Frame frame, HashSet<PdfDictionary> seen, WarningLog log)
    {
        PdfDictionary? parent = kid.Get<PdfDictionary>(Parent);
        if (ReferenceEquals(parent, frame.Node) || frame.Reference is null)
        {
            return;
        }

        if (parent is not null && seen.Contains(parent) && parent.Get<PdfArray>(Kids) is PdfArray siblings && reference is not null && siblings.Contains(reference))
        {
            return;
        }

        log.Add(PdfWarningCode.MalformedPageTree, $"A page tree kid has {(parent is null ? "no /Parent" : "a /Parent that does not list it")}; it was set to the node that does.", objectNumber: reference?.ObjectNumber);
        kid.SetParsed(Parent, frame.Reference);
    }

    private static void CheckCount(Frame frame, int found, WarningLog log)
    {
        // An empty /Pages node may leave /Count out; one that has it must say 0.
        if (frame.Node.GetInt(Count) == found || (frame.Kids is null && frame.Node[Count] is null))
        {
            return;
        }

        log.Add(PdfWarningCode.MalformedPageTree, $"A page tree node has /Count {Describe(frame.Node[Count])} but {found} pages below it; it was set to {found}.", objectNumber: frame.Reference?.ObjectNumber);
        frame.Node.SetParsed(Count, new PdfInteger(found));
    }

    /// <summary>Every dictionary with /Type /Page, in object number order, for a file without a usable page tree.</summary>
    private static List<PdfPage> Scan(PdfDocument document)
    {
        List<PdfPage> pages = [];
        foreach (PdfReference reference in document.Objects)
        {
            if (document.Objects[reference] is PdfDictionary dictionary && dictionary.GetName(Type) == PageType)
            {
                pages.Add(new PdfPage(dictionary, reference, document));
            }
        }

        return pages;
    }

    private static string Describe(PdfObject? value)
    {
        string text = value?.ToString() ?? "missing";
        return text.Length > 40 ? text[..40] + "..." : text;
    }

    /// <summary>A node being walked: its kids, the next kid to visit, and how many pages were found before it.</summary>
    private sealed class Frame(PdfDictionary node, PdfReference? reference, PdfArray? kids, int firstPage)
    {
        public PdfDictionary Node { get; } = node;

        public PdfReference? Reference { get; } = reference;

        public PdfArray? Kids { get; } = kids;

        public int FirstPage { get; } = firstPage;

        public int Next { get; set; }
    }
}
