using System.Globalization;

namespace Egap.Pdf.Comparing;

/// <summary>
/// The marked-up copy of compare (Phase 7 design Q13, Build Plan task 16): the new document saved to memory and opened as a
/// copy, then given highlight annotations over inserted (blue), replaced (yellow) and moved (green) words, one per change and
/// page, whose notes say what was there before; caret annotations where words were deleted; and red rectangles around areas
/// that only render differently. Every annotation gets its appearance, so viewers that do not draw annotations themselves
/// show them too.
/// </summary>
internal static class Markup
{
    private static readonly PdfRgbColor Inserted = new(0.45, 0.7, 1), Replaced = new(1, 0.9, 0.2), Moved = new(0.4, 0.85, 0.4), Removed = new(0.9, 0.1, 0.1);

    public static void Write(PdfDocument newDocument, PdfComparison comparison, Stream output)
    {
        using MemoryStream copy = new();
        newDocument.Save(copy, new PdfSaveOptions { AllowInvalidatingSignatures = true });
        using PdfDocument marked = PdfDocument.Open(copy.ToArray());
        List<(int Page, PdfRectangle Box)> covered = [];
        foreach (PdfTextChange change in comparison.TextChanges)
        {
            if (change.Kind == PdfTextChangeKind.Deleted)
            {
                Caret(marked, change);
                continue;
            }

            foreach (IGrouping<int, PdfComparedWord> words in change.NewWords.GroupBy(w => w.Page))
            {
                PdfPage page = marked.Pages[words.Key - 1];
                List<PdfPoint> quads = [];
                foreach (PdfComparedWord word in words)
                {
                    PdfRectangle b = word.Box;
                    quads.AddRange([new(b.Left, b.Top), new(b.Right, b.Top), new(b.Left, b.Bottom), new(b.Right, b.Bottom)]);
                    covered.Add((words.Key, b));
                }

                PdfHighlightAnnotation highlight = page.Annotations.Add(new PdfHighlightAnnotation(quads));
                highlight.Color = change.Kind switch
                {
                    PdfTextChangeKind.Inserted => Inserted,
                    PdfTextChangeKind.Replaced => Replaced,
                    _ => Moved,
                };
                highlight.Subject = change.Kind.ToString();
                highlight.Contents = change.Kind switch
                {
                    PdfTextChangeKind.Inserted => "Inserted",
                    PdfTextChangeKind.Replaced => "Replaced: " + change.OldText,
                    _ => string.Create(CultureInfo.InvariantCulture, $"Moved from page {change.OldPage}"),
                };
                highlight.UpdateAppearance();
            }
        }

        foreach (PdfVisualChange change in comparison.VisualChanges)
        {
            PdfPage page = marked.Pages[change.NewPage - 1];
            foreach (PdfRectangle box in change.Boxes)
            {
                if (covered.Any(c => c.Page == change.NewPage && Overlaps(c.Box, box)))
                {
                    continue;
                }

                PdfSquareAnnotation square = page.Annotations.Add(new PdfSquareAnnotation(Grow(box, 2)));
                square.Color = Removed;
                square.Subject = "Changed";
                square.Contents = "Renders differently";
                square.UpdateAppearance();
            }
        }

        foreach (PdfPageMatch added in comparison.Pages.Where(p => p.OldPage is null && p.NewPage is not null))
        {
            PdfPage page = marked.Pages[added.NewPage!.Value - 1];
            PdfRectangle crop = page.CropBox.Normalize();
            PdfTextAnnotation note = page.Annotations.Add(new PdfTextAnnotation(new PdfRectangle(crop.Left + 8, crop.Top - 32, crop.Left + 32, crop.Top - 8)));
            note.Contents = "Page added";
            note.UpdateAppearance();
        }

        marked.Save(output);
    }

    private static void Caret(PdfDocument marked, PdfTextChange change)
    {
        if (change.Anchor is not PdfComparedWord anchor)
        {
            return;
        }

        PdfPage page = marked.Pages[anchor.Page - 1];
        double size = Math.Max(6, anchor.Box.Height);
        PdfRectangle at = new(anchor.Box.Left - (size / 2), anchor.Box.Bottom - (size / 3), anchor.Box.Left + (size / 2), anchor.Box.Bottom + (size * 2 / 3));
        PdfCaretAnnotation caret = page.Annotations.Add(new PdfCaretAnnotation(at));
        caret.Color = Removed;
        caret.Subject = "Deleted";
        caret.Contents = "Deleted: " + change.OldText;
        caret.UpdateAppearance();
    }

    private static bool Overlaps(PdfRectangle a, PdfRectangle b) => a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;

    private static PdfRectangle Grow(PdfRectangle r, double by) => new(r.Left - by, r.Bottom - by, r.Right + by, r.Top + by);
}
