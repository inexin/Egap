using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Egap.Documents.Office;

namespace Egap.Documents.Tests;

/// <summary>Encrypted Word files ([MS-OFFCRYPTO]): saving with a password, loading with one, the password API and damage.</summary>
public sealed class OfficeEncryptionTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("egap-crypto-").FullName;

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private static Document Sample(string text = "Secret text, æøå 日本語")
    {
        Document document = new();
        Body body = document.AddSection().Body;
        body.AddParagraph(text);
        body.AddParagraph("Second paragraph");
        return document;
    }

    private static byte[] Encrypted(string password, Document? document = null)
    {
        using MemoryStream stream = new();
        DocxFormat.Save(document ?? Sample(), stream, new DocxSaveOptions { Password = password });
        return stream.ToArray();
    }

    [Fact]
    public void SavedWithAPasswordLoadsWithIt()
    {
        byte[] file = Encrypted("Pa55 wörd");

        Assert.True(CompoundFile.IsCompoundFile(file));
        Document loaded = DocxFormat.Load(new MemoryStream(file), new DocxLoadOptions { Password = "Pa55 wörd" });

        Assert.Contains("Secret text, æøå 日本語", loaded.GetText(), StringComparison.Ordinal);
        Assert.Contains("Second paragraph", loaded.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileHoldsWhatWordWrites()
    {
        CompoundFile file = CompoundFile.Read(Encrypted("x"), new DocumentLimits());

        Assert.Empty(file.Problems);
        Assert.Equal(76, file.ReadStream("\u0006DataSpaces/Version")!.Length);
        Assert.Equal(112, file.ReadStream("\u0006DataSpaces/DataSpaceMap")!.Length);
        Assert.Equal(64, file.ReadStream("\u0006DataSpaces/DataSpaceInfo/StrongEncryptionDataSpace")!.Length);
        Assert.Equal(200, file.ReadStream("\u0006DataSpaces/TransformInfo/StrongEncryptionTransform/\u0006Primary")!.Length);
        byte[] info = file.ReadStream("EncryptionInfo")!;
        Assert.Equal([4, 0, 4, 0, 0x40, 0, 0, 0], info[..8]);
        string xml = Encoding.UTF8.GetString(info, 8, info.Length - 8);
        Assert.Contains("spinCount=\"100000\"", xml, StringComparison.Ordinal);
        Assert.Contains("hashAlgorithm=\"SHA512\"", xml, StringComparison.Ordinal);
        Assert.Contains("keyBits=\"256\"", xml, StringComparison.Ordinal);
        Assert.Contains("<dataIntegrity ", xml, StringComparison.Ordinal);
        Assert.True(OfficeEncryption.IsEncryptedPackage(file));
    }

    [Fact]
    public void AWrongOrMissingPasswordThrowsThePasswordException()
    {
        byte[] file = Encrypted("right");

        DocumentPasswordException missing = Assert.Throws<DocumentPasswordException>(() => DocxFormat.Load(new MemoryStream(file)));
        DocumentPasswordException wrong = Assert.Throws<DocumentPasswordException>(() => DocxFormat.Load(new MemoryStream(file), new DocxLoadOptions { Password = "wrong" }));

        Assert.False(missing.WrongPassword);
        Assert.True(wrong.WrongPassword);
        Assert.IsAssignableFrom<DocumentException>(wrong);
    }

    [Fact]
    public void TheCallbackIsAskedUntilItGivesTheRightPasswordOrNull()
    {
        byte[] file = Encrypted("third");
        List<int> asked = [];
        string?[] answers = ["first", "second", "third"];

        Document loaded = DocxFormat.Load(new MemoryStream(file), new DocxLoadOptions
        {
            Password = "wrong",
            PasswordCallback = attempt =>
            {
                asked.Add(attempt);
                return answers[attempt - 1];
            },
        });

        Assert.Equal([1, 2, 3], asked);
        Assert.Contains("Secret text", loaded.GetText(), StringComparison.Ordinal);

        int calls = 0;
        DocumentPasswordException giveUp = Assert.Throws<DocumentPasswordException>(() => DocxFormat.Load(new MemoryStream(file), new DocxLoadOptions
        {
            PasswordCallback = attempt => ++calls < 3 ? "nope" : null,
        }));
        Assert.Equal(3, calls);
        Assert.True(giveUp.WrongPassword);
    }

    [Fact]
    public void DocumentLoadTakesThePasswordAndIdentifyReportsEncrypted()
    {
        string path = Path.Combine(folder, "secret.docx");
        Sample().SaveAsDocx(path, new DocxSaveOptions { Password = "open sesame" });

        Assert.Equal(DocumentFormatKind.Encrypted, Document.Identify(path));
        using (FileStream stream = File.OpenRead(path))
        {
            Assert.Equal(DocumentFormatKind.Encrypted, Document.Identify(stream));
            Assert.Equal(0, stream.Position);
        }

        Assert.Equal(DocumentFormatKind.Encrypted, Document.Identify(new NonSeekableStream(File.ReadAllBytes(path))));
        Assert.Throws<DocumentPasswordException>(() => Document.Load(path));
        Document loaded = Document.Load(path, new DocumentLoadOptions { Password = "open sesame" });
        Assert.Equal(path, loaded.FileName);
        Assert.Contains("Secret text", loaded.GetText(), StringComparison.Ordinal);

        using FileStream again = File.OpenRead(path);
        Assert.Contains("Secret text", Document.Load(again, out IReadOnlyList<ConversionWarning> warnings, new DocumentLoadOptions { PasswordCallback = _ => "open sesame" }).GetText(), StringComparison.Ordinal);
        Assert.NotNull(warnings);
    }

    [Fact]
    public void APlainCompoundFileIsStillAnOldWordFile()
    {
        using MemoryStream stream = new();
        CompoundFileWriter.Write(stream, [("WordDocument", new byte[600]), ("1Table", new byte[600])]);
        byte[] file = stream.ToArray();

        Assert.Equal(DocumentFormatKind.Docx, Document.Identify(new MemoryStream(file)));
        DocumentException error = Assert.Throws<DocumentException>(() => DocxFormat.Load(new MemoryStream(file)));
        Assert.IsNotType<DocumentPasswordException>(error);
        Assert.Contains("DOC", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingWithoutAPasswordWritesAPlainPackage()
    {
        using MemoryStream stream = new();
        DocxFormat.Save(Sample(), stream, new DocxSaveOptions { Password = "" });

        Assert.Equal((byte)'P', stream.ToArray()[0]);
        Assert.Throws<ArgumentException>(() => DocxFormat.Save(Sample(), new MemoryStream(), new DocxSaveOptions { Password = new string('a', 256) }));
    }

    [Fact]
    public void AChangedPackageFailsTheIntegrityCheck()
    {
        Document document = Sample();
        for (int i = 0; i < 300; i++)
        {
            document.Sections[0].Body.AddParagraph($"Filler {i * 7919 % 10007}");
        }

        byte[] file = Encrypted("pw", document);
        CompoundFile read = CompoundFile.Read(file, new DocumentLimits());
        CompoundFileEntry package = read.Find("EncryptedPackage")!;
        Assert.True(package.Size >= 4096);

        // The writer lays the package's sectors out one after another.
        file[512 + ((int)package.StartSector * 512) + 100] ^= 0x01;
        DocumentException error = Assert.Throws<DocumentException>(() => DocxFormat.Load(new MemoryStream(file), new DocxLoadOptions { Password = "pw" }));

        Assert.Contains("integrity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LargePackagesSpanManySegments()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        for (int i = 0; i < 3000; i++)
        {
            body.AddParagraph($"Paragraph {i} with text that does not compress away {i * 7919 % 10007}");
        }

        byte[] file = Encrypted("long", document);
        Document loaded = DocxFormat.Load(new MemoryStream(file), new DocxLoadOptions { Password = "long" });

        Assert.Equal(3000, loaded.Sections[0].Body.Paragraphs.Count());
        Assert.Contains("Paragraph 2999 with", loaded.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void StandardEncryptionIsRead()
    {
        // Office 2007's standard encryption, made here from [MS-OFFCRYPTO] 2.3.4.5 to 2.3.4.9: AES-128 in ECB mode, the key from
        // 50,000 SHA-1 spins and CryptDeriveKey.
        using MemoryStream docx = new();
        DocxFormat.Save(Sample("Standard encryption"), docx);
        byte[] file = StandardEncrypt(docx.ToArray(), "2007");

        Document loaded = DocxFormat.Load(new MemoryStream(file), new DocxLoadOptions { Password = "2007" });

        Assert.Contains("Standard encryption", loaded.GetText(), StringComparison.Ordinal);
        Assert.True(Assert.Throws<DocumentPasswordException>(() => DocxFormat.Load(new MemoryStream(file), new DocxLoadOptions { Password = "2010" })).WrongPassword);
    }

    [Fact]
    public void LibreOfficesStandardEncryptionIsRead()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "files", "encrypted", "libreoffice-standard.docx");
        CompoundFile file = CompoundFile.Read(File.ReadAllBytes(path), new DocumentLimits());
        byte[] info = file.ReadStream("EncryptionInfo")!;

        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(2)));
        Assert.Equal(DocumentFormatKind.Encrypted, Document.Identify(path));
        Document loaded = Document.Load(path, new DocumentLoadOptions { Password = "Lo pässwörd" });
        string text = loaded.GetText();
        Assert.Contains("Hello from Egap, encrypted. Æøå 日本語", text, StringComparison.Ordinal);
        Assert.Contains("Paragraph 399", text, StringComparison.Ordinal);
        Assert.True(Assert.Throws<DocumentPasswordException>(() => Document.Load(path, new DocumentLoadOptions { Password = "Lo passwOrd" })).WrongPassword);
    }

    [Fact]
    public void EncryptionOthersDoNotWriteIsRefusedByName()
    {
        DocumentException Load(byte[] info)
        {
            using MemoryStream stream = new();
            CompoundFileWriter.Write(stream, [("EncryptionInfo", info), ("EncryptedPackage", new byte[64])]);
            return Assert.Throws<DocumentException>(() => DocxFormat.Load(new MemoryStream(stream.ToArray()), new DocxLoadOptions { Password = "x" }));
        }

        Assert.Contains("extensible", Load([3, 0, 3, 0, 0, 0, 0, 0]).Message, StringComparison.Ordinal);
        Assert.Contains("version 9.9", Load([9, 0, 9, 0, 0, 0, 0, 0]).Message, StringComparison.Ordinal);
        Assert.Contains("well-formed", Load([4, 0, 4, 0, 0x40, 0, 0, 0, .. "<encryption"u8]).Message, StringComparison.Ordinal);
        string certificate = "<encryption xmlns=\"http://schemas.microsoft.com/office/2006/encryption\"><keyData saltSize=\"16\" blockSize=\"16\" keyBits=\"256\" hashSize=\"64\" cipherAlgorithm=\"AES\" cipherChaining=\"ChainingModeCBC\" hashAlgorithm=\"SHA512\" saltValue=\"AAAAAAAAAAAAAAAAAAAAAA==\"/></encryption>";
        Assert.Contains("certificate", Load([4, 0, 4, 0, 0x40, 0, 0, 0, .. Encoding.UTF8.GetBytes(certificate)]).Message, StringComparison.Ordinal);
        Assert.Contains("cipher", Load([4, 0, 4, 0, 0x40, 0, 0, 0, .. Encoding.UTF8.GetBytes(certificate.Replace("\"AES\"", "\"3DES\"", StringComparison.Ordinal))]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RightsManagementIsRefusedByName()
    {
        using MemoryStream stream = new();
        CompoundFileWriter.Write(stream, [("EncryptionInfo", [4, 0, 4, 0]), ("EncryptedPackage", new byte[64]), ("\u0009DRMContent", new byte[10])]);

        DocumentException error = Assert.Throws<DocumentException>(() => DocxFormat.Load(new MemoryStream(stream.ToArray()), new DocxLoadOptions { Password = "x" }));

        Assert.Contains("rights management", error.Message, StringComparison.Ordinal);
    }

    private static byte[] StandardEncrypt(byte[] package, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
#pragma warning disable CA5350 // the format's own hash
        byte[] h = SHA1.HashData([.. salt, .. Encoding.Unicode.GetBytes(password)]);
        for (int i = 0; i < 50_000; i++)
        {
            h = SHA1.HashData([.. BitConverter.GetBytes(i), .. h]);
        }

        h = SHA1.HashData([.. h, 0, 0, 0, 0]);
        byte[] buffer = new byte[64];
        buffer.AsSpan().Fill(0x36);
        for (int i = 0; i < h.Length; i++)
        {
            buffer[i] ^= h[i];
        }

        byte[] key = SHA1.HashData(buffer)[..16];
        byte[] verifier = RandomNumberGenerator.GetBytes(16);
        byte[] verifierHash = [.. SHA1.HashData(verifier), .. new byte[12]];
#pragma warning restore CA5350
        using Aes aes = Aes.Create();
        aes.Key = key;
#pragma warning disable CA5358 // the format's own mode
        byte[] encryptedVerifier = aes.EncryptEcb(verifier, PaddingMode.None);
        byte[] encryptedHash = aes.EncryptEcb(verifierHash, PaddingMode.None);
        byte[] padded = new byte[(package.Length + 15) / 16 * 16];
        package.CopyTo(padded, 0);
        byte[] encrypted = aes.EncryptEcb(padded, PaddingMode.None);
#pragma warning restore CA5358

        List<byte> info = [3, 0, 2, 0, 0x24, 0, 0, 0];
        byte[] csp = Encoding.Unicode.GetBytes("Microsoft Enhanced RSA and AES Cryptographic Provider\0");
        List<byte> header = [];
        foreach (int value in (int[])[0x24, 0, 0x660E, 0x8004, 128, 0x18, 0, 0])
        {
            header.AddRange(BitConverter.GetBytes(value));
        }

        header.AddRange(csp);
        info.AddRange(BitConverter.GetBytes(header.Count));
        info.AddRange(header);
        info.AddRange(BitConverter.GetBytes(16));
        info.AddRange(salt);
        info.AddRange(encryptedVerifier);
        info.AddRange(BitConverter.GetBytes(20));
        info.AddRange(encryptedHash);

        using MemoryStream stream = new();
        CompoundFileWriter.Write(stream, [("EncryptionInfo", [.. info]), ("EncryptedPackage", [.. BitConverter.GetBytes((long)package.Length), .. encrypted])]);
        return stream.ToArray();
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
