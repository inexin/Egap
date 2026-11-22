using Egap.Documents.Comparing;

namespace Egap.Documents.Tests.Comparing;

/// <summary>
/// Changes inside paragraphs: moved sentences, the text of notes that go or come with their reference, words Word reads
/// apart, and fields another format keeps as text.
/// </summary>
public sealed class TextMoveCompareTests
{
    private const string Moving = "The budget was agreed after a long debate.";

    // Longer than the moving sentence, so the diff keeps it and the moving sentence is the one that moves.
    private const string Staying = "The committee met on Tuesday evening in the big hall downtown.";

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ASentenceMovedInsideAParagraphIsOneMove(CompareBase baseDocument)
    {
        Document original = DocumentCompareTests.Make($"{Moving} {Staying}");
        Document revised = DocumentCompareTests.Make($"{Staying} {Moving}");

        DocumentComparison comparison = RoundTrip.Check(original, revised, new DocumentCompareOptions { Base = baseDocument });

        DocumentChange move = Assert.Single(comparison.Changes, c => c.Kind == CompareChangeKind.Moved);
        // "The" starts both sentences, so the diff keeps it where it is; the rest of the sentence moves.
        Assert.Equal("budget was agreed after a long debate", move.OldText);
        Assert.Equal(move.OldText, move.NewText);
        List<Run> runs = [.. comparison.Document.Descendants<Run>()];
        Run from = Assert.Single(runs, r => r.Revision?.Kind == RevisionKind.MoveFrom);
        Run to = Assert.Single(runs, r => r.Revision?.Kind == RevisionKind.MoveTo);
        Assert.Equal(from.Revision!.MoveName, to.Revision!.MoveName);
        Assert.Equal(7, comparison.Statistics.MovedWords);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ASentenceMovedToAnotherParagraphIsOneMove(CompareBase baseDocument)
    {
        Document original = DocumentCompareTests.Make($"Opening words. {Moving}", "A middle paragraph that stays.", "The last paragraph.");
        Document revised = DocumentCompareTests.Make("Opening words.", "A middle paragraph that stays.", $"The last paragraph. {Moving}");

        DocumentComparison comparison = RoundTrip.Check(original, revised, new DocumentCompareOptions { Base = baseDocument });

        Assert.Single(comparison.Changes, c => c.Kind == CompareChangeKind.Moved);
        Assert.Contains(comparison.Document.Descendants<Run>(), r => r.Revision?.Kind == RevisionKind.MoveTo && r.Text.Contains("long debate", StringComparison.Ordinal));
    }

    [Fact]
    public void AShortRunOfWordsIsNotAMove()
    {
        Document original = DocumentCompareTests.Make("Five words move here now. The rest stays.");
        Document revised = DocumentCompareTests.Make("The rest stays. Five words move here now.");

        DocumentComparison comparison = RoundTrip.Check(original, revised);

        Assert.DoesNotContain(comparison.Changes, c => c.Kind == CompareChangeKind.Moved);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("rtf")]
    [InlineData("odt")]
    public void MovedSentencesRoundTripThroughTheWriters(string format)
    {
        Document original = DocumentCompareTests.Make($"{Moving} {Staying}", "Another paragraph.");
        Document revised = DocumentCompareTests.Make(Staying, $"Another paragraph. {Moving}");

        DocumentComparison comparison = DocumentCompare.Run(original, revised);

        WriterRoundTripTests.Check(original, revised, comparison.Document, format);
    }

    [Fact]
    public void MovesCanBeLeftOut()
    {
        Document original = DocumentCompareTests.Make($"{Moving} {Staying}");
        Document revised = DocumentCompareTests.Make($"{Staying} {Moving}");

        DocumentComparison comparison = RoundTrip.Check(original, revised, new DocumentCompareOptions { Moves = false });

        Assert.DoesNotContain(comparison.Changes, c => c.Kind == CompareChangeKind.Moved);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ANoteInADeletedParagraphHasItsTextDeletedToo(CompareBase baseDocument)
    {
        Document original = DocumentCompareTests.Make("A paragraph that stays.");
        Paragraph going = original.Sections[0].Body.AddParagraph("A paragraph that goes");
        going.AddFootnote("The note's own words.");
        Document revised = DocumentCompareTests.Make("A paragraph that stays.");

        DocumentComparison comparison = RoundTrip.CheckStories(original, revised, new DocumentCompareOptions { Base = baseDocument });

        Footnote note = comparison.Document.Descendants<Footnote>().Single();
        Assert.Equal(RevisionKind.Deletion, note.Revision?.Kind);
        Assert.All(note.Story.Descendants<Run>(), r => Assert.Equal(RevisionKind.Deletion, r.Revision?.Kind));
        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Contains(change.TrackedChanges, t => t.Node is Run run && run.GetAncestor<Footnote>() == note);

        // Rejecting the change alone brings the note's text back unmarked.
        change.TrackedChanges.ToList().ForEach(t => t.Reject());
        Assert.All(note.Story.Descendants<Run>(), r => Assert.Null(r.Revision));
    }

    [Fact]
    public void ANoteInsertedInsideAParagraphHasItsTextInsertedToo()
    {
        Document original = DocumentCompareTests.Make("The claim stands on its own here.");
        Document revised = DocumentCompareTests.Make("The claim stands on its own here.");
        revised.Sections[0].Body.Paragraphs.First().AddFootnote("A source for the claim.");

        DocumentComparison comparison = RoundTrip.CheckStories(original, revised);

        Footnote note = comparison.Document.Descendants<Footnote>().Single();
        Assert.Equal(RevisionKind.Insertion, note.Revision?.Kind);
        Assert.All(note.Story.Descendants<Run>(), r => Assert.Equal(RevisionKind.Insertion, r.Revision?.Kind));
    }

    [Fact]
    public void AFullStopWithNoSpaceAfterItEndsAWord()
    {
        Document original = DocumentCompareTests.Make("We counted to one.This was the start.");
        Document revised = DocumentCompareTests.Make("We counted to one. This was the start.");

        DocumentComparison comparison = RoundTrip.Check(original, revised);

        // Only the space comes in: "one" and "This" are words of their own on both sides.
        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal((CompareChangeKind.Inserted, " "), (change.Kind, change.NewText));
        Assert.Equal(0, comparison.Statistics.InsertedWords + comparison.Statistics.DeletedWords);
    }

    [Fact]
    public void NumbersAndAbbreviationsKeepTheirStops()
    {
        Document original = DocumentCompareTests.Make("Version 3.5 is out.");
        Document revised = DocumentCompareTests.Make("Version 3.6 is out.");

        DocumentComparison comparison = RoundTrip.Check(original, revised);

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(("3.5", "3.6"), (change.OldText, change.NewText));
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AcrossFormatsAFieldEqualsTheTextItShows(CompareBase baseDocument)
    {
        Document withField = DocumentCompareTests.Make("Membership");
        Paragraph entry = withField.Sections[0].Body.Paragraphs.First();
        entry.AddTab();
        entry.AddField("PAGEREF _Toc1 \\h", "4");
        Document withText = DocumentCompareTests.Make("Membership\t4");
        Document original = Loaded(withField, "docx");
        Document revised = Loaded(withText, "rtf");

        DocumentComparison comparison = RoundTrip.Check(original, revised, new DocumentCompareOptions { Base = baseDocument });

        // The page number is the same word: no words change, only the field's frame is tracked.
        Assert.Equal(0, comparison.Statistics.InsertedWords + comparison.Statistics.DeletedWords);
        Assert.All(comparison.Document.Descendants<Run>().Where(r => r.Text == "4"), r => Assert.Null(r.Revision));
        Assert.Contains(comparison.Document.Descendants<FieldStart>(), f => f.Revision?.Kind == RevisionKind.Deletion);
        foreach (string format in (string[])["docx", "rtf", "odt"])
        {
            WriterRoundTripTests.Check(original, revised, comparison.Document, format);
        }
    }

    [Fact]
    public void InTheSameFormatAFieldStillDiffersFromItsText()
    {
        Document original = DocumentCompareTests.Make("Membership");
        original.Sections[0].Body.Paragraphs.First().AddField("PAGEREF _Toc1 \\h", "4");
        Document revised = DocumentCompareTests.Make("Membership4");

        DocumentComparison comparison = RoundTrip.Check(original, revised);

        Assert.Equal(1, comparison.Statistics.InsertedWords);
    }

    private static Document Loaded(Document document, string format)
    {
        using MemoryStream stream = new();
        if (format == "rtf")
        {
            RtfFormat.Save(document, stream);
        }
        else
        {
            DocxFormat.Save(document, stream);
        }

        stream.Position = 0;
        return Document.Load(stream);
    }
}
