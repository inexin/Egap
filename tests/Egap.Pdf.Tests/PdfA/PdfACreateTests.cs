using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Tests.PdfA;

/// <summary>PDF/A for new documents (Phase 5 task 14): identification, output intent and the writer rules at save.</summary>
public sealed class PdfACreateTests
{
    private static readonly Typeface Noto = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));

    /// <summary>A page with drawn text, a sticky note and a free text annotation (whose appearance uses Helvetica).</summary>
    private static PdfDocument Made(PdfAConformance conformance)
    {
        PdfDocument pdf = PdfDocument.Create(new PdfCreateOptions { Conformance = conformance });
        pdf.Metadata.Title = "Archive";
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 300, 300));
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawText("Kept for ever", new Vector2(20, 40), new Font(Noto, 14), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        }

        page.Annotations.Add(new PdfTextAnnotation(new PdfRectangle(200, 200, 220, 220), "A note"));
        PdfFreeTextAnnotation box = new(new PdfRectangle(20, 100, 200, 140), "Free text in Helvetica");
        page.Annotations.Add(box);
        return pdf;
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1b, "1.4", "1", "B")]
    [InlineData(PdfAConformance.PdfA2b, "1.7", "2", "B")]
    [InlineData(PdfAConformance.PdfA2u, "1.7", "2", "U")]
    [InlineData(PdfAConformance.PdfA3b, "1.7", "3", "B")]
    [InlineData(PdfAConformance.PdfA3u, "1.7", "3", "U")]
    [InlineData(PdfAConformance.PdfA4, "2.0", "4", null)]
    public void EachPartIsIdentifiedAndWrittenByItsRules(PdfAConformance conformance, string version, string part, string? level)
    {
        using PdfDocument pdf = Made(conformance);
        byte[] bytes = Save(pdf);
        Assert.StartsWith("%PDF-" + version, Encoding.Latin1.GetString(bytes, 0, 10), StringComparison.Ordinal);

        using PdfDocument saved = PdfDocument.Open(bytes);
        Assert.Equal(conformance, saved.Conformance);
        Assert.Equal((part, level), (saved.Metadata.GetPdfAId().Part, saved.Metadata.GetPdfAId().Conformance));
        Assert.Equal(part == "4" ? "2020" : null, saved.Metadata.GetPdfAId().Revision);
        PdfDictionary intent = Assert.IsType<PdfDictionary>(Assert.Single(saved.Catalog.Get<PdfArray>("OutputIntents")!).Resolve());
        Assert.Equal("GTS_PDFA1", intent.GetName("S")?.Value);
        byte[] profile = intent.Get<PdfStream>("DestOutputProfile")!.GetDecodedBytes();
        Assert.Equal(2, profile[8]);
        Assert.Equal("acsp", Encoding.ASCII.GetString(profile, 36, 4));
        Assert.Equal(2, saved.Trailer.Get<PdfArray>("ID")!.Count);
        Assert.Equal("Archive", saved.Metadata.Title);

        // Every font embedded, the free text's Helvetica as a Liberation subset with a ToUnicode map.
        foreach (PdfAnnotation annotation in saved.Pages[0].Annotations)
        {
            Assert.True((annotation.Flags & PdfAnnotationFlags.Print) != 0);
            Assert.NotNull(annotation.Dictionary.Get<PdfDictionary>("AP")?.Get("N"));
        }

        PdfDictionary freeText = saved.Pages[0].Annotations.OfType<PdfFreeTextAnnotation>().Single().Dictionary;
        PdfDictionary helvetica = Assert.IsType<PdfDictionary>(ResourceFonts(freeText.Get<PdfDictionary>("AP")!.Get<PdfStream>("N")!).Single());
        Assert.Equal("TrueType", helvetica.GetName("Subtype")?.Value);
        Assert.Contains("+LiberationSans", helvetica.GetName("BaseFont")!.Value, StringComparison.Ordinal);
        Assert.NotNull(helvetica.Get<PdfDictionary>("FontDescriptor")!.Get<PdfStream>("FontFile2"));
        Assert.NotNull(helvetica.Get<PdfStream>("ToUnicode"));

        if (part == "4")
        {
            Assert.Null(saved.Trailer["Info"]);
        }
    }

    [Fact]
    public void PdfA1HasNoObjectStreamsAndListsItsCids()
    {
        using PdfDocument pdf = Made(PdfAConformance.PdfA1b);
        string text = Encoding.Latin1.GetString(Save(pdf));
        Assert.DoesNotContain("/ObjStm", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/XRef", text, StringComparison.Ordinal);
        Assert.Contains("\nxref\n", text, StringComparison.Ordinal);
        Assert.Contains("/CIDSet", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertingToPdfA1ListsTheCidsOfSubsets()
    {
        using PdfDocument plain = PdfDocument.Create();
        PdfPage page = plain.Pages.AddBlank(new PdfRectangle(0, 0, 300, 300));
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawText("Kept for ever", new Vector2(20, 40), new Font(Noto, 14), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        }

        using PdfDocument pdf = PdfDocument.Open(Save(plain));
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA1b);
        Assert.True(report.Succeeded, report.ToString());
        Assert.Contains(report.Issues, i => i.Clause == "6.3.5" && i.Fixed);

        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        PdfDictionary fonts = saved.Pages[0].Resources!.Get<PdfDictionary>("Font")!;
        PdfDictionary font = fonts.Get<PdfDictionary>(Assert.Single(fonts.Keys))!;
        PdfDictionary descriptor = font.Get<PdfArray>("DescendantFonts")!.Get<PdfDictionary>(0)!.Get<PdfDictionary>("FontDescriptor")!;
        byte[] bits = descriptor.Get<PdfStream>("CIDSet")!.GetDecodedBytes();
        Assert.NotEqual(0, bits[0] & 0x80);
        foreach (char c in "Kept for ever")
        {
            ushort glyph = Noto.GetGlyph(c);
            Assert.NotEqual(0, bits[glyph / 8] & (0x80 >> (glyph % 8)));
        }

        Assert.True(PdfAConverter.Check(saved, PdfAConformance.PdfA1b).Succeeded);
    }

    [Fact]
    public void APlainDocumentClaimsNothing()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        Assert.Equal(PdfAConformance.None, saved.Conformance);
        Assert.Null(saved.Catalog["OutputIntents"]);
    }

    [Fact]
    public void EncryptionIsRefused()
    {
        using PdfDocument pdf = Made(PdfAConformance.PdfA2b);
        using MemoryStream output = new();
        PdfException e = Assert.Throws<PdfException>(() => pdf.Save(output, new PdfSaveOptions { Encryption = PdfEncryption.Aes256("u", "o") }));
        Assert.Contains("encrypted", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransparencyIsRefusedInPdfA1AndAllowedLater()
    {
        foreach (PdfAConformance conformance in (PdfAConformance[])[PdfAConformance.PdfA1b, PdfAConformance.PdfA2b])
        {
            using PdfDocument pdf = PdfDocument.Create(new PdfCreateOptions { Conformance = conformance });
            PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
            using (PdfCanvas canvas = new(page))
            {
                canvas.FillRect(new RectF(10, 10, 50, 50), new SolidPaint(new RgbaF32(1, 0, 0, 0.5f)));
            }

            if (conformance == PdfAConformance.PdfA1b)
            {
                PdfException e = Assert.Throws<PdfException>(() => Save(pdf));
                Assert.Contains("PDF/A-1b", e.Message, StringComparison.Ordinal);
                Assert.Contains("transparency", e.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.NotEmpty(Save(pdf));
            }
        }
    }

    [Fact]
    public void AttachmentsFollowThePart()
    {
        using (PdfDocument pdf = Made(PdfAConformance.PdfA2b))
        {
            pdf.Attachments.Add("data.csv", "a,b\n1,2\n"u8);
            Assert.Contains("not a PDF/A file", Assert.Throws<PdfException>(() => Save(pdf)).Message, StringComparison.Ordinal);
        }

        using (PdfDocument pdf = Made(PdfAConformance.PdfA3b))
        {
            pdf.Attachments.Add("data.csv", "a,b\n1,2\n"u8);
            using PdfDocument saved = PdfDocument.Open(Save(pdf));
            PdfAttachment attachment = Assert.Single(saved.Attachments);
            Assert.Equal("Unspecified", attachment.Relationship);
            Assert.Equal("application/octet-stream", attachment.MimeType);
            Assert.Single(saved.Catalog.Get<PdfArray>("AF")!);
        }

        using (PdfDocument pdf = Made(PdfAConformance.PdfA4f))
        {
            Assert.Contains("at least one attachment", Assert.Throws<PdfException>(() => Save(pdf)).Message, StringComparison.Ordinal);
        }

        // A PDF/A file may go inside PDF/A-2.
        byte[] inner = Save(Made(PdfAConformance.PdfA2b));
        using (PdfDocument pdf = Made(PdfAConformance.PdfA2b))
        {
            pdf.Attachments.Add("inner.pdf", inner, "application/pdf");
            Assert.NotEmpty(Save(pdf));
        }
    }

    [Fact]
    public void JavaScriptIsRefused()
    {
        using PdfDocument pdf = Made(PdfAConformance.PdfA2b);
        pdf.Catalog["OpenAction"] = new PdfDictionary { ["S"] = PdfName.Get("JavaScript"), ["JS"] = PdfString.FromText("app.alert(1)") };
        Assert.Contains("JavaScript action", Assert.Throws<PdfException>(() => Save(pdf)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenAnnotationsArePrintedAndShown()
    {
        using PdfDocument pdf = Made(PdfAConformance.PdfA2b);
        PdfAnnotation note = pdf.Pages[0].Annotations.OfType<PdfTextAnnotation>().Single();
        note.Dictionary["F"] = new PdfInteger((int)PdfAnnotationFlags.Hidden);
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        Assert.Equal(PdfAnnotationFlags.Print, saved.Pages[0].Annotations.OfType<PdfTextAnnotation>().Single().Flags);
    }

    [Fact]
    public void SymbolFontsAreEmbeddedFromFoxit()
    {
        using PdfDocument pdf = PdfDocument.Create(new PdfCreateOptions { Conformance = PdfAConformance.PdfA2b });
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        page.Dictionary["Resources"] = new PdfDictionary
        {
            ["Font"] = new PdfDictionary { ["F1"] = new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Symbol") } },
        };
        page.SetContent(PdfContent.Parse("BT /F1 12 Tf 10 50 Td (abg) Tj ET"u8));
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        PdfDictionary font = saved.Pages[0].Resources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("F1")!;
        PdfStream file = font.Get<PdfDictionary>("FontDescriptor")!.Get<PdfStream>("FontFile3")!;
        Assert.Equal("Type1C", file.Dictionary.GetName("Subtype")?.Value);
        Assert.Contains("\u03b1", saved.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1b)]
    [InlineData(PdfAConformance.PdfA2u)]
    [InlineData(PdfAConformance.PdfA3b)]
    [InlineData(PdfAConformance.PdfA4)]
    public void CreatedFilesPassThePreCheck(PdfAConformance conformance)
    {
        using PdfDocument pdf = Made(conformance);
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        PdfAReport report = PdfAConverter.Check(saved, conformance);
        Assert.True(report.Succeeded, report.ToString());
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void ThePreCheckOfAPlainFileSaysWhatConversionWouldFix()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        PdfAReport report = PdfAConverter.Check(saved, PdfAConformance.PdfA2b);
        Assert.False(report.Succeeded);
        Assert.Contains(report.Issues, i => i.Clause == "6.2.3" && i.Fixable && !i.Fixed);
        Assert.Contains(report.Issues, i => i.Clause == "6.6.4" && i.Fixable);
        Assert.False(saved.IsChanged);
    }

    private static IEnumerable<PdfObject> ResourceFonts(PdfStream form)
    {
        return form.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Font")!.Entries.Select(e => e.Value.Resolve()!);
    }
}
