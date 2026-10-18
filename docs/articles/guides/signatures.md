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
