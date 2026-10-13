using System.Collections;

namespace Egap.Pdf;

/// <summary>
/// A page's annotations (12.5): its <c>/Annots</c> array, in order, each annotation once. Entries that are not dictionaries
/// are left out, as pdf.js and PDFium leave them out. Adding and removing edit the array in place, keep each annotation's
/// <c>/P</c> pointing at the page, and keep popups with the annotations they belong to.
/// </summary>
public sealed class PdfAnnotationCollection : IReadOnlyList<PdfAnnotation>
{
    private static readonly PdfName AnnotsKey = "Annots";

    private PdfPage page;

    internal PdfAnnotationCollection(PdfPage page)
    {
        this.page = page;
    }

    /// <summary>The number of annotations.</summary>
    public int Count => List().Count;

    /// <summary>The annotation at an index, in <c>/Annots</c> order.</summary>
    public PdfAnnotation this[int index] => List()[index];

    private PdfDocument Document => page.Document;

    /// <summary>The index of an annotation, or -1 when it is not on the page.</summary>
    public int IndexOf(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return List().IndexOf(annotation);
    }

    /// <summary>Whether an annotation is on the page.</summary>
    public bool Contains(PdfAnnotation annotation)
    {
        return IndexOf(annotation) >= 0;
    }

    /// <summary>
    /// Adds a new annotation, made with a subtype's constructor, to the end of the page's annotations: it becomes an
    /// indirect object with <c>/P</c> pointing at the page, and gets an appearance when Egap draws its subtype
    /// (<see cref="PdfAnnotation.UpdateAppearance"/>).
    /// </summary>
    /// <returns>The stored annotation, which can read the document (named destinations, its page).</returns>
    public T Add<T>(T annotation)
        where T : PdfAnnotation
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.Document is not null || ((Objects.IContainer)annotation.Dictionary).Parent is not null)
        {
            throw new ArgumentException("Only a new annotation can be added; this one is already stored in a document.", nameof(annotation));
        }

        T stored = (T)Store(annotation.Dictionary);
        if (Annotations.AppearanceBuilder.Supports(stored))
        {
            Annotations.AppearanceBuilder.Update(stored, Document);
        }

        return stored;
    }

    /// <summary>
    /// Adds a popup for a markup annotation on this page, shown in a rectangle, usually beside the page's content; it
    /// replaces any popup the annotation had.
    /// </summary>
    public PdfPopupAnnotation AddPopup(PdfMarkupAnnotation parent, PdfRectangle rectangle, bool open = false)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.Reference is null || !Contains(parent))
        {
            throw new ArgumentException("The popup's annotation must be stored on this page.", nameof(parent));
        }

        if (parent.Popup is PdfPopupAnnotation old)
        {
            RemoveEntries(new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { old.Dictionary });
        }

        PdfDictionary popup = new()
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get("Popup"),
            ["Rect"] = rectangle.Normalize().ToArray(),
            ["Parent"] = parent.Reference,
        };
        if (open)
        {
            popup["Open"] = PdfBoolean.True;
        }

        PdfPopupAnnotation added = (PdfPopupAnnotation)Store(popup);
        parent.Dictionary["Popup"] = added.Reference;
        return added;
    }

    /// <summary>
    /// Adds a file attachment annotation: the file is embedded with its size, MD5 checksum, MIME type and date, as
    /// <see cref="PdfAttachmentCollection.Add"/> embeds one, and shown as an icon in a rectangle. The document's
    /// <c>/EmbeddedFiles</c> tree is not changed.
    /// </summary>
    public PdfFileAttachmentAnnotation AddFileAttachment(PdfRectangle rectangle, string fileName, ReadOnlySpan<byte> bytes, string? mimeType = null, string? description = null, DateTimeOffset? modified = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        PdfReference spec = PdfAttachmentCollection.CreateFileSpecification(Document, fileName, bytes, mimeType, description, modified);
        PdfDictionary annotation = new()
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get("FileAttachment"),
            ["Rect"] = rectangle.Normalize().ToArray(),
            ["F"] = new PdfInteger((int)PdfAnnotationFlags.Print),
            ["FS"] = spec,
            ["Contents"] = PdfString.FromText(description ?? fileName),
        };
        return (PdfFileAttachmentAnnotation)Store(annotation);
    }

    /// <summary>
    /// Removes an annotation from the page, with its popup and its replies (and theirs), as Acrobat removes them; removing a
    /// popup also takes it off its annotation. The objects stay until nothing reaches them. A widget stays in its form field
    /// until form editing removes it.
    /// </summary>
    /// <returns>Whether the annotation was on the page.</returns>
    public bool Remove(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        List<PdfAnnotation> list = List();
        if (!list.Contains(annotation))
        {
            return false;
        }

        HashSet<PdfDictionary> removed = new(ReferenceEqualityComparer.Instance) { annotation.Dictionary };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (PdfAnnotation other in list)
            {
                if (!removed.Contains(other.Dictionary) && (Points(other, "Parent", removed) || Points(other, "IRT", removed) || IsPopupOf(other, removed)))
                {
                    grew = removed.Add(other.Dictionary);
                }
            }
        }

        if (annotation is PdfPopupAnnotation popup && popup.Parent is PdfAnnotation parent && ReferenceEquals(parent.Dictionary.Get<PdfDictionary>("Popup"), popup.Dictionary))
        {
            parent.Dictionary.Remove("Popup");
        }

        RemoveEntries(removed);
        return true;
    }

    /// <summary>Removes the annotation at an index, as <see cref="Remove"/> does.</summary>
    public void RemoveAt(int index)
    {
        Remove(this[index]);
    }

    /// <summary>The markup annotations on this page that reply to or are grouped with an annotation (<c>/IRT</c>).</summary>
    public IReadOnlyList<PdfMarkupAnnotation> GetReplies(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return [.. List().OfType<PdfMarkupAnnotation>().Where(a => ReferenceEquals(a.Dictionary.Get<PdfDictionary>("IRT"), annotation.Dictionary))];
    }

    /// <inheritdoc/>
    public IEnumerator<PdfAnnotation> GetEnumerator()
    {
        return List().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private static bool Points(PdfAnnotation annotation, PdfName key, HashSet<PdfDictionary> targets)
    {
        return annotation.Dictionary.Get<PdfDictionary>(key) is PdfDictionary target && targets.Contains(target);
    }

    /// <summary>Whether an annotation is the <c>/Popup</c> of one being removed.</summary>
    private static bool IsPopupOf(PdfAnnotation annotation, HashSet<PdfDictionary> removed)
    {
        return annotation is PdfPopupAnnotation && removed.Any(r => ReferenceEquals(r.Get<PdfDictionary>("Popup"), annotation.Dictionary));
    }

    /// <summary>Every entry of <c>/Annots</c> that is a dictionary, each once.</summary>
    private List<PdfAnnotation> List()
    {
        if (page.Dictionary.Get<PdfArray>(AnnotsKey) is not PdfArray annots)
        {
            return [];
        }

        List<PdfAnnotation> list = new(annots.Count);
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < annots.Count; i++)
        {
            if (annots.Get(i) is PdfDictionary dictionary && seen.Add(dictionary))
            {
                list.Add(PdfAnnotation.FromDictionary(dictionary, annots[i] as PdfReference, Document));
            }
        }

        return list;
    }

    /// <summary>Stores an annotation dictionary as an indirect object, points it at the page and appends it to <c>/Annots</c>.</summary>
    private PdfAnnotation Store(PdfDictionary dictionary)
    {
        page = Document.Pages.EnsureTree(page);
        if (page.Reference is PdfReference pageReference)
        {
            dictionary["P"] = pageReference;
        }

        PdfReference reference = Document.Objects.Add(dictionary);
        if (page.Dictionary.Get<PdfArray>(AnnotsKey) is PdfArray annots)
        {
            annots.Add(reference);
        }
        else
        {
            page.Dictionary[AnnotsKey] = new PdfArray([reference]);
        }

        return PdfAnnotation.FromDictionary((PdfDictionary)Document.Objects[reference], reference, Document);
    }

    private void RemoveEntries(HashSet<PdfDictionary> dictionaries)
    {
        page = Document.Pages.EnsureTree(page);
        if (page.Dictionary.Get<PdfArray>(AnnotsKey) is not PdfArray annots)
        {
            return;
        }

        for (int i = annots.Count - 1; i >= 0; i--)
        {
            if (annots.Get(i) is PdfDictionary dictionary && dictionaries.Contains(dictionary))
            {
                annots.RemoveAt(i);
            }
        }
    }
}
