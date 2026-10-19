using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Reading the page tree (7.7.3) and page attributes (7.7.3.3, 14.11.2): Phase 2 task 3.</summary>
public sealed class PageTreeTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>A file from numbered object bodies, object 1 the catalog.</summary>
    private static byte[] File(params (int Number, string Body)[] objects)
    {
        FileBuilder file = new();
        foreach ((int number, string body) in objects)
        {
            file.Object(number, body);
        }

        return file.StartXref(file.XrefTable($"/Size {objects.Max(o => o.Number) + 1} /Root 1 0 R")).ToArray();
    }

    /// <summary>
    /// Pages 4, 6 and 7 under a nested tree: the root has Resources, MediaBox and Rotate 90; node 5 has a CropBox and Rotate 180.
    /// </summary>
    private static byte[] Nested()
    {
        return File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [4 0 R 5 0 R] /Count 3 /Resources << /Font << /F1 3 0 R >> >> /MediaBox [0 0 600 800] /Rotate 90 >>"),
            (3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            (4, "<< /Type /Page /Parent 2 0 R >>"),
            (5, "<< /Type /Pages /Parent 2 0 R /Kids [6 0 R 7 0 R] /Count 2 /CropBox [10 10 590 790] /Rotate 180 >>"),
            (6, "<< /Type /Page /Parent 5 0 R /MediaBox [0 0 300 300] /Resources << >> >>"),
            (7, "<< /Type /Page /Parent 5 0 R /Rotate -90 /UserUnit 2.5 /TrimBox [20 20 100 100] /ArtBox [-50 -50 2000 2000] >>"));
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void ListsPagesInTreeOrder()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested(), Strict);

        Assert.Equal(3, pdf.Pages.Count);
        Assert.Equal([4, 6, 7], pdf.Pages.Select(p => p.Reference!.ObjectNumber));
        Assert.Equal(1, pdf.Pages.IndexOf(pdf.Pages[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages[-1]);
        Assert.False(pdf.IsChanged);
        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void ViewsAreEqualWhenTheyWrapTheSameDictionary()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested());
        PdfPage first = pdf.Pages[0];
        PdfPage again = pdf.Pages.Single(p => p.Reference!.ObjectNumber == 4);

        Assert.Equal(first, again);
        Assert.True(first == again);
        Assert.NotEqual(first, pdf.Pages[1]);
        Assert.Equal(first.GetHashCode(), again.GetHashCode());

        using PdfDocument other = PdfDocument.Open(Nested());
        Assert.Equal(-1, pdf.Pages.IndexOf(other.Pages[0]));
    }

    [Fact]
    public void InheritsResourcesMediaBoxCropBoxAndRotate()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested(), Strict);
        (PdfPage a, PdfPage b, PdfPage c) = (pdf.Pages[0], pdf.Pages[1], pdf.Pages[2]);

        Assert.NotNull(a.Resources!.Get<PdfDictionary>("Font"));
        Assert.Equal(0, b.Resources!.Count);
        Assert.Equal(new PdfRectangle(0, 0, 600, 800), a.MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), b.MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 600, 800), a.CropBox);
        Assert.Equal(new PdfRectangle(10, 10, 300, 300), b.CropBox);
        Assert.Equal(new PdfRectangle(10, 10, 590, 790), c.CropBox);
        Assert.Equal((90, 180, 270), (a.Rotate, b.Rotate, c.Rotate));
    }

    [Fact]
    public void BoxesDefaultToTheCropBoxAndAreClippedToTheMediaBox()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested());
        PdfPage page = pdf.Pages[2];

        Assert.Equal(page.CropBox, page.BleedBox);
        Assert.Equal(new PdfRectangle(20, 20, 100, 100), page.TrimBox);
        Assert.Equal(new PdfRectangle(0, 0, 600, 800), page.ArtBox);
        Assert.Equal(2.5, page.UserUnit);
        Assert.Equal(1, pdf.Pages[0].UserUnit);
    }

    [Theory]
    [InlineData("", 0, 0, 612, 792)]
    [InlineData("/MediaBox [0 0 0 792]", 0, 0, 612, 792)]
    [InlineData("/MediaBox [0 0 100]", 0, 0, 612, 792)]
    [InlineData("/MediaBox [200 300 0 0]", 0, 0, 200, 300)]
    [InlineData("/MediaBox [0 0 595.5 842.25]", 0, 0, 595.5, 842.25)]
    public void ReadsTheMediaBoxOrUsLetter(string entry, double left, double bottom, double right, double top)
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, $"<< /Type /Page /Parent 2 0 R {entry} >>")));

        Assert.Equal(new PdfRectangle(left, bottom, right, top), pdf.Pages[0].MediaBox);
    }

    [Fact]
    public void ACropBoxOutsideTheMediaBoxIsIgnored()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /CropBox [200 200 300 300] /BleedBox [500 500 600 600] >>")));

        Assert.Equal(new PdfRectangle(0, 0, 100, 100), pdf.Pages[0].CropBox);
        Assert.Equal(new PdfRectangle(0, 0, 100, 100), pdf.Pages[0].BleedBox);
    }

    [Theory]
    [InlineData("/Rotate 0", 0)]
    [InlineData("/Rotate 450", 90)]
    [InlineData("/Rotate -90", 270)]
    [InlineData("/Rotate -720", 0)]
    [InlineData("/Rotate 45", 0)]
    [InlineData("/Rotate 180.0", 180)]
    [InlineData("/Rotate /Ninety", 0)]
    public void NormalisesRotate(string entry, int expected)
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, $"<< /Type /Page /Parent 2 0 R {entry} >>")));

        Assert.Equal(expected, pdf.Pages[0].Rotate);
    }

    [Fact]
    public void SettersWriteOnThePageItself()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested());
        PdfPage page = pdf.Pages[0];

        page.Rotate = -90;
        page.MediaBox = new PdfRectangle(612, 792, 0, 0);
        page.CropBox = new PdfRectangle(10, 10, 20, 20);
        page.UserUnit = 2;

        Assert.Equal(new PdfInteger(270), page.Dictionary["Rotate"]);
        Assert.Equal("[0 0 612 792]", page.Dictionary["MediaBox"]!.ToString());
        Assert.Equal((270, 2.0), (page.Rotate, page.UserUnit));
        Assert.Equal(90, pdf.Pages[0].Dictionary.Get<PdfDictionary>("Parent")!.GetInt("Rotate"));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Rotate = 45);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.UserUnit = 0);

        page.UserUnit = 1;
        Assert.Null(page.Dictionary["UserUnit"]);
        Assert.True(pdf.IsChanged);
    }

    [Fact]
    public void ANewDocumentHasNoPages()
    {
        using PdfDocument pdf = PdfDocument.Create();

        Assert.Empty(pdf.Pages);
        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void RepairsABrokenTreeInMemory()
    {
        // Node 2 lists a null, a loop back to itself, page 4 twice and node 5, whose /Count is wrong and whose page 6 has
        // no /Type and the wrong /Parent.
        byte[] bytes = File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 2 0 R 4 0 R 4 0 R 5 0 R] /Count 4 /MediaBox [0 0 200 200] >>"),
            (3, "null"),
            (4, "<< /Type /Page /Parent 2 0 R >>"),
            (5, "<< /Type /Pages /Parent 2 0 R /Kids [6 0 R] /Count 9 /Rotate 90 >>"),
            (6, "<< /Parent 2 0 R >>"));
        using PdfDocument pdf = PdfDocument.Open(bytes);

        Assert.Equal([4, 4, 6], pdf.Pages.Select(p => p.Reference!.ObjectNumber));
        Assert.Equal(90, pdf.Pages[2].Rotate);
        Assert.Equal(new PdfRectangle(0, 0, 200, 200), pdf.Pages[2].MediaBox);
        Assert.Equal(PdfName.Get("Page"), pdf.Pages[2].Dictionary["Type"]);
        Assert.Equal(1, ((PdfDictionary)pdf.Objects.Get(5)).GetInt("Count"));
        Assert.Equal(3, ((PdfDictionary)pdf.Objects.Get(2)).GetInt("Count"));
        Assert.Equal(6, pdf.Warnings.Count(w => w.Code == PdfWarningCode.MalformedPageTree));

        // Repairs made while reading are not edits: saving copies the file as it was.
        Assert.False(pdf.IsChanged);
        Assert.Equal(bytes, Save(pdf));
    }

    [Fact]
    public void StrictModeThrowsOnABrokenTree()
    {
        using PdfDocument pdf = PdfDocument.Open(
            File(
                (1, "<< /Type /Catalog /Pages 2 0 R >>"),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 2 >>"),
                (3, "<< /Type /Page /Parent 2 0 R >>")),
            Strict);

        PdfException error = Assert.Throws<PdfException>(() => pdf.Pages.Count);
        Assert.Equal(PdfWarningCode.MalformedPageTree, error.Code);
    }

    [Fact]
    public void ACatalogWhosePagesIsAPageHasThatOnePage()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Page /MediaBox [0 0 10 10] >>")));

        Assert.Equal([2], pdf.Pages.Select(p => p.Reference!.ObjectNumber));
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.MalformedPageTree);
    }

    [Fact]
    public void ACatalogThatIsItsOwnPageTreeHasNoPages()
    {
        // Found by fuzzing: the catalog was read as its own only page and lost /Type /Catalog, which broke a linearized save.
        using PdfDocument pdf = PdfDocument.Open(File((1, "<< /Type /Catalog /Pages 1 0 R /MediaBox [0 0 10 10] >>")));

        Assert.Empty(pdf.Pages);
        Assert.Equal("Catalog", pdf.Catalog.GetName("Type")?.Value);
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.MalformedPageTree);
    }

    [Fact]
    public void TheCatalogIsNeverAPageFoundByScanning()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Page /MediaBox [0 0 10 10] >>"),
            (2, "<< /Type /Page /MediaBox [0 0 20 20] >>")));

        Assert.Equal([2], pdf.Pages.Select(p => p.Reference!.ObjectNumber));
    }

    [Fact]
    public void PagesFoundAnyWayReadBackAfterARewrite()
    {
        // Found by fuzzing: an outline item in place of the page tree, or among the scanned pages, was read as a page, but
        // as a kid of the rewritten tree it was skipped, so a merged file lost its pages.
        foreach (byte[] bytes in new[]
        {
            File(
                (1, "<< /Type /Catalog /Pages 2 0 R >>"),
                (2, "<< /Rect [0 0 10 10] /P 4 0 R /Dest [4 0 R /Fit] >>"),
                (4, "<< /Type /Page /MediaBox [0 0 10 10] >>")),
            File(
                (1, "<< /Type /Catalog >>"),
                (2, "<< /Type /Page /Rect [0 0 10 10] /Dest [4 0 R /Fit] >>"),
                (4, "<< /Type /Page /MediaBox [0 0 10 10] >>")),
        })
        {
            using PdfDocument pdf = PdfDocument.Open(bytes);
            Assert.Equal([4], pdf.Pages.Select(p => p.Reference!.ObjectNumber));

            using MemoryStream output = new();
            pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
            using PdfDocument reopened = PdfDocument.Open(output.ToArray());
            Assert.Single(reopened.Pages);
            Assert.DoesNotContain(reopened.Warnings, w => w.Code == PdfWarningCode.MalformedPageTree);
        }
    }

    [Fact]
    public void WithoutAPageTreeThePagesAreFoundByScanning()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog >>"),
            (2, "<< /Type /Page /MediaBox [0 0 10 10] >>"),
            (3, "<< /Type /Font >>"),
            (4, "<< /Type /Page >>")));

        Assert.Equal([2, 4], pdf.Pages.Select(p => p.Reference!.ObjectNumber));
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.MalformedPageTree);
    }

    [Fact]
    public void AnOutlineItemListedAsAKidIsSkippedAndLeftUntouched()
    {
        // A damaged file lists outline item 11 and an annotation among the pages; a page with a wrong /Type stays a page.
        byte[] bytes = File(
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 10 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 11 0 R 12 0 R 4 0 R] /Count 4 /MediaBox [0 0 200 200] >>"),
            (3, "<< /Type /Page /Parent 2 0 R >>"),
            (4, "<< /Type /Paeg /Parent 2 0 R /Resources << >> >>"),
            (10, "<< /Type /Outlines /First 11 0 R /Last 11 0 R /Count 1 >>"),
            (11, "<< /Title (a) /Parent 10 0 R /Dest [3 0 R /Fit] >>"),
            (12, "<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Contents (note) >>"));
        using PdfDocument pdf = PdfDocument.Open(bytes);

        Assert.Equal([3, 4], pdf.Pages.Select(p => p.Reference!.ObjectNumber));
        PdfDictionary item = (PdfDictionary)pdf.Objects.Get(11);
        Assert.Equal(10, ((PdfReference)item["Parent"]!).ObjectNumber);
        Assert.Null(item["Type"]);
        Assert.Null(((PdfDictionary)pdf.Objects.Get(12))["Parent"]);

        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
        using PdfDocument reopened = PdfDocument.Open(output.ToArray());
        Assert.Equal(2, reopened.Pages.Count);
        Assert.Equal(["a"], reopened.Outline.Children.Select(i => i.Title));
        Assert.DoesNotContain(reopened.Warnings, w => w.Code == PdfWarningCode.MalformedOutline);
    }

    [Fact]
    public void AnEmptyPagesNodeWithoutKidsIsNotAPage()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 1 >>"),
            (3, "<< /Type /Pages /Parent 2 0 R /Count 0 >>"),
            (4, "<< /Type /Page /Parent 2 0 R >>")));

        Assert.Equal([4], pdf.Pages.Select(p => p.Reference!.ObjectNumber));
        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void ParentLoopsDoNotHangInheritance()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Parent 3 0 R >>"),
            (3, "<< /Type /Page /Parent 2 0 R >>")));

        Assert.Equal(PdfPage.DefaultMediaBox, pdf.Pages[0].MediaBox);
        Assert.Equal(0, pdf.Pages[0].Rotate);
    }

    [Fact]
    public void ADeepTreeIsWalkedWithoutRecursion()
    {
        // 5,000 nested nodes with the page at the bottom.
        const int depth = 5000;
        List<(int, string)> objects = [(1, "<< /Type /Catalog /Pages 2 0 R >>")];
        for (int i = 2; i < depth; i++)
        {
            objects.Add((i, $"<< /Type /Pages /Kids [{i + 1} 0 R] /Count 1 >>"));
        }

        objects.Add((depth, $"<< /Type /Page /Parent {depth - 1} 0 R >>"));
        using PdfDocument pdf = PdfDocument.Open(File([.. objects]));

        Assert.Single(pdf.Pages);
    }
}
