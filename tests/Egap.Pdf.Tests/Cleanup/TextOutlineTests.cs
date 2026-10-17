using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Cleanup;

/// <summary>Text to outlines: Phase 7 task 19.</summary>
public sealed class TextOutlineTests
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "files", "fonts");

    [Fact]
    public void TextBecomesOutlinesThatRenderTheSame()
    {
        using PdfDocument pdf = Sample();
        PdfPage page = pdf.Pages[0];
        Assert.NotEmpty(page.ExtractText().Trim());
        using Raster<Rgba8> before = page.Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);

        int glyphs = page.ConvertTextToOutlines();

        Assert.True(glyphs > 40, $"{glyphs} glyphs");
        Assert.Empty(page.ExtractText().Trim());
        using Raster<Rgba8> after = page.Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        int differing = 0;
        for (int y = 0; y < before.Height; y++)
        {
            for (int x = 0; x < before.Width; x++)
            {
                Rgba8 p = before.GetPixel(x, y), q = after.GetPixel(x, y);
                differing += Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))) > 64 ? 1 : 0;
            }
        }

        Assert.True(differing < before.Width * before.Height / 2000, $"{differing} pixels differ clearly");
        Assert.DoesNotContain(page.GetContent(), op => op.Operator is "Tj" or "TJ" or "BT");
    }

    [Fact]
    public void APageWithoutTextIsUnchanged()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.SetContent(PdfContent.Parse("0 g 10 10 100 100 re f"u8));
        Assert.Equal(0, page.ConvertTextToOutlines());
        Assert.Equal("0 g 10 10 100 100 re f", System.Text.Encoding.ASCII.GetString(page.GetContent().ToBytes()).ReplaceLineEndings(" ").Trim());
    }

    /// <summary>
    /// Helvetica (a substitute) filled, stroked and both, red text in an embedded TrueType font inside a form, a Type 3 glyph,
    /// and text used as a clip for a blue fill.
    /// </summary>
    private static PdfDocument Sample()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference helvetica = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        byte[] ttf = File.ReadAllBytes(Path.Combine(Fonts, "NotoSans-Latin.ttf"));
        PdfStream file = new(new PdfDictionary { ["Length1"] = new PdfInteger(ttf.Length) });
        file.SetDecodedBytes(ttf);
        PdfReference noto = pdf.Objects.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("TrueType"),
            ["BaseFont"] = PdfName.Get("NotoSans"),
            ["FirstChar"] = new PdfInteger(32),
            ["LastChar"] = new PdfInteger(126),
            ["Widths"] = new PdfArray([.. Enumerable.Range(32, 95).Select(_ => (PdfObject)new PdfInteger(560))]),
            ["Encoding"] = PdfName.Get("WinAnsiEncoding"),
            ["FontDescriptor"] = new PdfDictionary { ["Type"] = PdfName.Get("FontDescriptor"), ["FontName"] = PdfName.Get("NotoSans"), ["Flags"] = new PdfInteger(32), ["FontFile2"] = pdf.Objects.Add(file) },
        });
        PdfStream square = new(new PdfDictionary());
        square.SetDecodedBytes("1000 0 d0 0 0 800 800 re f"u8);
        PdfReference type3 = pdf.Objects.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type3"),
            ["FontBBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(1000), new PdfInteger(1000) },
            ["FontMatrix"] = new PdfArray { new PdfReal(0.001), new PdfInteger(0), new PdfInteger(0), new PdfReal(0.001), new PdfInteger(0), new PdfInteger(0) },
            ["CharProcs"] = new PdfDictionary { ["sq"] = pdf.Objects.Add(square) },
            ["Encoding"] = new PdfDictionary { ["Type"] = PdfName.Get("Encoding"), ["Differences"] = new PdfArray { new PdfInteger(65), PdfName.Get("sq") } },
            ["FirstChar"] = new PdfInteger(65),
            ["LastChar"] = new PdfInteger(65),
            ["Widths"] = new PdfArray { new PdfInteger(1000) },
            ["Resources"] = new PdfDictionary(),
        });
        PdfStream form = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(612), new PdfInteger(792) },
            ["Resources"] = new PdfDictionary { ["Font"] = new PdfDictionary { ["F2"] = noto } },
        });
        form.SetDecodedBytes("BT 1 0 0 rg /F2 20 Tf 72 500 Td (Embedded text in a form) Tj ET"u8);
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary
        {
            ["Font"] = new PdfDictionary { ["F1"] = helvetica, ["F3"] = type3 },
            ["XObject"] = new PdfDictionary { ["Fm"] = pdf.Objects.Add(form) },
        };
        page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes(
            "BT /F1 24 Tf 72 700 Td (Filled text) Tj 0 -30 Td 1 Tr 1 w (Stroked text) Tj 0 -30 Td 2 Tr [(Both) -250 (ways)] TJ ET "
            + "/Fm Do BT /F3 30 Tf 72 400 Td 0 Tr (AAA) Tj ET "
            + "q BT 7 Tr /F1 60 Tf 72 300 Td (CLIP) Tj ET 0 0 1 rg 0 0 612 792 re f Q")));
        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }
}
