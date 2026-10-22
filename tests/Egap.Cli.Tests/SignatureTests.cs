using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class SignatureTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void SignThenVerifyWithTheSignerAsTrustedRoot()
    {
        MakePdf("a.pdf");
        MakeCertificate();
        _tool.Environment["CERT_PASSWORD"] = "pfx-secret";
        ToolRun sign = _tool.Run("sign", "a.pdf", "-o", "signed.pdf", "--cert", "me.pfx", "--cert-password-env", "CERT_PASSWORD", "--reason", "Approved", "--visible", "1:20mm,20mm,60mm,20mm");
        Assert.Equal(ExitCodes.Success, sign.Code);
        using (PdfDocument signed = PdfDocument.Open(_tool.PathOf("signed.pdf")))
        {
            Assert.Single(signed.Signatures);
            Assert.Equal("Approved", signed.Signatures[0].Reason);
            Assert.Equal(2, signed.Revisions.Count);
        }

        ToolRun verify = _tool.Run("verify", "signed.pdf", "--trust", "me.cer", "--json");
        Assert.Equal(ExitCodes.Success, verify.Code);
        Assert.Empty(Schema.Check("egap-verify-1.json", verify.Text));

        Assert.Equal(ExitCodes.Failed, _tool.Run("verify", "signed.pdf").Code);
    }

    [Fact]
    public void AChangeAfterSigningFailsVerify()
    {
        MakePdf("a.pdf");
        MakeCertificate();
        _tool.Environment["CERT_PASSWORD"] = "pfx-secret";
        Assert.Equal(ExitCodes.Success, _tool.Run("sign", "a.pdf", "-o", "signed.pdf", "--cert", "me.pfx", "--cert-password-env", "CERT_PASSWORD", "--certify", "no-changes").Code);
        Assert.Equal(ExitCodes.Failed, _tool.Run("pages", "signed.pdf", "--rotate", "90", "-o", "changed.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("pages", "signed.pdf", "--rotate", "90", "-o", "changed.pdf", "--allow-invalidating-signatures").Code);
        Assert.Equal(ExitCodes.Failed, _tool.Run("verify", "changed.pdf", "--trust", "me.cer").Code);
    }

    [Fact]
    public void VerifiesPyHankoFiles()
    {
        string signed = _tool.Copy("signatures/pyhanko-bb-rsa.pdf");
        _tool.Copy("signatures/root.crt");
        _tool.Copy("signatures/inter.crt");
        ToolRun run = _tool.Run("verify", signed, "--trust", "root.crt", "--extra", "inter.crt");
        Assert.Contains("valid", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsignedFileFailsVerify()
    {
        MakePdf("a.pdf");
        ToolRun run = _tool.Run("verify", "a.pdf");
        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Contains("no signatures", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALevelAboveBNeedsATimestampAuthority()
    {
        MakePdf("a.pdf");
        MakeCertificate();
        _tool.Environment["CERT_PASSWORD"] = "pfx-secret";
        Assert.Equal(ExitCodes.Usage, _tool.Run("sign", "a.pdf", "-o", "s.pdf", "--cert", "me.pfx", "--cert-password-env", "CERT_PASSWORD", "--level", "b-t").Code);
    }

    [Fact]
    public void PdfaConvertThenCheck()
    {
        MakePdf("a.pdf");
        Assert.Equal(ExitCodes.Failed, _tool.Run("pdfa", "check", "a.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("pdfa", "convert", "a.pdf", "--level", "2b", "-o", "a2b.pdf").Code);
        ToolRun check = _tool.Run("pdfa", "check", "a2b.pdf", "--json");
        Assert.Equal(ExitCodes.Success, check.Code);
        Assert.Empty(Schema.Check("egap-pdfa-1.json", check.Text));
        Assert.Contains("PDF/A-2b", check.Text, StringComparison.Ordinal);
    }

    private void MakePdf(string name)
    {
        _tool.Write("source.md", Encoding.UTF8.GetBytes("# Contract\n\nThe parties agree.\n"));
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "source.md", "-o", name).Code);
    }

    /// <summary>A self-signed RSA certificate as me.pfx (password pfx-secret) and me.cer.</summary>
    private void MakeCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=Egap CLI Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.KeyCertSign, critical: true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _tool.Write("me.pfx", certificate.Export(X509ContentType.Pkcs12, "pfx-secret"));
        _tool.Write("me.cer", certificate.Export(X509ContentType.Cert));
    }
}
