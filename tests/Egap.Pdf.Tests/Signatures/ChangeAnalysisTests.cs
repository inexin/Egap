using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// Changes since signing (Phase 5 task 8): each later revision judged against DocMDP, with the shadow attacks (hide,
/// replace, hide-and-replace) reported as disallowed and long-term validation data allowed at every level.
/// </summary>
public sealed class ChangeAnalysisTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pyhanko-two-signatures.pdf", 0)]
    [InlineData("pyhanko-blta-rsa.pdf", 0)]
    [InlineData("pyhanko-blt-rsa.pdf", 0)]
    public void PyhankoUpdatesAreAllowed(string file, int signature)
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf(file));
        PdfSignatureReport report = pdf.Signatures[signature].Verify(new PdfVerifyOptions { VerificationTime = When });
        Assert.True(report.ChangesSince == PdfVerdict.Valid, string.Join(" | ", report.Changes));
        Assert.NotEmpty(report.Changes);
        Assert.All(report.Changes, c => Assert.Equal(PdfChangeVerdict.Allowed, c.Verdict));
    }

    [Theory]
    [InlineData("pyhanko-form-certify-1.pdf", PdfVerdict.Invalid)]
    [InlineData("pyhanko-form-certify-2.pdf", PdfVerdict.Valid)]
    [InlineData("pyhanko-form-certify-3.pdf", PdfVerdict.Valid)]
    [InlineData("pyhanko-form-approval.pdf", PdfVerdict.Valid)]
    public void FillingAFieldIsJudgedByTheCertificationLevel(string file, PdfVerdict expected)
    {
        byte[] filled = Update(SignedFiles.PathOf(file), pdf => ((PdfTextField)pdf.Form.GetAllFields().First(f => f.FullName == "Name")).Value = "Ada");
        Assert.Equal(expected, ChangesSince(filled));
    }

    [Theory]
    [InlineData("pyhanko-form-certify-1.pdf", PdfVerdict.Invalid)]
    [InlineData("pyhanko-form-certify-2.pdf", PdfVerdict.Invalid)]
    [InlineData("pyhanko-form-certify-3.pdf", PdfVerdict.Valid)]
    [InlineData("pyhanko-form-approval.pdf", PdfVerdict.Valid)]
    public void AddingAnAnnotationNeedsLevelThree(string file, PdfVerdict expected)
    {
        byte[] annotated = Update(SignedFiles.PathOf(file), pdf => pdf.Pages[0].Annotations.Add(new PdfTextAnnotation(new PdfRectangle(10, 10, 30, 30), "Note")));
        Assert.Equal(expected, ChangesSince(annotated));
    }

    [Theory]
    [InlineData("pyhanko-form-certify-2.pdf")]
    [InlineData("pyhanko-form-approval.pdf")]
    public async Task SigningAgainIsAllowedFromLevelTwo(string file)
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            byte[] twice = await SigningTests.Sign(File.ReadAllBytes(SignedFiles.PathOf(file)), "Second", new PdfSignOptions { Signer = PdfSigner.FromKey(key, certificate) });
            Assert.Equal(PdfVerdict.Valid, ChangesSince(twice));
        }

        // With the visible default appearance too.
        (X509Certificate2 other, AsymmetricAlgorithm otherKey) = CmsTests.SelfSigned("p256");
        using (other)
        using (otherKey)
        {
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(otherKey, other), Appearance = PdfSignatureAppearance.Default(0, new Egami.Drawing.RectF(300, 50, 200, 50)) };
            byte[] visible = await SigningTests.Sign(File.ReadAllBytes(SignedFiles.PathOf(file)), "Visible", options);
            Assert.Equal(PdfVerdict.Valid, ChangesSince(visible));
        }
    }

    [Fact]
    public void SigningAgainBreaksLevelOne()
    {
        // pyHanko's own second signature over a certification that allows no changes.
        byte[] filled = Update(SignedFiles.PathOf("pyhanko-certify-1.pdf"), pdf => pdf.Catalog["Lang"] = PdfString.FromText("en"));
        Assert.Equal(PdfVerdict.Invalid, ChangesSince(filled));
    }

    [Fact]
    public async Task LongTermDataIsAllowedEvenAtLevelOne()
    {
        byte[] withDss = Update(SignedFiles.PathOf("pyhanko-form-certify-1.pdf"), pdf =>
        {
            PdfReference certificate = pdf.Objects.Add(new PdfStream(new PdfDictionary()));
            pdf.Catalog["DSS"] = pdf.Objects.Add(new PdfDictionary { ["Certs"] = new PdfArray { certificate } });
        });
        Assert.Equal(PdfVerdict.Valid, ChangesSince(withDss));

        using TestTimestampAuthority tsa = new();
        using HttpClient http = new(tsa, disposeHandler: false);
        using PdfDocument pdf = PdfDocument.Open(withDss);
        using MemoryStream stamped = new();
        await pdf.TimestampAsync(TestTimestampAuthority.Client(http), stamped, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PdfVerdict.Valid, ChangesSince(stamped.ToArray()));
    }

    [Theory]
    [InlineData("pyhanko-form-certify-3.pdf")]
    [InlineData("pyhanko-form-approval.pdf")]
    public void ShadowHideNewContentIsDisallowed(string file)
    {
        // Hide: content drawn over the page after signing.
        byte[] covered = Update(SignedFiles.PathOf(file), pdf => pdf.Pages[0].AddContent("1 1 1 rg 0 0 595 842 re f"u8));
        PdfSignatureReport report = Report(covered);
        Assert.Equal(PdfVerdict.Invalid, report.ChangesSince);
        Assert.Contains(report.Changes, c => c.Verdict == PdfChangeVerdict.Disallowed);
    }

    [Theory]
    [InlineData("pyhanko-form-certify-3.pdf")]
    [InlineData("pyhanko-form-approval.pdf")]
    public void ShadowReplaceAFontIsDisallowed(string file)
    {
        // Replace: the font the signed text uses is redefined.
        byte[] replaced = Update(SignedFiles.PathOf(file), pdf => ((PdfDictionary)pdf.Objects.Get(5))["BaseFont"] = PdfName.Get("Courier"));
        Assert.Equal(PdfVerdict.Invalid, ChangesSince(replaced));
    }

    [Theory]
    [InlineData("pyhanko-form-certify-3.pdf")]
    [InlineData("pyhanko-form-approval.pdf")]
    public void ShadowHideAndReplaceTheContentStreamIsDisallowed(string file)
    {
        // Hide-and-replace: the signed content stream object itself is rewritten in the update.
        byte[] rewritten = Update(SignedFiles.PathOf(file), pdf => ((PdfStream)pdf.Objects.Get(4)).SetDecodedBytes("BT /F1 24 Tf 72 760 Td (Pay 1000000) Tj ET"u8));
        PdfSignatureReport report = Report(rewritten);
        Assert.Equal(PdfVerdict.Invalid, report.ChangesSince);
        Assert.Contains(report.Changes, c => c.ObjectNumber == 4 && c.Verdict == PdfChangeVerdict.Disallowed);
    }

    [Fact]
    public void ShadowReplaceAFieldsLookWithoutItsValueIsJudgedAsFilling()
    {
        // Changing a field's appearance stream is what filling does; at level 1 it is still disallowed.
        byte[] look = Update(SignedFiles.PathOf("pyhanko-form-certify-1.pdf"), pdf => ((PdfStream)pdf.Objects.Get(7)).SetDecodedBytes("/Tx BMC BT /Helv 12 Tf 2 6 Td (Approved) Tj ET EMC"u8));
        Assert.Equal(PdfVerdict.Invalid, ChangesSince(look));
    }

    [Fact]
    public void AnObjectNothingUsesIsSuspicious()
    {
        byte[] orphan = Update(SignedFiles.PathOf("pyhanko-form-certify-2.pdf"), pdf => pdf.Objects.Add(new PdfDictionary { ["Hidden"] = PdfString.FromText("payload") }));
        PdfSignatureReport report = Report(orphan);
        Assert.Equal(PdfVerdict.Indeterminate, report.ChangesSince);
        Assert.Contains(report.Changes, c => c.Verdict == PdfChangeVerdict.Suspicious && c.Description.Contains("nothing uses it", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewPageIsDisallowed()
    {
        byte[] grown = Update(SignedFiles.PathOf("pyhanko-form-certify-3.pdf"), pdf => pdf.Pages.AddBlank());
        Assert.Equal(PdfVerdict.Invalid, ChangesSince(grown));
    }

    [Fact]
    public void TheSignedFieldItselfCannotChange()
    {
        byte[] changed = Update(SignedFiles.PathOf("pyhanko-form-approval.pdf"), pdf => pdf.Form.GetAllFields().OfType<PdfSignatureField>().Single().Dictionary["TU"] = PdfString.FromText("tooltip"));
        Assert.Equal(PdfVerdict.Invalid, ChangesSince(changed));
    }

    [Fact]
    public void AnUnchangedRewriteIsNoChange()
    {
        // Saving an object again with the same value is not a change at all.
        byte[] same = Update(SignedFiles.PathOf("pyhanko-form-certify-1.pdf"), pdf =>
        {
            PdfDictionary page = pdf.Pages[0].Dictionary;
            page["Rotate"] = new PdfInteger(0);
            page.Remove("Rotate");
        });
        PdfSignatureReport report = Report(same);
        Assert.True(report.ChangesSince is PdfVerdict.Valid or PdfVerdict.NotApplicable, report.ToString());
    }

    private static PdfSignatureReport Report(byte[] file)
    {
        using PdfDocument pdf = PdfDocument.Open(file);
        return pdf.Signatures[0].Verify(new PdfVerifyOptions { VerificationTime = When });
    }

    private static PdfVerdict ChangesSince(byte[] file)
    {
        return Report(file).ChangesSince;
    }

    /// <summary>The file with one incremental update made by <paramref name="change"/>.</summary>
    private static byte[] Update(string path, Action<PdfDocument> change)
    {
        using PdfDocument pdf = PdfDocument.Open(File.ReadAllBytes(path));
        change(pdf);
        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { UpdateModificationDate = false });
        return output.ToArray();
    }
}
