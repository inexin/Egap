using System.Numerics;
using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Rendering;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>The render core (Phase 4 task 2): paths, fills, strokes, dashes, clips and device colours onto a raster.</summary>
public sealed class RenderCoreTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void FillsInDeviceColoursWithYUp()
    {
        using Raster<Rgba8> raster = Render("1 0 0 rg 0 0 10 10 re f 0 0 1 rg 10 10 10 10 re f 0.5 g 0 10 10 10 re f 0 0 0 1 k 10 0 10 10 re f");

        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(5, 15));
        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(15, 5));
        Assert.Equal(new Rgba8(128, 128, 128, 255), raster.GetPixel(5, 5));
        Rgba8 black = raster.GetPixel(15, 15);
        Assert.True(black.R < 60 && black.G < 60 && black.B < 60, black.ToString());
    }

    [Fact]
    public void AppliesTheMatrixAndRestoresIt()
    {
        using Raster<Rgba8> raster = Render("q 2 0 0 2 0 0 cm 0 0 5 5 re f Q 15 15 2 2 re f");

        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(9, 11));
        Assert.Equal(White, raster.GetPixel(11, 9));
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(16, 4));
    }

    [Fact]
    public void FillRulesDiffer()
    {
        // Two nested squares in the same direction: nonzero fills the hole, even-odd does not.
        const string Squares = "0 0 m 20 0 l 20 20 l 0 20 l h 5 5 m 15 5 l 15 15 l 5 15 l h";
        using Raster<Rgba8> nonZero = Render(Squares + " f");
        using Raster<Rgba8> evenOdd = Render(Squares + " f*");

        Assert.Equal(new Rgba8(0, 0, 0, 255), nonZero.GetPixel(10, 10));
        Assert.Equal(White, evenOdd.GetPixel(10, 10));
        Assert.Equal(new Rgba8(0, 0, 0, 255), evenOdd.GetPixel(2, 2));
    }

    [Fact]
    public void ClipsTakeEffectAfterThePathAndEndAtRestore()
    {
        // The clipping path itself is filled in full (8.5.4), the next fill is clipped, and Q drops the clip.
        using Raster<Rgba8> raster = Render("q 0 0 10 20 re W f 1 0 0 rg 0 0 20 20 re f Q 0 0 1 rg 15 0 5 5 re f", width: 20, height: 20);

        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(5, 10));
        Assert.Equal(White, raster.GetPixel(12, 10));
        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(17, 17));
    }

    [Fact]
    public void NestedClipsIntersect()
    {
        using Raster<Rgba8> raster = Render("0 0 15 20 re W n 5 0 15 20 re W* n 0 0 20 20 re f");

        Assert.Equal(White, raster.GetPixel(2, 10));
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(10, 10));
        Assert.Equal(White, raster.GetPixel(17, 10));
    }

    [Fact]
    public void ClipsInsideFormsStayInTheForm()
    {
        PdfDictionary resources = new() { ["XObject"] = new PdfDictionary { ["F"] = Form("0 0 100 100 re f", 0, 0, 10, 10) } };
        using Raster<Rgba8> raster = Render("/F Do 1 0 0 rg 12 12 4 4 re f", resources: resources);

        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(5, 15));
        Assert.Equal(White, raster.GetPixel(15, 15));
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(14, 6));
    }

    [Fact]
    public void StrokesWithWidthCapsAndJoins()
    {
        using Raster<Rgba8> butt = Render("4 w 5 10 m 15 10 l S");
        using Raster<Rgba8> square = Render("4 w 2 J 5 10 m 15 10 l S");

        Assert.Equal(new Rgba8(0, 0, 0, 255), butt.GetPixel(10, 10));
        Assert.Equal(White, butt.GetPixel(10, 6));
        Assert.Equal(White, butt.GetPixel(3, 10));
        Assert.Equal(new Rgba8(0, 0, 0, 255), square.GetPixel(3, 10));
    }

    [Fact]
    public void ThinLinesStayOnePixelWide()
    {
        // A 0.1 point line at one pixel per point and a zero-width line both draw a visible pixel (design Q11).
        using Raster<Rgba8> raster = Render("0.1 w 0 5.5 m 20 5.5 l S 0 w 0 10.5 m 20 10.5 l S");

        Assert.True(raster.GetPixel(10, 14).R < 128);
        Assert.True(raster.GetPixel(10, 9).R < 128);
    }

    [Fact]
    public void DashesFollowTheArrayAndPhase()
    {
        using Raster<Rgba8> raster = Render("2 w [4 4] 0 d 0 10 m 20 10 l S");
        using Raster<Rgba8> odd = Render("2 w [4] 2 d 0 10 m 20 10 l S");

        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(2, 10));
        Assert.Equal(White, raster.GetPixel(6, 10));
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(10, 10));
        Assert.Equal(White, odd.GetPixel(3, 10));
        Assert.Equal(new Rgba8(0, 0, 0, 255), odd.GetPixel(1, 10));
        Assert.Equal(new Rgba8(0, 0, 0, 255), odd.GetPixel(7, 10));
    }

    [Fact]
    public void FillThenStrokeAndConstantAlpha()
    {
        PdfDictionary resources = new()
        {
            ["ExtGState"] = new PdfDictionary { ["A"] = new PdfDictionary { ["ca"] = new PdfReal(0.5) } },
        };
        using Raster<Rgba8> raster = Render("1 0 0 rg 0 0 1 RG 4 w 4 4 12 12 re B /A gs 0 g 0 0 4 4 re f", resources: resources);

        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(10, 10));
        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(4, 10));
        Rgba8 half = raster.GetPixel(1, 18);
        Assert.InRange(half.R, 120, 135);
    }

    [Fact]
    public void DegenerateMatricesAndBadOperandsDrawNothing()
    {
        using Raster<Rgba8> raster = Render("0 0 0 0 0 0 cm 0 0 10 10 re f");
        using Raster<Rgba8> bad = Render("/X rg 0 0 10 10 re f");

        Assert.Equal(White, raster.GetPixel(5, 15));
        Assert.Equal(new Rgba8(0, 0, 0, 255), bad.GetPixel(5, 15));
    }

    [Fact]
    public void AntialiasingCanBeSwitchedOff()
    {
        using Raster<Rgba8> smooth = Render("0 0 m 20 3 l 20 0 l f");
        using Raster<Rgba8> hard = Render("0 0 m 20 3 l 20 0 l f", settings: new RenderSettings { AntialiasPaths = false });

        Assert.Contains(Pixels(smooth), p => p.R is > 10 and < 245);
        Assert.DoesNotContain(Pixels(hard), p => p.R is > 10 and < 245);
    }

    /// <summary>A document to hold the streams tests put in resources, since a stream must be an indirect object.</summary>
    internal static PdfDocument Scratch { get; } = PdfDocument.Create();

    internal static PdfReference Form(string content, double left, double bottom, double right, double top, PdfDictionary? extra = null)
    {
        PdfDictionary dictionary = extra?.Clone() ?? [];
        dictionary["Subtype"] = PdfName.Get("Form");
        dictionary["BBox"] = new PdfArray { new PdfReal(left), new PdfReal(bottom), new PdfReal(right), new PdfReal(top) };
        PdfStream form = new(dictionary);
        form.SetDecodedBytes(Encoding.ASCII.GetBytes(content), compress: false);
        lock (Scratch)
        {
            return Scratch.Objects.Add(form);
        }
    }

    internal static IEnumerable<Rgba8> Pixels(Raster<Rgba8> raster)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                yield return raster.GetPixel(x, y);
            }
        }
    }

    internal static Raster<Rgba8> Render(string content, int width = 20, int height = 20, PdfDictionary? resources = null, RenderSettings? settings = null)
    {
        Raster<Rgba8> raster = Raster.Create(width, height, White);
        using (RasterCanvas<Rgba8> canvas = new(raster))
        {
            PageRenderer renderer = new(canvas, new Matrix3x2(1, 0, 0, -1, 0, height), settings ?? new RenderSettings(), new ColorCache());
            renderer.Render(PdfContent.Parse(Encoding.ASCII.GetBytes(content)), resources, null);
        }

        return raster;
    }
}
