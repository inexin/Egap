namespace Egap.Pdf.Tests.Comparing;

/// <summary>PdfCompare: Phase 7 tasks 14 to 16.</summary>
public sealed class CompareTests
{
    private static readonly string[] Text =
    [
        "The council met on Monday and agreed the budget for roads and bridges.",
        "Members asked how much would be spent on repairs before the spring.",
        "The library will open late during the summer months for local residents.",
        "Results will be published later with a full list of every change made.",
    ];

    [Fact]
    public void IdenticalDocumentsHaveNoChanges()
    {
        using PdfDocument a = Document([Text]), b = Document([Text]);
        using PdfComparison result = PdfCompare.Run(a, b, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.HasChanges, result.ToString());
        Assert.Equal([new PdfPageMatch(1, 1)], result.Pages);
    }

    [Fact]
    public void FindsReplacedInsertedAndDeletedWords()
    {
        using PdfDocument a = Document([Text]);
        using PdfDocument b = Document([[Text[0].Replace("Monday", "Tuesday", StringComparison.Ordinal), Text[1].Replace("much ", "", StringComparison.Ordinal), Text[2].Replace("late ", "very late ", StringComparison.Ordinal), Text[3]]]);

        using PdfComparison result = PdfCompare.Run(a, b, new PdfCompareOptions { Visual = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([PdfTextChangeKind.Replaced, PdfTextChangeKind.Deleted, PdfTextChangeKind.Inserted], result.TextChanges.Select(c => c.Kind));
        Assert.Equal(("Monday", "Tuesday"), (result.TextChanges[0].OldText, result.TextChanges[0].NewText));
        Assert.Equal("much", result.TextChanges[1].OldText);
        Assert.Equal("would", result.TextChanges[1].Anchor!.Text);
        Assert.Equal("very", result.TextChanges[2].NewText);
        Assert.All(result.TextChanges.SelectMany(c => c.NewWords), w => Assert.True(w.Box.Width > 0));
    }

    [Fact]
    public void TextThatReflowsOntoTheNextPageIsNoChange()
    {
        using PdfDocument a = Document([[Text[0], Text[1]], [Text[2], Text[3]]]);
        using PdfDocument b = Document([[Text[0]], [Text[1], Text[2], Text[3]]]);

        using PdfComparison result = PdfCompare.Run(a, b, new PdfCompareOptions { Visual = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.TextChanges);
        Assert.Equal([new PdfPageMatch(1, 1), new PdfPageMatch(2, 2)], result.Pages);
    }

    [Fact]
    public void AMovedParagraphIsOneMove()
    {
        using PdfDocument a = Document([Text]);
        using PdfDocument b = Document([[Text[1], Text[2], Text[3], Text[0]]]);

        using PdfComparison result = PdfCompare.Run(a, b, new PdfCompareOptions { Visual = false }, cancellationToken: TestContext.Current.CancellationToken);

        PdfTextChange move = Assert.Single(result.TextChanges);
        Assert.Equal(PdfTextChangeKind.Moved, move.Kind);
        Assert.Equal(Text[0], move.NewText);
    }

    [Fact]
    public void MatchesPagesAndFindsAddedAndRemovedOnes()
    {
        string[] extra = ["A whole new page of text about something else entirely."];
        using PdfDocument a = Document([[Text[0]], [Text[1]], [Text[2]]]);
        using PdfDocument added = Document([[Text[0]], extra, [Text[1]], [Text[2]]]);
        using PdfDocument removed = Document([[Text[0]], [Text[2]]]);
        using PdfDocument rewritten = Document([[Text[0]], extra, [Text[2]]]);

        using PdfComparison plus = PdfCompare.Run(a, added, new PdfCompareOptions { Visual = false }, cancellationToken: TestContext.Current.CancellationToken);
        using PdfComparison minus = PdfCompare.Run(a, removed, new PdfCompareOptions { Visual = false }, cancellationToken: TestContext.Current.CancellationToken);
        using PdfComparison other = PdfCompare.Run(a, rewritten, new PdfCompareOptions { Visual = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([new PdfPageMatch(1, 1), new PdfPageMatch(null, 2), new PdfPageMatch(2, 3), new PdfPageMatch(3, 4)], plus.Pages);
        Assert.Equal([new PdfPageMatch(1, 1), new PdfPageMatch(2, null), new PdfPageMatch(3, 2)], minus.Pages);

        // A page whose every word changed still stands where it stood: it is paired, and its words replaced.
        Assert.Equal([new PdfPageMatch(1, 1), new PdfPageMatch(2, 2), new PdfPageMatch(3, 3)], other.Pages);
        Assert.Equal(PdfTextChangeKind.Replaced, Assert.Single(other.TextChanges).Kind);
    }

    [Fact]
    public void TheVisualPassBoxesWhatOnlyRendersDifferently()
    {
        using PdfDocument a = Document([Text]), b = Document([Text], box: true);

        using PdfComparison result = PdfCompare.Run(a, b, new PdfCompareOptions { DifferenceImages = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.TextChanges);
        PdfVisualChange change = Assert.Single(result.VisualChanges);
        PdfRectangle box = Assert.Single(change.Boxes);
        Assert.InRange(box.Left, 390, 402);
        Assert.InRange(box.Bottom, 90, 102);
        Assert.InRange(box.Right, 448, 460);
        Assert.NotNull(change.Difference);
    }

    [Fact]
    public void MarkupHighlightsChangesInACopy()
    {
        using PdfDocument a = Document([Text]);
        using PdfDocument b = Document([[Text[0].Replace("Monday", "Tuesday", StringComparison.Ordinal), Text[1].Replace("much ", "", StringComparison.Ordinal), Text[2], Text[3]]], box: true);
        using PdfComparison result = PdfCompare.Run(a, b, cancellationToken: TestContext.Current.CancellationToken);
        using MemoryStream marked = new();

        result.WriteMarkup(marked);

        using PdfDocument copy = PdfDocument.Open(marked.ToArray());
        string[] kinds = [.. copy.Pages[0].Annotations.Select(x => x.Subtype!).Where(s => s != "Popup").Order(StringComparer.Ordinal)];
        Assert.Contains("Highlight", kinds);
        Assert.Contains("Caret", kinds);
        Assert.Contains("Square", kinds);
        Assert.Contains(copy.Pages[0].Annotations, x => x.Contents == "Replaced: Monday");
        Assert.Empty(b.Pages[0].Annotations);
    }

    [Fact]
    public void RefusesBadOptions()
    {
        using PdfDocument a = Document([Text]);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfCompare.Run(a, a, new PdfCompareOptions { Dpi = 0 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfCompare.Run(a, a, new PdfCompareOptions { Tolerance = 300 }, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Pages of Helvetica lines, each page's lines given; with <paramref name="box"/>, a grey square on page 1.</summary>
    private static PdfDocument Document(string[][] pages, bool box = false)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference font = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        foreach (string[] lines in pages)
        {
            PdfPage page = pdf.Pages.AddBlank();
            page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
            System.Text.StringBuilder content = new("BT /F1 11 Tf 72 720 Td 14 TL ");
            foreach (string line in lines)
            {
                content.Append('(').Append(line).Append(") Tj T* ");
            }

            content.Append("ET");
            if (box && pdf.Pages.Count == 1)
            {
                content.Append(" 0.5 g 400 100 50 50 re f");
            }

            page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }
}
