using System.Numerics;
using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using DrawingPath = Egami.Drawing.Path;

namespace Egap.Pdf.Tests.Canvas;

/// <summary>The PDF canvas's state, paths, clips and solid paint: Phase 3 task 8.</summary>
public sealed class PdfCanvasTests
{
    private static readonly SolidPaint Red = new(new RgbaF32(1, 0, 0, 1));

    [Theory]
    [InlineData(0, 612, 792)]
    [InlineData(90, 792, 612)]
    [InlineData(180, 612, 792)]
    [InlineData(270, 792, 612)]
    public void DrawsWhatTheViewerShowsAtEveryRotation(int rotate, float width, float height)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Rotate = rotate;
        using (PdfCanvas canvas = new(page))
        {
            Assert.Equal((width, height), (canvas.Width, canvas.Height));
            canvas.FillRect(new RectF(10, 20, 30, 40), Red);
        }

        // The rectangle's corner nearest the displayed top left is 10 right and 20 down from that corner of the page.
        PdfRectangle bounds = Assert.Single(Paths(page)).Bounds;
        PdfRectangle expected = rotate switch
        {
            0 => new PdfRectangle(10, 792 - 60, 40, 792 - 20),
            90 => new PdfRectangle(20, 10, 60, 40),
            180 => new PdfRectangle(612 - 40, 20, 612 - 10, 60),
            _ => new PdfRectangle(612 - 60, 792 - 40, 612 - 20, 792 - 10),
        };
        Assert.Equal(expected, bounds);
    }

    [Fact]
    public void DrawsFromTheCropBoxCorner()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.CropBox = new PdfRectangle(100, 100, 400, 500);
        using (PdfCanvas canvas = new(page))
        {
            Assert.Equal((300f, 400f), (canvas.Width, canvas.Height));
            canvas.FillRect(new RectF(0, 0, 10, 10), Red);
        }

        Assert.Equal(new PdfRectangle(100, 490, 110, 500), Assert.Single(Paths(page)).Bounds);
    }

    [Fact]
    public void PdfCoordinatesAreUnflipped()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        using (PdfCanvas canvas = new(page, new PdfCanvasOptions { Coordinates = PdfCanvasCoordinates.Pdf }))
        {
            Assert.True(canvas.PageMatrix.IsIdentity);
            canvas.FillRect(new RectF(10, 20, 30, 40), Red);
        }

        Assert.Equal(new PdfRectangle(10, 20, 40, 60), Assert.Single(Paths(page)).Bounds);
    }

    [Fact]
    public void WritesTransformsLazilyAndClipsInDeviceSpace()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        using (PdfCanvas canvas = new(page))
        {
            canvas.Save();
            canvas.Translate(10, 10);
            canvas.Scale(2);
            canvas.ClipRect(new RectF(0, 0, 20, 20));
            canvas.FillRect(new RectF(0, 0, 5, 5), Red);
            canvas.FillRect(new RectF(5, 5, 5, 5), Red);
            canvas.Rotate(90);
            canvas.StrokePath(DrawingPath.Line(Vector2.Zero, new Vector2(10, 0)), new Stroke(2) { Cap = LineCap.Round, Dash = new DashPattern([3, 1]) }, Red);
            canvas.Restore();
            canvas.FillRect(new RectF(0, 0, 1, 1), Red);
        }

        string text = Content(page);
        Assert.Equal(
            "q\n1 0 0 -1 0 100 cm\nq\n10 10 40 40 re\nW\nn\nq\n2 0 0 2 10 10 cm\n1 0 0 rg\n0 0 5 5 re\nf\n5 5 5 5 re\nf\nQ\n"
            + "q\n0 2 -2 0 10 10 cm\n1 0 0 RG\n2 w\n1 J\n4 M\n[3 1] 0 d\n0 0 m\n10 0 l\nS\nQ\nQ\nq\n1 0 0 rg\n0 0 1 1 re\nf\nQ\nQ\n",
            text);
    }

    [Fact]
    public void ClipsToTheOutsideOfAPath()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        using (PdfCanvas canvas = new(page))
        {
            canvas.ClipRect(new RectF(10, 10, 50, 50), ClipOp.Difference);
            canvas.FillRect(new RectF(0, 0, 100, 100), Red);
        }

        // The clip is the canvas less the square: its bounds are the whole canvas.
        PathPaint clip = Paths(page)[0];
        Assert.Equal(PdfPathPaint.Clip, clip.Paint);
        Assert.Equal(new PdfRectangle(0, 0, 100, 100), clip.Bounds);
    }

    [Fact]
    public void SetsOpacityAndBlendThroughSharedGraphicsStates()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage first = pdf.Pages.AddBlank(), second = pdf.Pages.AddBlank();
        foreach (PdfPage page in new[] { first, second })
        {
            using PdfCanvas canvas = new(page);
            canvas.FillRect(new RectF(0, 0, 10, 10), new SolidPaint(new RgbaF32(0, 0, 1, 0.5f)) { BlendMode = BlendMode.Multiply });
            canvas.FillRect(new RectF(0, 0, 10, 10), new SolidPaint(new RgbaF32(0, 0, 1, 1)) { Opacity = 0.5f, BlendMode = BlendMode.Multiply });
        }

        PdfObject state = first.Resources!.Get<PdfDictionary>("ExtGState")!["EgG0"]!;
        Assert.Equal(state, second.Resources!.Get<PdfDictionary>("ExtGState")!["EgG0"]);
        Assert.Single(first.Resources!.Get<PdfDictionary>("ExtGState")!.Keys);
        PdfDictionary gs = (PdfDictionary)state.Resolve();
        Assert.Equal((0.5, 0.5, "Multiply"), (gs.GetNumber("CA"), gs.GetNumber("ca"), gs.GetName("BM")!.Value));
        Assert.Equal(1, Content(first).Split("/EgG0 gs").Length - 1);
    }

    [Fact]
    public void KeepsThePagesContentAndDrawsOverIt()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfStream old = new();
        old.SetDecodedBytes("2 0 0 2 0 0 cm 0 0 1 1 re f"u8);
        PdfReference reference = pdf.Objects.Add(old);
        page.Dictionary["Contents"] = reference;

        using (PdfCanvas canvas = new(page))
        {
            canvas.FillRect(new RectF(0, 0, 1, 1), Red);
        }

        PdfArray contents = page.Dictionary.Get<PdfArray>("Contents")!;
        Assert.Same(reference, contents[1]);
        Assert.Equal([new PdfMatrix(2, 0, 0, 2, 0, 0), new PdfMatrix(1, 0, 0, -1, 0, 792)], Paths(page).Select(p => p.Matrix));
    }

    [Fact]
    public void DrawsWhatPdfCannotExpressAsAnImage()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 72, 72));
        PdfCanvas canvas = new(page, new PdfCanvasOptions { FallbackDpi = 72 });
        canvas.FillRect(new RectF(10, 10, 20, 20), new SolidPaint(new RgbaF32(1, 0, 0, 1)) { BlendMode = BlendMode.Xor });
        canvas.Dispose();

        PdfWarning warning = Assert.Single(canvas.Warnings);
        Assert.Equal(PdfWarningCode.RasterFallback, warning.Code);
        PdfStream image = (PdfStream)page.Resources!.Get<PdfDictionary>("XObject")!["EgX0"]!.Resolve();
        Assert.Equal((72, 72), (image.Dictionary.GetInt("Width"), image.Dictionary.GetInt("Height")));
        Assert.NotNull(image.Dictionary.Get("SMask"));
        Assert.Contains("72 0 0 -72 0 72 cm\n/EgX0 Do\n", Content(page), StringComparison.Ordinal);

        using PdfCanvas strict = new(page, new PdfCanvasOptions { Strict = true });
        Assert.Throws<NotSupportedException>(() => strict.FillRect(new RectF(0, 0, 1, 1), new SolidPaint(new RgbaF32(1, 0, 0, 1)) { BlendMode = BlendMode.Xor }));
    }

    [Fact]
    public void CountsRepeatedFallbacksInOneWarning()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 72, 72));
        PdfCanvas canvas = new(page, new PdfCanvasOptions { FallbackDpi = 36 });
        SolidPaint xor = new(new RgbaF32(1, 0, 0, 1)) { BlendMode = BlendMode.Xor };
        for (int i = 0; i < 3; i++)
        {
            canvas.FillRect(new RectF(i * 10, 0, 5, 5), xor);
        }

        canvas.FillRect(new RectF(0, 20, 5, 5), new SolidPaint(new RgbaF32(1, 0, 0, 1)) { Blur = new MaskBlur(2) });
        canvas.Dispose();

        Assert.Equal(
            ["PDF cannot express the blend mode Xor; it was drawn as an image at 36 dpi. (3 times)", "PDF cannot express a blurred paint; it was drawn as an image at 36 dpi."],
            canvas.Warnings.Select(w => w.Message));
    }

    [Fact]
    public void ThrowsInStrictModeForEveryFallback()
    {
        using PdfDocument pdf = PdfDocument.Create();
        using PdfCanvas strict = new(pdf.Pages.AddBlank(), new PdfCanvasOptions { Strict = true });
        Assert.Throws<NotSupportedException>(() => strict.SaveLayer(new LayerOptions { Backdrop = o => o.Blur(2) }));
    }

    [Fact]
    public void WritesAnImageOnceHoweverOftenItIsDrawn()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        using Raster<Rgba8> image = Raster.Create<Rgba8>(4, 2, new Rgba8(10, 20, 30, 255));
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawImage(image, new Vector2(0, 0));
            canvas.DrawImage(image, new RectF(0, 0, 4, 1), new RectF(10, 10, 40, 10));
        }

        PdfDictionary xObjects = page.Resources!.Get<PdfDictionary>("XObject")!;
        PdfStream stream = (PdfStream)Assert.Single(xObjects.Entries).Value.Resolve();
        Assert.Null(stream.Dictionary.Get("SMask"));
        Assert.Equal("DeviceRGB", stream.Dictionary.GetName("ColorSpace")!.Value);
        Assert.Equal(3 * 8, stream.GetDecodedBytes().Length);
        string text = Content(page);
        Assert.Contains("4 0 0 -2 0 2 cm\n/EgX0 Do\n", text, StringComparison.Ordinal);
        Assert.Contains("10 10 40 10 re\nW\nn\n40 0 0 -20 10 30 cm\n/EgX0 Do\n", text, StringComparison.Ordinal);
    }

    private static string Content(PdfPage page)
    {
        PdfObject contents = page.Dictionary["Contents"]!;
        PdfStream stream = (PdfStream)(contents.Resolve() is PdfArray array ? array[^1] : contents).Resolve();
        return Encoding.ASCII.GetString(stream.GetDecodedBytes());
    }

    private static List<PathPaint> Paths(PdfPage page)
    {
        Recorder recorder = new();
        recorder.Walk(page);
        return recorder.Paths;
    }

    private sealed record PathPaint(PdfRectangle Bounds, PdfPathPaint Paint, PdfMatrix Matrix);

    private sealed class Recorder : PdfContentWalker
    {
        public List<PathPaint> Paths { get; } = [];

        protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
        {
            Paths.Add(new PathPaint(Round(path.GetBounds(State.Matrix)!.Value), paint, State.Matrix));
        }

        private static PdfRectangle Round(PdfRectangle r)
        {
            return new PdfRectangle(Math.Round(r.Left, 3), Math.Round(r.Bottom, 3), Math.Round(r.Right, 3), Math.Round(r.Top, 3));
        }
    }
}
