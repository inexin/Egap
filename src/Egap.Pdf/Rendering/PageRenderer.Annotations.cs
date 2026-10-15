using Egami.Drawing;

using Egap.Pdf.Annotations;
using Egap.Pdf.Forms;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Annotations (12.5, design Q12), drawn after the page's content in page order from their normal appearances, matched to
/// their appearance state and placed in their rectangle as algorithm 12.5.5 says. Hidden ones never show; for the display
/// intent NoView ones and popups are left out, for the print intent only Print-flagged ones show. An annotation or field
/// without an appearance, or a form that asks for new ones (NeedAppearances), gets one from Egap's generators in memory: the
/// document is never changed by a render.
/// </summary>
internal sealed partial class PageRenderer
{
    public void RenderAnnotations(PdfPage page, PdfAnnotationRendering which)
    {
        if (which == PdfAnnotationRendering.None)
        {
            return;
        }

        bool needAppearances = page.Document.Form.NeedAppearances;
        foreach (PdfAnnotation annotation in page.Annotations)
        {
            if (!Shows(annotation, which))
            {
                continue;
            }

            try
            {
                (PdfStream? appearance, PdfRectangle rectangle) = Appearance(annotation, needAppearances);
                if (appearance is null || rectangle.IsEmpty || Flattener.Placement(appearance, rectangle) is not double[] m)
                {
                    continue;
                }

                DrawAppearance(appearance, new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]), page.Resources);
            }
            catch (PdfException e) when (e is not PdfLimitException && readOptions?.Strict != true)
            {
                Warn(PdfWarningCode.MalformedContent, $"An annotation could not be drawn: {e.Message}");
            }
        }
    }

    private bool Shows(PdfAnnotation annotation, PdfAnnotationRendering which)
    {
        PdfAnnotationFlags flags = annotation.Flags;
        if (settings.Layers?.IsVisible(annotation.Dictionary.Get<PdfDictionary>("OC")) == false)
        {
            return false;
        }

        if (flags.HasFlag(PdfAnnotationFlags.Hidden) || annotation.Subtype == "Popup" || (which == PdfAnnotationRendering.NoForms && annotation is PdfWidgetAnnotation))
        {
            return false;
        }

        return settings.Intent == PdfRenderIntent.Print ? flags.HasFlag(PdfAnnotationFlags.Print) : !flags.HasFlag(PdfAnnotationFlags.NoView);
    }

    /// <summary>The appearance to draw and the rectangle it goes in: the stored one, or one generated in memory when needed.</summary>
    private static (PdfStream? Appearance, PdfRectangle Rectangle) Appearance(PdfAnnotation annotation, bool needAppearances)
    {
        PdfStream? stored = annotation.GetAppearance();
        if (annotation is PdfWidgetAnnotation widget)
        {
            if ((stored is null || needAppearances) && widget.Field is PdfField field && FieldAppearance.Build(field, widget) is PdfStream fresh)
            {
                return (fresh, widget.Rectangle);
            }

            return (stored, annotation.Rectangle);
        }

        if (stored is null && AppearanceBuilder.Supports(annotation) && annotation.Document is PdfDocument document)
        {
            // A copy grows its rectangle for line endings and borders, so the annotation itself stays as it was.
            PdfAnnotation copy = PdfAnnotation.FromDictionary(annotation.Dictionary.Clone(), null, document);
            return (AppearanceBuilder.Build(copy, document), copy.Rectangle);
        }

        return (stored, annotation.Rectangle);
    }

    /// <summary>Draws a form XObject's content under a matrix, clipped to its box, as a layer when it is a transparency group.</summary>
    private void DrawAppearance(PdfStream form, PdfMatrix placement, PdfDictionary? fallbackResources)
    {
        PdfContent content = [];
        if (PdfRectangle.FromArray(form.Dictionary.Get<PdfArray>("BBox")) is PdfRectangle box)
        {
            content.AddRange(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"{box.Left} {box.Bottom} {box.Width} {box.Height} re W n"))));
        }

        content.AddRange(PdfContent.Read(form));
        PdfMatrix own = PdfMatrix.FromArray(form.Dictionary.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity;
        PdfDictionary? group = form.Dictionary.Get<PdfDictionary>("Group");
        int saves = canvas.SaveCount;
        if (group?.GetName("S")?.Value == "Transparency")
        {
            canvas.SaveLayer(new LayerOptions { Isolated = group.GetBool("I") == true, Knockout = group.GetBool("K") == true });
        }

        try
        {
            Render(content, form.Dictionary.Get<PdfDictionary>("Resources") ?? fallbackResources, own * placement, readOptions);
        }
        finally
        {
            canvas.RestoreToCount(saves);
        }
    }
}
