using System.Text;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Drawing onto existing pages and adding resources to them: Phase 3 task 5.</summary>
public sealed class PageDrawingTests
{
    [Fact]
    public void DrawsOverTheContentFromACleanState()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfReference old = pdf.Objects.Add(Stream("q 2 0 0 2 0 0 cm 0 0 1 1 re f"));
        page.Dictionary["Contents"] = old;

        page.AddContent("0 0 5 5 re f"u8);

        PdfArray contents = page.Dictionary.Get<PdfArray>("Contents")!;
        Assert.Equal(3, contents.Count);
        Assert.Same(old, contents[1]);
        Assert.Equal("q\n", Decoded(contents[0]));
        Assert.Equal("\nQ\n0 0 5 5 re f", Decoded(contents[2]));

        // The old content leaves a q and a matrix open; the new drawing is still in default user space.
        Matrices walker = new();
        walker.Walk(page);
        Assert.Equal([new PdfMatrix(2, 0, 0, 2, 0, 0), PdfMatrix.Identity], walker.Seen);
    }

    [Fact]
    public void DrawsUnderTheContent()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfReference old = pdf.Objects.Add(Stream("0 0 1 1 re f"));
        page.Dictionary["Contents"] = new PdfArray { old };

        page.AddContent("2 0 0 2 0 0 cm 0 0 5 5 re f"u8, PdfCanvasPlacement.Under);

        PdfArray contents = page.Dictionary.Get<PdfArray>("Contents")!;
        Assert.Equal(2, contents.Count);
        Assert.Equal("q\n2 0 0 2 0 0 cm 0 0 5 5 re f\nQ\n", Decoded(contents[0]));
        Assert.Same(old, contents[1]);
        Matrices walker = new();
        walker.Walk(page);
        Assert.Equal([new PdfMatrix(2, 0, 0, 2, 0, 0), PdfMatrix.Identity], walker.Seen);
    }

    [Fact]
    public void DrawsOnAnEmptyPageWithOneStream()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();

        page.AddContent("0 0 5 5 re f"u8);

        Assert.Equal("0 0 5 5 re f", Decoded(page.Dictionary["Contents"]!));
    }

    [Fact]
    public void CopiesSharedAndInheritedResourcesBeforeAdding()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage first = pdf.Pages.AddBlank();
        PdfPage second = pdf.Pages.AddBlank();
        PdfReference font = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font") });
        PdfReference shared = pdf.Objects.Add(new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } });
        first.Dictionary["Resources"] = shared;
        second.Dictionary["Resources"] = shared;
        PdfReference image = pdf.Objects.Add(new PdfStream());

        PdfName name = first.AddResource("XObject", image);

        Assert.Equal("Eg0", name.Value);
        Assert.IsType<PdfDictionary>(first.Dictionary["Resources"]);
        Assert.Same(font, first.Resources!.Get<PdfDictionary>("Font")!["F1"]);
        Assert.Null(((PdfDictionary)pdf.Objects[shared]).Get("XObject"));
        Assert.Same(shared, second.Dictionary["Resources"]);
        Assert.Equal(name, first.AddResource("XObject", image));
        Assert.Equal("Eg1", first.AddResource("XObject", pdf.Objects.Add(new PdfStream())).Value);
    }

    [Fact]
    public void NamesNewResourcesClearOfExistingOnes()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfReference categoryReference = pdf.Objects.Add(new PdfDictionary { ["Eg0"] = new PdfInteger(1), ["Eg1"] = new PdfInteger(2) });
        page.Dictionary["Resources"] = new PdfDictionary { ["ExtGState"] = categoryReference };

        PdfName name = page.AddResource("ExtGState", new PdfDictionary { ["CA"] = new PdfReal(0.5) });

        Assert.Equal("Eg2", name.Value);
        Assert.Equal(2, ((PdfDictionary)pdf.Objects[categoryReference]).Count);
        Assert.Equal(3, page.Resources!.Get<PdfDictionary>("ExtGState")!.Count);
    }

    [Fact]
    public void AnIncrementalSaveWritesOnlyTheNewStreamsAndThePage()
    {
        using PdfDocument source = PdfDocument.Create();
        PdfPage blank = source.Pages.AddBlank();
        blank.Dictionary["Contents"] = source.Objects.Add(Stream("0 0 1 1 re f"));
        using MemoryStream original = new();
        source.Save(original);

        using PdfDocument pdf = PdfDocument.Open(original.ToArray());
        pdf.Pages[0].AddContent("1 0 0 rg 0 0 5 5 re f"u8);
        using MemoryStream saved = new();
        pdf.Save(saved);

        byte[] update = saved.ToArray()[(int)original.Length..];
        string text = Encoding.Latin1.GetString(update);
        // The two new streams and the page, besides the cross-reference stream.
        int xref = text.Contains("/Type /XRef", StringComparison.Ordinal) ? 1 : 0;
        Assert.Equal(2, text.Split("endstream").Length - 1 - xref);
        Assert.Contains("/Type /Page ", text, StringComparison.Ordinal);
        Assert.True(update.Length < 900, $"{update.Length} bytes");
    }

    private static string Decoded(PdfObject entry)
    {
        return Encoding.ASCII.GetString(((PdfStream)entry.Resolve()).GetDecodedBytes());
    }

    private static PdfStream Stream(string content)
    {
        PdfStream stream = new();
        stream.SetDecodedBytes(Encoding.ASCII.GetBytes(content));
        return stream;
    }

    private sealed class Matrices : PdfContentWalker
    {
        public List<PdfMatrix> Seen { get; } = [];

        protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
        {
            Seen.Add(State.Matrix);
        }
    }
}
