using System.Text;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Attachments;

/// <summary>Embedded files and associated files (7.11.4, 14.13): Phase 2 task 9.</summary>
public sealed class AttachmentTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    private static readonly byte[] Hello = "hello\n"u8.ToArray();

    /// <summary>One page; catalog entries and extra objects as given.</summary>
    private static byte[] File(string catalog, string header = "%PDF-1.7\n", params (int Number, string Body)[] extra)
    {
        FileBuilder file = new FileBuilder(header)
            .Object(1, $"<< /Type /Catalog /Pages 2 0 R {catalog} >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>");
        foreach ((int number, string body) in extra)
        {
            file.Object(number, body);
        }

        file.Stream(5, "/Type /EmbeddedFile /Subtype /text#2Fplain /Params << /Size 6 /CheckSum <B1946AC92492D2347C6235B4D2611184> /ModDate (D:20261012) >>", Hello);
        return file.StartXref(file.XrefTable($"/Size {extra.Select(e => e.Number + 1).DefaultIfEmpty(6).Max()} /Root 1 0 R")).ToArray();
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void ReadsEmbeddedFiles()
    {
        using PdfDocument pdf = PdfDocument.Open(
            File(
                "/Names << /EmbeddedFiles << /Names [(notes) 4 0 R (old) 6 0 R] >> >>",
                extra: [(4, "<< /Type /Filespec /F (notes.txt) /UF <FEFF006E00F80074> /Desc (Some notes) /EF << /F 5 0 R >> >>"), (6, "<< /Type /Filespec /F (old.txt) >>")]),
            Strict);

        Assert.Equal(["notes", "old"], pdf.Attachments.Select(a => a.Name));
        PdfAttachment notes = pdf.Attachments[0];
        Assert.Equal(("nøt", "Some notes", "text/plain", 6L), (notes.FileName, notes.Description, notes.MimeType, notes.Size));
        Assert.Equal(Hello, notes.GetBytes());
        Assert.True(notes.VerifyChecksum());
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), notes.ModificationDate);
        Assert.True(pdf.Attachments.TryGet("old", out PdfAttachment old));
        Assert.Null(old.GetBytes());
        Assert.Null(old.VerifyChecksum());
        Assert.False(pdf.Attachments.TryGet("missing", out _));
    }

    [Fact]
    public void AddsAndRemovesFiles()
    {
        using PdfDocument pdf = PdfDocument.Open(File(""));

        PdfAttachment data = pdf.Attachments.Add("data.csv", "a,b\n1,2\n"u8, "text/csv", "Numbers", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        pdf.Attachments.Add("other.bin", [0, 1, 2]);
        pdf.Attachments.Add("data.csv", "replaced"u8);
        Assert.True(pdf.Attachments.Remove("other.bin"));
        Assert.False(pdf.Attachments.Remove("other.bin"));
        Assert.Equal("text/csv", data.MimeType);

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        PdfAttachment only = Assert.Single(reopened.Attachments);
        Assert.Equal(("data.csv", "data.csv", "replaced"), (only.Name, only.FileName, Encoding.ASCII.GetString(only.GetBytes()!)));
        Assert.True(only.VerifyChecksum());
        Assert.Equal(8L, only.Size);
        Assert.Null(reopened.Catalog["AF"]);
    }

    [Fact]
    public void Pdf20FilesGoInTheAssociatedFilesToo()
    {
        using PdfDocument pdf = PdfDocument.Open(File("", "%PDF-2.0\n"));

        pdf.Attachments.Add("source.xml", "<a/>"u8, "text/xml");
        pdf.Attachments.Add("more.xml", "<b/>"u8);
        pdf.Attachments.Remove("source.xml");

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        PdfArray associated = reopened.Catalog.Get<PdfArray>("AF")!;
        Assert.Single(associated);
        Assert.Equal("more.xml", associated.Get<PdfDictionary>(0)!.GetText("UF"));
        Assert.Equal("Unspecified", reopened.Attachments[0].Relationship);
    }

    [Fact]
    public void AnExistingAssociatedFilesListIsKeptInStep()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            "/Names << /EmbeddedFiles << /Names [(notes) 4 0 R] >> >> /AF [4 0 R]",
            extra: [(4, "<< /Type /Filespec /F (notes.txt) /EF << /F 5 0 R >> /AFRelationship /Data >>")]));

        pdf.Attachments.Add("extra", "x"u8);
        Assert.Equal(2, pdf.Catalog.Get<PdfArray>("AF")!.Count);
        Assert.True(pdf.Attachments.Remove("notes"));

        Assert.Single(pdf.Catalog.Get<PdfArray>("AF")!);
        Assert.Equal("extra", Assert.Single(pdf.Attachments).Name);
    }

    [Fact]
    public void AKeyInAnotherEncodingIsFoundByItsText()
    {
        // The key is "nøt" in UTF-16, where Egap would write PDFDocEncoding (found by fuzzing).
        using PdfDocument pdf = PdfDocument.Open(File(
            "/Names << /EmbeddedFiles << /Names [<FEFF006E00F80074> 4 0 R] >> >>",
            extra: [(4, "<< /Type /Filespec /F (n.txt) /EF << /F 5 0 R >> >>")]));

        Assert.True(pdf.Attachments.TryGet("nøt", out _));
        pdf.Attachments.Add("nøt", "new"u8);

        Assert.Equal("new", Encoding.ASCII.GetString(Assert.Single(pdf.Attachments).GetBytes()!));
        Assert.True(pdf.Attachments.Remove("nøt"));
        Assert.Empty(pdf.Attachments);
    }

    [Fact]
    public void ManyFilesSplitTheNameTree()
    {
        using PdfDocument pdf = PdfDocument.Open(File(""));
        for (int i = 0; i < 300; i++)
        {
            pdf.Attachments.Add($"file{i:D3}.txt", Encoding.ASCII.GetBytes(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        Assert.Equal(300, reopened.Attachments.Count);
        Assert.True(reopened.Attachments.TryGet("file123.txt", out PdfAttachment file));
        Assert.Equal("123", Encoding.ASCII.GetString(file.GetBytes()!));
        Assert.NotNull(reopened.Catalog.Get<PdfDictionary>("Names")!.Get<PdfDictionary>("EmbeddedFiles")!["Kids"]);
    }
}
