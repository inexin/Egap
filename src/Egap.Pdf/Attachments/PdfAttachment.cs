using System.Security.Cryptography;

using Egap.Pdf.Metadata;

namespace Egap.Pdf;

/// <summary>
/// An embedded file (7.11.4): a thin view over its file specification dictionary, which holds the embedded file stream in
/// <c>/EF</c>. Two views are equal when they wrap the same file specification.
/// </summary>
public sealed class PdfAttachment : IEquatable<PdfAttachment>
{
    internal PdfAttachment(string name, PdfDictionary fileSpecification, PdfReference? reference)
    {
        Name = name;
        FileSpecification = fileSpecification;
        Reference = reference;
    }

    /// <summary>The attachment's key in the <c>/EmbeddedFiles</c> name tree.</summary>
    public string Name { get; }

    /// <summary>The file specification dictionary.</summary>
    public PdfDictionary FileSpecification { get; }

    /// <summary>The reference to the file specification, or <see langword="null"/> when it is written directly in the tree.</summary>
    public PdfReference? Reference { get; }

    /// <summary>The file name: <c>/UF</c> (Unicode), else <c>/F</c>; the tree key when both are missing.</summary>
    public string FileName => FileSpecification.GetText("UF") ?? FileSpecification.GetText("F") ?? Name;

    /// <summary>The description, <c>/Desc</c>.</summary>
    public string? Description
    {
        get => FileSpecification.GetText("Desc");
        set => FileSpecification["Desc"] = value is null ? null : PdfString.FromText(value);
    }

    /// <summary>The relationship to the document (<c>/AFRelationship</c>, PDF 2.0), such as <c>Source</c> or <c>Data</c>.</summary>
    public string? Relationship => FileSpecification.GetName("AFRelationship")?.Value;

    /// <summary>
    /// The embedded file stream: <c>/EF /UF</c>, else <c>/EF /F</c>, as the spec and qpdf prefer them; <see langword="null"/>
    /// when the file is not embedded.
    /// </summary>
    public PdfStream? Stream => FileSpecification.Get<PdfDictionary>("EF") is PdfDictionary ef ? ef.Get<PdfStream>("UF") ?? ef.Get<PdfStream>("F") : null;

    /// <summary>The MIME type, from the embedded file's <c>/Subtype</c> (such as <c>text/plain</c>).</summary>
    public string? MimeType => Stream?.Dictionary.GetName("Subtype")?.Value;

    /// <summary>The size in bytes from the embedded file's <c>/Params /Size</c>, or <see langword="null"/> when it is not given.</summary>
    public long? Size => Parameters?.GetInt("Size");

    /// <summary>The MD5 checksum of the file's bytes from <c>/Params /CheckSum</c>, or <see langword="null"/>.</summary>
    public byte[]? Checksum => Parameters?.Get<PdfString>("CheckSum")?.Bytes.ToArray();

    /// <summary>When the file was created, from <c>/Params /CreationDate</c>.</summary>
    public DateTimeOffset? CreationDate => PdfDate.ParsePdf(Parameters?.GetText("CreationDate"));

    /// <summary>When the file was last changed, from <c>/Params /ModDate</c>.</summary>
    public DateTimeOffset? ModificationDate => PdfDate.ParsePdf(Parameters?.GetText("ModDate"));

    private PdfDictionary? Parameters => Stream?.Dictionary.Get<PdfDictionary>("Params");

    /// <summary>The file's bytes, decoded; <see langword="null"/> when the file is not embedded.</summary>
    public byte[]? GetBytes()
    {
        return Stream?.GetDecodedBytes();
    }

    /// <summary>
    /// Whether the file's bytes match its <c>/Params /CheckSum</c>; <see langword="null"/> when there is no checksum or the
    /// file is not embedded.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "The spec makes /CheckSum an MD5 digest (7.11.4.2); it checks integrity, not authenticity.")]
    public bool? VerifyChecksum()
    {
        return Checksum is byte[] expected && GetBytes() is byte[] bytes ? MD5.HashData(bytes).AsSpan().SequenceEqual(expected) : null;
    }

    /// <summary>Whether both views wrap the same file specification.</summary>
    public bool Equals(PdfAttachment? other)
    {
        return other is not null && ReferenceEquals(FileSpecification, other.FileSpecification);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfAttachment);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(FileSpecification);
    }

    /// <summary>The name, for debugging.</summary>
    public override string ToString()
    {
        return Name;
    }
}
