using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Layers;

/// <summary>Optional content groups (8.11): listing, visibility, lock, rename and removal; Phase 2 task 10.</summary>
public sealed class LayerTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>
    /// Layers 10 (Text, off by default and locked), 11 (Images, design intent) and 12 (Notes) in a radio group with 10; an
    /// alternate configuration turns 11 off; a usage application names 10.
    /// </summary>
    private static byte[] File(string baseState = "/ON")
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [10 0 R 11 0 R 12 0 R 10 0 R]"
                + $" /D << /BaseState {baseState} /OFF [10 0 R] /Order [10 0 R [(Group) 11 0 R 12 0 R]] /Locked [10 0 R] /RBGroups [[10 0 R 12 0 R]]"
                + " /AS [<< /Event /View /Category [/Zoom] /OCGs [10 0 R 11 0 R] >>] >> /Configs [<< /OFF [11 0 R] /Order [11 0 R] >>] >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>")
            .Object(10, "<< /Type /OCG /Name (Text) >>")
            .Object(11, "<< /Type /OCG /Name (Images) /Intent [/View /Design] >>")
            .Object(12, "<< /Type /OCG /Name <FEFF004E00F80074> /Intent /View >>");
        return file.StartXref(file.XrefTable("/Size 13 /Root 1 0 R")).ToArray();
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void ListsLayersOnceWithTheirState()
    {
        using PdfDocument pdf = PdfDocument.Open(File(), Strict);

        Assert.Equal(["Text", "Images", "Nøt"], pdf.Layers.Select(l => l.Name));
        Assert.Equal((false, true, true), (pdf.Layers[0].IsVisible, pdf.Layers[1].IsVisible, pdf.Layers[2].IsVisible));
        Assert.Equal((true, false), (pdf.Layers[0].IsLocked, pdf.Layers[1].IsLocked));
        Assert.Equal(["View"], pdf.Layers[0].Intents);
        Assert.Equal(["View", "Design"], pdf.Layers[1].Intents);
        Assert.Equal(pdf.Layers[0], pdf.Layers.Single(l => l.Name == "Text"));
    }

    [Fact]
    public void BaseStateOffHidesLayersNotListedOn()
    {
        using PdfDocument pdf = PdfDocument.Open(File("/OFF"));

        Assert.All(pdf.Layers, l => Assert.False(l.IsVisible));

        pdf.Layers[1].IsVisible = true;
        Assert.True(pdf.Layers[1].IsVisible);
        Assert.Equal("[11 0 R]", pdf.Catalog.Get<PdfDictionary>("OCProperties")!.Get<PdfDictionary>("D")!["ON"]!.ToString());
    }

    [Fact]
    public void ShowsHidesLocksAndRenames()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        pdf.Layers[0].IsVisible = true;
        pdf.Layers[1].IsVisible = false;
        pdf.Layers[0].IsLocked = false;
        pdf.Layers[2].IsLocked = true;
        pdf.Layers[2].Name = "Notes";

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        Assert.Equal((true, false, true), (reopened.Layers[0].IsVisible, reopened.Layers[1].IsVisible, reopened.Layers[2].IsVisible));
        Assert.Equal((false, false, true), (reopened.Layers[0].IsLocked, reopened.Layers[1].IsLocked, reopened.Layers[2].IsLocked));
        Assert.Equal("Notes", reopened.Layers[2].Name);
    }

    [Fact]
    public void RemovingALayerTakesItOutOfEveryConfiguration()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        Assert.True(pdf.Layers.Remove(pdf.Layers[0]));
        Assert.True(pdf.Layers.Remove(pdf.Layers[0]));

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        Assert.Equal(["Nøt"], reopened.Layers.Select(l => l.Name));
        PdfDictionary properties = reopened.Catalog.Get<PdfDictionary>("OCProperties")!;
        Assert.Equal("[12 0 R]", properties["OCGs"]!.ToString());
        PdfDictionary defaults = properties.Get<PdfDictionary>("D")!;
        Assert.Equal("[[(Group) 12 0 R]]", defaults["Order"]!.ToString());
        Assert.Equal("[]", defaults["OFF"]!.ToString());
        Assert.Equal("[[12 0 R]]", defaults["RBGroups"]!.ToString());
        Assert.Equal("[]", defaults.Get<PdfArray>("AS")!.Get<PdfDictionary>(0)!["OCGs"]!.ToString());
        Assert.Equal("[]", properties.Get<PdfArray>("Configs")!.Get<PdfDictionary>(0)!["OFF"]!.ToString());
    }

    [Fact]
    public void ALayerOfAnotherDocumentIsNotRemoved()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        using PdfDocument other = PdfDocument.Open(File());

        Assert.False(pdf.Layers.Remove(other.Layers[0]));
        Assert.False(pdf.IsChanged);
    }

    [Fact]
    public void ADocumentWithoutLayersHasNone()
    {
        using PdfDocument pdf = PdfDocument.Create();

        Assert.Empty(pdf.Layers);
    }
}
