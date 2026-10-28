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
    /// near its anchor's line as the ones above and below let it, with a line from an arrow under the comment's first
    /// character, along under its text line and then straight to the balloon.
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

        double paneX = (page.Width * ContentShare) + (2 * Gap);
        double paneWidth = (page.Width * (1 - ContentShare)) - (3 * Gap);
        List<(Thread Thread, PageLayout Text, double Height, double Want)> balloons = [];
        foreach (Thread thread in list.OrderBy(t => t.AnchorY).ThenBy(t => t.AnchorX))
        {
            (PageLayout text, double height) = Balloon(thread.Comment, paneWidth - (2 * Padding));
            double boxHeight = height + (2 * Padding);
            balloons.Add((thread, text, boxHeight, Map(page, thread.AnchorX, thread.AnchorY).Y - (boxHeight / 2)));
        }

        // As many as fit in the pane's height stay; the rest of the page's comments go to the list, in their order.
        double room = page.Height - (2 * Gap);
        int kept = 0;
        double used = 0;
        while (kept < balloons.Count && used + balloons[kept].Height + (kept > 0 ? Gap : 0) <= room)
        {
            used += balloons[kept].Height + (kept > 0 ? Gap : 0);
            kept++;
        }

        foreach ((Thread thread, _, _, _) in balloons.Skip(kept))
        {
            overflow.Add((page.Number, thread.Comment));
        }

        double[] tops = Stack([.. balloons.Take(kept).Select(b => (b.Height, b.Want))], Gap, page.Height - Gap);
        Stroke dashed = new(0.6f) { Dash = new DashPattern([2.5f, 1.5f], 0) };
        for (int i = 0; i < kept; i++)
        {
            (Thread thread, PageLayout text, double height, _) = balloons[i];
            RectF box = new((float)paneX, (float)tops[i], (float)paneWidth, (float)height);
            Vector2 anchor = Map(page, thread.AnchorX, thread.AnchorY);
            Rgba8 line = thread.Colour.Line;
            Decoration(canvas, () =>
            {
                // An arrow pointing up at the comment's first character, then the line along under the text to past the
                // page's content, where it breaks and runs straight to the balloon's first line, as LibreOffice draws it.
                canvas.FillPath(Egami.Drawing.Path.Polygon([anchor, anchor + new Vector2(-2.5f, 3.5f), anchor + new Vector2(2.5f, 3.5f)], close: true), line);
                Vector2 bend = new((float)(page.Width * ContentShare), anchor.Y + 0.5f);
                canvas.DrawLine(anchor + new Vector2(0, 0.5f), bend, dashed, line);
                canvas.DrawLine(bend, new Vector2(box.Left, box.Top + 7), dashed, line);
            });

            Begin(canvas, thread.Comment);
            canvas.FillRect(box, thread.Colour.Fill);
            canvas.DrawLine(new Vector2(box.Left, box.Top), new Vector2(box.Left, box.Bottom), new Stroke(1.5f), line);
            canvas.Save();
            canvas.Translate((float)(paneX + Padding), (float)(tops[i] + Padding));
            text.Draw(canvas);
            canvas.Restore();
            if (tagger is not null)
            {
                canvas.EndTag();
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
