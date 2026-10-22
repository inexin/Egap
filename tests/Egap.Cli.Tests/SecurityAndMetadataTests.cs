using System.Text;
using System.Text.Json;

using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class SecurityAndMetadataTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void EncryptThenDecryptRoundTrips()
    {
        string plain = _tool.Copy("encrypted/plain.pdf");
        _tool.Environment["OWNER"] = "owner-secret";
        _tool.Environment["USER"] = "user-secret";
        ToolRun run = _tool.Run("encrypt", plain, "-o", "locked.pdf", "--user-password-env", "USER", "--owner-password-env", "OWNER", "--allow", "print,copy-content");
        Assert.Equal(ExitCodes.Success, run.Code);
        using (PdfDocument locked = PdfDocument.Open(_tool.PathOf("locked.pdf"), new PdfReadOptions { Password = "user-secret" }))
        {
            Assert.True(locked.IsEncrypted);
            Assert.Equal(PdfPermissions.Print | PdfPermissions.CopyContent, locked.Permissions);
        }

        Assert.Equal(ExitCodes.Failed, _tool.Run("info", "locked.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("decrypt", "locked.pdf", "-o", "open.pdf", "--password-env", "OWNER").Code);
        using PdfDocument open = PdfDocument.Open(_tool.PathOf("open.pdf"));
        Assert.False(open.IsEncrypted);
    }

    [Fact]
    public void EncryptNeedsAnOwnerPassword()
    {
        string plain = _tool.Copy("encrypted/plain.pdf");
        ToolRun run = _tool.Run("encrypt", plain, "-o", "locked.pdf");
        Assert.Equal(ExitCodes.Usage, run.Code);
        Assert.Contains("owner password", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownPermissionIsAUsageError()
    {
        string plain = _tool.Copy("encrypted/plain.pdf");
        _tool.Environment["OWNER"] = "o";
        Assert.Equal(ExitCodes.Usage, _tool.Run("encrypt", plain, "-o", "x.pdf", "--owner-password-env", "OWNER", "--allow", "fly").Code);
    }

    [Fact]
    public void SetShowAndStripInformation()
    {
        string plain = _tool.Copy("encrypted/plain.pdf");
        Assert.Equal(ExitCodes.Success, _tool.Run("metadata", "set", plain, "-o", "m.pdf", "--title", "Annual report", "--author", "Lars", "--created", "2026-01-02T03:04:05Z").Code);
        ToolRun show = _tool.Run("metadata", "show", "m.pdf", "--json", "--xmp", "--outline");
        Assert.Equal(ExitCodes.Success, show.Code);
        Assert.Empty(Schema.Check("egap-metadata-1.json", show.Text));
        using (JsonDocument json = JsonDocument.Parse(show.Text))
        {
            JsonElement info = json.RootElement.GetProperty("files")[0].GetProperty("info");
            Assert.Equal("Annual report", info.GetProperty("title").GetString());
            Assert.Equal("Lars", info.GetProperty("author").GetString());
            Assert.StartsWith("2026-01-02T03:04:05", info.GetProperty("created").GetString(), StringComparison.Ordinal);
        }

        Assert.Equal(ExitCodes.Success, _tool.Run("metadata", "strip", "m.pdf", "--in-place").Code);
        using PdfDocument stripped = PdfDocument.Open(_tool.PathOf("m.pdf"));
        Assert.Null(stripped.Metadata.Title);
        Assert.False(stripped.Metadata.HasXmp);
    }

    [Fact]
    public void AnOutlineRoundTripsThroughJson()
    {
        _tool.Write("three.md", "# One\n\nA.\n\n# Two\n\nB.\n"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "three.md", "-o", "doc.pdf").Code);
        string outline = """{ "egap-outline": 1, "items": [ { "title": "Start", "page": 1, "bold": true, "children": [ { "title": "Inner", "page": 1 } ] } ] }""";
        _tool.Write("outline.json", Encoding.UTF8.GetBytes(outline));
        Assert.Equal(ExitCodes.Success, _tool.Run("metadata", "set", "doc.pdf", "--outline", "outline.json", "-o", "out.pdf").Code);
        ToolRun show = _tool.Run("metadata", "show", "out.pdf", "--outline", "--json");
        _tool.Write("again.json", Encoding.UTF8.GetBytes(show.Text));
        Assert.Equal(ExitCodes.Success, _tool.Run("metadata", "set", "doc.pdf", "--outline", "again.json", "-o", "again.pdf").Code);
        using PdfDocument again = PdfDocument.Open(_tool.PathOf("again.pdf"));
        Assert.Equal(["Start", "Inner"], again.Outline.GetAllItems().Select(i => i.Title));
        Assert.True(again.Outline.Children[0].IsBold);
    }

    [Fact]
    public void AnOutlineToAMissingPageFailsTheFile()
    {
        string plain = _tool.Copy("encrypted/plain.pdf");
        _tool.Write("outline.json", """{ "egap-outline": 1, "items": [ { "title": "Far", "page": 9 } ] }"""u8.ToArray());
        Assert.Equal(ExitCodes.Failed, _tool.Run("metadata", "set", plain, "--outline", "outline.json", "-o", "x.pdf").Code);
    }
}
