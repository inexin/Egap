using Egap.Pdf.IO;

namespace Egap.Pdf;

/// <summary>
/// Walks the page tree (7.7.3) into the list of pages, repairing a broken tree in memory as pdf.js, PDFium and qpdf do:
/// <list type="bullet">
/// <item>a node with <c>/Kids</c> is an intermediate node and any other is a page, whatever its <c>/Type</c> says (qpdf);
/// a page without <c>/Type /Page</c> gets one;</item>
/// <item>a kid that is not a dictionary, or that loops back to a node above it, is skipped;</item>
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

    public static List<PdfPage> Read(PdfDocument document)
    {
        WarningLog log = document.Log;
        PdfObject? rootValue = document.Catalog["Pages"];
        if (rootValue?.Resolve() is not PdfDictionary root)
        {
            log.Add(PdfWarningCode.MalformedPageTree, "The catalog has no page tree; the pages were found by scanning the objects.");
            return Scan(document);
        }

        List<PdfPage> pages = [];
        if (root.Get<PdfArray>(Kids) is null && !IsPagesNode(root))
        {
            log.Add(PdfWarningCode.MalformedPageTree, "The catalog's /Pages is a page, not a page tree; it was read as the only page.", objectNumber: (rootValue as PdfReference)?.ObjectNumber);
            pages.Add(AsPage(root, rootValue as PdfReference, log));
            return pages;
        }

        HashSet<PdfDictionary> ancestors = new(ReferenceEqualityComparer.Instance);
        Stack<Frame> stack = new();
        Enter(root, rootValue as PdfReference, pages, ancestors, stack);
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
                continue;
            }

            if (ancestors.Contains(kid))
            {
                log.Add(PdfWarningCode.MalformedPageTree, $"Kid {index} of a page tree node loops back to a node above it; it was skipped.", objectNumber: frame.Reference?.ObjectNumber);
                continue;
            }

            if (++visits > MaxVisits)
            {
                log.Add(PdfWarningCode.MalformedPageTree, $"The page tree lists more than {MaxVisits} nodes; the rest were skipped.");
                break;
            }

            FixParent(kid, reference, frame, log);
            if (kid.Get<PdfArray>(Kids) is null && !IsPagesNode(kid))
            {
                pages.Add(AsPage(kid, reference, log));
            }
            else
            {
                Enter(kid, reference, pages, ancestors, stack);
            }
        }

        return pages;
    }

    /// <summary>A node without /Kids that says it is a /Pages node: an empty node, not a page.</summary>
    private static bool IsPagesNode(PdfDictionary node)
    {
        return node.GetName(Type) == PagesType;
    }

    private static void Enter(PdfDictionary node, PdfReference? reference, List<PdfPage> pages, HashSet<PdfDictionary> ancestors, Stack<Frame> stack)
    {
        ancestors.Add(node);
        stack.Push(new Frame(node, reference, node.Get<PdfArray>(Kids), pages.Count));
    }

    private static PdfPage AsPage(PdfDictionary page, PdfReference? reference, WarningLog log)
    {
        if (page.GetName(Type) != PageType)
        {
            log.Add(PdfWarningCode.MalformedPageTree, $"A page has {(page[Type] is null ? "no /Type" : $"/Type {Describe(page[Type]!)}")}; it was read as /Type /Page.", objectNumber: reference?.ObjectNumber);
            page.SetParsed(Type, PageType);
        }

        return new PdfPage(page, reference);
    }

    /// <summary>
    /// Points a kid's /Parent at the node listing it, when it is missing or names a node that does not list the kid. A kid
    /// listed by two nodes keeps the /Parent it has.
    /// </summary>
    private static void FixParent(PdfDictionary kid, PdfReference? reference, Frame frame, WarningLog log)
    {
        PdfDictionary? parent = kid.Get<PdfDictionary>(Parent);
        if (ReferenceEquals(parent, frame.Node) || frame.Reference is null)
        {
            return;
        }

        if (parent?.Get<PdfArray>(Kids) is PdfArray siblings && reference is not null && siblings.Contains(reference))
        {
            return;
        }

        log.Add(PdfWarningCode.MalformedPageTree, $"A page tree kid has {(parent is null ? "no /Parent" : "a /Parent that does not list it")}; it was set to the node that does.", objectNumber: reference?.ObjectNumber);
        kid.SetParsed(Parent, frame.Reference);
    }

    private static void CheckCount(Frame frame, int found, WarningLog log)
    {
        if (frame.Kids is null || frame.Node.GetInt(Count) == found)
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
                pages.Add(new PdfPage(dictionary, reference));
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
