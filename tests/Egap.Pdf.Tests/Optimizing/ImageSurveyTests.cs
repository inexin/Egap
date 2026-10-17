using Egami;
using Egami.Pixels;

using Egap.Pdf.Optimizing;

namespace Egap.Pdf.Tests.Optimizing;

/// <summary>The optimizer's image survey: Phase 7 task 2.</summary>
public sealed class ImageSurveyTests
{
    [Fact]
    public void ListsAnImageOnceAtItsLargestPlacement()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference image = pdf.Objects.Add(Image(300, 300, Gradient(300, 300)));
        AddPage(pdf, image, "q 72 0 0 72 0 0 cm /Im Do Q q 36 0 0 36 100 100 cm /Im Do Q");
        AddPage(pdf, image, "q 144 0 0 144 0 0 cm /Im Do Q");

        SurveyedImage surveyed = Assert.Single(ImageSurvey.Run(pdf));
        Assert.Equal(3, surveyed.Placements);
        Assert.Equal([0, 1], surveyed.Pages);
        Assert.Equal(144, surveyed.PlacedWidth, 6);
        Assert.Equal(150, surveyed.EffectiveResolution!.Value, 6);
        Assert.Equal(SurveyedImageKind.Photo, surveyed.Kind);
    }

    [Fact]
    public void TakesTheLowerResolutionOfAStretchedOrRotatedImage()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference image = pdf.Objects.Add(Image(600, 300, Gradient(600, 300)));
        // Rotated a quarter turn: 600 samples across 144 points one way, 300 across 288 the other.
        AddPage(pdf, image, "q 0 144 -288 0 300 0 cm /Im Do Q");

        SurveyedImage surveyed = Assert.Single(ImageSurvey.Run(pdf));
        Assert.Equal(300, surveyed.ResolutionX, 6);
        Assert.Equal(75, surveyed.ResolutionY, 6);
        Assert.Equal(75, surveyed.EffectiveResolution!.Value, 6);
    }

    [Fact]
    public void FindsImagesInFormsInlineAndInAnnotationAppearances()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference image = pdf.Objects.Add(Image(100, 100, Flat(100, 100)));
        PdfStream form = Form("q 50 0 0 50 0 0 cm /Im Do Q", new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = image } }, 0, 0, 50, 50);
        PdfReference formRef = pdf.Objects.Add(form);
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Fm"] = formRef } };
        page.SetContent(PdfContent.Parse(System.Text.Encoding.Latin1.GetBytes("q 2 0 0 2 0 0 cm /Fm Do Q BI /W 2 /H 1 /CS /G /BPC 8 ID \u0000\u00ff EI")));


        // An annotation whose 50-point appearance is stretched to a 200-point rectangle draws the image at 200 points.
        PdfDictionary annotation = new()
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get("Stamp"),
            ["Rect"] = new PdfArray { new PdfInteger(300), new PdfInteger(300), new PdfInteger(500), new PdfInteger(500) },
            ["AP"] = new PdfDictionary { ["N"] = formRef },
        };
        page.Dictionary["Annots"] = new PdfArray { pdf.Objects.Add(annotation) };

        IReadOnlyList<SurveyedImage> images = ImageSurvey.Run(pdf);
        Assert.Equal(2, images.Count);
        SurveyedImage shared = images.Single(i => !i.Image.IsInline);
        Assert.Equal(2, shared.Placements);
        Assert.True(shared.InAppearance);
        Assert.Equal(200, shared.PlacedWidth, 6);
        Assert.Equal(36, shared.EffectiveResolution!.Value, 6);
        Assert.Equal(SurveyedImageKind.FlatColor, shared.Kind);
        SurveyedImage inline = images.Single(i => i.Image.IsInline);
        Assert.Equal(SurveyedImageKind.Bilevel, inline.Kind);
    }

    [Fact]
    public void UserUnitsMakeAPlacementLarger()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference image = pdf.Objects.Add(Image(300, 300, Gradient(300, 300)));
        PdfPage page = AddPage(pdf, image, "q 72 0 0 72 0 0 cm /Im Do Q");
        page.UserUnit = 2;

        Assert.Equal(150, Assert.Single(ImageSurvey.Run(pdf)).EffectiveResolution!.Value, 6);
    }

    [Fact]
    public void JudgesTheKindOfPicture()
    {
        using Raster<Rgb8> photo = Raster.Create<Rgb8>(64, 64);
        using Raster<Rgb8> chart = Raster.Create<Rgb8>(64, 64, new Rgb8(255, 255, 255));
        using Raster<Rgb8> scan = Raster.Create<Rgb8>(64, 64, new Rgb8(255, 255, 255));
        Random random = new(1);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                photo.SetPixel(x, y, new Rgb8((byte)(x * 4), (byte)(y * 4), (byte)random.Next(256)));
                if (x > 10 && x < 30 && y > 20)
                {
                    chart.SetPixel(x, y, new Rgb8(30, 90, 200));
                }

                if ((x + y) % 7 == 0)
                {
                    scan.SetPixel(x, y, new Rgb8(0, 0, 0));
                }
            }
        }

        Assert.Equal(SurveyedImageKind.Photo, ImageSurvey.Classify(photo));
        Assert.Equal(SurveyedImageKind.FlatColor, ImageSurvey.Classify(chart));
        Assert.Equal(SurveyedImageKind.Bilevel, ImageSurvey.Classify(scan));
    }

    [Fact]
    public void BilevelByParametersWithoutDecoding()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream fax = Image(1728, 2200, []);
        fax.Dictionary["BitsPerComponent"] = new PdfInteger(1);
        fax.Dictionary["Filter"] = PdfName.Get("CCITTFaxDecode");
        PdfStream stencil = Image(8, 8, new byte[8]);
        stencil.Dictionary["ImageMask"] = PdfBoolean.Get(true);
        stencil.Dictionary["BitsPerComponent"] = new PdfInteger(1);
        stencil.Dictionary.Remove("ColorSpace");
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["A"] = pdf.Objects.Add(fax), ["B"] = pdf.Objects.Add(stencil) } };
        page.SetContent(PdfContent.Parse("q 612 0 0 792 0 0 cm /A Do Q q 8 0 0 8 0 0 cm /B Do Q"u8));

        Assert.All(ImageSurvey.Run(pdf), i => Assert.Equal(SurveyedImageKind.Bilevel, i.Kind));
    }

    [Fact]
    public void ReportsMasks()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream soft = Image(10, 10, new byte[100]);
        PdfStream image = Image(10, 10, Gradient(10, 10));
        image.Dictionary["SMask"] = pdf.Objects.Add(soft);
        PdfStream keyed = Image(10, 10, Gradient(10, 10));
        keyed.Dictionary["Mask"] = new PdfArray { new PdfInteger(0), new PdfInteger(10) };
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["A"] = pdf.Objects.Add(image), ["B"] = pdf.Objects.Add(keyed) } };
        page.SetContent(PdfContent.Parse("q 10 0 0 10 0 0 cm /A Do /B Do Q"u8));

        IReadOnlyList<SurveyedImage> images = ImageSurvey.Run(pdf);
        Assert.Same(soft, images[0].SoftMask);
        Assert.Null(images[0].ColorKeyMask);
        Assert.NotNull(images[1].ColorKeyMask);
    }

    private static PdfPage AddPage(PdfDocument pdf, PdfReference image, string content)
    {
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = image } };
        page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes(content)));
        return page;
    }

    private static PdfStream Image(int width, int height, byte[] gray)
    {
        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(width),
            ["Height"] = new PdfInteger(height),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["ColorSpace"] = PdfName.Get("DeviceGray"),
        });
        image.SetDecodedBytes(gray);
        return image;
    }

    private static PdfStream Form(string content, PdfDictionary resources, int x0, int y0, int x1, int y1)
    {
        PdfStream form = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(x0), new PdfInteger(y0), new PdfInteger(x1), new PdfInteger(y1) },
            ["Resources"] = resources,
        });
        form.SetDecodedBytes(System.Text.Encoding.ASCII.GetBytes(content));
        return form;
    }

    private static byte[] Gradient(int width, int height)
    {
        Random random = new(width);
        byte[] bytes = new byte[width * height];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i % width * 255 / width + random.Next(16)) & 255);
        }

        return bytes;
    }

    private static byte[] Flat(int width, int height)
    {
        byte[] bytes = new byte[width * height];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % width < width / 2 ? 40 : 200);
        }

        return bytes;
    }
}
