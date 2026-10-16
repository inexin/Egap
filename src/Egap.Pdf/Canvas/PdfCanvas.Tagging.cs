using Egami.Drawing;

namespace Egap.Pdf;

/// <summary>Tagging while drawing (Phase 5 task 18, design Q14).</summary>
public sealed partial class PdfCanvas
{
    /// <summary>
    /// Starts content that belongs to a structure element: the drawing until <see cref="EndTag"/> is marked with the
    /// element's type and the page's next marked-content id, and the element records that content, so the structure tree
    /// and the parent tree lead to it.
    /// </summary>
    /// <example>
    /// <code>
    /// PdfStructureElement heading = pdf.Structure.Add(PdfStructureType.Document).Add(PdfStructureType.H1);
    /// canvas.BeginTag(heading);
    /// canvas.DrawText("Annual report", new Vector2(72, 72), titleFont, black);
    /// canvas.EndTag();
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">The canvas draws a form XObject, whose content cannot be tagged here.</exception>
    public void BeginTag(PdfStructureElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (formTarget)
        {
            throw new InvalidOperationException("Tagging is for page content; a form XObject's content is tagged where the form is used.");
        }

        int mcid = Page.Document.Structure.NextMcid(Page);
        BeginMarkedContent(new MarkedContent(Tag(element.Type.Name)) { NodeId = mcid });
        element.AddMarkedContent(Page, mcid);
    }

    /// <summary>
    /// Starts content that is not part of the document's logical content, such as a page number, a running header or a
    /// decorative rule (14.8.2.2): assistive technology skips it. Ends with <see cref="EndTag"/>.
    /// </summary>
    public void BeginArtifact()
    {
        BeginMarkedContent(new MarkedContent("Artifact"));
    }

    /// <summary>Ends the content <see cref="BeginTag"/> or <see cref="BeginArtifact"/> started.</summary>
    public void EndTag()
    {
        EndMarkedContent();
    }

    /// <summary>A type usable as a marked-content tag: a name without spaces, slashes or control characters.</summary>
    private static string Tag(string type)
    {
        return string.IsNullOrEmpty(type) || type.Any(c => c <= ' ' || c == '/' || c == 0x7f) ? "Span" : type;
    }
}
