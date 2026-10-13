using System.Text;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Annotations;

/// <summary>Flattening form fields and annotations (Q13): Phase 2 task 16.</summary>
public sealed class FlattenTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>
    /// One page with inherited resources and content stream 4: a text field (10) with widget 20, a check box (11) merged with
    /// its widget, a hidden text field (12), a square (30) with popup 31, a link (32), a no-view note (33) and a square
    /// without appearance (34).
    /// </summary>
    private static byte[] File()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R 11 0 R 12 0 R] /DA (/Helv 0 Tf 0 g) >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /Font << /F1 50 0 R >> >> >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R /Annots [20 0 R 11 0 R 12 0 R 30 0 R 31 0 R 32 0 R 33 0 R 34 0 R] >>")
            .Stream(4, "", "1 0 0 rg 0 0 10 10 re f"u8.ToArray())
            .Object(10, "<< /T (name) /FT /Tx /V (Hello) /Kids [20 0 R] >>")
            .Object(11, "<< /T (agree) /FT /Btn /V /On /AS /On /Subtype /Widget /Rect [100 100 120 120] /AP << /N << /On 40 0 R /Off 41 0 R >> >> /F 4 >>")
            .Object(12, "<< /T (secret) /FT /Tx /V (x) /Subtype /Widget /Rect [0 200 50 220] /F 2 /AP << /N 40 0 R >> >>")
            .Object(20, "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /Rect [10 10 110 30] /F 4 >>")
            .Object(30, "<< /Type /Annot /Subtype /Square /Rect [150 150 250 200] /F 4 /AP << /N 42 0 R >> /Popup 31 0 R >>")
            .Object(31, "<< /Type /Annot /Subtype /Popup /Rect [0 0 50 50] /Parent 30 0 R >>")
            .Object(32, "<< /Type /Annot /Subtype /Link /Rect [0 250 50 260] /A << /S /URI /URI (https://example.com) >> >>")
            .Object(33, "<< /Type /Annot /Subtype /Text /Rect [260 260 280 280] /F 32 /AP << /N 40 0 R >> >>")
            .Object(34, "<< /Type /Annot /Subtype /Square /Rect [10 260 40 290] /F 4 /C [0 0 1] >>")
            .Stream(40, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 g 0 0 20 20 re f"u8.ToArray())
            .Stream(41, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", ""u8.ToArray())
            .Stream(42, "/Type /XObject /Subtype /Form /BBox [0 0 50 25] /Matrix [2 0 0 2 0 0]", "0 0 1 rg 0 0 50 25 re f"u8.ToArray())
            .Object(50, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return file.StartXref(file.XrefTable("/Size 51 /Root 1 0 R")).ToArray();
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    private static string Content(PdfPage page)
    {
        return string.Concat(page.Dictionary.Get<PdfArray>("Contents")!.Select(c => Encoding.Latin1.GetString(((PdfStream)c.Resolve()).GetDecodedBytes())));
    }

    [Fact]
    public void FlatteningTheFormDrawsWidgetsAndRemovesTheForm()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        Assert.Equal(2, pdf.Form.Flatten());

        PdfPage page = pdf.Pages[0];
        Assert.False(pdf.Form.Exists);
        Assert.Equal(["Square", "Popup", "Link", "Text", "Square"], page.Annotations.Select(a => a.Subtype));
        string content = Content(page);
        Assert.StartsWith("q\n1 0 0 rg 0 0 10 10 re f\nQ\n", content, StringComparison.Ordinal);
        Assert.Contains("q\n1 0 0 1 10 10 cm\n/EgapFlat0 Do\nQ\n", content, StringComparison.Ordinal);
        Assert.Contains("q\n1 0 0 1 100 100 cm\n/EgapFlat1 Do\nQ\n", content, StringComparison.Ordinal);
        PdfDictionary resources = page.Dictionary.Get<PdfDictionary>("Resources")!;
        Assert.NotNull(resources.Get<PdfDictionary>("Font")!.Get("F1"));
        Assert.Equal(new PdfReference(40, 0), resources.Get<PdfDictionary>("XObject")!["EgapFlat1"]);
        Assert.Null(pdf.Pages[0].Dictionary.Get<PdfDictionary>("Parent")!.Get<PdfDictionary>("Resources")!.Get("XObject"));

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        Assert.Equal(5, reopened.Pages[0].Annotations.Count);
    }

    [Fact]
    public void FlatteningSomeFieldsKeepsTheRest()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        Assert.True(pdf.Form.TryGetField("name", out PdfField name));

        Assert.Equal(1, pdf.Form.Flatten([name]));

        Assert.Equal(["agree", "secret"], pdf.Form.Select(f => f.FullName));
        Assert.DoesNotContain(pdf.Pages[0].Annotations, a => a.Reference == new PdfReference(20, 0));
        Assert.Contains("(Hello) Tj", Encoding.Latin1.GetString(((PdfStream)pdf.Objects[(PdfReference)pdf.Pages[0].Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("XObject")!["EgapFlat0"]!]).GetDecodedBytes()), StringComparison.Ordinal);
    }

    [Fact]
    public void FlatteningAnnotationsMapsTheBoxAndLeavesLinksAndWidgets()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        Assert.Equal(3, pdf.FlattenAnnotations());

        PdfPage page = pdf.Pages[0];
        Assert.Equal(["Widget", "Widget", "Widget", "Link"], page.Annotations.Select(a => a.Subtype));
        string content = Content(page);
        // The square's box [0 0 50 25] is scaled by its matrix to 100 by 50, which is its rectangle's size already.
        Assert.Contains("q\n1 0 0 1 150 150 cm\n/EgapFlat0 Do\nQ\n", content, StringComparison.Ordinal);
        // The square Egap drew has its rectangle as its box, so it is drawn where it is.
        Assert.Contains("q\n1 0 0 1 0 0 cm\n/EgapFlat2 Do\nQ\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FiltersChooseWhatIsDrawn()
    {
        using PdfDocument print = PdfDocument.Open(File());
        Assert.Equal(2, print.FlattenAnnotations(PdfFlattenFilter.Print));
        Assert.Contains(print.Pages[0].Annotations, a => a is PdfTextAnnotation);

        using PdfDocument screen = PdfDocument.Open(File());
        Assert.Equal(2, screen.FlattenAnnotations(PdfFlattenFilter.Screen));
        Assert.Contains(screen.Pages[0].Annotations, a => a is PdfTextAnnotation);

        using PdfDocument links = PdfDocument.Open(File());
        // The link has no appearance, so it is removed without being drawn.
        Assert.Equal(3, links.FlattenAnnotations(includeLinks: true));
        Assert.DoesNotContain(links.Pages[0].Annotations, a => a is PdfLinkAnnotation);
    }

    [Fact]
    public void NeedAppearancesRedrawsTextBeforeFlattening()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        pdf.Form.NeedAppearances = true;
        Assert.True(pdf.Form.TryGetField("name", out PdfField name));
        ((PdfTextField)name).Value = "Hello";
        name.Widgets[0].Dictionary.Remove("AP");
        ((PdfTextField)name).Dictionary["V"] = PdfString.FromText("Changed");

        pdf.Form.Flatten([name]);

        PdfStream drawn = (PdfStream)pdf.Objects[(PdfReference)pdf.Pages[0].Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("XObject")!["EgapFlat0"]!];
        Assert.Contains("(Changed) Tj", Encoding.Latin1.GetString(drawn.GetDecodedBytes()), StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithoutAFormFlattensNothing()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank();

        Assert.Equal(0, pdf.Form.Flatten());
        Assert.Equal(0, pdf.FlattenAnnotations());
    }

    [Fact]
    public void ARewriteKeepsPagesFoundByRepair()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 9 0 R >>")
            .Object(3, "<< /Type /Page /MediaBox [0 0 10 10] >>")
            .Object(4, "<< /Type /Page /MediaBox [0 0 20 20] >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 5 /Root 1 0 R")).ToArray());
        Assert.Equal(2, pdf.Pages.Count);

        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });

        using PdfDocument reopened = PdfDocument.Open(output.ToArray(), Strict);
        Assert.Equal([10.0, 20.0], reopened.Pages.Select(p => p.MediaBox.Width));
    }
}
