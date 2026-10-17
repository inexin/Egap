using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>The layout tree drawn and reused (Phase 6 Build Plan task 21).</summary>
public class LayoutDrawingTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    [Fact]
    public void RenderedPagesShowTheirText()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Rendered text");
        paragraph.Runs.Single().Format.Size = 24;

        Egami.Raster<Rgba8> page = document.Layout(Isolated).Pages[0].Render(72);

        Assert.Equal(612, page.Width);
        Assert.Equal(792, page.Height);
        int dark = 0;
        for (int y = 70; y < 110; y++)
        {
            for (int x = 72; x < 300; x++)
            {
                if (page.GetPixel(x, y).R < 128)
                {
                    dark++;
                }
            }
        }

        Assert.True(dark > 100, $"{dark} dark pixels where the text is");
        Assert.Equal(255, page.GetPixel(500, 600).R);
    }

    [Fact]
    public void TablesShapesAndSymbolsDraw()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Table table = body.AddTable(2, 2);
        table.Format.BorderTop = new Border(BorderStyle.Single, 1);
        table.Rows[0].Cells[0].Format.Shading = new Shading(ShadingPattern.Clear, DocumentColor.FromHex("FF0000"));
        Paragraph paragraph = body.AddParagraph();
        Shape shape = paragraph.Inlines.Add(new Shape(document, ShapeKind.Ellipse, 50, 50));
        shape.Fill = DocumentColor.FromHex("0000FF");
        Run symbol = paragraph.AddRun("");
        symbol.Format.FontName = "Symbol";

        DocumentLayout layout = document.Layout(Isolated);
        Egami.Raster<Rgba8> page = layout.Pages[0].Render(72);

        CellBox red = layout.FindBoxes(table.Rows[0].Cells[0]).Select(b => b.Box).OfType<CellBox>().Single();
        Rgba8 inside = page.GetPixel((int)(red.Bounds.X + (red.Bounds.Width / 2)), (int)(red.Bounds.Y + (red.Bounds.Height / 2)));
        Assert.Equal((255, 0, 0), (inside.R, inside.G, inside.B));
        LayoutRect ellipse = layout.FindBoxes(shape).Single().Box.Bounds;
        Rgba8 center = page.GetPixel((int)(ellipse.X + 25), (int)(ellipse.Y + 25));
        Assert.Equal((0, 0, 255), (center.R, center.G, center.B));
        Assert.Contains("•", layout.FindBoxes(paragraph).Select(b => b.Box).OfType<LineBox>().Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondLayoutReusesUnchangedParagraphs()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Paragraph same = body.AddParagraph("unchanged");
        Paragraph edited = body.AddParagraph("before");

        DocumentLayout first = document.Layout(Isolated);
        edited.Runs.Single().Text = "after";
        DocumentLayout second = document.Layout(Isolated);

        Assert.Same(Content(first, same), Content(second, same));
        Assert.NotSame(Content(first, edited), Content(second, edited));
        Assert.Equal("after", ((LineBox)second.FindBoxes(edited).Single(b => b.Box is LineBox).Box).Text);

        static object Content(DocumentLayout layout, Paragraph paragraph)
        {
            return ((LineBox)layout.FindBoxes(paragraph).Single(b => b.Box is LineBox).Box).Content;
        }
    }
    [Fact]
    public void ObserverSeesEveryBoxAndTheDecorationsOutsideLines()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        body.AddParagraph("text");
        Table table = body.AddTable(1, 2);
        table.Rows[0].Cells[0].Format.Shading = new Shading(ShadingPattern.Clear, DocumentColor.FromHex("FF0000"));
        table.Rows[0].Cells[0].Paragraphs.First().AddRun("cell");
        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false } });
        Recorder recorder = new();

        layout.Pages[0].Draw(new Egami.Drawing.RasterCanvas<Rgba8>(Egami.Raster.Create(10, 10, new Rgba8(255, 255, 255, 255))), recorder);

        Assert.Equal(0, recorder.Depth);
        Assert.Equal(layout.Pages[0].Boxes.SelectMany(b => b.Descendants()).Count(), recorder.Boxes.Count);
        Assert.True(recorder.Decorations > 0);
        Assert.False(recorder.DecorationInLine);
    }

    private sealed class Recorder : LayoutDrawObserver
    {
        private int lines;

        public List<LayoutBox> Boxes { get; } = [];

        public int Depth { get; private set; }

        public int Decorations { get; private set; }

        public bool DecorationInLine { get; private set; }

        public override void BeginBox(LayoutBox box)
        {
            Boxes.Add(box);
            Depth++;
            lines += box is LineBox ? 1 : 0;
        }

        public override void EndBox(LayoutBox box)
        {
            Depth--;
            lines -= box is LineBox ? 1 : 0;
        }

        public override void BeginDecoration()
        {
            Decorations++;
            DecorationInLine |= lines > 0;
        }
    }
}
