using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// Timestamps (Phase 5 task 5): an RFC 3161 client over the caller's HttpClient, signature timestamps for B-T, document
/// timestamps, deferred signatures timestamped on completion, and every way an authority can fail.
/// </summary>
public sealed class TimestampTests
{
    [Theory]
    [InlineData("SHA256")]
    [InlineData("SHA384")]
    public async Task BaselineTPutsATimestampOnTheSignatureValue(string imprint)
    {
        using TestTimestampAuthority tsa = new();
        using HttpClient http = new(tsa, disposeHandler: false);
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate), Level = PadesLevel.BaselineT, Timestamp = TestTimestampAuthority.Client(http, new HashAlgorithmName(imprint)) };
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", options));
            PdfSignature signature = Assert.Single(pdf.Signatures);
            SigningTests.AssertVerifies(signature, certificate);
            CmsSignerInfo signer = Assert.Single(signature.DecodeCms().Signers);
            Assert.True(Rfc3161TimestampToken.TryDecode(signer.TimestampToken!.Value, out Rfc3161TimestampToken? token, out _));
            Assert.Equal(CmsAlgorithms.Hash(new HashAlgorithmName(imprint), signer.Signature), token!.TokenInfo.GetMessageHash().ToArray());
            Assert.True(token.VerifySignatureForSignerInfo(SignerFor(signature), out X509Certificate2? tsaCertificate));
            Assert.Equal(tsa.Certificate.Thumbprint, tsaCertificate!.Thumbprint);
            Assert.Equal(1, tsa.Requests);
        }
    }

    [Fact]
    public async Task LevelsFromBaselineTNeedAnAuthority()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(SignedFiles.Base);
            await Assert.ThrowsAsync<ArgumentException>(() => pdf.SignAsync("Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate), Level = PadesLevel.BaselineT }, new MemoryStream(), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => pdf.PrepareSignatureAsync("Sig", new PdfSignOptions { Level = PadesLevel.BaselineLT }, new MemoryStream(), TestContext.Current.CancellationToken));
            Assert.False(pdf.IsReadOnly);
        }
    }

    [Fact]
    public async Task ADocumentTimestampCoversTheWholeRevision()
    {
        using TestTimestampAuthority tsa = new();
        using HttpClient http = new(tsa, disposeHandler: false);
        byte[] once;
        using (PdfDocument pdf = PdfDocument.Open(SignedFiles.Base))
        using (MemoryStream output = new())
        {
            await pdf.TimestampAsync(TestTimestampAuthority.Client(http), output, cancellationToken: TestContext.Current.CancellationToken);
            once = output.ToArray();
        }

        byte[] twice;
        using (PdfDocument pdf = PdfDocument.Open(once))
        using (MemoryStream output = new())
        {
            await pdf.TimestampAsync(TestTimestampAuthority.Client(http), output, cancellationToken: TestContext.Current.CancellationToken);
            twice = output.ToArray();
        }

        using PdfDocument stamped = PdfDocument.Open(twice);
        Assert.Equal(["DocTimeStamp1", "DocTimeStamp2"], stamped.Signatures.Select(s => s.Name));
        foreach (PdfSignature signature in stamped.Signatures)
        {
            Assert.Equal(PdfSignatureKind.DocumentTimestamp, signature.Kind);
            Assert.Equal("ETSI.RFC3161", signature.SubFilter);
            Assert.Equal(PdfByteRangeIssues.None, signature.ByteRangeIssues);
            Assert.True(Rfc3161TimestampToken.TryDecode(signature.Contents, out Rfc3161TimestampToken? token, out _));
            using Stream signed = signature.OpenSignedBytes();
            Assert.Equal(SHA256.HashData(signed), token!.TokenInfo.GetMessageHash().ToArray());
        }

        Assert.True(stamped.Signatures[1].CoversWholeFile);
    }

    [Fact]
    public async Task ADeferredSignatureIsTimestampedWhenCompleted()
    {
        using TestTimestampAuthority tsa = new();
        using HttpClient http = new(tsa, disposeHandler: false);
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("rsa");
        using (certificate)
        using (key)
        {
            using MemoryStream output = new();
            using (PdfDocument pdf = PdfDocument.Open(SignedFiles.Base))
            {
                PdfPreparedSignature prepared = await pdf.PrepareSignatureAsync("Sig", new PdfSignOptions { Level = PadesLevel.BaselineT, Timestamp = TestTimestampAuthority.Client(http) }, output, TestContext.Current.CancellationToken);
                await prepared.CompleteAsync(RemoteCms(prepared.Digest.ToArray(), certificate, key), TestContext.Current.CancellationToken);
            }

            using PdfDocument signed = PdfDocument.Open(output.ToArray());
            PdfSignature signature = Assert.Single(signed.Signatures);
            SigningTests.AssertVerifies(signature, certificate);
            Assert.NotNull(Assert.Single(signature.DecodeCms().Signers).TimestampToken);

            // A file prepared earlier is timestamped on completion too, and a CMS with a timestamp keeps its own.
            using MemoryStream later = new();
            byte[] digest;
            using (PdfDocument pdf = PdfDocument.Open(SignedFiles.Base))
            {
                digest = (await pdf.PrepareSignatureAsync("Sig", new PdfSignOptions(), later, TestContext.Current.CancellationToken)).Digest.ToArray();
            }

            using MemoryStream file = new(later.ToArray());
            await PdfPreparedSignature.CompleteAsync(file, RemoteCms(digest, certificate, key), TestTimestampAuthority.Client(http), TestContext.Current.CancellationToken);
            using PdfDocument completed = PdfDocument.Open(file.ToArray());
            Assert.NotNull(Assert.Single(Assert.Single(completed.Signatures).DecodeCms().Signers).TimestampToken);
            Assert.Equal(2, tsa.Requests);
        }
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("refuse")]
    [InlineData("nonsense")]
    public async Task AnAuthorityThatFailsIsReported(string mode)
    {
        using TestTimestampAuthority tsa = new() { Mode = mode };
        using HttpClient http = new(tsa, disposeHandler: false);
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(SignedFiles.Base);
            PdfException e = await Assert.ThrowsAsync<PdfException>(() => pdf.SignAsync("Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate), Level = PadesLevel.BaselineT, Timestamp = TestTimestampAuthority.Client(http) }, new MemoryStream(), TestContext.Current.CancellationToken));
            Assert.Contains("tsa.test", e.Message, StringComparison.Ordinal);
            using PdfDocument other = PdfDocument.Open(SignedFiles.Base);
            await Assert.ThrowsAsync<PdfException>(() => other.TimestampAsync(TestTimestampAuthority.Client(http), new MemoryStream(), cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    /// <summary>The signature's SignerInfo through .NET's SignedCms, which the token verifies against.</summary>
    private static SignerInfo SignerFor(PdfSignature signature)
    {
        SignedCms cms = new();
        cms.Decode(signature.Contents.Span);
        return cms.SignerInfos[0];
    }

    private static byte[] RemoteCms(byte[] digest, X509Certificate2 certificate, AsymmetricAlgorithm key)
    {
        byte[] attributes = CmsBuilder.SignedAttributes(HashAlgorithmName.SHA256, digest, certificate);
        (byte[] signature, CmsAlgorithm algorithm) = CmsAlgorithms.Sign(key, HashAlgorithmName.SHA256, false, attributes);
        return CmsBuilder.SignedData(HashAlgorithmName.SHA256, attributes, algorithm, signature, certificate, []);
    }
}
