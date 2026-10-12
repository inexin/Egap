using System.Text;

using Egap.Pdf.Objects;
using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Writing;

public sealed class RewriteTests
{
    private static readonly PdfSaveOptions Rewrite = new() { Mode = PdfSaveMode.Rewrite, Deterministic = true };

    private static byte[] Sample()
    {
        FileBuilder file = new FileBuilder("%PDF-1.5\n")
            .Object(7, "<< /Type /Catalog /Pages 3 0 R >>")
            .Object(3, "<< /Type /Pages /Kids [9 0 R] /Count 1 >>")
            .Object(9, "<< /Type /Page /Parent 3 0 R /Contents 12 0 R /Missing 40 0 R >>")
            .Raw("12 0 obj\n<< /Length 13 0 R >>\nstream\nBT (Hi) Tj ET\nendstream\nendobj\n13 0 obj 13 endobj\n")
            .Object(20, "<< /Title (Sample) >>")
            .Object(30, "(nobody refers to me)");
        string text = Encoding.Latin1.GetString(file.ToArray());
        file.Offsets[12] = text.IndexOf("12 0 obj", StringComparison.Ordinal);
        file.Offsets[13] = text.IndexOf("13 0 obj", StringComparison.Ordinal);
        long xref = file.Position;
        StringBuilder table = new("xref\n0 1\n0000000000 65535 f\r\n");
        foreach (int number in file.Offsets.Keys.Order())
        {
            table.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} 1\n{file.Offsets[number]:D10} 00000 n\r\n");
        }

        return file.Raw(table + "trailer\n<< /Size 31 /Root 7 0 R /Info 20 0 R /ID [<AABB> <CCDD>] >>\n").StartXref(xref).ToArray();
    }

    private static byte[] Save(PdfDocument pdf, PdfSaveOptions? options = null)
    {
        using MemoryStream output = new();
        pdf.Save(output, options ?? Rewrite);
        return output.ToArray();
    }

    /// <summary>Whether two documents hold the same objects reachable from their trailers, whatever their numbers.</summary>
    private static void AssertSameGraph(PdfDocument expected, PdfDocument actual)
    {
        Dictionary<(int, int), (int, int)> seen = [];
        Stack<(PdfObject, PdfObject, string)> pending = new();
        foreach (string key in new[] { "Root", "Info" })
        {
            pending.Push((expected.Trailer[key] ?? PdfNull.Instance, actual.Trailer[key] ?? PdfNull.Instance, key));
        }

        while (pending.TryPop(out (PdfObject A, PdfObject B, string Path) item))
        {
            (PdfObject a, PdfObject b, string path) = item;
            if (a is PdfReference ra)
            {
                PdfObject target = ra.Resolve();
                if (target is PdfNull)
                {
                    Assert.True(b is PdfNull || b.Resolve() is PdfNull, $"{path}: {b} for a missing object");
                    continue;
                }

                PdfReference rb = Assert.IsType<PdfReference>(b);
                if (seen.TryGetValue((ra.ObjectNumber, ra.Generation), out (int, int) mapped))
                {
                    Assert.Equal(mapped, (rb.ObjectNumber, rb.Generation));
                    continue;
                }

                seen[(ra.ObjectNumber, ra.Generation)] = (rb.ObjectNumber, rb.Generation);
                pending.Push((target, rb.Resolve(), path + "*"));
                continue;
            }

            switch (a)
            {
                case PdfArray arrayA:
                    PdfArray arrayB = Assert.IsType<PdfArray>(b);
                    Assert.Equal(arrayA.Count, arrayB.Count);
                    for (int i = 0; i < arrayA.Count; i++)
                    {
                        pending.Push((arrayA[i], arrayB[i], $"{path}[{i}]"));
                    }

                    break;
                case PdfDictionary dictionaryA:
                    CompareDictionaries(dictionaryA, Assert.IsType<PdfDictionary>(b), path, pending, skipLength: false);
                    break;
                case PdfStream streamA:
                    PdfStream streamB = Assert.IsType<PdfStream>(b);
                    Assert.Equal(streamA.GetEncodedBytes(), streamB.GetEncodedBytes());
                    CompareDictionaries(streamA.Dictionary, streamB.Dictionary, path, pending, skipLength: true);
                    break;
                default:
                    Assert.Equal(Encoding.Latin1.GetString(ObjectWriter.ToBytes(a)), Encoding.Latin1.GetString(ObjectWriter.ToBytes(b)));
                    break;
            }
        }
    }

    private static void CompareDictionaries(PdfDictionary a, PdfDictionary b, string path, Stack<(PdfObject, PdfObject, string)> pending, bool skipLength)
    {
        Assert.Equal(a.Keys.Where(k => !(skipLength && k.Value == "Length")), b.Keys.Where(k => !(skipLength && k.Value == "Length")));
        foreach (PdfName key in a.Keys.Where(k => !(skipLength && k.Value == "Length")))
        {
            pending.Push((a[key]!, b[key]!, $"{path}/{key.Value}"));
        }
    }

    [Fact]
    public void RewriteReadsBackStrictlyWithTheSameObjects()
    {
        using PdfDocument original = PdfDocument.Open(Sample());

        using PdfDocument rewritten = PdfDocument.Open(Save(original), new PdfReadOptions { Strict = true });

        Assert.Empty(rewritten.Warnings);
        AssertSameGraph(original, rewritten);
        Assert.Equal("1.5", rewritten.Version);
    }

    [Fact]
    public void ObjectsAreRenumberedDenselyInReachOrder()
    {
        using PdfDocument original = PdfDocument.Open(Sample());

        using PdfDocument rewritten = PdfDocument.Open(Save(original));

        // Catalog, info, pages, page, contents: the unused object, the indirect length and the missing object are gone.
        Assert.Equal([1, 2, 3, 4, 5], rewritten.Objects.Select(r => r.ObjectNumber));
        Assert.Equal(1, Assert.IsType<PdfReference>(rewritten.Trailer["Root"]).ObjectNumber);
        Assert.Equal(2, Assert.IsType<PdfReference>(rewritten.Trailer["Info"]).ObjectNumber);
        Assert.Equal(6, rewritten.Trailer.GetInt("Size"));
        PdfDictionary page = Assert.IsType<PdfDictionary>(rewritten.Objects.Get(4));
        Assert.Same(PdfNull.Instance, page["Missing"]);
        Assert.Equal(13, Assert.IsType<PdfStream>(rewritten.Objects.Get(5)).Dictionary["Length"] is PdfInteger { Value: 13 } ? 13 : 0);
    }

    [Fact]
    public void KeepsNumbersAndUnusedObjectsWhenAsked()
    {
        using PdfDocument original = PdfDocument.Open(Sample());

        using PdfDocument rewritten = PdfDocument.Open(Save(original, Rewrite with { KeepObjectNumbers = true, RemoveUnused = false }), new PdfReadOptions { Strict = true });

        Assert.Equal([3, 7, 9, 12, 13, 20, 30], rewritten.Objects.Select(r => r.ObjectNumber));
        Assert.Equal(7, Assert.IsType<PdfReference>(rewritten.Trailer["Root"]).ObjectNumber);
        AssertSameGraph(original, rewritten);
    }

    [Fact]
    public void DeterministicSavesAreIdenticalAndKeepTheFirstId()
    {
        using PdfDocument original = PdfDocument.Open(Sample());

        byte[] first = Save(original);
        byte[] second = Save(original);
        Assert.Equal(first, second);

        using PdfDocument rewritten = PdfDocument.Open(first);
        PdfArray id = rewritten.Trailer.Get<PdfArray>("ID")!;
        Assert.Equal([0xAA, 0xBB], id.Get<PdfString>(0)!.Bytes.ToArray());
        Assert.Equal(16, id.Get<PdfString>(1)!.Bytes.Length);
    }

    [Fact]
    public void OrdinarySavesGetANewSecondId()
    {
        using PdfDocument original = PdfDocument.Open(Sample());
        PdfSaveOptions options = Rewrite with { Deterministic = false };

        using PdfDocument first = PdfDocument.Open(Save(original, options));
        using PdfDocument second = PdfDocument.Open(Save(original, options));

        Assert.NotEqual(first.Trailer.Get<PdfArray>("ID")!.Get<PdfString>(1), second.Trailer.Get<PdfArray>("ID")!.Get<PdfString>(1));
    }

    [Fact]
    public void FileWithoutAnIdGetsOne()
    {
        FileBuilder file = new FileBuilder().Object(1, "<< /Type /Catalog >>");
        using PdfDocument original = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 2 /Root 1 0 R")).ToArray());

        using PdfDocument rewritten = PdfDocument.Open(Save(original));

        PdfArray id = rewritten.Trailer.Get<PdfArray>("ID")!;
        Assert.Equal(id.Get<PdfString>(0), id.Get<PdfString>(1));
    }

    [Fact]
    public void EditsAreWritten()
    {
        using PdfDocument original = PdfDocument.Open(Sample());
        original.Trailer.Get<PdfDictionary>("Info")!["Title"] = PdfString.FromText("Edited Ω");
        PdfReference added = original.Objects.Add(new PdfDictionary { ["Added"] = true });
        original.Catalog["Extra"] = added;
        PdfStream contents = (PdfStream)original.Objects.Get(12);
        contents.SetDecodedBytes("BT (Changed) Tj ET"u8);

        using PdfDocument rewritten = PdfDocument.Open(Save(original), new PdfReadOptions { Strict = true });

        Assert.Equal("Edited Ω", rewritten.Trailer.Get<PdfDictionary>("Info")!.GetText("Title"));
        Assert.True(rewritten.Catalog.Get<PdfDictionary>("Extra")!.GetBool("Added"));
        PdfStream page = rewritten.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!.Get<PdfStream>("Contents")!;
        Assert.Equal("BT (Changed) Tj ET"u8.ToArray(), page.GetDecodedBytes());
        AssertSameGraph(original, rewritten);
    }

    [Fact]
    public void RepairedFileRewritesClean()
    {
        byte[] good = Sample();
        byte[] damaged = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(good).Replace("startxref", "startxyz", StringComparison.Ordinal));
        using PdfDocument original = PdfDocument.Open(damaged);
        Assert.True(original.Rebuilt);

        using PdfDocument rewritten = PdfDocument.Open(Save(original), new PdfReadOptions { Strict = true });

        Assert.Empty(rewritten.Warnings);
        AssertSameGraph(original, rewritten);
    }

    [Fact]
    public void SavesOverTheFileItWasOpenedFrom()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-rewrite-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Sample());
        try
        {
            using (PdfDocument pdf = PdfDocument.Open(path))
            {
                pdf.Catalog["Saved"] = true;
                pdf.Save(path, Rewrite);

                // The open document still reads from what it opened.
                Assert.Equal("Sample", pdf.Trailer.Get<PdfDictionary>("Info")!.GetText("Title"));
            }

            using PdfDocument saved = PdfDocument.Open(path, new PdfReadOptions { Strict = true });
            Assert.True(saved.Catalog.GetBool("Saved"));
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $".{Path.GetFileName(path)}.*.tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SavesToANewFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-rewrite-{Guid.NewGuid():N}.pdf");
        try
        {
            using (PdfDocument pdf = PdfDocument.Open(Sample()))
            {
                pdf.Save(path, Rewrite);
            }

            using PdfDocument saved = PdfDocument.Open(path, new PdfReadOptions { Strict = true });
            Assert.Equal("Catalog", saved.Catalog.GetName("Type")!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EncryptedSavesAreNotSupportedYet()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample());

        pdf.Trailer["Encrypt"] = new PdfDictionary();
        Assert.Throws<NotSupportedException>(() => pdf.Save(new MemoryStream(), Rewrite));
        Assert.Throws<NotSupportedException>(() => pdf.Save(new MemoryStream()));
    }
}
