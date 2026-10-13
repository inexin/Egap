using Egap.Pdf.Metadata;

namespace Egap.Pdf;

/// <summary>
/// A markup annotation (12.5.6.2): one a reader writes on the page, with an author, a popup for its text, and replies.
/// </summary>
public abstract class PdfMarkupAnnotation : PdfAnnotation
{
    private protected PdfMarkupAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    private protected PdfMarkupAnnotation(string subtype, PdfRectangle rectangle)
        : base(subtype, rectangle)
    {
    }

    /// <summary>The author, shown in the popup's title bar (<c>/T</c>).</summary>
    public string? Author
    {
        get => Dictionary.GetText("T");
        set => Dictionary["T"] = value is null ? null : PdfString.FromText(value);
    }

    /// <summary>The subject (<c>/Subj</c>, PDF 1.5).</summary>
    public string? Subject
    {
        get => Dictionary.GetText("Subj");
        set => Dictionary["Subj"] = value is null ? null : PdfString.FromText(value);
    }

    /// <summary>
    /// The opacity the annotation is drawn with when it has no appearance (<c>/CA</c>, PDF 1.4), from 0 to 1; 1 when missing.
    /// Setting 1 removes the entry.
    /// </summary>
    public double Opacity
    {
        get => Dictionary.GetNumber("CA") is double opacity && !double.IsNaN(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
        set
        {
            if (!(value is >= 0 and <= 1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Opacity is from 0 to 1.");
            }

            Dictionary["CA"] = value == 1 ? null : new PdfReal(value);
            Changed();
        }
    }

    /// <summary>When the annotation was made (<c>/CreationDate</c>, PDF 1.5).</summary>
    public DateTimeOffset? CreationDate
    {
        get => PdfDate.ParsePdf(Dictionary.GetText("CreationDate"));
        set => Dictionary["CreationDate"] = value is DateTimeOffset date ? PdfString.FromText(PdfDate.FormatPdf(date)) : null;
    }

    /// <summary>The rich text version of <see cref="PdfAnnotation.Contents"/> (<c>/RC</c>, XHTML), as stored.</summary>
    public string? RichContents => Dictionary.Get("RC") switch
    {
        PdfString text => text.ToText(),
        PdfStream stream => System.Text.Encoding.UTF8.GetString(stream.GetDecodedBytes()),
        _ => null,
    };

    /// <summary>
    /// What the annotation is for (<c>/IT</c>, PDF 1.6), such as <c>FreeTextCallout</c>, <c>LineArrow</c> or
    /// <c>PolygonCloud</c>.
    /// </summary>
    public string? Intent
    {
        get => Dictionary.GetName("IT")?.Value;
        set => Dictionary["IT"] = value is null ? null : PdfName.Get(value);
    }

    /// <summary>
    /// The popup that shows the annotation's text (<c>/Popup</c>). Add one with
    /// <see cref="PdfAnnotationCollection.AddPopup"/>.
    /// </summary>
    public PdfPopupAnnotation? Popup => Linked("Popup") as PdfPopupAnnotation;

    /// <summary>
    /// The annotation this one replies to or is grouped with (<c>/IRT</c>, PDF 1.5). Setting one needs a stored annotation
    /// (one with a <see cref="PdfAnnotation.Reference"/>); setting none also removes <c>/RT</c>.
    /// </summary>
    public PdfAnnotation? InReplyTo
    {
        get => Linked("IRT");
        set
        {
            if (value is null)
            {
                Dictionary.Remove("IRT");
                Dictionary.Remove("RT");
                return;
            }

            if (value.Reference is null || value.Equals(this))
            {
                throw new ArgumentException("A reply needs another annotation that is stored in the document.", nameof(value));
            }

            Dictionary["IRT"] = value.Reference;
        }
    }

    /// <summary>
    /// How the annotation relates to <see cref="InReplyTo"/> (<c>/RT</c>): a reply, or grouped with it. Setting a reply
    /// removes the entry, since that is the default.
    /// </summary>
    public PdfReplyType ReplyType
    {
        get => Dictionary.GetName("RT")?.Value == "Group" ? PdfReplyType.Group : PdfReplyType.Reply;
        set => Dictionary["RT"] = value == PdfReplyType.Group ? PdfName.Get("Group") : null;
    }
}

/// <summary>How a markup annotation relates to the one it points at with <c>/IRT</c> (12.5.6.2).</summary>
public enum PdfReplyType
{
    /// <summary>A reply, <c>/R</c>.</summary>
    Reply,

    /// <summary>Grouped with it and shown as one, <c>/Group</c>.</summary>
    Group,
}
