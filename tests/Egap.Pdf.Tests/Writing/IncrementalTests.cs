using System.Text;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Writing;

public sealed class IncrementalTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    private static byte[] Classic()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>")
            .Stream(4, "", "BT (Hi) Tj ET"u8.ToArray())
            .Object(5, "<< /Title (Original) >>");
        return file.StartXref(file.XrefTable("/Size 6 /Root 1 0 R /Info 5 0 R /ID [<0102> <0304>]")).ToArray();
    }

    private static byte[] Compressed()
    {
        FileBuilder file = new FileBuilder("%PDF-1.6\n")
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .ObjectStream(10, (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), (3, "<< /Type /Page /Parent 2 0 R >>"));
        return file.StartXref(file.XrefStream(11, "/Root 1 0 R", compressed: [(2, 10, 0), (3, 10, 1)])).ToArray();
    }

    private static byte[] Save(PdfDocument pdf, PdfSaveOptions? options = null)
    {
        using MemoryStream output = new();
        pdf.Save(output, options);
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingWithoutEditsIsByteIdentical(bool compressed)
    {
        byte[] original = compressed ? Compressed() : Classic();
        using PdfDocument pdf = PdfDocument.Open(original);
        foreach (PdfReference reference in pdf.Objects)
        {
            _ = pdf.Objects[reference].ToString();
        }

        Assert.Equal(original, Save(pdf));
    }

    [Fact]
    public void EditsAreAppendedAfterTheOriginalBytes()
    {
        byte[] original = Classic();
        using PdfDocument pdf = PdfDocument.Open(original);
        pdf.Trailer.Get<PdfDictionary>("Info")!["Title"] = PdfString.FromText("Edited");
        PdfReference added = pdf.Objects.Add(new PdfDictionary { ["New"] = true });
        pdf.Catalog["Added"] = added;
        pdf.Objects.Remove(new PdfReference(4, 0));

        byte[] saved = Save(pdf, new PdfSaveOptions { Deterministic = true });

        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
        using PdfDocument reopened = PdfDocument.Open(saved, Strict);
        Assert.Empty(reopened.Warnings);
        Assert.Equal("Edited", reopened.Trailer.Get<PdfDictionary>("Info")!.GetText("Title"));
        Assert.True(reopened.Catalog.Get<PdfDictionary>("Added")!.GetBool("New"));
        Assert.Same(PdfNull.Instance, reopened.Objects.Get(4));
        Assert.Equal(2, reopened.Xref.Sections.Count);

        // Only the changed objects were written: the catalog (1), the info (5) and the new object (6), with 4 freed.
        string update = Encoding.Latin1.GetString(saved.AsSpan(original.Length));
        Assert.Contains("1 0 obj", update, StringComparison.Ordinal);
        Assert.Contains("5 0 obj", update, StringComparison.Ordinal);
        Assert.Contains("6 0 obj", update, StringComparison.Ordinal);
        Assert.DoesNotContain("2 0 obj", update, StringComparison.Ordinal);
        Assert.Contains("0000000000 00001 f", update, StringComparison.Ordinal);
        Assert.Contains("/Prev ", update, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateFollowsTheStyleOfTheNewestSection()
    {
        using PdfDocument pdf = PdfDocument.Open(Compressed());
        pdf.Catalog["Edited"] = true;

        byte[] saved = Save(pdf);

        using PdfDocument reopened = PdfDocument.Open(saved, Strict);
        Assert.True(reopened.Xref.Sections[0].IsStream);
        Assert.True(reopened.Catalog.GetBool("Edited"));
        Assert.Equal("Page", reopened.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!.GetName("Type")!.Value);
    }

    [Fact]
    public void ChangedObjectsFromObjectStreamsAreWrittenInTheUpdate()
    {
        using PdfDocument pdf = PdfDocument.Open(Compressed());
        ((PdfDictionary)pdf.Objects.Get(3))["Rotate"] = 90;

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);

        Assert.Equal(90, ((PdfDictionary)reopened.Objects.Get(3)).GetInt("Rotate"));
        Assert.True(reopened.Xref.TryGet(3, out Egap.Pdf.Parsing.XrefEntry entry));
        Assert.Equal(Egap.Pdf.Parsing.XrefEntryKind.InFile, entry.Kind);
    }

    [Fact]
    public void TrailerEditsAloneMakeAnUpdate()
    {
        byte[] original = Classic();
        using PdfDocument pdf = PdfDocument.Open(original);
        pdf.Trailer.Remove("Info");

        byte[] saved = Save(pdf);

        Assert.True(saved.Length > original.Length);
        using PdfDocument reopened = PdfDocument.Open(saved, Strict);
        Assert.False(reopened.Trailer.ContainsKey("Info"));
    }

    [Fact]
    public void IdKeepsItsFirstPartAndDeterministicSavesMatch()
    {
        using PdfDocument pdf = PdfDocument.Open(Classic());
        pdf.Catalog["X"] = 1;
        PdfSaveOptions deterministic = new() { Deterministic = true };

        byte[] first = Save(pdf, deterministic);
        Assert.Equal(first, Save(pdf, deterministic));

        using PdfDocument reopened = PdfDocument.Open(first);
        PdfArray id = reopened.Trailer.Get<PdfArray>("ID")!;
        Assert.Equal([1, 2], id.Get<PdfString>(0)!.Bytes.ToArray());
        Assert.NotEqual([3, 4], id.Get<PdfString>(1)!.Bytes.ToArray());
    }

    [Fact]
    public void AddsANewlineWhenTheFileDoesNotEndWithOne()
    {
        byte[] original = [.. Classic()[..^1]];
        using PdfDocument pdf = PdfDocument.Open(original);
        pdf.Catalog["X"] = 1;

        byte[] saved = Save(pdf);

        Assert.Equal((byte)'\n', saved[original.Length]);
        using PdfDocument reopened = PdfDocument.Open(saved, Strict);
        Assert.Equal(1, reopened.Catalog.GetInt("X"));
    }

    [Fact]
    public void RepairedFileIsWrittenWhole()
    {
        byte[] damaged = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(Classic()).Replace("startxref", "startxyz", StringComparison.Ordinal));
        using PdfDocument pdf = PdfDocument.Open(damaged);

        byte[] saved = Save(pdf);

        Assert.DoesNotContain("startxyz", Encoding.Latin1.GetString(saved), StringComparison.Ordinal);
        using PdfDocument reopened = PdfDocument.Open(saved, Strict);
        Assert.Empty(reopened.Warnings);
        Assert.Single(reopened.Xref.Sections);
    }

    [Fact]
    public void SavingOverTheOpenFileAppendsInPlace()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-incremental-{Guid.NewGuid():N}.pdf");
        byte[] original = Classic();
        File.WriteAllBytes(path, original);
        try
        {
            using (PdfDocument pdf = PdfDocument.Open(path))
            {
                pdf.Catalog["First"] = 1;
                pdf.Save(path);
                long afterFirst = new FileInfo(path).Length;

                // A second edit and save appends again, after the first update.
                pdf.Catalog["Second"] = 2;
                pdf.Objects.Remove(new PdfReference(5, 0));
                pdf.Save(path);
                Assert.True(new FileInfo(path).Length > afterFirst);

                // With nothing changed since, saving leaves the file alone.
                long final = new FileInfo(path).Length;
                pdf.Save(path);
                Assert.Equal(final, new FileInfo(path).Length);
                Assert.Equal(2, pdf.Catalog.GetInt("Second"));
            }

            byte[] saved = File.ReadAllBytes(path);
            Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
            using PdfDocument reopened = PdfDocument.Open(path, Strict);
            Assert.Empty(reopened.Warnings);
            Assert.Equal(3, reopened.Xref.Sections.Count);
            Assert.Equal(1, reopened.Catalog.GetInt("First"));
            Assert.Equal(2, reopened.Catalog.GetInt("Second"));
            Assert.Same(PdfNull.Instance, reopened.Trailer.Get("Info"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SavingToAnotherFileCopiesAndAppends()
    {
        string source = Path.Combine(Path.GetTempPath(), $"egap-incremental-{Guid.NewGuid():N}.pdf");
        string target = Path.Combine(Path.GetTempPath(), $"egap-incremental-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(source, Classic());
        try
        {
            using (PdfDocument pdf = PdfDocument.Open(source))
            {
                pdf.Catalog["Copied"] = true;
                pdf.Save(target);
            }

            Assert.Equal(Classic(), File.ReadAllBytes(source));
            using PdfDocument reopened = PdfDocument.Open(target, Strict);
            Assert.True(reopened.Catalog.GetBool("Copied"));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }

    [Fact]
    public void FileChangedByOthersIsReplacedNotAppended()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-incremental-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Classic());
        try
        {
            using (PdfDocument pdf = PdfDocument.Open(path))
            {
                using (FileStream other = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    other.Write("% someone else\n"u8);
                }

                pdf.Catalog["Mine"] = true;
                pdf.Save(path);
            }

            using PdfDocument reopened = PdfDocument.Open(path, Strict);
            Assert.True(reopened.Catalog.GetBool("Mine"));
            Assert.DoesNotContain("someone else", Encoding.Latin1.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Fuzzed(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "files", "fuzz", name);
    }

    [Fact]
    public void NewObjectsNeverTakeANumberAReferenceNames()
    {
        // Found by fuzzing: /Kids holds 7 0 R, past /Size 6, and the update's cross-reference stream once took number 7.
        byte[] original = File.ReadAllBytes(Fuzzed("incremental-dangling-reference.pdf"));
        using PdfDocument pdf = PdfDocument.Open(original);
        PdfReference added = pdf.Objects.Add(new PdfDictionary { ["Edited"] = true });
        pdf.Catalog["Added"] = added;

        using PdfDocument reopened = PdfDocument.Open(Save(pdf));

        Assert.True(added.ObjectNumber > 7);
        Assert.Same(PdfNull.Instance, reopened.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get(0));
        Assert.True(reopened.Catalog.Get<PdfDictionary>("Added")!.GetBool("Edited"));
    }

    [Fact]
    public void FileWithAStreamRunningToTheEndIsWrittenWhole()
    {
        // Found by fuzzing: the cross-reference stream has no endstream, so an update appended after it became its data.
        byte[] original = File.ReadAllBytes(Fuzzed("incremental-missing-endstream.pdf"));
        using PdfDocument pdf = PdfDocument.Open(original);
        pdf.Catalog["Edited"] = true;

        byte[] saved = Save(pdf);

        Assert.False(saved.AsSpan(0, original.Length).SequenceEqual(original));
        using PdfDocument reopened = PdfDocument.Open(saved, Strict);
        Assert.Equal("Page", reopened.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!.GetName("Type")!.Value);
    }

    [Fact]
    public void AStreamWithoutEndstreamKeepsItsDataAfterAnUpdate()
    {
        // The stream's /Length is wrong and it has neither endstream nor endobj: its data runs to the section's %%EOF, never
        // into what an update appends.
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>")
            .Unterminated(4, "<< /Length 99 >>\nstream\nBT (Hi) Tj ET\n");
        byte[] input = file.StartXref(file.XrefTable("/Size 5 /Root 1 0 R")).ToArray();
        using PdfDocument pdf = PdfDocument.Open(input);
        pdf.Catalog["Added"] = pdf.Objects.Add(new PdfDictionary { ["Edited"] = true });

        byte[] output = Save(pdf);

        Assert.Equal(input, output[..input.Length]);
        using PdfDocument reopened = PdfDocument.Open(output);
        byte[] before = Assert.IsType<PdfStream>(pdf.Objects.Get(4)).GetEncodedBytes();
        Assert.StartsWith("BT (Hi) Tj ET", Encoding.Latin1.GetString(before), StringComparison.Ordinal);
        Assert.Equal(before, Assert.IsType<PdfStream>(reopened.Objects.Get(4)).GetEncodedBytes());
    }

    [Fact]
    public void AnEditedFileWithoutAFinalEofIsWrittenWhole()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [] /Count 0 >>");
        byte[] input = file.StartXref(file.XrefTable("/Size 3 /Root 1 0 R")).Raw(new string('x', 2000)).ToArray();
        using PdfDocument pdf = PdfDocument.Open(input);
        pdf.Catalog["Added"] = pdf.Objects.Add(new PdfDictionary { ["Edited"] = true });

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);

        Assert.Single(reopened.Xref.Sections);
        Assert.True(reopened.Catalog.Get<PdfDictionary>("Added")!.GetBool("Edited"));
    }
}
