using Egap.Documents.Comparing;
using Egap.Documents.Docx;
using Egap.Documents.Rtf;

namespace Egap.Documents.Tests.Comparing;

public sealed class DocumentCompareTests
{
    [Fact]
    public void SameDocumentsHaveNoChanges()
    {
        Document a = Make("One paragraph.", "Another one.");

        DocumentComparison comparison = RoundTrip.Check(a, a.Clone());

        Assert.False(comparison.HasChanges);
        Assert.Equal(0, comparison.Document.Revisions.Count);
        Assert.Empty(comparison.Warnings);
    }

    [Fact]
    public void ChangedWordsAreMarkedInsideTheParagraph()
    {
        Document a = Make("The quick brown fox jumps over the dog.");
        Document b = Make("The quick red fox jumps over the lazy dog.");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Paragraph result = comparison.Document.Sections[0].Body.Paragraphs.Single();
        Assert.Equal("brown", Text(result, RevisionKind.Deletion));
        Assert.Equal("red lazy", Text(result, RevisionKind.Insertion));
        Assert.Equal(["Replaced \"brown\" with \"red\"", "Inserted \" lazy\""], comparison.Changes.Select(c => c.ToString()));
        Assert.Equal(2, comparison.Statistics.InsertedWords);
        Assert.Equal(1, comparison.Statistics.DeletedWords);
        Assert.Equal(1, comparison.Statistics.ParagraphsTouched);
        Assert.All(comparison.Changes, c => Assert.IsType<Body>(c.Story));
    }

    [Fact]
    public void AddedAndRemovedParagraphsAreWhole()
    {
        Document a = Make("Keep one.", "Gone entirely here.", "Keep two.");
        Document b = Make("Keep one.", "Keep two.", "A new closing paragraph.");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        List<Paragraph> result = [.. comparison.Document.Sections[0].Body.Paragraphs];
        Assert.Equal(4, result.Count);
        Assert.Equal(RevisionKind.Deletion, result[1].MarkRevision?.Kind);
        Assert.Equal(RevisionKind.Insertion, result[3].MarkRevision?.Kind);
        Assert.Equal([CompareChangeKind.Deleted, CompareChangeKind.Inserted], comparison.Changes.Select(c => c.Kind));
    }

    [Fact]
    public void ParagraphsAreSplitAndJoined()
    {
        RoundTrip.Check(Make("First half and second half together."), Make("First half and", "second half together."));
        RoundTrip.Check(Make("First half and", "second half together."), Make("First half and second half together."));
    }

    [Fact]
    public void LastParagraphsCanBeReplaced()
    {
        RoundTrip.Check(Make("Same start.", "Old end one.", "Old end two."), Make("Same start.", "Entirely different words."));
        RoundTrip.Check(Make("Same start."), Make("Same start.", "More.", "And more."));
        RoundTrip.Check(Make("Same start.", "More.", "And more."), Make("Same start."));
        RoundTrip.Check(Make("Only."), Make("Other."));
    }

    [Fact]
    public void ParagraphsBeforeATableCanGo()
    {
        Document a = Make("Intro.", "Goes away.");
        a.Sections[0].Body.AddTable(1, 1).Rows[0].Cells[0].Paragraphs.First().AddRun("Cell");
        a.Sections[0].Body.AddParagraph("After.");
        Document b = Make("Intro.");
        b.Sections[0].Body.AddTable(1, 1).Rows[0].Cells[0].Paragraphs.First().AddRun("Cell");
        b.Sections[0].Body.AddParagraph("After.");

        RoundTrip.Check(a, b);
        RoundTrip.Check(b, a);
    }

    [Fact]
    public void ChangedTablesAreReplacedWhole()
    {
        Document a = Make("Before.");
        Table ta = a.Sections[0].Body.AddTable(2, 2);
        ta.Rows[0].Cells[0].Paragraphs.First().AddRun("old");
        a.Sections[0].Body.AddParagraph("After.");
        Document b = Make("Before.");
        Table tb = b.Sections[0].Body.AddTable(2, 2);
        tb.Rows[0].Cells[0].Paragraphs.First().AddRun("new");
        b.Sections[0].Body.AddParagraph("After.");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Assert.Equal(2, comparison.Document.Sections[0].Body.Tables.Count());
        Assert.Equal([CompareChangeKind.Deleted, CompareChangeKind.Inserted], comparison.Changes.Select(c => c.Kind));
    }

    [Fact]
    public void FormattingAndStructureOfEqualTextIsKept()
    {
        Document a = Make("Plain words here.");
        Document b = new();
        Paragraph p = b.AddSection().Body.AddParagraph();
        p.AddRun("Plain ", new RunFormat { Bold = true });
        p.AddRun("words there.");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Paragraph result = comparison.Document.Sections[0].Body.Paragraphs.Single();
        Assert.True(result.Runs.First().Format.Bold);
        Assert.Equal("here", Text(result, RevisionKind.Deletion));
    }

    [Fact]
    public void AtomsCompareAsWholes()
    {
        Document a = Make("Total:");
        a.Sections[0].Body.Paragraphs.First().AddField("PAGE", "1");
        Document b = Make("Total:");
        b.Sections[0].Body.Paragraphs.First().AddField("PAGE", "2");
        b.Sections[0].Body.Paragraphs.First().AddTab();

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Paragraph result = comparison.Document.Sections[0].Body.Paragraphs.Single();
        Assert.Equal(5, result.Inlines.Count(i => i.Revision?.Kind == RevisionKind.Deletion));
        Assert.Equal(6, result.Inlines.Count(i => i.Revision?.Kind == RevisionKind.Insertion));
    }

    [Fact]
    public void BookmarksOfTheBaseStay()
    {
        Document a = Make("Alpha beta gamma.");
        Document b = Make("Alpha delta gamma.");
        Paragraph p = b.Sections[0].Body.Paragraphs.First();
        p.Inlines.Insert(0, new BookmarkStart(b, "start"));
        p.Inlines.Add(new BookmarkEnd(b, "start"));

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Assert.Equal("Alpha delta gamma.", comparison.Document.GetBookmark("start")?.Text.Replace("beta", "", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseOriginalBuildsOnTheOriginal()
    {
        Document a = Make("Old title", "Same text.");
        a.Sections[0].PageSetup.Orientation = PageOrientation.Landscape;
        Document b = Make("New title", "Same text.");

        DocumentComparison comparison = RoundTrip.Check(a, b, new DocumentCompareOptions { Base = CompareBase.Original });

        Assert.Equal(PageOrientation.Landscape, comparison.Document.Sections[0].PageSetup.Orientation);
        Assert.Equal(CompareChangeKind.Replaced, comparison.Changes.Single().Kind);
    }

    [Fact]
    public void InputsAreLeftAlone()
    {
        Document a = Make("One two three.");
        Document b = Make("One four three.");
        string before = a.Sections[0].Body.GetText() + b.Sections[0].Body.GetText();

        DocumentCompare.Run(a, b);

        Assert.Equal(before, a.Sections[0].Body.GetText() + b.Sections[0].Body.GetText());
        Assert.Equal(0, a.Revisions.Count + b.Revisions.Count);
    }

    [Fact]
    public void AuthorAndDateAreStamped()
    {
        DateTimeOffset date = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        DocumentComparison comparison = DocumentCompare.Run(Make("a b"), Make("a c"), new DocumentCompareOptions { Author = "Ada", Date = date });

        Assert.All(comparison.Document.Revisions, c =>
        {
            Assert.Equal("Ada", c.Author);
            Assert.Equal(date, c.Date);
        });
    }

    [Fact]
    public void CaseAndWhitespaceSwitches()
    {
        Document a = Make("Hello  World");
        Document b = Make("hello World");

        Assert.True(DocumentCompare.Run(a, b).HasChanges);
        Assert.True(DocumentCompare.Run(a, b, new DocumentCompareOptions { CaseChanges = false }).HasChanges);
        Assert.False(DocumentCompare.Run(a, b, new DocumentCompareOptions { CaseChanges = false, Whitespace = false }).HasChanges);
    }

    [Fact]
    public void CharacterGranularityMarksLetters()
    {
        DocumentComparison comparison = RoundTrip.Check(Make("colour"), Make("color"), new DocumentCompareOptions { Granularity = CompareGranularity.Character });

        Assert.Equal("u", comparison.Changes.Single().OldText);
        Assert.Equal(CompareChangeKind.Deleted, comparison.Changes.Single().Kind);
    }

    [Fact]
    public void ExistingRevisionsAreAcceptedOrRefused()
    {
        Document a = Make("Text one.");
        a.Sections[0].Body.Paragraphs.First().AddRun(" extra").Revision = new Revision(RevisionKind.Insertion, "Bob");
        Document b = Make("Text one. extra and more");

        DocumentComparison comparison = DocumentCompare.Run(a, b);

        Assert.Equal(DocumentCompareWarningKind.ExistingRevisionsAccepted, comparison.Warnings.Single().Kind);
        Assert.Equal("and more", string.Concat(comparison.Changes.Select(c => c.NewText)).Trim());
        Assert.Throws<InvalidOperationException>(() => DocumentCompare.Run(a, b, new DocumentCompareOptions { ExistingRevisions = ExistingRevisionHandling.Throw }));
        Assert.Equal(1, a.Revisions.Count);
    }

    [Fact]
    public void OneChangeCanBeAcceptedOrRejected()
    {
        DocumentComparison comparison = DocumentCompare.Run(Make("one two three", "four"), Make("one 2 three", "four", "five"));

        comparison.Changes[0].Reject();
        comparison.Changes[1].Accept();

        Assert.Equal(0, comparison.Document.Revisions.Count);
        Assert.Equal(["P|one two three", "P|four", "P|five"], RoundTrip.Blocks(comparison.Document));
    }

    [Fact]
    public void SectionsAreComparedAsOneRun()
    {
        Document a = Make("First section.");
        a.AddSection().Body.AddParagraph("Second section.");
        Document b = Make("First section.", "Second section, changed.");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Assert.Contains(comparison.Warnings, w => w.Kind == DocumentCompareWarningKind.SectionsDiffer);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("rtf")]
    public void ResultSavesAndReadsBackWithTheSameChanges(string format)
    {
        Document a = Make("Keep this.", "Change these few words.", "Drop this paragraph.");
        Document b = Make("Keep this.", "Change those few words now.", "Add this paragraph instead of it.");
        DocumentComparison comparison = RoundTrip.Check(a, b);

        Document read = format switch
        {
            "docx" => Trip(s => DocxFormat.Save(comparison.Document, s), s => DocxFormat.Load(s)),
            _ => Trip(s => RtfFormat.Save(comparison.Document, s), s => RtfFormat.Load(s)),
        };

        Assert.True(read.Revisions.Count > 0);
        Document accepted = read.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = read.Clone();
        rejected.Revisions.RejectAll();
        Assert.Equal(RoundTrip.Blocks(b), RoundTrip.Blocks(accepted));
        Assert.Equal(RoundTrip.Blocks(a), RoundTrip.Blocks(rejected));
    }

    private static Document Trip(Action<Stream> save, Func<Stream, Document> load)
    {
        using MemoryStream stream = new();
        save(stream);
        stream.Position = 0;
        return load(stream);
    }

    internal static Document Make(params string[] paragraphs)
    {
        Document document = new();
        Body body = document.AddSection().Body;
        foreach (string text in paragraphs)
        {
            body.AddParagraph(text);
        }

        return document;
    }

    private static string Text(Paragraph paragraph, RevisionKind kind)
    {
        return string.Concat(paragraph.Runs.Where(r => r.Revision?.Kind == kind).Select(r => r.Text));
    }
}
