using Egap.Documents.Comparing;

namespace Egap.Documents.Tests.Comparing;

/// <summary>Section breaks in a comparison: tracked as paragraph marks, so accepting or rejecting takes them along.</summary>
public sealed class SectionBreakCompareTests
{
    [Fact]
    public void AcceptingADeletedMarkThatEndsASectionJoinsTheSections()
    {
        Document document = DocumentCompareTests.Make("One", "Two");
        Section second = document.AddSection();
        second.Body.AddParagraph("Three");
        second.PageSetup.Orientation = PageOrientation.Landscape;
        document.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("First head");
        document.Sections[0].Body.Paragraphs.Last().MarkRevision = new Revision(RevisionKind.Deletion, "Egap", null);

        Document rejected = document.Clone();
        rejected.Revisions.RejectAll();
        document.Revisions.AcceptAll();

        // The section's text joins the next section, which keeps its own page setup; the paragraph joins the next one.
        Section only = Assert.Single(document.Sections);
        Assert.Equal(PageOrientation.Landscape, only.PageSetup.Orientation);
        Assert.Equal(["One", "TwoThree"], only.Body.Paragraphs.Select(p => p.GetText().TrimEnd('\r')));
        Assert.Equal(2, rejected.Sections.Count);
        Assert.Equal("First head\r", rejected.Sections[0].HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText());
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ABreakOnlyInTheOriginalIsTrackedAfterAParagraphInBoth(CompareBase baseDocument)
    {
        Document original = TwoSections("Introduction to the report.", "The findings follow here.", "Head of part one");
        Document revised = DocumentCompareTests.Make("Introduction to the report.", "The findings follow here.");

        DocumentComparison comparison = RoundTrip.CheckStories(original, revised, new DocumentCompareOptions { Base = baseDocument });

        Assert.Contains(comparison.Warnings, w => w.Kind == DocumentCompareWarningKind.SectionsDiffer);
        Assert.Equal(0, comparison.Statistics.InsertedWords + comparison.Statistics.DeletedWords);
        (Document accepted, Document rejected) = Settle(comparison.Document);
        Assert.Single(accepted.Sections);
        Assert.Equal(2, rejected.Sections.Count);
        Assert.Equal(["Introduction to the report."], rejected.Sections[0].Body.Paragraphs.Select(p => p.GetText().TrimEnd('\r')));
        Assert.Equal("Head of part one\r", rejected.Sections[0].HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText());
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ABreakOnlyInTheRevisedDocumentIsTrackedToo(CompareBase baseDocument)
    {
        Document original = DocumentCompareTests.Make("Introduction to the report.", "The findings follow here.");
        Document revised = TwoSections("Introduction to the report.", "The findings follow here.", "Head of part one");

        DocumentComparison comparison = RoundTrip.CheckStories(original, revised, new DocumentCompareOptions { Base = baseDocument });

        (Document accepted, Document rejected) = Settle(comparison.Document);
        Assert.Equal(2, accepted.Sections.Count);
        Assert.Single(rejected.Sections);
        Assert.Equal("Head of part one\r", accepted.Sections[0].HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText());
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ABreakAfterADeletedParagraphIsThatParagraphsMark(CompareBase baseDocument)
    {
        Document original = TwoSections("Introduction to the report.", "The findings follow here.", null);
        original.Sections[0].Body.AddParagraph("A closing line only the original has.");
        Document revised = DocumentCompareTests.Make("Introduction to the report.", "The findings follow here.");

        DocumentComparison comparison = RoundTrip.Check(original, revised, new DocumentCompareOptions { Base = baseDocument });

        Paragraph closing = comparison.Document.Sections[0].Body.Paragraphs.Last();
        Assert.Equal(RevisionKind.Deletion, closing.MarkRevision?.Kind);
        Assert.Equal("A closing line only the original has.", closing.GetText().TrimEnd('\r'));
        (Document accepted, Document rejected) = Settle(comparison.Document);
        Assert.Single(accepted.Sections);
        Assert.Equal(2, rejected.Sections.Count);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("rtf")]
    public void TrackedBreaksRoundTripThroughTheWriters(string format)
    {
        Document original = TwoSections("Introduction to the report.", "The findings follow here.", "Head of part one");
        Document revised = DocumentCompareTests.Make("Introduction to the report.", "The findings follow here.");
        revised.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Head of the report");

        foreach (CompareBase baseDocument in (CompareBase[])[CompareBase.Revised, CompareBase.Original])
        {
            DocumentComparison comparison = DocumentCompare.Run(original, revised, new DocumentCompareOptions { Base = baseDocument });
            Document back = WriterRoundTripTests.Through(comparison.Document, format);
            (Document accepted, Document rejected) = Settle(back);
            Assert.Single(accepted.Sections);
            Assert.Equal(2, rejected.Sections.Count);
            Assert.Equal(RoundTrip.Stories(WriterRoundTripTests.Through(revised, format)), RoundTrip.Stories(accepted));
            Assert.Equal(RoundTrip.Stories(WriterRoundTripTests.Through(original, format)), RoundTrip.Stories(rejected));
            WriterRoundTripTests.Check(original, revised, comparison.Document, format);
        }
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AParagraphDeletedAtTheEndOfASectionLeavesTheBreak(CompareBase baseDocument)
    {
        Document original = TwoSections("Introduction to the report.", "The findings follow here.", null);
        original.Sections[0].Body.Paragraphs.First().Format.Alignment = ParagraphAlignment.Center;
        Document revised = TwoSections("Introduction to the report.", "The findings follow here.", null);
        revised.Sections[0].Body.Paragraphs.First().Format.Alignment = ParagraphAlignment.Center;
        original.Sections[0].Body.AddParagraph("A closing line that goes.");

        DocumentComparison comparison = RoundTrip.CheckFormatting(original, revised, new DocumentCompareOptions { Base = baseDocument });

        (Document accepted, Document rejected) = Settle(comparison.Document);
        Assert.Equal(2, accepted.Sections.Count);
        Assert.Equal(2, rejected.Sections.Count);
    }

    private static Document TwoSections(string first, string second, string? header)
    {
        Document document = DocumentCompareTests.Make(first);
        if (header is not null)
        {
            document.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph(header);
        }

        document.AddSection().Body.AddParagraph(second);
        return document;
    }

    private static (Document Accepted, Document Rejected) Settle(Document document)
    {
        Document accepted = document.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = document.Clone();
        rejected.Revisions.RejectAll();
        return (accepted, rejected);
    }
}
