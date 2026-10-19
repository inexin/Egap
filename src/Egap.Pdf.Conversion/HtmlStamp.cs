using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Pdf.Conversion;

/// <summary>Stamps made from HTML, laid out by Egap.Documents.</summary>
public static class HtmlStamp
{
    /// <summary>The tallest an HTML stamp is laid out, in points (200 inches).</summary>
    private const double MaxHeight = 14400;

    extension(PdfStamp)
    {
        /// <summary>
        /// A stamp of HTML laid out <paramref name="width"/> wide and as tall as its content, drawn over each page at the
        /// centre: styled text, tables and images, such as a letterhead or a "Paid" seal. The HTML is the same on every page;
        /// use <see cref="PdfStamp.Text"/> for page numbers. Set the placement with <c>with</c>, as for any stamp.
        /// </summary>
        /// <param name="html">The HTML; its style sheets apply, and its body margin defaults to none.</param>
        /// <param name="width">How wide it is laid out.</param>
        /// <param name="options">The folder its images are read from (<see cref="PdfConvertOptions.BaseDirectory"/>); other settings are not used.</param>
        /// <example>
        /// <code>
        /// using PdfDocument pdf = PdfDocument.Open("invoice.pdf");
        /// pdf.Stamp(PdfStamp.Html("&lt;div style='border:3px solid green;color:green;font:bold 28pt sans-serif;padding:4pt'&gt;PAID&lt;/div&gt;", Length.Mm(50))
        ///     with { Anchor = StampAnchor.TopRight, Offset = new(-40, -40), Rotation = 15 });
        /// </code>
        /// </example>
        /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not positive.</exception>
        public static PdfStamp Html(string html, Length width, PdfConvertOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(html);
            if (!(width > 0 && double.IsFinite(width)))
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive.");
            }

            HtmlLoadOptions load = PdfConvert.HtmlOptions(options?.BaseDirectory, ["body { margin: 0 }"]);
            Document document = HtmlFormat.Parse(html, load);
            foreach (Section section in document.Sections)
            {
                section.PageSetup.PageWidth = width;
                section.PageSetup.PageHeight = MaxHeight;
                section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 0;
            }

            DocumentLayout layout = document.Layout();
            PageLayout first = layout.Pages[0];
            double bottom = first.Boxes.SelectMany(b => b.Descendants().Prepend(b)).Select(b => b.Bounds.Bottom).DefaultIfEmpty(1).Max();
            bottom = Math.Clamp(bottom, 1, MaxHeight);

            // Saved and read back, so fonts are complete; the page's own document lives as long as the stamp and holds
            // nothing that needs disposing.
            using MemoryStream saved = new();
            using (PdfDocument exported = layout.ToPdf(new PdfExportOptions { Outline = OutlineFrom.None, Links = false, NamedDestinations = false }))
            {
                exported.Save(saved);
            }

            PdfPage page = PdfDocument.Open(saved.ToArray()).Pages[0];
            PdfRectangle media = page.MediaBox;
            page.CropBox = new PdfRectangle(media.Left, media.Top - bottom, media.Left + width, media.Top);
            return PdfStamp.Page(page) with { Under = false };
        }
    }
}
