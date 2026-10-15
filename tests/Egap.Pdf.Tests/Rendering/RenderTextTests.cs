using System.Numerics;
using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Text from embedded fonts (Phase 4 task 4): glyph runs by PDF widths, the render modes and text clips.</summary>
public sealed class RenderTextTests
{
    private static readonly SolidPaint Black = new(new RgbaF32(0, 0, 0, 1));
    private static readonly Typeface Noto = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));
    private static readonly Typeface Source = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "SourceSans3-Latin.otf"));

    [Theory]
    [InlineData("ttf")]
    [InlineData("cff")]
    public void TextDrawnThroughAPdfRendersAsTheCanvasDrewIt(string face)
    {
        Typeface typeface = face == "ttf" ? Noto : Source;
        void Scene(Egami.Drawing.Canvas c)
        {
            c.DrawText("Hamburgefonstiv 0123", new Vector2(8, 30), new Font(typeface, 18), Black);
            c.Save();
            c.Rotate(-20, new Vector2(60, 80));
            c.DrawText("Turned", new Vector2(30, 80), new Font(typeface, 24), new SolidPaint(new RgbaF32(0, 0, 1, 1)));
            c.Restore();
        }

        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 240, 120));
        using (PdfCanvas canvas = new(page))
        {
            Scene(canvas);
        }

        using Raster<Rgba8> expected = Raster.Create(480, 240, new Rgba8(255, 255, 255, 255));
        using (RasterCanvas<Rgba8> canvas = new(expected))
        {
            canvas.Scale(2);
            Scene(canvas);
        }

        using Raster<Rgba8> actual = page.Render(new PdfRenderOptions { Dpi = 144 });
        if (Environment.GetEnvironmentVariable("EGAP_DEBUG_DIR") is string debug)
        {
            expected.Save(System.IO.Path.Combine(debug, face + "-expected.png"));
            actual.Save(System.IO.Path.Combine(debug, face + "-actual.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(debug, face + ".txt"), Encoding.ASCII.GetString(page.GetContent().ToBytes()));
        }

        Assert.True(Differing(expected, actual) < 0.002, $"{Differing(expected, actual):P3} of pixels differ");
        Assert.True(Ink(actual) > 0.01);
    }

    [Fact]
    public void RenderModesFillStrokeHideAndClip()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 200, 200));
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawText("I", new Vector2(-50, -50), new Font(Noto, 10), Black);
        }

        ushort glyph = Noto.GetGlyph('I');
        string show = $"<{glyph:X4}> Tj";
        string font = page.Resources!.Get<PdfDictionary>("Font")!.Keys.First().Value;
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(
            $"BT /{font} 100 Tf 1 0 0 rg 10 10 Td {show} ET " +
            $"BT /{font} 100 Tf 3 Tr 60 10 Td {show} ET " +
            $"BT /{font} 100 Tf 1 Tr 0 0 1 RG 4 w 110 10 Td {show} ET " +
            $"q BT /{font} 100 Tf 7 Tr 10 110 Td {show} ET 0 1 0 rg 0 100 200 100 re f Q 0 0 0 rg 150 150 10 10 re f")));

        using Raster<Rgba8> raster = page.Render();
        int stem = 10 + (int)Math.Round(Extents('I') * 100);

        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(stem, 150));
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(stem + 50, 150));

        // The stroke outlines the stem: blue at its edges, white in the middle of a stem wider than the stroke.
        Assert.Contains(Row(raster, 150, 110, 140), p => p.B > 200 && p.R < 50);
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(stem + 100, 150));

        // The clip lets the green rectangle show only inside the glyph; the black square after Q is not clipped.
        Assert.Equal(new Rgba8(0, 255, 0, 255), raster.GetPixel(stem, 50));
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(100, 50));
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(155, 45));
    }

    [Fact]
    public void GlyphsArePlacedByPdfWidthsNotTheFont()
    {
        // Two glyphs with widths of 1000 at size 10 sit 10 points apart however narrow the font's own glyph is.
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 40));
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawText("l", new Vector2(-50, -50), new Font(Noto, 10), Black);
        }

        pdf.WrittenResources.Finish();
        PdfDictionary type0 = (PdfDictionary)page.Resources!.Get<PdfDictionary>("Font")!.First().Value.Resolve();
        PdfDictionary cid = (PdfDictionary)type0.Get<PdfArray>("DescendantFonts")![0].Resolve();
        cid["W"] = new PdfArray { new PdfInteger(Noto.GetGlyph('l')), new PdfArray { new PdfInteger(3000) } };
        string font = page.Resources!.Get<PdfDictionary>("Font")!.Keys.First().Value;
        string l = $"{Noto.GetGlyph('l'):X4}";
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes($"BT /{font} 10 Tf 5 10 Td <{l}{l}{l}> Tj ET")));

        using Raster<Rgba8> raster = page.Render(new PdfRenderOptions { Dpi = 144 });
        int[] inked = [.. Enumerable.Range(0, raster.Width).Where(x => raster.GetPixel(x, raster.Height - 25).R < 128)];
        int[] starts = [.. inked.Where((x, i) => i == 0 || inked[i - 1] != x - 1)];

        Assert.Equal(3, starts.Length);
        Assert.InRange(starts[1] - starts[0], 59, 61);
        Assert.InRange(starts[2] - starts[1], 59, 61);
    }

    [Fact]
    public void Type3GlyphsRunTheirProceduresInTheFillColour()
    {
        PdfStream square = new(new PdfDictionary());
        square.SetDecodedBytes(Encoding.ASCII.GetBytes("1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f"), compress: false);
        PdfDictionary font = new()
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type3"),
            ["FontBBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(1000), new PdfInteger(1000) },
            ["FontMatrix"] = new PdfArray { new PdfReal(0.001), new PdfInteger(0), new PdfInteger(0), new PdfReal(0.001), new PdfInteger(0), new PdfInteger(0) },
            ["CharProcs"] = new PdfDictionary { ["sq"] = RenderCoreTests.Add(square) },
            ["Encoding"] = new PdfDictionary { ["Differences"] = new PdfArray { new PdfInteger(65), PdfName.Get("sq") } },
            ["FirstChar"] = new PdfInteger(65),
            ["LastChar"] = new PdfInteger(65),
            ["Widths"] = new PdfArray { new PdfInteger(1500) },
        };
        PdfDictionary resources = new() { ["Font"] = new PdfDictionary { ["T3"] = font } };

        using Raster<Rgba8> raster = RenderCoreTests.Render("BT /T3 4 Tf 0 0 1 rg 2 2 Td (AA) Tj ET", width: 20, height: 20, resources: resources);
        using Raster<Rgba8> invisible = RenderCoreTests.Render("BT /T3 4 Tf 3 Tr 0 0 1 rg 2 2 Td (AA) Tj ET", width: 20, height: 20, resources: resources);
        Assert.All(RenderCoreTests.Pixels(invisible), p => Assert.Equal(new Rgba8(255, 255, 255, 255), p));

        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(4, 15));
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(7, 15));
        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(10, 15));
    }

    private static double Extents(char c)
    {
        GlyphExtents extents = Noto.GetGlyphExtents(Noto.GetGlyph(c));
        return (extents.XMin + (extents.Width / 2)) / Noto.UnitsPerEm;
    }

    private static IEnumerable<Rgba8> Row(Raster<Rgba8> raster, int y, int from, int to)
    {
        for (int x = from; x < to; x++)
        {
            yield return raster.GetPixel(x, y);
        }
    }

    private static double Ink(Raster<Rgba8> raster)
    {
        return RenderCoreTests.Pixels(raster).Count(p => p.R < 128 || p.B < 128) / (double)(raster.Width * raster.Height);
    }

    /// <summary>The share of pixels that differ by more than 64 from every pixel within one of the other image, both ways.</summary>
    internal static double Differing(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int count = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                if (Far(a, b, x, y) && Far(b, a, x, y))
                {
                    count++;
                }
            }
        }

        return count / (double)(a.Width * a.Height);
    }

    private static bool Far(Raster<Rgba8> a, Raster<Rgba8> b, int x, int y)
    {
        Rgba8 p = a.GetPixel(x, y);
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)b.Width && (uint)ny < (uint)b.Height)
                {
                    Rgba8 q = b.GetPixel(nx, ny);
                    if (Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))) <= 64)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
