using System.Numerics;
using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Redaction;

/// <summary>
/// Redaction of text and paths (Phase 5 task 11): covered glyphs removed from their operators with the rest left in place,
/// paths by the option, shadings and pattern fills clipped, forms redacted in copies, and the save forced to a rewrite.
/// </summary>
public sealed class RedactionTextTests
{
    [Theory]
    [InlineData("BT /F1 12 Tf 72 700 Td (Secret 12345 visible) Tj ET")]
    [InlineData("BT /F1 12 Tf 72 700 Td [(Secret ) -50 (12345) 30 ( visible)] TJ ET")]
    [InlineData("BT /F1 12 Tf 14 TL 72 714 Td (Secret 12345 visible) ' ET")]
    [InlineData("BT /F1 12 Tf 14 TL 72 714 Td 2 0.5 (Secret 12345 visible) \" ET")]
    [InlineData("BT /F1 12 Tf 72 700 Td <5365637265742031323334352076697369626c65> Tj ET")]
    public void CoveredGlyphsGoAndTheRestStayInPlace(string content)
    {
        using PdfDocument pdf = Page(content);
        List<PdfTextGlyph> before = [.. pdf.Pages[0].GetText().Glyphs];
        PdfRectangle area = Around(before, "12345");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(area));

        PdfRedactionResult result = pdf.ApplyRedactions();
        Assert.Equal(1, result.Annotations);
        Assert.Equal(5, result.Glyphs);
        using PdfDocument saved = SaveAndOpen(pdf);
        string text = saved.Pages[0].ExtractText();
        Assert.DoesNotContain("12345", text, StringComparison.Ordinal);
        Assert.Contains("Secret", text, StringComparison.Ordinal);
        Assert.Contains("visible", text, StringComparison.Ordinal);
        Assert.Empty(saved.Pages[0].Annotations);

        // Every kept glyph is where it was.
        List<PdfTextGlyph> after = [.. saved.Pages[0].GetText().Glyphs.Where(g => !g.IsSpace)];
        List<PdfTextGlyph> expected = [.. before.Where(g => !g.IsSpace && !ContentRedactorTestsOverlap(area, g.Box))];
        Assert.Equal(expected.Select(g => g.Text), after.Select(g => g.Text));
        foreach ((PdfTextGlyph a, PdfTextGlyph b) in expected.Zip(after))
        {
            Assert.Equal(a.Origin.X, b.Origin.X, 3);
            Assert.Equal(a.Origin.Y, b.Origin.Y, 3);
        }

        Assert.False(StreamsContain(saved, "12345"));
    }

    [Theory]
    [InlineData(0.1, 0)]
    [InlineData(0.0, 20)]
    public void GlyphsTheAreaBarelyTouchesStayByDefault(double overlap, int removed)
    {
        // Without a descriptor a glyph box runs from 0.2 below the baseline to 0.8 above it, here 697.6 to 709.6; the area
        // overlaps its top 0.5 (about 4%), as a large title's descent does the line below.
        using PdfDocument pdf = Page("BT /F1 12 Tf 72 700 Td (Line below the title) Tj ET");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(60, 709.1, 400, 750)));

        Assert.Equal(removed, pdf.ApplyRedactions(new PdfRedactionOptions { GlyphOverlap = overlap }).Glyphs);
    }

    [Fact]
    public void GlyphOverlapOutsideZeroToOneThrows()
    {
        using PdfDocument pdf = Page("BT /F1 12 Tf 72 700 Td (Text) Tj ET");
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.ApplyRedactions(new PdfRedactionOptions { GlyphOverlap = 1.5 }));
    }

    [Fact]
    public void TwoByteTextFromTheCanvasIsRedacted()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 400, 200));
        using (PdfCanvas canvas = new(page))
        {
            Egami.Fonts.Typeface face = Egami.Fonts.Substitutes.SubstituteFonts.Find("Helvetica", Egami.Fonts.FontStyle.Normal)!;
            canvas.DrawText("Account 987654 closed", new Vector2(20, 100), new Egami.Fonts.Font(face, 14), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        }

        using PdfDocument reopened = SaveAndOpen(pdf);
        PdfRectangle area = Around([.. reopened.Pages[0].GetText().Glyphs], "987654");
        reopened.Pages[0].Annotations.Add(new PdfRedactAnnotation(area));
        Assert.Equal(6, reopened.ApplyRedactions().Glyphs);
        using PdfDocument saved = SaveAndOpen(reopened);
        string text = saved.Pages[0].ExtractText();
        Assert.Contains("Account", text, StringComparison.Ordinal);
        Assert.Contains("closed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("987654", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfRedactPaths.Covered, 1)]
    [InlineData(PdfRedactPaths.Touched, 2)]
    [InlineData(PdfRedactPaths.None, 0)]
    public void PathsGoByTheOption(PdfRedactPaths option, int removed)
    {
        // One rectangle inside the area, one half in it, one far away.
        using PdfDocument pdf = Page("0 0 1 rg 110 110 20 20 re f 1 0 0 rg 180 110 60 20 re f 0 1 0 rg 300 300 20 20 re f");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 200, 140)));
        Assert.Equal(removed, pdf.ApplyRedactions(new PdfRedactionOptions { Paths = option }).Paths);
        using PdfDocument saved = SaveAndOpen(pdf);
        string content = Encoding.Latin1.GetString(saved.Pages[0].GetContent().ToBytes());
        Assert.Equal(option == PdfRedactPaths.None, content.Contains("110 110 20 20 re", StringComparison.Ordinal));
        Assert.Equal(option != PdfRedactPaths.Touched, content.Contains("180 110 60 20 re", StringComparison.Ordinal));
        Assert.Contains("300 300 20 20 re", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ACoveredClipKeepsItsClip()
    {
        using PdfDocument pdf = Page("q 110 110 20 20 re W f 0 0 1 rg 0 0 400 400 re f Q");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 200, 140)));
        pdf.ApplyRedactions();
        string content = Encoding.Latin1.GetString(pdf.Pages[0].GetContent().ToBytes());
        Assert.Contains("W", content, StringComparison.Ordinal);
        Assert.Contains("0 0 400 400 re", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AShadingIsClippedToLeaveTheAreaOut()
    {
        const string shading = "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 400 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>";
        using PdfDocument pdf = Page("/Sh0 sh", resources: $"<< /Shading << /Sh0 {shading} >> >>");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(100, 100, 200, 200)));
        pdf.ApplyRedactions();
        using PdfDocument saved = SaveAndOpen(pdf);
        using Raster<Rgba8> raster = saved.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
        // The render is y down: the area's middle is at 150, 842 - 150.
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(150, 692));
        Assert.NotEqual(new Rgba8(255, 255, 255, 255), raster.GetPixel(50, 692));
    }

    [Fact]
    public void AFormIsRedactedInACopyAndOtherPagesKeepTheOriginal()
    {
        byte[] file = TwoPagesSharingAForm();
        using PdfDocument pdf = PdfDocument.Open(file);
        PdfRectangle area = Around([.. pdf.Pages[0].GetText().Glyphs], "Hidden");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(area));
        PdfRedactionResult result = pdf.ApplyRedactions();
        Assert.Equal(1, result.Forms);
        using PdfDocument saved = SaveAndOpen(pdf);
        Assert.DoesNotContain("Hidden", saved.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Contains("Kept", saved.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Contains("Hidden", saved.Pages[1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void QuadPointsMarkTheAreas()
    {
        using PdfDocument pdf = Page("BT /F1 12 Tf 72 700 Td (Secret 12345 visible) Tj ET");
        PdfRectangle area = Around([.. pdf.Pages[0].GetText().Glyphs], "12345");
        PdfRedactAnnotation redaction = new(new PdfRectangle(0, 0, 600, 800))
        {
            QuadPoints = [new PdfPoint(area.Left, area.Top), new PdfPoint(area.Right, area.Top), new PdfPoint(area.Left, area.Bottom), new PdfPoint(area.Right, area.Bottom)],
        };
        pdf.Pages[0].Annotations.Add(redaction);
        Assert.Equal(5, pdf.ApplyRedactions().Glyphs);
    }

    [Fact]
    public void ARedactedDocumentSavesWhole()
    {
        byte[] original;
        using (PdfDocument created = Page("BT /F1 12 Tf 72 700 Td (Secret 12345 visible) Tj ET"))
        using (MemoryStream output = new())
        {
            created.Save(output);
            original = output.ToArray();
        }

        // An incremental update first, so the original file has an earlier revision that holds the secret.
        byte[] updated;
        using (PdfDocument pdf = PdfDocument.Open(original))
        using (MemoryStream output = new())
        {
            pdf.Catalog["Lang"] = PdfString.FromText("en");
            pdf.Save(output);
            updated = output.ToArray();
        }

        using PdfDocument redacting = PdfDocument.Open(updated);
        Assert.Equal(2, redacting.Revisions.Count);
        redacting.Pages[0].Annotations.Add(new PdfRedactAnnotation(Around([.. redacting.Pages[0].GetText().Glyphs], "12345")));
        redacting.ApplyRedactions();
        using MemoryStream saved = new();
        redacting.Save(saved, new PdfSaveOptions { Mode = PdfSaveMode.Incremental, RemoveUnused = false });
        using PdfDocument result = PdfDocument.Open(saved.ToArray());
        Assert.Single(result.Revisions);
        Assert.False(StreamsContain(result, "12345"));
        Assert.DoesNotContain("12345", Encoding.Latin1.GetString(saved.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Type3GlyphsFollowTheGlyphRule()
    {
        const string type3 = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 9 0 R >> /Encoding << /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] >>";
        using PdfDocument pdf = Page("BT /T3 20 Tf 100 100 Td (aaaa) Tj ET", resources: $"<< /Font << /T3 {type3} >> >>", extra: "9 0 obj\n<< /Length 33 >>\nstream\n1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f\nendstream\nendobj\n");
        pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(120, 100, 140, 120)));
        Assert.Equal(1, pdf.ApplyRedactions().Glyphs);
    }

    /// <summary>A one-page document with the given content, a Helvetica /F1 unless the resources say otherwise.</summary>
    internal static PdfDocument Page(string content, string? resources = null, string extra = "")
    {
        resources ??= "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >>";
        StringBuilder file = new("%PDF-1.7\n");
        List<int> offsets = [];
        void Add(string body)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(file.ToString()));
            file.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources {resources} >>");
        Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        for (int i = offsets.Count; i < 8; i++)
        {
            Add("null");
        }

        if (extra.Length > 0)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(file.ToString()));
            file.Append(extra);
        }

        int xref = Encoding.Latin1.GetByteCount(file.ToString());
        file.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            file.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        file.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(file.ToString()));
    }

    internal static PdfDocument SaveAndOpen(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>The box around the glyphs of a word, a little inside them so neighbours are not touched.</summary>
    internal static PdfRectangle Around(IReadOnlyList<PdfTextGlyph> glyphs, string word)
    {
        string text = string.Concat(glyphs.Select(g => g.Text));
        int at = text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{word}' not in '{text}'");
        List<PdfTextGlyph> hit = [.. glyphs.Skip(at).Take(word.Length)];
        return new PdfRectangle(hit.Min(g => g.Box.Left) + 0.5, hit.Min(g => g.Box.Bottom) + 0.5, hit.Max(g => g.Box.Right) - 0.5, hit.Max(g => g.Box.Top) - 0.5);
    }

    internal static bool ContentRedactorTestsOverlap(PdfRectangle a, PdfRectangle b)
    {
        return a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;
    }

    /// <summary>Whether any stream of the file, decoded, holds the text.</summary>
    internal static bool StreamsContain(PdfDocument pdf, string text)
    {
        foreach (PdfReference reference in pdf.Objects)
        {
            if (pdf.Objects[reference] is PdfStream stream)
            {
                try
                {
                    if (Encoding.Latin1.GetString(stream.GetDecodedBytes()).Contains(text, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                catch (PdfException)
                {
                }
            }
        }

        return false;
    }

    private static byte[] TwoPagesSharingAForm()
    {
        const string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        const string form = "BT /F1 12 Tf 10 10 Td (Hidden) Tj 0 20 Td (Kept) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 5 0 R /Resources << /XObject << /Fm 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 5 0 R /Resources << /XObject << /Fm 6 0 R >> >> >>",
            "<< /Length 23 >>\nstream\nq 1 0 0 1 50 50 cm /Fm Do Q\nendstream",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 100] /Resources << /Font << /F1 {font} >> >> /Length {form.Length} >>\nstream\n{form}\nendstream",
        ];
        StringBuilder file = new("%PDF-1.7\n");
        List<int> offsets = [];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(file.Length);
            file.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = file.Length;
        file.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            file.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        file.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(file.ToString());
    }
}
