using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Cleanup;

/// <summary>Grayscale conversion: Phase 7 task 17.</summary>
public sealed class GrayTests
{
    [Fact]
    public void EveryColourBecomesGrayAndShowsAsBefore()
    {
        using PdfDocument pdf = Colourful();
        using Raster<Rgba8> before = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);

        IReadOnlyList<PdfWarning> warnings = pdf.ConvertToGray();

        Assert.Empty(warnings);
        using Raster<Rgba8> after = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        double worst = 0;
        for (int y = 0; y < after.Height; y++)
        {
            for (int x = 0; x < after.Width; x++)
            {
                Rgba8 p = after.GetPixel(x, y), q = before.GetPixel(x, y);
                Assert.True(Math.Abs(p.R - p.G) <= 1 && Math.Abs(p.G - p.B) <= 1, $"({x}, {y}) is {p}");
                worst = Math.Max(worst, Math.Abs(p.R - ((0.299 * q.R) + (0.587 * q.G) + (0.114 * q.B))));
            }
        }

        Assert.True(worst <= 12, $"a pixel's gray is {worst} levels from the luma it had");
        string content = System.Text.Encoding.Latin1.GetString(pdf.Pages[0].GetContent().ToBytes());
        Assert.DoesNotContain(" rg", content, StringComparison.Ordinal);
        Assert.DoesNotContain(" k\n", content, StringComparison.Ordinal);
        Assert.Single(pdf.Pages[0].Annotations[0].Dictionary.Get<PdfArray>("C")!);
    }

    [Fact]
    public void AnIndexedImageKeepsItsSamplesUnderAGrayPalette()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(2),
            ["Height"] = new PdfInteger(1),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["ColorSpace"] = new PdfArray { PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), new PdfInteger(1), PdfString.FromBytes([255, 0, 0, 0, 0, 255], hex: true) },
        });
        image.SetDecodedBytes([0, 1]);
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = pdf.Objects.Add(image) } };
        page.SetContent(PdfContent.Parse("q 100 0 0 50 0 0 cm /Im Do Q"u8));

        pdf.ConvertToGray();

        PdfArray space = Assert.IsType<PdfArray>(image.Dictionary.Get("ColorSpace"));
        Assert.Equal("DeviceGray", space.GetName(1)!.Value);
        Assert.Equal([76, 29], ((PdfString)space[3]).Bytes.ToArray());
        Assert.Equal([0, 1], image.GetDecodedBytes());
    }

    /// <summary>
    /// A page with an RGB fill, a CMYK stroke, a Separation fill, an RGB image, an inline RGB image, an RGB axial shading, an
    /// RGB tiling pattern, a free-form triangle mesh and a red annotation.
    /// </summary>
    private static PdfDocument Colourful()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        PdfStream rgbImage = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(2),
            ["Height"] = new PdfInteger(2),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
        });
        rgbImage.SetDecodedBytes([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]);
        PdfDictionary axial = new()
        {
            ["ShadingType"] = new PdfInteger(2),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
            ["Coords"] = new PdfArray { new PdfInteger(200), new PdfInteger(0), new PdfInteger(300), new PdfInteger(0) },
            ["Function"] = new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(2),
                ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                ["C0"] = new PdfArray { new PdfInteger(1), new PdfInteger(0), new PdfInteger(0) },
                ["C1"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(1) },
                ["N"] = new PdfInteger(1),
            },
        };
        PdfStream tile = new(new PdfDictionary
        {
            ["PatternType"] = new PdfInteger(1),
            ["PaintType"] = new PdfInteger(1),
            ["TilingType"] = new PdfInteger(1),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10) },
            ["XStep"] = new PdfInteger(10),
            ["YStep"] = new PdfInteger(10),
            ["Resources"] = new PdfDictionary(),
        });
        tile.SetDecodedBytes("0 0.6 0 rg 0 0 5 5 re f"u8);

        // Three vertices: flag, x, y as 8 bits each, then r, g, b as 8 bits each.
        PdfStream mesh = new(new PdfDictionary
        {
            ["ShadingType"] = new PdfInteger(4),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
            ["BitsPerCoordinate"] = new PdfInteger(8),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["BitsPerFlag"] = new PdfInteger(8),
            ["Decode"] = new PdfArray { new PdfInteger(0), new PdfInteger(255), new PdfInteger(0), new PdfInteger(255), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1) },
        });
        mesh.SetDecodedBytes([0, 10, 10, 255, 0, 0, 0, 100, 10, 0, 255, 0, 0, 50, 90, 0, 0, 255]);
        page.Resources = new PdfDictionary
        {
            ["XObject"] = new PdfDictionary { ["Im"] = pdf.Objects.Add(rgbImage) },
            ["Shading"] = new PdfDictionary { ["Ax"] = pdf.Objects.Add(axial), ["Me"] = pdf.Objects.Add(mesh) },
            ["Pattern"] = new PdfDictionary { ["P1"] = pdf.Objects.Add(tile) },
            ["ColorSpace"] = new PdfDictionary
            {
                ["Spot"] = new PdfArray
                {
                    PdfName.Get("Separation"),
                    PdfName.Get("Gold"),
                    PdfName.Get("DeviceCMYK"),
                    new PdfDictionary
                    {
                        ["FunctionType"] = new PdfInteger(2),
                        ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                        ["C0"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) },
                        ["C1"] = new PdfArray { new PdfInteger(0), new PdfReal(0.2), new PdfInteger(1), new PdfInteger(0) },
                        ["N"] = new PdfInteger(1),
                    },
                },
            },
        };
        page.SetContent(PdfContent.Parse(System.Text.Encoding.Latin1.GetBytes(
            "1 0 0 rg 10 300 80 80 re f 0 1 1 0 K 8 w 110 310 60 60 re S /Spot cs 0.8 sc 200 300 80 80 re f "
            + "q 80 0 0 80 10 200 cm /Im Do Q q 200 200 100 80 re W n /Ax sh Q /Pattern cs /P1 scn 310 200 80 80 re f "
            + "q /Me sh Q q 40 0 0 40 300 20 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID \u0000\u0080\u00ff EI Q")));
        page.Dictionary["Annots"] = new PdfArray
        {
            pdf.Objects.Add(new PdfDictionary
            {
                ["Type"] = PdfName.Get("Annot"),
                ["Subtype"] = PdfName.Get("Square"),
                ["Rect"] = new PdfArray { new PdfInteger(150), new PdfInteger(100), new PdfInteger(250), new PdfInteger(150) },
                ["C"] = new PdfArray { new PdfInteger(1), new PdfInteger(0), new PdfInteger(0) },
                ["F"] = new PdfInteger(4),
            }),
        };
        page.Annotations[0].UpdateAppearance();

        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }
}
