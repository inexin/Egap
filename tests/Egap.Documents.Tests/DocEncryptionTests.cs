using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Egap.Documents.Doc;

namespace Egap.Documents.Tests;

/// <summary>
/// Password-protected binary Word documents: files the builder makes and then encrypts here, from [MS-OFFCRYPTO], with RC4
/// (MD5 keys), RC4 CryptoAPI (SHA-1 keys of 40 and 128 bits) and XOR obfuscation.
/// </summary>
#pragma warning disable CA5350, CA5351 // The formats are defined with MD5 and SHA-1; these tests make files that use them.
public sealed class DocEncryptionTests
{
    private const string Password = "Egap pass1";
    private static readonly byte[] Salt = [.. Enumerable.Range(1, 16).Select(i => (byte)(i * 7))];
    private static readonly byte[] Verifier = [.. Enumerable.Range(1, 16).Select(i => (byte)(i * 13))];
    private static readonly string LongText = string.Concat(Enumerable.Repeat("Encrypted text that runs past one block. ", 30));

    private static Document Read(byte[] file, string? password, Func<int, string?>? callback = null)
    {
        return DocReader.Read(file, new DocumentLimits(), password, callback, out _);
    }

    private static string Text(Document document) => string.Concat(document.Sections[0].Body.Paragraphs.Select(p => p.GetText()));

    // ---- An RC4 of the tests' own and the keys of [MS-OFFCRYPTO] 2.3.5.2 and 2.3.6.2 ----

    private static void Rc4(byte[] key, Span<byte> bytes)
    {
        byte[] s = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        for (int n = 0, i = 0, j = 0; n < bytes.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            bytes[n] ^= s[(s[i] + s[j]) & 0xFF];
        }
    }

    private static Func<uint, byte[]> Md5Keys(string password)
    {
        byte[] h0 = MD5.HashData(Encoding.Unicode.GetBytes(password));
        byte[] buffer = [.. Enumerable.Range(0, 16).SelectMany(_ => h0.Take(5).Concat(Salt))];
        byte[] h1 = MD5.HashData(buffer);
        return block => MD5.HashData([.. h1.Take(5), .. BitConverter.GetBytes(block)]);
    }

    private static Func<uint, byte[]> Sha1Keys(string password, int bits)
    {
        byte[] h0 = SHA1.HashData([.. Salt, .. Encoding.Unicode.GetBytes(password)]);
        return block =>
        {
            byte[] final = SHA1.HashData([.. h0, .. BitConverter.GetBytes(block)]);
            return bits == 40 ? [.. final.Take(5), .. new byte[11]] : [.. final.Take(bits / 8)];
        };
    }

    /// <summary>Encrypts a stream in 512-byte blocks numbered from its start, then puts back its clear first bytes.</summary>
    private static void EncryptStream(byte[] stream, int clear, Func<uint, byte[]> keys)
    {
        byte[] prefix = stream[..clear];
        for (int block = 0; block * 512 < stream.Length; block++)
        {
            Rc4(keys((uint)block), stream.AsSpan(block * 512, Math.Min(512, stream.Length - (block * 512))));
        }

        prefix.CopyTo(stream, 0);
    }

    private static byte[] EncryptedVerifier(Func<uint, byte[]> keys, bool sha1)
    {
        byte[] hash = sha1 ? SHA1.HashData(Verifier) : MD5.HashData(Verifier);
        byte[] both = [.. Verifier, .. hash];
        Rc4(keys(0), both);
        return both;
    }

    private static byte[] Rc4Header(string password)
    {
        byte[] encrypted = EncryptedVerifier(Md5Keys(password), sha1: false);
        return [1, 0, 1, 0, .. Salt, .. encrypted];
    }

    private static byte[] CryptoApiHeader(string password, int bits, int algorithm = 0x6801)
    {
        byte[] csp = Encoding.Unicode.GetBytes("Microsoft Enhanced Cryptographic Provider v1.0\0");
        byte[] header =
        [
            .. BitConverter.GetBytes(0x04), .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(algorithm), .. BitConverter.GetBytes(0x8004),
            .. BitConverter.GetBytes(bits), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(0), .. csp,
        ];
        byte[] encrypted = EncryptedVerifier(Sha1Keys(password, bits), sha1: true);
        return
        [
            4, 0, 2, 0, .. BitConverter.GetBytes(0x04), .. BitConverter.GetBytes(header.Length), .. header,
            .. BitConverter.GetBytes(16), .. Salt, .. encrypted[..16], .. BitConverter.GetBytes(20), .. encrypted[16..],
        ];
    }

    private static DocBuilder Builder(byte[] header, Func<uint, byte[]> keys)
    {
        DocBuilder builder = new()
        {
            Flags = 0x0100,
            TableHeader = header,
            LKey = (uint)header.Length,
            Data = [.. Enumerable.Range(0, 700).Select(i => (byte)i)],
            Transform = (word, table, data) =>
            {
                EncryptStream(word, 68, keys);
                EncryptStream(table, header.Length, keys);
                EncryptStream(data!, 0, keys);
            },
        };
        builder.Run("Secret ").Run(LongText).Paragraph().Run("Second paragraph").Paragraph();
        return builder;
    }

    [Fact]
    public void Rc4EncryptedDocumentsOpenWithTheirPassword()
    {
        byte[] file = Builder(Rc4Header(Password), Md5Keys(Password)).Build();

        Assert.False(Assert.Throws<DocumentPasswordException>(() => Read(file, null)).WrongPassword);
        Assert.True(Assert.Throws<DocumentPasswordException>(() => Read(file, "wrong")).WrongPassword);
        Assert.Equal("Secret " + LongText + "Second paragraph", Text(Read(file, Password)));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(56)]
    [InlineData(128)]
    public void CryptoApiEncryptedDocumentsOpenWithTheirPassword(int bits)
    {
        byte[] file = Builder(CryptoApiHeader(Password, bits), Sha1Keys(Password, bits)).Build();

        Assert.True(Assert.Throws<DocumentPasswordException>(() => Read(file, "Egap pass2")).WrongPassword);
        Assert.StartsWith("Secret Encrypted text", Text(Read(file, Password)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCallbackIsAskedUntilAPasswordFits()
    {
        byte[] file = Builder(CryptoApiHeader(Password, 128), Sha1Keys(Password, 128)).Build();
        List<int> attempts = [];

        Document document = Read(file, "first", attempt =>
        {
            attempts.Add(attempt);
            return attempt < 3 ? "wrong" + attempt : Password;
        });

        Assert.Equal([1, 2, 3], attempts);
        Assert.StartsWith("Secret", Text(document), StringComparison.Ordinal);
        Assert.True(Assert.Throws<DocumentPasswordException>(() => Read(file, null, attempt => attempt < 4 ? "no" : null)).WrongPassword);
    }

    [Fact]
    public void XorObfuscatedDocumentsOpenWithTheirPassword()
    {
        byte[] password = Encoding.ASCII.GetBytes(Password);
        byte[] array = DocEncryption.XorArray(password);
        static void Obfuscate(byte[] stream, int clear, byte[] array)
        {
            for (int i = clear; i < stream.Length; i++)
            {
                byte k = array[i % 16];
                if (stream[i] != 0 && stream[i] != k)
                {
                    stream[i] ^= k;
                }
            }
        }

        DocBuilder builder = new()
        {
            Flags = 0x8100,
            LKey = DocEncryption.XorVerifier(password),
            Transform = (word, table, _) =>
            {
                Obfuscate(word, 68, array);
                Obfuscate(table, 0, array);
            },
        };
        builder.Run("Secret ").Run(LongText).Paragraph();
        byte[] file = builder.Build();

        Assert.False(Assert.Throws<DocumentPasswordException>(() => Read(file, null)).WrongPassword);
        Assert.True(Assert.Throws<DocumentPasswordException>(() => Read(file, "Egap pass2")).WrongPassword);
        Assert.Equal("Secret " + LongText, Text(Read(file, Password)));
    }

    [Fact]
    public void TheXorVerifierFollowsTheSpec()
    {
        // The low word is the 16-bit verifier of Method 1, the hash Excel's sheet protection keeps: 0x83AF for "password".
        Assert.Equal(0x83AF, (int)(DocEncryption.XorVerifier(Encoding.ASCII.GetBytes("password")) & 0xFFFF));
    }

    [Fact]
    public void OtherEncryptionsAreRefused()
    {
        byte[] header = CryptoApiHeader(Password, 128, algorithm: 0x660E);
        byte[] file = Builder(header, Sha1Keys(Password, 128)).Build();
        DocumentException e = Assert.Throws<DocumentException>(() => Read(file, Password));
        Assert.IsNotType<DocumentPasswordException>(e);

        byte[] unknown = Builder([9, 0, 9, 0, .. new byte[48]], Md5Keys(Password)).Build();
        Assert.Contains("version 9.9", Assert.Throws<DocumentException>(() => Read(unknown, Password)).Message, StringComparison.Ordinal);
    }
}
