using System.Text;

namespace Egap.Pdf.Tests.Security;

public sealed class EncryptionTests
{
    private static string File(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "files", "encrypted", name);
    }

    private static readonly PdfSaveOptions Rewrite = new() { Mode = PdfSaveMode.Rewrite, Deterministic = true };

    public static TheoryData<string, string?> Files => new()
    {
        { "rc4-40.pdf", "user" },
        { "rc4-128.pdf", "user" },
        { "aes-128.pdf", "user" },
        { "aes-128-clear-metadata.pdf", "user" },
        { "aes-128-object-streams.pdf", null },
        { "aes-256-r5.pdf", "user" },
        { "aes-256.pdf", "user" },
        { "aes-256-object-streams.pdf", "user" },
    };

    private static byte[] Save(PdfDocument pdf, PdfSaveOptions? options = null)
    {
        using MemoryStream output = new();
        pdf.Save(output, options);
        return output.ToArray();
    }

    private static void AssertPlainContent(PdfDocument pdf)
    {
        Assert.Equal("Encrypted sample", pdf.Trailer.Get<PdfDictionary>("Info")!.GetText("Title"));
        Assert.Equal("Ω", pdf.Trailer.Get<PdfDictionary>("Info")!.GetText("Author"));
        PdfDictionary page = pdf.Catalog.Get<PdfDictionary>("Pages")!.Get<PdfArray>("Kids")!.Get<PdfDictionary>(0)!;
        Assert.Equal("BT /F1 24 Tf 72 700 Td (Secret text) Tj ET"u8.ToArray(), page.Get<PdfStream>("Contents")!.GetDecodedBytes());
        Assert.Contains("Plain metadata", Encoding.UTF8.GetString(pdf.Catalog.Get<PdfStream>("Metadata")!.GetDecodedBytes()), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void RewriteKeepsTheEncryption(string name, string? password)
    {
        using PdfDocument original = PdfDocument.Open(File(name), new PdfReadOptions { Password = password });
        int revision = (int)original.Trailer.Get<PdfDictionary>("Encrypt")!.GetInt("R")!;

        foreach (PdfCompression compression in new[] { PdfCompression.None, PdfCompression.Compact })
        {
            byte[] saved = Save(original, Rewrite with { Compression = compression });

            Assert.DoesNotContain("Secret text", Encoding.Latin1.GetString(saved), StringComparison.Ordinal);
            using PdfDocument reopened = PdfDocument.Open(saved, new PdfReadOptions { Password = password, Strict = true });
            Assert.True(reopened.IsEncrypted);
            Assert.Equal(revision, reopened.Trailer.Get<PdfDictionary>("Encrypt")!.GetInt("R"));
            AssertPlainContent(reopened);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void IncrementalUpdatesAreEncryptedWithTheSameKey(string name, string? password)
    {
        byte[] original = System.IO.File.ReadAllBytes(File(name));
        using PdfDocument pdf = PdfDocument.Open(original, new PdfReadOptions { Password = password });
        pdf.Trailer.Get<PdfDictionary>("Info")!["Subject"] = PdfString.FromText("Added secretly");

        byte[] saved = Save(pdf, new PdfSaveOptions { Deterministic = true });

        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.DoesNotContain("Added secretly", Encoding.Latin1.GetString(saved), StringComparison.Ordinal);
        using PdfDocument reopened = PdfDocument.Open(saved, new PdfReadOptions { Password = password, Strict = true });
        Assert.Equal("Added secretly", reopened.Trailer.Get<PdfDictionary>("Info")!.GetText("Subject"));
        AssertPlainContent(reopened);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EncryptionCanBeDropped(string name, string? password)
    {
        using PdfDocument original = PdfDocument.Open(File(name), new PdfReadOptions { Password = password });

        // An incremental save that drops encryption writes the whole document decrypted.
        byte[] saved = Save(original, new PdfSaveOptions { Encryption = PdfEncryption.None });

        using PdfDocument reopened = PdfDocument.Open(saved, new PdfReadOptions { Strict = true });
        Assert.False(reopened.IsEncrypted);
        AssertPlainContent(reopened);
    }

    [Fact]
    public void NewAes256EncryptionOpensWithEitherPassword()
    {
        using PdfDocument plain = PdfDocument.Open(File("plain.pdf"));
        PdfSaveOptions options = new() { Encryption = PdfEncryption.Aes256("user", "owner", PdfPermissions.Print | PdfPermissions.ExtractForAccessibility) };

        byte[] saved = Save(plain, options);

        Assert.DoesNotContain("Secret text", Encoding.Latin1.GetString(saved), StringComparison.Ordinal);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(saved));
        using PdfDocument user = PdfDocument.Open(saved, new PdfReadOptions { Password = "user", Strict = true });
        Assert.False(user.IsOwner);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.ExtractForAccessibility, user.Permissions);
        AssertPlainContent(user);
        Assert.Equal(6, user.Trailer.Get<PdfDictionary>("Encrypt")!.GetInt("R"));

        // The 1.7 file declares Adobe's extension level 8, which AES-256 needs before PDF 2.0.
        Assert.Equal("1.7", user.Version);
        Assert.Equal(8, user.Catalog.Get<PdfDictionary>("Extensions")!.Get<PdfDictionary>("ADBE")!.GetInt("ExtensionLevel"));

        using PdfDocument owner = PdfDocument.Open(saved, new PdfReadOptions { Password = "owner", Strict = true });
        Assert.True(owner.IsOwner);
        AssertPlainContent(owner);
    }

    [Fact]
    public void NewEncryptionReplacesOldEncryption()
    {
        using PdfDocument original = PdfDocument.Open(File("rc4-40.pdf"), new PdfReadOptions { Password = "owner" });

        byte[] saved = Save(original, Rewrite with { Encryption = PdfEncryption.Aes256("", "new owner") });

        using PdfDocument reopened = PdfDocument.Open(saved, new PdfReadOptions { Strict = true });
        Assert.True(reopened.IsEncrypted);
        Assert.Equal(6, reopened.Trailer.Get<PdfDictionary>("Encrypt")!.GetInt("R"));
        AssertPlainContent(reopened);
        // The old owner password means nothing now: the file opens only through its empty user password.
        using PdfDocument old = PdfDocument.Open(saved, new PdfReadOptions { Password = "owner" });
        Assert.False(old.IsOwner);
        using PdfDocument owner = PdfDocument.Open(saved, new PdfReadOptions { Password = "new owner" });
        Assert.True(owner.IsOwner);
    }

    [Fact]
    public void DeterministicEncryptedSavesAreIdentical()
    {
        using PdfDocument original = PdfDocument.Open(File("aes-256.pdf"), new PdfReadOptions { Password = "user" });

        Assert.Equal(Save(original, Rewrite), Save(original, Rewrite));
    }

    [Fact]
    public void OwnerPasswordIsRequiredForNewEncryption()
    {
        Assert.Throws<ArgumentException>(() => PdfEncryption.Aes256("user", ""));
    }
}
