using Egami;
using Egami.Drawing;
using Egami.Operations;

namespace Egap.Pdf;

/// <content>Form XObjects on the canvas: whole pages drawn inside a page (Phase 7 design Q8).</content>
public sealed partial class PdfCanvas
{
    /// <summary>
    /// Draws a form XObject, such as a page made with <see cref="PdfPage.ToFormXObject"/>, stretched to fill
    /// <paramref name="destination"/> in the canvas's space; the form is written once however often it is drawn.
    /// </summary>
    /// <param name="form">The form; it must belong to the canvas's document.</param>
    /// <param name="destination">Where it goes, its top left first as for images.</param>
    /// <param name="paint">Opacity and blend mode; null for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="form"/> is null.</exception>
    public void DrawForm(PdfFormXObject form, RectF destination, Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.Width <= 0 || form.Height <= 0 || destination.Width == 0 || destination.Height == 0)
        {
            return;
        }

        PdfName name = AddResource("XObject", form.Reference);
        GraphicsGroup g = OpenGroup();
        SetAlpha(g, paint?.Opacity ?? 1, paint?.BlendMode ?? BlendMode.SourceOver);

        // The form is y up from its bottom left; the canvas is y down.
        double sx = destination.Width / form.Width, sy = destination.Height / form.Height;
        content.Save().Transform(sx, 0, 0, -sy, destination.X, destination.Y + destination.Height).Draw(name).Restore();
    }
}
