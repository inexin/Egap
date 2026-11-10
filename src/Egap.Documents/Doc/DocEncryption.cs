using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using Egap.Documents.Office;

namespace Egap.Documents.Doc;

/// <summary>
/// The password protection of binary Word documents ([MS-DOC] 2.2.6, [MS-OFFCRYPTO] 2.3.5 to 2.3.7): XOR obfuscation, Office
/// binary document RC4 encryption (MD5, 40-bit keys) and RC4 CryptoAPI encryption (SHA-1, 40- to 128-bit keys). The
/// WordDocument stream past its first 68 bytes, the table stream past the encryption header and the whole Data stream are
/// decrypted in place; the summary streams are not read.
/// </summary>
[SuppressMessage("Security", "CA5350", Justification = "DOC's RC4 CryptoAPI encryption is defined with SHA-1; this reads files that use it, it never writes them.")]
[SuppressMessage("Security", "CA5351", Justification = "DOC's RC4 encryption is defined with MD5; this reads files that use it, it never writes them.")]
internal static class DocEncryption
{
    /// <summary>The bytes at the start of the WordDocument stream that are never encrypted: FibBase and the start of FibRgW97.</summary>
    public const int ClearPrefix = 68;

    private const int BlockLength = 512;
    private const int MaxXorPassword = 15;

    // [MS-OFFCRYPTO] 2.3.7.2 and 2.3.7.4.
    private static ReadOnlySpan<byte> PadArray => [0xBB, 0xFF, 0xFF, 0xBA, 0xFF, 0xFF, 0xB9, 0x80, 0x00, 0xBE, 0x0F, 0x00, 0xBF, 0x0F, 0x00];

    private static ReadOnlySpan<ushort> InitialCode =>
    [
        0xE1F0, 0x1D0F, 0xCC9C, 0x84C0, 0x110C, 0x0E10, 0xF1CE, 0x313E, 0x1872, 0xE139, 0xD40F, 0x84F9, 0x280C, 0xA96A, 0x4EC3,
    ];

    private static ReadOnlySpan<ushort> XorMatrix =>
    [
        0xAEFC, 0x4DD9, 0x9BB2, 0x2745, 0x4E8A, 0x9D14, 0x2A09, 0x7B61, 0xF6C2, 0xFDA5, 0xEB6B, 0xC6F7, 0x9DCF, 0x2BBF,
        0x4563, 0x8AC6, 0x05AD, 0x0B5A, 0x16B4, 0x2D68, 0x5AD0, 0x0375, 0x06EA, 0x0DD4, 0x1BA8, 0x3750, 0x6EA0, 0xDD40,
        0xD849, 0xA0B3, 0x5147, 0xA28E, 0x553D, 0xAA7A, 0x44D5, 0x6F45, 0xDE8A, 0xAD35, 0x4A4B, 0x9496, 0x390D, 0x721A,
        0xEB23, 0xC667, 0x9CEF, 0x29FF, 0x53FE, 0xA7FC, 0x5FD9, 0x47D3, 0x8FA6, 0x0F6D, 0x1EDA, 0x3DB4, 0x7B68, 0xF6D0,
        0xB861, 0x60E3, 0xC1C6, 0x93AD, 0x377B, 0x6EF6, 0xDDEC, 0x45A0, 0x8B40, 0x06A1, 0x0D42, 0x1A84, 0x3508, 0x6A10,
        0xAA51, 0x4483, 0x8906, 0x022D, 0x045A, 0x08B4, 0x1168, 0x76B4, 0xED68, 0xCAF1, 0x85C3, 0x1BA7, 0x374E, 0x6E9C,
        0x3730, 0x6E60, 0xDCC0, 0xA9A1, 0x4363, 0x86C6, 0x1DAD, 0x3331, 0x6662, 0xCCC4, 0x89A9, 0x0373, 0x06E6, 0x0DCC,
        0x1021, 0x2042, 0x4084, 0x8108, 0x1231, 0x2462, 0x48C4,
    ];

    /// <summary>
    /// Decrypts the streams in place: tries <paramref name="password"/>, then each password <paramref name="callback"/> gives
    /// (with the attempt number from 1) until it gives null.
    /// </summary>
    /// <exception cref="DocumentPasswordException">No password was given, or none of them was right.</exception>
    /// <exception cref="DocumentException">The encryption is one Egap does not read, or its header is damaged.</exception>
    public static void Decrypt(byte[] word, byte[] table, byte[]? data, string? password, Func<int, string?>? callback)
    {
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(10));
        uint lKey = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(14));
        bool obfuscated = (flags & 0x8000) != 0;
        Func<string, IDecryptor?> keyFor = obfuscated ? p => XorKey(p, lKey) : RcKeyMaker(table, lKey);

        IDecryptor? decryptor = null;
        bool given = password is not null;
        if (password is not null)
        {
            decryptor = keyFor(password);
        }

        for (int attempt = 1; decryptor is null && callback is not null; attempt++)
        {
            string? next = callback(attempt);
            if (next is null)
            {
                break;
            }

            given = true;
            decryptor = keyFor(next);
        }

        if (decryptor is null)
        {
            throw given
                ? new DocumentPasswordException("The password is wrong: the document is encrypted with another one.", wrongPassword: true)
                : new DocumentPasswordException("The document is encrypted: give its password in the load options.", wrongPassword: false);
        }

        decryptor.Apply(word, ClearPrefix);
        decryptor.Apply(table, obfuscated ? 0 : (int)lKey);
        if (data is not null)
        {
            decryptor.Apply(data, 0);
        }
    }

    // ---- XOR obfuscation ([MS-OFFCRYPTO] 2.3.7) ----

    private static XorDecryptor? XorKey(string password, uint verifier)
    {
        // The password becomes single bytes two ways ([MS-OFFCRYPTO] 2.3.7.4): each character's low byte (its high byte when
        // the low one is 0), and the ANSI code page; either may match.
        foreach (byte[] bytes in XorPasswords(password))
        {
            if (bytes.Length > 0 && XorVerifier(bytes) == verifier)
            {
                return new XorDecryptor(XorArray(bytes));
            }
        }

        return null;
    }

    private static IEnumerable<byte[]> XorPasswords(string password)
    {
        int length = Math.Min(password.Length, MaxXorPassword);
        byte[] low = new byte[length];
        for (int i = 0; i < length; i++)
        {
            char c = password[i];
            low[i] = (byte)((c & 0xFF) != 0 ? c & 0xFF : c >> 8);
        }

        yield return low;
        Encoding? ansi = CodePagesEncodingProvider.Instance.GetEncoding(1252);
        if (ansi is not null)
        {
            byte[] encoded = ansi.GetBytes(password);
            yield return encoded.Length > MaxXorPassword ? encoded[..MaxXorPassword] : encoded;
        }
    }

    /// <summary>CreatePasswordVerifier_Method2: the XOR key above the Method 1 verifier.</summary>
    internal static uint XorVerifier(ReadOnlySpan<byte> password)
    {
        int verifier = 0;
        for (int i = password.Length; i >= 0; i--)
        {
            int b = i == 0 ? password.Length : password[i - 1];
            int intermediate = ((verifier & 0x4000) != 0 ? 1 : 0) | ((verifier << 1) & 0x7FFF);
            verifier = intermediate ^ b;
        }

        return ((uint)XorKeyMethod1(password) << 16) | (ushort)(verifier ^ 0xCE4B);
    }

    private static ushort XorKeyMethod1(ReadOnlySpan<byte> password)
    {
        ushort key = InitialCode[password.Length - 1];
        int element = 0x68;
        for (int i = password.Length - 1; i >= 0; i--)
        {
            int c = password[i];
            for (int bit = 0; bit < 7; bit++)
            {
                if ((c & 0x40) != 0)
                {
                    key ^= XorMatrix[element];
                }

                c <<= 1;
                element--;
            }
        }

        return key;
    }

    /// <summary>CreateXorArray_Method2: the password, padded, each byte XORed with the key's low or high byte and rotated right.</summary>
    internal static byte[] XorArray(ReadOnlySpan<byte> password)
    {
        ushort key = XorKeyMethod1(password);
        byte[] array = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            byte b = i < password.Length ? password[i] : PadArray[i - password.Length];
            int x = b ^ (i % 2 == 0 ? key & 0xFF : key >> 8);
            array[i] = (byte)((x >> 1) | (x << 7));
        }

        return array;
    }

    // ---- RC4 and RC4 CryptoAPI ([MS-OFFCRYPTO] 2.3.5 and 2.3.6) ----

    private static Func<string, IDecryptor?> RcKeyMaker(byte[] table, uint lKey)
    {
        if (lKey < 4 || lKey > table.Length)
        {
            throw new DocumentException("The encrypted Word document's encryption header is damaged.");
        }

        return RcKeyMaker(table.AsSpan(0, (int)lKey), BlockLength, "Word document");
    }

    /// <summary>
    /// Reads an RC4 or RC4 CryptoAPI encryption header ([MS-OFFCRYPTO] 2.3.6.1 and 2.3.5.1), as a Word or Excel 97-2003 file
    /// holds it, and gives the decryptor a password makes, or null for a wrong password. Every block of
    /// <paramref name="blockLength"/> bytes from the stream's start has its own key.
    /// </summary>
    /// <param name="header">The header, from its version.</param>
    /// <param name="blockLength">512 for Word, 1024 for Excel.</param>
    /// <param name="document">The kind of file the errors name, such as "Word document".</param>
    internal static Func<string, IDecryptor?> RcKeyMaker(ReadOnlySpan<byte> header, int blockLength, string document)
    {
        if (header.Length < 4)
        {
            throw new DocumentException($"The encrypted {document}'s encryption header is damaged.");
        }

        int major = BinaryPrimitives.ReadUInt16LittleEndian(header);
        int minor = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        if (major == 1 && minor == 1)
        {
            if (header.Length < 52)
            {
                throw new DocumentException($"The encrypted {document}'s RC4 encryption header is damaged.");
            }

            byte[] salt = header.Slice(4, 16).ToArray();
            byte[] verifier = header.Slice(20, 16).ToArray();
            byte[] verifierHash = header.Slice(36, 16).ToArray();
            return password => Rc4Key(password, salt, verifier, verifierHash, blockLength);
        }

        if (major is >= 2 and <= 4 && minor == 2)
        {
            return CryptoApiKeyMaker(header, blockLength, document);
        }

        throw new DocumentException($"The {document} is encrypted with an encryption Egap does not read (version {major}.{minor}).");
    }

    private static Rc4Decryptor? Rc4Key(string password, byte[] salt, byte[] verifier, byte[] verifierHash, int blockLength)
    {
        if (password.Length > OfficeEncryption.MaxPasswordLength)
        {
            return null;
        }

        byte[] h0 = MD5.HashData(Encoding.Unicode.GetBytes(password));
        byte[] buffer = new byte[16 * 21];
        for (int i = 0; i < 16; i++)
        {
            h0.AsSpan(0, 5).CopyTo(buffer.AsSpan(i * 21));
            salt.CopyTo(buffer.AsSpan((i * 21) + 5));
        }

        byte[] h1 = MD5.HashData(buffer);
        Rc4Decryptor decryptor = new(h1[..5], cryptoApi: false, keyBytes: 16, blockLength);
        return decryptor.Verify(verifier, verifierHash, sha1: false) ? decryptor : null;
    }

    private static Func<string, IDecryptor?> CryptoApiKeyMaker(ReadOnlySpan<byte> header, int blockLength, string document)
    {
        if (header.Length < 12)
        {
            throw new DocumentException($"The encrypted {document}'s RC4 CryptoAPI encryption header is damaged.");
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (headerSize < 32 || headerSize > header.Length - 12)
        {
            throw new DocumentException($"The encrypted {document}'s RC4 CryptoAPI encryption header is damaged.");
        }

        ReadOnlySpan<byte> encryption = header.Slice(12, (int)headerSize);
        uint algorithm = BinaryPrimitives.ReadUInt32LittleEndian(encryption[8..]);
        uint hash = BinaryPrimitives.ReadUInt32LittleEndian(encryption[12..]);
        uint keySize = BinaryPrimitives.ReadUInt32LittleEndian(encryption[16..]);
        keySize = keySize == 0 ? 40 : keySize;
        if ((algorithm != 0x6801 && algorithm != 0) || (hash != 0x8004 && hash != 0) || keySize < 40 || keySize > 128 || keySize % 8 != 0)
        {
            throw new DocumentException($"The {document} is encrypted with a CryptoAPI cipher Egap does not read: only RC4 with SHA-1 is defined for it.");
        }

        ReadOnlySpan<byte> rest = header[(12 + (int)headerSize)..];
        if (rest.Length < 4 + 16 + 16 + 4 + 20 || BinaryPrimitives.ReadUInt32LittleEndian(rest) != 16)
        {
            throw new DocumentException($"The encrypted {document}'s password verifier is damaged.");
        }

        byte[] salt = rest.Slice(4, 16).ToArray();
        byte[] verifier = rest.Slice(20, 16).ToArray();
        byte[] verifierHash = rest.Slice(40, 20).ToArray();
        int keyBytes = (int)keySize / 8;
        return password =>
        {
            if (password.Length > OfficeEncryption.MaxPasswordLength)
            {
                return null;
            }

            byte[] h0 = SHA1.HashData([.. salt, .. Encoding.Unicode.GetBytes(password)]);
            Rc4Decryptor decryptor = new(h0, cryptoApi: true, keyBytes, blockLength);
            return decryptor.Verify(verifier, verifierHash, sha1: true) ? decryptor : null;
        };
    }

    internal interface IDecryptor
    {
        /// <summary>Transforms the stream in place from <paramref name="start"/>; offsets count from the stream's start.</summary>
        void Apply(byte[] stream, int start);
    }

    private sealed class XorDecryptor(byte[] array) : IDecryptor
    {
        public void Apply(byte[] stream, int start)
        {
            // [MS-OFFCRYPTO] 2.3.7.6: a byte that is 0, or that the array's byte would make 0, stays as it is. The same test
            // undoes it: a stored 0 or a stored byte equal to the array's was stored as it was.
            for (int i = start; i < stream.Length; i++)
            {
                byte b = stream[i];
                byte k = array[i & 15];
                if (b != 0 && b != k)
                {
                    stream[i] = (byte)(b ^ k);
                }
            }
        }
    }

    private sealed class Rc4Decryptor(byte[] hash, bool cryptoApi, int keyBytes, int blockLength) : IDecryptor
    {
        /// <summary>The RC4 key of a block: the hash of the password hash and the block number, cut to the key size.</summary>
        private byte[] BlockKey(uint block)
        {
            byte[] input = [.. hash, 0, 0, 0, 0];
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(hash.Length), block);
            byte[] final = cryptoApi ? SHA1.HashData(input) : MD5.HashData(input);
            if (!cryptoApi)
            {
                return final;
            }

            // A 40-bit CryptoAPI key is padded with zeros to 128 bits ([MS-OFFCRYPTO] 2.3.5.2).
            byte[] key = new byte[keyBytes == 5 ? 16 : keyBytes];
            final.AsSpan(0, keyBytes).CopyTo(key);
            return key;
        }

        public bool Verify(byte[] verifier, byte[] verifierHash, bool sha1)
        {
            Rc4 rc4 = new(BlockKey(0));
            byte[] plain = [.. verifier];
            byte[] plainHash = [.. verifierHash];
            rc4.Apply(plain);
            rc4.Apply(plainHash);
            byte[] expected = sha1 ? SHA1.HashData(plain) : MD5.HashData(plain);
            return CryptographicOperations.FixedTimeEquals(expected, plainHash.AsSpan(0, expected.Length));
        }

        public void Apply(byte[] stream, int start)
        {
            // Every block from the stream's start has its own key; a clear prefix is skipped within its block.
            for (int block = start / blockLength; (long)block * blockLength < stream.Length; block++)
            {
                int from = block * blockLength;
                int to = Math.Min(stream.Length, from + blockLength);
                Rc4 rc4 = new(BlockKey((uint)block));
                if (start > from)
                {
                    rc4.Skip(start - from);
                    from = start;
                }

                rc4.Apply(stream.AsSpan(from, to - from));
            }
        }
    }

    /// <summary>The RC4 stream cipher.</summary>
    private struct Rc4
    {
        private readonly byte[] state = new byte[256];
        private int i;
        private int j;

        public Rc4(ReadOnlySpan<byte> key)
        {
            for (int n = 0; n < 256; n++)
            {
                state[n] = (byte)n;
            }

            int k = 0;
            for (int n = 0; n < 256; n++)
            {
                k = (k + state[n] + key[n % key.Length]) & 0xFF;
                (state[n], state[k]) = (state[k], state[n]);
            }
        }

        public void Apply(Span<byte> bytes)
        {
            for (int n = 0; n < bytes.Length; n++)
            {
                i = (i + 1) & 0xFF;
                j = (j + state[i]) & 0xFF;
                (state[i], state[j]) = (state[j], state[i]);
                bytes[n] ^= state[(state[i] + state[j]) & 0xFF];
            }
        }

        public void Skip(int count)
        {
            Span<byte> scratch = stackalloc byte[256];
            for (; count > 0; count -= scratch.Length)
            {
                Apply(scratch[..Math.Min(count, scratch.Length)]);
            }
        }
    }
}
