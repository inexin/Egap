using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Canvas;

/// <summary>Layers, soft masks, pictures, meshes and patches on the PDF canvas: Phase 3 task 11.</summary>
public sealed class PdfCanvasLayerTests
{
    private static readonly SolidPaint Black = new(new RgbaF32(0, 0, 0, 1));

    [Fact]
    public void WritesALayerAsATransparencyGroup()
    {
        (PdfPage page, string text, PdfCanvas canvas) = Draw(c =>
        {
            c.SaveLayer(new LayerOptions { Opacity = 0.5f, Isolated = false });
            c.FillRect(new RectF(0, 0, 10, 10), Black);
            c.Restore();
        });

        Assert.Empty(canvas.Warnings);
        Assert.Contains("/EgG0 gs\n/EgX0 Do\n", text, StringComparison.Ordinal);
        PdfStream form = (PdfStream)page.Resources!.Get<PdfDictionary>("XObject")!["EgX0"]!.Resolve();
        PdfDictionary group = form.Dictionary.Get<PdfDictionary>("Group")!;
        Assert.Equal(("Transparency", false), (group.GetName("S")!.Value, group.GetBool("I")));
        Assert.Contains("0 0 10 10 re", Encoding.ASCII.GetString(form.GetDecodedBytes()), StringComparison.Ordinal);
        Assert.Equal(0.5, page.Resources!.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("EgG0")!.GetNumber("ca"));
    }

    [Fact]
    public void WritesASoftMaskWithItsTransferCurve()
    {
        (PdfPage page, _, _) = Draw(c =>
        {
            c.SaveLayer(new LayerOptions { Mask = new SoftMask(m => m.FillRect(new RectF(0, 0, 50, 50), new SolidPaint(new RgbaF32(1, 1, 1, 1))), backdrop: new RgbaF32(0.5f, 0.5f, 0.5f, 1), transfer: v => 1 - v) });
            c.FillRect(new RectF(0, 0, 100, 100), Black);
            c.Restore();
        });

        PdfDictionary mask = page.Resources!.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("EgG0")!.Get<PdfDictionary>("SMask")!;
        Assert.Equal("Luminosity", mask.GetName("S")!.Value);
        Assert.Equal([0.5, 0.5, 0.5], mask.Get<PdfArray>("BC")!.Select(v => ((PdfReal)v).Value));
        byte[] transfer = mask.Get<PdfStream>("TR")!.GetDecodedBytes();
        Assert.Equal((255, 0), (transfer[0], transfer[255]));
        Assert.Contains("0 0 50 50 re", Encoding.ASCII.GetString(mask.Get<PdfStream>("G")!.GetDecodedBytes()), StringComparison.Ordinal);
    }

    [Fact]
    public void DrawsAFilteredLayerAsAnImage()
    {
        (PdfPage page, _, PdfCanvas canvas) = Draw(c =>
        {
            c.SaveLayer(new LayerOptions { Filter = o => o.Blur(2) });
            c.FillRect(new RectF(20, 20, 40, 40), Black);
            c.Restore();
            c.FillRect(new RectF(0, 0, 5, 5), Black);
        });

        Assert.Equal(PdfWarningCode.RasterFallback, Assert.Single(canvas.Warnings).Code);
        PdfStream image = (PdfStream)Assert.Single(page.Resources!.Get<PdfDictionary>("XObject")!.Entries).Value.Resolve();
        Assert.Equal("Image", image.Dictionary.GetName("Subtype")!.Value);
    }

    [Fact]
    public void WritesAPictureOnceAndDrawsItWhereverItIsUsed()
    {
        using PictureRecorder recorder = new(new RectF(0, 0, 20, 20));
        recorder.FillCircle(new Vector2(10, 10), 8, Black);
        Picture picture = recorder.Finish();

        (PdfPage page, string text, _) = Draw(c =>
        {
            c.DrawPicture(picture, Matrix3x2.CreateTranslation(10, 10));
            c.DrawPicture(picture, Matrix3x2.CreateTranslation(50, 10));
        });

        PdfStream form = (PdfStream)Assert.Single(page.Resources!.Get<PdfDictionary>("XObject")!.Entries).Value.Resolve();
        Assert.Equal([0.0, 0, 20, 20], form.Dictionary.Get<PdfArray>("BBox")!.Select(v => ((PdfReal)v).Value));
        Assert.Contains("1 0 0 1 10 10 cm\n/EgX0 Do\nQ\nq\n1 0 0 1 50 10 cm\n/EgX0 Do\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShadesMeshesAndPatches()
    {
        Mesh mesh = new(MeshMode.Triangles, [new(0, 0), new(10, 0), new(0, 10)], [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1)]);
        Patch tensor = Patch.Tensor([.. Enumerable.Range(0, 16).Select(i => new Vector2(20 + (i % 4 * 5), 20 + (i / 4 * 5)))], [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 1, 1)]);

        (PdfPage page, _, PdfCanvas canvas) = Draw(c =>
        {
            c.DrawMesh(mesh, Black);
            c.DrawPatch(tensor);
        });

        Assert.Empty(canvas.Warnings);
        PdfDictionary shadings = page.Resources!.Get<PdfDictionary>("Shading")!;
        Assert.Equal([4L, 7], shadings.Entries.Select(e => ((PdfStream)e.Value.Resolve()).Dictionary.GetInt("ShadingType")!.Value));

        // One triangle: a flag, two 32-bit coordinates and three 16-bit colour samples per vertex.
        Assert.Equal(3 * (1 + 8 + 6), ((PdfStream)shadings["EgS0"]!.Resolve()).GetDecodedBytes().Length);
    }

    private static (PdfPage Page, string Content, PdfCanvas Canvas) Draw(Action<Egami.Drawing.Canvas> draw)
    {
        PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        PdfCanvas canvas = new(page);
        draw(canvas);
        canvas.Dispose();
        return (page, Encoding.ASCII.GetString(((PdfStream)page.Dictionary["Contents"]!.Resolve()).GetDecodedBytes()), canvas);
    }
}
