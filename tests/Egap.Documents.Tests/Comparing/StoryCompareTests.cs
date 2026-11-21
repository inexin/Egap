using Egap.Documents.Comparing;
using Egap.Documents.Docx;

namespace Egap.Documents.Tests.Comparing;

/// <summary>Headers and footers, notes, comments, text boxes, fields and pictures compared (6.19 Build Plan task 6).</summary>
public sealed class StoryCompareTests
{
    private static Paragraph First(Document document)
    {
        return document.Sections[0].Body.Paragraphs.First();
    }

    private static Document WithHeader(string header, string? footer = null, HeaderFooterKind kind = HeaderFooterKind.HeaderPrimary)
    {
        Document document = DocumentCompareTests.Make("Body text.");
        document.Sections[0].HeadersFooters.GetOrAdd(kind).AddParagraph(header);
        if (footer is not null)
        {
            document.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.FooterPrimary).AddParagraph(footer);
        }

        return document;
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void HeadersAndFootersAreComparedByKind(CompareBase baseDocument)
    {
        Document a = WithHeader("Company draft", "Page footer");
        Document b = WithHeader("Company final", "Page footer");
        b.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderFirst).AddParagraph("Title page header");

        DocumentComparison comparison = RoundTrip.CheckStories(a, b, new DocumentCompareOptions { Base = baseDocument });

        Assert.Equal(2, comparison.Changes.Count);
        Assert.All(comparison.Changes, c => Assert.IsType<HeaderFooter>(c.Story));
        DocumentChange replaced = Assert.Single(comparison.Changes, c => c.Kind == CompareChangeKind.Replaced);
        Assert.Equal(("draft", "final"), (replaced.OldText, replaced.NewText));
    }

    [Fact]
    public void HeadersAndFootersCanBeLeftOut()
    {
        Document a = WithHeader("Company draft");
        Document b = WithHeader("Company final");

        DocumentComparison comparison = DocumentCompare.Run(a, b, new DocumentCompareOptions { HeadersFooters = false });

        Assert.False(comparison.HasChanges);
        Assert.Equal("Company final", comparison.Document.Sections[0].HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText().TrimEnd('\r'));
    }

    private static Document WithNote(string text, string note)
    {
        Document document = DocumentCompareTests.Make(text);
        First(document).AddFootnote(note);
        First(document).AddRun(" More.");
        return document;
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ANoteIsComparedAsItsOwnStory(CompareBase baseDocument)
    {
        Document a = WithNote("Claim", "See the old report.");
        Document b = WithNote("Claim", "See the new report.");

        DocumentComparison comparison = RoundTrip.CheckStories(a, b, new DocumentCompareOptions { Base = baseDocument });

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(("old", "new"), (change.OldText, change.NewText));
        Assert.IsType<NoteStory>(change.Story);
        Assert.Null(First(comparison.Document).Inlines.OfType<Footnote>().Single().Revision);
    }

    [Fact]
    public void ANoteOnlyInOneDocumentIsInsertedWhole()
    {
        Document a = DocumentCompareTests.Make("Claim More.");
        Document b = WithNote("Claim", "Added note.");

        DocumentComparison comparison = RoundTrip.CheckStories(a, b);

        Assert.Equal(RevisionKind.Insertion, First(comparison.Document).Inlines.OfType<Footnote>().Single().Revision?.Kind);
    }

    [Fact]
    public void NoteTextCanBeLeftOut()
    {
        Document a = WithNote("Claim", "See the old report.");
        Document b = WithNote("Claim", "See the new report.");

        Assert.False(DocumentCompare.Run(a, b, new DocumentCompareOptions { Notes = false }).HasChanges);
    }

    private static Document WithShape(string text, double width = 100)
    {
        Document document = DocumentCompareTests.Make("Before ");
        Shape shape = new(document, ShapeKind.Rectangle, width, 50);
        shape.AddTextBox().AddParagraph(text);
        First(document).Inlines.Add(shape);
        First(document).AddRun(" after.");
        return document;
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void ATextBoxIsComparedAsItsOwnStory(CompareBase baseDocument)
    {
        Document a = WithShape("Old caption here");
        Document b = WithShape("New caption here");

        DocumentComparison comparison = RoundTrip.CheckStories(a, b, new DocumentCompareOptions { Base = baseDocument });

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.IsType<TextBoxStory>(change.Story);
        Assert.False(DocumentCompare.Run(a, b, new DocumentCompareOptions { TextBoxes = false }).HasChanges);
    }

    [Fact]
    public void AResizedShapeIsDeletedAndInserted()
    {
        Document a = WithShape("Caption", width: 100);
        Document b = WithShape("Caption", width: 140);

        DocumentComparison comparison = RoundTrip.CheckStories(a, b);

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(CompareChangeKind.Replaced, change.Kind);
        Assert.Equal(2, First(comparison.Document).Inlines.OfType<Shape>().Count());
    }

    private static Document WithField(string code, string result)
    {
        Document document = DocumentCompareTests.Make("Total: ");
        First(document).AddField(code, result);
        return document;
    }

    [Fact]
    public void AFieldIsOneTokenComparedByCodeAndResult()
    {
        Document a = WithField("PAGE \\* roman", "iv");
        Document b = WithField("PAGE \\* arabic", "iv");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(CompareChangeKind.Replaced, change.Kind);
        Assert.False(DocumentCompare.Run(a, b, new DocumentCompareOptions { Fields = false }).HasChanges);
        Assert.True(DocumentCompare.Run(a, WithField("PAGE \\* roman", "v"), new DocumentCompareOptions { Fields = false }).HasChanges);
    }

    private static Document WithPicture(byte value, double width)
    {
        Document document = DocumentCompareTests.Make("Figure: ");
        First(document).Inlines.Add(new Picture(document, new ImageData(new byte[] { 0x89, 0x50, 0x4E, 0x47, value }, "image/png"), width, 40));
        return document;
    }

    [Fact]
    public void PicturesGoByImageAndSize()
    {
        Assert.False(DocumentCompare.Run(WithPicture(1, 60), WithPicture(1, 60)).HasChanges);
        Assert.Equal(CompareChangeKind.Replaced, Assert.Single(RoundTrip.Check(WithPicture(1, 60), WithPicture(2, 60)).Changes).Kind);
        Assert.Equal(CompareChangeKind.Replaced, Assert.Single(RoundTrip.Check(WithPicture(1, 60), WithPicture(1, 80)).Changes).Kind);
    }

    private static Document WithComment(string text, string anchor, string comment)
    {
        Document document = DocumentCompareTests.Make();
        Paragraph paragraph = document.Sections[0].Body.AddParagraph();
        int at = text.IndexOf(anchor, StringComparison.Ordinal);
        paragraph.AddRun(text[..at]);
        Comment note = new(document, "Reviewer");
        note.AddParagraph(comment);
        paragraph.Inlines.Add(new CommentRangeStart(document, note));
        paragraph.AddRun(anchor);
        paragraph.Inlines.Add(new CommentRangeEnd(document, note.Id));
        paragraph.Inlines.Add(note);
        paragraph.AddRun(text[(at + anchor.Length)..]);
        return document;
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void TheResultHasTheRevisedComments(CompareBase baseDocument)
    {
        Document a = WithComment("The parties agree within ten days.", "ten days", "Too short?");
        Document b = WithComment("The parties agree within thirty days.", "parties", "Name them.");

        DocumentComparison comparison = RoundTrip.CheckStories(a, b, new DocumentCompareOptions { Base = baseDocument });

        Comment comment = Assert.Single(comparison.Document.Descendants<Comment>());
        Assert.Equal("Name them.", comment.Story.GetText().TrimEnd('\r'));
        Assert.Null(comment.Revision);
        Assert.All(comparison.Changes, c => Assert.IsType<Body>(c.Story));
    }

    [Fact]
    public void AnUnchangedCommentStaysWhereItWas()
    {
        Document a = WithComment("Keep this sentence as it is.", "sentence", "Fine.");
        Document b = WithComment("Keep this sentence as it is.", "sentence", "Fine.");

        DocumentComparison comparison = RoundTrip.CheckStories(a, b, new DocumentCompareOptions { Base = CompareBase.Original });

        Assert.False(comparison.HasChanges);
        Assert.Single(comparison.Document.Descendants<Comment>());
    }

    [Fact]
    public void CommentsCanBeLeftAsTheBaseHasThem()
    {
        Document a = WithComment("The parties agree.", "parties", "Old remark.");
        Document b = WithComment("The parties agree.", "agree", "New remark.");

        DocumentComparison comparison = DocumentCompare.Run(a, b, new DocumentCompareOptions { Base = CompareBase.Original, Comments = false });

        Assert.Equal("Old remark.", comparison.Document.Descendants<Comment>().Single().Story.GetText().TrimEnd('\r'));
    }

    [Fact]
    public void StoryChangesSaveToDocxAndReadBack()
    {
        Document a = WithNote("Claim", "See the old report.");
        a.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Draft header");
        Document b = WithNote("Claim", "See the new report.");
        b.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Final header");
        DocumentComparison comparison = DocumentCompare.Run(a, b);

        using MemoryStream stream = new();
        DocxFormat.Save(comparison.Document, stream);
        stream.Position = 0;
        Document back = DocxFormat.Load(stream);

        Document accepted = back.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = back.Clone();
        rejected.Revisions.RejectAll();
        Assert.Equal(RoundTrip.Stories(b), RoundTrip.Stories(accepted));
        Assert.Equal(RoundTrip.Stories(a), RoundTrip.Stories(rejected));
    }
}
