using Egami;
using Egami.Drawing;
using Egami.Formats.Png;
using Egami.Pixels;

using Egap.Documents;

using Picture = Egap.Documents.Picture;

namespace Egap.Pdf.Layout;

internal sealed partial class ModelBuilder
{
    private readonly List<Inline> floats = [];

    /// <summary>
    /// Writes a figure as a picture (design Q7): a lone image drawn upright keeps its stored bytes when they are a JPEG and its
    /// pixels as a PNG otherwise; anything else (drawings, several images, text over them) is drawn from the page at the
    /// figure resolution. A figure text runs beside floats with square wrapping at its place on the page; others stand in
    /// their own paragraph, centred when centred in their column.
    /// </summary>
    private void WriteFigure(Body body, FigureDraft figure, PlacedItem placed)
    {
        if (!Placeable(figure.Box) || figure.Box.Width < 1 || figure.Box.Height < 1)
        {
            return;
        }

        ImageData image = Image(figure, placed.Page);
        Picture picture = new(document, image, Math.Round(figure.Box.Width, 2), Math.Round(figure.Box.Height, 2));
        if (figure.AltText is { Length: > 0 } alt)
        {
            picture.Description = Clean(alt);
        }

        lists.Break();
        if (FigureFinder.Wrapped(figure, [.. placed.Page.Content.Lines]))
        {
            picture.Floating = new FloatingPlacement
            {
                HorizontalAnchor = HorizontalAnchor.Page,
                HorizontalOffset = Math.Round(figure.Box.Left, 1),
                VerticalAnchor = VerticalAnchor.Paragraph,
                Wrap = WrapMode.Square,
            };
            floats.Add(picture);
            return;
        }

        Paragraph paragraph = new(document) { Style = styles.Normal.Style };
        paragraph.Format.FirstLineIndent = 0;
        paragraph.Format.LeftIndent = 0;
        double center = (figure.Box.Left + figure.Box.Right) / 2;
        Frame frame = placed.Frame;
        paragraph.Format.Alignment = Math.Abs(center - ((frame.Left + frame.Right) / 2)) < frame.Width * 0.05 ? ParagraphAlignment.Center
            : figure.Box.Left - frame.Left > frame.Width * 0.3 && frame.Right - figure.Box.Right < 6 ? ParagraphAlignment.Right
            : ParagraphAlignment.Left;
        if (paragraph.Format.Alignment == ParagraphAlignment.Left)
        {
            paragraph.Format.LeftIndent = StyleSheet.Indent(figure.Box.Left - frame.Left);
        }

        paragraph.Format.LineSpacing = 1;
        paragraph.Format.LineSpacingRule = LineSpacingRule.Multiple;
        paragraph.Inlines.Add(picture);
        body.Blocks.Add(paragraph);
        TakeFloats(paragraph);
        written.Add((placed.Page.Content.Index, figure.Box, paragraph));
        last = null;
        lastTable = null;
    }

    /// <summary>Anchors the floating figures waiting for a paragraph at the start of <paramref name="paragraph"/>.</summary>
    private void TakeFloats(Paragraph paragraph)
    {
        for (int i = floats.Count - 1; i >= 0; i--)
        {
            paragraph.Inlines.Insert(0, floats[i]);
        }

        floats.Clear();
    }

    /// <summary>The figure's picture: a lone upright image's own bytes or pixels, else the page drawn over the figure's area.</summary>
    private ImageData Image(FigureDraft figure, PageDraft page)
    {
        if (figure.Images.Count == 1 && figure.Paths.Count == 0 && figure.Lines.Count == 0 && Upright(figure.Images[0])
            && figure.Images[0].Bounds.Width >= figure.Box.Width - 1 && figure.Images[0].Bounds.Height >= figure.Box.Height - 1
            && Stored(PdfLayoutAnalyzer.Match(figure.Images[0], PdfLayoutAnalyzer.Placed(pdf.Pages[page.Content.Index]), page.Content)) is ImageData stored)
        {
            return stored;
        }

        PdfPage pdfPage = pdf.Pages[page.Content.Index];
        double scale = options.FigureResolution / 72;
        using Raster<Rgba8> raster = pdfPage.Render(new PdfRenderOptions
        {
            Dpi = options.FigureResolution,
            Region = new RectF((float)(figure.Box.Left * scale), (float)(figure.Box.Top * scale), (float)(figure.Box.Width * scale), (float)(figure.Box.Height * scale)),
            Annotations = PdfAnnotationRendering.None,
        });
        return Png(raster);
    }

    /// <summary>A stored image as it is: a JPEG's bytes, other images' pixels as a PNG; null for one that will not decode or carries a mask.</summary>
    private static ImageData? Stored(PdfImage? image)
    {
        if (image is null)
        {
            return null;
        }

        try
        {
            if (image.IsStencilMask || image.Stream?.Dictionary.Get("SMask") is not null || image.Stream?.Dictionary.Get("Mask") is not null)
            {
                return null;
            }

            if (image.Filters.Count == 1 && image.Filters[0].Value == "DCTDecode" && image.ColorSpace is PdfName { Value: "DeviceRGB" or "DeviceGray" })
            {
                return new ImageData(image.GetRawBytes(), "image/jpeg");
            }

            using Raster raster = image.ToRaster();
            return Png(raster);
        }
        catch (Exception e) when (e is PdfException or NotSupportedException or InvalidDataException)
        {
            return null;
        }
    }

    private static ImageData Png(Raster raster)
    {
        using MemoryStream png = new();
        raster.Save(png, PngFormat.Info);
        return new ImageData(png.ToArray(), "image/png");
    }

    /// <summary>Whether an image is drawn unturned and unflipped: its top edge level and above its bottom.</summary>
    private static bool Upright(PageImage image)
    {
        IReadOnlyList<PdfPoint> c = image.Corners;
        return Math.Abs(c[0].Y - c[1].Y) < 0.5 && Math.Abs(c[0].X - c[3].X) < 0.5 && c[0].X < c[1].X && c[0].Y < c[3].Y;
    }
}
