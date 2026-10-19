using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Scale to fit, clamped lines, repeating table footers and balanced columns.</summary>
public sealed class MorePagingTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

    [Fact]
    public void AGroupScalesDownToFitTheRoomLeft()
    {
        Document document = Page(out Section section);
        for (int i = 0; i < 10; i++)
        {
            section.Body.AddParagraph("Filler " + i);
        }

        ContentControl group = new(document) { Paging = new GroupPaging { ScaleToFit = true } };
        for (int i = 0; i < 12; i++)
        {
            group.AddParagraph("Scaled line " + i);
        }

        section.Body.Blocks.Add(group);

        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = FontSettings.Portable, Strict = true });

        Assert.Single(layout.Pages);
        ScaledBox scaled = Assert.Single(layout.Pages[0].Boxes.SelectMany(b => b.Descendants()).OfType<ScaledBox>());
        Assert.InRange(scaled.Scale, 0.2, 0.99);
        Assert.True(scaled.Bounds.Bottom <= 400 - 36 + 0.5);
        Assert.Same(group, scaled.Node);
    }

    [Fact]
    public void MaxLinesClampsAParagraphWithAnEllipsis()
    {
        Document document = Page(out Section section);
        Paragraph paragraph = section.Body.AddParagraph(string.Join(' ', Enumerable.Repeat("words that wrap", 40)));
        int lines = document.Layout(Isolated).Pages[0].BodyLines.Count();

        paragraph.Format.MaxLines = 2;
        LineBox[] clamped = [.. document.Layout(Isolated).Pages[0].BodyLines];

        Assert.True(lines > 2);
        Assert.Equal(2, clamped.Length);
        Assert.True(clamped[^1].Line.Ellipsized);

        Document html = HtmlFormat.Parse("<p style='-webkit-line-clamp: 3'>x</p>");
        Assert.Equal(3, html.Descendants().OfType<Paragraph>().Single().Format.MaxLines);
        Assert.Contains("line-clamp", HtmlFormat.ToHtml(html, out _), StringComparison.Ordinal);
    }

    [Fact]
    public void FooterRowsRepeatAtTheBottomOfEachPart()
    {
        Document document = Page(out Section section);
        Table table = section.Body.AddTable(42, 1);
        for (int r = 0; r < 41; r++)
        {
            table.Rows[r].Cells[0].Paragraphs.First().AddRun("Row " + r);
        }

        Row footer = table.Rows[^1];
        footer.Cells[0].Paragraphs.First().AddRun("Carried over");
        footer.Format.IsFooter = true;

        DocumentLayout layout = document.Layout(Isolated);

        Assert.True(layout.Pages.Count > 1);
        IReadOnlyList<(PageLayout Page, LayoutBox Box)> boxes = layout.FindBoxes(footer);
        Assert.Equal(layout.Pages.Count, boxes.Count);
        foreach ((PageLayout page, LayoutBox box) in boxes)
        {
            TableBox part = page.Boxes.SelectMany(b => b.Descendants()).OfType<TableBox>().Single();
            Assert.Equal(part.Bounds.Bottom, box.Bounds.Bottom, 1);
        }
    }

    [Fact]
    public void BalancedColumnsEndAtTheSameHeight()
    {
        Document document = Page(out Section section);
        section.PageSetup.ColumnCount = 2;
        for (int i = 0; i < 8; i++)
        {
            section.Body.AddParagraph("Line " + i);
        }

        static double[] Heights(DocumentLayout layout) => [.. layout.Pages[^1].Boxes.OfType<AreaBox>().Where(a => a.Area == PageArea.Body)
            .SelectMany(a => a.Children).GroupBy(b => Math.Round(b.Bounds.X)).Select(g => g.Max(b => b.Bounds.Bottom))];

        double[] plain = Heights(document.Layout(Isolated));
        section.PageSetup.BalanceColumns = true;
        double[] balanced = Heights(document.Layout(Isolated));

        Assert.Single(plain);
        Assert.Equal(2, balanced.Length);
        Assert.InRange(Math.Abs(balanced[0] - balanced[1]), 0, 20);
    }

    private static Document Page(out Section section)
    {
        Document document = new();
        section = document.AddSection();
        section.PageSetup.PageWidth = 400;
        section.PageSetup.PageHeight = 400;
        section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 36;
        return document;
    }
}
