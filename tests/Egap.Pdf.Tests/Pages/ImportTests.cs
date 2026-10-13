using System.Text;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Cross-document copy, page import, merge, extract and identical-object merging (Q4): Phase 2 task 18.</summary>
public sealed class ImportTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>
    /// Three pages under a node that gives them resources (font 20, shared) and a media box; page 4 links to page 5 and
    /// page 6; content streams 10, 11 and 12 say which page they are; a logo stream 21 is shared by name.
    /// </summary>
    private static byte[] Source(string label = "A")
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot << /Type /StructTreeRoot >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 3 >>")
            .Object(3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R 6 0 R] /Count 3 /MediaBox [0 0 300 400] /Resources << /Font << /F1 20 0 R >> /XObject << /Logo 21 0 R >> >> /Rotate 90 >>")
            .Object(4, "<< /Type /Page /Parent 3 0 R /Contents 10 0 R /Annots [30 0 R 31 0 R] >>")
            .Object(5, "<< /Type /Page /Parent 3 0 R /Contents 11 0 R >>")
            .Object(6, "<< /Type /Page /Parent 3 0 R /Contents 12 0 R /MediaBox [0 0 100 100] >>")
            .Stream(10, "", Encoding.ASCII.GetBytes($"({label}1) Tj"))
            .Stream(11, "", Encoding.ASCII.GetBytes($"({label}2) Tj"))
            .Stream(12, "", Encoding.ASCII.GetBytes($"({label}3) Tj"))
            .Object(20, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
            .Stream(21, "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", [0x80])
            .Object(30, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /P 4 0 R /Dest [5 0 R /Fit] >>")
            .Object(31, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /P 4 0 R /Dest [6 0 R /Fit] >>");
        return file.StartXref(file.XrefTable("/Size 32 /Root 1 0 R")).ToArray();
    }

    private static string Text(PdfPage page)
    {
        return Encoding.ASCII.GetString(page.Dictionary.Get<PdfStream>("Contents")!.GetDecodedBytes());
    }

    private static byte[] Save(PdfDocument pdf, PdfCompression compression = PdfCompression.Preserve)
    {
        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = compression });
        return output.ToArray();
    }

    [Fact]
    public void ImportsPagesWithWhatTheyInherit()
    {
        using PdfDocument source = PdfDocument.Open(Source());
        using PdfDocument target = PdfDocument.Create();
        target.Pages.AddBlank();

        IReadOnlyList<PdfPage> added = target.Pages.Import(source, [2, 0], at: 0);

        Assert.Equal(3, target.Pages.Count);
        Assert.Equal(["(A3) Tj", "(A1) Tj"], added.Select(Text));
        Assert.Equal(added, target.Pages.Take(2));
        Assert.Equal(new PdfRectangle(0, 0, 100, 100), added[0].MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 300, 400), added[1].MediaBox);
        Assert.Equal((90, 90), (added[0].Rotate, added[1].Rotate));
        Assert.Same(added[0].Resources!.Get<PdfDictionary>("Font")!["F1"], added[0].Resources!.Get<PdfDictionary>("Font")!["F1"]);
        Assert.Equal(added[0].Resources!.Get<PdfDictionary>("Font")!["F1"], added[1].Resources!.Get<PdfDictionary>("Font")!["F1"]);
        Assert.Contains(target.Warnings, w => w.Code == PdfWarningCode.NotImported);

        using PdfDocument reopened = PdfDocument.Open(Save(target), Strict);
        Assert.Equal(["(A3) Tj", "(A1) Tj"], reopened.Pages.Take(2).Select(Text));
        Assert.Equal("Helvetica", reopened.Pages[1].Resources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("F1")!.GetName("BaseFont")?.Value);
    }

    [Fact]
    public void LinksLeadToImportedPagesOrNowhere()
    {
        using PdfDocument source = PdfDocument.Open(Source());
        using PdfDocument target = PdfDocument.Create();

        IReadOnlyList<PdfPage> added = target.Pages.Import(source, [0, 1]);

        PdfLinkAnnotation toImported = (PdfLinkAnnotation)added[0].Annotations[0];
        PdfLinkAnnotation toMissing = (PdfLinkAnnotation)added[0].Annotations[1];
        Assert.Equal(added[1].Reference, toImported.Destination!.PageReference);
        Assert.Equal(added[0], toImported.Page);
        Assert.Null(toMissing.Destination?.PageReference);

        // Only the two pages, their content, the font, the logo and the links were copied: no page tree, no third page.
        using PdfDocument reopened = PdfDocument.Open(Save(target), Strict);
        Assert.Equal(2, reopened.Pages.Count);
        Assert.Equal(2, reopened.Objects.Count(r => reopened.Objects[r] is PdfStream s && s.Dictionary.GetName("Subtype") is null && s.Dictionary.GetName("Type") is null));
    }

    [Fact]
    public void MergesAndExtracts()
    {
        using PdfDocument a = PdfDocument.Open(Source("A"));
        using PdfDocument b = PdfDocument.Open(Source("B"));

        using PdfDocument merged = PdfDocument.Merge(a, b);
        Assert.Equal(["(A1) Tj", "(A2) Tj", "(A3) Tj", "(B1) Tj", "(B2) Tj", "(B3) Tj"], merged.Pages.Select(Text));

        using PdfDocument extracted = merged.ExtractPages([4, 0]);
        Assert.Equal(["(B2) Tj", "(A1) Tj"], extracted.Pages.Select(Text));

        using PdfDocument reopened = PdfDocument.Open(Save(extracted), Strict);
        Assert.Equal(["(B2) Tj", "(A1) Tj"], reopened.Pages.Select(Text));
    }

    [Fact]
    public void ImportingWithinADocumentDuplicatesPages()
    {
        using PdfDocument pdf = PdfDocument.Open(Source());

        pdf.Pages.Import(pdf, [0]);

        Assert.Equal(["(A1) Tj", "(A2) Tj", "(A3) Tj", "(A1) Tj"], pdf.Pages.Select(Text));
        Assert.NotEqual(pdf.Pages[0], pdf.Pages[3]);
    }

    [Fact]
    public void CompactWritesIdenticalObjectsOnce()
    {
        using PdfDocument a = PdfDocument.Open(Source("A"));
        using PdfDocument b = PdfDocument.Open(Source("B"));
        using PdfDocument merged = PdfDocument.Merge(a, b);

        using PdfDocument plain = PdfDocument.Open(Save(merged), Strict);
        using PdfDocument compact = PdfDocument.Open(Save(merged, PdfCompression.Compact), Strict);

        static int Count(PdfDocument pdf, string subtype)
        {
            return pdf.Objects.Count(r => pdf.Objects[r] switch
            {
                PdfStream s => s.Dictionary.GetName("Subtype")?.Value == subtype,
                PdfDictionary d => d.GetName("Subtype")?.Value == subtype,
                _ => false,
            });
        }

        Assert.Equal((2, 2), (Count(plain, "Image"), Count(plain, "Type1")));
        Assert.Equal((1, 1), (Count(compact, "Image"), Count(compact, "Type1")));
        using MemoryStream unmerged = new();
        merged.Save(unmerged, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = PdfCompression.Compact, MergeIdenticalObjects = false });
        using PdfDocument apart = PdfDocument.Open(unmerged.ToArray(), Strict);
        Assert.Equal((2, 2), (Count(apart, "Image"), Count(apart, "Type1")));
        Assert.Equal(["(A1) Tj", "(A2) Tj", "(A3) Tj", "(B1) Tj", "(B2) Tj", "(B3) Tj"], compact.Pages.Select(Text));
        Assert.Equal(6, compact.Pages.Count(p => p.Annotations.Count == 0 || p.Annotations.All(a => a.Page == p)));
    }

    [Fact]
    public void RejectsABadIndex()
    {
        using PdfDocument source = PdfDocument.Open(Source());
        using PdfDocument target = PdfDocument.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => target.Pages.Import(source, [3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => target.Pages.Import(source, [0], at: 2));
        Assert.Empty(target.Pages);
    }
}
