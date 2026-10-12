using System.Text;

using Egap.Pdf.IO;
using Egap.Pdf.Objects;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Tests.Parsing;

public sealed class XrefTests
{
    private sealed record Opened(XrefTable Xref, ObjectLoader Loader, WarningLog Log)
    {
        public PdfObject this[int number] => Loader.Load(number, Xref.TryGet(number, out XrefEntry entry) && entry.Kind == XrefEntryKind.InFile ? entry.Generation : 0);

        public PdfWarningCode[] Warnings => [.. Log.ToArray().Select(w => w.Code)];
    }

    private static Opened Open(byte[] file, PdfReadOptions? options = null, bool strict = false)
    {
        options ??= PdfReadOptions.Default;
        PdfSource source = PdfSource.FromMemory(file);
        WarningLog log = new(strict);
        XrefTable xref = new XrefReader(source, options, log).Read();
        return new Opened(xref, new ObjectLoader(source, xref, options, log), log);
    }

    private static string Write(PdfObject value)
    {
        return Encoding.Latin1.GetString(ObjectWriter.ToBytes(value));
    }

    private static FileBuilder Basic()
    {
        return new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>")
            .Stream(4, "", "BT ET"u8.ToArray());
    }

    [Fact]
    public void ReadsAClassicTable()
    {
        FileBuilder file = Basic();
        file.StartXref(file.XrefTable("/Size 5 /Root 1 0 R"));

        Opened opened = Open(file.ToArray());

        Assert.Equal(5, opened.Xref.Count);
        Assert.Equal("1 0 R", opened.Xref.Trailer["Root"]!.ToString());
        Assert.Equal("<< /Type /Catalog /Pages 2 0 R >>", Write(opened[1]));
        Assert.Equal("BT ET"u8.ToArray(), Assert.IsType<PdfStream>(opened[4]).GetEncodedBytes());
        Assert.Same(PdfNull.Instance, opened[0]);
        Assert.Same(PdfNull.Instance, opened[99]);
        Assert.Empty(opened.Warnings);
    }

    [Fact]
    public void ReferencesInLoadedObjectsResolve()
    {
        FileBuilder file = Basic();
        file.StartXref(file.XrefTable("/Size 5 /Root 1 0 R"));
        Opened opened = Open(file.ToArray());

        PdfDictionary catalog = Assert.IsType<PdfDictionary>(opened[1]);
        PdfDictionary page = catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!;

        Assert.Equal("Page", page.GetName("Type")!.Value);
        Assert.Same(opened[3], page);
    }

    [Fact]
    public void ResolvesAnIndirectStreamLength()
    {
        FileBuilder file = new FileBuilder()
            .Raw("1 0 obj\n<< /Length 2 0 R >>\nstream\nabcdef\nendstream\nendobj\n");
        file.Offsets[1] = 15;
        long two = file.Position;
        file.Raw("2 0 obj 6 endobj\n");
        long xref = file.Position;
        file.Raw($"xref\n0 3\n0000000000 65535 f\r\n{15:D10} 00000 n\r\n{two:D10} 00000 n\r\ntrailer << /Size 3 >>\n").StartXref(xref);

        Opened opened = Open(file.ToArray());

        Assert.Equal("abcdef"u8.ToArray(), Assert.IsType<PdfStream>(opened[1]).GetEncodedBytes());
        Assert.Empty(opened.Warnings);
    }

    [Fact]
    public void NewerSectionsWin()
    {
        FileBuilder file = Basic().Object(5, "(info)");
        long first = file.XrefTable("/Size 6 /Root 1 0 R /Info 5 0 R");
        file.StartXref(first);

        // An incremental update changes object 2, deletes object 3 and adds object 6.
        file.Object(2, "<< /Type /Pages /Kids [] /Count 0 >>").Object(6, "(new)");
        file.StartXref(file.XrefTable($"/Size 7 /Root 1 0 R /Prev {first}", withZero: false, free: [(3, 1)]));

        Opened opened = Open(file.ToArray());

        Assert.Equal(0, Assert.IsType<PdfDictionary>(opened[2]).GetInt("Count"));
        Assert.Same(PdfNull.Instance, opened[3]);
        Assert.Equal("(new)", Write(opened[6]));
        Assert.Equal("(info)", Write(opened[5]));
        Assert.Equal(2, opened.Xref.Sections.Count);
        // The newest trailer is the trailer: its update left /Info out, so it is gone (7.5.6).
        Assert.False(opened.Xref.Trailer.ContainsKey("Info"));
        Assert.Equal(7, opened.Xref.Trailer.GetInt("Size"));
        Assert.False(opened.Xref.Trailer.ContainsKey("Prev"));
    }

    [Fact]
    public void ReadsCrossReferenceStreamsAndObjectStreams()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .ObjectStream(10, (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), (3, "<< /Type /Page /Parent 2 0 R /Note 4 0 R >>"), (4, "[1 (two) /Three]"));
        file.StartXref(file.XrefStream(11, "/Root 1 0 R", compressed: [(2, 10, 0), (3, 10, 1), (4, 10, 2)]));

        Opened opened = Open(file.ToArray());

        Assert.Equal("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", Write(opened[2]));
        Assert.Equal("[1 (two) /Three]", Write(Assert.IsType<PdfDictionary>(opened[3]).Get("Note")!));
        Assert.Equal("1 0 R", opened.Xref.Trailer["Root"]!.ToString());
        Assert.False(opened.Xref.Trailer.ContainsKey("W"));
        Assert.Empty(opened.Warnings);
    }

    [Fact]
    public void ReadsAHybridFile()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .ObjectStream(10, (2, "<< /Type /Pages /Count 0 >>"));
        long stream = file.XrefStream(11, "", compressed: [(2, 10, 0)]);

        // The table lists the compressed object as free, as PDF 1.4 readers need; the stream fills it in.
        file.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        file.StartXref(file.XrefTable($"/Size 12 /Root 1 0 R /XRefStm {stream}", free: [(2, 0)]));

        Opened opened = Open(file.ToArray());

        Assert.Equal("<< /Type /Pages /Count 0 >>", Write(opened[2]));
        Assert.Equal(2, opened.Xref.Sections.Count);
        Assert.Empty(opened.Warnings);
    }

    [Fact]
    public void CutsAPrevLoop()
    {
        FileBuilder file = Basic();
        long at = file.Position;
        file.XrefTable($"/Size 5 /Root 1 0 R /Prev {at}");
        file.StartXref(at);

        Opened opened = Open(file.ToArray());

        Assert.Equal([PdfWarningCode.BrokenXrefChain], opened.Warnings);
        Assert.IsType<PdfDictionary>(opened[1]);
    }

    [Theory]
    [InlineData("/Prev 999999")]
    [InlineData("/Prev 3")]
    [InlineData("/Prev (x)")]
    public void SkipsABrokenPrev(string prev)
    {
        FileBuilder file = Basic();
        file.StartXref(file.XrefTable($"/Size 5 /Root 1 0 R {prev}"));

        Opened opened = Open(file.ToArray());

        Assert.Equal([PdfWarningCode.BrokenXrefChain], opened.Warnings);
        Assert.IsType<PdfDictionary>(opened[1]);
    }

    [Fact]
    public void RenumbersAFirstSubsectionThatStartsAtOne()
    {
        FileBuilder file = Basic();
        long at = file.XrefTable("/Size 5");
        byte[] bytes = file.StartXref(at).ToArray();
        int header = Encoding.Latin1.GetString(bytes).IndexOf("xref\n0 5", StringComparison.Ordinal);
        bytes[header + 5] = (byte)'1';

        Opened opened = Open(bytes);

        Assert.Equal([PdfWarningCode.MalformedXref], opened.Warnings);
        Assert.IsType<PdfDictionary>(opened[1]);
    }

    [Fact]
    public void ReadsAShortSubsectionAndEntriesWithOtherLineEnds()
    {
        FileBuilder file = Basic();
        long xref = file.Position;
        file.Raw($"xref\n0 9\n0000000000 65535 f \n{file.Offsets[1]:D10} 00000 n\n{file.Offsets[2]:D10} 00000 n \r\n{file.Offsets[3]:D10} 00000 n\r{file.Offsets[4]:D10} 00000 n\r\ntrailer\n<< /Size 5 >>\n").StartXref(xref);

        Opened opened = Open(file.ToArray());

        Assert.Equal([PdfWarningCode.MalformedXref], opened.Warnings);
        Assert.IsType<PdfStream>(opened[4]);
    }

    [Theory]
    [InlineData("1 0 obj null endobj")]
    [InlineData("1 0 obj null endobj startxref\n9999\n%%EOF")]
    [InlineData("1 0 obj null endobj startxref\n0\n%%EOF")]
    public void FileWithoutAUsableStartxrefThrows(string text)
    {
        Assert.Throws<PdfException>(() => Open(Encoding.Latin1.GetBytes(text)));
    }

    [Fact]
    public void FindsStartxrefFarFromTheEnd()
    {
        FileBuilder file = Basic();
        file.StartXref(file.XrefTable("/Size 5 /Root 1 0 R")).Raw(new string('%', 5000) + "\n");

        Assert.IsType<PdfDictionary>(Open(file.ToArray())[1]);
    }

    [Fact]
    public void SectionAndObjectCountsAreLimited()
    {
        FileBuilder file = Basic();
        long first = file.XrefTable("/Size 5");
        long second = file.XrefTable($"/Size 5 /Prev {first}", withZero: false);
        file.StartXref(file.XrefTable($"/Size 5 /Prev {second}", withZero: false));

        Assert.Equal("MaxXrefSections", Assert.Throws<PdfLimitException>(() => Open(file.ToArray(), new PdfReadOptions { MaxXrefSections = 2 })).Limit);
        Assert.Equal("MaxObjects", Assert.Throws<PdfLimitException>(() => Open(file.ToArray(), new PdfReadOptions { MaxObjects = 3 })).Limit);
        Assert.Equal(3, Open(file.ToArray()).Xref.Sections.Count);
    }

    [Fact]
    public void ObjectsNotAtTheirOffsetAreFoundNearbyOrByScanning()
    {
        FileBuilder file = Basic();
        long xref = file.Position;
        file.Raw($"xref\n0 3\n0000000000 65535 f\r\n{file.Offsets[2]:D10} 00000 n\r\n{file.Offsets[2] + 3:D10} 00000 n\r\ntrailer << /Size 3 >>\n").StartXref(xref);

        Opened opened = Open(file.ToArray());

        Assert.Equal("Catalog", Assert.IsType<PdfDictionary>(opened[1]).GetName("Type")!.Value);
        Assert.Equal("Pages", Assert.IsType<PdfDictionary>(opened[2]).GetName("Type")!.Value);
        Assert.Equal([PdfWarningCode.ObjectRelocated, PdfWarningCode.ObjectRelocated], opened.Warnings);
    }

    [Fact]
    public void ObjectFarFromItsOffsetIsFoundByScanning()
    {
        FileBuilder file = new FileBuilder().Object(1, "(one)").Raw(new string('%', 5000) + "\n").Object(2, "(two)");
        long xref = file.Position;
        file.Raw($"xref\n0 3\n0000000000 65535 f\r\n{file.Offsets[1]:D10} 00000 n\r\n{file.Offsets[1]:D10} 00000 n\r\ntrailer << /Size 3 >>\n").StartXref(xref);

        Opened opened = Open(file.ToArray());

        Assert.Equal("(two)", Encoding.Latin1.GetString(ObjectWriter.ToBytes(opened[2])));
        Assert.Contains("by scanning", Assert.Single(opened.Log.ToArray()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectThatIsNowhereIsNull()
    {
        FileBuilder file = Basic();
        long xref = file.Position;
        file.Raw($"xref\n0 1\n0000000000 65535 f\r\n9 1\n{file.Offsets[2]:D10} 00000 n\r\ntrailer << /Size 10 >>\n").StartXref(xref);

        Opened opened = Open(file.ToArray());

        Assert.Same(PdfNull.Instance, opened[9]);
        Assert.Equal([PdfWarningCode.MissingObject], opened.Warnings);
        Assert.Throws<PdfException>(() => Open(file.ToArray(), strict: true)[9]);
    }

    [Fact]
    public void ReferenceWithAnotherGenerationIsNull()
    {
        FileBuilder file = new FileBuilder().Object(1, "42", generation: 3);
        file.StartXref(file.XrefTable("/Size 2"));

        Opened opened = Open(file.ToArray());

        Assert.Equal(42, Assert.IsType<PdfInteger>(opened.Loader.Load(1, 3)).Value);
        Assert.Same(PdfNull.Instance, opened.Loader.Load(1, 0));
    }

    [Fact]
    public void SelfReferencingLengthDoesNotLoop()
    {
        FileBuilder file = new FileBuilder().Raw("1 0 obj << /Length 1 0 R >> stream\nabc\nendstream endobj\n");
        long xref = file.Position;
        file.Raw($"xref\n0 2\n0000000000 65535 f\r\n{"%PDF-1.7\n%âãÏÓ\n".Length:D10} 00000 n\r\ntrailer << /Size 2 >>\n").StartXref(xref);

        Opened opened = Open(file.ToArray());

        Assert.Equal("abc"u8.ToArray(), Assert.IsType<PdfStream>(opened[1]).GetEncodedBytes());
        Assert.Contains(PdfWarningCode.MissingObject, opened.Warnings);
        Assert.Contains(PdfWarningCode.WrongStreamLength, opened.Warnings);
    }

    [Fact]
    public void ObjectStreamEntryMustMatch()
    {
        FileBuilder file = new FileBuilder().ObjectStream(10, (2, "(two)"), (3, "(three)"));
        file.StartXref(file.XrefStream(11, "", compressed: [(2, 10, 1), (3, 10, 1), (4, 10, 7), (5, 12, 0)]));

        Opened opened = Open(file.ToArray());

        Assert.Same(PdfNull.Instance, opened[2]);
        Assert.Equal("(three)", Write(opened[3]));
        Assert.Same(PdfNull.Instance, opened[4]);
        Assert.Same(PdfNull.Instance, opened[5]);
        Assert.Equal(3, opened.Warnings.Count(w => w == PdfWarningCode.MissingObject));
    }

    [Fact]
    public void MalformedObjectStreamKeepsWhatItCan()
    {
        FileBuilder file = new FileBuilder()
            .Stream(10, "/Type /ObjStm /N 3 /First 8", "2 0 x 4 (two) 3 9"u8.ToArray());
        file.StartXref(file.XrefStream(11, "", compressed: [(2, 10, 0)]));

        Opened opened = Open(file.ToArray());

        Assert.Equal("(two)", Write(opened[2]));
        Assert.Contains(PdfWarningCode.MalformedObjectStream, opened.Warnings);
    }

    [Fact]
    public void StrictModeThrowsOnRepairs()
    {
        FileBuilder file = Basic();
        long at = file.Position;
        file.XrefTable($"/Size 5 /Prev {at}");
        file.StartXref(at);

        Assert.Equal(PdfWarningCode.BrokenXrefChain, Assert.Throws<PdfException>(() => Open(file.ToArray(), strict: true)).Code);
    }

    [Fact]
    public void CrossReferenceStreamIsAnObjectEvenWhenItsIndexLeavesItOut()
    {
        FileBuilder file = new FileBuilder().Object(1, "(one)");
        long at = file.XrefStream(2, "");
        byte[] bytes = file.StartXref(at).ToArray();
        string text = Encoding.Latin1.GetString(bytes);
        int index = text.IndexOf("/Index [0 3]", StringComparison.Ordinal);
        Encoding.Latin1.GetBytes("/Index [0 2]").CopyTo(bytes, index);

        Opened opened = Open(bytes);

        Assert.Equal("XRef", Assert.IsType<PdfStream>(opened[2]).Dictionary.GetName("Type")!.Value);
        Assert.Empty(opened.Warnings);
    }

    [Fact]
    public void CrossReferenceStreamIsAnObjectEvenWhenItsOwnEntryIsWrong()
    {
        // The stream lists itself as free; it is still the object at the offset it was read from, as qpdf reads it.
        FileBuilder file = new FileBuilder().Object(1, "(one)");
        long at = file.XrefStream(2, "", free: [2]);

        Opened opened = Open(file.StartXref(at).ToArray());

        Assert.IsType<PdfStream>(opened[2]);
    }

    [Fact]
    public void StreamWithoutTypeButWithWIsReadWithAWarning()
    {
        FileBuilder file = new FileBuilder().Object(1, "(one)");
        long at = file.XrefStream(2, "");
        byte[] bytes = file.StartXref(at).ToArray();
        string text = Encoding.Latin1.GetString(bytes);
        int type = text.IndexOf("/Type /XRef", StringComparison.Ordinal);
        Encoding.Latin1.GetBytes("/Tipe /XRef").CopyTo(bytes, type);

        Opened opened = Open(bytes);

        Assert.Equal("(one)", Write(opened[1]));
        Assert.Equal([PdfWarningCode.MalformedXref], opened.Warnings);
    }
}
