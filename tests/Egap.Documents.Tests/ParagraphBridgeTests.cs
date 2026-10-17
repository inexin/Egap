using Egami.Fonts;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Model paragraphs to Egami's line layout (Phase 6 Build Plan task 16).</summary>
public class ParagraphBridgeTests
{
    private static ParagraphBridge Bridge(Document document)
    {
        return new ParagraphBridge(new FontProvider(document, new FontSettings { UseSystemFonts = false }), document);
    }

    [Fact]
    public void TextStylesFollowEffectiveFormatting()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.AddRun("plain ");
        Run bold = paragraph.AddRun("bold");
        bold.Format.Bold = true;
        bold.Format.Size = 20;
        bold.Format.FontName = "Calibri";

        ParagraphContent content = Bridge(document).Build(paragraph);

        Assert.Equal("plain bold", content.Text.Text);
        Egami.Fonts.Layout.TextStyle style = content.Text.RichText.Spans[^1].Style;
        Assert.Equal(20, style.Size);
        Assert.Equal(700, style.FontStyle!.Value.Weight);
        Assert.Equal("Carlito", style.FontFamilies![0].Name);
        Assert.Equal(20, content.MaxFontSize);
        Assert.Same(bold, content.Spans[^1].Node);
    }

    [Fact]
    public void ListLabelsFieldResultsAndAcceptedChanges()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.SetList(document.Lists.AddNumbered());
        paragraph.Format.LeftIndent = 36;
        paragraph.Format.FirstLineIndent = -18;
        paragraph.AddRun("Page ");
        paragraph.AddField("PAGE", "7");
        Run gone = paragraph.AddRun(" deleted");
        gone.Revision = new Revision(RevisionKind.Deletion, "Ann");
        paragraph.AddRun(" end");
        Run hidden = paragraph.AddRun("secret");
        hidden.Format.Hidden = true;

        ParagraphContent content = Bridge(document).Build(paragraph);

        Assert.Equal("1.\tPage 7 end", content.Text.Text);
        Assert.Equal(3, content.LabelLength);
        Assert.Single(content.PageFields);
        Assert.Equal(18, content.Text.Style.TabStops!.Single().Position);
    }

    [Fact]
    public void ObjectsBreaksAndLayout()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.AddRun("before");
        paragraph.AddPicture(new ImageData(new byte[] { 1 }, "image/png"), 40, 20);
        Picture floating = paragraph.AddPicture(new ImageData(new byte[] { 1 }, "image/png"), 50, 50);
        floating.Floating = new FloatingPlacement();
        paragraph.AddBreak(BreakKind.Page);
        paragraph.AddRun("after a long stretch of words that will have to wrap onto more lines than one");

        ParagraphContent content = Bridge(document).Build(paragraph);

        Assert.Single(content.InlineObjects);
        Assert.Single(content.Floats);
        Assert.Equal(BreakKind.Page, content.Breaks.Single().Kind);
        Egami.Fonts.Layout.TextLayout layout = content.Text.Layout(120);
        Assert.True(layout.Lines.Count >= 3);
        Assert.Equal(40, layout.PlaceholderRects.Single().Width, 1);
    }

    [Fact]
    public void EmptyParagraphTakesItsMarkSize()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.MarkFormat.Size = 30;

        ParagraphContent content = Bridge(document).Build(paragraph);

        Assert.Equal(30, content.MaxFontSize);
        Assert.Equal("\u200B", content.Text.Text);
    }
}
