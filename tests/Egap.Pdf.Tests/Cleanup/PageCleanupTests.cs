using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Cleanup;

/// <summary>Blank page removal and deskew: Phase 7 task 18.</summary>
public sealed class PageCleanupTests
{
    [Fact]
    public void RemovesPagesThatShowNothing()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference font = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        string[] contents =
        [
            "BT /F1 12 Tf 72 700 Td 14 TL (Text) ' (on) ' (a) ' (page) ' (that) ' (holds) ' (several) ' (lines) ' ET",  // kept
            "",                                        // nothing at all
            "q 1 g 0 0 612 792 re f Q",                // a white fill
            "0 g 300 400 1 1 re f",                    // a speck below the ink limit
            "0 g 72 72 200 200 re f",                  // kept: a black square
        ];
        foreach (string content in contents)
        {
            PdfPage page = pdf.Pages.AddBlank();
            page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
            page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes(content)));
        }

        Assert.Equal(3, pdf.RemoveBlankPages());

        Assert.Equal(2, pdf.Pages.Count);
        Assert.Contains("Text", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.RemoveBlankPages(new BlankPageOptions { WhiteLevel = 0 }));
    }

    [Fact]
    public void ThresholdsAreOptions()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.SetContent(PdfContent.Parse("0.85 g 0 0 612 792 re f"u8));

        Assert.Equal(0, pdf.RemoveBlankPages());
        Assert.Equal(1, pdf.RemoveBlankPages(new BlankPageOptions { WhiteLevel = 0.8 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StraightensScans(bool bilevel)
    {
        using PdfDocument pdf = PdfDocument.Create();
        using Raster<Gray8> scan = Skewed(850, 1100, 3.0, bilevel ? (byte)0 : (byte)40, bilevel ? (byte)255 : (byte)245);
        PdfPage page = pdf.Pages.AddBlank();
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawImage(scan, new Egami.Drawing.RectF(0, 0, 612, 792));
        }

        if (bilevel)
        {
            Egap.Pdf.Optimizing.ImageSurvey.Run(pdf).ToList().ForEach(i => Egap.Pdf.Optimizing.BilevelEncoder.Optimize(i, 0));
        }

        Assert.Equal(1, pdf.DeskewScannedPages());

        PdfImage image = Assert.Single(page.GetImages()).Image;
        Assert.Equal((850, 1100), (image.Width, image.Height));
        Assert.Equal(bilevel ? "CCITTFaxDecode" : "FlateDecode", image.ImageCodec?.Value ?? image.Filters[0].Value);
        using Raster<Rgba8> straight = image.ToRaster<Rgba8>();
        Assert.InRange(straight.DetectSkew(), -0.3, 0.3);
        Assert.Equal(0, pdf.DeskewScannedPages());
    }

    [Fact]
    public void LeavesPagesThatAreMoreThanAScan()
    {
        using PdfDocument pdf = PdfDocument.Create();
        using Raster<Gray8> scan = Skewed(400, 500, 4.0);
        PdfPage page = pdf.Pages.AddBlank();
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawImage(scan, new Egami.Drawing.RectF(0, 0, 612, 792));
            canvas.FillRect(new Egami.Drawing.RectF(10, 10, 20, 20), new Egami.Drawing.SolidPaint(new RgbaF32(1, 0, 0, 1)));
        }

        Assert.Equal(0, pdf.DeskewScannedPages());
    }

    /// <summary>Lines of black bars like text, turned by <paramref name="degrees"/>, on white.</summary>
    private static Raster<Gray8> Skewed(int width, int height, double degrees, byte inkLevel = 0, byte paper = 255)
    {
        Raster<Gray8> raster = Raster.Create<Gray8>(width, height, new Gray8(paper));
        double t = Math.Tan(degrees * Math.PI / 180);
        for (int y = 0; y < height; y++)
        {
            Span<Gray8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                double straightY = y - ((x - (width / 2.0)) * t);
                int line = (int)Math.Floor(straightY / 30);
                bool ink = straightY - (line * 30) < 10 && x > 60 && x < width - 60 && ((x / 17) % 5 != 4) && line > 1 && line * 30 < height - 80;
                if (ink)
                {
                    row[x] = new Gray8(inkLevel);
                }
            }
        }

        return raster;
    }
}
