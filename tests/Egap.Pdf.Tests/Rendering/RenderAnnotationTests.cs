using Egami;
using Egami.Pixels;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Annotations and form fields (Phase 4 task 15): appearances by flags and intent, generated ones in memory, filters.</summary>
public sealed class RenderAnnotationTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    /// <summary>
    /// The flatten tests' page: a text field without an appearance (20), a checkbox (11), a hidden field (12), a square with a
    /// scaled appearance (30) and its popup (31), a link (32), a NoView note (33) and a square without an appearance (34).
    /// </summary>
    private static byte[] File()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R 11 0 R 12 0 R] /DA (/Helv 0 Tf 0 g) >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /Font << /F1 50 0 R >> >> >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R /Annots [20 0 R 11 0 R 12 0 R 30 0 R 31 0 R 32 0 R 33 0 R 34 0 R] >>")
            .Stream(4, "", "1 0 0 rg 0 0 10 10 re f"u8.ToArray())
            .Object(10, "<< /T (name) /FT /Tx /V (Hello) /DA (/Helv 12 Tf 0 g) /Kids [20 0 R] >>")
            .Object(11, "<< /T (agree) /FT /Btn /V /On /AS /On /Subtype /Widget /Rect [100 100 120 120] /AP << /N << /On 40 0 R /Off 41 0 R >> >> /F 4 >>")
            .Object(12, "<< /T (secret) /FT /Tx /V (x) /Subtype /Widget /Rect [0 200 50 220] /F 2 /AP << /N 40 0 R >> >>")
            .Object(20, "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /Rect [10 10 110 30] /F 4 >>")
            .Object(30, "<< /Type /Annot /Subtype /Square /Rect [150 150 250 200] /F 4 /AP << /N 42 0 R >> /Popup 31 0 R >>")
            .Object(31, "<< /Type /Annot /Subtype /Popup /Rect [0 0 50 50] /Parent 30 0 R >>")
            .Object(32, "<< /Type /Annot /Subtype /Link /Rect [0 250 50 260] /A << /S /URI /URI (https://example.com) >> >>")
            .Object(33, "<< /Type /Annot /Subtype /Text /Rect [260 260 280 280] /F 36 /AP << /N 40 0 R >> >>")
            .Object(34, "<< /Type /Annot /Subtype /Square /Rect [10 260 40 290] /F 4 /C [0 0 1] >>")
            .Stream(40, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 g 0 0 20 20 re f"u8.ToArray())
            .Stream(41, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", ""u8.ToArray())
            .Stream(42, "/Type /XObject /Subtype /Form /BBox [0 0 50 25] /Matrix [2 0 0 2 0 0]", "0 0 1 rg 0 0 50 25 re f"u8.ToArray())
            .Object(50, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return file.StartXref(file.XrefTable("/Size 51 /Root 1 0 R")).ToArray();
    }

    [Fact]
    public void AppearancesAreDrawnInTheirRectanglesByFlagsAndState()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        using Raster<Rgba8> raster = pdf.Pages[0].Render();

        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(200, 125));
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(110, 190));
        Assert.Equal(White, raster.GetPixel(25, 90));
        Assert.Equal(White, raster.GetPixel(270, 30));
        Assert.Equal(White, raster.GetPixel(25, 45));
    }

    [Fact]
    public void MissingAppearancesAreGeneratedWithoutChangingTheDocument()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        using Raster<Rgba8> raster = pdf.Pages[0].Render();

        // The square without an appearance gets its blue border; the empty text field shows its value.
        Assert.Contains(Row(raster, 25, 8, 14), p => p.B > 200 && p.R < 100);
        Assert.Contains(Area(raster, 12, 270, 100, 20), p => p.R < 100);
        PdfDictionary square = (PdfDictionary)pdf.Objects[new PdfReference(34, 0)];
        PdfDictionary widget = (PdfDictionary)pdf.Objects[new PdfReference(20, 0)];
        Assert.Null(square.Get("AP"));
        Assert.Null(widget.Get("AP"));
        Assert.Equal(new PdfRectangle(10, 260, 40, 290), PdfRectangle.FromArray(square.Get<PdfArray>("Rect")));
    }

    [Fact]
    public void FiltersAndThePrintIntentChooseWhatShows()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        using Raster<Rgba8> noForms = pdf.Pages[0].Render(new PdfRenderOptions { Annotations = PdfAnnotationRendering.NoForms });
        using Raster<Rgba8> none = pdf.Pages[0].Render(new PdfRenderOptions { Annotations = PdfAnnotationRendering.None });
        using Raster<Rgba8> print = pdf.Pages[0].Render(new PdfRenderOptions { Intent = PdfRenderIntent.Print });

        Assert.Equal(White, noForms.GetPixel(110, 190));
        Assert.Equal(new Rgba8(0, 0, 255, 255), noForms.GetPixel(200, 125));
        Assert.Equal(White, none.GetPixel(200, 125));
        Assert.Equal(new Rgba8(0, 0, 255, 255), print.GetPixel(200, 125));
        Assert.Equal(new Rgba8(255, 0, 0, 255), none.GetPixel(5, 295));
    }

    private static IEnumerable<Rgba8> Row(Raster<Rgba8> raster, int y, int from, int to)
    {
        for (int x = from; x < to; x++)
        {
            yield return raster.GetPixel(x, y);
        }
    }

    private static IEnumerable<Rgba8> Area(Raster<Rgba8> raster, int left, int top, int width, int height)
    {
        for (int y = top; y < top + height; y++)
        {
            foreach (Rgba8 p in Row(raster, y, left, left + width))
            {
                yield return p;
            }
        }
    }
}
