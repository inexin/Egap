namespace Egap.Documents.Tests;

/// <summary>
/// Fields, bookmarks, hyperlinks, notes, comments, tracked changes, content controls, drawings, islands and extension data
/// (Phase 6 Build Plan task 5, design Q4, Q7, Q8).
/// </summary>
public class RichContentTests
{
    [Fact]
    public void FieldHasCodeResultAndKind()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph("Page ");

        Field field = p.AddField(FieldKind.Page);
        Field date = p.AddField(" DATE \\@ \"yyyy\" ", "2026");

        Assert.Equal(" PAGE ", field.Code);
        Assert.Equal(FieldKind.Page, field.Kind);
        Assert.True(field.Start.IsDirty);
        Assert.Equal("", field.Result);
        Assert.Equal(FieldKind.Date, date.Kind);
        Assert.Equal("2026", date.Result);
        Assert.Equal("Page \u0013 PAGE \u0014\u0015\u0013 DATE \\@ \"yyyy\" \u00142026\u0015", p.GetText());
        Assert.Equal(2, doc.GetFields().Count());
    }

    [Fact]
    public void NestedFieldAndResultOverParagraphs()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        Paragraph first = body.AddParagraph();
        first.Inlines.Add(new FieldStart(doc));
        first.AddRun(" TOC \\o ");
        first.Inlines.Add(new FieldSeparator(doc));
        first.AddRun("Chapter 1\t");
        first.AddField("PAGEREF _Toc1", "3");
        Paragraph second = body.AddParagraph("Chapter 2\t4");
        second.Inlines.Add(new FieldEnd(doc));

        Field toc = doc.GetFields().First();

        Assert.Equal(FieldKind.Toc, toc.Kind);
        Assert.Equal(" TOC \\o ", toc.Code);
        Assert.Equal("Chapter 1\t 3\rChapter 2\t4", toc.Result.Replace("PAGEREF _Toc1", "", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal));
        Assert.Equal(FieldKind.PageRef, doc.GetFields().ElementAt(1).Kind);
        Assert.Null(new FieldStart(doc).GetField());
    }

    [Fact]
    public void BookmarksPairByNameAndGiveTheirText()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        Paragraph a = body.AddParagraph();
        a.Inlines.Add(new BookmarkStart(doc, "Intro"));
        a.AddRun("Hello");
        Paragraph b = body.AddParagraph("world");
        b.Inlines.Add(new BookmarkEnd(doc, "Intro"));
        b.Inlines.Add(new BookmarkStart(doc, "Place"));

        Assert.Equal("Hello\rworld", doc.GetBookmark("intro")!.Text);
        Assert.Null(doc.GetBookmark("Place")!.End);
        Assert.Equal("", doc.GetBookmark("Place")!.Text);
        Assert.Null(doc.GetBookmark("Missing"));
        Assert.Throws<ArgumentException>(() => new BookmarkStart(doc, ""));
    }

    [Fact]
    public void HyperlinkHoldsRunsAndRefusesNesting()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph("See ");
        Hyperlink link = p.AddHyperlink("Egap", "https://github.com/inexin/Egap");
        link.Runs.Single().Style = (CharacterStyle)doc.Styles.AddBuiltIn(BuiltInStyle.Hyperlink);

        Assert.Equal("See Egap", p.GetText());
        Assert.Same(p, link.Runs.Single().Paragraph);
        Assert.Equal(UnderlineStyle.Single, link.Runs.Single().GetEffectiveFormat().Underline);
        Assert.Throws<ArgumentException>(() => link.Inlines.Add(new Hyperlink(doc)));
        InlineContentControl control = new(doc);
        link.Inlines.Add(control);
        Assert.Throws<ArgumentException>(() => control.Inlines.Add(new Hyperlink(doc)));
    }

    [Fact]
    public void FootnoteKeepsItsTextInItsOwnStory()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph("Claim");
        Footnote note = p.AddFootnote("Source: a study.");
        p.AddRun(".");

        Assert.Equal("Claim\u0002.", p.GetText());
        Assert.Equal("Source: a study.\r", note.Story.GetText());
        Assert.Same(note, note.Story.Note);
        Assert.Same(note.Story, note.Story.Paragraphs.Single().Runs.Single().GetAncestor<Story>());
        Assert.Throws<ArgumentException>(() => note.Story.Paragraphs.Single().Inlines.Add(new Footnote(doc)));
        Assert.Throws<InvalidOperationException>(note.Story.Remove);

        note.Story.Paragraphs.Single().Runs.Single().Text = "Changed";
        Assert.Equal(doc.Version, note.Story.Version);
    }

    [Fact]
    public void CommentsHaveRangesAndReplies()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Comment comment = new(doc, "Lars", new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)) { Initials = "LD" };
        comment.AddParagraph("Check this.");
        p.Inlines.Add(new CommentRangeStart(doc, comment));
        p.AddRun("disputed text");
        p.Inlines.Add(new CommentRangeEnd(doc, comment));
        p.Inlines.Add(comment);

        Comment reply = comment.AddReply("Ada", "Checked.");
        Comment second = comment.AddReply("Ada", "Twice.");

        Assert.Same(comment, reply.ParentComment);
        Assert.Equal([reply, second], comment.Replies());
        Assert.Same(second, reply.NextSibling);
        Assert.Same(comment, p.Inlines.OfType<CommentRangeStart>().Single().Comment);
        Assert.Equal("disputed text\u0005\u0005\u0005", p.GetText());
        Assert.NotEqual(comment.Id, reply.Id);
        Assert.Throws<InvalidOperationException>(() => new Comment(doc, "x").AddReply("y", "z"));
    }

    [Fact]
    public void CopiedCommentsGetNewIdsAndTheirRangesFollow()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Comment comment = new(doc, "Lars");
        p.Inlines.Add(new CommentRangeStart(doc, comment));
        p.AddRun("text");
        p.Inlines.Add(new CommentRangeEnd(doc, comment));
        p.Inlines.Add(comment);
        comment.AddReply("Ada", "ok");

        Paragraph copy = p.Clone();
        doc.Sections[0].Body.Blocks.Add(copy);

        Comment copied = copy.Inlines.OfType<Comment>().First();
        Assert.NotEqual(comment.Id, copied.Id);
        Assert.Equal(copied.Id, copy.Inlines.OfType<CommentRangeStart>().Single().CommentId);
        Assert.Equal(copied.Id, copy.Inlines.OfType<CommentRangeEnd>().Single().CommentId);
        Assert.Same(copied, copy.Inlines.OfType<Comment>().Last().ParentComment);

        Document whole = doc.Clone();
        Assert.Equal(doc.Descendants<Comment>().Select(c => c.Id), whole.Descendants<Comment>().Select(c => c.Id));
    }

    [Fact]
    public void RevisionsAreMarksOnTheNodes()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Run inserted = p.AddRun("new");
        Revision revision = new(RevisionKind.Insertion, "Lars", DateTimeOffset.UnixEpoch);
        List<string?> changes = [];
        doc.Changed += (_, e) => changes.Add(e.Property);

        inserted.Revision = revision;
        inserted.FormatRevision = new FormatRevision("Lars", null, new RunFormat { Bold = true });
        p.MarkRevision = new Revision(RevisionKind.Deletion, "Ada");
        p.FormatRevision = new FormatRevision("Ada", null, new ParagraphFormat());

        Assert.Equal(["Revision", "FormatRevision", "MarkRevision", "FormatRevision"], changes);
        Assert.True(p.MarkRevision.Removes);
        Assert.Throws<ArgumentException>(() => inserted.FormatRevision = new FormatRevision("x", null, new ParagraphFormat()));
        Assert.Same(revision, inserted.Clone().Revision);
    }

    [Fact]
    public void ContentControlsHoldBlocksOrInlines()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        ContentControl block = body.Blocks.Add(new ContentControl(doc));
        block.Properties.Kind = ContentControlKind.RichText;
        block.Properties.Tag = "summary";
        block.AddParagraph("Inside");
        InlineContentControl inline = new(doc);
        inline.Properties.Kind = ContentControlKind.CheckBox;
        inline.Properties.IsChecked = true;
        inline.AddRun("☒");
        body.AddParagraph("Done: ").Inlines.Add(inline);

        Assert.Equal("Inside\rDone: ☒\r", body.GetText());
        ContentControl copy = block.Clone();
        Assert.Equal("summary", copy.Properties.Tag);
        Assert.Equal("Inside\r", copy.GetText());
        Assert.True(((InlineContentControl)inline.Clone()).Properties.IsChecked);
    }

    [Fact]
    public void PicturesShapesAndTextBoxes()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];
        Picture picture = p.AddPicture(ImageData.FromBytes(png), 100, 50);
        picture.Floating = new FloatingPlacement { Wrap = WrapMode.Tight, HorizontalAnchor = HorizontalAnchor.Margin, HorizontalAlignment = FloatingAlignment.Center };
        Shape box = p.Inlines.Add(new Shape(doc, ShapeKind.Rectangle, 200, 80) { Fill = DocumentColor.FromHex("#EEEEEE") });
        box.AddTextBox().AddParagraph("In the box");

        Assert.Equal("image/png", picture.Image.ContentType);
        Assert.Equal(WrapMode.Tight, picture.Floating.Wrap);
        Assert.Equal("\u0001\u0001", p.GetText());
        Assert.Equal("In the box\r", box.TextBox!.GetText());
        Assert.Same(box.AddTextBox(), box.TextBox);
        Assert.Equal("In the box\r", box.Clone().TextBox!.GetText());
        Assert.Throws<ArgumentOutOfRangeException>(() => picture.Width = -1);
        Assert.Equal("image/x-emf", ImageData.GuessContentType([1, 0, 0, 0, .. new byte[36], 0x20, 0x45, 0x4D, 0x46]));
    }

    [Fact]
    public void IslandsKeepMarkupAndShowTheirText()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph("x = ");
        p.Inlines.Add(new Island(doc, IslandKind.Math, "docx", "<m:oMath/>") { Text = "a/b", Width = 20, Height = 12 });
        doc.Sections[0].Body.Blocks.Add(new BlockIsland(doc, "docx", "<w:altChunk r:id=\"x\"/>"));

        Assert.Equal("x = a/b", p.GetText());
        Island copy = (Island)p.Inlines[1].Clone();
        Assert.Equal("<m:oMath/>", copy.Markup);
        Assert.Equal(IslandKind.Math, copy.Kind);
    }

    [Fact]
    public void ExtensionDataIsKeptPerFormatAndCopied()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Assert.False(p.HasExtensions);

        p.Extensions.Set("docx", "raw");

        Assert.True(p.HasExtensions);
        Assert.Equal("raw", p.Clone().Extensions.Get<string>("docx"));
        Assert.Equal(["docx"], p.Extensions.Formats);
        Assert.True(p.Extensions.Remove("docx"));
        Assert.True(p.Extensions.IsEmpty);
    }

    [Fact]
    public void VisitorSeesTheNewNodes()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.AddField(FieldKind.Page);
        p.AddHyperlink("link", "https://example.com");
        p.AddFootnote("note");
        CountingVisitor visitor = new();

        doc.Accept(visitor);

        Assert.Equal(["Field(", "Field|", "Field)", "Link(", "Link)", "Note(", "Note)"], visitor.Log);
    }

    private sealed class CountingVisitor : DocumentVisitor
    {
        public List<string> Log { get; } = [];

        public override VisitorAction VisitFieldStart(FieldStart fieldStart) => Add("Field(");

        public override VisitorAction VisitFieldSeparator(FieldSeparator separator) => Add("Field|");

        public override VisitorAction VisitFieldEnd(FieldEnd fieldEnd) => Add("Field)");

        public override VisitorAction VisitHyperlinkStart(Hyperlink hyperlink) => Add("Link(");

        public override VisitorAction VisitHyperlinkEnd(Hyperlink hyperlink) => Add("Link)");

        public override VisitorAction VisitFootnoteStart(Footnote note) => Add("Note(");

        public override VisitorAction VisitFootnoteEnd(Footnote note) => Add("Note)");

        private VisitorAction Add(string entry)
        {
            Log.Add(entry);
            return VisitorAction.Continue;
        }
    }
}
