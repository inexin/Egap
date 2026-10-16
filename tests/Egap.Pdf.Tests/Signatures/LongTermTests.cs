using System.Security.Cryptography.X509Certificates;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// Long-term validation (Phase 5 task 10): OCSP and CRLs fetched online or given offline, the DSS and VRI written for B-LT,
/// a document timestamp for B-LTA, and the revocation verdict from the file alone.
/// </summary>
public sealed class LongTermTests
{
    [Fact]
    public async Task BaselineLtCarriesWhatAVerifierNeeds()
    {
        using TestCertificateAuthority ca = new();
        using HttpClient http = new(ca, disposeHandler: false);
        PdfSignOptions options = new()
        {
            Signer = PdfSigner.FromKey(ca.SignerKey, ca.Signer, [ca.Root]),
            Level = PadesLevel.BaselineLT,
            Timestamp = TestCertificateAuthority.Timestamps(http),
            Revocation = PdfRevocationSource.Online(http),
        };
        byte[] signed = await SigningTests.Sign(SignedFiles.Base, "Sig", options);
        using PdfDocument pdf = PdfDocument.Open(signed);
        Assert.Equal(3, pdf.Revisions.Count);
        PdfDictionary dss = pdf.Catalog.Get<PdfDictionary>("DSS")!;
        Assert.NotEmpty(dss.Get<PdfArray>("Certs")!);
        Assert.NotEmpty(dss.Get<PdfArray>("OCSPs")!);
        PdfSignature signature = Assert.Single(pdf.Signatures);
        PdfDictionary vri = dss.Get<PdfDictionary>("VRI")!.Get<PdfDictionary>(Egap.Pdf.Signatures.LongTermWriter.VriKey(signature.Contents.Span))!;
        Assert.NotNull(vri.Get<PdfArray>("OCSP"));
        Assert.True(ca.OcspRequests >= 1);

        // Verified with nothing fetched: the file holds the chain's revocation data.
        PdfSignatureReport report = signature.Verify(new PdfVerifyOptions { TrustAnchors = [ca.Root] });
        Assert.Equal(PdfVerdict.Valid, report.Integrity);
        Assert.Equal(PdfVerdict.Valid, report.Chain);
        Assert.Equal(PdfVerdict.Valid, report.Timestamp);
        Assert.Equal(PdfVerdict.Valid, report.Revocation);
        Assert.Equal(PdfVerdict.Valid, report.ChangesSince);
        Assert.True(report.IsValid, string.Join("; ", report.Problems));
    }

    [Fact]
    public async Task BaselineLtaEndsWithADocumentTimestamp()
    {
        using TestCertificateAuthority ca = new();
        using HttpClient http = new(ca, disposeHandler: false);
        PdfSignOptions options = new()
        {
            Signer = PdfSigner.FromKey(ca.SignerKey, ca.Signer, [ca.Root]),
            Level = PadesLevel.BaselineLTA,
            Timestamp = TestCertificateAuthority.Timestamps(http),
            Revocation = PdfRevocationSource.Online(http),
        };
        using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", options));
        Assert.Equal([PdfSignatureKind.Approval, PdfSignatureKind.DocumentTimestamp], pdf.Signatures.Select(s => s.Kind));
        Assert.True(pdf.Signatures[1].CoversWholeFile);
        PdfVerifyOptions verify = new() { TrustAnchors = [ca.Root] };
        Assert.True(pdf.Signatures[0].Verify(verify).IsValid, string.Join("; ", pdf.Signatures[0].Verify(verify).Problems));
        PdfSignatureReport archive = pdf.Signatures[1].Verify(verify);
        Assert.Equal(PdfVerdict.Valid, archive.Integrity);
        Assert.Equal(PdfVerdict.Valid, archive.Chain);
    }

    [Fact]
    public async Task CrlsServeWhenOcspIsDown()
    {
        using TestCertificateAuthority ca = new() { OcspUp = false };
        using HttpClient http = new(ca, disposeHandler: false);
        PdfSignOptions options = new()
        {
            Signer = PdfSigner.FromKey(ca.SignerKey, ca.Signer, [ca.Root]),
            Level = PadesLevel.BaselineLT,
            Timestamp = TestCertificateAuthority.Timestamps(http),
            Revocation = PdfRevocationSource.Online(http),
        };
        using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", options));
        Assert.NotEmpty(pdf.Catalog.Get<PdfDictionary>("DSS")!.Get<PdfArray>("CRLs")!);
        Assert.True(ca.CrlRequests >= 1);
        Assert.Equal(PdfVerdict.Valid, Assert.Single(pdf.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [ca.Root] }).Revocation);
    }

    [Fact]
    public async Task ARevokedSignerIsInvalid()
    {
        using TestCertificateAuthority ca = new();
        using HttpClient http = new(ca, disposeHandler: false);
        byte[] signed = await SigningTests.Sign(SignedFiles.Base, "Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(ca.RevokedKey, ca.Revoked) });
        using PdfDocument pdf = PdfDocument.Open(signed);
        PdfSignature signature = Assert.Single(pdf.Signatures);

        // Without data the verdict is open; with an online source it is decided.
        Assert.Equal(PdfVerdict.Indeterminate, signature.Verify(new PdfVerifyOptions { TrustAnchors = [ca.Root] }).Revocation);
        PdfSignatureReport report = signature.Verify(new PdfVerifyOptions { TrustAnchors = [ca.Root], Revocation = PdfRevocationSource.Online(http) });
        Assert.Equal(PdfVerdict.Invalid, report.Revocation);
        Assert.Contains(report.Problems, p => p.Contains("revoked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfflineResponsesAnswerToo()
    {
        using TestCertificateAuthority ca = new();
        using HttpClient http = new(ca, disposeHandler: false);
        byte[] crl = await http.GetByteArrayAsync(new Uri(TestCertificateAuthority.Base, "crl"), TestContext.Current.CancellationToken);
        byte[] signed = await SigningTests.Sign(SignedFiles.Base, "Sig", new PdfSignOptions { Signer = PdfSigner.FromKey(ca.SignerKey, ca.Signer) });
        using PdfDocument pdf = PdfDocument.Open(signed);
        PdfSignatureReport report = Assert.Single(pdf.Signatures).Verify(new PdfVerifyOptions { TrustAnchors = [ca.Root], Revocation = PdfRevocationSource.Offline(crls: [crl]) });
        Assert.Equal(PdfVerdict.Valid, report.Revocation);
        Assert.True(report.IsValid);
    }

    [Fact]
    public async Task ValidationDataIsAddedToAnExistingSignature()
    {
        using TestCertificateAuthority ca = new();
        using HttpClient http = new(ca, disposeHandler: false);
        PdfSignOptions options = new() { Signer = PdfSigner.FromKey(ca.SignerKey, ca.Signer, [ca.Root]), Level = PadesLevel.BaselineT, Timestamp = TestCertificateAuthority.Timestamps(http) };
        byte[] signed = await SigningTests.Sign(SignedFiles.Base, "Sig", options);
        using MemoryStream output = new();
        using (PdfDocument pdf = PdfDocument.Open(signed))
        {
            await pdf.AddValidationDataAsync(PdfRevocationSource.Online(http), output, TestCertificateAuthority.Timestamps(http), TestContext.Current.CancellationToken);
            Assert.True(pdf.IsReadOnly);
        }

        using PdfDocument upgraded = PdfDocument.Open(output.ToArray());
        Assert.Equal(2, upgraded.Signatures.Count);
        Assert.True(upgraded.Signatures[0].Verify(new PdfVerifyOptions { TrustAnchors = [ca.Root] }).IsValid);
    }

    [Fact]
    public async Task LongTermLevelsNeedARevocationSource()
    {
        using TestCertificateAuthority ca = new();
        using HttpClient http = new(ca, disposeHandler: false);
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Base);
        PdfSignOptions options = new() { Signer = PdfSigner.FromKey(ca.SignerKey, ca.Signer, [ca.Root]), Level = PadesLevel.BaselineLT, Timestamp = TestCertificateAuthority.Timestamps(http) };
        await Assert.ThrowsAsync<ArgumentException>(() => pdf.SignAsync("Sig", options, new MemoryStream(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => pdf.PrepareSignatureAsync("Sig", options with { Revocation = PdfRevocationSource.Online(http) }, new MemoryStream(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PyhankosLongTermFilesVerifyOffline()
    {
        using X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(SignedFiles.PathOf("root.crt"));
        PdfVerifyOptions options = new() { TrustAnchors = [root], VerificationTime = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero) };
        foreach (string file in (string[])["pyhanko-blt-rsa.pdf", "pyhanko-blta-rsa.pdf"])
        {
            using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf(file));
            PdfSignatureReport report = pdf.Signatures[0].Verify(options);
            Assert.True(report.IsValid, $"{file}: {string.Join("; ", report.Problems)}");
        }
    }

    [Fact]
    public async Task OcspAndCrlReadersCheckTheirSigner()
    {
        using TestCertificateAuthority ca = new();
        using HttpClient http = new(ca, disposeHandler: false);
        byte[] crl = await http.GetByteArrayAsync(new Uri(TestCertificateAuthority.Base, "crl"), TestContext.Current.CancellationToken);
        Assert.Equal(RevocationStatus.Good, Crl.Read(crl, ca.Signer, ca.Root)!.Status);
        Assert.Equal(RevocationStatus.Revoked, Crl.Read(crl, ca.Revoked, ca.Root)!.Status);
        Assert.Null(Crl.Read(crl, ca.Signer, ca.Signer));

        using ByteArrayContent body = new(Ocsp.Request(ca.Signer, ca.Root, [1, 2, 3]));
        using HttpResponseMessage response = await http.PostAsync(new Uri(TestCertificateAuthority.Base, "ocsp"), body, TestContext.Current.CancellationToken);
        byte[] ocsp = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RevocationStatus.Good, Ocsp.Read(ocsp, ca.Signer, ca.Root, out X509Certificate2? responder)!.Status);
        Assert.Equal(ca.Root.Thumbprint, responder!.Thumbprint);
        Assert.Null(Ocsp.Read(ocsp, ca.Revoked, ca.Root, out _));
        Assert.Null(Ocsp.Read(ocsp, ca.Signer, ca.Tsa, out _));
        Assert.Null(Ocsp.Read(new byte[] { 0x30, 0x03, 0x0A, 0x01, 0x01 }, ca.Signer, ca.Root, out _));
    }
}
