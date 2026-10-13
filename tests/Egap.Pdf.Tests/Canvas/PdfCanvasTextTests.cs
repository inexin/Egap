using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Canvas;

/// <summary>Text, links and marked content on the PDF canvas: Phase 3 tasks 12 and 13.</summary>
public sealed class PdfCanvasTextTests
{
    private static readonly SolidPaint Black = new(new RgbaF32(0, 0, 0, 1));
    private static readonly Typeface Noto = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));
    private static readonly Typeface Source = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "SourceSans3-Latin.otf"));
    private static readonly Typeface Emoji = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "Twemoji-COLRv0-subset.ttf"));

    [Fact]
    public void EmbedsATrueTypeSubsetWithWidthsAndUnicode()
    {
        (PdfDocument pdf, string text, PdfCanvas canvas) = Draw(c => c.DrawText("Hello", new Vector2(10, 50), new Font(Noto, 20), Black));

        Assert.Empty(canvas.Warnings);
        Assert.Contains("BT\n/EgF0 1 Tf\n", text, StringComparison.Ordinal);
        Assert.Contains("TJ\nET\n", text, StringComparison.Ordinal);
        PdfDictionary type0 = Font(pdf);
        Assert.Equal(("Type0", "Identity-H"), (type0.GetName("Subtype")!.Value, type0.GetName("Encoding")!.Value));
        PdfDictionary cid = (PdfDictionary)type0.Get<PdfArray>("DescendantFonts")![0].Resolve();
        Assert.Equal(("CIDFontType2", "Identity"), (cid.GetName("Subtype")!.Value, cid.GetName("CIDToGIDMap")!.Value));
        Assert.Matches("^[A-Z]{6}\\+NotoSans-Regular$", cid.GetName("BaseFont")!.Value);
        PdfStream file = cid.Get<PdfDictionary>("FontDescriptor")!.Get<PdfStream>("FontFile2")!;
        Typeface subset = Typeface.FromMemory(file.GetDecodedBytes());
        Assert.False(subset.GetGlyphExtents(Noto.GetGlyph('H')).IsEmpty);

        // H, e, l and o: four glyphs mapped to their letters, l once.
        string cmap = Encoding.ASCII.GetString(type0.Get<PdfStream>("ToUnicode")!.GetDecodedBytes());
        Assert.Contains("4 beginbfchar", cmap, StringComparison.Ordinal);
        Assert.Contains($"<{Noto.GetGlyph('l'):X4}> <006C>", cmap, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbedsACffSubsetAsCidFontType0C()
    {
        (PdfDocument pdf, _, _) = Draw(c => c.DrawText("CFF", new Vector2(10, 50), new Font(Source, 20), Black));

        PdfDictionary cid = (PdfDictionary)Font(pdf).Get<PdfArray>("DescendantFonts")![0].Resolve();
        Assert.Equal("CIDFontType0", cid.GetName("Subtype")!.Value);
        PdfStream file = cid.Get<PdfDictionary>("FontDescriptor")!.Get<PdfStream>("FontFile3")!;
        Assert.Equal("CIDFontType0C", file.Dictionary.GetName("Subtype")!.Value);
    }

    [Fact]
    public void WritesOneFontForEveryRunOfAFaceWithGlyphsAddedLater()
    {
        (PdfDocument pdf, _, _) = Draw(c =>
        {
            c.DrawText("ab", new Vector2(10, 20), new Font(Noto, 10), Black);
            c.DrawText("cd", new Vector2(10, 40), new Font(Noto, 30), Black);
        });

        string cmap = Encoding.ASCII.GetString(Font(pdf).Get<PdfStream>("ToUnicode")!.GetDecodedBytes());
        Assert.Contains("4 beginbfchar", cmap, StringComparison.Ordinal);
    }

    [Fact]
    public void PlacesGlyphsAtTheirPositionsWithKerning()
    {
        ushort a = Noto.GetGlyph('A');
        double advance = new Font(Noto, 1000).GetAdvance(a);
        GlyphRun run = GlyphRun.Create(new Font(Noto, 10), [a, a, a], [new(0, 0), new((float)(advance / 100), 0), new((float)(advance / 100 * 2) + 1, 0)], "AAA", [0, 1, 2]);

        (_, string text, _) = Draw(c => c.DrawGlyphRun(run, new Vector2(5, 50), Black));

        // The page's y axis points up, so the text matrix flips y; the third glyph is moved 1 unit right, which TJ says as -100 thousandths of the size.
        Assert.Contains("10 0 0 -10 5 50 Tm\n", text, StringComparison.Ordinal);
        string hex = a.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"[<{hex}{hex}> -100 <{hex}>] TJ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MarksALigatureWithItsActualText()
    {
        // One glyph standing for "fi" says so through ToUnicode; two glyphs of one cluster need ActualText.
        ushort f = Noto.GetGlyph('f');
        ushort i = Noto.GetGlyph('i');
        GlyphRun run = GlyphRun.Create(new Font(Noto, 10), [f, i], [new(0, 0), new(3, 0)], "fi", [0, 0]);

        (_, string text, _) = Draw(c => c.DrawGlyphRun(run, new Vector2(5, 50), Black));

        Assert.Contains("/Span << /ActualText (fi) >> BDC\n", text, StringComparison.Ordinal);
        Assert.Contains("Q\nEMC\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MarksAGlyphThatStoodForOtherText()
    {
        ushort a = Noto.GetGlyph('a');
        (_, string text, _) = Draw(c =>
        {
            c.DrawGlyphRun(GlyphRun.Create(new Font(Noto, 10), [a], [default], "a", [0]), new Vector2(5, 20), Black);
            c.DrawGlyphRun(GlyphRun.Create(new Font(Noto, 10), [a], [default], "\u0251", [0]), new Vector2(5, 40), Black);
        });

        Assert.Single(text.Split("ActualText").Skip(1));
    }

    [Fact]
    public void StrokesSyntheticBoldAndSlantsSyntheticItalic()
    {
        Font font = new(Noto, 20) { Synthesis = FontSynthesis.Bold | FontSynthesis.Italic };
        (_, string text, _) = Draw(c => c.DrawText("B", new Vector2(10, 50), font, Black));

        Assert.Contains("2 Tr", text, StringComparison.Ordinal);
        Assert.Contains("1 j", text, StringComparison.Ordinal);
        Assert.Contains("0 0 0 RG", text, StringComparison.Ordinal);
        Assert.Matches(@"20 0 4\.98\d* -20 10 50 Tm", text);
    }

    [Fact]
    public void ClipsToTheGlyphsForAGradient()
    {
        LinearGradient gradient = new(new Vector2(0, 0), new Vector2(100, 0), [new(0, new Rgba8(255, 0, 0, 255)), new(1, new Rgba8(0, 0, 255, 255))]);
        (_, string text, PdfCanvas canvas) = Draw(c => c.DrawText("Grad", new Vector2(10, 50), new Font(Noto, 30), gradient));

        Assert.Empty(canvas.Warnings);
        Assert.Contains("7 Tr", text, StringComparison.Ordinal);
        Assert.Contains("ET\n", text, StringComparison.Ordinal);
        Assert.Contains(" sh\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DrawsColourGlyphsAsGraphicsKeepingTheirText()
    {
        (PdfDocument pdf, string text, _) = Draw(c => c.DrawText("\U0001F600", new Vector2(10, 50), new Font(Emoji, 30), Black));

        Assert.Null(pdf.Pages[0].Resources!.Get<PdfDictionary>("Font"));
        PdfOperation span = Assert.Single(PdfContent.Parse(Encoding.Latin1.GetBytes(text)), o => o.Operator == "BDC");
        Assert.Equal("\U0001F600", ((PdfDictionary)span.Operands[1]).Get<PdfString>("ActualText")!.ToText());
        Assert.Contains("h\nf\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesMarkedContentWithItsProperties()
    {
        (_, string text, _) = Draw(c =>
        {
            c.BeginMarkedContent(new MarkedContent("P") { NodeId = 3 });
            c.FillRect(new RectF(0, 0, 5, 5), Black);
            c.EndMarkedContent();
            c.BeginMarkedContent(new MarkedContent("Artifact"));
            c.EndMarkedContent();
        });

        Assert.Contains("cm\n/P << /MCID 3 >> BDC\n", text, StringComparison.Ordinal);
        Assert.Contains("/Artifact BMC\nEMC\n", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("EMC").Length - 1);
    }

    [Fact]
    public void WritesLinksAndDestinationsWherePageShowsThem()
    {
        (PdfDocument pdf, _, _) = Draw(c =>
        {
            c.DrawAnnotation(new RectF(10, 10, 40, 20), Annotation.Url("https://example.com/"));
            c.DrawAnnotation(new RectF(0, 50, 1, 1), Annotation.NamedDestination("there"));
            c.DrawAnnotation(new RectF(10, 70, 40, 20), Annotation.LinkToDestination("there"));
        });

        PdfPage page = pdf.Pages[0];
        PdfLinkAnnotation url = Assert.IsType<PdfLinkAnnotation>(page.Annotations[0]);
        Assert.Equal("https://example.com/", Assert.IsType<PdfUriAction>(url.Action).Uri);
        Assert.Equal(new PdfRectangle(10, 70, 50, 90), url.Rectangle);
        Assert.IsType<PdfGoToAction>(Assert.IsType<PdfLinkAnnotation>(page.Annotations[1]).Action);
        Assert.True(pdf.NamedDestinations.TryGet("there", out PdfDestination destination));
        Assert.Equal((0.0, 50.0), (destination.Left, destination.Top));
    }

    private static PdfDictionary Font(PdfDocument pdf)
    {
        PdfDictionary fonts = pdf.Pages[0].Resources!.Get<PdfDictionary>("Font")!;
        return (PdfDictionary)Assert.Single(fonts.Entries).Value.Resolve();
    }

    /// <summary>Draws on a 100 by 100 page and saves, so the fonts are written, and gives the reopened document.</summary>
    private static (PdfDocument Pdf, string Content, PdfCanvas Canvas) Draw(Action<Egami.Drawing.Canvas> draw)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        PdfCanvas canvas = new(page);
        draw(canvas);
        canvas.Dispose();
        using MemoryStream saved = new();
        pdf.Save(saved);
        PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        string content = Encoding.Latin1.GetString(((PdfStream)reopened.Pages[0].Dictionary["Contents"]!.Resolve()).GetDecodedBytes());
        return (reopened, content, canvas);
    }
}
