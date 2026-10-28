using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf;

/// <summary>Comments printed in a pane beside each page, as Word prints markup.</summary>
public sealed class MarginCommentTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };
    private static readonly DateTimeOffset When = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static Document Commented(int paragraphs, string text = "Check this.")
    {
        Document document = new();
        document.Properties.Title = "Margin";
        document.Styles.DefaultRunFormat.Language = "en-US";
        Section section = document.AddSection();
        section.PageSetup.PageWidth = 612;
        section.PageSetup.PageHeight = 792;
        for (int i = 0; i < paragraphs; i++)
        {
            Paragraph paragraph = section.Body.AddParagraph("Paragraph " + i + " ");
            Comment comment = new(document, i % 2 == 0 ? "Ann" : "Ben", When);
            comment.AddParagraph(text + " " + i);
            paragraph.Inlines.Add(new CommentRangeStart(document, comment));
            paragraph.AddRun("marked");
            paragraph.Inlines.Add(new CommentRangeEnd(document, comment));
            paragraph.Inlines.Add(comment);
            paragraph.AddRun(" and more text after it.");
            if (i == 0)
            {
                comment.AddReply("Ben", "Agreed.", When);
            }
        }

        return document;
    }

    private static PdfDocument Export(Document document, PdfExportOptions options)
    {
        using MemoryStream stream = new();
        document.SaveAsPdf(stream, options with { Layout = Isolated });
        return PdfDocument.Open(stream.ToArray());
    }

    [Fact]
    public void ThePageKeepsItsSizeAndTheContentMovesLeft()
    {
        using PdfDocument pdf = Export(Commented(3), new PdfExportOptions { Comments = PdfCommentMode.Margin });

        Assert.Single(pdf.Pages);
        Assert.Equal(612, pdf.Pages[0].MediaBox.Width, 1);
        Assert.Equal(792, pdf.Pages[0].MediaBox.Height, 1);
        string text = pdf.Pages[0].ExtractText();
        Assert.Contains("Paragraph 0", text, StringComparison.Ordinal);
        Assert.Contains("Check this. 2", text, StringComparison.Ordinal);
        Assert.Contains("Agreed.", text, StringComparison.Ordinal);

        // The body text sits in the left two thirds, the balloons in the pane.
        PdfTextLine body = pdf.Pages[0].GetText().Lines.First(l => l.Text.Contains("Paragraph 1", StringComparison.Ordinal));
        PdfTextLine balloon = pdf.Pages[0].GetText().Lines.First(l => l.Text.Contains("Check this. 1", StringComparison.Ordinal));
        Assert.True(body.Box.Right < 612 * 2 / 3.0, $"body to {body.Box.Right}");
        Assert.True(balloon.Box.Left > 612 * 2 / 3.0, $"balloon from {balloon.Box.Left}");
    }

    [Fact]
    public void BalloonsThatDoNotFitGoToACommentsListAtTheEnd()
    {
        using PdfDocument pdf = Export(Commented(30, "A long comment that takes several lines in the narrow pane beside the page, so the balloons run out of room before the page does."), new PdfExportOptions { Comments = PdfCommentMode.Margin });

        string last = pdf.Pages[^1].ExtractText();
        Assert.Contains("Comments", last, StringComparison.Ordinal);
        Assert.Contains("Page 1", last, StringComparison.Ordinal);
        string all = string.Join(' ', string.Join(' ', Enumerable.Range(0, pdf.Pages.Count).Select(p => pdf.Pages[p].ExtractText())).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        int printed = Enumerable.Range(0, 30).Count(i => all.Contains("before the page does. " + i + " ", StringComparison.Ordinal) || all.EndsWith("before the page does. " + i, StringComparison.Ordinal));
        Assert.Equal(30, printed);
    }

    [Fact]
    public void LinksFollowTheScaledPage()
    {
        Document document = Commented(1);
        document.Sections[0].Body.AddParagraph().AddHyperlink("Egap", "https://example.com/");

        using PdfDocument pdf = Export(document, new PdfExportOptions { Comments = PdfCommentMode.Margin });

        PdfLinkAnnotation link = pdf.Pages[0].Annotations.OfType<PdfLinkAnnotation>().Single();
        PdfTextLine line = pdf.Pages[0].GetText().Lines.First(l => l.Text.Contains("Egap", StringComparison.Ordinal));
        Assert.True(Math.Abs(link.Rectangle.Normalize().Left - line.Box.Left) < 2, $"link at {link.Rectangle.Left}, text at {line.Box.Left}");
    }

    [Fact]
    public void FormFieldsAndMarginCommentsDoNotMix()
    {
        Assert.Throws<ArgumentException>(() => Export(Commented(1), new PdfExportOptions { Comments = PdfCommentMode.Margin, FormFields = true }));
    }

    [Fact]
    public void TaggedBalloonsAreNotes()
    {
        using PdfDocument pdf = Export(Commented(2), new PdfExportOptions { Comments = PdfCommentMode.Margin, UniversalAccessibility = true });

        PdfStructureElement[] notes = [.. pdf.Structure.Root!.DescendantsAndSelf().Where(e => e.Type == PdfStructureType.Note)];
        Assert.Equal(2, notes.Length);
        PdfAReport ua = PdfAConverter.CheckUniversalAccessibility(pdf);
        Assert.True(ua.Succeeded, string.Join("\n", ua.Unmet.Select(i => i.Clause + " " + i.Description)));
    }
}
