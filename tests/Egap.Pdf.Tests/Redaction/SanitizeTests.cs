using System.Globalization;
using System.Text;

namespace Egap.Pdf.Tests.Redaction;

/// <summary>Sanitising (Phase 5 task 13): every switch of design Q10, all on by default, and the forced rewrite.</summary>
public sealed class SanitizeTests
{
    /// <summary>A page with something of every kind sanitising removes, and a go-to link and visible text it keeps.</summary>
    private static byte[] Hiding()
    {
        string[] objects =
        [
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R /Metadata 8 0 R /OpenAction << /S /JavaScript /JS (app.alert(1)) >> /AA << /WC << /S /JavaScript /JS (x) >> >>"
                + " /Names << /JavaScript << /Names [(a) << /S /JavaScript /JS (y) >>] >> /EmbeddedFiles << /Names [(secret.txt) 10 0 R] >> >>"
                + " /AcroForm << /Fields [12 0 R] >> /OCProperties << /OCGs [13 0 R] /D << /OFF [13 0 R] >> >> /PieceInfo << /App << /Private (data) >> >> >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Thumb 9 0 R /PieceInfo << /App << /P (x) >> >> /AA << /O << /S /JavaScript /JS (z) >> >>"
                + " /Resources << /Font << /F1 5 0 R >> /Properties << /Hid 13 0 R >> >> /Annots [6 0 R 7 0 R 12 0 R 14 0 R 15 0 R] >>",
            /* 4 */ Stream("BT /F1 12 Tf 72 700 Td (Visible text) Tj ET /OC /Hid BDC BT /F1 12 Tf 72 600 Td (Hidden layer text) Tj ET EMC"),
            /* 5 */ "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            /* 6 */ "<< /Type /Annot /Subtype /Link /Rect [72 690 150 712] /A << /S /URI /URI (http://tracker.example) >> >>",
            /* 7 */ "<< /Type /Annot /Subtype /Link /Rect [72 650 150 672] /A << /S /GoTo /D [3 0 R /Fit] >> >>",
            /* 8 */ "<< /Type /Metadata /Subtype /XML /Length 20 >>\nstream\n<x:xmpmeta></x:xmpmeta>\nendstream",
            /* 9 */ "<< /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\x80\nendstream",
            /* 10 */ "<< /Type /Filespec /F (secret.txt) /UF (secret.txt) /EF << /F 11 0 R >> >>",
            /* 11 */ "<< /Type /EmbeddedFile /Length 17 >>\nstream\nattachment-secret\nendstream",
            /* 12 */ "<< /FT /Tx /T (Name) /V (Ada Lovelace) /DV () /Type /Annot /Subtype /Widget /Rect [72 500 300 524] /P 3 0 R /AA << /K << /S /JavaScript /JS (k) >> >> >>",
            /* 13 */ "<< /Type /OCG /Name (Hidden) >>",
            /* 14 */ "<< /Type /Annot /Subtype /Text /Rect [400 700 420 720] /Contents (A private comment) >>",
            /* 15 */ "<< /Type /Annot /Subtype /FileAttachment /Rect [400 600 420 620] /FS 10 0 R >>",
        ];
        StringBuilder file = new("%PDF-1.7\n");
        List<int> offsets = [];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(file.ToString()));
            file.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = Encoding.Latin1.GetByteCount(file.ToString());
        file.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            file.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        file.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info << /Author (Someone) /Title (Private) >> >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(file.ToString());

        static string Stream(string content)
        {
            return $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
        }
    }

    [Fact]
    public void EverythingHiddenGoes()
    {
        using PdfDocument pdf = PdfDocument.Open(Hiding());
        PdfSanitizeResult result = pdf.Sanitize();
        Assert.True(result.Metadata >= 2);
        Assert.True(result.Actions >= 6);
        Assert.True(result.Attachments >= 2);
        Assert.Equal(1, result.Layers);
        Assert.Equal(1, result.Comments);
        Assert.Equal(1, result.Fields);
        Assert.Equal(1, result.Thumbnails);
        Assert.Equal(2, result.PrivateData);

        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        PdfDictionary catalog = saved.Catalog;
        Assert.Null(saved.Trailer["Info"]);
        foreach (string key in (string[])["Metadata", "OpenAction", "AA", "PieceInfo"])
        {
            Assert.False(catalog.ContainsKey(key), key);
        }

        Assert.Null(catalog.Get<PdfDictionary>("Names")?.Get("JavaScript"));
        Assert.Empty(saved.Attachments);
        PdfPage page = saved.Pages[0];
        Assert.False(page.Dictionary.ContainsKey("Thumb"));
        Assert.False(page.Dictionary.ContainsKey("AA"));
        Assert.Empty(saved.Layers);

        string text = page.ExtractText();
        Assert.Contains("Visible text", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden layer text", text, StringComparison.Ordinal);

        // The go-to link keeps its action, the web link loses its; the comment and the attachment annotation go.
        List<PdfLinkAnnotation> links = [.. page.Annotations.OfType<PdfLinkAnnotation>()];
        Assert.Equal(2, links.Count);
        Assert.Single(links, l => l.Dictionary.Get<PdfDictionary>("A")?.GetName("S")?.Value == "GoTo");
        Assert.DoesNotContain(page.Annotations, a => a is PdfTextAnnotation or PdfFileAttachmentAnnotation);

        PdfTextField field = Assert.IsType<PdfTextField>(Assert.Single(saved.Form.GetAllFields()));
        Assert.True(string.IsNullOrEmpty(field.Value));
        Assert.False(field.Dictionary.ContainsKey("AA"));

        Assert.False(RedactionTextTests.StreamsContain(saved, "attachment-secret"));
        Assert.False(RedactionTextTests.StreamsContain(saved, "Hidden layer text"));
        Assert.DoesNotContain("Ada Lovelace", Encoding.Latin1.GetString(Save(pdf)), StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchesKeepWhatTheyAreOffFor()
    {
        using PdfDocument pdf = PdfDocument.Open(Hiding());
        PdfSanitizeResult result = pdf.Sanitize(new PdfSanitizeOptions { Metadata = false, Comments = false, FormData = false, Attachments = false });
        Assert.Equal(0, result.Metadata + result.Comments + result.Fields + result.Attachments);
        using PdfDocument saved = RedactionTextTests.SaveAndOpen(pdf);
        Assert.NotNull(saved.Trailer["Info"]);
        Assert.Contains(saved.Pages[0].Annotations, a => a is PdfTextAnnotation);
        Assert.Equal("Ada Lovelace", ((PdfTextField)saved.Form.GetAllFields()[0]).Value);
        Assert.Single(saved.Attachments);
    }

    [Fact]
    public void ASanitizedFileHasOneRevision()
    {
        byte[] updated;
        using (PdfDocument pdf = PdfDocument.Open(Hiding()))
        using (MemoryStream output = new())
        {
            pdf.Catalog["Lang"] = PdfString.FromText("en");
            pdf.Save(output);
            updated = output.ToArray();
        }

        using PdfDocument sanitizing = PdfDocument.Open(updated);
        Assert.Equal(2, sanitizing.Revisions.Count);
        sanitizing.Sanitize();
        using PdfDocument result = PdfDocument.Open(Save(sanitizing));
        Assert.Single(result.Revisions);
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }
}
