using System.Text;
using System.Text.RegularExpressions;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Editing the page tree in place (Phase 2 Q3): insert, remove, move and blank pages, Phase 2 task 4.</summary>
public sealed partial class PageEditTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    private static byte[] File(params (int Number, string Body)[] objects)
    {
        FileBuilder file = new();
        foreach ((int number, string body) in objects)
        {
            file.Object(number, body);
        }

        return file.StartXref(file.XrefTable($"/Size {objects.Max(o => o.Number) + 1} /Root 1 0 R")).ToArray();
    }

    /// <summary>Pages 4, 6 and 7; node 5 (pages 6 and 7) has Rotate 90 and a CropBox, the root a MediaBox and Resources.</summary>
    private static byte[] Nested()
    {
        return File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [4 0 R 5 0 R] /Count 3 /Resources << /ProcSet [/PDF] >> /MediaBox [0 0 600 800] >>"),
            (4, "<< /Type /Page /Parent 2 0 R >>"),
            (5, "<< /Type /Pages /Parent 2 0 R /Kids [6 0 R 7 0 R] /Count 2 /CropBox [10 10 590 790] /Rotate 90 >>"),
            (6, "<< /Type /Page /Parent 5 0 R >>"),
            (7, "<< /Type /Page /Parent 5 0 R /MediaBox [0 0 300 300] >>"));
    }

    private static byte[] Save(PdfDocument pdf, PdfSaveOptions? options = null)
    {
        using MemoryStream output = new();
        pdf.Save(output, options);
        return output.ToArray();
    }

    /// <summary>
    /// Saves, reopens strictly (any repair would throw, so /Count, /Parent and /Type must all be right) and checks no node
    /// holds more than 128 kids. Returns the page object numbers in order.
    /// </summary>
    private static List<int> CheckWellFormed(PdfDocument pdf)
    {
        using PdfDocument reopened = PdfDocument.Open(Save(pdf, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite }), Strict);
        Stack<PdfDictionary> pending = new([reopened.Catalog.Get<PdfDictionary>("Pages")!]);
        while (pending.TryPop(out PdfDictionary? node))
        {
            if (node.Get<PdfArray>("Kids") is PdfArray kids)
            {
                Assert.InRange(kids.Count, 0, 128);
                Assert.All(kids, k => Assert.IsType<PdfReference>(k));
                foreach (PdfObject kid in kids)
                {
                    pending.Push((PdfDictionary)kid.Resolve());
                }
            }
        }

        Assert.Equal(pdf.Pages.Count, reopened.Pages.Count);
        Assert.Empty(reopened.Warnings);
        return [.. pdf.Pages.Select(p => p.Reference!.ObjectNumber)];
    }

    [Fact]
    public void InsertsBlankPagesAnywhere()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested(), Strict);

        PdfPage first = pdf.Pages.InsertBlank(0);
        PdfPage middle = pdf.Pages.InsertBlank(2, new PdfRectangle(0, 0, 100, 200));
        PdfPage last = pdf.Pages.AddBlank();

        Assert.Equal([first, pdf.Pages[1], middle, pdf.Pages[3], pdf.Pages[4], last], pdf.Pages);
        Assert.Equal([4, 6, 7], (int[])[pdf.Pages[1].Reference!.ObjectNumber, pdf.Pages[3].Reference!.ObjectNumber, pdf.Pages[4].Reference!.ObjectNumber]);
        Assert.Equal(new PdfRectangle(0, 0, 600, 800), first.MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 100, 200), middle.MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), last.MediaBox);
        Assert.Equal(0, middle.Resources!.Count);
        CheckWellFormed(pdf);
    }

    [Fact]
    public void ABlankPageDoesNotInheritRotationOrCropping()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested(), Strict);

        PdfPage page = pdf.Pages.InsertBlank(2, new PdfRectangle(0, 0, 50, 50));

        Assert.Equal(5, ((PdfReference)page.Dictionary["Parent"]!).ObjectNumber);
        Assert.Equal(0, page.Rotate);
        Assert.Equal(new PdfRectangle(0, 0, 50, 50), page.CropBox);
        Assert.Equal(90, pdf.Pages[1].Rotate);
    }

    [Fact]
    public void ANewDocumentGetsPages()
    {
        using PdfDocument pdf = PdfDocument.Create();

        pdf.Pages.AddBlank();
        pdf.Pages.AddBlank(new PdfRectangle(0, 0, 595, 842));

        Assert.Equal(2, pdf.Pages.Count);
        Assert.Equal(PdfPage.DefaultMediaBox, pdf.Pages[0].MediaBox);
        Assert.Equal(2, CheckWellFormed(pdf).Count);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1000)]
    [InlineData(20000)]
    public void SplitsNodesPast128Kids(int count)
    {
        using PdfDocument pdf = PdfDocument.Create();
        for (int i = 0; i < count; i++)
        {
            // Every other page goes to the front, so splits happen at both ends.
            pdf.Pages.InsertBlank(i % 2 == 0 ? pdf.Pages.Count : 0, new PdfRectangle(0, 0, i + 1, 10));
        }

        Assert.Equal(count, pdf.Pages.Count);
        List<int> order = CheckWellFormed(pdf);
        Assert.Equal(count, order.Distinct().Count());
        Assert.Equal(count % 2 == 0 ? count : count - 1, pdf.Pages[0].MediaBox.Width);
    }

    [Fact]
    public void RemovesPagesAndEmptyNodes()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested(), Strict);

        pdf.Pages.RemoveAt(1);
        Assert.True(pdf.Pages.Remove(pdf.Pages[1]));
        using (PdfDocument other = PdfDocument.Open(Nested()))
        {
            Assert.False(pdf.Pages.Remove(other.Pages[0]));
        }

        Assert.Equal([4], CheckWellFormed(pdf));
        Assert.Single(pdf.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!);

        pdf.Pages.RemoveAt(0);
        Assert.Empty(pdf.Pages);
        Assert.Empty(CheckWellFormed(pdf));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages.RemoveAt(0));
    }

    [Fact]
    public void RemovesManyPagesInAnyOrder()
    {
        using PdfDocument pdf = PdfDocument.Create();
        for (int i = 0; i < 3000; i++)
        {
            pdf.Pages.AddBlank(new PdfRectangle(0, 0, i + 1, 10));
        }

        Random random = new(3);
        while (pdf.Pages.Count > 10)
        {
            pdf.Pages.RemoveAt(random.Next(pdf.Pages.Count));
        }

        List<double> widths = [.. pdf.Pages.Select(p => p.MediaBox.Width)];
        Assert.Equal(widths.Order(), widths);
        Assert.Equal(10, CheckWellFormed(pdf).Count);
    }

    [Fact]
    public void MovesPagesAndTheyKeepWhatTheyInherited()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested(), Strict);
        PdfPage rotated = pdf.Pages[1];

        pdf.Pages.Move(1, 0);
        pdf.Pages.Move(0, 2);
        pdf.Pages.Move(2, 0);

        Assert.Equal([6, 4, 7], pdf.Pages.Select(p => p.Reference!.ObjectNumber));
        Assert.Equal(rotated, pdf.Pages[0]);
        Assert.Equal((90, new PdfRectangle(0, 0, 600, 800), new PdfRectangle(10, 10, 590, 790)), (rotated.Rotate, rotated.MediaBox, rotated.CropBox));
        Assert.NotNull(rotated.Resources!["ProcSet"]);
        Assert.Equal((0, new PdfRectangle(0, 0, 600, 800)), (pdf.Pages[1].Rotate, pdf.Pages[1].CropBox));
        CheckWellFormed(pdf);
    }

    [Fact]
    public void MovesWithinALargeTree()
    {
        using PdfDocument pdf = PdfDocument.Create();
        for (int i = 0; i < 500; i++)
        {
            pdf.Pages.AddBlank(new PdfRectangle(0, 0, i + 1, 10));
        }

        Random random = new(5);
        List<double> expected = [.. pdf.Pages.Select(p => p.MediaBox.Width)];
        for (int i = 0; i < 300; i++)
        {
            int from = random.Next(500);
            int to = random.Next(500);
            pdf.Pages.Move(from, to);
            double width = expected[from];
            expected.RemoveAt(from);
            expected.Insert(to, width);
        }

        Assert.Equal(expected, pdf.Pages.Select(p => p.MediaBox.Width));
        Assert.Equal(500, CheckWellFormed(pdf).Count);
    }

    [Fact]
    public void AnIncrementalEditWritesOnlyThePath()
    {
        using PdfDocument built = PdfDocument.Create();
        for (int i = 0; i < 2000; i++)
        {
            built.Pages.AddBlank();
        }

        byte[] original = Save(built);
        using PdfDocument pdf = PdfDocument.Open(original, Strict);

        pdf.Pages.InsertBlank(1000);
        pdf.Pages.RemoveAt(10);
        pdf.Pages[1500].Rotate = 90;
        byte[] updated = Save(pdf);

        // The new page, its node and the root for the insert, a node for the remove, and the rotated page.
        Assert.Equal(original, updated.AsSpan(0, original.Length).ToArray());
        string appended = Encoding.Latin1.GetString(updated.AsSpan(original.Length));
        Assert.InRange(ObjectHeader().Count(appended), 4, 7);

        using PdfDocument reopened = PdfDocument.Open(updated, Strict);
        Assert.Equal(2000, reopened.Pages.Count);
        Assert.Equal(90, reopened.Pages[1500].Rotate);
    }

    [Fact]
    public void ABrokenTreeIsRebuiltBeforeAnEdit()
    {
        // Page 4 is listed twice, a kid is null, and node 5 loops back to the root.
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 4 0 R 5 0 R] /Count 4 /MediaBox [0 0 200 200] >>"),
            (3, "null"),
            (4, "<< /Type /Page /Parent 2 0 R >>"),
            (5, "<< /Type /Pages /Parent 2 0 R /Kids [6 0 R 2 0 R] /Count 1 /Rotate 180 >>"),
            (6, "<< /Type /Page /Parent 5 0 R >>")));
        Assert.Equal([4, 4, 6], pdf.Pages.Select(p => p.Reference!.ObjectNumber));

        pdf.Pages.AddBlank();

        Assert.Equal(4, pdf.Pages.Count);
        Assert.Equal(4, pdf.Pages.Select(p => p.Dictionary).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(180, pdf.Pages[2].Rotate);
        Assert.Equal(new PdfRectangle(0, 0, 200, 200), pdf.Pages[1].MediaBox);
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.MalformedPageTree && w.Message.Contains("rebuilt", StringComparison.Ordinal));
        Assert.Equal(4, CheckWellFormed(pdf).Count);
    }

    [Fact]
    public void AFileWithoutAPageTreeGetsOneWhenEdited()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog >>"),
            (2, "<< /Type /Page /MediaBox [0 0 10 10] >>")));

        pdf.Pages.InsertBlank(0);

        Assert.Equal(2, CheckWellFormed(pdf).Count);
        Assert.Equal(new PdfRectangle(0, 0, 10, 10), pdf.Pages[1].MediaBox);
    }

    [Fact]
    public void AParentOutsideTheTreeIsReplacedByTheNodeThatListsThePage()
    {
        // Page 6 is listed by the root but names node 5, which is not in the tree, as its parent (found by fuzzing).
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [4 0 R 6 0 R] /Count 2 >>"),
            (4, "<< /Type /Page /Parent 2 0 R >>"),
            (5, "<< /Type /Pages /Kids [6 0 R] /Count 1 /Rotate 180 >>"),
            (6, "<< /Type /Page /Parent 5 0 R >>")));

        Assert.Equal(0, pdf.Pages[1].Rotate);
        pdf.Pages.InsertBlank(2);

        Assert.Equal(3, CheckWellFormed(pdf).Count);
    }

    [Fact]
    public void AMovedPageKeepsWhatItInheritedPastADanglingValue()
    {
        // Page 6's own /Rotate points at a missing object, so it inherits node 5's 180 (found by fuzzing).
        using PdfDocument pdf = PdfDocument.Open(File(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [4 0 R 5 0 R] /Count 2 >>"),
            (4, "<< /Type /Page /Parent 2 0 R >>"),
            (5, "<< /Type /Pages /Parent 2 0 R /Kids [6 0 R] /Count 1 /Rotate 180 >>"),
            (6, "<< /Type /Page /Parent 5 0 R /Rotate 99 0 R >>")));
        Assert.Equal(180, pdf.Pages[1].Rotate);

        pdf.Pages.Move(1, 0);

        Assert.Equal(180, pdf.Pages[0].Rotate);
        CheckWellFormed(pdf);
    }

    [Fact]
    public void RejectsBadArguments()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested());

        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages.InsertBlank(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages.InsertBlank(-1));
        Assert.Throws<ArgumentException>(() => pdf.Pages.AddBlank(new PdfRectangle(0, 0, 0, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages.Move(0, 3));
        Assert.False(pdf.IsChanged);
    }

    [GeneratedRegex(@"\d+ 0 obj")]
    private static partial Regex ObjectHeader();
}
