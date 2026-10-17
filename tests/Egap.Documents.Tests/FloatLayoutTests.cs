using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Floating objects and text boxes (Phase 6 Build Plan task 19).</summary>
public class FloatLayoutTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };
    private static readonly ImageData Pixel = new(new byte[] { 1 }, "image/png");
    private static readonly string Long = string.Join(" ", Enumerable.Repeat("words that wrap around the picture beside them", 20));

    private static (Document Document, Paragraph Paragraph, Picture Picture) WithFloat(WrapMode wrap, FloatingAlignment alignment = FloatingAlignment.Start)
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        Picture picture = paragraph.AddPicture(Pixel, 150, 100);
        picture.Floating = new FloatingPlacement { HorizontalAlignment = alignment, Wrap = wrap, DistanceLeft = 0, DistanceRight = 0 };
        paragraph.AddRun(Long);
        return (document, paragraph, picture);
    }

    [Fact]
    public void SquareWrapPutsTextBesideTheFloat()
    {
        (Document document, Paragraph paragraph, Picture picture) = WithFloat(WrapMode.Square);

        DocumentLayout layout = document.Layout(Isolated);

        ObjectBox box = (ObjectBox)layout.FindBoxes(picture).Single().Box;
        Assert.True(box.IsFloating);
        Assert.Equal(72, box.Bounds.X, 1);
        LineBox[] lines = [.. layout.Pages[0].BodyLines];
        Assert.True(lines[0].Bounds.X >= box.Bounds.Right - 0.01, "the first line starts right of the picture");
        Assert.True(lines[^1].Bounds.Y >= box.Bounds.Bottom - 0.01 ? lines[^1].Bounds.X < box.Bounds.Right : true);
        Assert.Contains(lines, l => l.Bounds.Y >= box.Bounds.Bottom && l.Bounds.X < box.Bounds.X + 1);
        Assert.Same(paragraph, lines[0].Node);
    }

    [Fact]
    public void TopAndBottomMovesTextBelow()
    {
        (Document document, _, Picture picture) = WithFloat(WrapMode.TopAndBottom, FloatingAlignment.Center);

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect box = layout.FindBoxes(picture).Single().Box.Bounds;
        Assert.All(layout.Pages[0].BodyLines, l => Assert.True(l.Bounds.Y >= box.Bottom - 0.01 || l.Bounds.Bottom <= box.Y + 0.01));
    }

    [Fact]
    public void BehindTextLeavesLinesFullWidth()
    {
        (Document document, _, _) = WithFloat(WrapMode.BehindText);

        DocumentLayout layout = document.Layout(Isolated);

        Assert.All(layout.Pages[0].BodyLines, l => Assert.Equal(72, l.Bounds.X, 1));
    }

    [Fact]
    public void HeaderLogosTextBoxesAndInlineObjects()
    {
        Document document = new();
        Section section = document.AddSection();
        Paragraph header = section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Header");
        Picture logo = header.AddPicture(Pixel, 40, 40);
        logo.Floating = new FloatingPlacement { HorizontalAnchor = HorizontalAnchor.Page, HorizontalAlignment = FloatingAlignment.End, VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 10, Wrap = WrapMode.InFrontOfText };
        Paragraph body = section.Body.AddParagraph("Before ");
        Picture inline = body.AddPicture(Pixel, 30, 20);
        Shape box = body.Inlines.Add(new Shape(document, ShapeKind.Rectangle, 200, 80));
        box.AddTextBox().AddParagraph("Inside the box");
        box.Floating = new FloatingPlacement { HorizontalAnchor = HorizontalAnchor.Margin, VerticalAnchor = VerticalAnchor.Margin, VerticalOffset = 300, Wrap = WrapMode.Square };

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect logoBounds = layout.FindBoxes(logo).Single().Box.Bounds;
        Assert.Equal(612 - 40, logoBounds.X, 1);
        Assert.Equal(10, logoBounds.Y, 1);
        ObjectBox inlineBox = (ObjectBox)layout.FindBoxes(inline).Single().Box;
        Assert.False(inlineBox.IsFloating);
        Assert.Equal(30, inlineBox.Bounds.Width, 1);
        ObjectBox textBox = (ObjectBox)layout.FindBoxes(box).Single().Box;
        LineBox inside = textBox.Descendants().OfType<LineBox>().Single();
        Assert.Equal("Inside the box", inside.Text);
        Assert.True(inside.Bounds.Y >= textBox.Bounds.Y && inside.Bounds.Bottom <= textBox.Bounds.Bottom + 0.01);
    }
}
