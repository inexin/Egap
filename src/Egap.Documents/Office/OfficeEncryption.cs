using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Egap.Documents.Office;

/// <summary>
/// Office's encryption of OOXML packages ([MS-OFFCRYPTO] 2.3.4): a compound file whose <c>EncryptionInfo</c> stream says how
/// the key is made from the password and whose <c>EncryptedPackage</c> stream holds the ZIP package, encrypted. Reads agile
/// encryption (Office 2010 and later; AES with SHA-1 to SHA-512, checked with its data-integrity HMAC) and standard
/// encryption (Office 2007; AES-128 to 256 with SHA-1), and writes agile encryption with AES-256, SHA-512 and 100,000 key
/// spins, as Word 2013 and later do.
/// </summary>
internal static class OfficeEncryption
{
    /// <summary>The password Word takes at most, in UTF-16 code units.</summary>
    public const int MaxPasswordLength = 255;

    private const int SegmentLength = 4096;
    private const int MaxSpinCount = 10_000_000;            // [MS-OFFCRYPTO] 2.3.4.10: spinCount MUST NOT be more
    private const string EncryptionNamespace = "http://schemas.microsoft.com/office/2006/encryption";
    private const string PasswordNamespace = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";
    private const string PasswordEncryptorUri = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";

    // The block keys of [MS-OFFCRYPTO] 2.3.4.11 to 2.3.4.14.
    private static ReadOnlySpan<byte> VerifierInputBlock => [0xFE, 0xA7, 0xD2, 0x76, 0x3B, 0x4B, 0x9E, 0x79];

    private static ReadOnlySpan<byte> VerifierValueBlock => [0xD7, 0xAA, 0x0F, 0x6D, 0x30, 0x61, 0x34, 0x4E];

    private static ReadOnlySpan<byte> KeyValueBlock => [0x14, 0x6E, 0x0B, 0xE7, 0xAB, 0xAC, 0xD0, 0xD6];

    private static ReadOnlySpan<byte> HmacKeyBlock => [0x5F, 0xB2, 0xAD, 0x01, 0x0C, 0xB9, 0xE1, 0xF6];

    private static ReadOnlySpan<byte> HmacValueBlock => [0xA0, 0x67, 0x7F, 0x02, 0xB2, 0x2C, 0x84, 0x33];

    /// <summary>Whether the compound file holds an encrypted package (its <c>EncryptionInfo</c> and <c>EncryptedPackage</c> streams).</summary>
    public static bool IsEncryptedPackage(CompoundFile file)
    {
        return file.Find("EncryptionInfo") is { Type: CompoundFileEntryType.Stream }
            && file.Find("EncryptedPackage") is { Type: CompoundFileEntryType.Stream };
    }

    /// <summary>
    /// Whether the bytes are a compound file that holds an encrypted package. Only the header, the FAT and the directory are
    /// read; a damaged file is not one.
    /// </summary>
    public static bool IsEncryptedPackage(Stream source)
    {
        long start = source.Position;
        try
        {
            Span<byte> head = stackalloc byte[8];
            source.Position = 0;
            return source.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && CompoundFile.IsCompoundFile(head)
                && IsEncryptedPackage(CompoundFile.Read(source, new DocumentLimits()));
        }
        catch (Exception e) when (e is DocumentException or IOException)
        {
            return false;
        }
        finally
        {
            source.Position = start;
        }
    }

    /// <summary>
    /// Decrypts the package: tries <paramref name="password"/>, then each password <paramref name="callback"/> gives (with
    /// the attempt number from 1) until it gives null.
    /// </summary>
    /// <returns>The package's bytes: a ZIP file.</returns>
    /// <exception cref="DocumentPasswordException">No password was given, or none of them was right.</exception>
    /// <exception cref="DocumentException">The encryption is one Egap does not read, or the file is damaged.</exception>
    /// <exception cref="DocumentLimitException">The package is larger than <see cref="DocumentLimits.MaxPartSize"/>.</exception>
    public static byte[] Decrypt(CompoundFile file, string? password, Func<int, string?>? callback, DocumentLimits limits)
    {
        if (file.Find("\u0009DRMContent") is not null || file.Find("\u0006DataSpaces/TransformInfo/DRMEncryptedTransform") is not null)
        {
            throw new DocumentException("The file is protected with rights management (IRM), which needs a rights server; Egap does not open it.");
        }

        byte[] info = file.ReadStream("EncryptionInfo") ?? throw new DocumentException("The encrypted file has no EncryptionInfo stream.");
        IKeyMaker maker = ReadInfo(info);
        CompoundFileEntry packageEntry = file.Find("EncryptedPackage") ?? throw new DocumentException("The encrypted file has no EncryptedPackage stream.");
        if (packageEntry.Size > limits.MaxPartSize + SegmentLength + 8)
        {
            throw new DocumentLimitException($"The encrypted package is {packageEntry.Size} bytes, more than the limit of {limits.MaxPartSize}.");
        }

        byte[]? key = null;
        bool given = password is not null;
        if (password is not null)
        {
            key = maker.KeyFor(password);
        }

        for (int attempt = 1; key is null && callback is not null; attempt++)
        {
            string? next = callback(attempt);
            if (next is null)
            {
                break;
            }

            given = true;
            key = maker.KeyFor(next);
        }

        if (key is null)
        {
            throw given
                ? new DocumentPasswordException("The password is wrong: the document is encrypted with another one.", wrongPassword: true)
                : new DocumentPasswordException("The document is encrypted: give its password in the load options.", wrongPassword: false);
        }

        byte[] encrypted = file.ReadStream(packageEntry);
        if (encrypted.Length < 8)
        {
            throw new DocumentException("The encrypted package is damaged: it is shorter than its size field.");
        }

        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(encrypted);
        if (size > (ulong)limits.MaxPartSize)
        {
            throw new DocumentLimitException($"The encrypted package says it is {size} bytes, more than the limit of {limits.MaxPartSize}.");
        }

        if (size > (ulong)(encrypted.Length - 8))
        {
            throw new DocumentException("The encrypted package is damaged: it is shorter than its size field says.");
        }

        maker.CheckIntegrity(key, encrypted);
        return maker.Decrypt(key, encrypted, (int)size);
    }

    /// <summary>
    /// Encrypts a package with agile encryption (AES-256 in CBC mode, SHA-512, 100,000 spins and a data-integrity HMAC) and
    /// writes the compound file Word writes for it, with its data spaces.
    /// </summary>
    public static void Encrypt(ReadOnlySpan<byte> package, string password, Stream output, int spinCount = 100_000)
    {
        ArgumentNullException.ThrowIfNull(password);
        const int keyBytes = 32, blockSize = 16, hashSize = 64, saltSize = 16;
        byte[] keySalt = RandomNumberGenerator.GetBytes(saltSize);
        byte[] passwordSalt = RandomNumberGenerator.GetBytes(saltSize);
        byte[] secretKey = RandomNumberGenerator.GetBytes(keyBytes);
        byte[] verifierInput = RandomNumberGenerator.GetBytes(saltSize);
        byte[] hmacKey = RandomNumberGenerator.GetBytes(hashSize);
        HashAlgorithmName hash = HashAlgorithmName.SHA512;

        using Aes aes = Aes.Create();
        byte[] passwordHash = SpinHash(hash, passwordSalt, password, spinCount);
        byte[] EncryptWithPassword(ReadOnlySpan<byte> block, ReadOnlySpan<byte> data)
        {
            aes.Key = Fit(Hash(hash, passwordHash, block), keyBytes);
            return aes.EncryptCbc(Pad(data, blockSize), Fit(passwordSalt, blockSize), PaddingMode.None);
        }

        byte[] encryptedVerifierInput = EncryptWithPassword(VerifierInputBlock, verifierInput);
        byte[] encryptedVerifierValue = EncryptWithPassword(VerifierValueBlock, Hash(hash, verifierInput, []));
        byte[] encryptedKey = EncryptWithPassword(KeyValueBlock, secretKey);

        // The package: its size, then 4096-byte segments, each in CBC mode with its own IV from the key data's salt.
        int segments = (package.Length + SegmentLength - 1) / SegmentLength;
        byte[] stream = new byte[8 + (package.Length + blockSize - 1) / blockSize * blockSize];
        BinaryPrimitives.WriteUInt64LittleEndian(stream, (ulong)package.Length);
        aes.Key = secretKey;
        Span<byte> index = stackalloc byte[4];
        byte[] last = new byte[SegmentLength];
        for (int i = 0; i < segments; i++)
        {
            int start = i * SegmentLength;
            int length = Math.Min(SegmentLength, package.Length - start);
            int padded = (length + blockSize - 1) / blockSize * blockSize;
            BinaryPrimitives.WriteUInt32LittleEndian(index, (uint)i);
            byte[] iv = Fit(Hash(hash, keySalt, index), blockSize);
            ReadOnlySpan<byte> plain = package.Slice(start, length);
            if (padded != length)
            {
                Array.Clear(last);
                plain.CopyTo(last);
                plain = last.AsSpan(0, padded);
            }

            aes.EncryptCbc(plain, iv, stream.AsSpan(8 + start, padded), PaddingMode.None);
        }

        byte[] encryptedHmacKey = aes.EncryptCbc(hmacKey, Fit(Hash(hash, keySalt, HmacKeyBlock), blockSize), PaddingMode.None);
        byte[] hmacValue = HMACSHA512.HashData(hmacKey, stream);
        byte[] encryptedHmacValue = aes.EncryptCbc(hmacValue, Fit(Hash(hash, keySalt, HmacValueBlock), blockSize), PaddingMode.None);

        string Params(string salt) =>
            $"saltSize=\"{saltSize}\" blockSize=\"{blockSize}\" keyBits=\"{keyBytes * 8}\" hashSize=\"{hashSize}\" cipherAlgorithm=\"AES\" cipherChaining=\"ChainingModeCBC\" hashAlgorithm=\"SHA512\" saltValue=\"{salt}\"";
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n"
            + $"<encryption xmlns=\"{EncryptionNamespace}\" xmlns:p=\"{PasswordNamespace}\" xmlns:c=\"http://schemas.microsoft.com/office/2006/keyEncryptor/certificate\">"
            + $"<keyData {Params(Convert.ToBase64String(keySalt))}/>"
            + $"<dataIntegrity encryptedHmacKey=\"{Convert.ToBase64String(encryptedHmacKey)}\" encryptedHmacValue=\"{Convert.ToBase64String(encryptedHmacValue)}\"/>"
            + $"<keyEncryptors><keyEncryptor uri=\"{PasswordEncryptorUri}\"><p:encryptedKey spinCount=\"{spinCount}\" {Params(Convert.ToBase64String(passwordSalt))}"
            + $" encryptedVerifierHashInput=\"{Convert.ToBase64String(encryptedVerifierInput)}\" encryptedVerifierHashValue=\"{Convert.ToBase64String(encryptedVerifierValue)}\""
            + $" encryptedKeyValue=\"{Convert.ToBase64String(encryptedKey)}\"/></keyEncryptor></keyEncryptors></encryption>";
        byte[] info = [4, 0, 4, 0, 0x40, 0, 0, 0, .. Encoding.UTF8.GetBytes(xml)];

        CompoundFileWriter.Write(output,
        [
            ("\u0006DataSpaces/Version", DataSpaceVersion()),
            ("\u0006DataSpaces/DataSpaceMap", DataSpaceMap()),
            ("\u0006DataSpaces/DataSpaceInfo/StrongEncryptionDataSpace", DataSpaceDefinition()),
            ("\u0006DataSpaces/TransformInfo/StrongEncryptionTransform/\u0006Primary", TransformInfo()),
            ("EncryptionInfo", info),
            ("EncryptedPackage", stream),
        ]);
    }

    private static IKeyMaker ReadInfo(byte[] info)
    {
        if (info.Length < 8)
        {
            throw new DocumentException("The encrypted file's EncryptionInfo stream is damaged.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(info);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(2));
        return (major, minor) switch
        {
            (4, 4) => AgileKeyMaker.Read(info.AsSpan(8)),
            (2 or 3 or 4, 2) => StandardKeyMaker.Read(info.AsSpan(4)),
            (3 or 4, 3) => throw new DocumentException("The document uses extensible encryption, a third-party cryptographic module Egap does not read."),
            _ => throw new DocumentException($"The document's encryption (version {major}.{minor}) is not one Egap reads."),
        };
    }

    // H(a + b) with the named hash.
    private static byte[] Hash(HashAlgorithmName name, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(name);
        hash.AppendData(a);
        hash.AppendData(b);
        return hash.GetHashAndReset();
    }

    // [MS-OFFCRYPTO] 2.3.4.7 and 2.3.4.11: H0 = H(salt + password), then H(i + H) for each spin, i as 4 bytes little-endian.
    private static byte[] SpinHash(HashAlgorithmName name, ReadOnlySpan<byte> salt, string password, int spinCount)
    {
        byte[] h = Hash(name, salt, Encoding.Unicode.GetBytes(password));
        byte[] buffer = new byte[4 + h.Length];
        h.CopyTo(buffer, 4);
        Span<byte> next = stackalloc byte[h.Length];
        for (int i = 0; i < spinCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)i);
            int written = CryptographicOperations.HashData(name, buffer, next);
            next[..written].CopyTo(buffer.AsSpan(4));
        }

        return buffer[4..];
    }

    // A key or IV of the given length: cut, or filled up with 0x36 ([MS-OFFCRYPTO] 2.3.4.11, 2.3.4.12).
    private static byte[] Fit(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }

        byte[] result = new byte[length];
        result.AsSpan().Fill(0x36);
        value.AsSpan(0, Math.Min(length, value.Length)).CopyTo(result);
        return result;
    }

    private static byte[] Pad(ReadOnlySpan<byte> data, int blockSize)
    {
        byte[] result = new byte[(data.Length + blockSize - 1) / blockSize * blockSize];
        data.CopyTo(result);
        return result;
    }

    // UNICODE-LP-P4 ([MS-OFFCRYPTO] 2.1.2): the byte length, the UTF-16 text, padding to 4 bytes.
    private static void WriteString(List<byte> to, string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        WriteInt(to, bytes.Length);
        to.AddRange(bytes);
        while (to.Count % 4 != 0)
        {
            to.Add(0);
        }
    }

    private static void WriteInt(List<byte> to, int value)
    {
        to.Add((byte)value);
        to.Add((byte)(value >> 8));
        to.Add((byte)(value >> 16));
        to.Add((byte)(value >> 24));
    }

    // Reader, updater and writer versions, each 1.0.
    private static void WriteVersions(List<byte> to)
    {
        for (int i = 0; i < 3; i++)
        {
            to.AddRange([1, 0, 0, 0]);
        }
    }

    // DataSpaceVersionInfo ([MS-OFFCRYPTO] 2.1.5).
    private static byte[] DataSpaceVersion()
    {
        List<byte> bytes = [];
        WriteString(bytes, "Microsoft.Container.DataSpaces");
        WriteVersions(bytes);
        return [.. bytes];
    }

    // DataSpaceMap ([MS-OFFCRYPTO] 2.1.6): one entry, the EncryptedPackage stream in the StrongEncryptionDataSpace.
    private static byte[] DataSpaceMap()
    {
        List<byte> entry = [];
        WriteInt(entry, 1);                     // one reference component
        WriteInt(entry, 0);                     // a stream
        WriteString(entry, "EncryptedPackage");
        WriteString(entry, "StrongEncryptionDataSpace");
        List<byte> bytes = [];
        WriteInt(bytes, 8);
        WriteInt(bytes, 1);
        WriteInt(bytes, entry.Count + 4);
        bytes.AddRange(entry);
        return [.. bytes];
    }

    // DataSpaceDefinition ([MS-OFFCRYPTO] 2.1.7): the data space has one transform.
    private static byte[] DataSpaceDefinition()
    {
        List<byte> bytes = [];
        WriteInt(bytes, 8);
        WriteInt(bytes, 1);
        WriteString(bytes, "StrongEncryptionTransform");
        return [.. bytes];
    }

    // TransformInfoHeader and EncryptionTransformInfo ([MS-OFFCRYPTO] 2.1.8, 2.1.9) for the encryption transform.
    private static byte[] TransformInfo()
    {
        List<byte> id = [];
        WriteString(id, "{FF9A3F03-56EF-4613-BDD5-5A41C1D07246}");
        List<byte> bytes = [];
        WriteInt(bytes, 8 + id.Count);          // the header's length up to and with the transform id
        WriteInt(bytes, 1);                     // the transform type
        bytes.AddRange(id);
        WriteString(bytes, "Microsoft.Container.EncryptionTransform");
        WriteVersions(bytes);
        WriteInt(bytes, 0);                     // no encryption name
        WriteInt(bytes, 0);                     // block size
        WriteInt(bytes, 0);                     // cipher mode
        WriteInt(bytes, 4);                     // reserved
        return [.. bytes];
    }

    private static HashAlgorithmName HashNamed(string? name, out int size) => name switch
    {
        "SHA1" or "SHA-1" => Sized(HashAlgorithmName.SHA1, 20, out size),
        "SHA256" => Sized(HashAlgorithmName.SHA256, 32, out size),
        "SHA384" => Sized(HashAlgorithmName.SHA384, 48, out size),
        "SHA512" => Sized(HashAlgorithmName.SHA512, 64, out size),
        "MD5" => Sized(HashAlgorithmName.MD5, 16, out size),
        _ => throw new DocumentException($"The document is encrypted with the hash \"{name}\", which Egap does not read."),
    };

    private static HashAlgorithmName Sized(HashAlgorithmName name, int length, out int size)
    {
        size = length;
        return name;
    }

    /// <summary>Makes the package's key from a password and decrypts with it.</summary>
    private interface IKeyMaker
    {
        /// <summary>The package's key, or null when the password is wrong.</summary>
        byte[]? KeyFor(string password);

        /// <summary>Throws when the package's HMAC does not match.</summary>
        void CheckIntegrity(byte[] key, byte[] encryptedPackage);

        /// <summary>The package's first <paramref name="size"/> bytes, decrypted.</summary>
        byte[] Decrypt(byte[] key, byte[] encryptedPackage, int size);
    }

    /// <summary>Agile encryption ([MS-OFFCRYPTO] 2.3.4.10): an XML description of the key data and the password key encryptor.</summary>
    private sealed class AgileKeyMaker : IKeyMaker
    {
        private Params keyData;
        private Params passwordKey;
        private int spinCount;
        private byte[] verifierInput = [];
        private byte[] verifierValue = [];
        private byte[] keyValue = [];
        private byte[]? hmacKey;
        private byte[]? hmacValue;

        public static AgileKeyMaker Read(ReadOnlySpan<byte> xml)
        {
            AgileKeyMaker maker = new();
            bool sawKeyData = false, sawPassword = false;
            try
            {
                using MemoryStream stream = new(xml.ToArray(), writable: false);
                using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (reader.LocalName == "keyData" && reader.NamespaceURI == EncryptionNamespace)
                    {
                        maker.keyData = Params.Read(reader);
                        sawKeyData = true;
                    }
                    else if (reader.LocalName == "dataIntegrity" && reader.NamespaceURI == EncryptionNamespace)
                    {
                        maker.hmacKey = Base64(reader, "encryptedHmacKey");
                        maker.hmacValue = Base64(reader, "encryptedHmacValue");
                    }
                    else if (reader.LocalName == "encryptedKey" && reader.NamespaceURI == PasswordNamespace && !sawPassword)
                    {
                        maker.passwordKey = Params.Read(reader);
                        maker.spinCount = int.TryParse(reader.GetAttribute("spinCount"), out int spins) && spins is >= 0 and <= MaxSpinCount
                            ? spins
                            : throw new DocumentException("The encrypted file's spin count is missing or past the 10,000,000 the format allows.");
                        maker.verifierInput = Base64(reader, "encryptedVerifierHashInput") ?? [];
                        maker.verifierValue = Base64(reader, "encryptedVerifierHashValue") ?? [];
                        maker.keyValue = Base64(reader, "encryptedKeyValue") ?? [];
                        sawPassword = true;
                    }
                }
            }
            catch (XmlException e)
            {
                throw new DocumentException("The encrypted file's EncryptionInfo is not well-formed XML.", e);
            }

            if (!sawKeyData)
            {
                throw new DocumentException("The encrypted file's EncryptionInfo has no key data.");
            }

            if (!sawPassword)
            {
                throw new DocumentException("The document is encrypted for a certificate, not with a password; Egap opens only password-encrypted files.");
            }

            return maker;
        }

        public byte[]? KeyFor(string password)
        {
            byte[] hash = SpinHash(passwordKey.Hash, passwordKey.Salt, password, spinCount);
            using Aes aes = Aes.Create();
            byte[] Decrypt(ReadOnlySpan<byte> block, byte[] data)
            {
                aes.Key = Fit(Hash(passwordKey.Hash, hash, block), passwordKey.KeyBytes);
                return aes.DecryptCbc(Whole(data, passwordKey.BlockSize), Fit(passwordKey.Salt, passwordKey.BlockSize), PaddingMode.None);
            }

            byte[] input = Decrypt(VerifierInputBlock, verifierInput);
            byte[] value = Decrypt(VerifierValueBlock, verifierValue);
            byte[] expected = Hash(passwordKey.Hash, input.AsSpan(0, Math.Min(input.Length, passwordKey.Salt.Length)), []);
            if (value.Length < expected.Length || !CryptographicOperations.FixedTimeEquals(value.AsSpan(0, expected.Length), expected))
            {
                return null;
            }

            byte[] key = Decrypt(KeyValueBlock, keyValue);
            return key.Length >= keyData.KeyBytes ? key[..keyData.KeyBytes] : throw new DocumentException("The encrypted file's key is shorter than its key size.");
        }

        public void CheckIntegrity(byte[] key, byte[] encryptedPackage)
        {
            if (hmacKey is null || hmacValue is null)
            {
                return;
            }

            using Aes aes = Aes.Create();
            aes.Key = key;
            byte[] macKey = aes.DecryptCbc(Whole(hmacKey, keyData.BlockSize), Fit(Hash(keyData.Hash, keyData.Salt, HmacKeyBlock), keyData.BlockSize), PaddingMode.None);
            byte[] mac = aes.DecryptCbc(Whole(hmacValue, keyData.BlockSize), Fit(Hash(keyData.Hash, keyData.Salt, HmacValueBlock), keyData.BlockSize), PaddingMode.None);
            int size = keyData.HashSize;
            byte[] actual = CryptographicOperations.HmacData(keyData.Hash, macKey.AsSpan(0, Math.Min(size, macKey.Length)), encryptedPackage);
            if (mac.Length < size || !CryptographicOperations.FixedTimeEquals(mac.AsSpan(0, size), actual))
            {
                throw new DocumentException("The encrypted package fails its integrity check: the file is damaged or was changed.");
            }
        }

        public byte[] Decrypt(byte[] key, byte[] encryptedPackage, int size)
        {
            byte[] result = new byte[size];
            using Aes aes = Aes.Create();
            aes.Key = key;
            int block = keyData.BlockSize;
            Span<byte> index = stackalloc byte[4];
            byte[] segment = new byte[SegmentLength];
            for (int i = 0, at = 0; at < size; i++, at += SegmentLength)
            {
                int available = Math.Min(SegmentLength, encryptedPackage.Length - 8 - at) / block * block;
                BinaryPrimitives.WriteUInt32LittleEndian(index, (uint)i);
                byte[] iv = Fit(Hash(keyData.Hash, keyData.Salt, index), block);
                int written = aes.DecryptCbc(encryptedPackage.AsSpan(8 + at, available), iv, segment, PaddingMode.None);
                segment.AsSpan(0, Math.Min(written, size - at)).CopyTo(result.AsSpan(at));
            }

            return result;
        }

        private static byte[]? Base64(XmlReader reader, string name)
        {
            string? value = reader.GetAttribute(name);
            if (value is null)
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException e)
            {
                throw new DocumentException($"The encrypted file's {name} is not base64.", e);
            }
        }

        // Whole blocks of the data, dropping a damaged tail.
        private static ReadOnlySpan<byte> Whole(byte[] data, int block) => data.AsSpan(0, data.Length / block * block);

        /// <summary>The cipher parameters keyData and encryptedKey share ([MS-OFFCRYPTO] 2.3.4.10).</summary>
        private readonly record struct Params(HashAlgorithmName Hash, int HashSize, int KeyBytes, int BlockSize, byte[] Salt)
        {
            public static Params Read(XmlReader reader)
            {
                if (reader.GetAttribute("cipherAlgorithm") is not "AES")
                {
                    throw new DocumentException($"The document is encrypted with the cipher \"{reader.GetAttribute("cipherAlgorithm")}\"; Egap reads AES only.");
                }

                if (reader.GetAttribute("cipherChaining") is not "ChainingModeCBC")
                {
                    throw new DocumentException($"The document is encrypted in the chaining mode \"{reader.GetAttribute("cipherChaining")}\"; Egap reads CBC only.");
                }

                HashAlgorithmName hash = HashNamed(reader.GetAttribute("hashAlgorithm"), out int hashSize);
                int keyBits = int.TryParse(reader.GetAttribute("keyBits"), out int bits) ? bits : 0;
                if (keyBits is not (128 or 192 or 256))
                {
                    throw new DocumentException($"The document is encrypted with a {keyBits}-bit key; AES takes 128, 192 or 256 bits.");
                }

                if (!int.TryParse(reader.GetAttribute("blockSize"), out int blockSize) || blockSize != 16)
                {
                    throw new DocumentException("The encrypted file's block size is not AES's 16 bytes.");
                }

                byte[] salt = Base64(reader, "saltValue") is { Length: > 0 and <= 65536 } s ? s : throw new DocumentException("The encrypted file has no salt.");
                return new Params(hash, hashSize, keyBits / 8, blockSize, salt);
            }
        }
    }

    /// <summary>Standard encryption ([MS-OFFCRYPTO] 2.3.4.5): a binary header and verifier, AES in ECB mode with SHA-1.</summary>
    [SuppressMessage("Security", "CA5350", Justification = "Standard encryption is defined with SHA-1; this reads files that use it, it never writes them.")]
    private sealed class StandardKeyMaker : IKeyMaker
    {
        private const int SpinCount = 50_000;
        private const uint FlagCryptoApi = 0x04, FlagAes = 0x20;

        private int keyBytes;
        private byte[] salt = [];
        private byte[] verifier = [];
        private byte[] verifierHash = [];

        public static StandardKeyMaker Read(ReadOnlySpan<byte> info)
        {
            // Flags, the header's size, the header, then the verifier.
            if (info.Length < 8)
            {
                throw new DocumentException("The encrypted file's EncryptionInfo stream is damaged.");
            }

            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(info[4..]);
            if (headerSize < 32 || 8 + headerSize > info.Length)
            {
                throw new DocumentException("The encrypted file's EncryptionInfo header is damaged.");
            }

            ReadOnlySpan<byte> header = info.Slice(8, headerSize);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint algorithm = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            uint hashAlgorithm = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            int keyBits = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            if ((flags & FlagCryptoApi) == 0 || (flags & FlagAes) == 0 || algorithm is not (0x660E or 0x660F or 0x6610) || hashAlgorithm is not (0 or 0x8004))
            {
                throw new DocumentException("The document's standard encryption does not use AES with SHA-1, the only kind the format allows for packages.");
            }

            if (keyBits is not (128 or 192 or 256))
            {
                throw new DocumentException($"The document is encrypted with a {keyBits}-bit key; AES takes 128, 192 or 256 bits.");
            }

            ReadOnlySpan<byte> verifier = info[(8 + headerSize)..];
            if (verifier.Length < 4 + 16 + 16 + 4 + 32 || BinaryPrimitives.ReadInt32LittleEndian(verifier) != 16)
            {
                throw new DocumentException("The encrypted file's password verifier is damaged.");
            }

            return new StandardKeyMaker
            {
                keyBytes = keyBits / 8,
                salt = verifier.Slice(4, 16).ToArray(),
                verifier = verifier.Slice(20, 16).ToArray(),
                verifierHash = verifier.Slice(40, 32).ToArray(),
            };
        }

        public byte[]? KeyFor(string password)
        {
            // [MS-OFFCRYPTO] 2.3.4.7: the spun hash with block 0, then CryptDeriveKey's two padded hashes.
            byte[] spun = SpinHash(HashAlgorithmName.SHA1, salt, password, SpinCount);
            byte[] final = Hash(HashAlgorithmName.SHA1, spun, [0, 0, 0, 0]);
            byte[] x = new byte[40];
            Span<byte> pad = stackalloc byte[64];
            for (int i = 0; i < 2; i++)
            {
                pad.Fill(i == 0 ? (byte)0x36 : (byte)0x5C);
                for (int j = 0; j < final.Length; j++)
                {
                    pad[j] ^= final[j];
                }

                SHA1.HashData(pad, x.AsSpan(20 * i));
            }

            byte[] key = x[..keyBytes];
            using Aes aes = Aes.Create();
            aes.Key = key;
            byte[] plainVerifier = aes.DecryptEcb(verifier, PaddingMode.None);
            byte[] plainHash = aes.DecryptEcb(verifierHash, PaddingMode.None);
            return CryptographicOperations.FixedTimeEquals(SHA1.HashData(plainVerifier), plainHash.AsSpan(0, 20)) ? key : null;
        }

        public void CheckIntegrity(byte[] key, byte[] encryptedPackage)
        {
            // Standard encryption has no integrity check.
        }

        public byte[] Decrypt(byte[] key, byte[] encryptedPackage, int size)
        {
            using Aes aes = Aes.Create();
            aes.Key = key;
            int length = (size + 15) / 16 * 16;
            byte[] plain = aes.DecryptEcb(encryptedPackage.AsSpan(8, Math.Min(length, (encryptedPackage.Length - 8) / 16 * 16)), PaddingMode.None);
            return plain.Length == size ? plain : plain[..Math.Min(size, plain.Length)];
        }
    }
}
