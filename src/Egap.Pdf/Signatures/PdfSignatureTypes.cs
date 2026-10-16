namespace Egap.Pdf;

/// <summary>What a signature is (ISO 32000-2, 12.8).</summary>
public enum PdfSignatureKind
{
    /// <summary>An approval signature: a signer approves the document as it stands.</summary>
    Approval,

    /// <summary>A certification (author) signature, with DocMDP permissions for later changes (12.8.2.2); at most one, the first.</summary>
    Certification,

    /// <summary>A document timestamp (12.8.5): an RFC 3161 timestamp over the whole revision, as PAdES B-LTA adds.</summary>
    DocumentTimestamp,
}

/// <summary>
/// The changes a certification signature allows after it (DocMDP <c>/P</c>, ISO 32000-2, 12.8.2.2); the values are the
/// numbers in the file.
/// </summary>
public enum PdfCertification
{
    /// <summary>No changes; any change invalidates the signature.</summary>
    NoChanges = 1,

    /// <summary>Filling in forms, instantiating page templates and signing.</summary>
    FormFillingAndSigning = 2,

    /// <summary>Form filling and signing, plus creating, deleting and changing annotations.</summary>
    AnnotationsFormFillingAndSigning = 3,
}

/// <summary>What is wrong with a signature's <c>/ByteRange</c> (Phase 5 design, 5.1); <see cref="None"/> when nothing is.</summary>
[Flags]
public enum PdfByteRangeIssues
{
    /// <summary>The byte range is well formed.</summary>
    None = 0,

    /// <summary>The <c>/ByteRange</c> is missing, has an odd count, or holds something other than offsets and lengths.</summary>
    Malformed = 1,

    /// <summary>The first range does not start at the beginning of the file.</summary>
    NotFromStart = 2,

    /// <summary>The ranges overlap or are out of order.</summary>
    Overlapping = 4,

    /// <summary>A range runs past the end of the file.</summary>
    OutsideFile = 8,

    /// <summary>
    /// The range does not leave out exactly the signature's <c>/Contents</c> string: it has other than two ranges, or the gap
    /// between them is not the hex string holding the signature's contents.
    /// </summary>
    ContentsNotExcluded = 16,

    /// <summary>The range does not end where a revision of the file ends, so it covers part of an update.</summary>
    NotAtRevisionEnd = 32,
}

/// <summary>One range of signed bytes: <paramref name="Length"/> bytes from <paramref name="Start"/>.</summary>
/// <param name="Start">The first byte's offset in the file.</param>
/// <param name="Length">The number of bytes.</param>
public readonly record struct PdfByteRange(long Start, long Length)
{
    /// <summary>The offset just past the range.</summary>
    public long End => Start + Length;
}

/// <summary>Which fields a field lock covers (<c>/Action</c>, ISO 32000-2, Table 236).</summary>
public enum PdfFieldLockAction
{
    /// <summary>Every field of the form.</summary>
    All,

    /// <summary>Only the fields listed.</summary>
    Include,

    /// <summary>Every field except those listed.</summary>
    Exclude,
}

/// <summary>
/// A field lock (ISO 32000-2, 12.7.5.5 and 12.8.2.4): the fields a signature makes read-only, from a signature field's
/// <c>/Lock</c> before signing or from a signature's FieldMDP reference after.
/// </summary>
/// <param name="Action">Which fields it covers.</param>
/// <param name="Fields">The fully qualified names of the fields listed, for <see cref="PdfFieldLockAction.Include"/> and <see cref="PdfFieldLockAction.Exclude"/>.</param>
/// <param name="Certification">The DocMDP permissions applied when the field is signed (<c>/P</c>, PDF 2.0), or <see langword="null"/>.</param>
public sealed record PdfFieldLock(PdfFieldLockAction Action, IReadOnlyList<string> Fields, PdfCertification? Certification = null)
{
    /// <summary>Whether the lock covers the field with this fully qualified name; a listed field also covers its descendants.</summary>
    public bool Covers(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        bool listed = Fields.Any(f => fullName == f || fullName.StartsWith(f + ".", StringComparison.Ordinal));
        return Action switch
        {
            PdfFieldLockAction.All => true,
            PdfFieldLockAction.Include => listed,
            _ => !listed,
        };
    }

    /// <summary>Reads a lock dictionary (<c>/Lock</c>, or a FieldMDP <c>/TransformParams</c>); null when it names no known action.</summary>
    internal static PdfFieldLock? Read(PdfDictionary? dictionary)
    {
        PdfFieldLockAction? action = dictionary?.GetName("Action")?.Value switch
        {
            "All" => PdfFieldLockAction.All,
            "Include" => PdfFieldLockAction.Include,
            "Exclude" => PdfFieldLockAction.Exclude,
            _ => null,
        };
        if (action is null)
        {
            return null;
        }

        List<string> fields = [];
        if (dictionary!.Get<PdfArray>("Fields") is { } array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array.Get<PdfString>(i) is { } name)
                {
                    fields.Add(name.ToText());
                }
            }
        }

        return new PdfFieldLock(action.Value, fields, Certifications.FromNumber(dictionary.GetInt("P")));
    }
}

/// <summary>Reading DocMDP permission numbers.</summary>
internal static class Certifications
{
    /// <summary>The permissions for a <c>/P</c> value; 1 to 3 as written, anything else as 2 (12.8.2.2's default).</summary>
    public static PdfCertification? FromNumber(long? p)
    {
        return p switch
        {
            null => null,
            1 => PdfCertification.NoChanges,
            3 => PdfCertification.AnnotationsFormFillingAndSigning,
            _ => PdfCertification.FormFillingAndSigning,
        };
    }
}
