using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// Comments printed in the margin, as Word's "show markup" print does: each page keeps its paper size, its content drawn a
/// third smaller on the left, and a pane on the right holds a balloon per comment thread, in the order of their anchors,
/// each beside its anchor's line or pushed down below the one above, with a line to its anchor. Balloons that do not fit
/// on their page go to a Comments list at the end of the document, each with its page number.
/// </summary>
internal sealed class MarginComments
{
    /// <summary>The share of the page width the content keeps; the rest is the pane.</summary>
    public const double ContentShare = 2.0 / 3;

    private const double Gap = 6;
    private const double Padding = 4;
    private const double FontSize = 8;
    private static readonly Rgba8 Border = new(204, 51, 51, 255);
    private static readonly Rgba8 Fill = new(253, 236, 236, 255);

    private readonly DocumentLayout layout;
    private readonly LayoutOptions? layoutOptions;
    private readonly PdfTagger? tagger;
    private readonly Dictionary<PageLayout, List<(Comment Comment, double AnchorX, double AnchorY)>> threads = [];
    private readonly List<(string Page, Comment Comment)> overflow = [];

    public MarginComments(DocumentLayout layout, LayoutOptions? layoutOptions, PdfTagger? tagger, bool includeResolved)
    {
        this.layout = layout;
        this.layoutOptions = layoutOptions;
        this.tagger = tagger;
        CommentExport places = new(layout, [], null, includeResolved);
        foreach (Comment comment in layout.Document.Descendants<Comment>())
        {
            if (comment.ParentId is not null || comment.Ancestors().OfType<CommentStory>().FirstOrDefault() is not null || (!includeResolved && comment.Done))
            {
                continue;
            }

            if (places.Place(comment) is { } placed)
            {
                // The connector starts at the foot of the range's last line, where it ends.
                LayoutRect last = placed.Rects[^1];
                if (!threads.TryGetValue(placed.Page, out List<(Comment, double, double)>? list))
                {
                    list = [];
                    threads[placed.Page] = list;
                }

                list.Add((comment, last.Right, last.Bottom - 1));
            }
        }
    }

    /// <summary>Where a point on the laid-out page lands on the printed page.</summary>
    public static Vector2 Map(PageLayout page, double x, double y)
    {
        double offset = page.Height * (1 - ContentShare) / 2;
        return new Vector2((float)(x * ContentShare), (float)(offset + (y * ContentShare)));
    }

    /// <summary>A rectangle on the laid-out page as it lands on the printed page.</summary>
    public static LayoutRect Map(PageLayout page, LayoutRect r)
    {
        Vector2 at = Map(page, r.X, r.Y);
        return new LayoutRect(at.X, at.Y, r.Width * ContentShare, r.Height * ContentShare);
    }

    /// <summary>Draws a page's content scaled to the left of the pane, then its balloons.</summary>
    public void DrawPage(PageLayout page, PdfCanvas canvas, LayoutDrawObserver? observer)
    {
        canvas.Save();
        canvas.Translate(0, (float)(page.Height * (1 - ContentShare) / 2));
        canvas.Scale((float)ContentShare);
        page.Draw(canvas, observer);
        canvas.Restore();

        if (!threads.TryGetValue(page, out List<(Comment Comment, double AnchorX, double AnchorY)>? list))
        {
            return;
        }

        double paneX = (page.Width * ContentShare) + Gap;
        double paneWidth = (page.Width * (1 - ContentShare)) - (2 * Gap);
        double bottom = page.Height - Gap;
        double next = Gap;
        bool full = false;
        foreach ((Comment comment, double anchorX, double anchorY) in list.OrderBy(t => t.AnchorY).ThenBy(t => t.AnchorX))
        {
            Vector2 anchor = Map(page, anchorX, anchorY);
            (PageLayout balloon, double height) = Balloon(comment, paneWidth - (2 * Padding));
            double top = Math.Max(anchor.Y - 10, next);
            if (full || top + height + (2 * Padding) > bottom)
            {
                // Once the pane is full, the rest of the page's comments follow in the list, in their order.
                full = true;
                overflow.Add((page.Number, comment));
                continue;
            }

            RectF box = new((float)paneX, (float)top, (float)paneWidth, (float)(height + (2 * Padding)));
            Begin(canvas, comment);
            canvas.FillRect(box, Fill);
            Stroke stroke = new(0.75f);
            canvas.DrawLine(new Vector2(box.Left, box.Top), new Vector2(box.Right, box.Top), stroke, Border);
            canvas.DrawLine(new Vector2(box.Right, box.Top), new Vector2(box.Right, box.Bottom), stroke, Border);
            canvas.DrawLine(new Vector2(box.Right, box.Bottom), new Vector2(box.Left, box.Bottom), stroke, Border);
            canvas.DrawLine(new Vector2(box.Left, box.Bottom), new Vector2(box.Left, box.Top), stroke, Border);
            // The line runs along the anchor's line to the pane, then to the balloon, as Word draws it.
            Vector2 edge = new((float)(paneX - (Gap / 2)), anchor.Y);
            canvas.DrawLine(anchor, edge, new Stroke(0.5f), Border);
            canvas.DrawLine(edge, new Vector2(box.Left, box.Top + 6), new Stroke(0.5f), Border);
            canvas.Save();
            canvas.Translate((float)(paneX + Padding), (float)(top + Padding));
            balloon.Draw(canvas);
            canvas.Restore();
            if (tagger is not null)
            {
                canvas.EndTag();
            }

            next = box.Bottom + Gap;
        }
    }

    /// <summary>Adds pages listing the comments that did not fit beside their page, after the document's last page.</summary>
    public void AddOverflow(PdfDocument pdf)
    {
        if (overflow.Count == 0)
        {
            return;
        }

        PageLayout lastPage = layout.Pages[^1];
        Document list = new();
        Section section = list.AddSection();
        section.PageSetup.PageWidth = lastPage.Width;
        section.PageSetup.PageHeight = lastPage.Height;
        section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 72;
        Paragraph heading = section.Body.AddParagraph("Comments");
        heading.Format.OutlineLevel = 0;
        heading.Format.SpaceAfter = 12;
        heading.Runs.Single().Format.Bold = true;
        heading.Runs.Single().Format.Size = 14;
        foreach ((string page, Comment comment) in overflow)
        {
            Paragraph entry = section.Body.AddParagraph();
            entry.Format.SpaceBefore = 6;
            Run where = entry.AddRun("Page " + page + "  ");
            where.Format.Bold = true;
            where.Format.Size = FontSize;
            AddThread(entry, comment, section.Body);
        }

        foreach (PageLayout page in list.Layout(layoutOptions).Pages)
        {
            PdfPage pdfPage = pdf.Pages.AddBlank(new PdfRectangle(0, 0, page.Width, page.Height));
            using PdfCanvas canvas = new(pdfPage);
            if (tagger is not null)
            {
                canvas.BeginTag(tagger.CommentsElement());
            }

            page.Draw(canvas);
            if (tagger is not null)
            {
                canvas.EndTag();
            }
        }
    }

    /// <summary>A thread's balloon laid out at <paramref name="width"/>: its first page and the height its text takes.</summary>
    private (PageLayout Page, double Height) Balloon(Comment comment, double width)
    {
        Document text = new();
        Section section = text.AddSection();
        section.PageSetup.PageWidth = Math.Max(24, width);
        section.PageSetup.PageHeight = 4000;
        section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 0;
        AddThread(section.Body.AddParagraph(), comment, section.Body);
        PageLayout page = text.Layout(layoutOptions).Pages[0];
        double height = page.Boxes.Count == 0 ? 0 : page.Boxes.Max(b => b.Bounds.Bottom) - page.Boxes.Min(b => b.Bounds.Y);
        return (page, Math.Max(height, FontSize * 1.2));
    }

    /// <summary>A comment's author and text in <paramref name="first"/>, then each reply on a paragraph of its own.</summary>
    private static void AddThread(Paragraph first, Comment comment, Body body)
    {
        Write(first, comment);
        foreach (Comment reply in Replies(comment))
        {
            Paragraph paragraph = body.AddParagraph();
            paragraph.Format.LeftIndent = 6;
            paragraph.Format.SpaceBefore = 2;
            Write(paragraph, reply);
        }
    }

    private static void Write(Paragraph paragraph, Comment comment)
    {
        paragraph.Format.SpaceAfter = 0;
        Run author = paragraph.AddRun(comment.Author + (comment.Done ? " (resolved)" : "") + ": ");
        author.Format.Bold = true;
        author.Format.Size = FontSize;
        Run body = paragraph.AddRun(CommentExport.Contents(comment).Replace('\n', ' '));
        body.Format.Size = FontSize;
    }

    /// <summary>Every reply in the thread, in document order.</summary>
    private static IEnumerable<Comment> Replies(Comment first)
    {
        foreach (Comment reply in first.Document.Descendants<Comment>().Where(c => c.ParentId is not null))
        {
            for (Comment? at = reply.ParentComment; at is not null; at = at.ParentComment)
            {
                if (at == first)
                {
                    yield return reply;
                    break;
                }
            }
        }
    }

    private void Begin(PdfCanvas canvas, Comment comment)
    {
        if (tagger is not null)
        {
            canvas.BeginTag(tagger.BalloonElement(comment));
        }
    }
}
