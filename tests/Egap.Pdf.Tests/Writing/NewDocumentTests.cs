namespace Egap.Pdf.Tests.Writing;

public sealed class NewDocumentTests
{
    private static byte[] Saved(PdfDocument pdf, PdfSaveOptions? options = null)
    {
        using MemoryStream output = new();
        pdf.Save(output, options);
        return output.ToArray();
    }

    [Fact]
    public void CreateMakesAnEmptyVersion17Document()
    {
        using PdfDocument pdf = PdfDocument.Create();

        Assert.Equal("1.7", pdf.Version);
        Assert.Equal("Catalog", pdf.Catalog.GetName("Type")!.Value);
        Assert.Equal(0, pdf.Catalog.Get<PdfDictionary>("Pages")!.GetInt("Count"));
        Assert.Empty(pdf.Warnings);
        Assert.False(pdf.IsEncrypted);
        Assert.True(pdf.IsOwner);
    }

    [Fact]
    public void ANewDocumentSavesWholeAndCompact()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Trailer["Info"] = pdf.Objects.Add(new PdfDictionary { ["Title"] = PdfString.FromText("Report") });

        byte[] bytes = Saved(pdf);

        using PdfDocument saved = PdfDocument.Open(bytes, new PdfReadOptions { Strict = true });
        Assert.Equal("1.7", saved.Version);
        Assert.Equal("Report", saved.Trailer.Get<PdfDictionary>("Info")!.GetText("Title"));
        Assert.Single(saved.Xref.Sections);
        Assert.True(saved.Xref.Sections[0].IsStream);
        Assert.Contains(saved.Xref.Entries, e => e.Value.Kind == Egap.Pdf.Parsing.XrefEntryKind.InObjectStream);
        Assert.Equal(2, saved.Trailer.Get<PdfArray>("ID")!.Count);
    }

    [Fact]
    public void ANewDocumentCanSaveWithAClassicTable()
    {
        using PdfDocument pdf = PdfDocument.Create();

        using PdfDocument saved = PdfDocument.Open(Saved(pdf, new PdfSaveOptions { Compression = PdfCompression.None }), new PdfReadOptions { Strict = true });

        Assert.False(saved.Xref.Sections[0].IsStream);
        Assert.Equal(0, saved.Catalog.Get<PdfDictionary>("Pages")!.GetInt("Count"));
    }

    [Fact]
    public async Task SaveAsyncWritesWhatSaveWrites()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfSaveOptions options = new() { Deterministic = true };
        using MemoryStream output = new();

        await pdf.SaveAsync(output, options, TestContext.Current.CancellationToken);

        Assert.Equal(Saved(pdf, options), output.ToArray());
    }
}
