using System.Numerics;
using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Canvas;

/// <summary>Gradients, image paints and blend modes on the PDF canvas: Phase 3 task 9.</summary>
public sealed class PdfCanvasPaintTests
{
    private static readonly GradientStop[] Stops = [new(0, new Rgba8(255, 0, 0, 255)), new(0.5f, new Rgba8(0, 255, 0, 255)), new(1, new Rgba8(0, 0, 255, 255))];

    [Fact]
    public void FillsALinearGradientAsAnAxialShadingInsideTheShape()
    {
        (PdfPage page, string text) = Draw(c => c.FillRect(new RectF(10, 10, 80, 20), new LinearGradient(new Vector2(10, 0), new Vector2(90, 0), Stops)));

        Assert.Contains("10 10 80 20 re\nW\nn\nq\n1 0 0 1 0 0 cm\n/Eg0 sh\nQ\n", text, StringComparison.Ordinal);
        PdfDictionary shading = page.Resources!.Get<PdfDictionary>("Shading")!.Get<PdfDictionary>("Eg0")!;
        Assert.Equal(2, shading.GetInt("ShadingType"));
        Assert.Equal([10.0, 0, 90, 0], shading.Get<PdfArray>("Coords")!.Select(v => ((PdfReal)v).Value));
        PdfDictionary function = shading.Get<PdfDictionary>("Function")!;
        Assert.Equal(3, function.GetInt("FunctionType"));
        Assert.Equal(2, function.Get<PdfArray>("Functions")!.Count);
        Assert.Equal(0.5, function.Get<PdfArray>("Bounds")!.GetNumber(0));
    }

    [Fact]
    public void RepeatsAGradientWithOneShadingPerPeriod()
    {
        (PdfPage page, string text) = Draw(c => c.FillRect(new RectF(0, 0, 100, 10), new LinearGradient(new Vector2(0, 0), new Vector2(25, 0), Stops) { Tile = TileMode.Mirror }));

        PdfDictionary shadings = page.Resources!.Get<PdfDictionary>("Shading")!;
        Assert.Equal(4, shadings.Count);
        Assert.Equal([25.0, 0, 50, 0], shadings.Get<PdfDictionary>("Eg1")!.Get<PdfArray>("Coords")!.Select(v => ((PdfReal)v).Value).Reverse().Chunk(2).SelectMany(p => p.Reverse()));
        Assert.Equal(4, text.Split(" sh\n").Length - 1);
    }

    [Fact]
    public void DrawsASweepAsAPatchMesh()
    {
        (PdfPage page, _) = Draw(c => c.FillCircle(new Vector2(50, 50), 40, new SweepGradient(new Vector2(50, 50), Stops)));

        PdfStream mesh = (PdfStream)page.Resources!.Get<PdfDictionary>("Shading")!["Eg0"]!.Resolve();
        Assert.Equal(6, mesh.Dictionary.GetInt("ShadingType"));

        // 24 arcs of 15 degrees: a flag, 12 points of two 32-bit numbers and 4 colours of three 16-bit samples each.
        Assert.Equal(24 * (1 + (12 * 8) + (4 * 6)), mesh.GetDecodedBytes().Length);
    }

    [Fact]
    public void MasksAGradientsAlpha()
    {
        GradientStop[] fade = [new(0, new Rgba8(255, 0, 0, 255)), new(1, new Rgba8(255, 0, 0, 0))];
        (PdfPage page, string text) = Draw(c => c.FillRect(new RectF(0, 0, 100, 100), new LinearGradient(new Vector2(0, 0), new Vector2(100, 0), fade)));

        PdfDictionary state = page.Resources!.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("Eg0")!;
        PdfDictionary mask = state.Get<PdfDictionary>("SMask")!;
        Assert.Equal("Luminosity", mask.GetName("S")!.Value);
        PdfStream group = mask.Get<PdfStream>("G")!;
        Assert.Contains("/Sh0 sh", Encoding.ASCII.GetString(group.GetDecodedBytes()), StringComparison.Ordinal);
        Assert.Equal("DeviceGray", group.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Shading")!.Get<PdfDictionary>("Sh0")!.GetName("ColorSpace")!.Value);
        Assert.StartsWith("q\n1 0 0 -1 0 100 cm\nq\nq\n/Eg0 gs\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplesGradientsMixedInOtherSpaces()
    {
        (PdfPage page, _) = Draw(c => c.FillRect(new RectF(0, 0, 100, 10), new LinearGradient(new Vector2(0, 0), new Vector2(100, 0), Stops) { Interpolation = GradientInterpolation.Oklab }));

        PdfStream function = (PdfStream)page.Resources!.Get<PdfDictionary>("Shading")!.Get<PdfDictionary>("Eg0")!["Function"]!.Resolve();
        Assert.Equal(0, function.Dictionary.GetInt("FunctionType"));
        Assert.Equal(256 * 3, function.GetDecodedBytes().Length);
    }

    [Fact]
    public void FillsGradientStrokesAsOutlines()
    {
        (_, string text) = Draw(c => c.StrokeRect(new RectF(10, 10, 50, 50), new Stroke(4), new LinearGradient(new Vector2(0, 0), new Vector2(100, 0), Stops)));

        Assert.DoesNotContain("SCN", text, StringComparison.Ordinal);
        Assert.Contains(" sh\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TilesAnImagePaintWithAPattern()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(4, 4, new Rgba8(0, 0, 255, 255));
        (PdfPage page, string text) = Draw(c => c.FillRect(new RectF(0, 0, 100, 100), new ImagePaint(image, TileMode.Repeat, TileMode.Mirror)));

        Assert.Contains("/Pattern cs\n/Eg0 scn\n0 0 100 100 re\nf\n", text, StringComparison.Ordinal);
        PdfStream pattern = (PdfStream)page.Resources!.Get<PdfDictionary>("Pattern")!["Eg0"]!.Resolve();
        Assert.Equal((4.0, 8.0), (pattern.Dictionary.GetNumber("XStep"), pattern.Dictionary.GetNumber("YStep")));
        Assert.Equal(2, Encoding.ASCII.GetString(pattern.GetDecodedBytes()).Split("/Im0 Do").Length - 1);
    }

    private static (PdfPage Page, string Content) Draw(Action<Egami.Drawing.Canvas> draw)
    {
        PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        using (PdfCanvas canvas = new(page))
        {
            draw(canvas);
            Assert.Empty(canvas.Warnings);
        }

        return (page, Encoding.ASCII.GetString(((PdfStream)page.Dictionary["Contents"]!.Resolve()).GetDecodedBytes()));
    }
}
