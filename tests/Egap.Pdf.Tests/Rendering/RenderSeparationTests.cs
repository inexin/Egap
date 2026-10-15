using System.Text;

using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Separation and DeviceN (Phase 4 task 9): tint transforms, the All and None colourants, overprint ignored.</summary>
public sealed class RenderSeparationTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void NoneMarksNothingAndAllIsDrawnThroughItsAlternate()
    {
        PdfDictionary resources = new()
        {
            ["ColorSpace"] = new PdfDictionary
            {
                ["N"] = Separation("None"),
                ["A"] = Separation("All"),
            },
        };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/N cs 1 sc 0 0 10 20 re f /A cs 1 sc 10 0 10 20 re f /N CS 1 SC 4 w 0 10 m 20 10 l S", resources: resources);

        Assert.Equal(White, raster.GetPixel(5, 5));
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(15, 5));
        Assert.Equal(White, raster.GetPixel(5, 10));
    }

    [Fact]
    public void DeviceNMixesItsColourantsThroughAPostScriptTransform()
    {
        // Cyan and magenta inks to RGB: red falls with cyan, green with magenta.
        PdfStream transform = new(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(4),
            ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1) },
            ["Range"] = new PdfArray { new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1) },
        });
        transform.SetDecodedBytes(Encoding.ASCII.GetBytes("{ 1 exch sub exch 1 exch sub exch 1 }"), compress: false);
        PdfDictionary resources = new()
        {
            ["ColorSpace"] = new PdfDictionary
            {
                ["D"] = new PdfArray { PdfName.Get("DeviceN"), new PdfArray { PdfName.Get("Cyan"), PdfName.Get("Magenta") }, PdfName.Get("DeviceRGB"), RenderCoreTests.Add(transform) },
            },
        };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/D cs 1 0 sc 0 0 10 20 re f /D cs 0 1 sc 10 0 10 20 re f", resources: resources);

        Assert.Equal(new Rgba8(0, 255, 255, 255), raster.GetPixel(5, 5));
        Assert.Equal(new Rgba8(255, 0, 255, 255), raster.GetPixel(15, 5));
    }

    [Fact]
    public void OverprintIsIgnored()
    {
        PdfDictionary resources = new() { ["ExtGState"] = new PdfDictionary { ["O"] = new PdfDictionary { ["OP"] = PdfBoolean.True, ["op"] = PdfBoolean.True, ["OPM"] = new PdfInteger(1) } } };
        using Raster<Rgba8> raster = RenderCoreTests.Render("1 0 0 0 k 0 0 20 20 re f /O gs 0 1 0 0 k 5 5 10 10 re f", resources: resources);

        // Knocked out as without overprint: the magenta square replaces the cyan under it.
        Rgba8 inside = raster.GetPixel(10, 10);
        Assert.True(inside.R > 200 && inside.G < 60, inside.ToString());
    }

    private static PdfArray Separation(string name)
    {
        return new PdfArray
        {
            PdfName.Get("Separation"), PdfName.Get(name), PdfName.Get("DeviceGray"),
            new PdfDictionary { ["FunctionType"] = new PdfInteger(2), ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) }, ["C0"] = new PdfArray { new PdfInteger(1) }, ["C1"] = new PdfArray { new PdfInteger(0) }, ["N"] = new PdfInteger(1) },
        };
    }
}
