using System.Text;

using Egami;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Redaction;

/// <summary>
/// Redaction of images, annotations and overlays (Phase 5 task 12): covered pixels painted over and the image encoded again
/// in its own kind, images removed by option, covered annotations removed, the overlay drawn, and pages rasterized on request.
/// </summary>
public sealed class RedactionImageTests
{
    /// <summary>A 100 by 100 point image at 100, 100: 20 by 20 pixels of one colour, Flate, or a JPEG.</summary>
    private static PdfDocument ImagePage(bool jpeg = false)
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        using (Raster<Rgba8> image = Raster.Create<Rgba8>(20, 20, new Rgba8(0, 200, 0, 255)))
        using (PdfCanvas canvas = new(page, new PdfCanvasOptions { Coordinates = PdfCanvasCoordinates.Pdf }))
        {
            canvas.DrawImage(image, new Egami.Drawing.RectF(100, 100, 100, 100));
        }

        PdfDocument pdf = RedactionTextTests.SaveAndOpen(created);
        if (jpeg)
        {
            using Raster<Rgb8> source = Raster.Create<Rgb8>(20, 20, new Rgb8(0, 200, 0));
            using MemoryStream encoded = new();
            source.Save(encoded, JpegFormat.Info);
            pdf.Pages[0].GetImages()[0].Image.ReplaceWithJpeg(encoded.ToArray());
        }

        return pdf;
    }

    [Fact]
    public void CoveredPixelsAreBlackAndTheRestKeepTheirColour()
    {
        using PdfDocument pdf = ImagePage();
        PdfImage before = Assert.Single(pdf.Pages[0].GetImages()).Image;
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 150, 150, 200)));
        Assert.Equal(1, pdf.ApplyRedactions().Images);
        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        PdfPlacedImage placed = Assert.Single(saved.Pages[0].GetImages());
        Assert.EndsWith("R", placed.Name, StringComparison.Ordinal);
        _ = before;

        // On the rendered page (400 points high, y down): the area's corner of the image is black, the rest green.
        using Raster<Rgba8> render = saved.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new Rgba8(0, 0, 0, 255), render.GetPixel(125, 400 - 175));
        Assert.Equal(new Rgba8(0, 200, 0, 255), render.GetPixel(175, 400 - 125));
        Assert.Equal(new Rgba8(0, 200, 0, 255), render.GetPixel(175, 400 - 175));
    }

    [Fact]
    public void TheInteriorColourPaintsThePixels()
    {
        using PdfDocument pdf = ImagePage();
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 200, 200)) { InteriorColor = new PdfRgbColor(1, 0, 0) });
        pdf.ApplyRedactions();
        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        using Raster<Rgba8> raster = Assert.Single(saved.Pages[0].GetImages()).Image.ToRaster<Rgba8>();
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(10, 10));
    }

    [Fact]
    public void AJpegStaysAJpeg()
    {
        using PdfDocument pdf = ImagePage(jpeg: true);
        Assert.Equal("DCTDecode", Assert.Single(pdf.Pages[0].GetImages()).Image.ImageCodec?.Value);
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 150, 150)));
        pdf.ApplyRedactions();
        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        PdfImage image = Assert.Single(saved.Pages[0].GetImages()).Image;
        Assert.Equal("DCTDecode", image.ImageCodec?.Value);
        using Raster<Rgba8> render = saved.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(render.GetPixel(120, 400 - 120).G < 40);
        Assert.True(render.GetPixel(180, 400 - 180).G > 150);
    }

    [Theory]
    [InlineData(PdfRedactImages.Remove, 0)]
    [InlineData(PdfRedactImages.None, 1)]
    public void ImagesGoOrStayByTheOption(PdfRedactImages option, int left)
    {
        using PdfDocument pdf = ImagePage();
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 120, 120)));
        pdf.ApplyRedactions(new PdfRedactionOptions { Images = option });
        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        Assert.Equal(left, saved.Pages[0].GetImages().Count);
    }

    [Fact]
    public void AnImageTheAreaMissesIsLeftAlone()
    {
        using PdfDocument pdf = ImagePage();
        PdfStream? before = pdf.Pages[0].GetImages()[0].Image.Stream;
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(300, 300, 350, 350)));
        Assert.Equal(0, pdf.ApplyRedactions().Images);
        Assert.Same(before, pdf.Pages[0].GetImages()[0].Image.Stream);
    }

    [Fact]
    public void AnInlineImageBecomesARedactedXObject()
    {
        // An 8 by 8 grey inline image filling 100, 100 to 200, 200.
        string samples = new('\x80', 64);
        using PdfDocument pdf = RedactionTextTests.Page($"q 100 0 0 100 100 100 cm BI /W 8 /H 8 /CS /G /BPC 8 ID {samples} EI Q", resources: "<< >>");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 150, 150)));
        Assert.Equal(1, pdf.ApplyRedactions().Images);
        string content = Encoding.Latin1.GetString(pdf.Pages[0].GetContent().ToBytes());
        Assert.DoesNotContain("BI", content, StringComparison.Ordinal);
        Assert.Contains("Do", content, StringComparison.Ordinal);
        using Raster<Rgba8> raster = Assert.Single(pdf.Pages[0].GetImages()).Image.ToRaster<Rgba8>();
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(1, 6));
        Assert.Equal(new Rgba8(128, 128, 128, 255), raster.GetPixel(6, 1));
    }

    [Fact]
    public void AStencilMasksCoveredSamplesStopPainting()
    {
        // An 8 by 8 stencil mask that paints everywhere (all zero samples), filled red.
        using PdfDocument pdf = RedactionTextTests.Page("1 0 0 rg q 100 0 0 100 100 100 cm /M Do Q", resources: "<< /XObject << /M 9 0 R >> >>",
            extra: "9 0 obj\n<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ImageMask true /BitsPerComponent 1 /Length 8 >>\nstream\n\0\0\0\0\0\0\0\0\nendstream\nendobj\n");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 150, 150)));
        Assert.Equal(1, pdf.ApplyRedactions().Images);
        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        using Raster<Rgba8> render = saved.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new Rgba8(255, 255, 255, 255), render.GetPixel(125, 842 - 125));
        Assert.Equal(new Rgba8(255, 0, 0, 255), render.GetPixel(175, 842 - 175));
    }

    [Fact]
    public void CoveredAnnotationsGoAndOthersStay()
    {
        using PdfDocument pdf = ImagePage();
        pdf.Pages[0].Annotations.Add(new PdfTextAnnotation(new PdfRectangle(110, 110, 130, 130), "Under the area"));
        pdf.Pages[0].Annotations.Add(new PdfTextAnnotation(new PdfRectangle(300, 300, 320, 320), "Elsewhere"));
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 150, 150)));
        Assert.Equal(1, pdf.ApplyRedactions().RemovedAnnotations);
        PdfAnnotation left = Assert.Single(pdf.Pages[0].Annotations);
        Assert.Equal("Elsewhere", ((PdfTextAnnotation)left).Contents);
    }

    [Fact]
    public void TheOverlayIsDrawn()
    {
        using PdfDocument pdf = RedactionTextTests.Page("BT /F1 12 Tf 72 700 Td (Secret 12345 visible) Tj ET");
        PdfRectangle area = RedactionTextTests.Around([.. pdf.Pages[0].GetText().Glyphs], "12345");
        PdfRedactAnnotation redaction = new(new PdfRectangle(area.Left, area.Bottom - 2, area.Right + 30, area.Top + 2)) { InteriorColor = new PdfRgbColor(0, 0, 0), OverlayText = "XX" };
        redaction.Dictionary["DA"] = PdfString.FromText("/Helv 8 Tf 1 1 1 rg");
        pdf.Pages[0].Annotations.Add(redaction);
        pdf.ApplyRedactions();
        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        Assert.Contains("XX", saved.Pages[0].ExtractText(), StringComparison.Ordinal);
        using Raster<Rgba8> render = saved.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
        Rgba8 pixel = render.GetPixel((int)area.Right + 25, 842 - (int)area.Bottom);
        Assert.Equal(new Rgba8(0, 0, 0, 255), pixel);
    }

    [Fact]
    public void RasterizingLeavesOnlyAnImage()
    {
        using PdfDocument pdf = RedactionTextTests.Page("BT /F1 12 Tf 72 700 Td (Secret 12345 visible) Tj ET");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(RedactionTextTests.Around([.. pdf.Pages[0].GetText().Glyphs], "12345")));
        pdf.ApplyRedactions(new PdfRedactionOptions { RasterizeDpi = 72 });
        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        Assert.Equal("", saved.Pages[0].ExtractText().Trim());
        Assert.Single(saved.Pages[0].GetImages());
        Assert.False(RedactionTextTests.StreamsContain(saved, "Secret"));
    }
}
