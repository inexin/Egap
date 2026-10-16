using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// Verification (Phase 5 task 7): integrity by SubFilter, the chain to the caller's trust anchors, and the signature
/// timestamp, each a verdict of its own with the reasons.
/// </summary>
public sealed class VerificationTests
{
    /// <summary>A day inside the test PKI's validity, so the reference files verify whenever the tests run.</summary>
    private static readonly DateTimeOffset When = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static X509Certificate2 Root => X509CertificateLoader.LoadCertificateFromFile(SignedFiles.PathOf("root.crt"));

    private static PdfVerifyOptions Trusting => new() { TrustAnchors = [Root], VerificationTime = When };

    [Theory]
    [InlineData("pyhanko-bb-rsa.pdf", "SHA256", "RSA")]
    [InlineData("pyhanko-bb-pkcs7.pdf", "SHA256", "RSA")]
    [InlineData("pyhanko-bb-ec384-sha384.pdf", "SHA384", "ECDSA")]
    [InlineData("pyhanko-certify-2.pdf", "SHA256", "RSA")]
    [InlineData("pyhanko-revoked.pdf", "SHA256", "RSA")]
    public void PyhankoSignaturesVerify(string file, string digest, string algorithm)
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf(file));
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(Trusting);
        Assert.Equal(PdfVerdict.Valid, report.Integrity);
        Assert.Equal(PdfVerdict.Valid, report.Chain);
        Assert.Equal(PdfVerdict.NotApplicable, report.Timestamp);
        Assert.Equal(PdfVerdict.NotApplicable, report.ChangesSince);
        Assert.Equal(PdfVerdict.Indeterminate, report.Revocation);
        Assert.False(report.IsValid);
        Assert.Equal(digest, report.DigestAlgorithm);
        Assert.Equal(algorithm, report.SignatureAlgorithm);
        Assert.StartsWith("CN=Egap Test Signer", report.SignerCertificate!.Subject, StringComparison.Ordinal);
        Assert.Equal(3, report.ChainCertificates.Count);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void ASignatureTimestampIsChecked()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-bt-ec.pdf"));
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(Trusting);
        Assert.Equal(PdfVerdict.Valid, report.Integrity);
        Assert.Equal(PdfVerdict.Valid, report.Timestamp);
        Assert.Equal(new DateTime(2026, 10, 16), report.TimestampTime!.Value.UtcDateTime.Date);
        Assert.Equal("CN=Egap Test TSA, O=Egap Test, C=NO", report.TimestampCertificate!.Subject);
    }

    [Fact]
    public void ADocumentTimestampVerifiesAsATimestamp()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-doc-timestamp.pdf"));
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(Trusting);
        Assert.Equal(PdfVerdict.Valid, report.Integrity);
        Assert.Equal(PdfVerdict.Valid, report.Chain);
        Assert.Equal("RFC 3161", report.SignatureAlgorithm);
        Assert.NotNull(report.TimestampTime);
    }

    [Fact]
    public void WithoutTrustAnchorsTheChainIsUndecided()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-bb-rsa.pdf"));
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify();
        Assert.Equal(PdfVerdict.Valid, report.Integrity);
        Assert.Equal(PdfVerdict.Indeterminate, report.Chain);
        Assert.Contains(report.Problems, p => p.Contains("No trust anchors", StringComparison.Ordinal));

        (X509Certificate2 other, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256", "CN=Someone Else");
        using (other)
        using (key)
        {
            Assert.Equal(PdfVerdict.Indeterminate, Assert.Single(pdf.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [other], VerificationTime = When }).Chain);
        }
    }

    [Fact]
    public void AChangedByteBreaksIntegrity()
    {
        byte[] file = File.ReadAllBytes(SignedFiles.PathOf("pyhanko-bb-rsa.pdf"));
        int at = Encoding.Latin1.GetString(file).IndexOf("Egap signature base", StringComparison.Ordinal);
        file[at] = (byte)'e';
        using PdfDocument pdf = PdfDocument.Open(file);
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(Trusting);
        Assert.Equal(PdfVerdict.Invalid, report.Integrity);
        Assert.Contains(report.Problems, p => p.Contains("message digest", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangedSignatureValueBreaksIntegrity()
    {
        byte[] file = File.ReadAllBytes(SignedFiles.PathOf("pyhanko-bb-rsa.pdf"));
        using (PdfDocument original = PdfDocument.Open(file))
        {
            PdfSignature signature = Assert.Single(original.Signatures);
            byte[] signatureValue = Assert.Single(signature.DecodeCms().Signers).Signature;
            string hex = Convert.ToHexString(signatureValue[..8]);
            int at = Encoding.Latin1.GetString(file).IndexOf(hex, StringComparison.OrdinalIgnoreCase);
            file[at] = file[at] == (byte)'0' ? (byte)'1' : (byte)'0';
        }

        using PdfDocument pdf = PdfDocument.Open(file);
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(Trusting);
        Assert.Equal(PdfVerdict.Invalid, report.Integrity);
        Assert.Contains(report.Problems, p => p.Contains("does not verify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EgapsOwnSignatureVerifiesAgainstItsCertificate()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("rsa");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) }));
            PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [certificate] });
            Assert.Equal(PdfVerdict.Valid, report.Integrity);
            Assert.Equal(PdfVerdict.Valid, report.Chain);
            Assert.Equal("Sig: integrity Valid, chain Valid, timestamp NotApplicable, revocation Valid, changes NotApplicable", report.ToString());
            Assert.True(report.IsValid);
        }
    }

    [Fact]
    public async Task AnExpiredCertificateIsUndecidedUnlessATimestampSpeaksForIt()
    {
        using TestTimestampAuthority tsa = new();
        using HttpClient http = new(tsa, disposeHandler: false);
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=Short Lived", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        DateTimeOffset later = DateTimeOffset.UtcNow.AddDays(10);

        using (PdfDocument plain = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) })))
        {
            PdfSignatureReport report = Assert.Single(plain.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [certificate, tsa.Certificate], VerificationTime = later });
            Assert.Equal(PdfVerdict.Indeterminate, report.Chain);
            Assert.DoesNotContain(report.Problems, p => p.Contains("expired since", StringComparison.Ordinal));
        }

        PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate), Level = PadesLevel.BaselineT, Timestamp = TestTimestampAuthority.Client(http) };
        using PdfDocument stamped = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", options));
        PdfSignatureReport timestamped = Assert.Single(stamped.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [certificate, tsa.Certificate], VerificationTime = later });
        Assert.Equal(PdfVerdict.Valid, timestamped.Timestamp);
        Assert.Equal(PdfVerdict.Indeterminate, timestamped.Chain);
        Assert.Contains(timestamped.Problems, p => p.Contains("expired since", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACertificateWithoutTheRightToSignFailsTheChain()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=Encryption Only", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) }));
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [certificate] });
        Assert.Equal(PdfVerdict.Valid, report.Integrity);
        Assert.Equal(PdfVerdict.Invalid, report.Chain);
    }

    [Fact]
    public void AnEarlierSignatureHasItsLaterRevisionsJudged()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-two-signatures.pdf"));
        PdfSignatureReport first = pdf.Signatures[0].Verify(Trusting);
        Assert.Equal(PdfVerdict.Valid, first.Integrity);
        Assert.Equal(PdfVerdict.Valid, first.ChangesSince);
        Assert.NotEmpty(first.Changes);
        Assert.Equal(PdfVerdict.NotApplicable, pdf.Signatures[1].Verify(Trusting).ChangesSince);
    }

    [Fact]
    public void ABadByteRangeIsNeverValid()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Signed(byteRange: r => $"[{r[0]} {r[1] - 1} {r[2]} {r[3]}]"));
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(Trusting);
        Assert.Equal(PdfVerdict.Invalid, report.Integrity);
        Assert.Contains(report.Problems, p => p.Contains("byte range", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOldRsaSha1FormatVerifiesWithAWarning()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("rsa");
        using (certificate)
        using (key)
        {
            string cert = "<" + Convert.ToHexString(certificate.RawData) + ">";
            byte[] file = SignedFiles.Signed(contents: new byte[264], signatureEntries: $"/Cert {cert}");
            file = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(file).Replace("/ETSI.CAdES.detached", "/adbe.x509.rsa_sha1 ", StringComparison.Ordinal));
#pragma warning disable CA5350 // The old format is SHA-1 by definition; this test makes one to read.
            Fill(file, range => Octets(((RSA)key).SignHash(SHA1.HashData(range), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)));
#pragma warning restore CA5350
            using PdfDocument pdf = PdfDocument.Open(file);
            PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [certificate] });
            Assert.Equal(PdfVerdict.Valid, report.Integrity);
            Assert.Equal(PdfVerdict.Valid, report.Chain);
            Assert.Contains(report.Warnings, w => w.Contains("SHA-1", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheOldPkcs7Sha1FormatVerifiesWithAWarning()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("rsa");
        using (certificate)
        using (key)
        {
            byte[] file = SignedFiles.Signed(contents: new byte[4096]);
            file = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(file).Replace("/ETSI.CAdES.detached", "/adbe.pkcs7.sha1    ", StringComparison.Ordinal));
#pragma warning disable CA5350 // The old format is SHA-1 by definition; this test makes one to read.
            Fill(file, range =>
            {
                SignedCms cms = new(new ContentInfo(SHA1.HashData(range)), detached: false);
                cms.ComputeSignature(new CmsSigner(certificate));
                return cms.Encode();
            });
#pragma warning restore CA5350
            using PdfDocument pdf = PdfDocument.Open(file);
            PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [certificate] });
            Assert.Equal(PdfVerdict.Valid, report.Integrity);
            Assert.Contains(report.Warnings, w => w.Contains("adbe.pkcs7.sha1", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ACertificateWithAnUnreadableKeyIsReportedNotThrown()
    {
        // Found by the signatures fuzz target (2026-10-16): on Linux, OpenSSL's chain building throws for this certificate.
        using PdfDocument pdf = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "files", "fuzz", "signature-unreadable-key.pdf"));
        foreach (PdfSignature signature in pdf.Signatures)
        {
            PdfSignatureReport report = signature.Verify(Trusting);
            Assert.False(report.IsValid);
        }
    }

    /// <summary>Writes into the reserved /Contents of a hand-made file the bytes made from its signed range.</summary>
    private static void Fill(byte[] file, Func<byte[], byte[]> sign)
    {
        byte[] range;
        long gapStart;
        using (PdfDocument pdf = PdfDocument.Open(file))
        {
            PdfSignature signature = Assert.Single(pdf.Signatures);
            using MemoryStream bytes = new();
            using (Stream signed = signature.OpenSignedBytes())
            {
                signed.CopyTo(bytes);
            }

            range = bytes.ToArray();
            gapStart = signature.ByteRange[0].End;
        }

        Encoding.ASCII.GetBytes(Convert.ToHexString(sign(range))).CopyTo(file, (int)gapStart + 1);
    }

    private static byte[] Octets(byte[] value)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);
        writer.WriteOctetString(value);
        return writer.Encode();
    }
}
