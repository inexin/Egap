using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// Certification, locks and the edit guard (Phase 5 task 9): DocMDP and FieldMDP written, seed values honoured, and saves
/// that would break a signature warned about, thrown in strict mode, or let through with AllowInvalidatingSignatures.
/// </summary>
public sealed class CertificationTests
{
    [Theory]
    [InlineData(PdfCertification.NoChanges)]
    [InlineData(PdfCertification.FormFillingAndSigning)]
    [InlineData(PdfCertification.AnnotationsFormFillingAndSigning)]
    public async Task ACertificationSignatureIsWritten(PdfCertification level)
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Author", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate), Certification = level }));
            PdfSignature signature = Assert.Single(pdf.Signatures);
            Assert.Equal(PdfSignatureKind.Certification, signature.Kind);
            Assert.Equal(level, signature.Certification);
            Assert.Same(signature.Dictionary, pdf.Catalog.Get<PdfDictionary>("Perms")!.Get<PdfDictionary>("DocMDP"));
            SigningTests.AssertVerifies(signature, certificate);
        }
    }

    [Fact]
    public async Task ACertificationMustComeFirst()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-form-approval.pdf"));
            ArgumentException e = await Assert.ThrowsAsync<ArgumentException>(() => pdf.SignAsync("Author", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate), Certification = PdfCertification.NoChanges }, new MemoryStream(), TestContext.Current.CancellationToken));
            Assert.Contains("first signature", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ALockedFieldCannotBeFilledAfterwards()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate), FieldLock = new PdfFieldLock(PdfFieldLockAction.Include, ["Name"]) };
            byte[] signed = await SigningTests.Sign(File.ReadAllBytes(SignedFiles.PathOf("base-form.pdf")), "Sig", options);
            using (PdfDocument pdf = PdfDocument.Open(signed))
            {
                PdfFieldLock written = Assert.Single(pdf.Signatures).FieldLock!;
                Assert.Equal(PdfFieldLockAction.Include, written.Action);
                Assert.Equal(["Name"], written.Fields);
            }

            byte[] filled = Fill(signed, "Ada", new PdfSaveOptions { AllowInvalidatingSignatures = true });
            using PdfDocument after = PdfDocument.Open(filled);
            PdfSignatureReport report = Assert.Single(after.Signatures).Verify();
            Assert.Equal(PdfVerdict.Invalid, report.ChangesSince);
            Assert.Contains(report.Changes, c => c.Description.Contains("locked", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheFieldsOwnLockIsWrittenWhenSigned()
    {
        byte[] withField = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(SignedFiles.Signed(fieldEntries: "/Lock << /Type /SigFieldLock /Action /All >>")).Replace("/V 6 0 R", "/V  null", StringComparison.Ordinal));
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(withField, "Sig1", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) }));
            Assert.Equal(PdfFieldLockAction.All, Assert.Single(pdf.Signatures).FieldLock!.Action);
        }
    }

    [Theory]
    [InlineData("/Ff 2 /SubFilter [/adbe.pkcs7.detached]", "formats")]
    [InlineData("/Ff 8 /Reasons [(Approved)]", "reasons")]
    [InlineData("/Ff 8 /Reasons [(.)]", "no reason")]
    [InlineData("/Ff 64 /DigestMethod [/SHA384]", "digests")]
    [InlineData("/TimeStamp << /URL (http://tsa.example) /Ff 1 >>", "timestamp")]
    [InlineData("/MDP << /P 1 >>", "certification")]
    [InlineData("/Ff 32 /AddRevInfo true", "revocation")]
    [InlineData("/Cert << /Ff 1 /Subject [<3003020100>] >>", "signer certificates")]
    [InlineData("/Cert << /Ff 32 /KeyUsage [(XX1XXXXXX)] >>", "key usage")]
    public async Task UnmetSeedValuesStopSigning(string seed, string named)
    {
        byte[] withField = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(SignedFiles.Signed(fieldEntries: $"/SV << /Type /SV {seed} >>")).Replace("/V 6 0 R", "/V  null", StringComparison.Ordinal));
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(withField);
            ArgumentException e = await Assert.ThrowsAsync<ArgumentException>(() => pdf.SignAsync("Sig1", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate), Reason = "Testing" }, new MemoryStream(), TestContext.Current.CancellationToken));
            Assert.Contains(named, e.Message, StringComparison.Ordinal);
            Assert.False(pdf.IsReadOnly);
        }
    }

    [Fact]
    public async Task MetSeedValuesSign()
    {
        const string seed = "/SV << /Type /SV /Ff 74 /SubFilter [/adbe.pkcs7.detached] /Reasons [(Approved)] /DigestMethod [/SHA256 /SHA512] /Cert << /Ff 32 /KeyUsage [(1XXXXXXXX)] >> >>";
        byte[] withField = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(SignedFiles.Signed(fieldEntries: seed)).Replace("/V 6 0 R", "/V  null", StringComparison.Ordinal));
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate), Format = PdfSignatureFormat.Pkcs7, Reason = "Approved" };
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(withField, "Sig1", options));
            SigningTests.AssertVerifies(Assert.Single(pdf.Signatures), certificate);
        }
    }

    [Fact]
    public void ASaveThatBreaksASignatureWarns()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-form-certify-1.pdf"));
        ((PdfTextField)pdf.Form.GetAllFields().First(f => f.FullName == "Name")).Value = "Ada";
        pdf.Save(new MemoryStream(), new PdfSaveOptions { UpdateModificationDate = false });
        PdfWarning warning = Assert.Single(pdf.Warnings, w => w.Code == PdfWarningCode.SignatureInvalidated);
        Assert.Contains("Signature1", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictModeThrowsAndTheOptionLetsItThrough()
    {
        using (PdfDocument strict = PdfDocument.Open(SignedFiles.PathOf("pyhanko-form-certify-1.pdf"), new PdfReadOptions { Strict = true }))
        {
            ((PdfTextField)strict.Form.GetAllFields().First(f => f.FullName == "Name")).Value = "Ada";
            PdfException e = Assert.Throws<PdfException>(() => strict.Save(new MemoryStream(), new PdfSaveOptions { UpdateModificationDate = false }));
            Assert.Equal(PdfWarningCode.SignatureInvalidated, e.Code);
        }

        using PdfDocument allowed = PdfDocument.Open(SignedFiles.PathOf("pyhanko-form-certify-1.pdf"), new PdfReadOptions { Strict = true });
        ((PdfTextField)allowed.Form.GetAllFields().First(f => f.FullName == "Name")).Value = "Ada";
        allowed.Save(new MemoryStream(), new PdfSaveOptions { UpdateModificationDate = false, AllowInvalidatingSignatures = true });
        Assert.DoesNotContain(allowed.Warnings, w => w.Code == PdfWarningCode.SignatureInvalidated);
    }

    [Fact]
    public void AllowedChangesAndUnsignedFilesSaveQuietly()
    {
        using (PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-form-certify-2.pdf")))
        {
            ((PdfTextField)pdf.Form.GetAllFields().First(f => f.FullName == "Name")).Value = "Ada";
            pdf.Save(new MemoryStream(), new PdfSaveOptions { UpdateModificationDate = false });
            Assert.DoesNotContain(pdf.Warnings, w => w.Code == PdfWarningCode.SignatureInvalidated);
        }

        using PdfDocument plain = PdfDocument.Open(SignedFiles.Base);
        plain.Catalog["Lang"] = PdfString.FromText("en");
        plain.Save(new MemoryStream(), new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
        Assert.DoesNotContain(plain.Warnings, w => w.Code == PdfWarningCode.SignatureInvalidated);
    }

    [Fact]
    public void RewritingASignedFileWarns()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-bb-rsa.pdf"));
        pdf.Save(new MemoryStream(), new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.SignatureInvalidated && w.Message.Contains("Rewriting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SigningACertifiedNoChangesFileWarns()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using (PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-certify-1.pdf")))
            {
                await pdf.SignAsync("Second", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) }, new MemoryStream(), TestContext.Current.CancellationToken);
                Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.SignatureInvalidated);
            }

            using (PdfDocument strict = PdfDocument.Open(SignedFiles.PathOf("pyhanko-certify-1.pdf"), new PdfReadOptions { Strict = true }))
            {
                await Assert.ThrowsAsync<PdfException>(() => strict.SignAsync("Second", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) }, new MemoryStream(), TestContext.Current.CancellationToken));
            }

            using PdfDocument allowed = PdfDocument.Open(SignedFiles.PathOf("pyhanko-certify-1.pdf"), new PdfReadOptions { Strict = true });
            await allowed.SignAsync("Second", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate), AllowInvalidatingSignatures = true }, new MemoryStream(), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task EgapsCertificationAllowsEgapsSecondSignature()
    {
        (X509Certificate2 first, AsymmetricAlgorithm firstKey) = CmsTests.SelfSigned("rsa", "CN=Author");
        (X509Certificate2 second, AsymmetricAlgorithm secondKey) = CmsTests.SelfSigned("p256", "CN=Approver");
        using (first)
        using (firstKey)
        using (second)
        using (secondKey)
        {
            byte[] certified = await SigningTests.Sign(File.ReadAllBytes(SignedFiles.PathOf("base-form.pdf")), "Author", new PdfSignOptions { Signer = PdfSigner.FromKey(firstKey, first), Certification = PdfCertification.FormFillingAndSigning });
            byte[] filled = Fill(certified, "Ada", new PdfSaveOptions());
            byte[] approved = await SigningTests.Sign(filled, "Approver", new PdfSignOptions { Signer = PdfSigner.FromKey(secondKey, second) });
            using PdfDocument pdf = PdfDocument.Open(approved);
            Assert.Equal(PdfVerdict.Valid, pdf.Signatures[0].Verify(new PdfVerifyOptions { TrustAnchors = [first] }).ChangesSince);
            Assert.All(pdf.Signatures, s => Assert.Equal(PdfVerdict.Valid, s.Verify().Integrity));
        }
    }

    private static byte[] Fill(byte[] file, string value, PdfSaveOptions options)
    {
        using PdfDocument pdf = PdfDocument.Open(file);
        ((PdfTextField)pdf.Form.GetAllFields().First(f => f.FullName == "Name")).Value = value;
        using MemoryStream output = new();
        pdf.Save(output, options with { UpdateModificationDate = false });
        return output.ToArray();
    }
}
