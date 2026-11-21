using Egami.Fonts.Layout;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests;

/// <summary>Tracked changes on pages and in PDFs: final, marked up and original (6.19 Build Plan task 11).</summary>
public class RevisionViewTests
{
    private static readonly FontSettings Fonts = new() { UseSystemFonts = false };

    private static LayoutOptions View(RevisionView view, Action<LayoutWarning>? onWarning = null)
    {
        return new LayoutOptions { Fonts = Fonts, RevisionView = view, OnWarning = onWarning };
    }

    /// <summary>"Keep " + inserted "new " by Ann + deleted "old " by Bob + "end", and a second, plain paragraph.</summary>
    private static (Document Document, Paragraph Changed, Run Inserted, Run Deleted) Sample()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Paragraph changed = body.AddParagraph("Keep ");
        Run inserted = changed.AddRun("new ");
        inserted.Revision = new Revision(RevisionKind.Insertion, "Ann");
        Run deleted = changed.AddRun("old ");
        deleted.Revision = new Revision(RevisionKind.Deletion, "Bob");
        changed.AddRun("end");
        body.AddParagraph("Plain");
        return (document, changed, inserted, deleted);
    }

    private static string Text(DocumentLayout layout, Paragraph paragraph)
    {
        return string.Concat(layout.FindBoxes(paragraph).Select(b => b.Box).OfType<LineBox>().Select(l => l.Text));
    }

    private static TextStyle StyleOf(DocumentLayout layout, Paragraph paragraph, string text)
    {
        foreach (LineBox line in layout.FindBoxes(paragraph).Select(b => b.Box).OfType<LineBox>())
        {
            int at = line.Content.Text.Text.IndexOf(text, StringComparison.Ordinal);
            if (at >= 0)
            {
                // The style of the span the text is in (Egami may lay out spans that shape alike as one run).
                IReadOnlyList<StyleSpan> spans = line.Content.Text.RichText.Spans;
                int span = Enumerable.Range(0, spans.Count).Single(i => spans[i].Start <= at && at < spans[i].End);
                return line.Content.Text.GetStyle(span + 1);
            }
        }

        throw new InvalidOperationException($"'{text}' is not laid out.");
    }

    [Fact]
    public void FinalShowsTheChangesAcceptedAndSaysSo()
    {
        (Document document, Paragraph changed, _, _) = Sample();
        List<LayoutWarning> warnings = [];

        DocumentLayout layout = document.Layout(View(RevisionView.Final, warnings.Add));

        Assert.Equal("Keep new end", Text(layout, changed));
        LayoutWarning warning = Assert.Single(warnings);
        Assert.Equal(LayoutWarningKind.ChangesAccepted, warning.Kind);
        Assert.Same(changed, warning.Node);
        Assert.Null(StyleOf(layout, changed, "new").Decoration);
        Assert.All(layout.Pages, p => Assert.False(p.ChangeBars));
    }

    [Fact]
    public void ADocumentWithoutChangesHasNoWarning()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("Nothing changed");
        List<LayoutWarning> warnings = [];

        document.Layout(View(RevisionView.Final, warnings.Add));

        Assert.Empty(warnings);
    }

    [Fact]
    public void MarkupShowsBothSidesInTheirAuthorsColours()
    {
        (Document document, Paragraph changed, _, _) = Sample();
        List<LayoutWarning> warnings = [];

        DocumentLayout layout = document.Layout(View(RevisionView.Markup, warnings.Add));

        Assert.Equal("Keep new old end", Text(layout, changed));
        Assert.Empty(warnings);
        TextStyle inserted = StyleOf(layout, changed, "new");
        TextStyle deleted = StyleOf(layout, changed, "old");
        TextStyle kept = StyleOf(layout, changed, "Keep");
        Assert.Equal(new Rgba8(192, 0, 0, 255), inserted.Color);
        Assert.Equal(TextDecorationLines.Underline, inserted.Decoration!.Lines);
        Assert.Equal(new Rgba8(0, 70, 190, 255), deleted.Color);
        Assert.Equal(TextDecorationLines.LineThrough, deleted.Decoration!.Lines);
        Assert.Null(kept.Decoration);
        Assert.All(layout.Pages, p => Assert.True(p.ChangeBars));
    }

    [Fact]
    public void MarkedTextIsDrawnInItsAuthorsColour()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Plain words then ");
        Run inserted = paragraph.AddRun("INSERTED");
        inserted.Format.Size = 30;
        paragraph.Runs.First().Format.Size = 30;
        inserted.Revision = new Revision(RevisionKind.Insertion, "Ann");

        DocumentLayout layout = document.Layout(View(RevisionView.Markup));
        Egami.Raster<Rgba8> page = layout.Pages[0].Render(72);

        LayoutRect line = layout.FindBoxes(paragraph).Single(b => b.Box is LineBox).Box.Bounds;
        Assert.True(CountPixels(page, line, p => p.R > 150 && p.G < 80 && p.B < 80) > 50, "red inserted text");
        Assert.True(CountPixels(page, line, p => p.R < 80 && p.G < 80 && p.B < 80) > 50, "black plain text");
    }

    [Fact]
    public void AColouredWordInALineIsDrawnInItsColour()
    {
        // Not a tracked change: a run whose only difference is its colour shares a shaped run with the text before it.
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Black words then ");
        Run red = paragraph.AddRun("RED");
        red.Format.Color = DocumentColor.FromHex("FF0000");
        foreach (Run run in paragraph.Runs)
        {
            run.Format.Size = 30;
        }

        DocumentLayout layout = document.Layout(View(RevisionView.Final));
        Egami.Raster<Rgba8> page = layout.Pages[0].Render(72);

        LayoutRect line = layout.FindBoxes(paragraph).Single(b => b.Box is LineBox).Box.Bounds;
        Assert.True(CountPixels(page, line, p => p.R > 200 && p.G < 60 && p.B < 60) > 50, "red word");
        Assert.True(CountPixels(page, line, p => p.R < 60 && p.G < 60 && p.B < 60) > 50, "black words");
    }

    private static int CountPixels(Egami.Raster<Rgba8> page, LayoutRect area, Func<Rgba8, bool> match)
    {
        int count = 0;
        for (int y = (int)area.Y; y < (int)area.Bottom && y < page.Height; y++)
        {
            for (int x = (int)area.X; x < (int)(area.X + area.Width) && x < page.Width; x++)
            {
                if (match(page.GetPixel(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void FormatChangesKeepTheirNewLookWithADottedUnderline()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("plain ");
        Run bold = paragraph.AddRun("bold");
        bold.Format.Bold = true;
        bold.FormatRevision = new FormatRevision("Cid", null, new RunFormat());

        DocumentLayout layout = document.Layout(View(RevisionView.Markup));

        TextStyle style = StyleOf(layout, paragraph, "bold");
        Assert.Equal(700, style.FontStyle!.Value.Weight);
        Assert.Equal(TextDecorationStyle.Dotted, style.Decoration!.Style);
        Assert.Equal(TextDecorationLines.Underline, style.Decoration.Lines);
        Assert.Equal(new Rgba8(192, 0, 0, 255), style.Decoration.Color);
        Assert.NotEqual(new Rgba8(192, 0, 0, 255), style.Color);
    }

    [Fact]
    public void OriginalShowsTheDocumentBeforeItsChanges()
    {
        (Document document, Paragraph changed, _, _) = Sample();

        DocumentLayout layout = document.Layout(View(RevisionView.Original));

        Assert.NotSame(document, layout.Document);
        Paragraph shown = layout.Document.Sections[0].Body.Blocks.OfType<Paragraph>().First();
        Assert.Equal("Keep old end", Text(layout, shown));
        Assert.Equal(2, document.Revisions.Count);
        Assert.Equal("Keep new end", Text(document.Layout(View(RevisionView.Final)), changed));
    }

    [Fact]
    public void DeletedParagraphsTakeSpaceInMarkup()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        body.AddParagraph("First");
        Paragraph gone = body.AddParagraph();
        gone.AddRun("Deleted paragraph").Revision = new Revision(RevisionKind.Deletion, "Ann");
        gone.MarkRevision = new Revision(RevisionKind.Deletion, "Ann");
        body.AddParagraph("Last");

        DocumentLayout markup = document.Layout(View(RevisionView.Markup));
        DocumentLayout original = document.Layout(View(RevisionView.Original));

        Assert.Equal("Deleted paragraph", Text(markup, gone));
        Assert.Equal(TextDecorationLines.LineThrough, StyleOf(markup, gone, "Deleted").Decoration!.Lines);
        Assert.Equal(["First", "Deleted paragraph", "Last"], original.Document.Sections[0].Body.Blocks.Select(b => b.GetText()));
    }

    [Fact]
    public void SwitchingViewsBuildsParagraphsAgain()
    {
        (Document document, Paragraph changed, _, _) = Sample();

        Assert.Equal("Keep new end", Text(document.Layout(View(RevisionView.Final)), changed));
        Assert.Equal("Keep new old end", Text(document.Layout(View(RevisionView.Markup)), changed));
        Assert.Equal("Keep new end", Text(document.Layout(View(RevisionView.Final)), changed));
    }

    [Fact]
    public void ChangeBarsStandInTheLeftMarginBesideChangedLinesOnly()
    {
        (Document document, Paragraph changed, _, _) = Sample();
        Paragraph plain = (Paragraph)document.Sections[0].Body.Blocks[1];

        DocumentLayout markup = document.Layout(View(RevisionView.Markup));
        Egami.Raster<Rgba8> marked = markup.Pages[0].Render(72);
        Egami.Raster<Rgba8> final = document.Layout(View(RevisionView.Final)).Pages[0].Render(72);

        LayoutRect line = markup.FindBoxes(changed).Single(b => b.Box is LineBox).Box.Bounds;
        LayoutRect plainLine = markup.FindBoxes(plain).Single(b => b.Box is LineBox).Box.Bounds;
        Assert.True(DarkAt(marked, line.X - 9, line), "a bar beside the changed line");
        Assert.False(DarkAt(marked, line.X - 9, plainLine), "no bar beside the plain line");
        Assert.False(DarkAt(final, line.X - 9, line), "no bars in the final view");
    }

    [Fact]
    public void MirroredMarginsPutBarsOnTheOutsideOfOddPages()
    {
        (Document document, Paragraph changed, _, _) = Sample();
        document.Settings.MirrorMargins = true;

        DocumentLayout markup = document.Layout(View(RevisionView.Markup));
        Egami.Raster<Rgba8> page = markup.Pages[0].Render(72);
        AreaBox body = markup.Pages[0].Boxes.OfType<AreaBox>().Single(a => a.Area == PageArea.Body);
        LayoutRect line = markup.FindBoxes(changed).Single(b => b.Box is LineBox).Box.Bounds;

        Assert.True(DarkAt(page, body.Bounds.X + body.Bounds.Width + 9, line));
        Assert.False(DarkAt(page, body.Bounds.X - 9, line));
    }

    [Fact]
    public void PdfExportShowsTheViewAsked()
    {
        (Document document, _, _, _) = Sample();

        string final = Export(document, RevisionView.Final);
        string markup = Export(document, RevisionView.Markup);
        string original = Export(document, RevisionView.Original);

        Assert.Contains("Keep new end", final, StringComparison.Ordinal);
        Assert.Contains("Keep new old end", markup, StringComparison.Ordinal);
        Assert.Contains("Keep old end", original, StringComparison.Ordinal);
        Assert.Equal(2, document.Revisions.Count);

        static string Export(Document document, RevisionView view)
        {
            using MemoryStream stream = new();
            document.SaveAsPdf(stream, new PdfExportOptions { Layout = new LayoutOptions { Fonts = Fonts }, RevisionView = view });
            using PdfDocument pdf = PdfDocument.Open(stream.ToArray());
            return pdf.Pages[0].ExtractText();
        }
    }

    [Fact]
    public void TaggedMarkupKeepsTheReadingOrderAndBarsAreArtifacts()
    {
        (Document document, _, _, _) = Sample();

        using PdfDocument final = Tagged(document, RevisionView.Final);
        using PdfDocument markup = Tagged(document, RevisionView.Markup);

        PdfStructureElement finalRoot = Assert.IsType<PdfStructureElement>(final.Structure.Root);
        PdfStructureElement markupRoot = Assert.IsType<PdfStructureElement>(markup.Structure.Root);
        Assert.Equal(finalRoot.Elements.Select(e => e.Type.Name), markupRoot.Elements.Select(e => e.Type.Name));
        Assert.StartsWith("Keep new old end", markup.Pages[0].ExtractText().Trim(), StringComparison.Ordinal);

        static PdfDocument Tagged(Document document, RevisionView view)
        {
            using MemoryStream stream = new();
            document.SaveAsPdf(stream, new PdfExportOptions { Layout = new LayoutOptions { Fonts = Fonts }, RevisionView = view, Tagged = true });
            return PdfDocument.Open(stream.ToArray());
        }
    }

    private static bool DarkAt(Egami.Raster<Rgba8> page, double x, LayoutRect line)
    {
        int middle = (int)(line.Y + (line.Height / 2));
        for (int dx = -1; dx <= 1; dx++)
        {
            if (page.GetPixel((int)Math.Round(x) + dx, middle).R < 128)
            {
                return true;
            }
        }

        return false;
    }
}
