using System.Text;

using Egap.Pdf.Objects;
using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests;

/// <summary>Damaged copies of good files must open with warnings and the same objects, and must throw in strict mode.</summary>
public sealed class RepairTests
{
    /// <summary>A file with a stream, an incremental update that changes object 2, and an info dictionary.</summary>
    private static byte[] Good()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>")
            .Stream(4, "", "BT /F1 12 Tf (Hello) Tj ET"u8.ToArray())
            .Object(5, "<< /Title (Good) >>");
        long first = file.XrefTable("/Size 6 /Root 1 0 R /Info 5 0 R");
        file.StartXref(first);
        file.Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Updated true >>");
        return file.StartXref(file.XrefTable($"/Size 6 /Root 1 0 R /Info 5 0 R /Prev {first}", withZero: false)).ToArray();
    }

    /// <summary>A file whose objects are in an object stream, found through a cross-reference stream.</summary>
    private static byte[] Compressed()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .ObjectStream(10, (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), (3, "<< /Type /Page /Parent 2 0 R >>"));
        return file.StartXref(file.XrefStream(11, "/Root 1 0 R", compressed: [(2, 10, 0), (3, 10, 1)])).ToArray();
    }

    private static Dictionary<int, string> Objects(PdfDocument pdf)
    {
        return pdf.Objects.Where(r => pdf.Objects[r] is not PdfNull)
            .ToDictionary(r => r.ObjectNumber, r => Encoding.Latin1.GetString(ObjectWriter.ToBytes(pdf.Objects[r])));
    }

    private static Dictionary<int, string> Expected(byte[] good)
    {
        using PdfDocument pdf = PdfDocument.Open(good);
        Assert.Empty(pdf.Warnings);
        return Objects(pdf);
    }

    private static PdfWarningCode[] Codes(PdfDocument pdf)
    {
        return [.. pdf.Warnings.Select(w => w.Code).Distinct()];
    }

    private static byte[] Replace(byte[] file, string find, string replacement, bool last = false)
    {
        string text = Encoding.Latin1.GetString(file);
        int at = last ? text.LastIndexOf(find, StringComparison.Ordinal) : text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, find);
        return Encoding.Latin1.GetBytes(text[..at] + replacement + text[(at + find.Length)..]);
    }

    public static TheoryData<string> Damage => ["truncated", "no startxref", "bad startxref", "broken xref keyword", "garbage table"];

    private static byte[] Damaged(string kind, byte[] good)
    {
        string text = Encoding.Latin1.GetString(good);
        return kind switch
        {
            // Cut in the middle of the last table: no trailer, no startxref.
            "truncated" => good[..(text.LastIndexOf("\nxref", StringComparison.Ordinal) + 20)],
            "no startxref" => Replace(good, "startxref", "startxyz", last: true),
            "bad startxref" => Replace(good, "startxref\n" + text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0], "startxref\n99", last: true),
            "broken xref keyword" => Replace(Replace(good, "xref\n", "xrof\n", last: true), "xref\n", "xrof\n"),
            _ => Replace(Replace(good, "0000000000 65535 f", "hello world, broken"), "trailer", "trailor", last: true),
        };
    }

    [Theory]
    [MemberData(nameof(Damage))]
    public void UnreadableCrossReferenceIsRebuilt(string kind)
    {
        byte[] good = Good();
        byte[] damaged = Damaged(kind, good);

        using PdfDocument pdf = PdfDocument.Open(damaged);

        Assert.Contains(PdfWarningCode.XrefRebuilt, Codes(pdf));
        Assert.True(pdf.Rebuilt);
        Assert.Equal(Expected(good), Objects(pdf));
        Assert.Equal("Good", pdf.Trailer.Get<PdfDictionary>("Info")?.GetText("Title"));
        Assert.True(pdf.Catalog.Get<PdfDictionary>("Pages")!.GetBool("Updated"));
        Assert.Throws<PdfException>(() => PdfDocument.Open(damaged, new PdfReadOptions { Strict = true }));
    }

    [Fact]
    public void RebuildFindsObjectsInObjectStreams()
    {
        byte[] good = Compressed();
        byte[] damaged = Replace(good, "startxref", "startxyz", last: true);

        using PdfDocument pdf = PdfDocument.Open(damaged);

        Assert.Contains(PdfWarningCode.XrefRebuilt, Codes(pdf));
        Assert.Equal("Page", pdf.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!.GetName("Type")!.Value);
        Assert.Equal(Expected(good)[3], Objects(pdf)[3]);
    }

    [Fact]
    public void RebuildFindsTheCatalogByItsType()
    {
        byte[] good = Good();

        // No trailer names a root: both trailers lose /Root, and the cross-reference is unreadable too.
        byte[] damaged = Replace(Replace(Damaged("no startxref", good), "/Root 1 0 R", "/Rest 1 0 R"), "/Root 1 0 R", "/Rest 1 0 R");

        using PdfDocument pdf = PdfDocument.Open(damaged);

        Assert.Equal("Catalog", pdf.Catalog.GetName("Type")!.Value);
        Assert.Equal(1, Assert.IsType<PdfReference>(pdf.Trailer["Root"]).ObjectNumber);
    }

    [Fact]
    public void ReadableCrossReferenceWithoutACatalogIsRebuilt()
    {
        byte[] damaged = Replace(Replace(Good(), "/Root 1 0 R", "/Root 9 0 R"), "/Root 1 0 R", "/Root 9 0 R");

        using PdfDocument pdf = PdfDocument.Open(damaged);

        Assert.Contains(PdfWarningCode.XrefRebuilt, Codes(pdf));
        Assert.Equal("Catalog", pdf.Catalog.GetName("Type")!.Value);
    }

    [Fact]
    public void ShiftedObjectsAreFound()
    {
        // The table lists object 3 four bytes late and object 4 far away, as after an editor shifted bytes.
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>")
            .Raw(new string('%', 3000) + "\n")
            .Stream(4, "", "BT ET"u8.ToArray());
        long xref = file.Position;
        long[] offsets = [file.Offsets[1], file.Offsets[2], file.Offsets[3] + 4, 9];
        file.Raw("xref\n0 5\n0000000000 65535 f\r\n" + string.Concat(offsets.Select(o => $"{o:D10} 00000 n\r\n")) + "trailer << /Size 5 /Root 1 0 R >>\n").StartXref(xref);
        byte[] damaged = file.ToArray();

        using PdfDocument pdf = PdfDocument.Open(damaged);

        Assert.Equal("Page", Assert.IsType<PdfDictionary>(pdf.Objects.Get(3)).GetName("Type")!.Value);
        Assert.Equal("BT ET"u8.ToArray(), Assert.IsType<PdfStream>(pdf.Objects.Get(4)).GetEncodedBytes());
        Assert.Equal([PdfWarningCode.ObjectRelocated], Codes(pdf));
        Assert.Equal(2, pdf.Warnings.Count);
        Assert.False(pdf.Rebuilt);
        using PdfDocument strict = PdfDocument.Open(damaged, new PdfReadOptions { Strict = true });
        Assert.Throws<PdfException>(() => strict.Objects.Get(3));
    }

    [Fact]
    public void WrongLengthIsRepaired()
    {
        byte[] damaged = Replace(Good(), "/Length 26", "/Length 21");

        using PdfDocument pdf = PdfDocument.Open(damaged);

        Assert.Equal("BT /F1 12 Tf (Hello) Tj ET"u8.ToArray(), Assert.IsType<PdfStream>(pdf.Objects.Get(4)).GetEncodedBytes());
        Assert.Equal([PdfWarningCode.WrongStreamLength], Codes(pdf));
    }

    [Fact]
    public void NewestObjectWinsWhenRebuilding()
    {
        // Both versions of object 2 are in the file; the later one is the update.
        using PdfDocument pdf = PdfDocument.Open(Damaged("truncated", Good()));

        Assert.True(pdf.Objects.Get(2) is PdfDictionary pages && pages.GetBool("Updated") == true);
    }

    [Fact]
    public void DamagedCrossReferenceStreamIsRebuilt()
    {
        byte[] good = Compressed();
        string text = Encoding.Latin1.GetString(good);

        // Damage the middle of the cross-reference stream's Flate data.
        int data = text.IndexOf("/Type /XRef", StringComparison.Ordinal);
        data = text.IndexOf("stream\n", data, StringComparison.Ordinal) + 7 + 6;
        byte[] damaged = (byte[])good.Clone();
        damaged[data] ^= 0xFF;
        damaged[data + 1] ^= 0x55;

        using PdfDocument pdf = PdfDocument.Open(damaged);

        Assert.True(pdf.Rebuilt);
        Assert.Equal(Expected(good)[3], Objects(pdf)[3]);
    }

    [Fact]
    public void ObjectsADamagedTableLeavesOutAreAdded()
    {
        // The table's second subsection is cut short by garbage, so objects 3 and 4 are not listed.
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>")
            .Stream(4, "", "BT ET"u8.ToArray());
        long xref = file.Position;
        file.Raw($"xref\n0 3\n0000000000 65535 f\r\n{file.Offsets[1]:D10} 00000 n\r\n{file.Offsets[2]:D10} 00000 n\r\n3 2\n4294967296 garbage\r\ntrailer << /Size 5 /Root 1 0 R >>\n").StartXref(xref);

        using PdfDocument pdf = PdfDocument.Open(file.ToArray());

        Assert.Equal("Page", pdf.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!.GetName("Type")!.Value);
        Assert.Contains(PdfWarningCode.XrefRebuilt, Codes(pdf));
        Assert.False(pdf.Rebuilt);
    }

    [Fact]
    public void FileWithNothingInItStillThrows()
    {
        Assert.Throws<PdfException>(() => PdfDocument.Open("%PDF-1.7\nno objects at all\n"u8.ToArray()));
    }
}
