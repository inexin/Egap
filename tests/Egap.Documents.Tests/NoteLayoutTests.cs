using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Footnotes and endnotes in layout (Phase 6 Build Plan task 20).</summary>
public class NoteLayoutTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    [Fact]
    public void FootnotesSitAtTheFootOfTheirPage()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Footnote first = body.AddParagraph("Text with a note").AddFootnote(" The note's text.");
        for (int i = 0; i < 60; i++)
        {
            body.AddParagraph("filler " + i);
        }

        Footnote second = body.AddParagraph("A later note").AddFootnote(" Second.");

        DocumentLayout layout = document.Layout(Isolated);

        AreaBox notes = layout.Pages[0].Boxes.OfType<AreaBox>().Single(a => a.Area == PageArea.Notes);
        LineBox note = notes.Descendants().OfType<LineBox>().Single();
        Assert.Equal("1 The note's text.", note.Text);
        Assert.True(notes.Bounds.Bottom <= 720 + 0.01);
        Assert.All(layout.Pages[0].BodyLines, l => Assert.True(l.Bounds.Bottom <= notes.Bounds.Y + 0.01));
        PageLayout secondPage = layout.PageOf(second.Paragraph!)!;
        Assert.Contains(secondPage.Boxes.OfType<AreaBox>().Where(a => a.Area == PageArea.Notes).SelectMany(a => a.Descendants().OfType<LineBox>()), l => l.Text == "2 Second.");
        Assert.Same(first.Story.Paragraphs.Single(), note.Node);
    }

    [Fact]
    public void ALineMovesWithItsNoteWhenTheNoteDoesNotFit()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        for (int i = 0; i < 44; i++)
        {
            body.AddParagraph("filler " + i);
        }

        Paragraph last = body.AddParagraph("reference");
        Footnote note = last.AddFootnote(string.Join(" ", Enumerable.Repeat("a long note", 60)));

        DocumentLayout layout = document.Layout(Isolated);

        PageLayout page = layout.PageOf(last)!;
        Assert.Contains(page.Boxes.OfType<AreaBox>().Where(a => a.Area == PageArea.Notes).SelectMany(a => a.Descendants()), b => b.Node == note.Story.Paragraphs.Single());
        Assert.All(page.BodyLines, l => Assert.True(l.Bounds.Bottom <= page.Boxes.OfType<AreaBox>().Single(a => a.Area == PageArea.Notes).Bounds.Y + 0.01));
    }

    [Fact]
    public void EndnotesFollowTheText()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        body.AddParagraph("Body").AddFootnote(" An endnote.", NoteKind.Endnote);
        body.AddParagraph("More body");

        DocumentLayout layout = document.Layout(Isolated);

        string[] lines = [.. layout.Pages[^1].BodyLines.Select(l => l.Text)];
        Assert.Equal("i An endnote.", lines[^1]);
        Assert.DoesNotContain(layout.Pages[0].Boxes.OfType<AreaBox>(), a => a.Area == PageArea.Notes);
    }
}
