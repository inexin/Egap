using Egap.Documents.Layout;
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>How a document's comments go into the PDF.</summary>
public enum PdfCommentMode
{
    /// <summary>The default: comments are left out.</summary>
    None,

    /// <summary>
    /// Each comment becomes a PDF annotation any viewer shows and edits: a highlight over the text it is about (a note at its
    /// place when it is about no text) with its author, date and text in a popup, replies pointing at it.
    /// </summary>
    Annotations,
}

/// <summary>
/// Comments as markup annotations (ISO 32000-2, 12.5.6.2 to 12.5.6.10), as Acrobat writes them: a Highlight over the line
/// quads of the commented text, or a Text note where an empty range sits, with <c>/T</c>, <c>/M</c>, <c>/Contents</c> and
/// a popup; replies as Text annotations with <c>/IRT</c>; a resolved comment as a <c>Completed</c> review state reply.
/// Every annotation prints and has an appearance, as PDF/A asks; tagged output puts each in an <c>Annot</c> element of its
/// paragraph.
/// </summary>
internal sealed class CommentExport(DocumentLayout layout, Dictionary<PageLayout, PdfPage> pageOf, PdfTagger? tagger, bool includeResolved)
{
    private static readonly PdfRgbColor Yellow = new(1, 0.92, 0.23);
    private readonly Dictionary<int, (PdfMarkupAnnotation Annotation, PdfPage Page)> annotations = [];
    private readonly List<Inline> inlines = [.. layout.Document.Descendants<Inline>()];

    public void Write()
    {
        List<Comment> comments = [.. layout.Document.Descendants<Comment>().Where(c => c.Ancestors().OfType<CommentStory>().FirstOrDefault() is null)];
        foreach (Comment comment in comments.Where(c => c.ParentId is null))
        {
            if (!includeResolved && comment.Done)
            {
                continue;
            }

            if (Place(comment) is { } placed)
            {
                Annotate(comment, placed.Page, placed.Rects);
            }
        }

        // Replies come after the comments they answer, in document order.
        foreach (Comment reply in comments.Where(c => c.ParentId is not null))
        {
            if (Thread(reply) is { } first && annotations.TryGetValue(first.Id, out (PdfMarkupAnnotation Annotation, PdfPage Page) parent))
            {
                // A reply answers the comment it names, which may itself be a reply in the thread.
                PdfMarkupAnnotation to = reply.ParentId is int id && annotations.TryGetValue(id, out (PdfMarkupAnnotation Annotation, PdfPage Page) direct) ? direct.Annotation : parent.Annotation;
                PdfTextAnnotation note = Reply(parent.Page, to, Contents(reply), reply.Author, reply.Date);
                annotations[reply.Id] = (note, parent.Page);
                Tag(first, parent.Page, note);
            }
        }
    }

    /// <summary>The page and the line rectangles of the text a comment is about, or of its mark when it is about none.</summary>
    private (PageLayout Page, List<LayoutRect> Rects)? Place(Comment comment)
    {
        int start = inlines.FindIndex(i => i is CommentRangeStart s && s.CommentId == comment.Id);
        int end = inlines.FindIndex(i => i is CommentRangeEnd e && e.CommentId == comment.Id);
        List<PageRect> rects = [];
        if (start >= 0 && end > start)
        {
            for (int i = start + 1; i < end; i++)
            {
                if (inlines[i] is Run && inlines[i].Ancestors().OfType<CommentStory>().FirstOrDefault() is null)
                {
                    rects.AddRange(layout.GetTextRects(inlines[i]).Where(r => r.Bounds.Width > 0));
                }
            }
        }

        if (rects.Count > 0)
        {
            // The range's first page carries the comment; its lines there are joined line by line.
            PageLayout page = rects[0].Page;
            return (page, Lines([.. rects.Where(r => r.Page == page).Select(r => r.Bounds)]));
        }

        // No text in the range: a note at the text just before the mark, or at the start of its paragraph.
        Inline anchor = start >= 0 ? inlines[start] : comment;
        for (int i = inlines.IndexOf(anchor) - 1; i >= 0 && inlines[i].Ancestors().OfType<Paragraph>().FirstOrDefault() == anchor.Ancestors().OfType<Paragraph>().FirstOrDefault(); i--)
        {
            if (inlines[i] is Run && layout.GetTextRects(inlines[i]).LastOrDefault(r => r.Bounds.Width > 0) is { Page: not null } before)
            {
                return (before.Page, [new LayoutRect(before.Bounds.Right, before.Bounds.Y, 0, before.Bounds.Height)]);
            }
        }

        if (anchor.Ancestors().OfType<Paragraph>().FirstOrDefault() is { } paragraph
            && layout.FindBoxes(paragraph).FirstOrDefault(b => b.Box is LineBox) is { Page: not null } line)
        {
            return (line.Page, [new LayoutRect(line.Box.Bounds.X, line.Box.Bounds.Y, 0, line.Box.Bounds.Height)]);
        }

        return null;
    }

    private void Annotate(Comment comment, PageLayout page, List<LayoutRect> rects)
    {
        PdfPage pdfPage = pageOf[page];
        double height = page.Height;
        PdfMarkupAnnotation annotation;
        if (rects.Exists(r => r.Width > 0))
        {
            List<PdfPoint> quads = [];
            foreach (LayoutRect r in rects.Where(r => r.Width > 0))
            {
                quads.AddRange(PdfTextMarkupAnnotation.Quad(new PdfRectangle(r.X, height - r.Bottom, r.Right, height - r.Y)));
            }

            annotation = new PdfHighlightAnnotation(quads);
        }
        else
        {
            // A note's icon sits above the line, at the place the comment marks.
            LayoutRect at = rects[0];
            annotation = new PdfTextAnnotation(new PdfRectangle(at.X, height - at.Y, at.X + 14, height - at.Y + 14)) { Icon = "Comment" };
        }

        annotation.Contents = Contents(comment);
        annotation.Author = comment.Author;
        annotation.ModificationDate = comment.Date;
        annotation.CreationDate = comment.Date;
        annotation.Color = Yellow;
        annotation.Flags = PdfAnnotationFlags.Print;
        pdfPage.Annotations.Add(annotation);
        PdfRectangle r0 = annotation.Rectangle;
        PdfPopupAnnotation popup = pdfPage.Annotations.AddPopup(annotation, new PdfRectangle(Math.Min(r0.Right + 4, page.Width - 4), r0.Top - 80, Math.Min(r0.Right + 184, page.Width), r0.Top));
        annotations[comment.Id] = (annotation, pdfPage);
        Tag(comment, pdfPage, annotation);

        // PDF/UA tags popups too, each with the text it shows (ISO 14289-1, 7.18.1).
        if (tagger is not null)
        {
            popup.Contents = annotation.Contents;
            Tag(comment, pdfPage, popup);
        }
        if (comment.Done)
        {
            PdfTextAnnotation state = Reply(pdfPage, annotation, "Completed", comment.Author, comment.Date);
            state.Dictionary["State"] = PdfString.FromText("Completed");
            state.Dictionary["StateModel"] = PdfString.FromText("Review");
            Tag(comment, pdfPage, state);
        }
    }

    /// <summary>A reply: a Text annotation pointing at <paramref name="parent"/>, of no area at its corner, as it shows in the thread rather than on the page.</summary>
    private static PdfTextAnnotation Reply(PdfPage page, PdfMarkupAnnotation parent, string contents, string author, DateTimeOffset? date)
    {
        PdfRectangle at = parent.Rectangle;
        PdfTextAnnotation reply = new(new PdfRectangle(at.Left, at.Top, at.Left, at.Top), contents)
        {
            Author = author,
            ModificationDate = date,
            CreationDate = date,
            Flags = PdfAnnotationFlags.Print,
        };
        page.Annotations.Add(reply);
        reply.InReplyTo = parent;
        return reply;
    }

    private void Tag(Comment comment, PdfPage page, PdfAnnotation annotation)
    {
        tagger?.TagAnnotation(comment, page, annotation);
    }

    /// <summary>The comment that starts <paramref name="reply"/>'s thread.</summary>
    private static Comment? Thread(Comment reply)
    {
        Comment? at = reply;
        for (int depth = 0; at?.ParentComment is { } parent && depth < 64; depth++)
        {
            at = parent;
        }

        return at == reply ? null : at;
    }

    private static string Contents(Comment comment)
    {
        return string.Join("\n", comment.Story.Blocks.OfType<Paragraph>().Select(p => p.GetText().TrimEnd('\r', '\n')));
    }

    /// <summary>Rectangles on one line joined into one, as a highlight marks a line.</summary>
    private static List<LayoutRect> Lines(List<LayoutRect> rects)
    {
        List<LayoutRect> lines = [];
        foreach (LayoutRect r in rects.OrderBy(r => r.Y).ThenBy(r => r.X))
        {
            if (lines.Count > 0 && Math.Abs(lines[^1].Y - r.Y) < 0.5)
            {
                LayoutRect last = lines[^1];
                double x = Math.Min(last.X, r.X);
                double right = Math.Max(last.Right, r.Right);
                lines[^1] = new LayoutRect(x, last.Y, right - x, Math.Max(last.Height, r.Height));
            }
            else
            {
                lines.Add(r);
            }
        }

        return lines;
    }
}
