namespace Egap.Pdf;

/// <summary>
/// Repairs a broken outline in memory, once per document, before its first use (12.3.3), as pdf.js and qpdf read it: each
/// chain of children is followed through <c>/First</c> and <c>/Next</c> and cut where it loops, reaches an item already in
/// the outline, or reaches something that is not a dictionary or is the catalog, a page or a page tree node; each item's <c>/Parent</c> and <c>/Prev</c>, and each node's
/// <c>/Last</c>, are set to what the chain says, and each <c>/Count</c> to the number of items shown, keeping an item closed
/// when its count is negative (a leaf's <c>/Count 0</c> is left alone).
/// Repairs change the dictionaries without marking them changed, like any repair made while reading.
/// </summary>
internal static class OutlineReader
{
    private static readonly PdfName First = "First";
    private static readonly PdfName Last = "Last";
    private static readonly PdfName Next = "Next";
    private static readonly PdfName Prev = "Prev";
    private static readonly PdfName Parent = "Parent";
    private static readonly PdfName CountKey = "Count";
    private static readonly PdfName Kids = "Kids";
    private static readonly PdfName Type = "Type";

    public static void Check(PdfDocument document)
    {
        if (document.OutlineChecked)
        {
            return;
        }

        lock (document.OutlineGate)
        {
            if (document.OutlineChecked)
            {
                return;
            }

            if (document.Catalog["Outlines"] is PdfObject rootValue && rootValue.Resolve() is PdfDictionary root)
            {
                Repair(document, root, rootValue as PdfReference);
            }

            document.OutlineChecked = true;
        }
    }

    private static void Repair(PdfDocument document, PdfDictionary root, PdfReference? rootReference)
    {
        if (root[Parent] is not null)
        {
            Fix(document, root, Parent, null, rootReference, "/Parent");
        }

        List<PdfDictionary> order = [];
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance) { root };
        Stack<(PdfDictionary Node, PdfReference? Reference)> pending = new([(root, rootReference)]);
        while (pending.TryPop(out (PdfDictionary Node, PdfReference? Reference) at))
        {
            (PdfDictionary node, PdfReference? nodeReference) = at;
            order.Add(node);
            PdfDictionary? previous = null;
            PdfReference? previousReference = null;
            PdfObject? link = node[First];
            while (link is not null)
            {
                string? problem = link is not PdfReference ? "is not a reference to an item"
                    : link.Resolve() is not PdfDictionary ? "is not a dictionary"
                    : seen.Contains((PdfDictionary)link.Resolve()) ? "loops back to an item already in the outline"
                    : IsOtherObject(document, (PdfDictionary)link.Resolve()) ? "is the catalog, a page or a page tree node"
                    : null;
                if (problem is not null)
                {
                    document.Log.Add(PdfWarningCode.MalformedOutline, $"An outline item's next {problem}; the chain was cut there.", objectNumber: previousReference?.ObjectNumber ?? nodeReference?.ObjectNumber);
                    (previous ?? node).RemoveParsed(previous is null ? First : Next);
                    break;
                }

                PdfReference reference = (PdfReference)link;
                PdfDictionary item = (PdfDictionary)reference.Resolve();
                seen.Add(item);
                // Under an outline written directly in the catalog, items can name no /Parent: they get none.
                bool parentRight = nodeReference is null ? item[Parent] is null : item[Parent] is PdfReference parent && ReferenceEquals(parent.Resolve(), node);
                if (!parentRight)
                {
                    Fix(document, item, Parent, nodeReference, reference, "/Parent");
                }

                if (!Equals(item[Prev], previousReference))
                {
                    Fix(document, item, Prev, previousReference, reference, "/Prev");
                }

                pending.Push((item, reference));
                previous = item;
                previousReference = reference;
                link = item[Next];
            }

            if (!Equals(node[Last], previousReference) && (node[Last] is not null || previousReference is not null))
            {
                Fix(document, node, Last, previousReference, nodeReference, "/Last");
            }
        }

        // Counts from the bottom up, as qpdf reads open state: a closed item (negative /Count) stays closed, any other is open.
        for (int i = order.Count - 1; i >= 0; i--)
        {
            PdfDictionary node = order[i];
            int visible = 0;
            for (PdfDictionary? child = node.Get<PdfDictionary>(First); child is not null; child = child.Get<PdfDictionary>(Next))
            {
                visible += 1 + (child.GetInt(CountKey) is long count and > 0 ? (int)count : 0);
            }

            long? expected = ReferenceEquals(node, root) ? visible : visible == 0 ? null : node.GetInt(CountKey) < 0 ? -visible : visible;
            long? actual = node.GetInt(CountKey);
            if (actual != expected && !(expected is null or 0 && actual is null or 0))
            {
                document.Log.Add(PdfWarningCode.MalformedOutline, $"An outline node's /Count is {node[CountKey]?.ToString() ?? "missing"}, not {expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}; it was set to match.");
                if (expected is long value)
                {
                    node.SetParsed(CountKey, new PdfInteger(value));
                }
                else
                {
                    node.RemoveParsed(CountKey);
                }
            }
        }
    }

    /// <summary>
    /// An object that is plainly not an outline item: the catalog, a page or a page tree node. Taking it for an item would
    /// write outline links and counts into it and break the page tree.
    /// </summary>
    private static bool IsOtherObject(PdfDocument document, PdfDictionary item)
    {
        return ReferenceEquals(item, document.Catalog) || item.ContainsKey(Kids) || item.GetName(Type)?.Value is "Catalog" or "Pages" or "Page";
    }

    /// <summary>Sets or removes a link as a repair, with a warning.</summary>
    private static void Fix(PdfDocument document, PdfDictionary node, PdfName key, PdfReference? value, PdfReference? where, string what)
    {
        document.Log.Add(PdfWarningCode.MalformedOutline, $"An outline node's {what} disagrees with its chain of items; it was set to match.", objectNumber: where?.ObjectNumber);
        if (value is null)
        {
            node.RemoveParsed(key);
        }
        else
        {
            node.SetParsed(key, value);
        }
    }
}
