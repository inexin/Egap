namespace Egap.Pdf;

/// <summary>
/// What the owner of an encrypted file allows (ISO 32000-2, Table 22). The flags are advisory: anyone who can open the file
/// can read everything in it, so honouring them is the caller's job (design Q5). The values are the bits of <c>/P</c>.
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>Nothing is allowed.</summary>
    None = 0,

    /// <summary>Printing (at low quality when <see cref="PrintHighQuality"/> is not set).</summary>
    Print = 1 << 2,

    /// <summary>Changing the document other than by the operations below.</summary>
    Modify = 1 << 3,

    /// <summary>Copying or otherwise extracting text and graphics.</summary>
    CopyContent = 1 << 4,

    /// <summary>Adding or changing annotations and filling in form fields.</summary>
    Annotate = 1 << 5,

    /// <summary>Filling in form fields, even when <see cref="Annotate"/> is not set.</summary>
    FillForms = 1 << 8,

    /// <summary>Extracting text and graphics for accessibility.</summary>
    ExtractForAccessibility = 1 << 9,

    /// <summary>Assembling the document: inserting, rotating or deleting pages, bookmarks and thumbnails.</summary>
    Assemble = 1 << 10,

    /// <summary>Printing at full quality.</summary>
    PrintHighQuality = 1 << 11,

    /// <summary>Everything: the permissions of a file that is not encrypted.</summary>
    All = Print | Modify | CopyContent | Annotate | FillForms | ExtractForAccessibility | Assemble | PrintHighQuality,
}
