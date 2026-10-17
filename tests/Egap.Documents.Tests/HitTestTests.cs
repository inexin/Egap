using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Hit testing, carets and selection rectangles (Phase 6 Build Plan task 21b).</summary>
public class HitTestTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    [Fact]
    public void HitTestFindsTheCharacterUnderThePoint()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Hello world");
        DocumentLayout layout = document.Layout(Isolated);
        PageLayout page = layout.Pages[0];
        LineBox line = page.BodyLines.First();

        DocumentPosition? start = layout.HitTest(page, line.Bounds.X - 20, line.Bounds.Y + 2);
        DocumentPosition? end = layout.HitTest(page, line.Bounds.Right + 200, line.Bounds.Y + 2);

        Assert.Equal(new DocumentPosition(paragraph, 0), start);
        Assert.Equal(paragraph, end!.Value.Paragraph);
        Assert.Equal(11, end.Value.Index);

        // The caret for each index moves right, and hit testing at a caret gives its index back.
        double previous = -1;
        for (int i = 0; i <= 11; i++)
        {
            PageRect caret = layout.GetCaret(new DocumentPosition(paragraph, i))!.Value;
            Assert.Same(page, caret.Page);
            Assert.True(caret.Bounds.X > previous);
            Assert.Equal(line.Bounds.Height, caret.Bounds.Height, 3);
            previous = caret.Bounds.X;
            Assert.Equal(i, layout.HitTest(page, caret.Bounds.X + 0.2, caret.Bounds.Y + 1)!.Value.Index);
        }
    }

    [Fact]
    public void HitTestPicksTheNearestLine()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        body.AddParagraph("first");
        Paragraph second = body.AddParagraph("second");
        DocumentLayout layout = document.Layout(Isolated);
        PageLayout page = layout.Pages[0];
        LineBox line = page.BodyLines.Last();

        Assert.Equal(second, layout.HitTest(page, line.Bounds.X + 1, line.Bounds.Bottom - 1)!.Value.Paragraph);
        Assert.Equal(second, layout.HitTest(page, line.Bounds.X + 1, page.Height - 1)!.Value.Paragraph);
        Assert.Throws<ArgumentException>(() => layout.HitTest(new Document().Layout(Isolated).Pages[0], 0, 0));
    }

    [Fact]
    public void CaretAtALineBreakFollowsTheAffinity()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph(string.Join(' ', Enumerable.Repeat("wrapping words", 30)));
        DocumentLayout layout = document.Layout(Isolated);
        List<LineBox> lines = [.. layout.Pages[0].BodyLines];
        Assert.True(lines.Count > 1);
        int breakIndex = lines[1].Line.Start;

        PageRect down = layout.GetCaret(new DocumentPosition(paragraph, breakIndex))!.Value;
        PageRect up = layout.GetCaret(new DocumentPosition(paragraph, breakIndex, CaretAffinity.Upstream))!.Value;

        Assert.Equal(lines[1].Bounds.Y, down.Bounds.Y, 3);
        Assert.Equal(lines[0].Bounds.Y, up.Bounds.Y, 3);
        Assert.True(up.Bounds.X > down.Bounds.X);
    }

    [Fact]
    public void SelectionCoversEachLineAcrossParagraphsAndPages()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        List<Paragraph> paragraphs = [];
        for (int i = 0; i < 80; i++)
        {
            paragraphs.Add(body.AddParagraph("Line of text " + i));
        }

        DocumentLayout layout = document.Layout(Isolated);
        Assert.True(layout.Pages.Count >= 2);

        IReadOnlyList<PageRect> rects = layout.GetSelectionRects(new DocumentPosition(paragraphs[^1], 4), new DocumentPosition(paragraphs[0], 5));

        Assert.Equal(80, rects.Count);
        Assert.Same(layout.Pages[0], rects[0].Page);
        Assert.Same(layout.Pages[^1], rects[^1].Page);
        Assert.True(rects.All(r => r.Bounds.Width > 0));

        // Within one paragraph: just the selected characters.
        PageRect one = Assert.Single(layout.GetSelectionRects(new DocumentPosition(paragraphs[0], 0), new DocumentPosition(paragraphs[0], 4)));
        Assert.Equal(layout.GetCaret(new DocumentPosition(paragraphs[0], 0))!.Value.Bounds.X, one.Bounds.X, 3);
        Assert.Equal(layout.GetCaret(new DocumentPosition(paragraphs[0], 4))!.Value.Bounds.X, one.Bounds.Right, 3);
        Assert.Empty(layout.GetSelectionRects(new DocumentPosition(paragraphs[0], 2), new DocumentPosition(paragraphs[0], 2)));
    }

    [Fact]
    public void PositionsSkipTheListLabelAndLocateTheRun()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Paragraph item = body.AddParagraph();
        item.AddRun("bold ");
        Run second = item.AddRun("text");
        item.SetList(document.Lists.AddNumbered());
        DocumentLayout layout = document.Layout(Isolated);
        PageLayout page = layout.Pages[0];
        LineBox line = page.BodyLines.First();

        DocumentPosition first = layout.HitTest(page, line.Bounds.X - 50, line.Bounds.Y + 1)!.Value;

        Assert.Equal(0, first.Index);
        Assert.Equal((second, 2), layout.Locate(new DocumentPosition(item, 7)));
        Assert.Null(layout.Locate(new DocumentPosition(item, 9)));
    }
}
