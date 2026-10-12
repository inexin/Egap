using System.Text;

namespace Egap.Pdf.Tests.Security;

public sealed class DecryptionTests
{
    private static string File(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "files", "encrypted", name);
    }

    public static TheoryData<string, string?> WithPasswords => new()
    {
        { "rc4-40.pdf", "user" },
        { "rc4-128.pdf", "user" },
        { "aes-128.pdf", "user" },
        { "aes-128-clear-metadata.pdf", "user" },
        { "aes-128-object-streams.pdf", null },
        { "aes-256-r5.pdf", "user" },
        { "aes-256.pdf", "user" },
        { "aes-256-empty-user.pdf", null },
        { "aes-256-object-streams.pdf", "user" },
        { "rc4-40-empty.pdf", null },
    };

    public static TheoryData<string> OwnerFiles => ["rc4-40.pdf", "rc4-128.pdf", "aes-128.pdf", "aes-256-r5.pdf", "aes-256.pdf", "aes-256-object-streams.pdf", "aes-256-empty-user.pdf"];

    /// <summary>The content every encrypted file must decrypt to: the plain file's.</summary>
    private static void AssertPlainContent(PdfDocument pdf)
    {
        PdfDictionary info = pdf.Trailer.Get<PdfDictionary>("Info")!;
        Assert.Equal("Encrypted sample", info.GetText("Title"));
        Assert.Equal("Ω", info.GetText("Author"));
        PdfDictionary page = pdf.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!;
        Assert.Equal("BT /F1 24 Tf 72 700 Td (Secret text) Tj ET"u8.ToArray(), page.Get<PdfStream>("Contents")!.GetDecodedBytes());
        Assert.Contains("Plain metadata", Encoding.UTF8.GetString(pdf.Catalog.Get<PdfStream>("Metadata")!.GetDecodedBytes()), StringComparison.Ordinal);
        Assert.Equal("Helvetica", page.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("F1")!.GetName("BaseFont")!.Value);
    }

    [Theory]
    [MemberData(nameof(WithPasswords))]
    public void UserPasswordDecryptsEverything(string name, string? password)
    {
        using PdfDocument pdf = PdfDocument.Open(File(name), new PdfReadOptions { Password = password, Strict = true });

        Assert.True(pdf.IsEncrypted);
        Assert.False(pdf.IsOwner);
        AssertPlainContent(pdf);
        Assert.Empty(pdf.Warnings);
    }

    [Theory]
    [MemberData(nameof(OwnerFiles))]
    public void OwnerPasswordDecryptsAndAllowsEverything(string name)
    {
        using PdfDocument pdf = PdfDocument.Open(File(name), new PdfReadOptions { Password = "owner", Strict = true });

        Assert.True(pdf.IsOwner);
        Assert.Equal(PdfPermissions.All, pdf.Permissions);
        AssertPlainContent(pdf);
    }

    [Theory]
    [InlineData("rc4-40.pdf")]
    [InlineData("rc4-128.pdf")]
    [InlineData("aes-128.pdf")]
    [InlineData("aes-256-r5.pdf")]
    [InlineData("aes-256.pdf")]
    public void MissingOrWrongPasswordThrows(string name)
    {
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(File(name)));
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(File(name), new PdfReadOptions { Password = "wrong" }));
    }

    [Fact]
    public void PermissionsAreReportedNotEnforced()
    {
        using PdfDocument pdf = PdfDocument.Open(File("aes-256-empty-user.pdf"));

        Assert.False(pdf.Permissions.HasFlag(PdfPermissions.Print));
        Assert.False(pdf.Permissions.HasFlag(PdfPermissions.CopyContent));
        Assert.True(pdf.Permissions.HasFlag(PdfPermissions.Modify));

        // Reading is never refused: the flags are for the caller to honour (design Q5).
        AssertPlainContent(pdf);
    }

    [Fact]
    public void UnencryptedFileAllowsEverything()
    {
        using PdfDocument pdf = PdfDocument.Open(File("plain.pdf"));

        Assert.False(pdf.IsEncrypted);
        Assert.Equal(PdfPermissions.All, pdf.Permissions);
        AssertPlainContent(pdf);
    }

    [Fact]
    public void ClearMetadataStaysReadableAsStored()
    {
        using PdfDocument pdf = PdfDocument.Open(File("aes-128-clear-metadata.pdf"), new PdfReadOptions { Password = "user" });

        // The metadata stream was not encrypted, so its stored bytes are the plain XML.
        Assert.Contains("Plain metadata", Encoding.UTF8.GetString(pdf.Catalog.Get<PdfStream>("Metadata")!.GetEncodedBytes()), StringComparison.Ordinal);
    }

    [Fact]
    public void EncryptionDictionaryIsNotDecrypted()
    {
        using PdfDocument pdf = PdfDocument.Open(File("aes-256.pdf"), new PdfReadOptions { Password = "user" });

        PdfDictionary encrypt = pdf.Trailer.Get<PdfDictionary>("Encrypt")!;
        Assert.Equal(48, encrypt.Get<PdfString>("U")!.Bytes.Length);
        Assert.Equal(6, encrypt.GetInt("R"));
    }

    [Fact]
    public void UnsupportedSecurityHandlerThrows()
    {
        byte[] plain = System.IO.File.ReadAllBytes(File("plain.pdf"));
        string text = Encoding.Latin1.GetString(plain).Replace("/Root 1 0 R", "/Root 1 0 R /Encrypt << /Filter /Adobe.PubSec /V 4 /R 4 >>", StringComparison.Ordinal);

        PdfException e = Assert.Throws<PdfException>(() => PdfDocument.Open(Encoding.Latin1.GetBytes(text)));
        Assert.Contains("Adobe.PubSec", e.Message, StringComparison.Ordinal);
    }
}
