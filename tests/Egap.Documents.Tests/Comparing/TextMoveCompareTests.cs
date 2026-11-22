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
    public void AnEditedParagraphStillPairsAcrossAParagraphMovedBeforeIt(CompareBase baseDocument)
    {
        const string Service = "The service runs many parks across the country and its staff of rangers keeps them open all year.";
        const string Objects = "More than one hundred million objects in collections.";
        Document original = DocumentCompareTests.Make("Alpha stays.", Service, "Bravo stays.", "Charlie stays.", Objects, "Delta stays.");
        Document revised = DocumentCompareTests.Make("Alpha stays.", Objects, Service.Replace("rangers", "wardens", StringComparison.Ordinal), "Bravo stays.", "Charlie stays.", "Delta stays.");

        DocumentComparison comparison = RoundTrip.Check(original, revised, new DocumentCompareOptions { Base = baseDocument });

        // As Word marks it: the moved paragraph is a move, and the edited one keeps its place with one word replaced.
        DocumentChange move = Assert.Single(comparison.Changes, c => c.Kind == CompareChangeKind.Moved);
        Assert.Equal(Objects, move.OldText.TrimEnd('\r'));
        DocumentChange edit = Assert.Single(comparison.Changes, c => c.Kind != CompareChangeKind.Moved);
        Assert.Equal((CompareChangeKind.Replaced, "rangers", "wardens"), (edit.Kind, edit.OldText, edit.NewText));
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void TheRestOfARunCutForAMoveStaysInItsChange(CompareBase baseDocument)
    {
        Document original = DocumentCompareTests.Make(
            "An opening paragraph that stays as it is.",
            "The quick brown fox jumps over the lazy dog. Extra words that go away.",
            "A middle paragraph that stays as well, long enough to hold its own.",
            "The closing paragraph.");
        Document revised = DocumentCompareTests.Make(
            "An opening paragraph that stays as it is.",
            "A middle paragraph that stays as well, long enough to hold its own.",
            "The closing paragraph. The quick brown fox jumps over the lazy dog.");
        DocumentCompareOptions options = new() { Base = baseDocument };

        DocumentComparison comparison = RoundTrip.Check(original, revised, options);

        Assert.Single(comparison.Changes, c => c.Kind == CompareChangeKind.Moved);
        Assert.Contains(comparison.Changes, c => c.OldText.Contains("Extra words that go away", StringComparison.Ordinal));
        EveryChangeIsListed(original, revised, options);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void TwoMovesOutOfOneRunLeaveTheMiddleInItsChange(CompareBase baseDocument)
    {
        Document original = DocumentCompareTests.Make(
            "An opening paragraph that stays as it is.",
            "Alpha sentence of six words here. A middle bit that goes. Beta sentence is also six words long.",
            "A middle paragraph that stays as well, long enough to hold its own.",
            "The closing paragraph.");
        Document revised = DocumentCompareTests.Make(
            "An opening paragraph that stays as it is. Beta sentence is also six words long.",
            "A middle paragraph that stays as well, long enough to hold its own.",
            "The closing paragraph. Alpha sentence of six words here.");
        DocumentCompareOptions options = new() { Base = baseDocument };

        DocumentComparison comparison = RoundTrip.Check(original, revised, options);

        Assert.Equal(2, comparison.Changes.Count(c => c.Kind == CompareChangeKind.Moved));
        Assert.Contains(comparison.Changes, c => c.OldText.Contains("A middle bit that goes", StringComparison.Ordinal));
        EveryChangeIsListed(original, revised, options);
    }

    // Each tracked run is in a listed change, and accepting or rejecting the listed changes one by one gives each document.
    private static void EveryChangeIsListed(Document original, Document revised, DocumentCompareOptions options)
    {
        DocumentComparison comparison = DocumentCompare.Run(original, revised, options);
        HashSet<Node> listed = [.. comparison.Changes.SelectMany(c => c.TrackedChanges).Select(t => t.Node)];
        Assert.All(comparison.Document.Descendants<Run>().Where(r => r.Revision is not null), r => Assert.Contains(r, listed));
        foreach (bool accept in (bool[])[true, false])
        {
            DocumentComparison settled = DocumentCompare.Run(original, revised, options);
            foreach (TrackedChange tracked in settled.Changes.SelectMany(c => c.TrackedChanges).ToList())
            {
                if (accept)
                {
                    tracked.Accept();
                }
                else
                {
                    tracked.Reject();
                }
            }

            Assert.Equal(RoundTrip.Blocks(accept ? revised : original), RoundTrip.Blocks(settled.Document));
        }
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

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AHyperlinkEqualsTheSameLinkReadFromRtf(CompareBase baseDocument)
    {
        Document withLink = DocumentCompareTests.Make("Write to ");
        withLink.Sections[0].Body.Paragraphs.First().AddHyperlink("our office", "mailto:office@example.com");
        Document withField = DocumentCompareTests.Make("Write to ");
        withField.Sections[0].Body.Paragraphs.First().AddField("HYPERLINK \"mailto:office@example.com\" \\o \"Mail us\"", "our office");
        Document original = Loaded(withLink, "docx");
        Document revised = Loaded(withField, "rtf");

        DocumentComparison comparison = RoundTrip.Check(original, revised, new DocumentCompareOptions { Base = baseDocument });

        // RTF keeps links as HYPERLINK fields, which read back as hyperlinks: the same link, so nothing changes.
        Assert.Empty(comparison.Changes);
        foreach (string format in (string[])["docx", "rtf", "odt"])
        {
            WriterRoundTripTests.Check(original, revised, comparison.Document, format);
        }
    }

    [Theory]
    [InlineData("rtf")]
    [InlineData("odt")]
    [InlineData("docx")]
    public void ChangedHyperlinksRoundTrip(string format)
    {
        Document original = DocumentCompareTests.Make("Write to ", "Old line.", "Kept line.");
        original.Sections[0].Body.Paragraphs.First().AddHyperlink("our main office", "mailto:office@example.com");
        original.Sections[0].Body.Paragraphs.ElementAt(1).AddHyperlink("the old site", "https://old.example.com/");
        Document revised = DocumentCompareTests.Make("Write to ", "Old line.", "Kept line.");
        revised.Sections[0].Body.Paragraphs.First().AddHyperlink("our new office", "mailto:office@example.com");
        revised.Sections[0].Body.Paragraphs.ElementAt(2).AddHyperlink("the new site", "https://new.example.com/");

        // A link whose text changed, a link deleted whole and a link inserted whole: each keeps its changes through the
        // format, a link RTF writes as a field reading back as a link.
        DocumentComparison comparison = DocumentCompare.Run(original, revised);

        WriterRoundTripTests.Check(original, revised, comparison.Document, format);
        Document back = WriterRoundTripTests.Through(comparison.Document, format);
        Assert.Empty(back.Descendants<FieldStart>());
        Assert.Equal(comparison.Document.Descendants<Hyperlink>().Count(), back.Descendants<Hyperlink>().Count());
    }

    [Theory]
    [InlineData(IslandKind.ShapeGroup, true)]
    [InlineData(IslandKind.OleObject, true)]
    [InlineData(IslandKind.Math, false)]
    public void AcrossFormatsAKeptDrawingEqualsTheOtherFormatsDrawing(IslandKind kind, bool same)
    {
        // DOCX keeps a shape group as a VML island where RTF keeps a group of its own, each in its own markup.
        Document original = DocumentCompareTests.Make("Figure ");
        original.Sections[0].Body.Paragraphs.First().Inlines.Add(new Island(original, IslandKind.Vml, "docx", "<v:group />"));
        original.SourceFormat = DocumentFormatKind.Docx;
        Document revised = DocumentCompareTests.Make("Figure ");
        revised.Sections[0].Body.Paragraphs.First().Inlines.Add(new Island(revised, kind, "rtf", @"{\shp}") { Text = "Group 334" });
        revised.SourceFormat = DocumentFormatKind.Rtf;

        DocumentComparison comparison = DocumentCompare.Run(original, revised);

        Assert.Equal(same, comparison.Changes.Count == 0);
    }

    [Fact]
    public void AcrossFormatsALinkIsTheSameWithoutItsTrailingSpace()
    {
        // HTML drops the white space at a line's end, a table of contents entry's included.
        Document original = DocumentCompareTests.Make("");
        original.Sections[0].Body.Paragraphs.First().AddHyperlink("Executive  Summary ", "#executive");
        original.SourceFormat = DocumentFormatKind.Docx;
        Document revised = DocumentCompareTests.Make("");
        revised.Sections[0].Body.Paragraphs.First().AddHyperlink("Executive Summary", "#executive");
        revised.SourceFormat = DocumentFormatKind.Html;

        Assert.Empty(DocumentCompare.Run(original, revised).Changes);
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
