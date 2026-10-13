using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Text;

/// <summary>Glyphs, words, lines and ExtractText: Phase 3 tasks 20 and 21.</summary>
public sealed class TextExtractionTests
{
    [Fact]
    public void ExtractsLinesAndWordsInContentOrder()
    {
        PdfPage page = Page("BT /F1 12 Tf 72 700 Td (Hello world) Tj 0 -14 Td [(Ke) 120 (rn) -2000 (gap)] TJ ET");

        // A kern of 120 thousandths joins "Ke" and "rn"; a gap of two ems breaks the word.
        Assert.Equal("Hello world\nKern gap", page.ExtractText());
        PdfTextPage text = page.GetText();
        Assert.Equal(["Hello", "world"], text.Lines[0].Words.Select(w => w.Text));
        PdfTextGlyph h = text.Glyphs[0];
        Assert.Equal(("H", 12.0, 72.0), (h.Text, h.FontSize, h.Box.Left));
        Assert.Equal(700 - (0.207 * 12), h.Box.Bottom, 3);
        Assert.Equal("Helvetica", h.Font!.BaseFont);
    }

    [Fact]
    public void ReplacesActualTextSpans()
    {
        PdfPage page = Page("BT /F1 12 Tf 72 700 Td /Span << /ActualText (office) >> BDC (o) Tj (ffi) Tj (ce) Tj EMC ( now) Tj ET");

        PdfTextPage text = page.GetText();
        Assert.Equal("office now", text.ToString());
        Assert.Equal(-1, text.Glyphs[0].Code);
    }

    [Fact]
    public void SortsByPositionWhenAsked()
    {
        PdfPage page = Page("BT /F1 12 Tf 72 600 Td (second) Tj 0 100 Td (first) Tj ET BT /F1 12 Tf 200 600 Td (right) Tj ET");

        Assert.Equal("second\nfirst\nright", page.ExtractText());
        Assert.Equal("first\nsecond right", page.ExtractText(new PdfTextOptions { Order = PdfTextOrder.Position }));
    }

    [Fact]
    public void CountsGlyphsWithoutUnicode()
    {
        PdfPage page = Page("BT /F2 12 Tf 72 700 Td <0102> Tj ET", composite: true);

        PdfTextPage text = page.GetText();
        Assert.Equal(PdfWarningCode.TextWithoutUnicode, Assert.Single(text.Warnings).Code);
        Assert.All(text.Glyphs, g => Assert.InRange(g.Text[0], '\uE000', '\uF8FF'));
    }

    [Fact]
    public void ReadsBackWhatTheCanvasWrote()
    {
        Typeface face = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawText("Drawn by Egami", new Vector2(72, 100), new Font(face, 18), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
            canvas.DrawText("on two lines", new Vector2(72, 130), new Font(face, 18), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        }

        pdf.WrittenResources.Finish();
        Assert.Equal("Drawn by Egami\non two lines", page.ExtractText());
    }

    private static PdfPage Page(string content, bool composite = false)
    {
        PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfDictionary fonts = new()
        {
            ["F1"] = new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica"), ["Encoding"] = PdfName.Get("WinAnsiEncoding") },
        };
        if (composite)
        {
            fonts["F2"] = new PdfDictionary
            {
                ["Type"] = PdfName.Get("Font"),
                ["Subtype"] = PdfName.Get("Type0"),
                ["BaseFont"] = PdfName.Get("Unknown"),
                ["Encoding"] = PdfName.Get("Identity-H"),
                ["DescendantFonts"] = new PdfArray { new PdfDictionary { ["Subtype"] = PdfName.Get("CIDFontType2"), ["DW"] = new PdfInteger(600) } },
            };
        }

        page.Resources = new PdfDictionary { ["Font"] = fonts };
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(content)));
        return page;
    }
}
