using System.Text;

using Egami;
using Egami.Colors;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Colour spaces (Phase 4 task 8): Default spaces, output intents, ICC profiles, rendering intents, Lab and Indexed.</summary>
public sealed class RenderColorTests
{
    [Fact]
    public void DefaultSpacesReplaceDeviceSpaces()
    {
        // DeviceGray 0.5 is 128 as it is, and brighter through a linear gray profile.
        PdfDictionary resources = new() { ["ColorSpace"] = new PdfDictionary { ["DefaultGray"] = new PdfArray { PdfName.Get("ICCBased"), Icc(ColorProfile.GrayLinear, 1) } } };
        using Raster<Rgba8> plain = RenderCoreTests.Render("0.5 g 0 0 20 20 re f");
        using Raster<Rgba8> defaulted = RenderCoreTests.Render("0.5 g 0 0 20 20 re f", resources: resources);

        Assert.Equal(128, plain.GetPixel(10, 10).R);
        Assert.InRange(defaulted.GetPixel(10, 10).R, 180, 195);
    }

    [Fact]
    public void IccBasedColoursGoThroughTheirProfiles()
    {
        PdfDictionary resources = new() { ["ColorSpace"] = new PdfDictionary { ["P3"] = new PdfArray { PdfName.Get("ICCBased"), Icc(ColorProfile.DisplayP3, 3) } } };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/P3 cs 0.5 0.2 0.2 sc 0 0 20 20 re f", resources: resources);

        // Display P3's red is more saturated than sRGB's, so the same numbers come out redder and less green.
        Rgba8 p = raster.GetPixel(10, 10);
        Assert.True(p.R > 128 && p.G < 51, p.ToString());
    }

    [Fact]
    public void OutputIntentsSetTheDeviceSpaceTheyMatchUnlessTurnedOff()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 10, 10));
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes("0.5 g 0 0 10 10 re f")));
        PdfStream profile = new(new PdfDictionary { ["N"] = new PdfInteger(1) });
        profile.SetDecodedBytes(ColorProfile.GrayLinear.ToIcc());
        pdf.Catalog["OutputIntents"] = new PdfArray
        {
            new PdfDictionary { ["Type"] = PdfName.Get("OutputIntent"), ["S"] = PdfName.Get("GTS_PDFX"), ["DestOutputProfile"] = pdf.Objects.Add(profile) },
        };

        using Raster<Rgba8> intended = page.Render();
        using Raster<Rgba8> ignored = page.Render(new PdfRenderOptions { UseOutputIntents = false });

        Assert.InRange(intended.GetPixel(5, 5).R, 180, 195);
        Assert.Equal(128, ignored.GetPixel(5, 5).R);
    }

    [Fact]
    public void LabIndexedAndSeparationColours()
    {
        PdfDictionary resources = new()
        {
            ["ColorSpace"] = new PdfDictionary
            {
                ["L"] = new PdfArray { PdfName.Get("Lab"), new PdfDictionary { ["WhitePoint"] = new PdfArray { new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089) } } },
                ["I"] = new PdfArray { PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), new PdfInteger(1), PdfString.FromBytes([255, 0, 0, 0, 0, 255]) },
                ["S"] = new PdfArray
                {
                    PdfName.Get("Separation"), PdfName.Get("Spot"), PdfName.Get("DeviceRGB"),
                    new PdfDictionary { ["FunctionType"] = new PdfInteger(2), ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) }, ["C0"] = new PdfArray { new PdfInteger(1), new PdfInteger(1), new PdfInteger(1) }, ["C1"] = new PdfArray { new PdfInteger(0), new PdfInteger(1), new PdfInteger(0) }, ["N"] = new PdfInteger(1) },
                },
            },
        };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/L cs 50 0 0 sc 0 0 10 10 re f /I cs 1 sc 10 0 10 10 re f /S cs 1 sc 0 10 10 10 re f /S cs 0.5 sc 10 10 10 10 re f", resources: resources);

        Assert.InRange(raster.GetPixel(5, 15).R, 115, 122);
        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(15, 15));
        Assert.Equal(new Rgba8(0, 255, 0, 255), raster.GetPixel(5, 5));
        Assert.InRange(raster.GetPixel(15, 5).R, 126, 130);
    }

    [Fact]
    public void CalRgbAndCalGrayApplyTheirGammaAndMatrix()
    {
        // Gamma 1 with sRGB's own primaries is linear sRGB: 0.5 linear is 188 encoded, where DeviceRGB would give 128.
        PdfDictionary linear = new()
        {
            ["WhitePoint"] = new PdfArray { new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089) },
            ["Matrix"] = new PdfArray { new PdfReal(0.4124), new PdfReal(0.2126), new PdfReal(0.0193), new PdfReal(0.3576), new PdfReal(0.7152), new PdfReal(0.1192), new PdfReal(0.1805), new PdfReal(0.0722), new PdfReal(0.9505) },
        };
        PdfDictionary gray = new() { ["WhitePoint"] = new PdfArray { new PdfReal(0.9505), new PdfInteger(1), new PdfReal(1.089) }, ["Gamma"] = new PdfInteger(1) };
        PdfDictionary resources = new()
        {
            ["ColorSpace"] = new PdfDictionary
            {
                ["C"] = new PdfArray { PdfName.Get("CalRGB"), linear },
                ["K"] = new PdfArray { PdfName.Get("CalGray"), gray },
            },
        };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/C cs 0.5 0.5 0.5 sc 0 0 10 20 re f /K cs 0.5 sc 10 0 10 20 re f", resources: resources);

        Assert.InRange(raster.GetPixel(5, 10).R, 183, 192);
        Assert.InRange(raster.GetPixel(15, 10).G, 183, 192);
    }

    [Fact]
    public void RenderingIntentsReachTheTransform()
    {
        // ProPhoto's saturated green is far outside sRGB: perceptual and relative colorimetric may map it differently, and
        // the render must follow the ri operator either way without failing.
        PdfDictionary resources = new() { ["ColorSpace"] = new PdfDictionary { ["W"] = new PdfArray { PdfName.Get("ICCBased"), Icc(ColorProfile.ProPhotoRgb, 3) } } };
        using Raster<Rgba8> relative = RenderCoreTests.Render("/W cs 0.1 0.9 0.1 sc 0 0 20 20 re f", resources: resources);
        using Raster<Rgba8> perceptual = RenderCoreTests.Render("/Perceptual ri /W cs 0.1 0.9 0.1 sc 0 0 20 20 re f", resources: resources);

        Assert.True(relative.GetPixel(10, 10).G > 200);
        Assert.True(perceptual.GetPixel(10, 10).G > 150);
    }

    [Fact]
    public void DeviceCmykWithoutAnOutputIntentGoesThroughFogra39()
    {
        ColorTransform fogra = ColorTransform.Create(ColorProfile.Fogra39, ColorProfile.Srgb, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true });
        Rgb8[] expected = new Rgb8[1];
        fogra.Apply<Cmyk8, Rgb8>([new Cmyk8(0, 255, 255, 0)], expected);

        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 10, 10));
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes("0 1 1 0 k 0 0 10 10 re f")));
        using Raster<Rgba8> shown = page.Render();
        using Raster<Rgba8> chosen = page.Render(new PdfRenderOptions { UseOutputIntents = false, DefaultCmykProfile = ColorProfile.Fogra39 });

        Rgba8 p = shown.GetPixel(5, 5);
        Assert.True(Math.Abs(p.R - expected[0].R) <= 1 && Math.Abs(p.G - expected[0].G) <= 1 && Math.Abs(p.B - expected[0].B) <= 1, $"{p} against {expected[0]}");
        Assert.Equal(p, chosen.GetPixel(5, 5));
        Assert.Throws<ArgumentException>(() => new PdfRenderOptions { DefaultCmykProfile = ColorProfile.Srgb });
    }

    private static PdfReference Icc(ColorProfile profile, int components)
    {
        PdfStream stream = new(new PdfDictionary { ["N"] = new PdfInteger(components) });
        stream.SetDecodedBytes(profile.ToIcc());
        return RenderCoreTests.Add(stream);
    }
}
