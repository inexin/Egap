namespace Egap.Pdf.Signatures.Cms;

/// <summary>The object identifiers the CMS layer reads and writes (RFC 5652, RFC 5035, RFC 3161, RFC 4055, RFC 5758).</summary>
internal static class CmsOids
{
    public const string Data = "1.2.840.113549.1.7.1";
    public const string SignedData = "1.2.840.113549.1.7.2";
    public const string TstInfo = "1.2.840.113549.1.9.16.1.4";

    public const string ContentType = "1.2.840.113549.1.9.3";
    public const string MessageDigest = "1.2.840.113549.1.9.4";
    public const string SigningTime = "1.2.840.113549.1.9.5";
    public const string SigningCertificate = "1.2.840.113549.1.9.16.2.12";
    public const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    public const string SignatureTimestampToken = "1.2.840.113549.1.9.16.2.14";

    /// <summary>Adobe's revocation information in a signed attribute (adbe-revocationInfoArchival, ISO 32000-2, 12.8.3.3.2).</summary>
    public const string AdobeRevocationInfoArchival = "1.2.840.113583.1.1.8";

    public const string Sha1 = "1.3.14.3.2.26";
    public const string Sha224 = "2.16.840.1.101.3.4.2.4";
    public const string Sha256 = "2.16.840.1.101.3.4.2.1";
    public const string Sha384 = "2.16.840.1.101.3.4.2.2";
    public const string Sha512 = "2.16.840.1.101.3.4.2.3";

    public const string RsaEncryption = "1.2.840.113549.1.1.1";
    public const string Sha1WithRsa = "1.2.840.113549.1.1.5";
    public const string Sha224WithRsa = "1.2.840.113549.1.1.14";
    public const string Sha256WithRsa = "1.2.840.113549.1.1.11";
    public const string Sha384WithRsa = "1.2.840.113549.1.1.12";
    public const string Sha512WithRsa = "1.2.840.113549.1.1.13";
    public const string RsaPss = "1.2.840.113549.1.1.10";
    public const string Mgf1 = "1.2.840.113549.1.1.8";

    public const string EcPublicKey = "1.2.840.10045.2.1";
    public const string EcdsaWithSha1 = "1.2.840.10045.4.1";
    public const string EcdsaWithSha224 = "1.2.840.10045.4.3.1";
    public const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
    public const string EcdsaWithSha384 = "1.2.840.10045.4.3.3";
    public const string EcdsaWithSha512 = "1.2.840.10045.4.3.4";
}
