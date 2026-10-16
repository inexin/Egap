namespace Egap.Pdf;

/// <summary>
/// Which seed values a signer must follow (<c>/Ff</c> of a seed value dictionary, ISO 32000-2, Table 237); a value whose bit
/// is clear is only a suggestion.
/// </summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The spec calls /Ff the seed value flags.")]
public enum PdfSeedValueFlags
{
    /// <summary>Every value is a suggestion.</summary>
    None = 0,

    /// <summary><see cref="PdfSeedValue.Filter"/> is required.</summary>
    Filter = 1,

    /// <summary>One of <see cref="PdfSeedValue.SubFilters"/> is required.</summary>
    SubFilter = 2,

    /// <summary>The minimum handler version (<c>/V</c>) is required.</summary>
    Version = 4,

    /// <summary>One of <see cref="PdfSeedValue.Reasons"/> is required.</summary>
    Reasons = 8,

    /// <summary>Only the legal attestations listed are allowed.</summary>
    LegalAttestation = 16,

    /// <summary><see cref="PdfSeedValue.AddRevocationInfo"/> is required.</summary>
    AddRevocationInfo = 32,

    /// <summary>One of <see cref="PdfSeedValue.DigestMethods"/> is required.</summary>
    DigestMethod = 64,

    /// <summary>The <c>/LockDocument</c> value is required (PDF 2.0).</summary>
    LockDocument = 128,

    /// <summary>The <c>/AppearanceFilter</c> value is required (PDF 2.0).</summary>
    AppearanceFilter = 256,
}

/// <summary>
/// Which certificate seed values a signer must follow (<c>/Ff</c> of a certificate seed value dictionary, ISO 32000-2,
/// Table 238).
/// </summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The spec calls /Ff the certificate seed value flags.")]
public enum PdfCertificateSeedFlags
{
    /// <summary>Every value is a suggestion.</summary>
    None = 0,

    /// <summary>The signer's certificate must be one of <see cref="PdfCertificateSeed.Subjects"/>.</summary>
    Subject = 1,

    /// <summary>The signer's certificate must be issued by one of <see cref="PdfCertificateSeed.Issuers"/>.</summary>
    Issuer = 2,

    /// <summary>The signer's certificate must carry one of the policy <see cref="PdfCertificateSeed.Policies"/>.</summary>
    Policy = 4,

    /// <summary>The signer's certificate must have one of the subject names in <c>/SubjectDN</c>.</summary>
    SubjectDN = 8,

    /// <summary>The signer's certificate must have the key usage in <see cref="PdfCertificateSeed.KeyUsage"/>.</summary>
    KeyUsage = 32,

    /// <summary>The <see cref="PdfCertificateSeed.Url"/> must be used.</summary>
    Url = 64,
}

/// <summary>
/// A signature field's seed values (<c>/SV</c>, ISO 32000-2, 12.7.5.5): constraints on how the field may be signed, honoured
/// when signing (Phase 5 design Q7).
/// </summary>
public sealed class PdfSeedValue
{
    internal PdfSeedValue(PdfDictionary dictionary)
    {
        Dictionary = dictionary;
        Flags = (PdfSeedValueFlags)(int)(dictionary.GetInt("Ff") ?? 0);
        Filter = dictionary.GetName("Filter")?.Value;
        SubFilters = Names(dictionary.Get<PdfArray>("SubFilter"));
        DigestMethods = Names(dictionary.Get<PdfArray>("DigestMethod"));
        Reasons = Texts(dictionary.Get<PdfArray>("Reasons"));
        LegalAttestations = Texts(dictionary.Get<PdfArray>("LegalAttestation"));
        Certification = Certifications.FromNumber(dictionary.Get<PdfDictionary>("MDP")?.GetInt("P") is long p and >= 1 and <= 3 ? p : null);
        TimestampUrl = dictionary.Get<PdfDictionary>("TimeStamp")?.GetText("URL");
        TimestampRequired = dictionary.Get<PdfDictionary>("TimeStamp")?.GetInt("Ff") == 1;
        AddRevocationInfo = dictionary.GetBool("AddRevInfo") == true;
        Certificate = dictionary.Get<PdfDictionary>("Cert") is { } cert ? new PdfCertificateSeed(cert) : null;
    }

    /// <summary>The seed value dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Which values are required rather than suggested.</summary>
    public PdfSeedValueFlags Flags { get; }

    /// <summary>The signature handler (<c>/Filter</c>), such as <c>Adobe.PPKLite</c>.</summary>
    public string? Filter { get; }

    /// <summary>The acceptable SubFilters, in order of preference.</summary>
    public IReadOnlyList<string> SubFilters { get; }

    /// <summary>The acceptable digest methods, such as <c>SHA256</c>.</summary>
    public IReadOnlyList<string> DigestMethods { get; }

    /// <summary>The acceptable reasons for signing; a single <c>"."</c> means no reason may be given.</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>The legal attestations a certification may give.</summary>
    public IReadOnlyList<string> LegalAttestations { get; }

    /// <summary>The certification the signature must be, from <c>/MDP</c>; <see langword="null"/> when none is asked for.</summary>
    public PdfCertification? Certification { get; }

    /// <summary>The timestamp server to use (<c>/TimeStamp /URL</c>).</summary>
    public string? TimestampUrl { get; }

    /// <summary>Whether a timestamp is required (<c>/TimeStamp /Ff</c> 1).</summary>
    public bool TimestampRequired { get; }

    /// <summary>Whether revocation information must be embedded (<c>/AddRevInfo</c>).</summary>
    public bool AddRevocationInfo { get; }

    /// <summary>The certificate constraints (<c>/Cert</c>), or <see langword="null"/>.</summary>
    public PdfCertificateSeed? Certificate { get; }

    internal static IReadOnlyList<string> Names(PdfArray? array)
    {
        List<string> names = [];
        for (int i = 0; i < (array?.Count ?? 0); i++)
        {
            if (array!.Get<PdfName>(i) is { } name)
            {
                names.Add(name.Value);
            }
        }

        return names;
    }

    internal static IReadOnlyList<string> Texts(PdfArray? array)
    {
        List<string> texts = [];
        for (int i = 0; i < (array?.Count ?? 0); i++)
        {
            if (array!.Get<PdfString>(i) is { } text)
            {
                texts.Add(text.ToText());
            }
        }

        return texts;
    }
}

/// <summary>Certificate constraints of a seed value (<c>/Cert</c>, ISO 32000-2, Table 238).</summary>
public sealed class PdfCertificateSeed
{
    internal PdfCertificateSeed(PdfDictionary dictionary)
    {
        Dictionary = dictionary;
        Flags = (PdfCertificateSeedFlags)(int)(dictionary.GetInt("Ff") ?? 0);
        Subjects = Bytes(dictionary.Get<PdfArray>("Subject"));
        Issuers = Bytes(dictionary.Get<PdfArray>("Issuer"));
        Policies = [.. Bytes(dictionary.Get<PdfArray>("OID")).Select(b => System.Text.Encoding.ASCII.GetString(b))];
        KeyUsage = PdfSeedValue.Texts(dictionary.Get<PdfArray>("KeyUsage"));
        Url = dictionary.GetText("URL");
    }

    /// <summary>The certificate seed value dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Which constraints are required rather than suggested.</summary>
    public PdfCertificateSeedFlags Flags { get; }

    /// <summary>The certificates (DER) one of which must sign.</summary>
    public IReadOnlyList<byte[]> Subjects { get; }

    /// <summary>The issuer certificates (DER) one of which must have issued the signer's certificate.</summary>
    public IReadOnlyList<byte[]> Issuers { get; }

    /// <summary>The certificate policy OIDs, one of which the signer's certificate must carry.</summary>
    public IReadOnlyList<string> Policies { get; }

    /// <summary>
    /// The key usage patterns: each a string of nine characters, one per key usage bit from digitalSignature, <c>0</c> for must
    /// not be set, <c>1</c> for must be set, <c>X</c> for either.
    /// </summary>
    public IReadOnlyList<string> KeyUsage { get; }

    /// <summary>Where a signer can get a suitable certificate.</summary>
    public string? Url { get; }

    private static List<byte[]> Bytes(PdfArray? array)
    {
        List<byte[]> items = [];
        for (int i = 0; i < (array?.Count ?? 0); i++)
        {
            if (array!.Get<PdfString>(i) is { } value)
            {
                items.Add(value.Bytes.ToArray());
            }
        }

        return items;
    }
}
