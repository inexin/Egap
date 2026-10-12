using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using Egap.Pdf.IO;

namespace Egap.Pdf.Security;

/// <summary>How strings and streams are encrypted: not at all, RC4, AES-128 or AES-256.</summary>
internal enum CryptMethod
{
    Identity,
    Rc4,
    Aes128,
    Aes256,
}

/// <summary>
/// The standard security handler (ISO 32000-2, 7.6.4): checks a password against <c>/O</c> and <c>/U</c> for revisions 2 to
/// 6, computes the file key, and decrypts strings and streams with RC4, AES-128 or AES-256, crypt filters included.
/// </summary>
[SuppressMessage("Security", "CA5351", Justification = "MD5 and RC4 are what the PDF format's older revisions use; this reads them, it does not choose them.")]
internal sealed class StandardSecurityHandler
{
    /// <summary>The 32-byte padding string for passwords (7.6.4.3, Algorithm 2).</summary>
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private readonly byte[] key;
    private readonly int revision;
    private readonly Dictionary<string, CryptMethod> cryptFilters;
    private readonly CryptMethod streamMethod;
    private readonly CryptMethod stringMethod;
    private readonly WarningLog log;

    private StandardSecurityHandler(byte[] key, int revision, Dictionary<string, CryptMethod> cryptFilters, CryptMethod streamMethod, CryptMethod stringMethod, bool encryptMetadata, int permissions, bool owner, WarningLog log)
    {
        this.key = key;
        this.revision = revision;
        this.cryptFilters = cryptFilters;
        this.streamMethod = streamMethod;
        this.stringMethod = stringMethod;
        EncryptMetadata = encryptMetadata;
        Permissions = permissions;
        IsOwner = owner;
        this.log = log;
    }

    /// <summary>The raw <c>/P</c> value.</summary>
    public int Permissions { get; }

    /// <summary>Whether the password given was the owner password.</summary>
    public bool IsOwner { get; }

    /// <summary>Whether metadata streams are encrypted too.</summary>
    public bool EncryptMetadata { get; }

    /// <summary>
    /// Sets up decryption for a file: <paramref name="password"/> as the owner password, then the empty user password, then
    /// <paramref name="password"/> as the user password.
    /// </summary>
    /// <exception cref="PdfPasswordException">No password given opens the file.</exception>
    /// <exception cref="PdfException">The file uses a security handler or revision this handler does not support.</exception>
    public static StandardSecurityHandler Open(PdfDictionary encrypt, byte[] firstId, string? password, WarningLog log)
    {
        string filter = encrypt.GetName("Filter")?.Value ?? "";
        if (filter != "Standard")
        {
            throw new PdfException($"The file is encrypted with the /{filter} security handler, which is not supported; only the standard (password) handler is.");
        }

        int version = (int)(encrypt.GetInt("V") ?? 0);
        int revision = (int)(encrypt.GetInt("R") ?? 0);
        if (revision is < 2 or > 6 || version is not (1 or 2 or 4 or 5))
        {
            throw new PdfException($"The file's encryption (/V {version}, /R {revision}) is not supported.");
        }

        byte[] o = encrypt.Get<PdfString>("O")?.Bytes.ToArray() ?? throw new PdfException("The encryption dictionary has no /O.");
        byte[] u = encrypt.Get<PdfString>("U")?.Bytes.ToArray() ?? throw new PdfException("The encryption dictionary has no /U.");
        int p = (int)(encrypt.GetInt("P") ?? -1);
        bool encryptMetadata = encrypt.GetBool("EncryptMetadata") ?? true;

        // Crypt filters (7.6.6): version 4 and 5 name them; earlier versions use RC4 for everything.
        Dictionary<string, CryptMethod> filters = [];
        CryptMethod streams = CryptMethod.Rc4, strings = CryptMethod.Rc4;
        int length = version == 1 ? 40 : (int)(encrypt.GetInt("Length") ?? 40);
        if (version >= 4)
        {
            if (encrypt.Get<PdfDictionary>("CF") is PdfDictionary cf)
            {
                foreach ((PdfName name, PdfObject value) in cf.Entries)
                {
                    if (value.Resolve() is PdfDictionary definition)
                    {
                        filters[name.Value] = definition.GetName("CFM")?.Value switch
                        {
                            "V2" => CryptMethod.Rc4,
                            "AESV2" => CryptMethod.Aes128,
                            "AESV3" => CryptMethod.Aes256,
                            _ => CryptMethod.Identity,
                        };
                        if (version == 4 && definition.GetInt("Length") is long bits)
                        {
                            // Some writers give the length in bytes, as the spec once said.
                            length = bits <= 16 ? (int)bits * 8 : (int)bits;
                        }
                    }
                }
            }

            streams = Method(encrypt.GetName("StmF")?.Value, filters);
            strings = Method(encrypt.GetName("StrF")?.Value, filters);
            if (version == 4 && (streams == CryptMethod.Aes128 || strings == CryptMethod.Aes128) && !encrypt.ContainsKey("Length"))
            {
                length = 128;
            }
        }

        if (revision <= 4 && (length < 40 || length > 128 || length % 8 != 0))
        {
            log.Add(PdfWarningCode.MalformedEncryption, $"The encryption key length {length} is not valid; 128 was used.");
            length = 128;
        }

        // A password given is tried as the owner password first, so an owner opening a file with an empty user password is
        // known as the owner; then the empty user password, then the password given as the user password.
        byte[]? fileKey = null;
        bool owner = false;
        if (revision >= 5)
        {
            byte[] oe = encrypt.Get<PdfString>("OE")?.Bytes.ToArray() ?? [];
            byte[] ue = encrypt.Get<PdfString>("UE")?.Bytes.ToArray() ?? [];
            if (password is not null)
            {
                fileKey = Open256(Utf8Password(password), o, u, oe, ue, revision, out owner);
            }

            if (fileKey is null)
            {
                owner = false;
                fileKey = Open256(Utf8Password(""), o, u, oe, ue, revision, out _);
            }
        }
        else
        {
            int keyBytes = revision == 2 ? 5 : length / 8;
            byte[]? given = password is null ? null : Latin1Password(password);
            if (given is not null)
            {
                fileKey = TryUser(UserPasswordFromOwner(given, o, revision, keyBytes), o, u, p, firstId, revision, keyBytes, encryptMetadata);
                owner = fileKey is not null;
            }

            fileKey ??= TryUser([], o, u, p, firstId, revision, keyBytes, encryptMetadata);
            if (fileKey is null && given is not null)
            {
                fileKey = TryUser(given, o, u, p, firstId, revision, keyBytes, encryptMetadata);
            }
        }

        if (fileKey is null)
        {
            throw new PdfPasswordException(password is null ? "The file is encrypted and needs a password." : "The password is not the file's user or owner password.");
        }

        return new StandardSecurityHandler(fileKey, revision, filters, streams, strings, encryptMetadata, p, owner, log);
    }

    private static CryptMethod Method(string? name, Dictionary<string, CryptMethod> filters)
    {
        return name is null or "Identity" ? CryptMethod.Identity : filters.GetValueOrDefault(name, CryptMethod.Identity);
    }

    /// <summary>Decrypts a string of object <paramref name="number"/> generation <paramref name="generation"/>.</summary>
    public byte[] DecryptString(ReadOnlySpan<byte> data, int number, int generation)
    {
        return Decrypt(stringMethod, data, number, generation);
    }

    /// <summary>Decrypts a stream's data, with its own crypt filter when <paramref name="cryptFilter"/> names one.</summary>
    public byte[] DecryptStream(ReadOnlySpan<byte> data, int number, int generation, string? cryptFilter)
    {
        CryptMethod method = cryptFilter is null ? streamMethod : Method(cryptFilter, cryptFilters);
        return Decrypt(method, data, number, generation);
    }

    private byte[] Decrypt(CryptMethod method, ReadOnlySpan<byte> data, int number, int generation)
    {
        switch (method)
        {
            case CryptMethod.Identity:
                return data.ToArray();
            case CryptMethod.Rc4:
                return Rc4.Apply(ObjectKey(number, generation, aes: false), data);
            default:
                byte[] objectKey = method == CryptMethod.Aes256 || revision >= 5 ? key : ObjectKey(number, generation, aes: true);
                return DecryptAes(objectKey, data, number);
        }
    }

    /// <summary>The key for one object (7.6.3.3, Algorithm 1): the file key, number, generation and, for AES, "sAlT".</summary>
    private byte[] ObjectKey(int number, int generation, bool aes)
    {
        byte[] input = new byte[key.Length + 5 + (aes ? 4 : 0)];
        key.CopyTo(input, 0);
        input[key.Length] = (byte)number;
        input[key.Length + 1] = (byte)(number >> 8);
        input[key.Length + 2] = (byte)(number >> 16);
        input[key.Length + 3] = (byte)generation;
        input[key.Length + 4] = (byte)(generation >> 8);
        if (aes)
        {
            "sAlT"u8.CopyTo(input.AsSpan(key.Length + 5));
        }

        return MD5.HashData(input)[..Math.Min(key.Length + 5, 16)];
    }

    /// <summary>AES in CBC mode with the initialization vector in the first 16 bytes and PKCS#7 padding (7.6.3.2).</summary>
    private byte[] DecryptAes(byte[] objectKey, ReadOnlySpan<byte> data, int number)
    {
        if (data.Length < 16)
        {
            // No room for the initialization vector: nothing was encrypted (empty strings are sometimes written this way).
            return data.Length == 0 ? [] : Damaged(data, number, "is shorter than an AES block");
        }

        ReadOnlySpan<byte> iv = data[..16];
        ReadOnlySpan<byte> body = data[16..];
        if (body.Length % 16 != 0)
        {
            body = body[..(body.Length / 16 * 16)];
            log.Add(PdfWarningCode.MalformedEncryption, "AES-encrypted data is not a whole number of blocks; the last partial block was dropped.", null, number);
        }

        using Aes aes = Aes.Create();
        aes.Key = objectKey;
        try
        {
            return aes.DecryptCbc(body, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            log.Add(PdfWarningCode.MalformedEncryption, "AES-encrypted data has invalid padding; it was kept unpadded.", null, number);
            return aes.DecryptCbc(body, iv, PaddingMode.None);
        }
    }

    private byte[] Damaged(ReadOnlySpan<byte> data, int number, string problem)
    {
        log.Add(PdfWarningCode.MalformedEncryption, $"Encrypted data {problem}; it was kept as it is.", null, number);
        return data.ToArray();
    }

    /// <summary>Algorithms 2 and 6: the file key for a user password, or null when the password is not the user's.</summary>
    private static byte[]? TryUser(byte[] password, byte[] o, byte[] u, int p, byte[] firstId, int revision, int keyBytes, bool encryptMetadata)
    {
        byte[] fileKey = FileKey(password, o, p, firstId, revision, keyBytes, encryptMetadata);
        byte[] expected;
        if (revision == 2)
        {
            expected = Rc4.Apply(fileKey, Padding);
            return u.Length >= 32 && expected.AsSpan().SequenceEqual(u.AsSpan(0, 32)) ? fileKey : null;
        }

        expected = Rc4.Apply(fileKey, MD5.HashData([.. Padding, .. firstId]));
        for (int i = 1; i <= 19; i++)
        {
            expected = Rc4.Apply(Xor(fileKey, i), expected);
        }

        // Only the first 16 bytes are significant (Algorithm 5).
        return u.Length >= 16 && expected.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16)) ? fileKey : null;
    }

    /// <summary>Algorithm 2: the file key from a password for revisions 2 to 4.</summary>
    private static byte[] FileKey(byte[] password, byte[] o, int p, byte[] firstId, int revision, int keyBytes, bool encryptMetadata)
    {
        List<byte> input = [.. Pad(password), .. o.AsSpan(0, Math.Min(32, o.Length)), (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24), .. firstId];
        if (revision >= 4 && !encryptMetadata)
        {
            input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        }

        byte[] hash = MD5.HashData([.. input]);
        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash.AsSpan(0, keyBytes));
            }
        }

        return hash[..keyBytes];
    }

    /// <summary>Algorithm 7: recovers the padded user password from the owner password and <c>/O</c>.</summary>
    private static byte[] UserPasswordFromOwner(byte[] ownerPassword, byte[] o, int revision, int keyBytes)
    {
        byte[] hash = MD5.HashData(Pad(ownerPassword));
        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash);
            }
        }

        byte[] rc4Key = hash[..keyBytes];
        byte[] data = o.AsSpan(0, Math.Min(32, o.Length)).ToArray();
        if (revision == 2)
        {
            return Rc4.Apply(rc4Key, data);
        }

        for (int i = 19; i >= 0; i--)
        {
            data = Rc4.Apply(Xor(rc4Key, i), data);
        }

        return data;
    }

    /// <summary>
    /// Algorithms 2.A and 2.B (revisions 5 and 6): checks the password as owner, then as user, and returns the file key from
    /// <c>/OE</c> or <c>/UE</c>, or null.
    /// </summary>
    private static byte[]? Open256(byte[] password, byte[] o, byte[] u, byte[] oe, byte[] ue, int revision, out bool owner)
    {
        owner = false;
        if (o.Length < 48 || u.Length < 48)
        {
            return null;
        }

        if (Hash256(password, o.AsSpan(32, 8), u.AsSpan(0, 48), revision).AsSpan().SequenceEqual(o.AsSpan(0, 32)))
        {
            owner = true;
            return DecryptKey(Hash256(password, o.AsSpan(40, 8), u.AsSpan(0, 48), revision), oe);
        }

        if (Hash256(password, u.AsSpan(32, 8), [], revision).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
        {
            return DecryptKey(Hash256(password, u.AsSpan(40, 8), [], revision), ue);
        }

        return null;
    }

    [SuppressMessage("Security", "CA5358", Justification = "The spec decrypts /OE and /UE with AES-256 and a zero initialization vector.")]
    private static byte[]? DecryptKey(byte[] intermediate, byte[] encrypted)
    {
        if (encrypted.Length < 32)
        {
            return null;
        }

        using Aes aes = Aes.Create();
        aes.Key = intermediate;
        return aes.DecryptCbc(encrypted.AsSpan(0, 32), new byte[16], PaddingMode.None);
    }

    /// <summary>Revision 5 hashes once with SHA-256; revision 6 uses Algorithm 2.B's rounds of SHA-2 and AES-128.</summary>
    private static byte[] Hash256(byte[] password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userKey, int revision)
    {
        byte[] k = SHA256.HashData([.. password, .. salt, .. userKey]);
        if (revision == 5)
        {
            return k;
        }

        byte[] userKeyBytes = userKey.ToArray();
        using Aes aes = Aes.Create();
        for (int round = 0; ; round++)
        {
            byte[] block = [.. password, .. k, .. userKeyBytes];
            byte[] k1 = new byte[block.Length * 64];
            for (int i = 0; i < 64; i++)
            {
                block.CopyTo(k1, i * block.Length);
            }

            aes.Key = k.AsSpan(0, 16).ToArray();
            byte[] e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            int sum = 0;
            for (int i = 0; i < 16; i++)
            {
                sum += e[i];
            }

            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            if (round >= 63 && e[^1] <= round - 31)
            {
                return k[..32];
            }
        }
    }

    private static byte[] Pad(byte[] password)
    {
        byte[] padded = new byte[32];
        int count = Math.Min(32, password.Length);
        password.AsSpan(0, count).CopyTo(padded);
        Padding.AsSpan(0, 32 - count).CopyTo(padded.AsSpan(count));
        return padded;
    }

    private static byte[] Xor(byte[] key, int value)
    {
        byte[] result = new byte[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            result[i] = (byte)(key[i] ^ value);
        }

        return result;
    }

    /// <summary>Revisions 2 to 4 take the password in PDFDocEncoding; characters outside Latin-1 become '?'.</summary>
    private static byte[] Latin1Password(string password)
    {
        return Objects.PdfDocEncoding.TryEncode(password) ?? Encoding.Latin1.GetBytes(password);
    }

    /// <summary>Revisions 5 and 6 take the password as UTF-8, at most 127 bytes (7.6.4.3.3).</summary>
    private static byte[] Utf8Password(string password)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        return bytes.Length > 127 ? bytes[..127] : bytes;
    }
}
