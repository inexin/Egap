using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf;

/// <summary>One verdict of a signature report (Phase 5 design Q8).</summary>
public enum PdfVerdict
{
    /// <summary>The check passed.</summary>
    Valid,

    /// <summary>The check failed: the signature is broken, tampered with or wrongly made.</summary>
    Invalid,

    /// <summary>The check could not be decided: a trust anchor, a certificate or revocation data is missing, or it is not checked yet.</summary>
    Indeterminate,

    /// <summary>The check does not apply: there is no timestamp, or nothing was added after the signature.</summary>
    NotApplicable,
}

/// <summary>
/// Options for verifying a signature (Phase 5 design Q8): the trust anchors are the caller's, there is no built-in trust
/// list, and nothing is fetched from the network unless a revocation source says so.
/// </summary>
public sealed record PdfVerifyOptions
{
    /// <summary>The default options: no trust anchors, so chains come out indeterminate.</summary>
    public static PdfVerifyOptions Default { get; } = new();

    /// <summary>The root certificates (or any certificates) to trust; a chain must end in one of them.</summary>
    public IReadOnlyList<X509Certificate2> TrustAnchors { get; init; } = [];

    /// <summary>Whether the operating system's trusted roots count as trust anchors too. The default is <see langword="false"/>.</summary>
    public bool UseSystemTrust { get; init; }

    /// <summary>More certificates to build chains from, such as intermediates the signature left out.</summary>
    public IReadOnlyList<X509Certificate2> ExtraCertificates { get; init; } = [];

    /// <summary>
    /// The time to check certificates at; now when left out. A chain that has expired since, but was valid when a trusted
    /// timestamp proves the signature existed, comes out indeterminate, as long-term validation decides it.
    /// </summary>
    public DateTimeOffset? VerificationTime { get; init; }
}

/// <summary>
/// What verifying a signature found (Phase 5 design Q8): separate verdicts for integrity, the certificate chain, the
/// timestamp, revocation and the changes made since signing, with the reasons behind each.
/// </summary>
public sealed class PdfSignatureReport
{
    internal PdfSignatureReport(PdfSignature signature)
    {
        Signature = signature;
    }

    /// <summary>The signature verified.</summary>
    public PdfSignature Signature { get; }

    /// <summary>
    /// Whether the signed bytes are what was signed: the byte range covers the file but <c>/Contents</c>, the digest matches,
    /// and the signature verifies with the signer's public key.
    /// </summary>
    public PdfVerdict Integrity { get; internal set; } = PdfVerdict.Indeterminate;

    /// <summary>Whether the signer's certificate chains to a trust anchor, valid at the verification time.</summary>
    public PdfVerdict Chain { get; internal set; } = PdfVerdict.Indeterminate;

    /// <summary>Whether the signature timestamp is valid, covers this signature and comes from a trusted authority; not applicable without one.</summary>
    public PdfVerdict Timestamp { get; internal set; } = PdfVerdict.NotApplicable;

    /// <summary>Whether the signer's certificates were not revoked, from the file's own data or a revocation source.</summary>
    public PdfVerdict Revocation { get; internal set; } = PdfVerdict.Indeterminate;

    /// <summary>
    /// Whether what was added after the signature is allowed: not applicable when nothing was, else each later revision is
    /// judged against the signature's DocMDP and FieldMDP permissions.
    /// </summary>
    public PdfVerdict ChangesSince { get; internal set; } = PdfVerdict.Indeterminate;

    /// <summary>Whether every verdict is valid or not applicable.</summary>
    public bool IsValid => new[] { Integrity, Chain, Timestamp, Revocation, ChangesSince }.All(v => v is PdfVerdict.Valid or PdfVerdict.NotApplicable);

    /// <summary>The signer's certificate, when it was found.</summary>
    public X509Certificate2? SignerCertificate { get; internal set; }

    /// <summary>The chain built from the signer's certificate to a trust anchor, signer first; empty when none was built.</summary>
    public IReadOnlyList<X509Certificate2> ChainCertificates { get; internal set; } = [];

    /// <summary>The digest algorithm, such as <c>SHA256</c>.</summary>
    public string? DigestAlgorithm { get; internal set; }

    /// <summary>The signature algorithm: <c>RSA</c>, <c>RSA-PSS</c> or <c>ECDSA</c>.</summary>
    public string? SignatureAlgorithm { get; internal set; }

    /// <summary>The time a valid timestamp proves; for a document timestamp, its own time.</summary>
    public DateTimeOffset? TimestampTime { get; internal set; }

    /// <summary>The timestamp authority's certificate, when there is a timestamp.</summary>
    public X509Certificate2? TimestampCertificate { get; internal set; }

    /// <summary>Why a verdict is not valid, one line each.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>What is valid but worth knowing, such as a weak digest algorithm (design Q4).</summary>
    public IReadOnlyList<string> Warnings => warnings;

    private readonly List<string> problems = [];
    private readonly List<string> warnings = [];

    internal void Problem(string message)
    {
        problems.Add(message);
    }

    internal void Warning(string message)
    {
        warnings.Add(message);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{Signature.Name}: integrity {Integrity}, chain {Chain}, timestamp {Timestamp}, revocation {Revocation}, changes {ChangesSince}";
    }
}
