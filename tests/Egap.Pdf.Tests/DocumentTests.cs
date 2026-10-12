using System.Text;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests;

public sealed class DocumentTests
{
    private static byte[] Sample(int pages = 1, string header = "%PDF-1.4\n", string catalogExtra = "")
    {
        FileBuilder file = new(header);
        StringBuilder kids = new();
        for (int i = 0; i < pages; i++)
        {
            kids.Append(System.Globalization.CultureInfo.InvariantCulture, $"{10 + i} 0 R ");
        }

        file.Object(1, $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>")
            .Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>")
            .Object(3, "<< /Title (Sample) /Producer (Egap tests) >>");
        for (int i = 0; i < pages; i++)
        {
            file.Object(10 + i, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> >>");
        }

        file.Object(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return file.StartXref(file.XrefTable($"/Size {10 + pages} /Root 1 0 R /Info 3 0 R /ID [<00> <01>]")).ToArray();
    }

    [Fact]
    public void OpensFromEverySource()
    {
        byte[] bytes = Sample();
        string path = Path.Combine(Path.GetTempPath(), $"egap-doc-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        try
        {
            using PdfDocument fromPath = PdfDocument.Open(path);
            using PdfDocument fromStream = PdfDocument.Open(new MemoryStream(bytes));
            using PdfDocument fromMemory = PdfDocument.Open(bytes);
            foreach (PdfDocument pdf in new[] { fromPath, fromStream, fromMemory })
            {
                Assert.Equal("Catalog", pdf.Catalog.GetName("Type")!.Value);
                Assert.Equal("1.4", pdf.Version);
                Assert.Equal("Sample", pdf.Trailer.Get<PdfDictionary>("Info")!.GetText("Title"));
                Assert.Empty(pdf.Warnings);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpensAsyncFromANonSeekableStream()
    {
        await using NonSeekableStream stream = new(Sample());

        using PdfDocument pdf = await PdfDocument.OpenAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, pdf.Catalog.Get<PdfDictionary>("Pages")!.GetInt("Count"));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public void OpeningReadsOnlyTheCrossReference()
    {
        CountingStream stream = new(Sample(pages: 5000));

        using PdfDocument pdf = PdfDocument.Open(stream);
        long afterOpen = stream.BytesRead;
        PdfDictionary catalog = pdf.Catalog;
        long afterCatalog = stream.BytesRead;
        foreach (PdfReference reference in pdf.Objects)
        {
            _ = pdf.Objects[reference];
        }

        Assert.True(afterCatalog < stream.Length / 2, $"{afterCatalog} of {stream.Length} bytes read for the catalog.");
        Assert.True(afterOpen <= afterCatalog);
        Assert.Equal(5004, pdf.Objects.Count);
        Assert.Equal("Catalog", catalog.GetName("Type")!.Value);
    }

    [Fact]
    public void ReferencesResolveToTheSameInstance()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());

        PdfDictionary pages = pdf.Catalog.Get<PdfDictionary>("Pages")!;
        PdfDictionary page = pages.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!;

        Assert.Same(pages, page.Get("Parent"));
        Assert.Same(pages, pdf.Objects.Get(2));
        Assert.Same(pages, pdf.Objects[new PdfReference(2, 0)]);
        Assert.Same(PdfNull.Instance, pdf.Objects[new PdfReference(2, 1)]);
        Assert.Same(PdfNull.Instance, pdf.Objects.Get(999));
    }

    [Fact]
    public void ReadingChangesNothing()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());

        foreach (PdfReference reference in pdf.Objects)
        {
            _ = pdf.Objects[reference].ToString();
        }

        Assert.False(pdf.IsChanged);
        Assert.All(pdf.Objects.Entries, e => Assert.Equal(EntryState.Unchanged, e.State));
    }

    [Fact]
    public void EditingANestedValueMarksItsObjectChanged()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());
        PdfDictionary page = pdf.Objects.Get(10) as PdfDictionary ?? throw new InvalidOperationException();

        page.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Font")!["F2"] = new PdfReference(4, 0);

        Assert.True(pdf.IsChanged);
        Assert.Equal([10], pdf.Objects.Entries.Where(e => e.State == EntryState.Changed).Select(e => e.Number));
    }

    [Fact]
    public void EditingTheTrailerMarksTheDocumentChanged()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());

        pdf.Trailer.Remove("Info");

        Assert.True(pdf.IsChanged);
        Assert.False(pdf.Objects.IsChanged);
    }

    [Fact]
    public void AddsSetsAndRemovesObjects()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());

        PdfReference added = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Example") });
        pdf.Catalog["Example"] = added;
        Assert.Equal(11, added.ObjectNumber);
        Assert.Equal("Example", pdf.Catalog.Get<PdfDictionary>("Example")!.GetName("Type")!.Value);

        pdf.Objects.Set(new PdfReference(3, 0), new PdfDictionary { ["Title"] = PdfString.FromText("New") });
        Assert.Equal("New", pdf.Trailer.Get<PdfDictionary>("Info")!.GetText("Title"));

        Assert.True(pdf.Objects.Remove(new PdfReference(4, 0)));
        Assert.Same(PdfNull.Instance, pdf.Objects.Get(4));
        Assert.False(pdf.Objects.Contains(new PdfReference(4, 0)));
        Assert.False(pdf.Objects.Remove(new PdfReference(4, 0)));

        Dictionary<int, EntryState> states = pdf.Objects.Entries.ToDictionary(e => e.Number, e => e.State);
        Assert.Equal(EntryState.Changed, states[1]);
        Assert.Equal(EntryState.Changed, states[3]);
        Assert.Equal(EntryState.Deleted, states[4]);
        Assert.Equal(EntryState.New, states[11]);
        Assert.Equal(EntryState.Unchanged, states[2]);
        Assert.Equal(5, pdf.Objects.Count);
    }

    [Fact]
    public void ObjectsFollowTheOnePlaceRule()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());
        PdfDictionary pages = pdf.Catalog.Get<PdfDictionary>("Pages")!;

        Assert.Throws<InvalidOperationException>(() => pdf.Objects.Add(pages));
        Assert.Throws<InvalidOperationException>(() => pdf.Catalog["Copy"] = pages);
        Assert.Throws<ArgumentException>(() => pdf.Objects.Add(new PdfReference(1, 0)));

        PdfReference copy = pdf.Objects.Add(pages.Clone());
        Assert.NotSame(pages, pdf.Objects[copy]);
    }

    [Fact]
    public void RemovingANewObjectForgetsIt()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());
        PdfDictionary value = [];
        PdfReference added = pdf.Objects.Add(value);

        Assert.True(pdf.Objects.Remove(added));
        Assert.False(pdf.Objects.IsChanged);

        // The removed value belongs nowhere again.
        pdf.Objects.Add(value);
    }

    [Fact]
    public void NewObjectsFitWhenADamagedFileUsesTheTopNumber()
    {
        FileBuilder file = new FileBuilder().Object(1, "<< /Type /Catalog >>");
        long xref = file.Position;
        file.Raw($"xref\n0 2\n0000000000 65535 f\r\n{file.Offsets[1]:D10} 00000 n\r\n2147483647 1\n0000000000 00001 f\r\ntrailer << /Size 2147483647 /Root 1 0 R >>\n").StartXref(xref);
        using PdfDocument pdf = PdfDocument.Open(file.ToArray());

        Assert.Equal(2, pdf.Objects.Add(PdfNull.Instance).ObjectNumber);
        Assert.Equal(3, pdf.Objects.Add(PdfNull.Instance).ObjectNumber);
    }

    [Fact]
    public void SetOnAMissingObjectThrows()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());

        Assert.Throws<KeyNotFoundException>(() => pdf.Objects.Set(new PdfReference(99, 0), PdfNull.Instance));
    }

    [Fact]
    public void CatalogVersionWinsWhenHigher()
    {
        using PdfDocument higher = PdfDocument.Open(Sample(catalogExtra: "/Version /1.7"));
        using PdfDocument lower = PdfDocument.Open(Sample(catalogExtra: "/Version /1.2"));

        Assert.Equal("1.7", higher.Version);
        Assert.Equal("1.4", lower.Version);
    }

    [Fact]
    public void MissingHeaderIsAWarning()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample(header: "junk\n"));

        Assert.Equal("1.7", pdf.Version);
        Assert.Equal(PdfWarningCode.MissingHeader, Assert.Single(pdf.Warnings).Code);
        Assert.Throws<PdfException>(() => PdfDocument.Open(Sample(header: "junk\n"), new PdfReadOptions { Strict = true }));
    }

    [Fact]
    public void ReadsOffsetsThatCountFromALateHeader()
    {
        byte[] pdf = Sample();
        byte[] shifted = [.. "junk before the header\n"u8, .. pdf];

        using PdfDocument document = PdfDocument.Open(shifted);

        Assert.Equal("Catalog", document.Catalog.GetName("Type")!.Value);
        Assert.Equal(PdfWarningCode.ShiftedOffsets, Assert.Single(document.Warnings).Code);
    }

    [Fact]
    public void FileWithoutACatalogThrows()
    {
        FileBuilder file = new FileBuilder().Object(1, "<< >>");
        byte[] bytes = file.StartXref(file.XrefTable("/Size 2")).ToArray();

        Assert.Throws<PdfException>(() => PdfDocument.Open(bytes));
    }

    [Fact]
    public void WarningsGrowAsObjectsLoad()
    {
        FileBuilder file = new FileBuilder("%PDF-1.7\n").Object(1, "<< /Type /Catalog /Bad 1.2.3 >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 2 /Root 1 0 R")).ToArray());

        // Opening loads the catalog to check it, which finds the malformed number.
        Assert.Contains(pdf.Warnings, w => w.ObjectNumber == 1);
    }

    [Fact]
    public void DisposeClosesTheStream()
    {
        MemoryStream stream = new(Sample());
        PdfDocument.Open(stream).Dispose();
        Assert.False(stream.CanRead);

        MemoryStream kept = new(Sample());
        PdfDocument.Open(kept, leaveOpen: true).Dispose();
        Assert.True(kept.CanRead);
    }

    [Fact]
    public void FailedOpenClosesTheStream()
    {
        MemoryStream stream = new("not a pdf"u8.ToArray());

        Assert.Throws<PdfException>(() => PdfDocument.Open(stream));
        Assert.False(stream.CanRead);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        public override bool CanSeek => false;

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
    }

    [Fact]
    public void AFileThatIsNotEncryptedHasEveryPermission()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());

        Assert.False(pdf.IsEncrypted);
        Assert.True(pdf.IsOwner);
        Assert.Equal(PdfPermissions.All, pdf.Permissions);
    }

    [Theory]
    [InlineData(PdfCompression.Compact, true)]
    [InlineData(PdfCompression.None, false)]
    public void ACompressionChangeWritesTheWholeFileEvenInIncrementalMode(PdfCompression compression, bool xrefStream)
    {
        byte[] input = Sample();
        using PdfDocument pdf = PdfDocument.Open(input);
        using MemoryStream output = new();

        pdf.Save(output, new PdfSaveOptions { Compression = compression });

        Assert.NotEqual(input, output.ToArray()[..Math.Min(input.Length, (int)output.Length)]);
        using PdfDocument saved = PdfDocument.Open(output.ToArray(), new PdfReadOptions { Strict = true });
        Assert.Single(saved.Xref.Sections);
        Assert.Equal(xrefStream, saved.Xref.Sections[0].IsStream);
    }
}
