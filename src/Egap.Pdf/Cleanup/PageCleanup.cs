using Egami;
using Egami.Operations;
using Egami.Pixels;

using Egap.Pdf.Optimizing;

namespace Egap.Pdf
{
    /// <summary>When <see cref="PdfDocument.RemoveBlankPages"/> takes a page for blank (Phase 7 design Q15).</summary>
    public sealed record BlankPageOptions
    {
        /// <summary>The default options: at most 0.1% of pixels darker than 90% white, at 50 dpi.</summary>
        public static BlankPageOptions Default { get; } = new();

        /// <summary>Gets the largest share of pixels, 0 to 1, that may be darker than <see cref="WhiteLevel"/>. Defaults to 0.001.</summary>
        public double MaxInkFraction { get; init; } = 0.001;

        /// <summary>Gets how light a pixel must be, 0 to 1, to count as white. Defaults to 0.9.</summary>
        public double WhiteLevel { get; init; } = 0.9;

        /// <summary>Gets the resolution pages with content are rendered at to judge them. Defaults to 50.</summary>
        public double Dpi { get; init; } = 50;
    }

    /// <summary>How <see cref="PdfDocument.DeskewScannedPages"/> straightens scans.</summary>
    public sealed record DeskewOptions
    {
        /// <summary>The default options: skews up to 15 degrees found, below 0.1 left alone.</summary>
        public static DeskewOptions Default { get; } = new();

        /// <summary>Gets the largest skew looked for, in degrees. Defaults to 15.</summary>
        public double MaxAngle { get; init; } = 15;

        /// <summary>Gets the smallest skew corrected, in degrees; smaller ones are left as they are. Defaults to 0.1.</summary>
        public double MinAngle { get; init; } = 0.1;
    }
}

namespace Egap.Pdf.Cleanup
{
    /// <summary>
    /// Blank page removal and deskew (Phase 7 design Q14 and Q15, Build Plan task 18). A page that paints nothing is blank at
    /// once; a page with more than a few words of text never is, even when they do not show; any other is rendered in gray
    /// without annotations, and is blank when few enough pixels are darker than the white level. A page whose content is one image covering most of it, and nothing else that shows, is a scan: its skew is
    /// found and corrected with Egami's Deskew, the image keeping its size, a white background filling the corners, and its
    /// encoding (JPEG stays JPEG, bilevel scans go back to bilevel as CCITT G4).
    /// </summary>
    internal static class PageCleanup
    {
        /// <summary>Operators that paint something.</summary>
        private static readonly HashSet<string> Painting = ["f", "F", "f*", "B", "B*", "b", "b*", "S", "s", "sh", "Do", "Tj", "TJ", "'", "\"", "BI"];

        /// <summary>A page with more characters of text than this is never blank, however little of it shows.</summary>
        private const int MaxBlankText = 40;

        public static int RemoveBlank(PdfDocument document, BlankPageOptions options)
        {
            List<PdfPage> blank = [];
            foreach (PdfPage page in document.Pages)
            {
                if (IsBlank(page, options))
                {
                    blank.Add(page);
                }
            }

            foreach (PdfPage page in blank)
            {
                document.Pages.Remove(page);
            }

            return blank.Count;
        }

        internal static bool IsBlank(PdfPage page, BlankPageOptions options)
        {
            try
            {
                if (!page.GetContent().Any(op => Painting.Contains(op.Operator)))
                {
                    return true;
                }

                // Text that shows nothing, such as white or clipped words, is still the page's content: it stays.
                if (page.ExtractText().Count(c => !char.IsWhiteSpace(c)) > MaxBlankText)
                {
                    return false;
                }

                using Raster<Gray8> shown = page.Render<Gray8>(new PdfRenderOptions { Dpi = options.Dpi, Annotations = PdfAnnotationRendering.None });
                int limit = (int)Math.Round(options.WhiteLevel * 255), dark = 0;
                for (int y = 0; y < shown.Height; y++)
                {
                    foreach (Gray8 p in shown.Row(y))
                    {
                        dark += p.L < limit ? 1 : 0;
                    }
                }

                return dark <= options.MaxInkFraction * shown.Width * shown.Height;
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                return false;
            }
        }

        public static int Deskew(PdfDocument document, DeskewOptions options)
        {
            int straightened = 0;
            foreach (PdfPage page in document.Pages)
            {
                if (ScanImage(page) is PdfImage scan && Straighten(document, page, scan, options))
                {
                    straightened++;
                }
            }

            return straightened;
        }

        /// <summary>The page's one image when the page is a scan: it covers 70% of the page and nothing else shows.</summary>
        private static PdfImage? ScanImage(PdfPage page)
        {
            try
            {
                if (page.GetContent().Count(op => Painting.Contains(op.Operator)) != 1 || page.GetImages() is not [PdfPlacedImage placed]
                    || placed.Image.Stream is null || placed.Image.IsStencilMask)
                {
                    return null;
                }

                PdfRectangle box = page.MediaBox;
                double area = Math.Abs(box.Width * box.Height);
                return area > 0 && Math.Abs(placed.Matrix.Determinant) >= 0.7 * area ? placed.Image : null;
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                return null;
            }
        }

        private static bool Straighten(PdfDocument document, PdfPage page, PdfImage scan, DeskewOptions options)
        {
            Raster<Rgba8> raster;
            try
            {
                raster = scan.ToRaster<Rgba8>(new PdfImageOptions { ApplyMasks = false });
            }
            catch (PdfException)
            {
                return false;
            }

            using (raster)
            {
                float angle = raster.DetectSkew((float)options.MaxAngle);
                if (!(Math.Abs(angle) >= options.MinAngle))
                {
                    return false;
                }

                SurveyedImageKind kind = ImageSurvey.Classify(scan);
                string? codec = scan.ImageCodec?.Value;
                using Raster<Rgba8> straight = raster.Derive(ops => ops.Deskew((float)options.MaxAngle, new Rgba8(255, 255, 255, 255), keepSize: true));
                if (kind == SurveyedImageKind.Bilevel)
                {
                    // Back to black and white: the turn's grays are thresholded, then coded as G4.
                    using Raster<Gray8> gray = straight.As<Gray8>();
                    for (int y = 0; y < gray.Height; y++)
                    {
                        foreach (ref Gray8 p in gray.Row(y))
                        {
                            p = new Gray8(p.L >= 128 ? (byte)255 : (byte)0);
                        }
                    }

                    scan.Replace(gray);
                    BilevelEncoder.Optimize(new SurveyedImage(scan) { Kind = SurveyedImageKind.Bilevel }, 0);
                }
                else
                {
                    int quality = codec == "DCTDecode" && JpegOptimizer.Quality(scan.GetRawBytes()) is int had ? had : 85;
                    scan.Replace(straight, codec == "DCTDecode" ? PdfImageEncoding.Jpeg(Math.Clamp(quality, 1, 100)) : null);
                }
            }

            document.ResetRenderCache();
            return true;
        }
    }
}
