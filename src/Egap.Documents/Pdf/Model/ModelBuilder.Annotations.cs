using System.Globalization;

using Egami;
using Egami.Pixels;

using Egap.Pdf;


using Picture = Egap.Documents.Picture;

namespace Egap.Documents.Pdf;

/// <summary>A page that is only a picture of a page: no text, one image over most of it.</summary>
internal sealed record ScannedPage(PageImage Image);

internal sealed partial class ModelBuilder
{
    /// <summary>Markup annotations that become comments when they carry text.</summary>
    private static readonly HashSet<string> NoteKinds = ["Text", "Highlight", "Underline", "StrikeOut", "Squiggly", "Caret", "Square", "Circle", "Polygon", "PolyLine", "Ink", "Stamp"];

    /// <summary>
    /// The seam for an OCR engine (hub step 5.7, design Q9): given a scanned page, the text lines it recognises in page
    /// space, or null. Until 5.7 registers an engine there is none, and a scanned page stays a picture with a warning.
    /// </summary>
    internal static Func<PdfPage, IReadOnlyList<PdfOcrLine>?>? Ocr { get; set; }

    private readonly List<(int Page, PageBox Box, Paragraph Paragraph)> written = [];
    private readonly List<(Hyperlink Link, int Page, double? Top)> internalLinks = [];

    /// <summary>The image a page is a scan of, when it has no text of its own and one image covers most of it.</summary>
    private static PageImage? Scan(PageDraft page)
    {
        if (page.Content.Glyphs.Any(g => !g.IsArtifact && !g.IsSpace))
        {
            return null;
        }

        double area = page.Content.Width * page.Content.Height;
        return page.Content.Images.Where(i => i.Bounds.Intersect(page.Content.Bounds).Area >= area * 0.6).MaxBy(i => i.Bounds.Area);
    }

    /// <summary>
    /// A scanned page (design Q9): run through the registered OCR engine when there is one, else kept as a picture across the
    /// text area with a warning, since Egap does no OCR of its own.
    /// </summary>
    private void WriteScan(BlockContainer body, ScannedPage scan, PlacedItem placed)
    {
        int index = placed.Page.Content.Index;
        PdfPage pdfPage = pdf.Pages[index];
        _ = Ocr?.Invoke(pdfPage);
        options.OnWarning?.Invoke(new PdfToDocumentWarning(PdfToDocumentWarningKind.ScannedPage, index,
            $"Page {index + 1} is a scanned picture with no text; it is kept as a picture."));

        PageBox box = scan.Image.Bounds.Intersect(placed.Page.Content.Bounds);
        ImageData image = Stored(PdfLayoutAnalyzer.Match(scan.Image, PdfLayoutAnalyzer.Placed(pdfPage), placed.Page.Content)) ?? Rendered(pdfPage, box);
        double scale = Math.Min(1, placed.Frame.Width / Math.Max(box.Width, 1));
        Paragraph paragraph = new(document) { Style = styles.Normal.Style };
        paragraph.Format.SpaceBefore = 0;
        paragraph.Format.SpaceAfter = 0;
        paragraph.Format.FirstLineIndent = 0;
        paragraph.Format.LeftIndent = 0;
        paragraph.Format.Alignment = ParagraphAlignment.Center;
        paragraph.Inlines.Add(new Picture(document, image, Math.Round(box.Width * scale, 2), Math.Round(box.Height * scale, 2)));
        body.Blocks.Add(paragraph);
        TakeFloats(paragraph);
        written.Add((index, box, paragraph));
        last = null;
        lastTable = null;
    }

    private ImageData Rendered(PdfPage page, PageBox box)
    {
        double scale = options.FigureResolution / 72;
        using Raster<Rgba8> raster = page.Render(new PdfRenderOptions
        {
            Dpi = options.FigureResolution,
            Region = new Egami.Drawing.RectF((float)(box.Left * scale), (float)(box.Top * scale), (float)(box.Width * scale), (float)(box.Height * scale)),
            Annotations = PdfAnnotationRendering.None,
        });
        return Png(raster);
    }

    /// <summary>
    /// What annotations carry (design Q9), once the body is written: links inside the document point at a bookmark on the
    /// paragraph at their target; notes and markup with text become comments on the paragraph they sit on (replies as
    /// replies); filled form fields show their values as text where the field is.
    /// </summary>
    private void Annotations()
    {
        ResolveLinks();
        Dictionary<PdfDictionary, Comment> comments = new(ReferenceEqualityComparer.Instance);
        foreach (PageDraft page in pages)
        {
            int index = page.Content.Index;
            List<(PageBox Box, Paragraph Paragraph)> onPage = [.. written.Where(w => w.Page == index).Select(w => (w.Box, w.Paragraph))];
            if (onPage.Count == 0)
            {
                continue;
            }

            // Notes first, replies after the note they answer.
            foreach (PageAnnotation note in page.Content.Annotations.Where(a => NoteKinds.Contains(a.Subtype) && a.Contents is { Length: > 0 })
                .OrderBy(a => a.Annotation.Dictionary.Get("IRT") is null ? 0 : 1))
            {
                Comment comment;
                string author = Clean(note.Author ?? "");
                if (note.Annotation.Dictionary.Get<PdfDictionary>("IRT") is PdfDictionary parent && comments.TryGetValue(parent, out Comment? to))
                {
                    comment = to.AddReply(author, Clean(note.Contents!), note.Annotation.ModificationDate);
                }
                else
                {
                    comment = new Comment(document, author, note.Annotation.ModificationDate);
                    comment.AddParagraph(Clean(note.Contents!));
                    Paragraph target = Nearest(onPage, note.Box);
                    target.Inlines.Insert(0, new CommentRangeStart(document, comment));
                    target.Inlines.Add(new CommentRangeEnd(document, comment));
                    target.Inlines.Add(comment);
                }

                comments[note.Annotation.Dictionary] = comment;
            }

            foreach (PageAnnotation widget in page.Content.Annotations.Where(a => a.Subtype == "Widget"))
            {
                if (FieldText(widget) is not { Length: > 0 } value || !Placeable(widget.Box))
                {
                    continue;
                }

                Shape frame = Frame(widget.Box, WrapMode.InFrontOfText);
                Paragraph text = Framed(styles.Normal);
                text.AddRun(Clean(value));
                frame.TextBox!.Blocks.Add(text);
                Nearest(onPage, widget.Box).Inlines.Insert(0, frame);
            }
        }
    }

    /// <summary>The paragraph an annotation belongs to: one it overlaps, else the last above it, else the first on the page.</summary>
    private static Paragraph Nearest(List<(PageBox Box, Paragraph Paragraph)> onPage, PageBox box)
    {
        foreach ((PageBox b, Paragraph p) in onPage)
        {
            if (!b.Intersect(box).IsEmpty)
            {
                return p;
            }
        }

        return onPage.Where(p => p.Box.Top <= box.Top).Select(p => p.Paragraph).LastOrDefault() ?? onPage[0].Paragraph;
    }

    /// <summary>A form field's value as text: the text, the choice, a ticked or empty box for a check box or radio button.</summary>
    private static string? FieldText(PageAnnotation widget)
    {
        if (widget.Annotation is not PdfWidgetAnnotation annotation || annotation.Field is not PdfField field)
        {
            return null;
        }

        return field switch
        {
            PdfTextField text => text.IsPassword ? null : text.Value,
            PdfCheckBoxField box => box.IsChecked ? "\u2612" : "\u2610",
            PdfRadioButtonField radio => annotation.AppearanceState is string state && state != "Off" && state == radio.Value ? "\u25C9" : "\u25CB",
            PdfComboBoxField combo => combo.Value,
            PdfListBoxField list => list.Values.Count > 0 ? string.Join(", ", list.Values) : null,
            _ => null,
        };
    }

    /// <summary>Gives each link inside the document a bookmark to go to: on the first paragraph at or below its target on the target page.</summary>
    private void ResolveLinks()
    {
        Dictionary<Paragraph, string> marks = new(ReferenceEqualityComparer.Instance);
        foreach ((Hyperlink link, int page, double? top) in internalLinks)
        {
            List<(int Page, PageBox Box, Paragraph Paragraph)> candidates = [.. written.Where(w => w.Page == page)];
            if (candidates.Count == 0)
            {
                continue;
            }

            Paragraph target = (top is double y ? candidates.FirstOrDefault(c => c.Box.Bottom >= y - 2).Paragraph : null) ?? candidates[0].Paragraph;
            if (!marks.TryGetValue(target, out string? name))
            {
                name = "_Page" + (page + 1).ToString(CultureInfo.InvariantCulture) + "_" + marks.Count.ToString(CultureInfo.InvariantCulture);
                marks[target] = name;
                target.Inlines.Insert(0, new BookmarkEnd(document, name));
                target.Inlines.Insert(0, new BookmarkStart(document, name));
            }

            link.Anchor = name;
        }
    }
}
