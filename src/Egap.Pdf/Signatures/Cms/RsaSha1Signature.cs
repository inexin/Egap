using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf.Signatures.Cms;

/// <summary>
/// The old <c>adbe.x509.rsa_sha1</c> format (ISO 32000-1, 12.8.3.2; deprecated in PDF 2.0): <c>/Contents</c> is a DER OCTET
/// STRING holding a raw PKCS #1 v1.5 signature over the SHA-1 digest of the byte range, and the certificates are in the
/// signature dictionary's <c>/Cert</c>. Read and verified with a warning, never written (Phase 5 design Q4).
/// </summary>
internal static class RsaSha1Signature
{
    /// <summary>The raw signature in <paramref name="contents"/> (padding after it is ignored).</summary>
    /// <exception cref="PdfException">The contents are not an OCTET STRING.</exception>
    public static byte[] Read(ReadOnlyMemory<byte> contents)
    {
        try
        {
            return new AsnReader(contents, AsnEncodingRules.BER).ReadOctetString();
        }
        catch (AsnContentException e)
        {
            throw new PdfException($"The adbe.x509.rsa_sha1 signature is not an OCTET STRING: {e.Message}");
        }
    }

    /// <summary>The certificates of <c>/Cert</c>: one string, or an array whose first entry is the signer's.</summary>
    public static List<X509Certificate2> Certificates(PdfDictionary dictionary)
    {
        List<X509Certificate2> certificates = [];
        IEnumerable<PdfString> strings = dictionary.Get("Cert") switch
        {
            PdfString one => [one],
            PdfArray array => Enumerable.Range(0, array.Count).Select(array.Get<PdfString>).OfType<PdfString>(),
            _ => [],
        };
        foreach (PdfString value in strings)
        {
            try
            {
                certificates.Add(X509CertificateLoader.LoadCertificate(value.Bytes));
            }
            catch (CryptographicException)
            {
                // A certificate .NET cannot read cannot be the signer's.
            }
        }

        return certificates;
    }
}
