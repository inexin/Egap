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

    /// <summary>Removes the page at an index from the page tree. Its object stays in the document until nothing reaches it.</summary>
    public void RemoveAt(int index)
    {
        lock (gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)List.Count, nameof(index));
            Editor().RemoveAt(index);
        }
    }

    /// <summary>Removes a page from the page tree.</summary>
    /// <returns>Whether it was one of the document's pages.</returns>
    public bool Remove(PdfPage page)
    {
        lock (gate)
        {
            int index = IndexOf(page);
            if (index < 0)
            {
                return false;
            }

            RemoveAt(index);
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
