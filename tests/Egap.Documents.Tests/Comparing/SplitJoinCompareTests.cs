using Egap.Documents.Comparing;

namespace Egap.Documents.Tests.Comparing;

/// <summary>
/// Paragraphs split and joined: compared as one text, with only the paragraph marks between the pieces inserted (a split)
/// or deleted (a join), as Word marks them.
/// </summary>
public sealed class SplitJoinCompareTests
{
    private const string Whole = "The committee met on Tuesday and agreed the budget for the coming year after a long debate.";

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ASplitIsOneInsertedMark(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("Before.", Whole, "After.");
        Document b = DocumentCompareTests.Make("Before.", "The committee met on Tuesday and agreed the budget", "for the coming year after a long debate.", "After.");

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, Options(baseDocument));

        List<Paragraph> result = [.. comparison.Document.Sections[0].Body.Paragraphs];
        Assert.Equal(4, result.Count);
        Assert.Equal(RevisionKind.Insertion, result[1].MarkRevision?.Kind);
        Assert.Null(result[2].MarkRevision);
        Assert.Equal(0, comparison.Statistics.InsertedWords + comparison.Statistics.DeletedWords);
        Assert.Contains(comparison.Changes, c => c.Kind == CompareChangeKind.Inserted && c.NewText == "\r");
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AJoinIsOneDeletedMark(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("Before.", "The committee met on Tuesday and agreed the budget", "for the coming year after a long debate.", "After.");
        Document b = DocumentCompareTests.Make("Before.", Whole, "After.");

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, Options(baseDocument));

        List<Paragraph> result = [.. comparison.Document.Sections[0].Body.Paragraphs];
        Assert.Equal(4, result.Count);
        Assert.Equal(RevisionKind.Deletion, result[1].MarkRevision?.Kind);
        Assert.Null(result[2].MarkRevision);
        Assert.Equal(0, comparison.Statistics.InsertedWords + comparison.Statistics.DeletedWords);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ASplitIntoThreeHasTwoInsertedMarks(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make(Whole);
        Document b = DocumentCompareTests.Make("The committee met on Tuesday", "and agreed the budget for the coming year", "after a long debate.");

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, Options(baseDocument));

        Assert.Equal(
            [RevisionKind.Insertion, RevisionKind.Insertion, null],
            comparison.Document.Sections[0].Body.Paragraphs.Select(p => p.MarkRevision?.Kind));
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ASplitKeepsTheWordChangesInside(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make(Whole);
        Document b = DocumentCompareTests.Make("The committee met on Monday and agreed the budget", "for the coming year after a short debate today.");

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, Options(baseDocument));

        List<Paragraph> result = [.. comparison.Document.Sections[0].Body.Paragraphs];
        Assert.Equal(RevisionKind.Insertion, result[0].MarkRevision?.Kind);
        Assert.Equal("Tuesday", Text(result[0], RevisionKind.Deletion));
        Assert.Equal("Monday", Text(result[0], RevisionKind.Insertion));
        Assert.Equal("long", Text(result[1], RevisionKind.Deletion));
        Assert.Equal(["short", "today"], Text(result[1], RevisionKind.Insertion).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(3, comparison.Statistics.InsertedWords);
        Assert.Equal(2, comparison.Statistics.DeletedWords);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AJoinKeepsTheWordChangesInside(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("The committee met on Monday and agreed the budget", "for the coming year after a short debate today.");
        Document b = DocumentCompareTests.Make(Whole);

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, Options(baseDocument));

        List<Paragraph> result = [.. comparison.Document.Sections[0].Body.Paragraphs];
        Assert.Equal(2, result.Count);
        Assert.Equal(RevisionKind.Deletion, result[0].MarkRevision?.Kind);
        Assert.Equal("Monday", Text(result[0], RevisionKind.Deletion));
        Assert.Equal("Tuesday", Text(result[0], RevisionKind.Insertion));
        Assert.Equal(2, comparison.Statistics.InsertedWords);
        Assert.Equal(3, comparison.Statistics.DeletedWords);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AListItemSplitStaysInTheList(CompareBase baseDocument)
    {
        Document a = ListOf("Alpha item.", "Beta item with several more words in it.", "Gamma item.");
        Document b = ListOf("Alpha item.", "Beta item with several", "more words in it.", "Gamma item.");

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, Options(baseDocument));

        List<Paragraph> result = [.. comparison.Document.Sections[0].Body.Paragraphs];
        Assert.Equal([null, RevisionKind.Insertion, null, null], result.Select(p => p.MarkRevision?.Kind));
        Assert.All(result, p => Assert.NotNull(p.ListLabel));
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ASplitWithADifferentLookMarksTheFormat(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make(Whole);
        Document b = DocumentCompareTests.Make("The committee met on Tuesday and agreed the budget", "for the coming year after a long debate.");
        b.Sections[0].Body.Paragraphs.First().Format.Alignment = ParagraphAlignment.Center;
        b.Sections[0].Body.Paragraphs.Last().Format.Alignment = ParagraphAlignment.Right;

        RoundTrip.CheckFormatting(a, b, Options(baseDocument));
    }

    [Fact]
    public void AnUnrelatedParagraphAfterASplitIsInsertedWhole()
    {
        Document a = DocumentCompareTests.Make(Whole);
        Document b = DocumentCompareTests.Make("The committee met on Tuesday and agreed the budget", "for the coming year after a long debate.", "Minutes taken by Sam.");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Assert.Equal(
            [RevisionKind.Insertion, null, RevisionKind.Insertion],
            comparison.Document.Sections[0].Body.Paragraphs.Select(p => p.MarkRevision?.Kind));
        Assert.Equal(4, comparison.Statistics.InsertedWords);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ASplitAcrossATableIsNotJoined(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make(Whole);
        Document b = DocumentCompareTests.Make("The committee met on Tuesday and agreed the budget");
        b.Sections[0].Body.AddTable(1, 1).Rows[0].Cells[0].Paragraphs.First().AddRun("Cell");
        b.Sections[0].Body.AddParagraph("for the coming year after a long debate.");

        DocumentComparison comparison = RoundTrip.Check(a, b, Options(baseDocument));

        // The first piece pairs with the paragraph; the table and the second piece are inserted whole, the second piece's
        // words as moved from the paragraph's end.
        List<Paragraph> result = [.. comparison.Document.Sections[0].Body.Paragraphs];
        Assert.Equal(RevisionKind.Insertion, result[^1].MarkRevision?.Kind);
        Assert.Equal("for the coming year after a long debate", Text(result[^1], RevisionKind.MoveTo));
        Assert.Equal("for the coming year after a long debate", Text(result[0], RevisionKind.MoveFrom));
        Assert.Contains(comparison.Changes, c => c.Kind == CompareChangeKind.Moved);
        Assert.Equal(RevisionKind.Insertion, comparison.Document.Sections[0].Body.Tables.Single().Rows[0].Revision?.Kind);
    }

    private static DocumentCompareOptions Options(CompareBase baseDocument)
    {
        return new DocumentCompareOptions { Base = baseDocument };
    }

    private static Document ListOf(params string[] items)
    {
        Document document = DocumentCompareTests.Make(items);
        List list = document.Lists.AddNumbered();
        foreach (Paragraph paragraph in document.Sections[0].Body.Paragraphs)
        {
            paragraph.SetList(list, 0);
        }

        return document;
    }

    private static string Text(Paragraph paragraph, RevisionKind kind)
    {
        return string.Concat(paragraph.Runs.Where(r => r.Revision?.Kind == kind).Select(r => r.Text)).Trim();
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ShortParagraphsJoinedWithWordsDeletedAreAJoin(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("One.", "Three.", "Four.", "Five six.", "Seven eight.", "Nine.");
        Document b = DocumentCompareTests.Make("One.", "Three.", "Four.Five eight.", "Nine.");

        DocumentComparison comparison = RoundTrip.Check(a, b, new DocumentCompareOptions { Base = baseDocument });

        // As Word marks it: the three paragraphs join, and only "six." and "Seven " go.
        DocumentChange change = Assert.Single(comparison.Changes, c => c.OldText.Trim().Length > 0);
        Assert.Equal("six.Seven ", change.OldText.Replace("\r", "", StringComparison.Ordinal));
        Assert.Equal(0, comparison.Statistics.InsertedWords);
        Assert.Equal(2, comparison.Statistics.DeletedWords);
    }
}
