using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Egap.Documents.Odf;

namespace Egap.Documents.Tests;

/// <summary>Password-protected OpenDocument text: LibreOffice's three encryptions read, and Egap's own written and read back.</summary>
public class OdfEncryptionTests
{
    private static readonly XNamespace M = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "files", "encrypted", name);

    /// <summary>A small plain ODT package of Egap's own, made from the flat seed.</summary>
    private static byte[] Plain(string text = "Secret text, æøå 日本語")
    {
        string content = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" office:version="1.3"><office:body><office:text><text:p>{text}</text:p></office:text></office:body></office:document-content>
            """;
        string manifest = """
            <?xml version="1.0" encoding="UTF-8"?>
            <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3"><manifest:file-entry manifest:full-path="/" manifest:version="1.3" manifest:media-type="application/vnd.oasis.opendocument.text"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="Thumbnails/thumbnail.png" manifest:media-type="image/png"/></manifest:manifest>
            """;
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
            Add(zip, "content.xml", content.Trim(), CompressionLevel.Optimal);
            Add(zip, "Thumbnails/thumbnail.png", "not really a picture", CompressionLevel.Optimal);
            Add(zip, "META-INF/manifest.xml", manifest.Trim(), CompressionLevel.Optimal);
        }

        return stream.ToArray();

        static void Add(ZipArchive zip, string name, string data, CompressionLevel level)
        {
            using Stream entry = zip.CreateEntry(name, level).Open();
            entry.Write(Encoding.UTF8.GetBytes(data));
        }
    }

    private static byte[] Encrypt(byte[] package, string password, OdfEncryptionMethod method)
    {
        using MemoryStream output = new();
        OdfEncryption.Encrypt(package, password, method, output);
        return output.ToArray();
    }

    [Theory]
    [InlineData("libreoffice-blowfish.odt")]
    [InlineData("libreoffice-aes-cbc.odt")]
    [InlineData("libreoffice-aes-gcm.odt")]
    public void LibreOfficeFilesOpenWithTheirPassword(string name)
    {
        Document encrypted = OdtFormat.Load(Fixture(name), new OdtLoadOptions { Password = "pw" });
        // LibreOffice saved one document three ways, so all three read the same.
        Document reference = OdtFormat.Load(Fixture("libreoffice-aes-cbc.odt"), new OdtLoadOptions { Password = "pw" });
        Assert.Equal(reference.GetText(), encrypted.GetText());
        Assert.Contains("Intro", encrypted.GetText(), StringComparison.Ordinal);
        Assert.Equal("The title", encrypted.Properties.Title);
        Assert.NotEmpty(encrypted.Descendants<Picture>());
    }

    [Theory]
    [InlineData("libreoffice-blowfish.odt")]
    [InlineData("libreoffice-aes-cbc.odt")]
    [InlineData("libreoffice-aes-gcm.odt")]
    public void LibreOfficeFilesRefuseAWrongOrMissingPassword(string name)
    {
        DocumentPasswordException wrong = Assert.Throws<DocumentPasswordException>(() => OdtFormat.Load(Fixture(name), new OdtLoadOptions { Password = "PW" }));
        Assert.True(wrong.WrongPassword);
        DocumentPasswordException missing = Assert.Throws<DocumentPasswordException>(() => OdtFormat.Load(Fixture(name)));
        Assert.False(missing.WrongPassword);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncryptedPackagesReadBack(bool whole)
    {
        OdfEncryptionMethod method = whole ? OdfEncryptionMethod.AesGcmArgon2id : OdfEncryptionMethod.AesCbc;
        byte[] file = Encrypt(Plain(), "Pa55 wörd", method);
        Document loaded = OdtFormat.Load(new MemoryStream(file), new OdtLoadOptions { Password = "Pa55 wörd" });
        Assert.Equal("Secret text, æøå 日本語", loaded.GetText().Trim());
        Assert.True(Assert.Throws<DocumentPasswordException>(() => OdtFormat.Load(new MemoryStream(file), new OdtLoadOptions { Password = "Pa55 word" })).WrongPassword);
    }

    [Fact]
    public void PerFileEncryptionHidesTheTextAndDropsTheThumbnail()
    {
        byte[] file = Encrypt(Plain("Findable words"), "pw", OdfEncryptionMethod.AesCbc);
        using ZipArchive zip = new(new MemoryStream(file), ZipArchiveMode.Read);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Null(zip.GetEntry("Thumbnails/thumbnail.png"));
        ZipArchiveEntry content = zip.GetEntry("content.xml")!;
        Assert.Equal(content.Length, content.CompressedLength);
        using MemoryStream bytes = new();
        content.Open().CopyTo(bytes);
        Assert.DoesNotContain("Findable", Encoding.UTF8.GetString(bytes.ToArray()), StringComparison.Ordinal);
        XElement manifest = XElement.Load(zip.GetEntry("META-INF/manifest.xml")!.Open());
        XElement entry = manifest.Elements(M + "file-entry").Single(e => (string?)e.Attribute(M + "full-path") == "content.xml");
        XElement data = entry.Element(M + "encryption-data")!;
        Assert.Equal("urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha256-1k", (string?)data.Attribute(M + "checksum-type"));
        Assert.Equal("http://www.w3.org/2001/04/xmlenc#aes256-cbc", (string?)data.Element(M + "algorithm")!.Attribute(M + "algorithm-name"));
        Assert.Equal("100000", (string?)data.Element(M + "key-derivation")!.Attribute(M + "iteration-count"));
        Assert.DoesNotContain(manifest.Elements(M + "file-entry"), e => ((string?)e.Attribute(M + "full-path"))!.StartsWith("Thumbnails", StringComparison.Ordinal));
    }

    [Fact]
    public void WholePackageEncryptionHidesTheFileNames()
    {
        byte[] file = Encrypt(Plain(), "pw", OdfEncryptionMethod.AesGcmArgon2id);
        using ZipArchive zip = new(new MemoryStream(file), ZipArchiveMode.Read);
        Assert.Equal(["mimetype", "encrypted-package", "META-INF/manifest.xml"], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void TheCallbackIsAskedUntilAPasswordIsRight()
    {
        byte[] file = Encrypt(Plain(), "third", OdfEncryptionMethod.AesCbc);
        List<int> attempts = [];
        Document loaded = OdtFormat.Load(new MemoryStream(file), new OdtLoadOptions
        {
            Password = "first",
            PasswordCallback = attempt =>
            {
                attempts.Add(attempt);
                return attempt == 1 ? "second" : "third";
            },
        });
        Assert.Equal([1, 2], attempts);
        Assert.Contains("Secret text", loaded.GetText(), StringComparison.Ordinal);
        DocumentPasswordException giveUp = Assert.Throws<DocumentPasswordException>(() => OdtFormat.Load(new MemoryStream(file), new OdtLoadOptions { PasswordCallback = _ => null }));
        Assert.False(giveUp.WrongPassword);
    }

    [Fact]
    public void DocumentLoadAndIdentifyKnowEncryptedOdt()
    {
        byte[] file = Encrypt(Plain(), "pw", OdfEncryptionMethod.AesCbc);
        Assert.Equal(DocumentFormatKind.Encrypted, Document.Identify(new MemoryStream(file), "a.odt"));
        Assert.Equal(DocumentFormatKind.Odt, Document.Identify(new MemoryStream(Plain()), "a.odt"));
        Assert.Equal(DocumentFormatKind.Encrypted, Document.Identify(Fixture("libreoffice-aes-gcm.odt")));
        string path = Path.Combine(Path.GetTempPath(), $"egap-odfenc-{Guid.NewGuid():N}.odt");
        try
        {
            File.WriteAllBytes(path, file);
            Assert.Contains("Secret text", Document.Load(path, new DocumentLoadOptions { Password = "pw" }).GetText(), StringComparison.Ordinal);
            Assert.Throws<DocumentPasswordException>(() => Document.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BlowfishWithEightBitFeedbackReadsToo()
    {
        // ODF's text names 8-bit feedback; a writer that follows it is read as well as OpenOffice.org's 64-bit feedback.
        byte[] file = Rewrite(Plain(), "Blowfish CFB", cfb8: true);
        Assert.Contains("Secret text", OdtFormat.Load(new MemoryStream(file), new OdtLoadOptions { Password = "pw" }).GetText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("iteration-count", "20000000", typeof(DocumentLimitException))]
    [InlineData("iteration-count", "0", typeof(DocumentLimitException))]
    [InlineData("key-size", "40", typeof(DocumentException))]
    public void KeyDerivationsPastTheLimitsAreRefused(string attribute, string value, Type expected)
    {
        byte[] file = EditManifest(Encrypt(Plain(), "pw", OdfEncryptionMethod.AesCbc), m =>
        {
            foreach (XElement derivation in m.Descendants(M + "key-derivation"))
            {
                derivation.SetAttributeValue(M + attribute, value);
            }
        });
        Exception error = Assert.ThrowsAny<DocumentException>(() => OdtFormat.Load(new MemoryStream(file), new OdtLoadOptions { Password = "pw" }));
        Assert.IsType(expected, error);
    }

    [Fact]
    public void Argon2MemoryPastTheLimitIsRefused()
    {
        XNamespace loext = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";
        byte[] file = EditManifest(Encrypt(Plain(), "pw", OdfEncryptionMethod.AesGcmArgon2id), m =>
            m.Descendants(M + "key-derivation").Single().SetAttributeValue(loext + "argon2-memory", "524288"));
        Assert.Throws<DocumentLimitException>(() => OdtFormat.Load(new MemoryStream(file), new OdtLoadOptions { Password = "pw" }));
    }

    [Fact]
    public void UnknownAlgorithmsAreNamed()
    {
        byte[] file = EditManifest(Encrypt(Plain(), "pw", OdfEncryptionMethod.AesCbc), m =>
        {
            foreach (XElement algorithm in m.Descendants(M + "algorithm"))
            {
                algorithm.SetAttributeValue(M + "algorithm-name", "urn:example:rot13");
            }
        });
        DocumentException error = Assert.Throws<DocumentException>(() => OdtFormat.Load(new MemoryStream(file), new OdtLoadOptions { Password = "pw" }));
        Assert.Contains("urn:example:rot13", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DamagedFilesThrowDocumentExceptions()
    {
        foreach (OdfEncryptionMethod method in Enum.GetValues<OdfEncryptionMethod>())
        {
            byte[] file = Encrypt(Plain(), "pw", method);
            for (int i = 0; i < 40; i++)
            {
                byte[] damaged = (byte[])file.Clone();
                damaged[(i * 7919) % damaged.Length] ^= 0x5A;
                try
                {
                    OdtFormat.Load(new MemoryStream(damaged), new OdtLoadOptions { Password = "pw" });
                }
                catch (DocumentException)
                {
                }
            }
        }
    }

    /// <summary>A package with each file encrypted by hand with Blowfish, as old OpenOffice.org files are.</summary>
    private static byte[] Rewrite(byte[] package, string algorithmName, bool cfb8)
    {
        using MemoryStream output = new();
        using ZipArchive source = new(new MemoryStream(package), ZipArchiveMode.Read);
        XElement manifest = XElement.Load(source.GetEntry("META-INF/manifest.xml")!.Open());
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using MemoryStream data = new();
                entry.Open().CopyTo(data);
                byte[] bytes = data.ToArray();
                if (entry.FullName is "content.xml")
                {
                    using MemoryStream deflated = new();
                    using (DeflateStream deflate = new(deflated, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        deflate.Write(bytes);
                    }

                    byte[] salt = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];
                    byte[] iv = [9, 8, 7, 6, 5, 4, 3, 2];
                    byte[] key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(System.Security.Cryptography.SHA256.HashData("pw"u8), salt, 1024, System.Security.Cryptography.HashAlgorithmName.SHA1, 16);
                    byte[] cipher = deflated.ToArray();
                    byte[] checksum = System.Security.Cryptography.SHA256.HashData(cipher.AsSpan(0, Math.Min(1024, cipher.Length)));
                    Blowfish blowfish = new(key);
                    if (cfb8)
                    {
                        blowfish.EncryptCfb8(iv, cipher);
                    }
                    else
                    {
                        blowfish.EncryptCfb64(iv, cipher);
                    }

                    manifest.Elements(M + "file-entry").Single(e => (string?)e.Attribute(M + "full-path") == "content.xml").Add(
                        new XAttribute(M + "size", bytes.Length),
                        new XElement(
                            M + "encryption-data",
                            new XAttribute(M + "checksum-type", "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha256-1k"),
                            new XAttribute(M + "checksum", Convert.ToBase64String(checksum)),
                            new XElement(M + "algorithm", new XAttribute(M + "algorithm-name", algorithmName), new XAttribute(M + "initialisation-vector", Convert.ToBase64String(iv))),
                            new XElement(M + "start-key-generation", new XAttribute(M + "start-key-generation-name", "http://www.w3.org/2000/09/xmldsig#sha256"), new XAttribute(M + "key-size", "32")),
                            new XElement(M + "key-derivation", new XAttribute(M + "key-derivation-name", "PBKDF2"), new XAttribute(M + "iteration-count", "1024"), new XAttribute(M + "salt", Convert.ToBase64String(salt)))));
                    bytes = cipher;
                }

                if (entry.FullName != "META-INF/manifest.xml")
                {
                    using Stream stream = zip.CreateEntry(entry.FullName, entry.FullName == "content.xml" || entry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                    stream.Write(bytes);
                }
            }

            using Stream manifestStream = zip.CreateEntry("META-INF/manifest.xml").Open();
            manifest.Save(manifestStream);
        }

        return output.ToArray();
    }

    private static byte[] EditManifest(byte[] package, Action<XElement> edit)
    {
        using MemoryStream output = new();
        using ZipArchive source = new(new MemoryStream(package), ZipArchiveMode.Read);
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using MemoryStream data = new();
                entry.Open().CopyTo(data);
                byte[] bytes = data.ToArray();
                if (entry.FullName == "META-INF/manifest.xml")
                {
                    XElement manifest = XElement.Load(new MemoryStream(bytes));
                    edit(manifest);
                    using MemoryStream edited = new();
                    manifest.Save(edited);
                    bytes = edited.ToArray();
                }

                using Stream stream = zip.CreateEntry(entry.FullName, entry.FullName == "META-INF/manifest.xml" ? CompressionLevel.Optimal : CompressionLevel.NoCompression).Open();
                stream.Write(bytes);
            }
        }

        return output.ToArray();
    }
}
