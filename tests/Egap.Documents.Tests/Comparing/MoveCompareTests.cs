using System.IO.Compression;

using Egap.Documents.Comparing;
using Egap.Documents.Docx;
using Egap.Documents.Rtf;

namespace Egap.Documents.Tests.Comparing;

/// <summary>Moved paragraphs, inputs from different formats and the comparison's counts (6.19 Build Plan tasks 7 and 8).</summary>
public sealed class MoveCompareTests
{
    private const string Moved = "This whole paragraph moves to the end of the document.";

    private static Document Original()
    {
        return DocumentCompareTests.Make(Moved, "The second paragraph stays.", "The third one stays too.");
    }

    private static Document Revised()
    {
        return DocumentCompareTests.Make("The second paragraph stays.", "The third one stays too.", Moved);
    }

    private static List<Revision> Revisions(Document document, RevisionKind kind)
    {
        return [.. document.Descendants<Paragraph>().Select(p => p.MarkRevision).OfType<Revision>().Where(r => r.Kind == kind)];
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AMovedParagraphIsOneMove(CompareBase baseDocument)
    {
        DocumentComparison comparison = RoundTrip.Check(Original(), Revised(), new DocumentCompareOptions { Base = baseDocument });

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(CompareChangeKind.Moved, change.Kind);
        Assert.Equal((Moved, Moved), (change.OldText, change.NewText));
        Assert.Equal($"Moved \"{Moved}\"", change.ToString());
        Revision from = Assert.Single(Revisions(comparison.Document, RevisionKind.MoveFrom));
        Revision to = Assert.Single(Revisions(comparison.Document, RevisionKind.MoveTo));
        Assert.NotNull(from.MoveName);
        Assert.Equal(from.MoveName, to.MoveName);
        Assert.Equal(new DocumentCompareStatistics { MovedWords = 10, ParagraphsTouched = 2 }, comparison.Statistics);
    }

    [Fact]
    public void AMoveAcceptsAndRejectsAsOneChange()
    {
        DocumentComparison accepted = DocumentCompare.Run(Original(), Revised());
        accepted.Changes.Single().Accept();
        Assert.Equal(RoundTrip.Blocks(Revised()), RoundTrip.Blocks(accepted.Document));

        DocumentComparison rejected = DocumentCompare.Run(Original(), Revised());
        rejected.Changes.Single().Reject();
        Assert.Equal(RoundTrip.Blocks(Original()), RoundTrip.Blocks(rejected.Document));
    }

    [Fact]
    public void AShortParagraphIsNotAMove()
    {
        Document a = DocumentCompareTests.Make("Short line.", "The second paragraph stays.", "The third one stays too.");
        Document b = DocumentCompareTests.Make("The second paragraph stays.", "The third one stays too.", "Short line.");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Assert.Equal([CompareChangeKind.Inserted, CompareChangeKind.Deleted], comparison.Changes.Select(c => c.Kind).Order());
    }

    [Fact]
    public void ARunOfParagraphsMovesTogether()
    {
        Document a = DocumentCompareTests.Make("First moved paragraph.", "Second moved paragraph here.", "Stays put.", "Also stays put.");
        Document b = DocumentCompareTests.Make("Stays put.", "Also stays put.", "First moved paragraph.", "Second moved paragraph here.");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(CompareChangeKind.Moved, change.Kind);
        Assert.Equal(4, change.TrackedChanges.Count(c => c.Target == TrackedChangeTarget.ParagraphMark));
        Assert.Equal(7, comparison.Statistics.MovedWords);

        // Through DOCX, one range over both paragraphs, and every mark and run keeps the move's name.
        using MemoryStream stream = new();
        DocxFormat.Save(comparison.Document, stream);
        stream.Position = 0;
        Document back = DocxFormat.Load(stream);
        Assert.Equal(4, back.Descendants<Paragraph>().Count(p => p.MarkRevision is { MoveName: "move1" }));
        Assert.Equal(4, back.Descendants<Run>().Count(r => r.Revision is { MoveName: "move1" }));
        Assert.DoesNotContain(back.Descendants<Run>(), r => r.Revision is { MoveName: null });
        RoundTrip.Check(a, b, back);
    }

    [Fact]
    public void MovesCanBeLeftOut()
    {
        DocumentComparison comparison = RoundTrip.Check(Original(), Revised(), new DocumentCompareOptions { Moves = false });

        Assert.Equal([CompareChangeKind.Inserted, CompareChangeKind.Deleted], comparison.Changes.Select(c => c.Kind).Order());
        Assert.Empty(Revisions(comparison.Document, RevisionKind.MoveFrom));
    }

    [Fact]
    public void DocxWritesNamedMoveRangesAndReadsThemBack()
    {
        DocumentComparison comparison = DocumentCompare.Run(Original(), Revised());
        using MemoryStream stream = new();
        DocxFormat.Save(comparison.Document, stream);

        stream.Position = 0;
        using (ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            using StreamReader reader = new(zip.GetEntry("word/document.xml")!.Open());
            string xml = reader.ReadToEnd();
            Assert.Contains("<w:moveFromRangeStart w:id=", xml, StringComparison.Ordinal);
            Assert.Contains("<w:moveToRangeStart w:id=", xml, StringComparison.Ordinal);
            Assert.Equal(2, CountOf(xml, "w:name=\"move1\""));
            Assert.Equal(1, CountOf(xml, "<w:moveFromRangeEnd "));
            Assert.Equal(1, CountOf(xml, "<w:moveToRangeEnd "));
        }

        stream.Position = 0;
        Document back = DocxFormat.Load(stream);
        Assert.Equal("move1", Assert.Single(Revisions(back, RevisionKind.MoveFrom)).MoveName);
        Assert.Equal("move1", Assert.Single(Revisions(back, RevisionKind.MoveTo)).MoveName);
        Assert.All(back.Descendants<Run>().Where(r => r.Revision is not null), r => Assert.Equal("move1", r.Revision!.MoveName));
        RoundTrip.Check(Original(), Revised(), back);

        // Saved again, the ranges are written once, not kept and written again.
        using MemoryStream again = new();
        DocxFormat.Save(back, again);
        again.Position = 0;
        using ZipArchive zipAgain = new(again, ZipArchiveMode.Read);
        using StreamReader readerAgain = new(zipAgain.GetEntry("word/document.xml")!.Open());
        Assert.Equal(2, CountOf(readerAgain.ReadToEnd(), "w:name=\"move1\""));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static Document Loaded(Document document, bool rtf)
    {
        using MemoryStream stream = new();
        if (rtf)
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

    private static Document WithBold(bool bold)
    {
        Document document = DocumentCompareTests.Make();
        Paragraph paragraph = document.Sections[0].Body.AddParagraph();
        paragraph.AddRun("Some ");
        paragraph.AddRun("important").Format.Bold = bold;
        paragraph.AddRun(" words.");
        return document;
    }

    [Fact]
    public void InputsFromDifferentFormatsLeaveFormattingOut()
    {
        Document a = Loaded(WithBold(false), rtf: false);
        Document b = Loaded(WithBold(true), rtf: true);
        Assert.Equal((DocumentFormatKind.Docx, DocumentFormatKind.Rtf), (a.SourceFormat, b.SourceFormat));
        Assert.Equal(DocumentFormatKind.Docx, a.Clone().SourceFormat);

        DocumentComparison comparison = DocumentCompare.Run(a, b);

        Assert.False(comparison.HasChanges);
        Assert.Equal(DocumentCompareWarningKind.FormattingNotCompared, Assert.Single(comparison.Warnings).Kind);

        DocumentComparison forced = DocumentCompare.Run(a, b, new DocumentCompareOptions { Formatting = true });
        Assert.Contains(forced.Changes, c => c.Kind == CompareChangeKind.Formatted);
        Assert.Empty(forced.Warnings);
    }

    [Fact]
    public void FormattingCanBeLeftOut()
    {
        Document a = WithBold(false);
        Document b = WithBold(true);
        Assert.Equal(DocumentFormatKind.Unknown, a.SourceFormat);

        DocumentComparison compared = RoundTrip.CheckFormatting(a, b);
        Assert.Equal(1, compared.Statistics.FormattingChanges);

        DocumentComparison ignored = DocumentCompare.Run(a, b, new DocumentCompareOptions { Formatting = false });
        Assert.False(ignored.HasChanges);
        Assert.Empty(ignored.Warnings);
    }

    [Fact]
    public void InputsFromTheSameFormatCompareFormatting()
    {
        DocumentComparison comparison = DocumentCompare.Run(Loaded(WithBold(false), rtf: false), Loaded(WithBold(true), rtf: false));

        Assert.Equal(CompareChangeKind.Formatted, Assert.Single(comparison.Changes).Kind);
        Assert.Empty(comparison.Warnings);
    }
}
