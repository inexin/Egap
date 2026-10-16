using System.Globalization;
using System.Text;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>Revisions (Phase 5 task 2): the original file and each update, found from the startxref that ends each one.</summary>
public sealed class RevisionTests
{
    [Fact]
    public void AnUnchangedFileHasOneRevision()
    {
        byte[] bytes = SignedFiles.Base;
        using PdfDocument pdf = PdfDocument.Open(bytes);
        PdfRevision revision = Assert.Single(pdf.Revisions);
        Assert.Equal(0, revision.Index);
        Assert.Equal(bytes.Length, revision.Length);
        Assert.True(revision.IsLatest);
        Assert.Equal(SignedFiles.LastStartXref(bytes), revision.XrefOffset);
    }

    [Fact]
    public void EachUpdateIsARevision()
    {
        byte[] original = SignedFiles.Base;
        using PdfDocument pdf = PdfDocument.Open(original);
        pdf.Catalog["Lang"] = PdfString.FromText("en");
        using MemoryStream first = new();
        pdf.Save(first, new PdfSaveOptions { UpdateModificationDate = false });

        using PdfDocument once = PdfDocument.Open(first.ToArray());
        once.Catalog["Lang"] = PdfString.FromText("nb");
        using MemoryStream second = new();
        once.Save(second, new PdfSaveOptions { UpdateModificationDate = false });

        using PdfDocument twice = PdfDocument.Open(second.ToArray());
        Assert.Equal([original.Length, first.Length, second.Length], twice.Revisions.Select(r => r.Length));
        Assert.Equal([0, 1, 2], twice.Revisions.Select(r => r.Index));
        Assert.Equal([false, false, true], twice.Revisions.Select(r => r.IsLatest));
    }

    [Fact]
    public void AnEarlierRevisionOpensAsItWasAndCannotBeSaved()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-two-signatures.pdf"));
        Assert.True(pdf.Revisions.Count >= 3);
        using PdfDocument original = pdf.Revisions[0].Open();
        Assert.True(original.IsReadOnly);
        Assert.False(pdf.IsReadOnly);
        Assert.False(original.Form.Exists);
        Assert.Empty(original.Signatures);
        Assert.Single(original.Revisions);
        Assert.Throws<InvalidOperationException>(() => original.Save(new MemoryStream()));
        Assert.Throws<InvalidOperationException>(() => original.Save(Path.Combine(Path.GetTempPath(), "egap-never-written.pdf")));

        using PdfDocument signedOnce = pdf.Revisions[1].Open();
        PdfSignature signature = Assert.Single(signedOnce.Signatures);
        Assert.Equal("Signature1", signature.Name);
        Assert.True(signature.CoversWholeFile);
        Assert.Equal(2, pdf.Signatures.Count);
    }

    [Fact]
    public void TheEndOfLineAfterEofBelongsToTheRevision()
    {
        byte[] original = SignedFiles.Base;
        Assert.Equal((byte)'\n', original[^1]);
        byte[] crlf = [.. original[..^1], (byte)'\r', (byte)'\n'];
        byte[] none = original[..^1];
        using PdfDocument withCrlf = PdfDocument.Open(crlf);
        using PdfDocument withNone = PdfDocument.Open(none);
        Assert.Equal(crlf.Length, Assert.Single(withCrlf.Revisions).Length);
        Assert.Equal(none.Length, Assert.Single(withNone.Revisions).Length);
    }

    [Fact]
    public void ALinearizedFileIsOneRevision()
    {
        // Linearized layout: the first-page section near the start is what the last startxref names, and its /Prev leads to
        // the main section at the end, which no startxref names.
        StringBuilder file = new("%PDF-1.7\n");
        string[] objects =
        [
            "<< /Linearized 1 /L 0 /O 3 /E 0 /N 1 /T 0 /H [0 0] >>",
            "<< /Type /Catalog /Pages 4 0 R >>",
            "<< /Type /Page /Parent 4 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        ];
        int[] offsets = new int[objects.Length + 1];
        offsets[1] = file.Length;
        file.Append(CultureInfo.InvariantCulture, $"1 0 obj\n{objects[0]}\nendobj\n");
        int firstSection = file.Length;

        // The first-page section lists objects 2 and 3; it is written once their offsets are known, so reserve its place.
        string Section(int prev)
        {
            return string.Create(CultureInfo.InvariantCulture, $"xref\n2 2\n{offsets[2]:D10} 00000 n\r\n{offsets[3]:D10} 00000 n\r\ntrailer\n<< /Size 5 /Root 2 0 R /Prev {prev:D10} >>\n");
        }

        string reserved = Section(0);
        file.Append(reserved);
        for (int i = 2; i <= 4; i++)
        {
            offsets[i] = file.Length;
            file.Append(CultureInfo.InvariantCulture, $"{i} 0 obj\n{objects[i - 1]}\nendobj\n");
        }

        int mainSection = file.Length;
        file.Append(CultureInfo.InvariantCulture, $"xref\n0 5\n0000000000 65535 f\r\n{offsets[1]:D10} 00000 n\r\n0000000000 00000 f\r\n0000000000 00000 f\r\n{offsets[4]:D10} 00000 n\r\ntrailer\n<< /Size 5 >>\n");
        file.Append(CultureInfo.InvariantCulture, $"startxref\n{firstSection}\n%%EOF\n");
        string section = Section(mainSection);
        Assert.Equal(reserved.Length, section.Length);
        file.Remove(firstSection, reserved.Length).Insert(firstSection, section);

        using PdfDocument pdf = PdfDocument.Open(Encoding.ASCII.GetBytes(file.ToString()));
        Assert.Single(pdf.Pages);
        PdfRevision revision = Assert.Single(pdf.Revisions);
        Assert.Equal(file.Length, revision.Length);
        Assert.Equal(firstSection, revision.XrefOffset);
    }

    [Fact]
    public void ARepairedFileIsOneRevision()
    {
        byte[] broken = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(SignedFiles.Base).Replace("startxref", "startxrex", StringComparison.Ordinal));
        using PdfDocument pdf = PdfDocument.Open(broken);
        Assert.Single(pdf.Revisions);
    }
}
