using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf;

/// <summary>Comments written as PDF annotations, and read back as comments.</summary>
public sealed class CommentExportTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };
    private static readonly DateTimeOffset When = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    /// <summary>"Sales rose by a tenth." with a comment on "rose" (a reply, then the reply's reply), a resolved one on "tenth" and an empty one at the end.</summary>
    private static Document Sample()
    {
        Document document = new();
        document.Properties.Title = "Comments";
        document.Styles.DefaultRunFormat.Language = "en-US";
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.AddRun("Sales ");
        Comment rose = Comment(document, paragraph, "rose", "Ann", "Is this right?");
        paragraph.AddRun(" by a ");
        Comment tenth = Comment(document, paragraph, "tenth", "Ben", "Source?");
        tenth.Done = true;
        paragraph.AddRun(".");
        Comment empty = new(document, "Cy", When);
        empty.AddParagraph("A note on nothing");
        paragraph.Inlines.Add(empty);
        Comment reply = rose.AddReply("Ben", "Yes, checked.", When);
        reply.AddReply("Ann", "Thanks.", When);
        return document;
    }

    private static Comment Comment(Document document, Paragraph paragraph, string text, string author, string body)
    {
        Comment comment = new(document, author, When);
        comment.AddParagraph(body);
        paragraph.Inlines.Add(new CommentRangeStart(document, comment));
        paragraph.AddRun(text);
        paragraph.Inlines.Add(new CommentRangeEnd(document, comment));
        paragraph.Inlines.Add(comment);
        return comment;
    }

    private static byte[] Export(Document document, PdfExportOptions options)
    {
        using MemoryStream stream = new();
        document.SaveAsPdf(stream, options with { Layout = Isolated });
        return stream.ToArray();
    }

    [Fact]
    public void CommentsAreLeftOutByDefault()
    {
        using PdfDocument pdf = PdfDocument.Open(Export(Sample(), new PdfExportOptions()));

        Assert.Empty(pdf.Pages[0].Annotations);
    }

    [Fact]
    public void EachCommentIsAnAnnotation()
    {
        using PdfDocument pdf = PdfDocument.Open(Export(Sample(), new PdfExportOptions { Comments = PdfCommentMode.Annotations }));

        PdfAnnotation[] all = [.. pdf.Pages[0].Annotations];
        PdfHighlightAnnotation[] highlights = [.. all.OfType<PdfHighlightAnnotation>()];
        Assert.Equal(2, highlights.Length);
        PdfHighlightAnnotation rose = highlights.Single(h => h.Contents == "Is this right?");
        Assert.Equal("Ann", rose.Author);
        Assert.Equal(When, rose.ModificationDate);
        Assert.NotNull(rose.Popup);
        Assert.Equal(4, rose.QuadPoints.Count);
        Assert.True((rose.Flags & PdfAnnotationFlags.Print) != 0);
        Assert.NotNull(rose.GetAppearance());

        PdfTextAnnotation[] notes = [.. all.OfType<PdfTextAnnotation>()];
        Assert.Contains(notes, n => n.Contents == "A note on nothing" && n.InReplyTo is null);
        PdfTextAnnotation yes = notes.Single(n => n.Contents == "Yes, checked.");
        Assert.Equal(rose, yes.InReplyTo);
        Assert.Equal(yes, notes.Single(n => n.Contents == "Thanks.").InReplyTo);
        PdfTextAnnotation state = notes.Single(n => n.State is not null);
        Assert.Equal("Completed", state.State);
        Assert.Equal("Review", state.StateModel);
        Assert.Equal("Source?", ((PdfMarkupAnnotation)state.InReplyTo!).Contents);
    }

    [Fact]
    public void ResolvedCommentsCanBeLeftOut()
    {
        using PdfDocument pdf = PdfDocument.Open(Export(Sample(), new PdfExportOptions { Comments = PdfCommentMode.Annotations, IncludeResolvedComments = false }));

        Assert.DoesNotContain(pdf.Pages[0].Annotations, a => a.Contents is "Source?" or "Completed");
        Assert.Contains(pdf.Pages[0].Annotations, a => a.Contents == "Is this right?");
    }

    [Fact]
    public void CommentsComeBackFromThePdf()
    {
        Document back = Document.LoadPdf(new MemoryStream(Export(Sample(), new PdfExportOptions { Comments = PdfCommentMode.Annotations })));

        Comment[] comments = [.. back.Descendants<Comment>()];
        Comment rose = comments.Single(c => c.Story.GetText().Trim() == "Is this right?");
        Assert.Equal("Ann", rose.Author);
        Assert.Equal(When, rose.Date);
        Assert.Equal("rose", RangeText(back, rose));
        Comment yes = comments.Single(c => c.Story.GetText().Trim() == "Yes, checked.");
        Assert.Equal(rose.Id, yes.ParentId);
        Assert.Equal(yes.Id, comments.Single(c => c.Story.GetText().Trim() == "Thanks.").ParentId);
        Comment tenth = comments.Single(c => c.Story.GetText().Trim() == "Source?");
        Assert.True(tenth.Done);
        Assert.False(rose.Done);
        Assert.Equal("tenth", RangeText(back, tenth));
        Assert.DoesNotContain(comments, c => c.Story.GetText().Trim() == "Completed");
    }

    [Fact]
    public void PdfA2bAndUniversalAccessibilityHold()
    {
        using PdfDocument archive = PdfDocument.Open(Export(Sample(), new PdfExportOptions { Comments = PdfCommentMode.Annotations, Conformance = PdfAConformance.PdfA2b }));
        PdfAReport report = PdfAConverter.Check(archive, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, string.Join("\n", report.Unmet.Select(i => i.Clause + " " + i.Description)));

        using PdfDocument accessible = PdfDocument.Open(Export(Sample(), new PdfExportOptions { Comments = PdfCommentMode.Annotations, UniversalAccessibility = true }));
        PdfAReport ua = PdfAConverter.CheckUniversalAccessibility(accessible);
        Assert.True(ua.Succeeded, string.Join("\n", ua.Unmet.Select(i => i.Clause + " " + i.Description)));
        PdfStructureElement[] annots = [.. accessible.Structure.Root!.DescendantsAndSelf().Where(e => e.Type == PdfStructureType.Annot)];
        Assert.True(annots.Length >= 4);
        Assert.Contains(annots, a => a.AlternateText == "Is this right?");
    }

    /// <summary>The text between a comment's range marks.</summary>
    private static string RangeText(Document document, Comment comment)
    {
        List<Inline> inlines = [.. document.Descendants<Inline>()];
        int start = inlines.FindIndex(i => i is CommentRangeStart s && s.CommentId == comment.Id);
        int end = inlines.FindIndex(i => i is CommentRangeEnd e && e.CommentId == comment.Id);
        return string.Concat(inlines.Skip(start + 1).Take(end - start - 1).OfType<Run>().Where(r => r.Ancestors().OfType<CommentStory>().FirstOrDefault() is null).Select(r => r.Text));
    }
}
