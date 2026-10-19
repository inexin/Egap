using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// Signing (Phase 5 task 4): an incremental update with the byte range patched and the CMS in the reserved space, for each
/// kind of signer, into new and existing fields, again over earlier signatures, and deferred.
/// </summary>
public sealed class SigningTests
{
    [Fact]
    public async Task ASignatureCoversTheWholeFileAndVerifies()
    {
        byte[] original = SignedFiles.Base;
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("rsa", "CN=Egap Signer");
        using (certificate)
        using (key)
        {
            // A self-signed certificate made here holds its private key, as one loaded from a PFX file does.
            byte[] signed = await Sign(original, "Signature1", new PdfSignOptions { Signer = PdfSigner.FromCertificate(certificate), Reason = "Approved", Location = "Oslo" });
            Assert.Equal(original, signed[..original.Length]);

            using PdfDocument pdf = PdfDocument.Open(signed);
            Assert.Equal(2, pdf.Revisions.Count);
            PdfSignature signature = Assert.Single(pdf.Signatures);
            Assert.Equal("Signature1", signature.Name);
            Assert.Equal(PdfByteRangeIssues.None, signature.ByteRangeIssues);
            Assert.True(signature.CoversWholeFile);
            Assert.Equal("ETSI.CAdES.detached", signature.SubFilter);
            Assert.Equal("Egap Signer", signature.SignerName);
            Assert.Equal("Approved", signature.Reason);
            Assert.Equal("Oslo", signature.Location);
            Assert.NotNull(signature.SigningTime);
            Assert.Equal(3, pdf.Form.SignatureFlags);
            Assert.Equal([0, 0, 0, 0], signature.Field.Dictionary.Get<PdfArray>("Rect")!.Select(n => ((PdfInteger)n).Value));
            Assert.Single(pdf.Pages[0].Annotations);
            AssertVerifies(signature, certificate);
        }
    }

    [Theory]
    [InlineData("p256", "SHA256", false)]
    [InlineData("p384", "SHA384", false)]
    [InlineData("rsa", "SHA512", true)]
    public async Task AKeyObjectSigns(string kind, string hash, bool pss)
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned(kind);
        using (certificate)
        using (key)
        {
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate), DigestAlgorithm = new HashAlgorithmName(hash), UseRsaPss = pss };
            using PdfDocument pdf = PdfDocument.Open(await Sign(SignedFiles.Base, "Sig", options));
            PdfSignature signature = Assert.Single(pdf.Signatures);
            Assert.Equal(new HashAlgorithmName(hash), Assert.Single(signature.DecodeCms().Signers).DigestHash);
            AssertVerifies(signature, certificate);
        }
    }

    [Fact]
    public async Task AKeyThatSignsOutsideTheProcessIsAskedOnlyForTheSignature()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        using (CountingKey remote = new((ECDsa)key))
        {
            using PdfDocument pdf = PdfDocument.Open(await Sign(SignedFiles.Base, "Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(remote, certificate) }));
            Assert.Equal(1, remote.Calls);
            AssertVerifies(Assert.Single(pdf.Signatures), certificate);
        }
    }

    [Fact]
    public async Task ATokenKeyAsTheGuideWrapsItSigns()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("rsa", "CN=Token");
        using (certificate)
        using (key)
        using (TokenRsa token = new(certificate, digestInfo => TokenRsa.Token((RSA)key, digestInfo)))
        {
            using PdfDocument pdf = PdfDocument.Open(await Sign(SignedFiles.Base, "Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(token, certificate) }));
            AssertVerifies(Assert.Single(pdf.Signatures), certificate);
        }
    }

    [Fact]
    public async Task ASecondSignatureKeepsTheFirst()
    {
        (X509Certificate2 first, AsymmetricAlgorithm firstKey) = CmsTests.SelfSigned("rsa", "CN=First");
        (X509Certificate2 second, AsymmetricAlgorithm secondKey) = CmsTests.SelfSigned("p256", "CN=Second");
        using (first)
        using (firstKey)
        using (second)
        using (secondKey)
        {
            byte[] once = await Sign(SignedFiles.Base, "One", new PdfSignOptions { Signer = PdfSigner.FromKey(firstKey, first) });
            byte[] twice = await Sign(once, "Two", new PdfSignOptions { Signer = PdfSigner.FromKey(secondKey, second) });
            Assert.Equal(once, twice[..once.Length]);
            using PdfDocument pdf = PdfDocument.Open(twice);
            Assert.Equal(["One", "Two"], pdf.Signatures.Select(s => s.Name));
            Assert.False(pdf.Signatures[0].CoversWholeFile);
            Assert.True(pdf.Signatures[1].CoversWholeFile);
            AssertVerifies(pdf.Signatures[0], first);
            AssertVerifies(pdf.Signatures[1], second);
            Assert.Equal(2, pdf.Pages[0].Annotations.Count);
        }
    }

    [Fact]
    public async Task AnExistingEmptyFieldIsSignedInPlace()
    {
        byte[] withField = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(SignedFiles.Signed()).Replace("/V 6 0 R", "/V  null", StringComparison.Ordinal));
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(await Sign(withField, "Sig1", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) }));
            Assert.Single(pdf.Form.GetAllFields());
            AssertVerifies(Assert.Single(pdf.Signatures), certificate);
        }
    }

    [Fact]
    public async Task WrongFieldsAndMissingSignersAreRefused()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate) };
            using PdfDocument signed = PdfDocument.Open(SignedFiles.Signed());
            await Assert.ThrowsAsync<ArgumentException>(() => signed.SignAsync("Sig1", options, new MemoryStream(), TestContext.Current.CancellationToken));
            using PdfDocument plain = PdfDocument.Open(SignedFiles.Base);
            await Assert.ThrowsAsync<ArgumentException>(() => plain.SignAsync("Sig", new PdfSignOptions(), new MemoryStream(), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => plain.SignAsync("A.B", options, new MemoryStream(), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => plain.SignAsync("Sig", options with { DigestAlgorithm = HashAlgorithmName.SHA1 }, new MemoryStream(), TestContext.Current.CancellationToken));
            using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
            Assert.Throws<ArgumentException>(() => PdfSigner.FromCertificate(publicOnly));
#pragma warning disable CA5384 // A DSA key is what the test needs refused.
            using DSA dsa = DSA.Create(1024);                       // the largest size every OS creates (macOS stops at 1024)
#pragma warning restore CA5384
            Assert.Throws<ArgumentException>(() => PdfSigner.FromKey(dsa, certificate));
        }
    }

    [Fact]
    public async Task ASignedDocumentIsReadOnlyAfterwards()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(SignedFiles.Base);
            await pdf.SignAsync("Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) }, new MemoryStream(), TestContext.Current.CancellationToken);
            Assert.True(pdf.IsReadOnly);
            InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => pdf.Save(new MemoryStream()));
            Assert.Contains("signed into another stream", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AdbePkcs7CarriesTheSigningTimeInTheCms()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("rsa");
        using (certificate)
        using (key)
        {
            DateTimeOffset time = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate), Format = PdfSignatureFormat.Pkcs7, SigningTime = time };
            using PdfDocument pdf = PdfDocument.Open(await Sign(SignedFiles.Base, "Sig", options));
            PdfSignature signature = Assert.Single(pdf.Signatures);
            Assert.Equal("adbe.pkcs7.detached", signature.SubFilter);
            Assert.Equal(time, signature.SigningTime);
            Assert.Equal(time, Assert.Single(signature.DecodeCms().Signers).SigningTime);
            AssertVerifies(signature, certificate);
        }
    }

    [Fact]
    public async Task TooSmallAReservationNamesTheSizeNeeded()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("rsa");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(SignedFiles.Base);
            PdfException e = await Assert.ThrowsAsync<PdfException>(() => pdf.SignAsync("Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate), ReservedSize = 100 }, new MemoryStream(), TestContext.Current.CancellationToken));
            Assert.Contains("only 100 were reserved", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DeferredSigningCompletesInTheSameStreamOrLater()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using MemoryStream output = new();
            using (PdfDocument pdf = PdfDocument.Open(SignedFiles.Base))
            {
                PdfPreparedSignature prepared = await pdf.PrepareSignatureAsync("Sig", new PdfSignOptions(), output, TestContext.Current.CancellationToken);
                Assert.Equal(HashAlgorithmName.SHA256, prepared.DigestAlgorithm);
                Assert.Equal(32 * 1024, prepared.ReservedSize);
                await prepared.CompleteAsync(RemoteCms(prepared.Digest.ToArray(), certificate, key), TestContext.Current.CancellationToken);
            }

            using (PdfDocument signed = PdfDocument.Open(output.ToArray()))
            {
                AssertVerifies(Assert.Single(signed.Signatures), certificate);
            }

            // Prepared now, completed later from the file alone.
            using MemoryStream later = new();
            byte[] digest;
            using (PdfDocument pdf = PdfDocument.Open(SignedFiles.Base))
            {
                digest = (await pdf.PrepareSignatureAsync("Sig", new PdfSignOptions(), later, TestContext.Current.CancellationToken)).Digest.ToArray();
            }

            using (MemoryStream file = new(later.ToArray()))
            {
                await PdfPreparedSignature.CompleteAsync(file, RemoteCms(digest, certificate, key), cancellationToken: TestContext.Current.CancellationToken);
                using PdfDocument signed = PdfDocument.Open(file.ToArray());
                AssertVerifies(Assert.Single(signed.Signatures), certificate);
                file.Position = 0;
                await Assert.ThrowsAsync<PdfException>(() => PdfPreparedSignature.CompleteAsync(file, new byte[10], cancellationToken: TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public async Task DeferredSigningNeedsASeekableStreamAndACmsThatFits()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Base);
        await Assert.ThrowsAsync<ArgumentException>(() => pdf.PrepareSignatureAsync("Sig", new PdfSignOptions(), new ForwardOnlyStream(), TestContext.Current.CancellationToken));
        using MemoryStream output = new();
        using PdfDocument other = PdfDocument.Open(SignedFiles.Base);
        PdfPreparedSignature prepared = await other.PrepareSignatureAsync("Sig", new PdfSignOptions { ReservedSize = 50 }, output, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PdfException>(() => prepared.CompleteAsync(new byte[51], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("aes-256.pdf", "user")]
    [InlineData("rc4-128.pdf", "owner")]
    public async Task AnEncryptedFileIsSignedWithItsContentsInTheClear(string file, string password)
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "encrypted", file));
            PdfReadOptions read = new() { Password = password };
            byte[] signed;
            using (PdfDocument pdf = PdfDocument.Open(bytes, read))
            using (MemoryStream output = new())
            {
                await pdf.SignAsync("Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate), Reason = "Encrypted" }, output, TestContext.Current.CancellationToken);
                signed = output.ToArray();
            }

            using PdfDocument reopened = PdfDocument.Open(signed, read);
            PdfSignature signature = Assert.Single(reopened.Signatures);
            Assert.Equal("Encrypted", signature.Reason);
            AssertVerifies(signature, certificate);
        }
    }

    [Fact]
    public async Task AFileWithACrossReferenceStreamIsSigned()
    {
        byte[] compact;
        using (PdfDocument created = PdfDocument.Open(SignedFiles.Base))
        using (MemoryStream output = new())
        {
            created.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = PdfCompression.Compact });
            compact = output.ToArray();
        }

        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(await Sign(compact, "Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) }));
            PdfSignature signature = Assert.Single(pdf.Signatures);
            Assert.True(signature.CoversWholeFile);
            AssertVerifies(signature, certificate);
        }
    }

    internal static async Task<byte[]> Sign(byte[] input, string field, PdfSignOptions options)
    {
        using PdfDocument pdf = PdfDocument.Open(input);
        using MemoryStream output = new();
        await pdf.SignAsync(field, options, output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    /// <summary>The signature's message digest is the hash of its byte ranges, and its CMS signature verifies with the certificate.</summary>
    internal static void AssertVerifies(PdfSignature signature, X509Certificate2 certificate)
    {
        CmsSignedData cms = signature.DecodeCms();
        CmsSignerInfo signer = Assert.Single(cms.Signers);
        HashAlgorithmName hash = signer.DigestHash!.Value;
        using (Stream signed = signature.OpenSignedBytes())
        {
            Assert.Equal(CmsAlgorithms.Hash(hash, signed), signer.MessageDigest);
        }

        Assert.True(signer.Id.Matches(certificate));
        Assert.True(CmsAlgorithms.Verify(signer.SignatureAlgorithm, hash, signer.SignedAttributesEncoding, signer.Signature, certificate));

        // .NET's own SignedCms agrees, given the signed bytes.
        using MemoryStream content = new();
        using (Stream signed = signature.OpenSignedBytes())
        {
            signed.CopyTo(content);
        }

        SignedCms dotnet = new(new ContentInfo(content.ToArray()), detached: true);
        dotnet.Decode(signature.Contents.Span);
        dotnet.CheckSignature([certificate], verifySignatureOnly: true);
    }

    /// <summary>What a remote signing service returns for a digest: a whole CMS.</summary>
    private static byte[] RemoteCms(byte[] digest, X509Certificate2 certificate, AsymmetricAlgorithm key)
    {
        byte[] attributes = CmsBuilder.SignedAttributes(HashAlgorithmName.SHA256, digest, certificate);
        (byte[] signature, CmsAlgorithm algorithm) = CmsAlgorithms.Sign(key, HashAlgorithmName.SHA256, false, attributes);
        return CmsBuilder.SignedData(HashAlgorithmName.SHA256, attributes, algorithm, signature, certificate, []);
    }

    /// <summary>
    /// The PKCS#11 sample in the signatures guide: an RSA whose SignHash hands a DigestInfo to a token. The token is played
    /// by a real key, which takes the digest back out of the DigestInfo and signs it with PKCS#1 v1.5 padding, as
    /// <c>CKM_RSA_PKCS</c> would sign the DigestInfo.
    /// </summary>
    private sealed class TokenRsa : RSA
    {
        private static readonly byte[] Sha256Prefix = [0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20];
        private readonly X509Certificate2 certificate;
        private readonly Func<byte[], byte[]> signOnToken;

        public TokenRsa(X509Certificate2 certificate, Func<byte[], byte[]> signOnToken)
        {
            this.certificate = certificate;
            this.signOnToken = signOnToken;
            using RSA publicKey = certificate.GetRSAPublicKey()!;
            KeySizeValue = publicKey.KeySize;
            LegalKeySizesValue = [new KeySizes(KeySizeValue, KeySizeValue, 0)];
        }

        public static byte[] Token(RSA key, byte[] digestInfo)
        {
            Assert.Equal(Sha256Prefix, digestInfo[..Sha256Prefix.Length]);
            return key.SignHash(digestInfo[Sha256Prefix.Length..], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

    /// <summary>An ECDsa that forwards to another and counts the signatures asked of it, as an HSM key would be called.</summary>
    private sealed class CountingKey(ECDsa inner) : ECDsa
    {
        public int Calls { get; private set; }

        public override int KeySize
        {
            get => inner.KeySize;
            set => throw new NotSupportedException();
        }

        public override byte[] SignHash(byte[] hash)
        {
            Calls++;
            return inner.SignHash(hash);
        }

        public override bool VerifyHash(byte[] hash, byte[] signature)
        {
            return inner.VerifyHash(hash, signature);
        }

        public override ECParameters ExportParameters(bool includePrivateParameters)
        {
            return inner.ExportParameters(false);
        }

        protected override byte[] HashData(byte[] data, int offset, int count, HashAlgorithmName hashAlgorithm)
        {
            return CmsAlgorithms.Hash(hashAlgorithm, data.AsSpan(offset, count));
        }
    }

    private sealed class ForwardOnlyStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
