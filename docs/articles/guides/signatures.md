# Signatures

Egap signs with .NET's own cryptography: PAdES baseline levels B-B to B-LTA, with approval and certification signatures,
timestamps and visible appearances. Signing always writes to a new stream, as an incremental update.

```csharp
using System.Security.Cryptography.X509Certificates;
using Egap.Pdf;

using HttpClient http = new();
using PdfDocument pdf = PdfDocument.Open("in.pdf");
using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile("me.pfx", "password");
await using (FileStream signed = File.Create("signed.pdf"))
{
    await pdf.SignAsync("Signature1", new PdfSignOptions             // PAdES, incremental, written to a new stream
    {
        Signer = PdfSigner.FromCertificate(certificate),
        Level = PadesLevel.BaselineT,
        Timestamp = new PdfTimestampClient(new Uri("https://tsa.example.com"), http),  // Egap opens no connection itself
    }, signed);
}
```

Egap never opens a network connection on its own. Timestamps and revocation data come through the `HttpClient` you pass.
Keys that live elsewhere (a smart card, an HSM, a cloud service) sign through any `RSA` or `ECDsa` object, or through deferred
signing.

## Verifying

```csharp
using System.Security.Cryptography.X509Certificates;
using Egap.Pdf;

X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile("root.cer");
using PdfDocument check = PdfDocument.Open("signed.pdf");
foreach (PdfSignature signature in check.Signatures)
{
    PdfSignatureReport report = signature.Verify(new PdfVerifyOptions { TrustAnchors = [root] });
    Console.WriteLine($"{signature.Name}: {report.Integrity} {report.Chain} {report.Timestamp} {report.ChangesSince}");
}
```

The report gives a verdict for integrity, the certificate chain, revocation and the timestamp, and lists every change made
after signing, each one rated allowed, suspicious or disallowed. Trust anchors always come from you.

## Smart cards and HSMs (PKCS#11)

A key on a smart card, a USB token or an HSM is reached through its PKCS#11 library. Egap does not load native libraries,
so the PKCS#11 calls come from a library you choose, such as [Pkcs11Interop](https://github.com/Pkcs11Interop/Pkcs11Interop)
(Apache-2.0). Wrap the token's signing call in an `RSA` (or `ECDsa`) whose `SignHash` asks the token, and pass it to
`PdfSigner.FromKey` with the certificate read from the token. The private key never leaves the device.

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Egap.Pdf;

// signOnToken stands for the PKCS#11 call: with Pkcs11Interop, session.Sign(new Mechanism(CKM.CKM_RSA_PKCS), key, digestInfo).
Func<byte[], byte[]> signOnToken = digestInfo => throw new NotImplementedException("call the token here");
X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(File.ReadAllBytes("token-certificate.cer"));

using PdfDocument pdf = PdfDocument.Open("contract.pdf");
using RSA tokenKey = new TokenRsa(certificate, signOnToken);
await using FileStream output = File.Create("contract-signed.pdf");
await pdf.SignAsync("Signature1", new PdfSignOptions { Signer = PdfSigner.FromKey(tokenKey, certificate) }, output);

/// <summary>An RSA key whose private operation runs on a PKCS#11 token; it signs SHA-256 digests with PKCS#1 v1.5 padding.</summary>
sealed class TokenRsa : RSA
{
    // The DER prefix of a SHA-256 DigestInfo (RFC 8017 9.2), which CKM_RSA_PKCS expects in front of the digest.
    private static readonly byte[] Sha256Prefix = [0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20];
    private readonly X509Certificate2 certificate;
    private readonly Func<byte[], byte[]> signOnToken;

    public TokenRsa(X509Certificate2 certificate, Func<byte[], byte[]> signOnToken)
    {
        this.certificate = certificate;
        this.signOnToken = signOnToken;
        using RSA publicKey = certificate.GetRSAPublicKey()!;
        KeySizeValue = publicKey.KeySize;               // the size of the signatures it makes
        LegalKeySizesValue = [new KeySizes(KeySizeValue, KeySizeValue, 0)];
    }

    public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (hashAlgorithm != HashAlgorithmName.SHA256 || padding != RSASignaturePadding.Pkcs1)
        {
            throw new CryptographicException("The token signs SHA-256 with PKCS#1 v1.5 padding.");
        }

        return signOnToken([.. Sha256Prefix, .. hash]);
    }

    public override RSAParameters ExportParameters(bool includePrivateParameters)
    {
        using RSA publicKey = certificate.GetRSAPublicKey()!;
        return includePrivateParameters ? throw new CryptographicException("The private key stays on the token.") : publicKey.ExportParameters(false);
    }

    public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();
}
```

Egap signs with SHA-256 and PKCS#1 v1.5 padding by default, so this is all a token has to do. For a token reached
over a network, or a signing service with its own API, deferred signing (`PrepareSignatureAsync`) hands out the bytes to
sign and takes the signature back later.
