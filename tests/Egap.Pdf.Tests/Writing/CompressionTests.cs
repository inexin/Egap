using System.Text;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Writing;

public sealed class CompressionTests
{
    private static PdfSaveOptions Options(PdfCompression compression)
    {
        return new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = compression, Deterministic = true };
    }

    /// <summary>A classic file with many small objects and a stream.</summary>
    private static byte[] Classic(int extra = 250)
    {
        FileBuilder file = new FileBuilder("%PDF-1.4\n")
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /Names 4 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>")
            .Stream(5, "", "BT (Hi) Tj ET"u8.ToArray());
        StringBuilder names = new("<< /Items [");
        for (int i = 0; i < extra; i++)
        {
            file.Object(10 + i, $"<< /Index {i} /Name (item {i}) >>");
            names.Append(System.Globalization.CultureInfo.InvariantCulture, $"{10 + i} 0 R ");
        }

        file.Object(4, names.Append("] >>").ToString());
        return file.StartXref(file.XrefTable($"/Size {10 + extra} /Root 1 0 R")).ToArray();
    }

    /// <summary>A file with a cross-reference stream and an object stream.</summary>
    private static byte[] Compressed()
    {
        FileBuilder file = new FileBuilder("%PDF-1.6\n")
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .ObjectStream(10, (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), (3, "<< /Type /Page /Parent 2 0 R >>"));
        return file.StartXref(file.XrefStream(11, "/Root 1 0 R", compressed: [(2, 10, 0), (3, 10, 1)])).ToArray();
    }

    private static (PdfDocument Pdf, byte[] Bytes) Rewrite(byte[] input, PdfCompression compression)
    {
        using PdfDocument original = PdfDocument.Open(input);
        using MemoryStream output = new();
        original.Save(output, Options(compression));
        byte[] bytes = output.ToArray();
        return (PdfDocument.Open(bytes, new PdfReadOptions { Strict = true }), bytes);
    }

    private static int CountObjectStreams(PdfDocument pdf)
    {
        return pdf.Xref.Entries.Where(e => e.Value.Kind == Egap.Pdf.Parsing.XrefEntryKind.InObjectStream).Select(e => e.Value.Offset).Distinct().Count();
    }

    [Fact]
    public void CompactPacksObjectsAndRaisesTheVersion()
    {
        (PdfDocument pdf, byte[] bytes) = Rewrite(Classic(), PdfCompression.Compact);
        using (pdf)
        {
            Assert.Empty(pdf.Warnings);
            Assert.True(pdf.Xref.Sections[0].IsStream);
            Assert.Equal("1.5", pdf.HeaderVersion);

            // 254 objects that are not streams, at most 100 to an object stream.
            Assert.Equal(3, CountObjectStreams(pdf));
            Assert.Equal("item 249", pdf.Catalog.Get<PdfDictionary>("Names")!.Get<PdfArray>("Items")!.Get<PdfDictionary>(249)!.GetText("Name"));
            Assert.Equal("BT (Hi) Tj ET"u8.ToArray(), pdf.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!.Get<PdfStream>("Contents")!.GetEncodedBytes());
        }

        (PdfDocument plain, byte[] plainBytes) = Rewrite(Classic(), PdfCompression.None);
        plain.Dispose();
        Assert.True(bytes.Length < plainBytes.Length / 2, $"{bytes.Length} compact bytes against {plainBytes.Length}");
    }

    [Fact]
    public void PreserveKeepsAClassicFileClassic()
    {
        (PdfDocument pdf, _) = Rewrite(Classic(), PdfCompression.Preserve);
        using (pdf)
        {
            Assert.False(pdf.Xref.Sections[0].IsStream);
            Assert.Equal(0, CountObjectStreams(pdf));
            Assert.Equal("1.4", pdf.HeaderVersion);
        }
    }

    [Fact]
    public void PreserveKeepsACompressedFileCompressed()
    {
        (PdfDocument pdf, _) = Rewrite(Compressed(), PdfCompression.Preserve);
        using (pdf)
        {
            Assert.True(pdf.Xref.Sections[0].IsStream);
            Assert.Equal(1, CountObjectStreams(pdf));
            Assert.Equal("1.6", pdf.HeaderVersion);
            Assert.Equal("Page", pdf.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!.GetName("Type")!.Value);
        }
    }

    [Fact]
    public void NoneUnpacksACompressedFile()
    {
        (PdfDocument pdf, _) = Rewrite(Compressed(), PdfCompression.None);
        using (pdf)
        {
            Assert.False(pdf.Xref.Sections[0].IsStream);
            Assert.Equal(0, CountObjectStreams(pdf));
            Assert.Equal("Page", pdf.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!.GetName("Type")!.Value);
        }
    }

    [Fact]
    public void ObjectsWithAGenerationStayOutOfObjectStreams()
    {
        FileBuilder file = new FileBuilder().Object(1, "<< /Type /Catalog /Extra 2 3 R >>").Object(2, "(old generation)", generation: 3);
        byte[] input = file.StartXref(file.XrefTable("/Size 3 /Root 1 0 R")).ToArray();
        using PdfDocument original = PdfDocument.Open(input);
        using MemoryStream output = new();

        original.Save(output, Options(PdfCompression.Compact) with { KeepObjectNumbers = true });

        using PdfDocument pdf = PdfDocument.Open(output.ToArray(), new PdfReadOptions { Strict = true });
        Assert.True(pdf.Xref.TryGet(2, out Egap.Pdf.Parsing.XrefEntry entry));
        Assert.Equal(Egap.Pdf.Parsing.XrefEntryKind.InFile, entry.Kind);
        Assert.Equal("old generation", pdf.Catalog.GetText("Extra"));
    }

    [Fact]
    public void LargeOffsetsWidenTheFields()
    {
        // A stream large enough that offsets need three bytes.
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Big 2 0 R /After 3 0 R >>")
            .Stream(2, "", new byte[200_000])
            .Object(3, "(after)");
        using PdfDocument original = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 4 /Root 1 0 R")).ToArray());
        using MemoryStream output = new();

        original.Save(output, Options(PdfCompression.Compact));

        using PdfDocument pdf = PdfDocument.Open(output.ToArray(), new PdfReadOptions { Strict = true });
        Assert.Equal(3, pdf.Xref.Sections[0].Trailer.Get<PdfArray>("W")!.GetInt(1));
        Assert.Equal("after", pdf.Catalog.GetText("After"));
        Assert.Equal(200_000, pdf.Catalog.Get<PdfStream>("Big")!.EncodedLength);
    }
}
