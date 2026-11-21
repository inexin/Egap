using Egap.Documents.Comparing;
using Egap.Documents.Docx;

namespace Egap.Documents.Tests.Comparing;

/// <summary>Formatting compared on equal text, and the CaseChanges, Whitespace and Granularity switches (6.19 Build Plan task 5).</summary>
public sealed class FormatCompareTests
{
    private static Paragraph First(Document document)
    {
        return document.Sections[0].Body.Paragraphs.First();
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AReformattedRunIsAFormatChange(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("The quick brown fox.");
        Document b = DocumentCompareTests.Make("The quick brown fox.");
        First(b).Runs.First().Format.Bold = true;

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, new DocumentCompareOptions { Base = baseDocument });

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(CompareChangeKind.Formatted, change.Kind);
        Run run = Assert.Single(First(comparison.Document).Runs);
        Assert.NotNull(run.FormatRevision);
        Assert.True(run.GetEffectiveFormat().Bold);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void PartOfARunReformattedSplitsIt(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("The quick brown fox.");
        Document b = DocumentCompareTests.Make("The ");
        Paragraph paragraph = First(b);
        paragraph.AddRun("quick").Format.Size = 16;
        paragraph.AddRun(" brown fox.");

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, new DocumentCompareOptions { Base = baseDocument });

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(CompareChangeKind.Formatted, change.Kind);
        Run changed = Assert.Single(First(comparison.Document).Runs, r => r.FormatRevision is not null);
        Assert.Equal("quick", changed.Text);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ACharacterStyleChangeIsKeptWithTheOldStyle(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("Plain words here.");
        Document b = DocumentCompareTests.Make("Plain words here.");
        First(b).Runs.First().Style = (CharacterStyle)b.Styles.AddBuiltIn(BuiltInStyle.Strong);

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, new DocumentCompareOptions { Base = baseDocument });

        Run run = Assert.Single(First(comparison.Document).Runs);
        Assert.True(run.FormatRevision!.StyleChanged);
        Assert.Null(run.FormatRevision.OldStyle);
        Assert.Equal("Strong", run.Style?.Id);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AParagraphStyleOrAlignmentChangeIsAParagraphFormatChange(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("A heading", "Body text.");
        Document b = DocumentCompareTests.Make("A heading", "Body text.");
        First(b).Style = (ParagraphStyle)b.Styles.AddBuiltIn(BuiltInStyle.Heading1);
        b.Sections[0].Body.Paragraphs.Last().Format.Alignment = ParagraphAlignment.Center;

        DocumentComparison comparison = RoundTrip.CheckFormatting(a, b, new DocumentCompareOptions { Base = baseDocument });

        Assert.Equal([CompareChangeKind.Formatted, CompareChangeKind.Formatted], comparison.Changes.Select(c => c.Kind));
        List<Paragraph> paragraphs = [.. comparison.Document.Sections[0].Body.Paragraphs];
        Assert.True(paragraphs[0].FormatRevision!.StyleChanged);
        Assert.False(paragraphs[1].FormatRevision!.StyleChanged);
    }

    [Fact]
    public void EqualFormattingByAnotherRouteIsNoChange()
    {
        // Bold set directly in one document and through a character style in the other: the same look.
        Document a = DocumentCompareTests.Make("Same look.");
        Document b = DocumentCompareTests.Make("Same look.");
        First(a).Runs.First().Format.Bold = true;
        CharacterStyle strong = b.Styles.AddCharacterStyle("JustBold");
        strong.RunFormat.Bold = true;
        First(b).Runs.First().Style = strong;

        Assert.False(DocumentCompare.Run(a, b).HasChanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaseChangesFollowTheSwitch(bool caseChanges)
    {
        Document a = DocumentCompareTests.Make("The contract shall end.");
        Document b = DocumentCompareTests.Make("The CONTRACT shall end.");
        DocumentCompareOptions options = new() { CaseChanges = caseChanges };

        DocumentComparison comparison = DocumentCompare.Run(a, b, options);

        if (caseChanges)
        {
            RoundTrip.Check(a, b, options);
            DocumentChange change = Assert.Single(comparison.Changes);
            Assert.Equal((CompareChangeKind.Replaced, "contract", "CONTRACT"), (change.Kind, change.OldText, change.NewText));
        }
        else
        {
            Assert.False(comparison.HasChanges);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhitespaceChangesFollowTheSwitch(bool whitespace)
    {
        Document a = DocumentCompareTests.Make("One two three.");
        Document b = DocumentCompareTests.Make("One  two three.");
        DocumentCompareOptions options = new() { Whitespace = whitespace };

        DocumentComparison comparison = DocumentCompare.Run(a, b, options);

        Assert.Equal(whitespace, comparison.HasChanges);
        if (whitespace)
        {
            RoundTrip.Check(a, b, options);
        }
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void CharacterGranularityKeepsGraphemesWhole(CompareBase baseDocument)
    {
        // "e" + combining acute against "e" + combining grave: one grapheme replaced, never half of one.
        Document a = DocumentCompareTests.Make("café noir");
        Document b = DocumentCompareTests.Make("cafè noir");
        DocumentCompareOptions options = new() { Granularity = CompareGranularity.Character, Base = baseDocument };

        DocumentComparison comparison = RoundTrip.Check(a, b, options);

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(("é", "è"), (change.OldText, change.NewText));
    }

    [Fact]
    public void CharacterGranularityMarksLettersWithinAWord()
    {
        Document a = DocumentCompareTests.Make("colour");
        Document b = DocumentCompareTests.Make("color");

        DocumentComparison comparison = RoundTrip.Check(a, b, new DocumentCompareOptions { Granularity = CompareGranularity.Character });

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal((CompareChangeKind.Deleted, "u"), (change.Kind, change.OldText));
    }

    [Fact]
    public void EachFormatChangeAcceptsAndRejectsOnItsOwn()
    {
        Document a = DocumentCompareTests.Make("First words.", "Second words.");
        Document b = DocumentCompareTests.Make("First words.", "Second words.");
        First(b).Runs.First().Format.Italic = true;
        b.Sections[0].Body.Paragraphs.Last().Runs.First().Format.Bold = true;

        DocumentComparison comparison = DocumentCompare.Run(a, b);
        Assert.Equal(2, comparison.Changes.Count);
        comparison.Changes[0].Reject();
        comparison.Changes[1].Accept();

        List<string> looks = RoundTrip.Looks(comparison.Document);
        Assert.Equal(RoundTrip.Looks(a)[0], looks[0]);
        Assert.Equal(RoundTrip.Looks(b)[1], looks[1]);
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void FormatChangesSaveToDocxAndReadBack(CompareBase baseDocument)
    {
        Document a = DocumentCompareTests.Make("Plain words here.", "Second paragraph.");
        Document b = DocumentCompareTests.Make("Plain words here.", "Second paragraph.");
        First(b).Runs.First().Style = (CharacterStyle)b.Styles.AddBuiltIn(BuiltInStyle.Strong);
        First(b).Runs.First().Format.Size = 14;
        b.Sections[0].Body.Paragraphs.Last().Style = (ParagraphStyle)b.Styles.AddBuiltIn(BuiltInStyle.Quote);
        DocumentComparison comparison = DocumentCompare.Run(a, b, new DocumentCompareOptions { Base = baseDocument });

        using MemoryStream stream = new();
        DocxFormat.Save(comparison.Document, stream);
        stream.Position = 0;
        Document back = DocxFormat.Load(stream);

        Document accepted = back.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = back.Clone();
        rejected.Revisions.RejectAll();
        Assert.Equal(RoundTrip.Looks(b), RoundTrip.Looks(accepted));
        Assert.Equal(RoundTrip.Looks(a), RoundTrip.Looks(rejected));
    }
}
