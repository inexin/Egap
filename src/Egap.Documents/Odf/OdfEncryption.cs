using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>How an OpenDocument package is encrypted on save.</summary>
internal enum OdfEncryptionMethod
{
    /// <summary>
    /// Each file on its own with AES-256 in CBC mode, its key derived with PBKDF2 (HMAC-SHA-1, 100,000 iterations) from the
    /// SHA-256 of the password, and a SHA-256 checksum of its first kilobyte (ODF 1.2 to 1.4): what LibreOffice writes for
    /// ODF 1.2 to 1.4, and what every ODF reader since 2011 opens.
    /// </summary>
    AesCbc,

    /// <summary>
    /// The whole package in one file with AES-256 in GCM mode, its key derived with Argon2id from the SHA-256 of the
    /// password: what LibreOffice writes by default since version 24.2, which hides the file names too. Older readers do
    /// not open it.
    /// </summary>
    AesGcmArgon2id,
}

/// <summary>
/// OpenDocument encryption (ODF 1.3 Part 2, 3.4 and 4.4 to 4.7): each encrypted file is deflated, then encrypted with a key
/// derived from the password and its own salt; the manifest says how. Read: Blowfish in 8-bit CFB mode with PBKDF2 (old
/// files), AES in CBC mode with PBKDF2 (ODF 1.2 and later), and AES in GCM mode with Argon2id over the whole package in one
/// <c>encrypted-package</c> file (LibreOffice's extension, from 24.2).
/// </summary>
internal static class OdfEncryption
{
    /// <summary>The file LibreOffice's whole-package encryption holds the real package in.</summary>
    public const string EncryptedPackage = "encrypted-package";

    /// <summary>The most PBKDF2 iterations one file may ask for; more is refused rather than spun for minutes.</summary>
    public const int MaxIterations = 10_000_000;

    /// <summary>The most PBKDF2 iterations all of a package's files may ask for together (LibreOffice's take about 1,000,000).</summary>
    public const long MaxTotalIterations = 20_000_000;

    /// <summary>The most memory Argon2id may ask for: 256 MiB (LibreOffice asks for 64 MiB).</summary>
    public const int MaxArgon2MemoryKib = 256 * 1024;

    /// <summary>The most Argon2id work, memory times passes: 1 GiB passed over once (LibreOffice asks for 64 MiB three times).</summary>
    public const long MaxArgon2Work = 1024 * 1024;

    private const string Blowfish = "Blowfish CFB";
    private const string BlowfishUrn = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#blowfish";
    private const string Aes128Cbc = "http://www.w3.org/2001/04/xmlenc#aes128-cbc";
    private const string Aes192Cbc = "http://www.w3.org/2001/04/xmlenc#aes192-cbc";
    private const string Aes256Cbc = "http://www.w3.org/2001/04/xmlenc#aes256-cbc";
    private const string Aes128Gcm = "http://www.w3.org/2009/xmlenc11#aes128-gcm";
    private const string Aes192Gcm = "http://www.w3.org/2009/xmlenc11#aes192-gcm";
    private const string Aes256Gcm = "http://www.w3.org/2009/xmlenc11#aes256-gcm";
    private const string Sha256Dsig = "http://www.w3.org/2000/09/xmldsig#sha256";
    private const string Sha256Enc = "http://www.w3.org/2001/04/xmlenc#sha256";
    private const string Sha1Dsig = "http://www.w3.org/2000/09/xmldsig#sha1";
    private const string Sha256Checksum = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha256-1k";
    private const string Sha1Checksum = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha1-1k";
    private const string Pbkdf2Urn = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#pbkdf2";
    private const string Argon2idName = "urn:org:documentfoundation:names:experimental:office:manifest:argon2id";

    /// <summary>Whether any file of the manifest is encrypted.</summary>
    public static bool IsEncrypted(XElement manifest)
    {
        return manifest.Elements(ManifestNs + "file-entry").Any(e => e.Element(ManifestNs + "encryption-data") is not null);
    }

    /// <summary>
    /// Decrypts every encrypted file of <paramref name="files"/> in place: tries <paramref name="password"/>, then each password
    /// <paramref name="callback"/> gives (with the attempt number from 1) until it gives null. A whole-package encryption
    /// leaves the inner package's bytes as the only file, named <see cref="EncryptedPackage"/>.
    /// </summary>
    /// <exception cref="DocumentPasswordException">No password was given, or none of them was right.</exception>
    /// <exception cref="DocumentException">The encryption is one Egap does not read, or the file is damaged.</exception>
    /// <exception cref="DocumentLimitException">The key derivation or a file is past a limit.</exception>
    public static void Decrypt(Dictionary<string, byte[]> files, XElement manifest, string? password, Func<int, string?>? callback, DocumentLimits limits)
    {
        List<Encrypted> entries = [];
        foreach (XElement entry in manifest.Elements(ManifestNs + "file-entry"))
        {
            if (entry.Element(ManifestNs + "encryption-data") is not { } data || entry.Attr(ManifestNs, "full-path") is not { } path)
            {
                continue;
            }

            string name = OdfPackage.NormalizePath(path);
            if (files.TryGetValue(name, out byte[]? bytes))
            {
                entries.Add(Encrypted.Read(name, bytes, data, entry.Attr(ManifestNs, "size"), limits));
            }
        }

        if (entries.Count == 0)
        {
            return;
        }

        long total = entries.Sum(e => (long)e.Iterations);
        if (entries.Exists(e => e.UsesPbkdf2) && total > MaxTotalIterations)
        {
            throw new DocumentLimitException($"The encrypted files ask for {total} PBKDF2 iterations together, past the limit of {MaxTotalIterations}.");
        }

        // The password is checked on the smallest file: its checksum, or for GCM its tag, says whether the key is right.
        Encrypted probe = entries.MinBy(e => e.Data.Length)!;
        bool given = password is not null;
        byte[]? probed = null;
        bool right = password is not null && probe.TryDecrypt(password, out probed);
        for (int attempt = 1; !right && callback is not null; attempt++)
        {
            string? next = callback(attempt);
            if (next is null)
            {
                break;
            }

            given = true;
            password = next;
            right = probe.TryDecrypt(password, out probed);
        }

        if (!right)
        {
            throw given
                ? new DocumentPasswordException("The password is wrong: the document is encrypted with another one.", wrongPassword: true)
                : new DocumentPasswordException("The document is encrypted: give its password in the load options.", wrongPassword: false);
        }

        files[probe.Name] = probed!;
        foreach (Encrypted entry in entries)
        {
            if (entry == probe)
            {
                continue;
            }

            if (!entry.TryDecrypt(password!, out byte[]? plain))
            {
                // Files of one package share the password; one that does not decrypt with it is damaged.
                throw new DocumentException($"The encrypted file {entry.Name} is damaged: it does not decrypt with the document's password.");
            }

            files[entry.Name] = plain!;
        }
    }

    /// <summary>
    /// Encrypts a plain OpenDocument package with <paramref name="password"/> and writes it to <paramref name="output"/>. The
    /// <c>mimetype</c> file, the manifest and signatures stay plain; a thumbnail is left out, as ODF asks of encrypted files.
    /// </summary>
    /// <exception cref="DocumentException">The package is not an OpenDocument package.</exception>
    public static void Encrypt(byte[] package, string password, OdfEncryptionMethod method, Stream output)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(password);
        List<(string Name, byte[] Data)> files = ReadPlain(package, out string mediaType, out XElement? manifest);
        if (method == OdfEncryptionMethod.AesGcmArgon2id)
        {
            EncryptWhole(package, password, mediaType, output);
        }
        else
        {
            EncryptFiles(files, password, mediaType, manifest, output);
        }
    }

    private static void EncryptFiles(List<(string Name, byte[] Data)> files, string password, string mediaType, XElement? original, Stream output)
    {
        Dictionary<string, XElement> entries = new(StringComparer.Ordinal);
        XElement manifest = original is null ? NewManifest(mediaType, "1.3") : new XElement(original);
        foreach (XElement entry in manifest.Elements(ManifestNs + "file-entry"))
        {
            if (entry.Attr(ManifestNs, "full-path") is { } path)
            {
                entry.Element(ManifestNs + "encryption-data")?.Remove();
                entries[path == "/" ? "" : OdfPackage.NormalizePath(path)] = entry;
            }
        }

        byte[] startKey = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        using ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true);
        Write(zip, "mimetype", Encoding.ASCII.GetBytes(mediaType), CompressionLevel.NoCompression);
        foreach ((string name, byte[] data) in files)
        {
            if (!ShouldEncrypt(name))
            {
                Write(zip, name, data, CompressionLevel.Optimal);
                continue;
            }

            if (!entries.TryGetValue(name, out XElement? entry))
            {
                entry = new XElement(ManifestNs + "file-entry", new XAttribute(ManifestNs + "full-path", name), new XAttribute(ManifestNs + "media-type", ""));
                manifest.Add(entry);
                entries[name] = entry;
            }

            byte[] compressed = Deflate(data);
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] iv = RandomNumberGenerator.GetBytes(16);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(startKey, salt, 100_000, HashAlgorithmName.SHA1, 32);
            byte[] checksum = SHA256.HashData(compressed.AsSpan(0, Math.Min(1024, compressed.Length)));
            using Aes aes = Aes.Create();
            aes.Key = key;
            byte[] encrypted = aes.EncryptCbc(compressed, iv, PaddingMode.PKCS7);
            entry.SetAttributeValue(ManifestNs + "size", data.Length.ToString(CultureInfo.InvariantCulture));
            entry.Add(new XElement(
                ManifestNs + "encryption-data",
                new XAttribute(ManifestNs + "checksum-type", Sha256Checksum),
                new XAttribute(ManifestNs + "checksum", Convert.ToBase64String(checksum)),
                new XElement(ManifestNs + "algorithm", new XAttribute(ManifestNs + "algorithm-name", Aes256Cbc), new XAttribute(ManifestNs + "initialisation-vector", Convert.ToBase64String(iv))),
                new XElement(ManifestNs + "start-key-generation", new XAttribute(ManifestNs + "start-key-generation-name", Sha256Dsig), new XAttribute(ManifestNs + "key-size", "32")),
                new XElement(
                    ManifestNs + "key-derivation",
                    new XAttribute(ManifestNs + "key-derivation-name", "PBKDF2"),
                    new XAttribute(ManifestNs + "key-size", "32"),
                    new XAttribute(ManifestNs + "iteration-count", "100000"),
                    new XAttribute(ManifestNs + "salt", Convert.ToBase64String(salt)))));
            Write(zip, name, encrypted, CompressionLevel.NoCompression);
        }

        // Entries for files the package no longer has (a dropped thumbnail) go too.
        foreach ((string name, XElement entry) in entries)
        {
            if (name.Length > 0 && !name.EndsWith('/') && !files.Exists(f => f.Name == name))
            {
                entry.Remove();
            }
        }

        Write(zip, "META-INF/manifest.xml", Xml(manifest), CompressionLevel.Optimal);
    }

    private static void EncryptWhole(byte[] package, string password, string mediaType, Stream output)
    {
        byte[] startKey = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] key = Argon2id.Hash(startKey, salt, 3, 65536, 4, 32);
        byte[] compressed = Deflate(package);

        // XML Encryption 1.1 (5.2.4): the nonce, the cipher text, then the tag; the manifest names the nonce too.
        byte[] encrypted = new byte[12 + compressed.Length + 16];
        iv.CopyTo(encrypted, 0);
        using (AesGcm gcm = new(key, 16))
        {
            gcm.Encrypt(iv, compressed, encrypted.AsSpan(12, compressed.Length), encrypted.AsSpan(12 + compressed.Length));
        }

        XNamespace loext = LoExtNs;
        XElement manifest = new(
            ManifestNs + "manifest",
            new XAttribute(XNamespace.Xmlns + "manifest", ManifestNs.NamespaceName),
            new XAttribute(ManifestNs + "version", "1.4"),
            new XAttribute(XNamespace.Xmlns + "loext", loext.NamespaceName),
            new XElement(
                ManifestNs + "file-entry",
                new XAttribute(ManifestNs + "full-path", EncryptedPackage),
                new XAttribute(ManifestNs + "media-type", mediaType),
                new XAttribute(ManifestNs + "size", package.Length.ToString(CultureInfo.InvariantCulture)),
                new XElement(
                    ManifestNs + "encryption-data",
                    new XElement(ManifestNs + "algorithm", new XAttribute(ManifestNs + "algorithm-name", Aes256Gcm), new XAttribute(ManifestNs + "initialisation-vector", Convert.ToBase64String(iv))),
                    new XElement(ManifestNs + "start-key-generation", new XAttribute(ManifestNs + "start-key-generation-name", Sha256Enc), new XAttribute(ManifestNs + "key-size", "32")),
                    new XElement(
                        ManifestNs + "key-derivation",
                        new XAttribute(ManifestNs + "key-derivation-name", Argon2idName),
                        new XAttribute(loext + "argon2-iterations", "3"),
                        new XAttribute(loext + "argon2-memory", "65536"),
                        new XAttribute(loext + "argon2-lanes", "4"),
                        new XAttribute(ManifestNs + "salt", Convert.ToBase64String(salt)),
                        new XAttribute(ManifestNs + "key-size", "32")))));
        using ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true);
        Write(zip, "mimetype", Encoding.ASCII.GetBytes(mediaType), CompressionLevel.NoCompression);
        Write(zip, EncryptedPackage, encrypted, CompressionLevel.NoCompression);
        Write(zip, "META-INF/manifest.xml", Xml(manifest), CompressionLevel.Optimal);
    }

    private static bool ShouldEncrypt(string name)
    {
        return name != "mimetype" && !name.StartsWith("META-INF/", StringComparison.Ordinal) && !name.EndsWith('/');
    }

    /// <summary>The package's files in order, without the mimetype, the manifest and a thumbnail.</summary>
    private static List<(string Name, byte[] Data)> ReadPlain(byte[] package, out string mediaType, out XElement? manifest)
    {
        List<(string, byte[])> files = [];
        mediaType = "";
        manifest = null;
        try
        {
            using ZipArchive zip = new(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                using MemoryStream copy = new();
                using (Stream stream = entry.Open())
                {
                    stream.CopyTo(copy);
                }

                string name = entry.FullName;
                if (name == "mimetype")
                {
                    mediaType = Encoding.ASCII.GetString(copy.ToArray()).Trim();
                }
                else if (name == "META-INF/manifest.xml")
                {
                    manifest = XElement.Load(new MemoryStream(copy.ToArray()));
                }
                else if (!name.StartsWith("Thumbnails/", StringComparison.Ordinal))
                {
                    files.Add((name, copy.ToArray()));
                }
            }
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException("The package to encrypt is not a valid ZIP file: " + e.Message, e);
        }

        if (mediaType.Length == 0)
        {
            throw new DocumentException("The package to encrypt is not an OpenDocument package: it has no mimetype.");
        }

        return files;
    }

    private static XElement NewManifest(string mediaType, string version)
    {
        return new XElement(
            ManifestNs + "manifest",
            new XAttribute(XNamespace.Xmlns + "manifest", ManifestNs.NamespaceName),
            new XAttribute(ManifestNs + "version", version),
            new XElement(ManifestNs + "file-entry", new XAttribute(ManifestNs + "full-path", "/"), new XAttribute(ManifestNs + "version", version), new XAttribute(ManifestNs + "media-type", mediaType)));
    }

    private static byte[] Xml(XElement root)
    {
        using MemoryStream stream = new();
        new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(stream, SaveOptions.DisableFormatting);
        return stream.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        using Stream stream = zip.CreateEntry(name, level).Open();
        stream.Write(data);
    }

    private static byte[] Deflate(byte[] data)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>One encrypted file and how to decrypt it.</summary>
    [SuppressMessage("Security", "CA5350", Justification = "ODF's legacy encryption, which old files use, is defined with SHA-1 start keys and checksums; this reads them, it never writes them.")]
    private sealed class Encrypted
    {
        private string algorithm = "";
        private byte[] iv = [];
        private string startKeyName = "SHA1";
        private string derivation = "";
        private byte[] salt = [];
        private int iterations;
        private int keySize;
        private int argon2Memory;
        private int argon2Lanes;
        private string? checksumType;
        private byte[]? checksum;
        private int size;
        private DocumentLimits limits = null!;

        public string Name { get; private set; } = "";

        public byte[] Data { get; private set; } = [];

        public int Iterations => iterations;

        public bool UsesPbkdf2 => derivation != Argon2idName;

        public static Encrypted Read(string name, byte[] data, XElement encryption, string? size, DocumentLimits limits)
        {
            XElement algorithm = encryption.Element(ManifestNs + "algorithm") ?? throw new DocumentException($"The encrypted file {name} names no algorithm.");
            XElement derivation = encryption.Element(ManifestNs + "key-derivation") ?? throw new DocumentException($"The encrypted file {name} names no key derivation.");
            XElement? start = encryption.Element(ManifestNs + "start-key-generation");
            Encrypted entry = new()
            {
                Name = name,
                Data = data,
                limits = limits,
                algorithm = algorithm.Attr(ManifestNs, "algorithm-name") ?? "",
                iv = Base64(algorithm.Attr(ManifestNs, "initialisation-vector")),
                startKeyName = start?.Attr(ManifestNs, "start-key-generation-name") ?? "SHA1",
                derivation = derivation.Attr(ManifestNs, "key-derivation-name") ?? "",
                salt = Base64(derivation.Attr(ManifestNs, "salt")),
                iterations = Int(derivation.Attr(ManifestNs, "iteration-count")) ?? Int(derivation.Attr(LoExtNs, "argon2-iterations")) ?? 0,
                argon2Memory = Int(derivation.Attr(LoExtNs, "argon2-memory")) ?? 0,
                argon2Lanes = Int(derivation.Attr(LoExtNs, "argon2-lanes")) ?? 0,
                checksumType = encryption.Attr(ManifestNs, "checksum-type"),
                checksum = encryption.Attr(ManifestNs, "checksum") is { } sum ? Base64(sum) : null,
                size = Int(size) ?? -1,
            };
            entry.keySize = Int(derivation.Attr(ManifestNs, "key-size")) ?? (entry.IsAes ? entry.AesKeySize : 16);
            entry.Check(name);
            return entry;
        }

        private bool IsAes => algorithm is Aes128Cbc or Aes192Cbc or Aes256Cbc or Aes128Gcm or Aes192Gcm or Aes256Gcm;

        private bool IsGcm => algorithm is Aes128Gcm or Aes192Gcm or Aes256Gcm;

        private int AesKeySize => algorithm switch
        {
            Aes128Cbc or Aes128Gcm => 16,
            Aes192Cbc or Aes192Gcm => 24,
            _ => 32,
        };

        /// <summary>Decrypts and inflates the file; false when the key from <paramref name="password"/> is not its key.</summary>
        public bool TryDecrypt(string password, out byte[]? plain)
        {
            plain = null;
            byte[] key = Key(password);
            byte[] compressed;
            if (algorithm is Blowfish or BlowfishUrn)
            {
                if (iv.Length != 8)
                {
                    throw new DocumentException($"The encrypted file {Name} is damaged: its initialisation vector is not 8 bytes.");
                }

                // OpenOffice.org and LibreOffice feed back 64 bits; ODF names 8, which the checksum tells apart.
                Blowfish cipher = new(key);
                compressed = (byte[])Data.Clone();
                cipher.DecryptCfb64(iv, compressed);
                if (checksum is not null && !ChecksumMatches(compressed))
                {
                    compressed = (byte[])Data.Clone();
                    cipher.DecryptCfb8(iv, compressed);
                }
            }
            else if (IsGcm)
            {
                // XML Encryption 1.1 (5.2.4): the 12-byte nonce, the cipher text, then the 16-byte tag.
                if (Data.Length < 28)
                {
                    throw new DocumentException($"The encrypted file {Name} is damaged: it is shorter than its nonce and tag.");
                }

                compressed = new byte[Data.Length - 28];
                try
                {
                    using AesGcm gcm = new(key, 16);
                    gcm.Decrypt(Data.AsSpan(0, 12), Data.AsSpan(12, compressed.Length), Data.AsSpan(Data.Length - 16), compressed);
                }
                catch (AuthenticationTagMismatchException)
                {
                    return false;
                }
            }
            else
            {
                if (Data.Length % 16 != 0 || iv.Length != 16)
                {
                    throw new DocumentException($"The encrypted file {Name} is damaged: its length is not a whole number of AES blocks.");
                }

                using Aes aes = Aes.Create();
                aes.Key = key;
                byte[] padded = aes.DecryptCbc(Data, iv, PaddingMode.None);

                // XML Encryption's padding: the last byte counts the bytes added, 1 to 16 (xmlenc-core 5.2).
                int pad = padded.Length > 0 ? padded[^1] : 0;
                if (pad is < 1 or > 16 || pad > padded.Length)
                {
                    return checksum is null ? throw new DocumentException($"The encrypted file {Name} is damaged: its padding is wrong.") : false;
                }

                compressed = padded[..^pad];
            }

            if (checksum is not null && !ChecksumMatches(compressed))
            {
                return false;
            }

            plain = Inflate(compressed);
            if (plain is null)
            {
                // Without a checksum (GCM's tag already said the key is right) a file that does not inflate is damaged.
                return checksum is null && !IsGcm ? false : throw new DocumentException($"The encrypted file {Name} is damaged: it does not inflate.");
            }

            return true;
        }

        private bool ChecksumMatches(ReadOnlySpan<byte> compressed)
        {
            ReadOnlySpan<byte> head = compressed[..Math.Min(1024, compressed.Length)];
            byte[] sum = checksumType switch
            {
                Sha256Checksum or "SHA256/1K" => SHA256.HashData(head),
                _ => SHA1.HashData(head),
            };
            return sum.AsSpan().SequenceEqual(checksum);
        }

        private byte[] Key(string password)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(password);
            byte[] startKey = startKeyName switch
            {
                Sha256Dsig or Sha256Enc or "SHA256" => SHA256.HashData(bytes),
                _ => SHA1.HashData(bytes),
            };
            if (derivation == Argon2idName)
            {
                return Argon2id.Hash(startKey, salt, iterations, argon2Memory, argon2Lanes, keySize);
            }

            return Rfc2898DeriveBytes.Pbkdf2(startKey, salt, iterations, HashAlgorithmName.SHA1, keySize);
        }

        private byte[]? Inflate(byte[] compressed)
        {
            long limit = size >= 0 ? size : limits.MaxPartSize;
            try
            {
                using DeflateStream inflate = new(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
                using MemoryStream output = new();
                byte[] buffer = new byte[81920];
                int read;
                while ((read = inflate.Read(buffer)) > 0)
                {
                    if (output.Length + read > limit)
                    {
                        throw new DocumentLimitException($"The encrypted file {Name} inflates past its size or the limit of {limits.MaxPartSize} bytes.");
                    }

                    output.Write(buffer, 0, read);
                }

                return size >= 0 && output.Length != size ? null : output.ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private void Check(string name)
        {
            if (algorithm is not (Blowfish or BlowfishUrn) && !IsAes)
            {
                throw new DocumentException($"The encrypted file {name} uses {algorithm}, an encryption Egap does not read.");
            }

            if (derivation is not ("PBKDF2" or Pbkdf2Urn or Argon2idName))
            {
                throw new DocumentException($"The encrypted file {name} derives its key with {derivation}, which Egap does not read.");
            }

            if (size > limits.MaxPartSize)
            {
                throw new DocumentLimitException($"The encrypted file {name} is {size} bytes, more than the limit of {limits.MaxPartSize}.");
            }

            if (keySize is < 1 or > 64 || (IsAes && keySize != AesKeySize) || (!IsAes && keySize > 56))
            {
                throw new DocumentException($"The encrypted file {name} asks for a key of {keySize} bytes, which its algorithm does not take.");
            }

            if (derivation == Argon2idName)
            {
                if (iterations < 1 || argon2Lanes is < 1 or > 64 || argon2Memory < 8 * argon2Lanes)
                {
                    throw new DocumentException($"The encrypted file {name} asks for Argon2id with {iterations} passes over {argon2Memory} KiB in {argon2Lanes} lanes, which is not a valid hash.");
                }

                if (argon2Memory > MaxArgon2MemoryKib || (long)argon2Memory * iterations > MaxArgon2Work)
                {
                    throw new DocumentLimitException($"The encrypted file {name} asks for Argon2id with {iterations} passes over {argon2Memory} KiB, past the limits of {MaxArgon2MemoryKib} KiB and {MaxArgon2Work} KiB passed over in all.");
                }
            }
            else if (iterations is < 1 or > MaxIterations)
            {
                throw new DocumentLimitException($"The encrypted file {name} asks for {iterations} PBKDF2 iterations, past the limit of {MaxIterations}.");
            }
        }

        private static byte[] Base64(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return [];
            }

            byte[] buffer = new byte[value.Length];
            return Convert.TryFromBase64String(value, buffer, out int written) ? buffer[..written] : [];
        }

        private static int? Int(string? value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : null;
        }
    }
}
