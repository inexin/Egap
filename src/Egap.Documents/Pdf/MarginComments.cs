using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// Comments printed in the margin, as Word's "show markup" print does: each page keeps its paper size, its content drawn at
/// 76 % on the left, and a pane on the right holds a balloon per comment, its replies in balloons under it, in the
/// order of their anchors, each as near its anchor's line as the others let it, with a line to its anchor. Balloons that
/// do not fit on their page go to a Comments list at the end of the document, each with its page number.
/// </summary>
internal sealed class MarginComments
{
    /// <summary>The share of the page's size the content keeps, as Word shrinks it; the rest of the width is the pane.</summary>
    public const double ContentShare = 0.76;

    private const double Gap = 6;
    private const double Padding = 4;
    private const double ReplyGap = 2;
    private const double FontSize = 8;

    /// <summary>One colour per author, in the order authors first comment: the balloon's and the shading's fill, and the line's.</summary>
    private static readonly (Rgba8 Fill, Rgba8 Line)[] Palette =
    [
        (new(214, 233, 252, 255), new(45, 110, 190, 255)),
        (new(236, 245, 196, 255), new(110, 150, 30, 255)),
        (new(250, 214, 250, 255), new(160, 60, 160, 255)),
        (new(255, 226, 196, 255), new(200, 110, 30, 255)),
        (new(210, 240, 232, 255), new(40, 140, 120, 255)),
        (new(240, 220, 214, 255), new(160, 80, 60, 255)),
    ];

    private readonly DocumentLayout layout;
    private readonly LayoutOptions? layoutOptions;
    private readonly PdfTagger? tagger;
    private readonly Dictionary<PageLayout, List<Thread>> threads = [];
    private readonly Dictionary<string, int> authors = new(StringComparer.Ordinal);
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
                if (!threads.TryGetValue(placed.Page, out List<Thread>? list))
                {
                    list = [];
                    threads[placed.Page] = list;
                }

                // The line starts below the first commented character, as LibreOffice and Word point at a comment.
                LayoutRect first = placed.Rects[0];
                list.Add(new Thread(comment, first.X, first.Bottom, placed.Rects, ColourOf(comment.Author)));
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

    /// <summary>
    /// Draws a page's content scaled to the left of the pane, the commented words shaded behind it, then its balloons: each as
    /// near its anchor's line as the ones above and below let it, its replies in balloons of their own under it, with a line
    /// from an arrow under the comment's first character, along under its text line and then straight to the balloon.
    /// </summary>
    public void DrawPage(PageLayout page, PdfCanvas canvas, LayoutDrawObserver? observer)
    {
        List<Thread> list = threads.TryGetValue(page, out List<Thread>? found) ? found : [];
        canvas.Save();
        canvas.Translate(0, (float)(page.Height * (1 - ContentShare) / 2));
        canvas.Scale((float)ContentShare);
        foreach (Thread thread in list)
        {
            Decoration(canvas, () =>
            {
                foreach (LayoutRect line in thread.Lines.Where(l => l.Width > 0))
                {
                    canvas.FillRect(new RectF((float)line.X, (float)line.Y, (float)line.Width, (float)line.Height), thread.Colour.Fill);
                }
            });
        }

        page.Draw(canvas, observer);
        canvas.Restore();
        if (list.Count == 0)
        {
            return;
        }

        // The pane beside the shrunk page, as tall as the page drawn, as Word prints it.
        double offset = page.Height * (1 - ContentShare) / 2;
        double paneTop = offset;
        double paneBottom = offset + (page.Height * ContentShare);

        double paneX = (page.Width * ContentShare) + Gap;
        double paneWidth = (page.Width * (1 - ContentShare)) - (2 * Gap);

        // One group per thread: the comment's balloon, then a balloon of its own for each reply, kept together.
        List<(Thread Thread, List<(Comment Comment, PageLayout Text, double Height)> Boxes, double Height, double Want)> groups = [];
        foreach (Thread thread in list.OrderBy(t => t.AnchorY).ThenBy(t => t.AnchorX))
        {
            List<(Comment, PageLayout, double)> boxes = [];
            foreach (Comment comment in (IEnumerable<Comment>)[thread.Comment, .. Replies(thread.Comment)])
            {
                (PageLayout text, double height) = Balloon(comment, paneWidth - (2 * Padding));
                boxes.Add((comment, text, height + (2 * Padding)));
            }

            double total = boxes.Sum(b => b.Item3) + (ReplyGap * (boxes.Count - 1));
            double first = boxes[0].Item3;
            groups.Add((thread, boxes, total, Map(page, thread.AnchorX, thread.AnchorY).Y - (first / 2)));
        }

        // As many as fit in the pane stay; the rest of the page's comments go to the list, in their order.
        double room = paneBottom - paneTop - (2 * Gap);
        int kept = 0;
        double used = 0;
        while (kept < groups.Count && used + groups[kept].Height + (kept > 0 ? Gap : 0) <= room)
        {
            used += groups[kept].Height + (kept > 0 ? Gap : 0);
            kept++;
        }

        foreach ((Thread thread, _, _, _) in groups.Skip(kept))
        {
            overflow.Add((page.Number, thread.Comment));
        }

        double[] tops = Stack([.. groups.Take(kept).Select(g => (g.Height, g.Want))], paneTop + Gap, paneBottom - Gap);
        Stroke dashed = new(0.6f) { Dash = new DashPattern([2.5f, 1.5f], 0) };

        // The lines break at the right edge of the text, so they cross the page's margin to their balloons instead of
        // running down the pane's edge together.
        float textRight = (float)(((page.Width - (page.Section.PageSetup.MarginRight ?? 72)) * ContentShare) + 4);
        for (int i = 0; i < kept; i++)
        {
            (Thread thread, List<(Comment Comment, PageLayout Text, double Height)> boxes, _, _) = groups[i];
            Vector2 anchor = Map(page, thread.AnchorX, thread.AnchorY);
            Rgba8 line = thread.Colour.Line;
            float firstTop = (float)tops[i];
            Decoration(canvas, () =>
            {
                // An arrow pointing up at the comment's first character, then the line along under the text to the text's
                // right edge, where it breaks and runs straight to the balloon's first line, as LibreOffice draws it.
                canvas.FillPath(Egami.Drawing.Path.Polygon([anchor, anchor + new Vector2(-2.5f, 3.5f), anchor + new Vector2(2.5f, 3.5f)], close: true), line);
                Vector2 bend = new(textRight, anchor.Y + 0.5f);
                canvas.DrawLine(anchor + new Vector2(0, 0.5f), bend, dashed, line);
                canvas.DrawLine(bend, new Vector2((float)paneX, firstTop + 7), dashed, line);
            });

            double at = tops[i];
            foreach ((Comment comment, PageLayout text, double height) in boxes)
            {
                (Rgba8 fill, Rgba8 edge) = comment == thread.Comment ? thread.Colour : ColourOf(comment.Author);
                RectF box = new((float)paneX, (float)at, (float)paneWidth, (float)height);
                Begin(canvas, comment);
                canvas.FillRect(box, fill);
                canvas.DrawLine(new Vector2(box.Left, box.Top), new Vector2(box.Left, box.Bottom), new Stroke(1.5f), edge);
                canvas.Save();
                canvas.Translate((float)(paneX + Padding), (float)(at + Padding));
                text.Draw(canvas);
                canvas.Restore();
                if (tagger is not null)
                {
                    canvas.EndTag();
                }

                at += height + ReplyGap;
            }
        }
    }

    /// <summary>
    /// The tops of boxes of the given heights, each as near its wanted top as it can be without overlapping the others,
    /// within <paramref name="top"/> and <paramref name="bottom"/>: pushed down below the box above, then pulled up where
    /// the boxes would run past the bottom. The boxes are known to fit together.
    /// </summary>
    internal static double[] Stack(IReadOnlyList<(double Height, double Want)> boxes, double top, double bottom)
    {
        double[] tops = new double[boxes.Count];
        double next = top;
        for (int i = 0; i < boxes.Count; i++)
        {
            tops[i] = Math.Max(boxes[i].Want, next);
            next = tops[i] + boxes[i].Height + Gap;
        }

        double limit = bottom;
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            tops[i] = Math.Max(top, Math.Min(tops[i], limit - boxes[i].Height));
            limit = tops[i] - Gap;
        }

        return tops;
    }

    /// <summary>Draws what is only a mark (shading, lines, arrows) as an artifact in tagged output.</summary>
    private void Decoration(PdfCanvas canvas, Action draw)
    {
        if (tagger is not null)
        {
            canvas.BeginArtifact();
        }

        draw();
        if (tagger is not null)
        {
            canvas.EndTag();
        }
    }

    private (Rgba8 Fill, Rgba8 Line) ColourOf(string author)
    {
        if (!authors.TryGetValue(author, out int index))
        {
            index = authors.Count;
            authors[author] = index;
        }

        return Palette[index % Palette.Length];
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

    /// <summary>One comment's balloon text laid out at <paramref name="width"/>: its page and the height the text takes.</summary>
    private (PageLayout Page, double Height) Balloon(Comment comment, double width)
    {
        Document text = new();
        Section section = text.AddSection();
        section.PageSetup.PageWidth = Math.Max(24, width);
        section.PageSetup.PageHeight = 4000;
        section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 0;
        Write(section.Body.AddParagraph(), comment, section.Body);
        PageLayout page = text.Layout(layoutOptions).Pages[0];
        double height = page.Boxes.Count == 0 ? 0 : page.Boxes.Max(b => b.Bounds.Bottom) - page.Boxes.Min(b => b.Bounds.Y);
        return (page, Math.Max(height, FontSize * 1.2));
    }

    /// <summary>A comment's author and text in <paramref name="first"/>, then each reply on a paragraph of its own.</summary>
    private static void AddThread(Paragraph first, Comment comment, Body body)
    {
        Write(first, comment, body);
        foreach (Comment reply in Replies(comment))
        {
            Paragraph paragraph = body.AddParagraph();
            paragraph.Format.LeftIndent = 6;
            paragraph.Format.SpaceBefore = 2;
            Write(paragraph, reply, body);
        }
    }

    /// <summary>A comment's text, then its author and date in small grey type under it, as LibreOffice and Word show them.</summary>
    private static void Write(Paragraph paragraph, Comment comment, Body body)
    {
        paragraph.Format.SpaceAfter = 0;
        Run text = paragraph.AddRun(CommentExport.Contents(comment).Replace('\n', ' '));
        text.Format.Size = FontSize;
        Paragraph by = body.AddParagraph();
        by.Format.LeftIndent = paragraph.Format.LeftIndent;
        by.Format.SpaceAfter = 0;
        string when = comment.Date is { } date ? "  " + date.ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";
        Run author = by.AddRun(comment.Author + (comment.Done ? " (resolved)" : "") + when);
        author.Format.Size = FontSize - 1.5;
        author.Format.Color = DocumentColor.FromHex("666666");
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

    /// <summary>A thread on its page: the comment, where its range starts (the foot of its first line), its lines and its colour.</summary>
    private sealed record Thread(Comment Comment, double AnchorX, double AnchorY, List<LayoutRect> Lines, (Rgba8 Fill, Rgba8 Line) Colour);

    private void Begin(PdfCanvas canvas, Comment comment)
    {
        if (tagger is not null)
        {
            canvas.BeginTag(tagger.BalloonElement(comment));
        }
    }
}
