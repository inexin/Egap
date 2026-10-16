using System.Text.RegularExpressions;

namespace Egap.Documents.Tests;

/// <summary>
/// Field updates, accepting and rejecting changes, DocumentBuilder and Find and Replace (Phase 6 Build Plan task 6, design
/// Q3 and Q8).
/// </summary>
public class EditingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 14, 5, 0, TimeSpan.Zero);

    [Fact]
    public void DateAndPropertyFieldsUpdate()
    {
        Document doc = new();
        doc.Properties.Author = "Lars";
        doc.Properties.Title = "Report";
        doc.Properties.SetCustom("Client", "Acme");
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Field date = p.AddField("DATE \\@ \"d MMMM yyyy\"", "old");
        Field author = p.AddField(FieldKind.Author);
        Field title = p.AddField("TITLE \\* Upper", "x");
        Field client = p.AddField("DOCPROPERTY Client", "x");
        Field page = p.AddField(FieldKind.Page);
        Field locked = p.AddField("AUTHOR", "kept");
        locked.Start.IsLocked = true;

        int updated = doc.UpdateFields(Now);

        Assert.Equal(4, updated);
        Assert.Equal("16 October 2026", date.Start.GetField()!.Result);
        Assert.Equal("Lars", author.Start.GetField()!.Result);
        Assert.Equal("REPORT", title.Start.GetField()!.Result);
        Assert.Equal("Acme", client.Start.GetField()!.Result);
        Assert.Equal("", page.Start.GetField()!.Result);
        Assert.Equal("kept", locked.Start.GetField()!.Result);
        Assert.False(author.Start.IsDirty);
    }

    [Fact]
    public void ResultKeepsTheFormattingOfItsFirstRun()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Field field = p.AddField("TIME \\@ \"HH:mm\"", "old");
        ((Run)field.ResultNodes[0]).Format.Bold = true;

        doc.UpdateFields(Now);

        Run result = (Run)field.Start.GetField()!.ResultNodes.Single();
        Assert.Equal("14:05", result.Text);
        Assert.True(result.Format.Bold);
    }

    [Fact]
    public void SequenceRefNoteRefStyleRefAndIf()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        Paragraph heading = body.AddParagraph("Results");
        heading.Style = (ParagraphStyle)doc.Styles.AddBuiltIn(BuiltInStyle.Heading1);
        Paragraph first = body.AddParagraph("Figure ");
        first.Inlines.Add(new BookmarkStart(doc, "Fig1"));
        Field one = first.AddField("SEQ Figure", "?");
        first.Inlines.Add(new BookmarkEnd(doc, "Fig1"));
        Field two = body.AddParagraph("Figure ").AddField("SEQ Figure", "?");
        Field reset = body.AddParagraph().AddField("SEQ Figure \\r 10", "?");
        Paragraph notes = body.AddParagraph("Claim");
        notes.Inlines.Add(new BookmarkStart(doc, "Note1"));
        notes.AddFootnote("source");
        notes.Inlines.Add(new BookmarkEnd(doc, "Note1"));
        Paragraph refs = body.AddParagraph();
        Field reference = refs.AddField("REF Fig1", "?");
        Field noteRef = refs.AddField("NOTEREF Note1", "?");
        Field styleRef = refs.AddField("STYLEREF \"heading 1\"", "?");
        Field ifNumber = refs.AddField("IF 3 > 2 \"yes\" \"no\"", "?");
        Field ifText = refs.AddField("IF \"Lars\" = \"L*\" \"match\" \"none\"", "?");

        doc.UpdateFields(Now);

        Assert.Equal("1", one.Start.GetField()!.Result);
        Assert.Equal("2", two.Start.GetField()!.Result);
        Assert.Equal("10", reset.Start.GetField()!.Result);
        Assert.Equal("1", reference.Start.GetField()!.Result);
        Assert.Equal("1", noteRef.Start.GetField()!.Result);
        Assert.Equal("Results", styleRef.Start.GetField()!.Result);
        Assert.Equal("yes", ifNumber.Start.GetField()!.Result);
        Assert.Equal("match", ifText.Start.GetField()!.Result);
    }

    [Fact]
    public void TableOfContentsListsHeadingsWithPageReferences()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        Paragraph tocParagraph = body.AddParagraph();
        tocParagraph.AddField("TOC \\o \"1-2\" \\h", "old entries");
        foreach ((string text, BuiltInStyle style) in new[] { ("Intro", BuiltInStyle.Heading1), ("Detail", BuiltInStyle.Heading2), ("Deep", BuiltInStyle.Heading3), ("Second", BuiltInStyle.Heading1) })
        {
            body.AddParagraph(text).Style = (ParagraphStyle)doc.Styles.AddBuiltIn(style);
            body.AddParagraph("Body text.");
        }

        doc.UpdateFields(Now);
        doc.UpdateFields(Now);

        Field toc = doc.GetFields().First();
        Assert.Equal(FieldKind.Toc, toc.Kind);
        List<Paragraph> entries = [.. body.Paragraphs.Take(3)];
        Assert.Equal(["Intro", "Detail", "Second"], entries.Select(e => e.Descendants<Run>().First(r => !r.Text.Contains("TOC", StringComparison.Ordinal)).Text));
        Assert.Equal(["TOC1", "TOC2", "TOC1"], entries.Select(e => e.Style!.Id));
        Assert.Equal(3, doc.GetFields().Count(f => f.Kind == FieldKind.PageRef));
        Assert.All(entries, e => Assert.Equal(TabLeader.Dot, e.Format.TabStops!.Single().Leader));
        Assert.Equal(3, doc.GetBookmarks().Count(b => b.Name.StartsWith("_Toc", StringComparison.Ordinal)));
        Assert.Equal("Intro", doc.GetBookmarks().First().Text);
        Assert.Equal("_Toc000000001", body.Paragraphs.First().Descendants<Hyperlink>().First().Anchor);
    }

    [Fact]
    public void AcceptAllAndRejectAll()
    {
        (Document accepted, Paragraph acceptedParagraph) = ChangedDocument();
        (Document rejected, Paragraph rejectedParagraph) = ChangedDocument();

        Assert.Equal(5, accepted.Revisions.Count);
        Assert.Equal(5, accepted.Revisions.AcceptAll());
        Assert.Equal(5, rejected.Revisions.RejectAll());

        Assert.Equal("Keep new text\rsecond\r", accepted.Sections[0].Body.GetText());
        Assert.True(acceptedParagraph.Runs.Last().Format.Bold);
        Assert.Equal("Keep old text\rfirst\rsecond\r", rejected.Sections[0].Body.GetText());
        Assert.Null(rejectedParagraph.Runs.Last().Format.Bold);
        Assert.Empty(accepted.Revisions);
        Assert.Empty(rejected.Revisions);
    }

    [Fact]
    public void OneChangeCanBeAcceptedAlone()
    {
        (Document doc, _) = ChangedDocument();
        TrackedChange insertion = doc.Revisions.First(c => c.Revision?.Kind == RevisionKind.Insertion);

        Assert.Equal("Ada", insertion.Author);
        insertion.Accept();

        Assert.Equal(4, doc.Revisions.Count);
        Assert.Contains("new ", doc.Sections[0].Body.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeletedRowGoesOnAccept()
    {
        Document doc = new();
        Table table = doc.AddSection().Body.AddTable(2, 1);
        table.Rows[1].Revision = new Revision(RevisionKind.Deletion, "Ada");

        doc.Revisions.AcceptAll();

        Assert.Single(table.Rows);
    }

    [Fact]
    public void ReplaceAcrossRunsKeepsTheFirstRunsFormatting()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.AddRun("Hello Wo", new RunFormat { Bold = true });
        p.AddRun("rld and world", new RunFormat { Italic = true });

        int count = doc.Replace("world", "Egap");

        Assert.Equal(2, count);
        Assert.Equal("Hello Egap and Egap", p.GetText());
        Assert.True(p.Runs.First().Format.Bold);
        Assert.Equal("Hello Egap", p.Runs.First().Text);
    }

    [Fact]
    public void ReplaceWithOptionsAndRegex()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        Paragraph p = body.AddParagraph("cat Cat category");
        Paragraph dates = body.AddParagraph("2026-10-16 and 2025-01-02");
        Paragraph tabbed = body.AddParagraph("a");
        tabbed.AddTab();
        tabbed.AddRun("b");

        Assert.Equal(1, p.Replace("cat", "dog", new FindOptions { MatchCase = true, WholeWord = true }));
        Assert.Equal("dog Cat category", p.GetText());
        Assert.Equal(2, dates.Replace(new Regex(@"(\d{4})-(\d\d)-(\d\d)"), "$3.$2.$1"));
        Assert.Equal("16.10.2026 and 02.01.2025", dates.GetText());
        Assert.Equal(1, tabbed.Replace("a\tb", "joined"));
        Assert.Equal("joined", tabbed.GetText());
        Assert.Equal(1, body.Replace(new Regex("Cat"), m => m.Value.ToUpperInvariant()));
        Assert.Equal("dog CAT category", p.GetText());
    }

    [Fact]
    public void ReplaceSkipsDeletedText()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.AddRun("old").Revision = new Revision(RevisionKind.Deletion, "Ada");
        p.AddRun("old");

        Assert.Equal(1, p.Replace("old", "new"));
        Assert.Equal("oldnew", p.GetText());
    }

    [Fact]
    public void BuilderWritesTextParagraphsAndBreaks()
    {
        Document doc = new();
        DocumentBuilder builder = new(doc);
        builder.ParagraphStyle = (ParagraphStyle)doc.Styles.AddBuiltIn(BuiltInStyle.Heading1);
        builder.Writeln("Title");
        builder.ParagraphStyle = doc.Styles.DefaultParagraphStyle;
        builder.Font.Bold = true;
        builder.Write("Bold ");
        builder.Font.Bold = null;
        builder.Write("plain\nnext line");
        builder.InsertBreak(BreakKind.Page);
        builder.Write("after");

        Body body = doc.Sections[0].Body;
        Assert.Equal("Title\rBold plain\rnext line\f\rafter\r", body.GetText());
        Assert.Equal("Heading1", body.Paragraphs.First().Style!.Id);
        Assert.Equal("Normal", body.Paragraphs.ElementAt(1).Style!.Id);
        Assert.True(body.Paragraphs.ElementAt(1).Runs.First().Format.Bold);
        Assert.Null(body.Paragraphs.ElementAt(1).Runs.Last().Format.Bold);
    }

    [Fact]
    public void BuilderInsertsInTheMiddleAndSplitsParagraphs()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.AddRun("Hello ");
        Run world = p.AddRun("world");
        DocumentBuilder builder = new(doc);

        builder.MoveTo(world);
        builder.Write("big ");
        builder.InsertParagraph();

        Assert.Equal("Hello big \rworld\r", doc.Sections[0].Body.GetText());
        Assert.Same(world, builder.CurrentNode);
    }

    [Fact]
    public void BuilderMakesNestedTables()
    {
        Document doc = new();
        DocumentBuilder builder = new(doc);
        builder.Write("Before");
        builder.InsertParagraph();
        Table outer = builder.StartTable();
        builder.InsertCell();
        builder.Write("A");
        builder.InsertCell();
        Table inner = builder.StartTable();
        builder.InsertCell();
        builder.Write("nested");
        builder.EndRow();
        builder.EndTable();
        builder.EndRow();
        builder.InsertCell();
        builder.Write("B");
        builder.EndRow();
        builder.EndTable();
        builder.Write("After");

        Assert.Equal(2, outer.Rows.Count);
        Assert.Equal(2, outer.Rows[0].Cells.Count);
        Assert.Same(outer.Rows[0].Cells[1], inner.Parent);
        Assert.Equal("Before\rA\rnested\r\rB\rAfter\r", doc.Sections[0].Body.GetText());
    }

    [Fact]
    public void BuilderFieldsLinksBookmarksNotesAndSections()
    {
        Document doc = new();
        DocumentBuilder builder = new(doc);
        builder.StartBookmark("Top");
        builder.Write("Start");
        builder.EndBookmark("Top");
        builder.InsertField("PAGE");
        builder.InsertHyperlink("Egap", "https://github.com/inexin/Egap");
        builder.InsertFootnote("A note.");
        Section second = builder.InsertSectionBreak(SectionStart.Continuous);
        builder.Write("In section two");
        builder.MoveToHeaderFooter(HeaderFooterKind.HeaderPrimary);
        builder.Write("Header");
        Assert.True(builder.MoveToBookmark("Top"));
        builder.Write("!");

        Assert.Equal(2, doc.Sections.Count);
        Assert.Equal(SectionStart.Continuous, second.PageSetup.SectionStart);
        Assert.Equal("In section two\r", second.Body.GetText());
        Assert.Equal("Header\r", second.HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText());
        Assert.Equal("Start!", doc.GetBookmark("Top")!.Text);
        Assert.Equal("Hyperlink", doc.Descendants<Hyperlink>().Single().Runs.Single().Style!.Id);
        Assert.Equal(FieldKind.Page, doc.GetFields().Single().Kind);
        Assert.Equal("1", doc.Descendants<Footnote>().Single().Mark);
        Assert.False(builder.MoveToBookmark("Missing"));
    }

    private static (Document Doc, Paragraph Paragraph) ChangedDocument()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        Paragraph p = body.AddParagraph("Keep ");
        p.AddRun("old ").Revision = new Revision(RevisionKind.Deletion, "Ada");
        p.AddRun("new ").Revision = new Revision(RevisionKind.Insertion, "Ada");
        Run changed = p.AddRun("text");
        changed.Format.Bold = true;
        changed.FormatRevision = new FormatRevision("Ada", null, new RunFormat());
        Paragraph first = body.AddParagraph("first");
        first.MarkRevision = new Revision(RevisionKind.Deletion, "Ada");
        body.AddParagraph("second");
        first.Runs.Single().Revision = new Revision(RevisionKind.Deletion, "Ada");
        return (doc, p);
    }
}
