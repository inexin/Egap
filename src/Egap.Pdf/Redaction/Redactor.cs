namespace Egap.Pdf.Redaction;

/// <summary>
/// Applies a document's redaction annotations (Phase 5 design Q9): on each page with any, the content and the forms it
/// draws lose what the areas cover, and the redaction annotations are deleted. The document can then only be saved whole
/// with unused objects dropped, so nothing redacted survives in an earlier revision or an orphaned object.
/// </summary>
internal static class Redactor
{
    public static PdfRedactionResult Apply(PdfDocument document, PdfRedactionOptions options)
    {
        RedactionCounters counters = new();
        int applied = 0;
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            List<PdfRedactAnnotation> redactions = [.. page.Annotations.OfType<PdfRedactAnnotation>()];
            if (redactions.Count == 0)
            {
                continue;
            }

            List<(PdfRectangle Area, PdfRgbColor? Fill)> marked = [.. redactions.SelectMany(r => Areas(r).Select(a => (a, r.InteriorColor))).Where(m => m.a.Width > 0 && m.a.Height > 0)];
            List<PdfRectangle> areas = [.. marked.Select(m => m.Area)];
            if (areas.Count > 0)
            {
                PdfDictionary resources = Pages.PageDrawing.OwnResources(page);
                ContentRedactor redactor = new(document, areas, [.. marked.Select(m => m.Fill)], options, counters);
                if (redactor.Redact(page.GetContent(), resources, PdfMatrix.Identity, 0) is PdfContent redacted)
                {
                    page.SetContent(redacted);
                }

                // Annotations under an area go with what they cover.
                foreach (PdfAnnotation annotation in page.Annotations.Where(a => a is not PdfRedactAnnotation && areas.Any(area => ContentRedactor.Overlaps(area, a.Rectangle))).ToList())
                {
                    page.Annotations.Remove(annotation);
                    counters.Annotations++;
                }
            }

            foreach (PdfRedactAnnotation redaction in redactions)
            {
                page.Annotations.Remove(redaction);
                applied++;
            }

            Overlay.Draw(page, redactions);
            if (options.RasterizeDpi is double dpi)
            {
                Rasterize(page, dpi);
            }
        }

        document.MarkRedacted();
        return new PdfRedactionResult(applied, counters.Glyphs, counters.Paths, counters.Images, counters.Forms, counters.Annotations);
    }

    /// <summary>
    /// The most cautious redaction (design Q9's extra): the page, already redacted and overlaid, is rendered and its content
    /// replaced by that one image, so no text or vector data of the page remains.
    /// </summary>
    private static void Rasterize(PdfPage page, double dpi)
    {
        // The annotations that remain stay annotations, so the image holds the page content alone.
        using Egami.Raster<Egami.Pixels.Rgba8> raster = page.Render(new PdfRenderOptions { Dpi = dpi, Annotations = PdfAnnotationRendering.None });
        page.SetContent(new PdfContent());
        page.Resources = new PdfDictionary();
        using PdfCanvas canvas = new(page);
        canvas.DrawImage(raster, new Egami.Drawing.RectF(0, 0, canvas.Width, canvas.Height));
    }

    /// <summary>The areas a redaction annotation marks: each quadrilateral's bounding box, or its rectangle when it has none.</summary>
    internal static IEnumerable<PdfRectangle> Areas(PdfRedactAnnotation redaction)
    {
        IReadOnlyList<PdfPoint> quads = redaction.QuadPoints;
        if (quads.Count >= 4)
        {
            for (int i = 0; i + 3 < quads.Count; i += 4)
            {
                double left = Math.Min(Math.Min(quads[i].X, quads[i + 1].X), Math.Min(quads[i + 2].X, quads[i + 3].X));
                double right = Math.Max(Math.Max(quads[i].X, quads[i + 1].X), Math.Max(quads[i + 2].X, quads[i + 3].X));
                double bottom = Math.Min(Math.Min(quads[i].Y, quads[i + 1].Y), Math.Min(quads[i + 2].Y, quads[i + 3].Y));
                double top = Math.Max(Math.Max(quads[i].Y, quads[i + 1].Y), Math.Max(quads[i + 2].Y, quads[i + 3].Y));
                yield return new PdfRectangle(left, bottom, right, top);
            }

            yield break;
        }

        yield return redaction.Rectangle;
    }
}
