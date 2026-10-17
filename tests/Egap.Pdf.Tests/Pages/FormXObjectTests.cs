using Egami;
using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Pages as form XObjects: Phase 7 task 9.</summary>
public sealed class FormXObjectTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void AFormShowsThePageAsAViewerDoes(int rotate)
    {
        using PdfDocument source = PdfDocument.Create();
        PdfPage page = Drawn(source, rotate);
        using PdfDocument target = PdfDocument.Create();

        PdfFormXObject form = page.ToFormXObject(target);

        (double width, double height) = rotate is 90 or 270 ? (200.0, 300.0) : (300.0, 200.0);
        Assert.Equal((width, height), (form.Width, form.Height));
        PdfPage sheet = target.Pages.AddBlank(new PdfRectangle(0, 0, width, height));
        using (PdfCanvas canvas = new(sheet))
        {
            canvas.DrawForm(form, new RectF(0, 0, (float)width, (float)height));
        }

        using Raster<Rgba8> expected = page.Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        using Raster<Rgba8> actual = sheet.Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.InRange(MeanDifference(expected, actual), 0, 1.5);
    }

    [Fact]
    public void FormsFromOneSourceShareItsResources()
    {
        using PdfDocument source = PdfDocument.Create();
        PdfReference font = source.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        for (int i = 0; i < 3; i++)
        {
            PdfPage p = source.Pages.AddBlank();
            p.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
            p.SetContent(PdfContent.Parse("BT /F1 12 Tf 72 700 Td (Hello) Tj ET"u8));
        }

        using PdfDocument target = PdfDocument.Create();
        PdfFormXObject[] forms = [.. source.Pages.Select(p => p.ToFormXObject(target))];

        PdfObject[] fonts = [.. forms.Select(f => f.Stream.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Font")!["F1"]!)];
        Assert.All(fonts, f => Assert.IsType<PdfReference>(f));
        Assert.Single(fonts.Distinct());
    }

    [Fact]
    public void PrintableAppearancesComeAlongAndTheRestIsReported()
    {
        using PdfDocument source = PdfDocument.Create();
        PdfPage page = source.Pages.AddBlank(new PdfRectangle(0, 0, 200, 200));
        PdfStream stamp = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10) },
        });
        stamp.SetDecodedBytes("1 0 0 rg 0 0 10 10 re f"u8);
        PdfReference appearance = source.Objects.Add(stamp);
        page.Dictionary["Annots"] = new PdfArray
        {
            source.Objects.Add(Annotation("Stamp", 4, new PdfDictionary { ["N"] = appearance }, 50, 50, 150, 150)),
            source.Objects.Add(Annotation("Square", 0, new PdfDictionary { ["N"] = appearance }, 0, 0, 20, 20)),
            source.Objects.Add(Annotation("Link", 4, null, 0, 180, 200, 200)),
        };
        page.Dictionary["StructParents"] = new PdfInteger(0);
        using PdfDocument target = PdfDocument.Create();

        PdfFormXObject form = page.ToFormXObject(target);

        Assert.Equal(3, form.Warnings.Count);
        Assert.All(form.Warnings, w => Assert.Equal(PdfWarningCode.NotImported, w.Code));
        PdfPage sheet = target.Pages.AddBlank(new PdfRectangle(0, 0, 200, 200));
        using (PdfCanvas canvas = new(sheet))
        {
            canvas.DrawForm(form, new RectF(0, 0, 200, 200));
        }

        using Raster<Rgba8> shown = sheet.Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        Assert.Equal(new Rgba8(255, 0, 0, 255), shown.GetPixel(100, 100));
        Assert.Equal(new Rgba8(255, 255, 255, 255), shown.GetPixel(10, 190));
    }

    [Fact]
    public void WorksWithinTheSameDocument()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Drawn(pdf, 0);
        PdfFormXObject form = page.ToFormXObject(pdf);
        PdfPage sheet = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 600, 400));
        using (PdfCanvas canvas = new(sheet))
        {
            canvas.DrawForm(form, new RectF(0, 0, 300, 200));
            canvas.DrawForm(form, new RectF(300, 200, 300, 200));
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        Assert.Equal(2, reopened.Pages.Count);
        Assert.Equal(new Rgba8(0, 0, 255, 255), reopened.Pages[1].Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken).GetPixel(320, 220));
    }

    /// <summary>A 300 by 200 crop box from a larger media box, with a blue block at the shown top left and red at the bottom right.</summary>
    private static PdfPage Drawn(PdfDocument pdf, int rotate)
    {
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        page.CropBox = new PdfRectangle(50, 100, 350, 300);
        using (PdfCanvas canvas = new(page))
        {
            canvas.FillRect(new RectF(0, 0, 60, 40), new SolidPaint(new RgbaF32(0, 0, 1, 1)));
            canvas.FillRect(new RectF(canvas.Width - 80, canvas.Height - 30, 80, 30), new SolidPaint(new RgbaF32(1, 0, 0, 1)));
        }

        page.Rotate = rotate;
        return page;
    }

    private static PdfDictionary Annotation(string subtype, int flags, PdfDictionary? ap, int x0, int y0, int x1, int y1)
    {
        PdfDictionary annotation = new()
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get(subtype),
            ["F"] = new PdfInteger(flags),
            ["Rect"] = new PdfArray { new PdfInteger(x0), new PdfInteger(y0), new PdfInteger(x1), new PdfInteger(y1) },
        };
        if (ap is not null)
        {
            annotation["AP"] = ap;
        }

        return annotation;
    }

    private static double MeanDifference(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgba8 p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B)) / 3.0;
            }
        }

        return sum / (a.Width * a.Height);
    }
}
