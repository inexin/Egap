using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf;

/// <summary>
/// Who signs and with which key (Phase 5 design Q3): a certificate with its private key, or any RSA or ECDsa object with the
/// certificate it belongs to. A subclass of RSA or ECDsa reaches a smart card, an HSM or a cloud key service, since Egap only
/// ever asks it to sign the signed attributes. A signature made somewhere else entirely goes through
/// <see cref="PdfDocument.PrepareSignatureAsync"/> instead.
/// </summary>
/// <example>
/// <code>
/// using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile("me.pfx", password);
/// PdfSigner signer = PdfSigner.FromCertificate(certificate, chain: intermediates);
/// </code>
/// </example>
public sealed class PdfSigner
{
    private readonly AsymmetricAlgorithm? key;

    private PdfSigner(X509Certificate2 certificate, AsymmetricAlgorithm? key, IEnumerable<X509Certificate2>? chain)
    {
        Certificate = certificate;
        this.key = key;
        Chain = [.. (chain ?? []).Where(c => c.Thumbprint != certificate.Thumbprint)];
    }

    /// <summary>The signer's certificate.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>
    /// The certificates between the signer's and a trust anchor, written into the signature so a verifier can build the
    /// chain; the root may be left out.
    /// </summary>
    public IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>A signer for a certificate that holds its RSA or ECDSA private key, such as one loaded from a PFX file.</summary>
    /// <param name="certificate">The certificate, with its private key.</param>
    /// <param name="chain">The intermediate certificates, and the root if wanted, to embed.</param>
    /// <exception cref="ArgumentException">The certificate has no RSA or ECDSA private key.</exception>
    public static PdfSigner FromCertificate(X509Certificate2 certificate, IEnumerable<X509Certificate2>? chain = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey || (certificate.GetKeyAlgorithm() != Signatures.Cms.CmsOids.RsaEncryption && certificate.GetKeyAlgorithm() != Signatures.Cms.CmsOids.EcPublicKey))
        {
            throw new ArgumentException("The certificate must hold an RSA or ECDSA private key.", nameof(certificate));
        }

        return new PdfSigner(certificate, null, chain);
    }

    /// <summary>
    /// A signer for a key held apart from its certificate: an <see cref="RSA"/> or <see cref="ECDsa"/> object, or a subclass
    /// that signs on a device or in a key service. The key stays the caller's to dispose.
    /// </summary>
    /// <param name="key">The private key, RSA or ECDSA.</param>
    /// <param name="certificate">The certificate for the key's public half.</param>
    /// <param name="chain">The intermediate certificates, and the root if wanted, to embed.</param>
    /// <exception cref="ArgumentException">The key is neither RSA nor ECDSA.</exception>
    public static PdfSigner FromKey(AsymmetricAlgorithm key, X509Certificate2 certificate, IEnumerable<X509Certificate2>? chain = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(certificate);
        if (key is not (RSA or ECDsa))
        {
            throw new ArgumentException("The key must be an RSA or ECDsa object.", nameof(key));
        }

        return new PdfSigner(certificate, key, chain);
    }

    /// <summary>The size of a signature value this key makes, in bytes, for sizing the space reserved for the CMS.</summary>
    internal int SignatureSize()
    {
        bool ec;
        int bits;
        if (key is not null)
        {
            ec = key is ECDsa;
            bits = key.KeySize;
        }
        else
        {
            using RSA? rsa = Certificate.GetRSAPublicKey();
            using ECDsa? ecdsa = rsa is null ? Certificate.GetECDsaPublicKey() : null;
            ec = ecdsa is not null;
            bits = rsa?.KeySize ?? ecdsa?.KeySize ?? 4096;
        }

        // An ECDSA signature is a DER SEQUENCE of two integers, each up to the key's size and a sign byte.
        int bytes = (bits + 7) / 8;
        return ec ? (2 * bytes) + 9 : bytes;
    }

    /// <summary>Signs the signed attributes with the key, returning the signature and its algorithm identifier.</summary>
    internal (byte[] Signature, Signatures.Cms.CmsAlgorithm Algorithm) Sign(HashAlgorithmName digest, bool pss, ReadOnlySpan<byte> signedAttributes)
    {
        if (key is not null)
        {
            return Signatures.Cms.CmsAlgorithms.Sign(key, digest, pss, signedAttributes);
        }

        using AsymmetricAlgorithm own = (AsymmetricAlgorithm?)Certificate.GetRSAPrivateKey() ?? Certificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("The certificate's private key cannot be used.");
        return Signatures.Cms.CmsAlgorithms.Sign(own, digest, pss, signedAttributes);
    }
}
