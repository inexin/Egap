using System.Security.Cryptography;

namespace Egap.Pdf;

/// <summary>The PAdES baseline level a signature is made at (ETSI EN 319 142-1; Phase 5 design Q4).</summary>
public enum PadesLevel
{
    /// <summary>B-B: the signature with the signer's certificate and the PAdES signed attributes.</summary>
    BaselineB,

    /// <summary>B-T: B-B with an RFC 3161 timestamp on the signature, proving it existed at that time.</summary>
    BaselineT,

    /// <summary>B-LT: B-T with the certificates and revocation data a verifier needs, in the Document Security Store.</summary>
    BaselineLT,

    /// <summary>B-LTA: B-LT with a document timestamp over it all, so it can be renewed before its algorithms age.</summary>
    BaselineLTA,
}

/// <summary>How the signature value is encoded (<c>/SubFilter</c>, Phase 5 design Q4).</summary>
public enum PdfSignatureFormat
{
    /// <summary><c>ETSI.CAdES.detached</c>: PAdES, what EU rules ask for and the default.</summary>
    Pades,

    /// <summary><c>adbe.pkcs7.detached</c>: the older CMS format every PDF reader knows; PAdES levels other than B-B still apply.</summary>
    Pkcs7,
}

/// <summary>How a document is signed (Phase 5 design, 5.1).</summary>
public sealed record PdfSignOptions
{
    /// <summary>
    /// Who signs. Required by <see cref="PdfDocument.SignAsync"/>; <see cref="PdfDocument.PrepareSignatureAsync"/> may leave
    /// it out, and uses its certificate only to size the reserved space.
    /// </summary>
    public PdfSigner? Signer { get; init; }

    /// <summary>The PAdES level. The default is <see cref="PadesLevel.BaselineB"/>.</summary>
    public PadesLevel Level { get; init; } = PadesLevel.BaselineB;

    /// <summary>The encoding. The default is <see cref="PdfSignatureFormat.Pades"/>.</summary>
    public PdfSignatureFormat Format { get; init; } = PdfSignatureFormat.Pades;

    /// <summary>
    /// The timestamp authority for the signature timestamp; required from <see cref="PadesLevel.BaselineT"/>, and for B-LTA's
    /// document timestamp.
    /// </summary>
    public PdfTimestampClient? Timestamp { get; init; }

    /// <summary>The digest algorithm: SHA-256 (the default), SHA-384 or SHA-512.</summary>
    public HashAlgorithmName DigestAlgorithm { get; init; } = HashAlgorithmName.SHA256;

    /// <summary>For an RSA key, whether to sign with RSA-PSS instead of PKCS #1 v1.5. The default is <see langword="false"/>.</summary>
    public bool UseRsaPss { get; init; }

    /// <summary>The signer's name for the dictionary's <c>/Name</c>; the certificate's subject name when left out.</summary>
    public string? SignerName { get; init; }

    /// <summary>The reason for signing (<c>/Reason</c>).</summary>
    public string? Reason { get; init; }

    /// <summary>Where the document is signed (<c>/Location</c>).</summary>
    public string? Location { get; init; }

    /// <summary>How to reach the signer (<c>/ContactInfo</c>).</summary>
    public string? ContactInfo { get; init; }

    /// <summary>The signing time claimed in <c>/M</c>; now when left out. Only a timestamp proves a time.</summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>
    /// The bytes reserved for the CMS in <c>/Contents</c>. Left out, it is sized from the signer's certificates, the key and,
    /// from B-T, room for the timestamp; a signature that does not fit throws, naming the size it needed.
    /// </summary>
    public int? ReservedSize { get; init; }
}
