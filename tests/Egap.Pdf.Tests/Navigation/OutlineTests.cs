using System.Text;
using System.Text.RegularExpressions;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Navigation;

/// <summary>The document outline (12.3.3): reading, editing, counts and repairs; Phase 2 task 7.</summary>
public sealed partial class OutlineTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>Pages 3 and 4, the catalog entries given, and extra objects.</summary>
    private static byte[] File(string catalog, params (int Number, string Body)[] extra)
    {
        FileBuilder file = new FileBuilder()
            .Object(1, $"<< /Type /Catalog /Pages 2 0 R {catalog} >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>")
            .Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        foreach ((int number, string body) in extra)
        {
            file.Object(number, body);
        }

        return file.StartXref(file.XrefTable($"/Size {extra.Select(e => e.Number + 1).DefaultIfEmpty(5).Max()} /Root 1 0 R")).ToArray();
    }

    /// <summary>
    /// Root 10 with items 11 (open, children 13 and 14) and 12; 13 has child 15 and is closed. 11 goes to page 3, 12 runs a
    /// go-to action to the named destination "two", 13 is red and bold.
    /// </summary>
    private static byte[] Nested()
    {
        return File(
            "/Outlines 10 0 R /Dests << /two [4 0 R /Fit] >>",
            (10, "<< /Type /Outlines /First 11 0 R /Last 12 0 R /Count 4 >>"),
            (11, "<< /Title (One) /Parent 10 0 R /Next 12 0 R /First 13 0 R /Last 14 0 R /Count 2 /Dest [3 0 R /XYZ 0 100 null] >>"),
            (12, "<< /Title <FEFF0054007700F8> /Parent 10 0 R /Prev 11 0 R /A << /S /GoTo /D /two >> >>"),
            (13, "<< /Title (One.A) /Parent 11 0 R /Next 14 0 R /First 15 0 R /Last 15 0 R /Count -1 /C [1 0 0] /F 2 >>"),
            (14, "<< /Title (One.B) /Parent 11 0 R /Prev 13 0 R >>"),
            (15, "<< /Title (One.A.i) /Parent 13 0 R >>"));
    }

    /// <summary>
    /// Checks every link and count the spec asks for, from the saved file reopened: /First, /Last, /Prev, /Next and /Parent
    /// agree, and each /Count is the number of items shown (negated for a closed item, none without children).
    /// </summary>
    private static List<string> CheckWellFormed(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
        using PdfDocument reopened = PdfDocument.Open(output.ToArray(), Strict);
        _ = reopened.Outline.Children;
        Assert.DoesNotContain(reopened.Warnings, w => w.Code == PdfWarningCode.MalformedOutline);
        if (reopened.Outline.Dictionary is PdfDictionary root)
        {
            Assert.Equal(Shown(root), root.GetInt("Count") ?? 0);
        }

        foreach (PdfOutlineItem item in reopened.Outline.GetAllItems())
        {
            int shown = Shown(item.Dictionary);
            long? count = item.Dictionary.GetInt("Count");
            Assert.True(shown == 0 ? count is null : Math.Abs(count ?? 0) == shown, $"{item.Title}: /Count {count}, {shown} shown");
        }

        return [.. reopened.Outline.GetAllItems().Select(i => i.Title)];
    }

    /// <summary>The items a node shows when open: its children and, below each open child, what that child shows.</summary>
    private static int Shown(PdfDictionary node)
    {
        int shown = 0;
        for (PdfDictionary? child = node.Get<PdfDictionary>("First"); child is not null; child = child.Get<PdfDictionary>("Next"))
        {
            shown += 1 + (child.GetInt("Count") > 0 ? Shown(child) : 0);
        }

        return shown;
    }

    [Fact]
    public void ReadsItemsTitlesDestinationsAndStyle()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested(), Strict);
        IReadOnlyList<PdfOutlineItem> top = pdf.Outline.Children;

        Assert.Equal(["One", "Twø"], top.Select(i => i.Title));
        Assert.Equal(["One", "One.A", "One.A.i", "One.B", "Twø"], pdf.Outline.GetAllItems().Select(i => i.Title));
        Assert.Equal((PdfDestinationFit.Xyz, 100.0, 0), (top[0].Destination!.Fit, top[0].Destination!.Top, top[0].Destination!.GetPageIndex(pdf)));
        Assert.Equal(1, top[1].Destination!.GetPageIndex(pdf));
        Assert.IsType<PdfGoToAction>(top[1].Action);
        PdfOutlineItem a = top[0].Children[0];
        Assert.Equal((new PdfRgbColor(1, 0, 0), true, false, false), (a.Color, a.IsBold, a.IsItalic, a.IsOpen));
        Assert.True(top[0].IsOpen);
        Assert.Equal(top[0], a.Parent);
        Assert.IsType<PdfOutline>(top[0].Parent);
        Assert.Equal(PdfRgbColor.Black, top[0].Color);
        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void ADocumentWithoutAnOutlineGetsOne()
    {
        using PdfDocument pdf = PdfDocument.Open(File(""));
        Assert.Empty(pdf.Outline.Children);
        Assert.Null(pdf.Outline.Dictionary);

        PdfOutlineItem first = pdf.Outline.Add("First", PdfDestination.ToPage(pdf.Pages[0]));
        PdfOutlineItem child = first.Add("Child", PdfDestination.ToPage(pdf.Pages[1], PdfDestinationFit.FitH) with { Top = 50 });
        pdf.Outline.Insert(0, "Zeroth");
        first.IsOpen = true;

        Assert.Equal(["Zeroth", "First", "Child"], CheckWellFormed(pdf));
        Assert.Equal(3, pdf.Outline.Dictionary!.GetInt("Count"));
        Assert.Equal(1, child.Destination!.GetPageIndex(pdf));
    }

    [Fact]
    public void EditsKeepLinksAndCountsRight()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested());
        PdfOutlineItem one = pdf.Outline.Children[0];
        PdfOutlineItem oneA = one.Children[0];

        oneA.Add("One.A.ii");
        one.Insert(1, "One.Between");
        pdf.Outline.Children[1].Remove();
        oneA.IsOpen = true;
        Assert.Equal(["One", "One.A", "One.A.i", "One.A.ii", "One.Between", "One.B"], CheckWellFormed(pdf));
        Assert.Equal(6, pdf.Outline.Dictionary!.GetInt("Count"));

        oneA.MoveTo(pdf.Outline, 1);
        one.IsOpen = false;
        Assert.Equal(["One", "One.Between", "One.B", "One.A", "One.A.i", "One.A.ii"], CheckWellFormed(pdf));
        Assert.Equal(-2, one.Dictionary.GetInt("Count"));
        Assert.Equal(4, pdf.Outline.Dictionary!.GetInt("Count"));

        one.Children[1].MoveTo(one, 0);
        Assert.Equal(["One", "One.B", "One.Between", "One.A", "One.A.i", "One.A.ii"], CheckWellFormed(pdf));
        Assert.Throws<ArgumentException>(() => one.MoveTo(one, 0));
        Assert.Throws<ArgumentException>(() => pdf.Outline.Children[1].MoveTo(pdf.Outline.Children[1].Children[0], 0));
    }

    [Fact]
    public void SetsTitleColourStyleDestinationAndAction()
    {
        using PdfDocument pdf = PdfDocument.Open(Nested());
        PdfOutlineItem item = pdf.Outline.Children[1];

        item.Title = "Ω";
        item.Color = new PdfRgbColor(0, 0.5, 2);
        item.IsItalic = true;
        item.Destination = PdfDestination.ToPage(pdf.Pages[0]);
        Assert.Null(item.Dictionary["A"]);
        Assert.Equal(new PdfRgbColor(0, 0.5, 1), item.Color);

        item.Action = new PdfUriAction("https://example.com/");
        Assert.Null(item.Dictionary["Dest"]);
        Assert.Null(item.Destination);
        item.Color = PdfRgbColor.Black;
        item.IsItalic = false;
        Assert.Null(item.Dictionary["C"]);
        Assert.Null(item.Dictionary["F"]);
        Assert.Equal("Ω", CheckWellFormed(pdf)[^1]);
    }

    [Fact]
    public void RepairsBrokenChainsInMemory()
    {
        // 11's /Next loops back to itself through 12; 12 has the wrong /Parent and /Prev; the root's /Last is wrong;
        // 13's /Next is not an item; the root's /Count is wrong.
        byte[] bytes = File(
            "/Outlines 10 0 R",
            (10, "<< /First 11 0 R /Last 11 0 R /Count 2 >>"),
            (11, "<< /Title (a) /Parent 10 0 R /Next 12 0 R /First 13 0 R /Last 13 0 R /Count 1 >>"),
            (12, "<< /Title (b) /Parent 11 0 R /Next 11 0 R >>"),
            (13, "<< /Title (c) /Parent 11 0 R /Next 5 >>"));
        using PdfDocument pdf = PdfDocument.Open(bytes);

        Assert.Equal(["a", "c", "b"], pdf.Outline.GetAllItems().Select(i => i.Title));
        // Four links, the cut chain, and the root's /Count (3 items shown, not 2).
        Assert.Equal(6, pdf.Warnings.Count(w => w.Code == PdfWarningCode.MalformedOutline));
        Assert.Equal(3, pdf.Outline.Dictionary!.GetInt("Count"));
        Assert.False(pdf.IsChanged);
        using (MemoryStream unchanged = new())
        {
            pdf.Save(unchanged);
            Assert.Equal(bytes, unchanged.ToArray());
        }

        pdf.Outline.Add("d");
        Assert.Equal(["a", "c", "b", "d"], CheckWellFormed(pdf));
    }

    [Fact]
    public void AChainIsCutWhereItReachesThePageTree()
    {
        // Found by fuzzing: item 11's /Next named the page tree root, which was then read as an item and given outline
        // links, while its /Count stayed the page count.
        using PdfDocument pdf = PdfDocument.Open(File(
            "/Outlines 10 0 R",
            (10, "<< /Type /Outlines /First 11 0 R /Last 11 0 R /Count 1 >>"),
            (11, "<< /Title (One) /Parent 10 0 R /Next 2 0 R >>")));

        Assert.Equal(["One"], pdf.Outline.GetAllItems().Select(i => i.Title));
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.MalformedOutline);
        Assert.Null(((PdfDictionary)pdf.Objects.Get(2))["Parent"]);

        pdf.Outline.Insert(1, "Two");
        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
        using PdfDocument reopened = PdfDocument.Open(output.ToArray());
        Assert.Equal(["One", "Two"], reopened.Outline.GetAllItems().Select(i => i.Title));
        Assert.Equal(2, reopened.Pages.Count);
        Assert.DoesNotContain(reopened.Warnings, w => w.Code == PdfWarningCode.MalformedOutline);
    }

    [Fact]
    public void StrictModeThrowsOnABrokenOutline()
    {
        using PdfDocument pdf = PdfDocument.Open(
            File("/Outlines 10 0 R", (10, "<< /First 11 0 R /Last 11 0 R >>"), (11, "<< /Title (a) /Parent 10 0 R /Next 11 0 R >>")),
            Strict);

        Assert.Equal(PdfWarningCode.MalformedOutline, Assert.Throws<PdfException>(() => pdf.Outline.Children).Code);
    }

    [Fact]
    public void AnIncrementalEditWritesOnlyTheItemsInvolved()
    {
        using PdfDocument built = PdfDocument.Open(File(""));
        for (int i = 0; i < 200; i++)
        {
            PdfOutlineItem chapter = built.Outline.Add($"Chapter {i}");
            for (int j = 0; j < 5; j++)
            {
                chapter.Add($"Section {i}.{j}");
            }
        }

        using MemoryStream original = new();
        built.Save(original);
        using PdfDocument pdf = PdfDocument.Open(original.ToArray(), Strict);

        pdf.Outline.Children[100].Insert(2, "New");
        using MemoryStream updated = new();
        pdf.Save(updated);

        // The new item, its two neighbours, its parent (/Count) and the root (/Count).
        string appended = Encoding.Latin1.GetString(updated.ToArray().AsSpan((int)original.Length));
        Assert.Equal(5, ObjectHeader().Count(appended));
    }

    [GeneratedRegex(@"\d+ 0 obj")]
    private static partial Regex ObjectHeader();
}
