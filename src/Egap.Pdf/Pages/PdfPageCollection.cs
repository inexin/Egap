using System.Collections;

namespace Egap.Pdf;

/// <summary>
/// A document's pages in order, numbered from 0 (Phase 2 Q2). The list is built on first use by walking the page tree from
/// the catalog; only the tree's nodes are loaded, not the pages' content. A broken tree is repaired in memory with
/// <see cref="PdfWarningCode.MalformedPageTree"/> warnings, as pdf.js and PDFium read it.
/// </summary>
/// <remarks>
/// The list reflects the page tree when it was first used. A page tree changed by hand through its dictionaries is not
/// seen again by this collection.
/// </remarks>
public sealed class PdfPageCollection : IReadOnlyList<PdfPage>
{
    private readonly PdfDocument document;
    private readonly Lock gate = new();
    private List<PdfPage>? pages;
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
                return pages ??= PageTreeReader.Read(document);
            }
        }
    }
}
