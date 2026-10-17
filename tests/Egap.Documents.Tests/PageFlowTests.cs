using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>The page flow (Phase 6 Build Plan task 17).</summary>
public class PageFlowTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    private static Document Lines(int paragraphs, string text = "A line of body text.")
    {
        Document document = new();
        Body body = document.AddSection().Body;
        for (int i = 0; i < paragraphs; i++)
        {
            body.AddParagraph(text + " " + i);
        }

        return document;
    }

    [Fact]
    public void ParagraphsFlowOntoNewPages()
    {
        Document document = Lines(120);

        DocumentLayout layout = document.Layout(Isolated);

        Assert.True(layout.Pages.Count >= 2);
        PageLayout first = layout.Pages[0];
        Assert.Equal(612, first.Width);
        Assert.Equal(792, first.Height);
        LineBox top = first.BodyLines.First();
        Assert.True(top.Bounds.Y >= 72 - 0.01);
        Assert.All(first.BodyLines, line => Assert.True(line.Bounds.Bottom <= 720 + 0.01));
        Assert.Equal("A line of body text. 0", top.Text);
        Assert.Same(document.Sections[0].Body.Paragraphs.Last(), layout.Pages[^1].BodyLines.Last().Node);
        Assert.Same(layout.Pages[0], layout.PageOf(document.Sections[0].Body.Paragraphs.First()));
    }

    [Fact]
    public void GutterAtTopAndMirroredMargins()
    {
        Document document = Lines(120);
        PageSetup setup = document.Sections[0].PageSetup;
        setup.MarginLeft = 50;
        setup.MarginRight = 100;
        setup.Gutter = 30;
        document.Settings.MirrorMargins = true;

        DocumentLayout layout = document.Layout(Isolated);

        // The gutter is on the inside: left on odd pages, right on even ones.
        Assert.Equal(80, layout.Pages[0].BodyLines.First().Bounds.X, 0.01);
        Assert.Equal(100, layout.Pages[1].BodyLines.First().Bounds.X, 0.01);

        document.Settings.GutterAtTop = true;
        layout = document.Layout(Isolated);

        Assert.Equal(50, layout.Pages[0].BodyLines.First().Bounds.X, 0.01);
        Assert.True(layout.Pages[0].BodyLines.First().Bounds.Y >= 102 - 0.01);
    }

    [Fact]
    public void PageBreaksSectionsAndOrientation()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        body.AddParagraph("one");
        Paragraph second = body.AddParagraph("two");
        second.Format.PageBreakBefore = true;
        Paragraph third = body.AddParagraph("three");
        third.AddBreak(BreakKind.Page);
        third.AddRun("four");
        Section landscape = document.AddSection();
        landscape.PageSetup.PageWidth = 792;
        landscape.PageSetup.PageHeight = 612;
        landscape.Body.AddParagraph("five");

        DocumentLayout layout = document.Layout(Isolated);

        Assert.Equal(["one", "two", "four", "five"], layout.Pages.Select(p => p.BodyLines.First().Text).ToArray());
        Assert.Equal(792, layout.Pages[^1].Width);
    }

    [Fact]
    public void FooterPageNumbersAndNumPages()
    {
        Document document = Lines(150);
        Section section = document.Sections[0];
        Paragraph footer = section.HeadersFooters.GetOrAdd(HeaderFooterKind.FooterPrimary).AddParagraph("Page ");
        footer.AddField("PAGE", "1");
        footer.AddRun(" of ");
        footer.AddField("NUMPAGES", "1");
        section.PageSetup.PageNumberStart = 5;

        DocumentLayout layout = document.Layout(Isolated);

        int count = layout.Pages.Count;
        Assert.True(count >= 3);
        AreaBox area = layout.Pages[1].Boxes.OfType<AreaBox>().Single(a => a.Area == PageArea.Footer);
        string text = string.Concat(area.Descendants().OfType<LineBox>().Select(l => l.Text));
        Assert.Equal($"Page 6 of {count}", text);
        Assert.Equal("6", layout.Pages[1].Number);
        Assert.True(area.Bounds.Bottom <= 792 - 36 + 0.01);
    }

    [Fact]
    public void TallHeaderPushesTheBodyDown()
    {
        Document document = Lines(3);
        HeaderFooter header = document.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary);
        for (int i = 0; i < 8; i++)
        {
            header.AddParagraph("header line " + i);
        }

        DocumentLayout layout = document.Layout(Isolated);

        AreaBox headerArea = layout.Pages[0].Boxes.OfType<AreaBox>().Single(a => a.Area == PageArea.Header);
        Assert.True(layout.Pages[0].BodyLines.First().Bounds.Y >= headerArea.Bounds.Bottom - 0.01);
    }

    [Fact]
    public void ColumnsFillLeftToRight()
    {
        Document document = Lines(90);
        document.Sections[0].PageSetup.ColumnCount = 2;

        DocumentLayout layout = document.Layout(Isolated);

        AreaBox[] columns = [.. layout.Pages[0].Boxes.OfType<AreaBox>().Where(a => a.Area == PageArea.Body)];
        Assert.Equal(2, columns.Length);
        Assert.True(columns[1].Bounds.X > columns[0].Bounds.Right);
        Assert.NotEmpty(columns[1].Descendants().OfType<LineBox>());
    }

    [Fact]
    public void KeepWithNextAndWidowControl()
    {
        Document document = Lines(44);
        Body body = document.Sections[0].Body;
        Paragraph heading = body.AddParagraph("Heading kept with the next paragraph");
        heading.Format.KeepWithNext = true;
        body.AddParagraph(string.Join(" ", Enumerable.Repeat("words that wrap over several lines", 12)));

        DocumentLayout layout = document.Layout(Isolated);

        PageLayout headingPage = layout.PageOf(heading)!;
        Paragraph following = body.Paragraphs.Last();
        Assert.Same(headingPage, layout.PageOf(following));

        // No page holds a single line of a paragraph that has more lines elsewhere.
        foreach ((PageLayout page, ParagraphBox part) in layout.FindBoxes(following).Where(b => b.Box is ParagraphBox).Select(b => (b.Page, (ParagraphBox)b.Box)))
        {
            if (part.IsContinuation || part.Continues)
            {
                Assert.True(part.Lines.Count >= 2, $"page {page.Index} has {part.Lines.Count} line");
            }
        }
    }

    [Fact]
    public void TablesFlowRowByRow()
    {
        Document document = new();
        Table table = document.AddSection().Body.AddTable(80, 3);

        DocumentLayout layout = document.Layout(Isolated);

        Assert.True(layout.Pages.Count >= 2);
        Assert.Equal(80, layout.FindBoxes(table).Sum(b => b.Box.Children.Count));
    }
    [Fact]
    public void LinesUseWordsUnroundedFontMetrics()
    {
        // The default theme font's twin Carlito, like Calibri: (1536 + 452 + 512) / 2048 of 11 points a line, as Word sets
        // Calibri, where Egami's metrics rounded to whole points would give 13.
        Document document = new();
        document.Styles.DefaultRunFormat.Size = 11;
        Paragraph paragraph = document.AddSection().Body.AddParagraph(string.Join(' ', Enumerable.Repeat("wrapping words", 30)));
        paragraph.Format.LineSpacingRule = LineSpacingRule.Multiple;
        paragraph.Format.LineSpacing = 1;

        LineBox[] lines = [.. document.Layout(Isolated).Pages[0].BodyLines];

        Assert.Equal(2500.0 / 2048 * 11, lines[1].Bounds.Y - lines[0].Bounds.Y, 3);
    }

    [Fact]
    public void SectionBreakParagraphsTakeNoRoomOfTheirOwn()
    {
        Document document = new();
        Section first = document.AddSection();
        first.Body.AddParagraph("before");
        first.Body.AddParagraph();
        Section second = document.AddSection();
        second.PageSetup.SectionStart = SectionStart.Continuous;
        second.Body.AddParagraph("after");

        LineBox[] lines = [.. document.Layout(Isolated).Pages[0].BodyLines];

        // The empty paragraph that carries the continuous break has no line.
        Assert.Equal(["before", "after"], lines.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void ALineHoldingOnlyAPageBreakStaysAtTheFoot()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        int count = 0;
        while (document.Layout(Isolated).Pages.Count == 1)
        {
            body.AddParagraph("filler " + count++);
        }

        body.Paragraphs.Last().Remove();
        Paragraph broken = body.AddParagraph();
        broken.AddBreak(BreakKind.Page);
        broken.AddRun("next page");

        DocumentLayout layout = document.Layout(Isolated);

        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal("next page", layout.Pages[1].BodyLines.First().Text);
    }
}
