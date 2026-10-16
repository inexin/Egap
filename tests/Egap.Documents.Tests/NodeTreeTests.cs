namespace Egap.Documents.Tests;

/// <summary>The node tree (Phase 6 Build Plan task 2, design Q3): parents, typed collections and their refusals, clone and import, the visitor.</summary>
public class NodeTreeTests
{
    [Fact]
    public void BuiltTreeLinksParentsAndSiblings()
    {
        Document doc = new();
        Section section = doc.AddSection();
        Paragraph p = section.Body.AddParagraph("Hello");
        Run world = p.AddRun(" world");
        Tab tab = p.AddTab();

        Assert.Same(doc, section.Parent);
        Assert.Same(section, section.Body.Parent);
        Assert.Same(section, section.Body.Section);
        Assert.Same(section.Body, p.Parent);
        Assert.Same(p, world.Paragraph);
        Assert.Same(p.Inlines[0], world.PreviousSibling);
        Assert.Same(tab, world.NextSibling);
        Assert.Null(tab.NextSibling);
        Assert.Null(section.Body.NextSibling);
        Assert.All(doc.Descendants(), node => Assert.Same(doc, node.Document));
        Assert.Equal([section.Body, section, doc], p.Ancestors());
        Assert.Same(section, world.GetAncestor<Section>());
        Assert.Null(doc.Parent);
        Assert.Same(doc, doc.Document);
    }

    [Fact]
    public void CollectionsRefuseNodesOfAnotherDocument()
    {
        Document a = new();
        Document b = new();
        Body body = a.AddSection().Body;

        ArgumentException error = Assert.Throws<ArgumentException>(() => body.Blocks.Add(new Paragraph(b)));
        Assert.Contains("Import", error.Message, StringComparison.Ordinal);
        Assert.Empty(body.Blocks);
    }

    [Fact]
    public void CollectionsRefuseNodesAlreadyInTheTree()
    {
        Document doc = new();
        Section section = doc.AddSection();
        Paragraph p = section.Body.AddParagraph("x");
        Cell cell = section.Body.AddTable(1, 1).Rows[0].Cells[0];

        Assert.Throws<ArgumentException>(() => cell.Blocks.Add(p));

        p.Remove();
        cell.Blocks.Add(p);
        Assert.Same(cell, p.Parent);
        Assert.Equal(2, cell.Blocks.Count);
    }

    [Fact]
    public void CollectionsRefuseANodeThatHoldsTheParent()
    {
        Document doc = new();
        Table table = new(doc);
        Cell cell = table.AddRow(1).Cells[0];

        Assert.Throws<ArgumentException>(() => cell.Blocks.Add(table));
    }

    [Fact]
    public void SectionHoldsOneHeaderOrFooterOfEachKind()
    {
        Document doc = new();
        Section section = doc.AddSection();
        HeaderFooter footer = section.HeadersFooters.GetOrAdd(HeaderFooterKind.FooterPrimary);

        Assert.Same(footer, section.HeadersFooters.GetOrAdd(HeaderFooterKind.FooterPrimary));
        Assert.Same(footer, section.HeadersFooters[HeaderFooterKind.FooterPrimary]);
        Assert.Null(section.HeadersFooters[HeaderFooterKind.HeaderFirst]);
        Assert.False(footer.IsHeader);
        Assert.Throws<ArgumentException>(() => section.HeadersFooters.Add(new HeaderFooter(doc, HeaderFooterKind.FooterPrimary)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeaderFooter(doc, (HeaderFooterKind)99));
    }

    [Fact]
    public void BodyCannotBeRemoved()
    {
        Section section = new Document().AddSection();

        Assert.Throws<InvalidOperationException>(section.Body.Remove);
    }

    [Fact]
    public void InsertAndRemoveKeepIndexesAndSiblings()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Run a = p.AddRun("a");
        Run c = p.AddRun("c");
        Run b = new(doc, "b");

        p.Inlines.Insert(1, b);
        Assert.Equal("abc", p.GetText());
        Assert.Equal(1, p.Inlines.IndexOf(b));
        Assert.Same(c, b.NextSibling);

        Assert.True(p.Inlines.Remove(a));
        Assert.False(p.Inlines.Remove(a));
        Assert.Null(a.Parent);
        Assert.Equal(-1, p.Inlines.IndexOf(a));
        Assert.Equal(0, p.Inlines.IndexOf(b));
        Assert.Null(b.PreviousSibling);

        p.Inlines.Clear();
        Assert.Null(c.Parent);
        Assert.Empty(p.Inlines);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Inlines.Insert(1, a));
    }

    [Fact]
    public void ChangingACollectionWhileEnumeratingThrows()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph("a");
        p.AddRun("b");

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (Inline inline in p.Inlines)
            {
                inline.Remove();
            }
        });
    }

    [Fact]
    public void DeepCloneCopiesEverythingDetached()
    {
        Document doc = new();
        Section section = doc.AddSection();
        Table table = section.Body.AddTable(2, 2);
        table.Rows[1].Cells[1].Blocks.OfType<Paragraph>().Single().AddRun("cell");
        table.Rows[0].Cells[0].AddTable(1, 1);

        Table copy = table.Clone();

        Assert.NotSame(table, copy);
        Assert.Null(copy.Parent);
        Assert.Same(doc, copy.Document);
        Assert.Equal(table.Descendants().Select(n => n.GetType()), copy.Descendants().Select(n => n.GetType()));
        Assert.Equal(table.GetText(), copy.GetText());
        Assert.Empty(table.Descendants().Intersect(copy.Descendants()));
        Assert.All(copy.Descendants(), node => Assert.NotNull(node.Parent));
    }

    [Fact]
    public void ShallowCloneLeavesChildrenOut()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph("text");

        Paragraph copy = p.Clone(deep: false);

        Assert.Empty(copy.Inlines);
        Assert.Equal("text", ((Run)p.Clone().Inlines[0]).Text);
    }

    [Fact]
    public void SectionCloneCopiesBodyAndHeadersFooters()
    {
        Document doc = new();
        Section section = doc.AddSection();
        section.Body.AddParagraph("body");
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderFirst).AddParagraph("first");

        Section copy = section.Clone();
        doc.Sections.Add(copy);

        Assert.Equal("body\r", copy.Body.GetText());
        Assert.Same(copy, copy.Body.Section);
        Assert.Equal("first\r", copy.HeadersFooters[HeaderFooterKind.HeaderFirst]!.GetText());
        Assert.Equal(2, doc.Sections.Count);
    }

    [Fact]
    public void ImportBringsANodeIntoAnotherDocument()
    {
        Document source = new();
        Paragraph p = source.AddSection().Body.AddParagraph("moved");
        p.AddBreak(BreakKind.Page);
        Document target = new();
        Body body = target.AddSection().Body;

        Paragraph imported = target.Import(p);
        body.Blocks.Add(imported);

        Assert.Same(target, imported.Document);
        Assert.All(imported.Descendants(), node => Assert.Same(target, node.Document));
        Assert.Equal("moved\f", imported.GetText());
        Assert.Same(source.Sections[0].Body, p.Parent);
        Assert.Throws<ArgumentException>(() => target.Import(source));
    }

    [Fact]
    public void DocumentCloneIsIndependent()
    {
        Document doc = new();
        Section section = doc.AddSection();
        section.Body.AddParagraph("one");
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("head");

        Document copy = doc.Clone();
        ((Run)copy.Sections[0].Body.Paragraphs.Single().Inlines[0]).Text = "changed";

        Assert.Equal("one\r", doc.Sections[0].Body.GetText());
        Assert.Equal("changed\r", copy.Sections[0].Body.GetText());
        Assert.All(copy.Descendants(), node => Assert.Same(copy, node.Document));
        Assert.Equal(doc.Descendants().Count(), copy.Descendants().Count());
        Assert.Empty(doc.Clone(deep: false).Sections);
        Assert.IsType<Document>(((Node)doc).Clone());
    }

    [Fact]
    public void GetTextUsesWordsControlCharacters()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        Paragraph p = body.AddParagraph("a");
        p.AddTab();
        p.AddRun("b");
        p.AddBreak();
        p.AddRun("c");
        p.AddBreak(BreakKind.Column);
        body.AddParagraph();
        body.AddTable(1, 2).Rows[0].Cells[1].Paragraphs.Single().AddRun("x");

        Assert.Equal("a\tb\vc\u000E", p.GetText());
        Assert.Equal("a\tb\vc\u000E\r\r\rx\r", body.GetText());
    }

    [Fact]
    public void VisitorSeesNodesInDocumentOrder()
    {
        Document doc = new();
        Section section = doc.AddSection();
        section.Body.AddParagraph("a");
        section.Body.AddTable(1, 1);
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("h");
        RecordingVisitor visitor = new();

        Assert.True(doc.Accept(visitor));

        Assert.Equal(
            "Document( Section( Story( Paragraph( Run:a )Paragraph Table( Row( Cell( Paragraph( )Paragraph )Cell )Row )Table )Story " +
            "Story( Paragraph( Run:h )Paragraph )Story )Section )Document",
            string.Join(' ', visitor.Log));
    }

    [Fact]
    public void VisitorCanSkipChildrenAndStop()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        body.AddTable(1, 1).Rows[0].Cells[0].Paragraphs.Single().AddRun("in table");
        body.AddParagraph("after");
        body.AddParagraph("never");
        RecordingVisitor visitor = new() { SkipTables = true, StopAt = "after" };

        Assert.False(doc.Accept(visitor));

        Assert.DoesNotContain("Run:in table", visitor.Log);
        Assert.Contains(")Table", visitor.Log);
        Assert.Equal("Run:after", visitor.Log[^1]);
    }

    [Fact]
    public void DeepNestingNeedsNoRecursion()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        BlockContainer container = body;
        for (int i = 0; i < 20_000; i++)
        {
            container = container.AddTable(1, 1).Rows[0].Cells[0];
        }

        ((Cell)container).Paragraphs.Single().AddRun("deep");

        Assert.Equal(80_001, body.Descendants().Count());   // table, row, cell and paragraph per level, then the run
        Assert.EndsWith("deep\r", body.GetText(), StringComparison.Ordinal);
        Assert.True(doc.Accept(new RecordingVisitor()));
        Assert.Equal(body.Descendants().Count(), doc.Clone().Sections[0].Body.Descendants().Count());
    }

    [Fact]
    public void ConstructorsCheckTheirArguments()
    {
        Document doc = new();

        Assert.Throws<ArgumentNullException>(() => new Paragraph(null!));
        Assert.Throws<ArgumentNullException>(() => new Run(doc, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Break(doc, (BreakKind)7));
        Assert.Throws<ArgumentNullException>(() => new Run(doc).Text = null!);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddSection().Body.AddTable(-1, 1));
    }

    private sealed class RecordingVisitor : DocumentVisitor
    {
        public List<string> Log { get; } = [];

        public bool SkipTables { get; init; }

        public string? StopAt { get; init; }

        public override VisitorAction VisitDocumentStart(Document document) => Log1("Document(");

        public override VisitorAction VisitDocumentEnd(Document document) => Log1(")Document");

        public override VisitorAction VisitSectionStart(Section section) => Log1("Section(");

        public override VisitorAction VisitSectionEnd(Section section) => Log1(")Section");

        public override VisitorAction VisitStoryStart(Story story) => Log1("Story(");

        public override VisitorAction VisitStoryEnd(Story story) => Log1(")Story");

        public override VisitorAction VisitParagraphStart(Paragraph paragraph) => Log1("Paragraph(");

        public override VisitorAction VisitParagraphEnd(Paragraph paragraph) => Log1(")Paragraph");

        public override VisitorAction VisitTableStart(Table table)
        {
            Log1("Table(");
            return SkipTables ? VisitorAction.SkipChildren : VisitorAction.Continue;
        }

        public override VisitorAction VisitTableEnd(Table table) => Log1(")Table");

        public override VisitorAction VisitRowStart(Row row) => Log1("Row(");

        public override VisitorAction VisitRowEnd(Row row) => Log1(")Row");

        public override VisitorAction VisitCellStart(Cell cell) => Log1("Cell(");

        public override VisitorAction VisitCellEnd(Cell cell) => Log1(")Cell");

        public override VisitorAction VisitRun(Run run)
        {
            Log1("Run:" + run.Text);
            return run.Text == StopAt ? VisitorAction.Stop : VisitorAction.Continue;
        }

        private VisitorAction Log1(string entry)
        {
            if (Log.Count < 1000)
            {
                Log.Add(entry);
            }

            return VisitorAction.Continue;
        }
    }
}
