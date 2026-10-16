namespace Egap.Pdf;

/// <summary>
/// One revision of a file (Phase 5 design, 5.1): the original file or one incremental update after it (ISO 32000-2, 7.5.6).
/// Each signature covers one revision; verification opens that revision and compares it with the later ones.
/// </summary>
/// <example>
/// <code>
/// foreach (PdfRevision revision in pdf.Revisions)
/// {
///     using PdfDocument before = revision.Open();
///     Console.WriteLine($"Revision {revision.Index}: {revision.Length} bytes, {before.Pages.Count} pages");
/// }
/// </code>
/// </example>
public sealed class PdfRevision
{
    private readonly PdfDocument document;

    internal PdfRevision(PdfDocument document, int index, long xrefOffset, long eofEnd, long length, bool isLatest)
    {
        this.document = document;
        Index = index;
        XrefOffset = xrefOffset;
        EofEnd = eofEnd;
        Length = length;
        IsLatest = isLatest;
    }

    /// <summary>The revision's number: 0 for the original file, then one more for each update.</summary>
    public int Index { get; }

    /// <summary>The length of the file as this revision left it: up to its <c>%%EOF</c> and the end-of-line after it.</summary>
    public long Length { get; }

    /// <summary>Where the revision's newest cross-reference section starts.</summary>
    public long XrefOffset { get; }

    /// <summary>Whether this is the file's last revision, the one the document shows.</summary>
    public bool IsLatest { get; }

    /// <summary>Where the revision's <c>%%EOF</c> marker ends, before any end-of-line.</summary>
    internal long EofEnd { get; }

    /// <summary>
    /// Opens the file as this revision left it, as a read-only document: it cannot be saved. It reads from this document's
    /// file, which must stay open while it is used.
    /// </summary>
    /// <param name="options">The read options, or <see langword="null"/> for this document's own.</param>
    public PdfDocument Open(PdfReadOptions? options = null)
    {
        return document.OpenRevision(Length, options ?? document.Options);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Revision {Index} ({Length} bytes)";
    }
}
