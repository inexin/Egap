using System.Text.Json;

namespace Egap.Cli.Tests;

public sealed class InfoTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void SummaryNamesVersionPagesAndPaper()
    {
        string file = _tool.Copy("encrypted/plain.pdf");
        ToolRun run = _tool.Run("info", file);
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal("plain.pdf: PDF 1.7, 1 page, Letter portrait (612x792 pt)\n", run.Text);
    }

    [Theory]
    [InlineData("rc4-40.pdf", "rc4-40")]
    [InlineData("rc4-128.pdf", "rc4-128")]
    [InlineData("aes-128.pdf", "aes-128")]
    [InlineData("aes-256.pdf", "aes-256")]
    public void EncryptedInputsOpenWithAPasswordFromTheEnvironment(string name, string encryption)
    {
        string file = _tool.Copy("encrypted/" + name);
        _tool.Environment["PDF_PASSWORD"] = "user";
        ToolRun run = _tool.Run("info", file, "--password-env", "PDF_PASSWORD", "--json");
        Assert.Equal(ExitCodes.Success, run.Code);
        using JsonDocument json = JsonDocument.Parse(run.Text);
        JsonElement info = json.RootElement.GetProperty("files")[0];
        Assert.True(info.GetProperty("encrypted").GetBoolean());
        Assert.Equal(encryption, info.GetProperty("encryption").GetString());
    }

    [Fact]
    public void AMissingPasswordFailsTheFileWithAHint()
    {
        string file = _tool.Copy("encrypted/aes-256.pdf");
        ToolRun run = _tool.Run("info", file);
        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Contains("--password-env", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void APlainPasswordWorksButWarns()
    {
        string file = _tool.Copy("encrypted/aes-256.pdf");
        ToolRun run = _tool.Run("info", file, "--password", "user");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("shell history", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void APasswordFileGivesItsFirstLine()
    {
        string file = _tool.Copy("encrypted/aes-256.pdf");
        _tool.Write("secret.txt", "user\nignored\n"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("info", file, "--password-file", "secret.txt").Code);
    }

    [Fact]
    public void JsonListsSignatures()
    {
        string file = _tool.Copy("signatures/pyhanko-blta-rsa.pdf");
        ToolRun run = _tool.Run("info", file, "--json");
        using JsonDocument json = JsonDocument.Parse(run.Text);
        JsonElement info = json.RootElement.GetProperty("files")[0];
        Assert.Equal(1, json.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(["approval", "document-timestamp"], info.GetProperty("signatures").EnumerateArray().Select(s => s.GetProperty("kind").GetString()));
    }

    [Theory]
    [InlineData("encrypted/plain.pdf")]
    [InlineData("encrypted/aes-256-empty-user.pdf")]
    [InlineData("signatures/pyhanko-blta-rsa.pdf")]
    [InlineData("signatures/pyhanko-form-certify-2.pdf")]
    [InlineData("fuzz/layout-footer-below-page.pdf")]
    public void JsonMatchesTheSchema(string file)
    {
        string name = _tool.Copy(file);
        foreach (string[] extra in (string[][])[[], ["--all"]])
        {
            ToolRun run = _tool.Run(["info", name, "--json", .. extra]);
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Empty(Schema.Check("egap-info-1.json", run.Text));
        }
    }

    [Fact]
    public void ARepairedFileWarnsAndStrictFailsIt()
    {
        string file = _tool.Copy("fuzz/calgray-gamma-object.pdf");
        ToolRun run = _tool.Run("info", file);
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("warning", run.Error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Failed, _tool.Run("info", file, "--strict").Code);
    }

    [Fact]
    public void ALimitFailsWithExitCodeThree()
    {
        string file = _tool.Copy("encrypted/plain.pdf");
        Assert.Equal(ExitCodes.Limit, _tool.Run("info", file, "--limit", "objects=2").Code);
    }

    [Fact]
    public void AnUnknownLimitIsAUsageError()
    {
        string file = _tool.Copy("encrypted/plain.pdf");
        ToolRun run = _tool.Run("info", file, "--limit", "pixels=2");
        Assert.Equal(ExitCodes.Usage, run.Code);
        Assert.Contains("objects, decoded, ratio, depth, xref", run.Error, StringComparison.Ordinal);
    }
}
