using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// The CMS layer (Phase 5 task 3): SignedData written with PAdES signed attributes for every algorithm of design Q4 and read
/// back by .NET's own SignedCms, pyHanko's signatures read and checked, SHA-1 read as weak, and broken input refused cleanly.
/// </summary>
public sealed class CmsTests
{
    public static TheoryData<string, string> Algorithms => new()
    {
        { "rsa", "SHA256" }, { "rsa", "SHA384" }, { "rsa", "SHA512" },
        { "rsa-pss", "SHA256" }, { "rsa-pss", "SHA512" },
        { "p256", "SHA256" }, { "p384", "SHA384" }, { "p521", "SHA512" },
    };

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void WrittenSignedDataVerifiesInSignedCms(string keyKind, string hashName)
    {
        HashAlgorithmName hash = new(hashName);
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = SelfSigned(keyKind);
        using (certificate)
        using (key)
        {
            byte[] content = RandomNumberGenerator.GetBytes(10_000);
            byte[] attributes = CmsBuilder.SignedAttributes(hash, CmsAlgorithms.Hash(hash, content), certificate);
            (byte[] signature, CmsAlgorithm algorithm) = CmsAlgorithms.Sign(key, hash, keyKind == "rsa-pss", attributes);
            byte[] cms = CmsBuilder.SignedData(hash, attributes, algorithm, signature, certificate, []);

            SignedCms dotnet = new(new ContentInfo(content), detached: true);
            dotnet.Decode(cms);
            dotnet.CheckSignature(verifySignatureOnly: true);
            Assert.Equal(CmsAlgorithms.DigestOid(hash), dotnet.SignerInfos[0].DigestAlgorithm.Value);

            CmsSignedData read = CmsSignedData.Decode(cms);
            CmsSignerInfo signer = Assert.Single(read.Signers);
            Assert.Equal(CmsOids.Data, read.ContentType);
            Assert.Null(read.Content);
            Assert.Equal(hash, signer.DigestHash);
            Assert.Equal(CmsOids.Data, signer.ContentType);
            Assert.Equal(CmsAlgorithms.Hash(hash, content), signer.MessageDigest);
            Assert.Null(signer.SigningTime);
            Assert.False(signer.UsesWeakDigest);
            (HashAlgorithmName certHash, byte[] certDigest) = Assert.Single(signer.SigningCertificateHashes);
            Assert.Equal(hash, certHash);
            Assert.Equal(CmsAlgorithms.Hash(hash, certificate.RawData), certDigest);
            X509Certificate2 found = signer.FindCertificate(read.LoadCertificates())!;
            Assert.Equal(certificate.Thumbprint, found.Thumbprint);
            Assert.True(CmsAlgorithms.Verify(signer.SignatureAlgorithm, hash, signer.SignedAttributesEncoding, signer.Signature, found));
            Assert.Equal(keyKind switch { "rsa" => "RSA", "rsa-pss" => "RSA-PSS", _ => "ECDSA" }, CmsAlgorithms.SignatureName(signer.SignatureAlgorithm));

            // A changed attribute byte no longer verifies.
            byte[] tampered = (byte[])signer.SignedAttributesEncoding!.Clone();
            tampered[^1] ^= 1;
            Assert.False(CmsAlgorithms.Verify(signer.SignatureAlgorithm, hash, tampered, signer.Signature, found));
        }
    }

    [Fact]
    public void ChainSigningTimeAndUnsignedAttributesAreWritten()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = SelfSigned("p256");
        (X509Certificate2 other, AsymmetricAlgorithm otherKey) = SelfSigned("rsa");
        using (certificate)
        using (key)
        using (other)
        using (otherKey)
        {
            DateTimeOffset time = new(2026, 10, 16, 12, 30, 0, TimeSpan.Zero);
            byte[] content = "PDF"u8.ToArray();
            byte[] attributes = CmsBuilder.SignedAttributes(HashAlgorithmName.SHA256, SHA256.HashData(content), certificate, time);
            (byte[] signature, CmsAlgorithm algorithm) = CmsAlgorithms.Sign(key, HashAlgorithmName.SHA256, false, attributes);
            CmsAttribute token = new(CmsOids.SignatureTimestampToken, [new byte[] { 0x30, 0x00 }]);
            byte[] cms = CmsBuilder.SignedData(HashAlgorithmName.SHA256, attributes, algorithm, signature, certificate, [other, certificate], [token]);

            CmsSignedData read = CmsSignedData.Decode(cms);
            Assert.Equal(2, read.Certificates.Count);
            CmsSignerInfo signer = Assert.Single(read.Signers);
            Assert.Equal(time, signer.SigningTime);
            Assert.Equal([0x30, 0x00], signer.TimestampToken!.Value.ToArray());

            SignedCms dotnet = new(new ContentInfo(content), detached: true);
            dotnet.Decode(cms);
            dotnet.CheckSignature(verifySignatureOnly: true);
            Assert.Single(dotnet.SignerInfos[0].UnsignedAttributes);
        }
    }

    [Theory]
    [InlineData("pyhanko-bb-rsa.pdf", "SHA256", "RSA")]
    [InlineData("pyhanko-bb-pkcs7.pdf", "SHA256", "RSA")]
    [InlineData("pyhanko-bb-ec384-sha384.pdf", "SHA384", "ECDSA")]
    [InlineData("pyhanko-bt-ec.pdf", "SHA256", "ECDSA")]
    [InlineData("pyhanko-certify-1.pdf", "SHA256", "RSA")]
    public void PyhankoSignaturesReadAndCheck(string file, string hashName, string signatureName)
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf(file));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        CmsSignedData cms = signature.DecodeCms();
        CmsSignerInfo signer = Assert.Single(cms.Signers);
        HashAlgorithmName hash = new(hashName);
        Assert.Equal(hash, signer.DigestHash);
        using (Stream signed = signature.OpenSignedBytes())
        {
            Assert.Equal(CmsAlgorithms.Hash(hash, signed), signer.MessageDigest);
        }

        List<X509Certificate2> certificates = cms.LoadCertificates();
        X509Certificate2 certificate = signer.FindCertificate(certificates)!;
        Assert.StartsWith("CN=Egap Test Signer", certificate.Subject, StringComparison.Ordinal);
        Assert.Equal(signatureName, CmsAlgorithms.SignatureName(signer.SignatureAlgorithm));
        Assert.True(CmsAlgorithms.Verify(signer.SignatureAlgorithm, hash, signer.SignedAttributesEncoding, signer.Signature, certificate));
        (HashAlgorithmName certHash, byte[] certDigest) = signer.SigningCertificateHashes[0];
        Assert.Equal(CmsAlgorithms.Hash(certHash, certificate.RawData), certDigest);
        foreach (X509Certificate2 c in certificates)
        {
            c.Dispose();
        }
    }

    [Fact]
    public void AnRfc3161TokenIsTheSignaturesTimestamp()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-bt-ec.pdf"));
        CmsSignerInfo signer = Assert.Single(Assert.Single(pdf.Signatures).DecodeCms().Signers);
        Assert.True(Rfc3161TimestampToken.TryDecode(signer.TimestampToken!.Value, out Rfc3161TimestampToken? token, out _));
        Assert.Equal(SHA256.HashData(signer.Signature), token!.TokenInfo.GetMessageHash().ToArray());

        // The token is itself a SignedData over a TSTInfo, which the CMS reader reads too.
        CmsSignedData tokenData = CmsSignedData.Decode(signer.TimestampToken.Value);
        Assert.Equal(CmsOids.TstInfo, tokenData.ContentType);
        Assert.NotNull(tokenData.Content);
    }

    [Fact]
    public void ADocumentTimestampIsATokenOverTheRange()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-doc-timestamp.pdf"));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.True(Rfc3161TimestampToken.TryDecode(signature.Contents, out Rfc3161TimestampToken? token, out _));
        using Stream signed = signature.OpenSignedBytes();
        Assert.Equal(CmsAlgorithms.Hash(HashAlgorithmName.SHA256, signed), token!.TokenInfo.GetMessageHash().ToArray());
    }

    [Fact]
    public void Sha1IsReadAndMarkedWeak()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = SelfSigned("rsa");
        using (certificate)
        using (key)
        {
            byte[] content = "old"u8.ToArray();
            SignedCms dotnet = new(new ContentInfo(content), detached: true);
#pragma warning disable CA5350 // Writing SHA-1 is what this test needs: Egap itself never does.
            CmsSigner cmsSigner = new(certificate) { DigestAlgorithm = new Oid(CmsOids.Sha1) };
            cmsSigner.SignedAttributes.Add(new Pkcs9SigningTime());
            dotnet.ComputeSignature(cmsSigner);
#pragma warning restore CA5350
            CmsSignedData read = CmsSignedData.Decode(dotnet.Encode());
            CmsSignerInfo signer = Assert.Single(read.Signers);
            Assert.Equal(HashAlgorithmName.SHA1, signer.DigestHash);
            Assert.True(signer.UsesWeakDigest);
            Assert.NotNull(signer.SigningTime);
            Assert.True(CmsAlgorithms.Verify(signer.SignatureAlgorithm, HashAlgorithmName.SHA1, signer.SignedAttributesEncoding, signer.Signature, certificate));
            Assert.Throws<ArgumentException>(() => CmsAlgorithms.Sign(key, HashAlgorithmName.SHA1, false, content));
        }
    }

    [Theory]
    [InlineData("rsa")]
    [InlineData("p384")]
    public void ASignerWithoutSignedAttributesVerifiesOverTheContentHash(string kind)
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = SelfSigned(kind);
        using (certificate)
        using (key)
        {
            byte[] content = RandomNumberGenerator.GetBytes(5000);
            SignedCms dotnet = new(new ContentInfo(content), detached: true);
            dotnet.ComputeSignature(new CmsSigner(certificate) { DigestAlgorithm = new Oid(CmsOids.Sha256) });
            CmsSignerInfo signer = Assert.Single(CmsSignedData.Decode(dotnet.Encode()).Signers);
            Assert.Null(signer.SignedAttributesEncoding);
            Assert.Null(signer.MessageDigest);
            Assert.True(CmsAlgorithms.VerifyHash(signer.SignatureAlgorithm, HashAlgorithmName.SHA256, SHA256.HashData(content), signer.Signature, certificate));
            Assert.False(CmsAlgorithms.VerifyHash(signer.SignatureAlgorithm, HashAlgorithmName.SHA256, SHA256.HashData("other"u8), signer.Signature, certificate));
            Assert.False(CmsAlgorithms.VerifyHash(signer.SignatureAlgorithm, HashAlgorithmName.SHA384, SHA384.HashData(content), signer.Signature, certificate));
        }
    }

    [Fact]
    public void SignedCmsWithASubjectKeyIdentifierFindsItsSigner()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = SelfSigned("p256");
        using (certificate)
        using (key)
        {
            SignedCms dotnet = new(new ContentInfo("x"u8.ToArray()), detached: true);
            dotnet.ComputeSignature(new CmsSigner(SubjectIdentifierType.SubjectKeyIdentifier, certificate));
            CmsSignedData read = CmsSignedData.Decode(dotnet.Encode());
            CmsSignerInfo signer = Assert.Single(read.Signers);
            Assert.NotNull(signer.Id.SubjectKeyIdentifier);
            Assert.Equal(certificate.Thumbprint, signer.FindCertificate(read.LoadCertificates())?.Thumbprint);
        }
    }

    [Fact]
    public void PaddingAfterTheSignedDataIsIgnored()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-bb-rsa.pdf"));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal(0, signature.Contents.Span[^1]);
        Assert.Single(signature.DecodeCms().Signers);
    }

    [Fact]
    public void BrokenInputThrowsPdfException()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-bb-rsa.pdf"));
        byte[] cms = Assert.Single(pdf.Signatures).Contents.ToArray();
        int length = new AsnReader(cms, AsnEncodingRules.BER).ReadEncodedValue().Length;
        Assert.Throws<PdfException>(() => CmsSignedData.Decode(Array.Empty<byte>()));
        Assert.Throws<PdfException>(() => CmsSignedData.Decode(new byte[] { 0x30, 0x03, 0x06, 0x01, 0x00 }));
        for (int cut = 1; cut < length; cut += 97)
        {
            Assert.Throws<PdfException>(() => CmsSignedData.Decode(cms.AsMemory(0, cut)));
        }

        // Every single-byte change either still reads or throws PdfException.
        Random random = new(5);
        for (int i = 0; i < 2000; i++)
        {
            byte[] changed = (byte[])cms.Clone();
            changed[random.Next(length)] = (byte)random.Next(256);
            try
            {
                CmsSignedData read = CmsSignedData.Decode(changed);
                foreach (CmsSignerInfo signer in read.Signers)
                {
                    _ = (signer.MessageDigest, signer.ContentType, signer.SigningTime, signer.SigningCertificateHashes, signer.TimestampToken, signer.UsesWeakDigest);
                }

                read.LoadCertificates().ForEach(c => c.Dispose());
            }
            catch (PdfException)
            {
            }
        }
    }

    [Fact]
    public void AnRsaSha1SignatureReadsItsOctetStringAndCertificates()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = SelfSigned("rsa");
        using (certificate)
        using (key)
        {
            AsnWriter writer = new(AsnEncodingRules.DER);
            writer.WriteOctetString([1, 2, 3]);
            byte[] contents = [.. writer.Encode(), 0, 0, 0];
            Assert.Equal([1, 2, 3], RsaSha1Signature.Read(contents));
            Assert.Throws<PdfException>(() => RsaSha1Signature.Read(new byte[] { 0x30, 0x00 }));

            PdfDictionary dictionary = new() { ["Cert"] = new PdfArray { PdfString.FromBytes(certificate.RawData), PdfString.FromBytes([1, 2]) } };
            Assert.Equal(certificate.Thumbprint, Assert.Single(RsaSha1Signature.Certificates(dictionary)).Thumbprint);
            dictionary["Cert"] = PdfString.FromBytes(certificate.RawData);
            Assert.Single(RsaSha1Signature.Certificates(dictionary));
        }
    }

    internal static (X509Certificate2 Certificate, AsymmetricAlgorithm Key) SelfSigned(string kind, string name = "CN=Egap CMS Test")
    {
        AsymmetricAlgorithm key = kind switch
        {
            "rsa" or "rsa-pss" => RSA.Create(2048),
            "p256" => ECDsa.Create(ECCurve.NamedCurves.nistP256),
            "p384" => ECDsa.Create(ECCurve.NamedCurves.nistP384),
            _ => ECDsa.Create(ECCurve.NamedCurves.nistP521),
        };
        CertificateRequest request = key is RSA rsa
            ? new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(name, (ECDsa)key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate, key);
    }
}
