using System.Xml.Linq;

using Egap.Presentations.Pptx;

namespace Egap.Presentations.Tests;

/// <summary>Tables, notes, comments and sections: read, made and edited.</summary>
public sealed class ContentTests
{
    private static readonly string TableFrame =
        "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Table 2\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>"
        + "<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"3000\" cy=\"300\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">"
        + "<a:tbl><a:tblPr firstRow=\"1\"><a:tableStyleId>{073A0DAA-6AF3-43AB-8588-CEC1D06C72B9}</a:tableStyleId></a:tblPr><a:tblGrid><a:gridCol w=\"1000\"/><a:gridCol w=\"1000\"/><a:gridCol w=\"1000\"/></a:tblGrid>"
        + "<a:tr h=\"100\">" + Cell("A1", "gridSpan=\"2\"") + Cell("hidden", "hMerge=\"1\"") + Cell("C1", "rowSpan=\"2\"") + "</a:tr>"
        + "<a:tr h=\"100\">" + Cell("A2") + Cell("B2") + Cell("", "vMerge=\"1\"") + "</a:tr>"
        + "<a:tr h=\"120\">" + Cell("A3") + Cell("B3") + Cell("C3") + "</a:tr>"
        + "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    [Fact]
    public void ATablesGridCellsAndMergesAreRead()
    {
        SlideTable table = LoadTable();

        Assert.Equal("{073A0DAA-6AF3-43AB-8588-CEC1D06C72B9}", table.StyleId);
        Assert.Equal([1000L, 1000L, 1000L], table.Columns.Select(c => c.Width));
        Assert.Equal([100L, 100L, 120L], table.Rows.Select(r => r.Height));
        Assert.Equal("A1", table.Cell(0, 0).Text);
        Assert.Equal(2, table.Cell(0, 0).ColumnSpan);
        Assert.True(table.Cell(0, 1).IsCoveredHorizontally);
        Assert.Same(table.Cell(0, 0), table.Cell(0, 1).Origin);
        Assert.Equal(2, table.Cell(0, 2).RowSpan);
        Assert.True(table.Cell(1, 2).IsCoveredVertically);
        Assert.Same(table.Cell(0, 2), table.Cell(1, 2).Origin);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Cell(3, 0));
    }

    [Fact]
    public void CellTextRowHeightsColumnWidthsAndStyleAreEdited()
    {
        SlideTable table = LoadTable();

        table.Cell(2, 1).Text = "Edited\rTwo lines";
        table.Rows[0].Height = 500;
        table.Columns[2].Width = 1500;
        table.StyleId = SlideTable.DefaultStyleId;

        SlideTable read = Reread(table);
        Assert.Equal("Edited\rTwo lines", read.Cell(2, 1).Text);
        Assert.Equal(500, read.Rows[0].Height);
        Assert.Equal(1500, read.Columns[2].Width);
        Assert.Equal(SlideTable.DefaultStyleId, read.StyleId);
        table.StyleId = null;
        Assert.Null(Reread(table).StyleId);
    }

    [Fact]
    public void RowsAreAddedAndRemovedKeepingMergesWhole()
    {
        SlideTable table = LoadTable();

        // Inside C1's vertical merge: the merge grows.
        SlideTableRow added = table.InsertRow(1);
        Assert.Equal(100, added.Height);
        Assert.Equal(3, table.Cell(0, 2).RowSpan);
        Assert.True(table.Cell(1, 2).IsCoveredVertically);

        // Removing the merge's first row passes it to the next, text and all.
        table.RemoveRow(0);
        Assert.Equal("C1", table.Cell(0, 2).Text);
        Assert.Equal(2, table.Cell(0, 2).RowSpan);
        Assert.False(table.Cell(0, 2).IsCoveredVertically);
        Assert.Equal("", table.Cell(0, 0).Text);

        table.AddRow().Cells[0].Text = "Last";
        SlideTable read = Reread(table);
        Assert.Equal(4, read.Rows.Count);
        Assert.Equal("Last", read.Cell(3, 0).Text);
        Assert.Equal(120, read.Rows[3].Height);
    }

    [Fact]
    public void ColumnsAreAddedAndRemovedWithTheFrame()
    {
        SlideTable table = LoadTable();

        table.InsertColumn(1, 400);
        Assert.Equal(3, table.Cell(0, 0).ColumnSpan);
        Assert.True(table.Cell(0, 1).IsCoveredHorizontally);
        Assert.Equal(3400, table.Frame.Transform!.Value.Width);

        table.RemoveColumn(0);
        Assert.Equal("A1", table.Cell(0, 0).Text);
        Assert.Equal(2, table.Cell(0, 0).ColumnSpan);
        Assert.Equal(["", "B2", ""], table.Rows[1].Cells.Select(c => c.Text));
        Assert.Equal(2400, table.Frame.Transform!.Value.Width);

        SlideTable read = Reread(table);
        Assert.Equal(3, read.Columns.Count);
        Assert.All(read.Rows, r => Assert.Equal(3, r.Cells.Count));
    }

    [Fact]
    public void CellsAreMergedAndSplit()
    {
        SlideTable table = LoadTable();
        table.Split(table.Cell(0, 1));
        table.Split(table.Cell(1, 2));
        Assert.All(table.Rows.SelectMany(r => r.Cells), c => Assert.Same(c, c.Origin));
        Assert.Equal("hidden", table.Cell(0, 1).Text);

        SlideTableCell merged = table.Merge(1, 0, 2, 2);

        Assert.Equal((2, 2), (merged.RowSpan, merged.ColumnSpan));
        SlideTable read = Reread(table);
        Assert.Same(read.Cell(1, 0), read.Cell(2, 1).Origin);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Merge(2, 2, 2, 1));
    }

    [Fact]
    public void ATableIsAddedToASlide()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].GetLayout(SlideLayoutType.TitleOnly)!);

        GraphicFrame frame = slide.Shapes.AddTable(2, 4, new ShapeTransform(0, 0, 4000, 1000));
        frame.Table!.Cell(1, 3).Text = "Bottom right";

        GraphicFrame read = Assert.IsType<GraphicFrame>(BuildingTests.Reload(presentation).Slides[0].Shapes[1]);
        Assert.Equal(GraphicFrameKind.Table, read.Kind);
        SlideTable table = read.Table!;
        Assert.Equal([1000L, 1000L, 1000L, 1000L], table.Columns.Select(c => c.Width));
        Assert.Equal([500L, 500L], table.Rows.Select(r => r.Height));
        Assert.Equal("Bottom right", table.Cell(1, 3).Text);
        Assert.Equal(SlideTable.DefaultStyleId, table.StyleId);
        Assert.Throws<ArgumentOutOfRangeException>(() => slide.Shapes.AddTable(0, 1, new ShapeTransform(0, 0, 1, 1)));
    }

    [Fact]
    public void NotesAreMadeWithANotesMasterWhenThereIsNone()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].Layouts[0]);
        Assert.Null(slide.Notes);
        Assert.Equal("", slide.NotesText);

        slide.NotesText = "";
        Assert.Null(slide.Notes);
        slide.NotesText = "Say hello\rThen goodbye";

        Assert.NotNull(presentation.NotesMaster);
        Assert.Equal("/ppt/notesMasters/notesMaster1.xml", presentation.NotesMasterPartName);
        Assert.Same(slide, slide.Notes!.Slide);
        Assert.Same(presentation.NotesMaster, slide.Notes.Master);

        Presentation reloaded = BuildingTests.Reload(presentation);
        Slide read = reloaded.Slides[0];
        Assert.Equal("Say hello\rThen goodbye", read.NotesText);
        Assert.Equal("/ppt/notesSlides/notesSlide1.xml", read.NotesPartName);
        Assert.Equal(PlaceholderType.Body, read.Notes!.NotesBody!.Shape.InheritedPlaceholder!.Placeholder!.Type);

        read.NotesText = "Changed";
        Assert.Equal("Changed", BuildingTests.Reload(reloaded).Slides[0].NotesText);
    }

    [Fact]
    public void ExistingNotesAreReadAndEdited()
    {
        PptxBuilder builder = new() { Notes = true };
        builder.Slides.Add("");
        Presentation presentation = PresentationTests.Load(builder);
        Slide slide = presentation.Slides[0];
        string before = slide.NotesText;

        slide.NotesText = before + " and more";

        Assert.Equal(before + " and more", BuildingTests.Reload(presentation).Slides[0].NotesText);
        Assert.Equal(1, BuildingTests.PartNames(presentation).Count(p => p.StartsWith("ppt/notesMasters/", StringComparison.Ordinal) && p.EndsWith(".xml", StringComparison.Ordinal)));
    }

    [Fact]
    public void ModernCommentsAreAddedRepliedToAndRemoved()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].Layouts[6]);
        Assert.True(presentation.CommentAuthors.WritesModern);
        CommentAuthor ada = presentation.CommentAuthors.Add("Ada", "A");
        CommentAuthor bob = presentation.CommentAuthors.Add("Bob");
        Assert.Same(ada, presentation.CommentAuthors.Add("Ada"));
        Assert.True(ada.IsModern);

        SlideComment first = slide.Comments.Add(ada, "First thought");
        first.Reply(bob, "A reply");
        slide.Comments.Add(bob, "Second thread");

        Presentation reloaded = BuildingTests.Reload(presentation);
        Slide read = reloaded.Slides[0];
        SlideCommentCollection comments = read.Comments;
        Assert.Equal(2, comments.Count);
        Assert.Equal("First thought", comments[0].Text);
        Assert.Equal("Ada", comments[0].Author!.Name);
        Assert.Equal("A", comments[0].Author!.Initials);
        Assert.NotNull(comments[0].Created);
        SlideComment reply = Assert.Single(comments[0].Replies);
        Assert.Equal(("A reply", "Bob"), (reply.Text, reply.Author!.Name));
        Assert.Same(comments[0], reply.Parent);
        Assert.Equal(2, reloaded.CommentAuthors.Count);

        comments[0].Text = "Edited";
        Assert.True(comments.Remove(reply));
        Assert.Equal("Edited", BuildingTests.Reload(reloaded).Slides[0].Comments[0].Text);
        Assert.Empty(BuildingTests.Reload(reloaded).Slides[0].Comments[0].Replies);

        comments.Remove(comments[1]);
        comments.Remove(comments[0]);
        Assert.Empty(read.Comments);
        Assert.DoesNotContain(BuildingTests.PartNames(reloaded), p => p.StartsWith("ppt/comments/", StringComparison.Ordinal));
        Assert.Null(read.Xml.Root.Element(PptxNames.P + "extLst"));
    }

    [Fact]
    public void OlderCommentsAreReadAndWrittenInTheirOwnKind()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].Layouts[6]);
        PackagePart authors = presentation.Package.AddXmlPart("/ppt/commentAuthors.xml", DefaultTemplate.Ct + "commentAuthors+xml",
            DefaultTemplate.Parse($"<p:cmAuthorLst {DefaultTemplate.Namespaces}><p:cmAuthor id=\"0\" name=\"Old Hand\" initials=\"OH\" lastIdx=\"0\" clrIdx=\"0\"/></p:cmAuthorLst>"));
        presentation.Package.AddRelationship(presentation.Main.Part.Name, PptxNames.Office + "commentAuthors", authors.Name);

        Assert.False(presentation.CommentAuthors.WritesModern);
        CommentAuthor old = presentation.CommentAuthors[0];
        CommentAuthor added = presentation.CommentAuthors.Add("New Hand", "NH");
        Assert.False(added.IsModern);
        Assert.Equal("1", added.Id);

        SlideComment comment = slide.Comments.Add(old, "Older kind");
        comment.Reply(added, "Threaded reply");

        Slide read = BuildingTests.Reload(presentation).Slides[0];
        SlideComment thread = Assert.Single(read.Comments);
        Assert.False(thread.IsModern);
        Assert.Equal(("Older kind", "Old Hand"), (thread.Text, thread.Author!.Name));
        Assert.Equal(("Threaded reply", "New Hand"), (thread.Replies[0].Text, thread.Replies[0].Author!.Name));
        Assert.Throws<ArgumentException>(() => thread.Reply(Presentation.Create().CommentAuthors.Add("Stranger"), "No"));
    }

    [Fact]
    public void SectionsSplitTheSlidesAndFollowTheirChanges()
    {
        Presentation presentation = Presentation.Create();
        SlideLayout layout = presentation.SlideMasters[0].Layouts[6];
        Slide[] slides = [.. Enumerable.Range(0, 4).Select(_ => presentation.Slides.Add(layout))];

        SlideSection second = presentation.Sections.Add("Second", slides[2]);
        SlideSection first = presentation.Sections[0];
        Assert.Equal("Default Section", first.Name);
        Assert.Equal([slides[0], slides[1]], first.Slides);
        Assert.Equal([slides[2], slides[3]], second.Slides);
        Assert.Matches("^\\{[0-9A-F-]{36}\\}$", second.Id);

        first.Name = "First";
        SlideSection empty = presentation.Sections.Add("Empty");
        Assert.Empty(empty.Slides);

        presentation.Sections.MoveSlide(slides[0], second);
        Assert.Equal([slides[1], slides[2], slides[3], slides[0]], presentation.Slides);
        Assert.Same(second, presentation.Sections.SectionOf(slides[0]));

        Slide added = presentation.Slides.Insert(1, layout);
        Assert.Same(first, presentation.Sections.SectionOf(added));
        presentation.Slides.Remove(slides[2]);
        presentation.Slides.Move(slides[3], 0);
        Assert.Same(first, presentation.Sections.SectionOf(slides[3]));

        Presentation reloaded = BuildingTests.Reload(presentation);
        Assert.Equal(["First", "Second", "Empty"], reloaded.Sections.Select(s => s.Name));
        Assert.Equal(reloaded.Slides, reloaded.Sections.SelectMany(s => s.Slides));
        Assert.Equal([3, 1, 0], reloaded.Sections.Select(s => s.Slides.Count));

        Assert.True(reloaded.Sections.Remove(reloaded.Sections[0]));
        Assert.Equal(4, reloaded.Sections[0].Slides.Count);
        reloaded.Sections.Remove(reloaded.Sections[1]);
        reloaded.Sections.Remove(reloaded.Sections[0]);
        Assert.Empty(reloaded.Sections);
        Assert.Null(reloaded.Main.Root.Element(PptxNames.P + "extLst"));
    }

    [Fact]
    public void TheFirstSectionAddedTakesEverySlide()
    {
        Presentation presentation = Presentation.Create();
        presentation.Slides.Add(presentation.SlideMasters[0].Layouts[6]);
        presentation.Slides.Add(presentation.SlideMasters[0].Layouts[6]);

        SlideSection only = presentation.Sections.Add("All");

        Assert.Equal(presentation.Slides, only.Slides);
        Assert.Contains(SlideSectionCollection.ExtensionUri, BuildingTests.Reload(presentation).Main.Root.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);
    }

    private static string Cell(string text, string attributes = "") =>
        $"<a:tc {attributes}><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{text}</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc>";

    private static SlideTable LoadTable()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(TableFrame);
        return ((GraphicFrame)PresentationTests.Load(builder).Slides[0].Shapes[0]).Table!;
    }

    private static SlideTable Reread(SlideTable table) => ((GraphicFrame)BuildingTests.Reload(table.Frame.Owner.Presentation).Slides[0].Shapes[0]).Table!;
}
