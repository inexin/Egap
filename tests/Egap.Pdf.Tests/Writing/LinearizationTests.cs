using System.Text;

namespace Egap.Pdf.Tests.Writing;

/// <summary>Linearized saves (Phase 5 task 19): the layout Annex F asks for, read back, and reported as such.</summary>
public sealed class LinearizationTests
{
    /// <summary>A document of three pages sharing a font, each with its own text.</summary>
    private static PdfDocument ThreePages()
    {
        PdfDocument pdf = PdfDocument.Create();
        PdfReference font = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        for (int i = 0; i < 3; i++)
        {
            PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 200, 200));
            page.Dictionary["Resources"] = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
            page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes($"BT /F1 12 Tf 20 100 Td (Page {i + 1}) Tj ET")));
        }

        pdf.Metadata.Title = "Linearized";
        return pdf;
    }

    private static byte[] Save(PdfDocument pdf, PdfSaveOptions? options = null)
    {
        using MemoryStream output = new();
        pdf.Save(output, options ?? new PdfSaveOptions { Linearize = true });
        return output.ToArray();
    }

    [Fact]
    public void ALinearizedFileStartsWithItsParameters()
    {
        using PdfDocument pdf = ThreePages();
        byte[] bytes = Save(pdf);
        string head = Encoding.Latin1.GetString(bytes, 0, 300);
        Assert.Contains("/Linearized 1", head, StringComparison.Ordinal);
        Assert.Contains($"/L {bytes.Length,10}", head, StringComparison.Ordinal);
        Assert.Contains($"/N {3,10}", head, StringComparison.Ordinal);

        using PdfDocument saved = PdfDocument.Open(bytes);
        Assert.True(saved.IsLinearized);
        Assert.Equal(3, saved.Pages.Count);
        Assert.Equal(["Page 1", "Page 2", "Page 3"], Enumerable.Range(0, 3).Select(i => saved.Pages[i].ExtractText().Trim()));
        Assert.Equal("Linearized", saved.Metadata.Title);
        Assert.DoesNotContain(saved.Log.ToArray(), w => w.Code is PdfWarningCode.MalformedXref or PdfWarningCode.XrefRebuilt);
    }

    [Fact]
    public void TheFirstPageIsNamedAndNumberedFirst()
    {
        using PdfDocument pdf = ThreePages();
        byte[] bytes = Save(pdf);
        string head = Encoding.Latin1.GetString(bytes, 0, 300);
        using PdfDocument saved = PdfDocument.Open(bytes);

        // The first page's objects take the numbers after the rest's, as qpdf numbers them; /O names the first page.
        int first = saved.Pages[0].Reference!.ObjectNumber;
        Assert.True(first > saved.Pages[1].Reference!.ObjectNumber);
        Assert.Contains($"/O {first,10}", head, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpdateEndsLinearization()
    {
        using PdfDocument pdf = ThreePages();
        using PdfDocument linearized = PdfDocument.Open(Save(pdf));
        linearized.Metadata.Title = "Changed";
        using PdfDocument updated = PdfDocument.Open(Save(linearized, PdfSaveOptions.Default));
        Assert.False(updated.IsLinearized);
        Assert.Equal("Changed", updated.Metadata.Title);
    }

    [Fact]
    public void AnOrdinaryFileIsNotLinearized()
    {
        using PdfDocument pdf = ThreePages();
        using PdfDocument saved = PdfDocument.Open(Save(pdf, PdfSaveOptions.Default));
        Assert.False(saved.IsLinearized);
    }

    [Fact]
    public void EncryptionIsRefused()
    {
        using PdfDocument pdf = ThreePages();
        Assert.Throws<PdfException>(() => Save(pdf, new PdfSaveOptions { Linearize = true, Encryption = PdfEncryption.Aes256("u", "o") }));
    }
}
