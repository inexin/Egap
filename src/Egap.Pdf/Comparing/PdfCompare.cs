using System.Globalization;

using Egami;
using Egami.Pixels;

using Egap.Pdf.Comparing;

namespace Egap.Pdf;

/// <summary>How <see cref="PdfCompare.Run"/> compares two documents (Phase 7 design Q12 and Q13).</summary>
public sealed record PdfCompareOptions
{
    /// <summary>The default options: both passes, pages rendered at 100 dpi.</summary>
    public static PdfCompareOptions Default { get; } = new();

    /// <summary>Gets whether the text pass runs. Defaults to true.</summary>
    public bool Text { get; init; } = true;

    /// <summary>Gets whether the visual pass runs. Defaults to true.</summary>
    public bool Visual { get; init; } = true;

    /// <summary>Gets the resolution matching pages are rendered at for the visual pass. Defaults to 100.</summary>
    public double Dpi { get; init; } = 100;

    /// <summary>
    /// Gets how far a pixel's channels may differ, 0 to 255, before it counts as changed, which hides anti-aliasing.
    /// Defaults to 48.
    /// </summary>
    public int Tolerance { get; init; } = 48;

    /// <summary>Gets how many changed pixels a cell of 8 by 8 must hold to count, against noise. Defaults to 3.</summary>
    public int MinChangedPixels { get; init; } = 3;

    /// <summary>Gets whether each visual change keeps a raster of the changed pixels. Defaults to false.</summary>
    public bool DifferenceImages { get; init; }
}

/// <summary>How a run of words changed.</summary>
public enum PdfTextChangeKind
{
    /// <summary>Words only the new document has.</summary>
    Inserted,

    /// <summary>Words only the old document has.</summary>
    Deleted,

    /// <summary>Words of the old document replaced by others.</summary>
    Replaced,

    /// <summary>The same words, at another place in the text.</summary>
    Moved,
}

/// <summary>A word compare read, with the page (1-based) and box (default user space) it is on.</summary>
/// <param name="Page">The page, 1-based.</param>
/// <param name="Text">The word.</param>
/// <param name="Box">Its box in the page's default user space.</param>
public sealed record PdfComparedWord(int Page, string Text, PdfRectangle Box);

/// <summary>One change in the text (Phase 7 design Q12).</summary>
/// <param name="Kind">The kind of change.</param>
/// <param name="OldWords">The words removed or moved away, in the old document.</param>
/// <param name="NewWords">The words added or moved here, in the new document.</param>
/// <param name="Anchor">
/// For a deletion, the new document's word just after where the old words were, or the last word before them at the end;
/// null when the new document has no text.
/// </param>
public sealed record PdfTextChange(PdfTextChangeKind Kind, IReadOnlyList<PdfComparedWord> OldWords, IReadOnlyList<PdfComparedWord> NewWords, PdfComparedWord? Anchor)
{
    /// <summary>The old words as one string.</summary>
    public string OldText => string.Join(' ', OldWords.Select(w => w.Text));

    /// <summary>The new words as one string.</summary>
    public string NewText => string.Join(' ', NewWords.Select(w => w.Text));

    /// <summary>The first old page the change touches, 1-based; null when it has no old words.</summary>
    public int? OldPage => OldWords.Count > 0 ? OldWords[0].Page : null;

    /// <summary>The first new page the change touches, 1-based; null when it has no new words.</summary>
    public int? NewPage => NewWords.Count > 0 ? NewWords[0].Page : Anchor?.Page;
}

/// <summary>Which old page a new page matches; a page only one side has is added or removed.</summary>
/// <param name="OldPage">The old page, 1-based; null for a page added in the new document.</param>
/// <param name="NewPage">The new page, 1-based; null for a page removed from the old document.</param>
public sealed record PdfPageMatch(int? OldPage, int? NewPage);

/// <summary>Where two matching pages render differently.</summary>
public sealed class PdfVisualChange : IDisposable
{
    internal PdfVisualChange(int oldPage, int newPage, IReadOnlyList<PdfRectangle> boxes, Raster<Rgba8>? difference)
    {
        OldPage = oldPage;
        NewPage = newPage;
        Boxes = boxes;
        Difference = difference;
    }

    /// <summary>The old page, 1-based.</summary>
    public int OldPage { get; }

    /// <summary>The new page, 1-based.</summary>
    public int NewPage { get; }

    /// <summary>The areas that changed, in the new page's default user space.</summary>
    public IReadOnlyList<PdfRectangle> Boxes { get; }

    /// <summary>The changed pixels in red over a faded new page, when <see cref="PdfCompareOptions.DifferenceImages"/> asked for it.</summary>
    public Raster<Rgba8>? Difference { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Difference?.Dispose();
    }
}

/// <summary>
/// What changed between two documents (Phase 7 design Q12 and Q13): text changes found by a word-level diff over the whole
/// documents, so text that only moved to another page is no change; how pages match; and where matching pages render
/// differently. <see cref="WriteMarkup(string)"/> writes a copy of the new document with the changes marked.
/// </summary>
public sealed class PdfComparison : IDisposable
{
    private readonly PdfDocument newDocument;

    internal PdfComparison(PdfDocument newDocument, IReadOnlyList<PdfTextChange> text, IReadOnlyList<PdfPageMatch> pages, IReadOnlyList<PdfVisualChange> visual)
    {
        this.newDocument = newDocument;
        TextChanges = text;
        Pages = pages;
        VisualChanges = visual;
    }

    /// <summary>The text changes, in the new document's order.</summary>
    public IReadOnlyList<PdfTextChange> TextChanges { get; }

    /// <summary>How the pages match, in the new document's order with removed pages where they were.</summary>
    public IReadOnlyList<PdfPageMatch> Pages { get; }

    /// <summary>The matching pages that render differently.</summary>
    public IReadOnlyList<PdfVisualChange> VisualChanges { get; }

    /// <summary>Whether anything changed: text, pages added or removed, or pixels.</summary>
    public bool HasChanges => TextChanges.Count > 0 || VisualChanges.Count > 0 || Pages.Any(p => p.OldPage is null || p.NewPage is null);

    /// <summary>
    /// Writes a copy of the new document with the changes marked (Phase 7 design Q13), as Acrobat's Compare does: highlights
    /// over inserted, replaced and moved words, the old text in their pop-up notes, carets where words were deleted, and
    /// rectangles around areas that only render differently. The new document is not changed.
    /// </summary>
    public void WriteMarkup(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream file = new(path, FileMode.Create, FileAccess.Write);
        WriteMarkup(file);
    }

    /// <summary>Writes the marked-up copy to a stream. See <see cref="WriteMarkup(string)"/>.</summary>
    public void WriteMarkup(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Markup.Write(newDocument, this, output);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (PdfVisualChange change in VisualChanges)
        {
            change.Dispose();
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{TextChanges.Count} text changes, {VisualChanges.Count} pages render differently, {Pages.Count(p => p.OldPage is null)} pages added, {Pages.Count(p => p.NewPage is null)} removed");
    }
}

/// <summary>
/// Compares two PDF documents (Phase 7 design 7.4): a text pass that diffs the words of both documents in reading order and
/// matches pages by the words they share, and a visual pass that renders each matching pair of pages and boxes the pixels
/// that differ. Both documents must stay open while the comparison is used, as markup reads the new one.
/// </summary>
/// <example>
/// <code>
/// using PdfComparison changes = PdfCompare.Run(oldDoc, newDoc);
/// foreach (PdfTextChange t in changes.TextChanges)
/// {
///     Console.WriteLine($"{t.Kind}: '{t.OldText}' -> '{t.NewText}' on page {t.NewPage}");
/// }
///
/// changes.WriteMarkup("changes.pdf");
/// </code>
/// </example>
public static class PdfCompare
{
    /// <summary>Compares <paramref name="oldDocument"/> with <paramref name="newDocument"/>.</summary>
    /// <exception cref="ArgumentNullException">A document is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside what it can mean.</exception>
    public static PdfComparison Run(PdfDocument oldDocument, PdfDocument newDocument, PdfCompareOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldDocument);
        ArgumentNullException.ThrowIfNull(newDocument);
        options ??= PdfCompareOptions.Default;
        if (!(options.Dpi > 0 && options.Dpi <= 600) || options.Tolerance is < 0 or > 255 || options.MinChangedPixels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Dpi must be above 0 and at most 600, Tolerance 0 to 255 and MinChangedPixels at least 1.");
        }

        List<PdfComparedWord> oldWords = TextCompare.Words(oldDocument), newWords = TextCompare.Words(newDocument);
        List<(int Old, int New)> equal = [];
        List<PdfTextChange> text = TextCompare.Changes(oldWords, newWords, equal);
        IReadOnlyList<PdfPageMatch> pages = TextCompare.Match(oldDocument.Pages.Count, newDocument.Pages.Count, equal, oldWords, newWords);
        List<PdfVisualChange> visual = options.Visual ? VisualCompare.Run(oldDocument, newDocument, pages, options) : [];
        return new PdfComparison(newDocument, options.Text ? text : [], pages, visual);
    }
}
