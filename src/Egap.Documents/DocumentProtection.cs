using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Egap.Documents;

/// <summary>What a protected document lets a reader change.</summary>
public enum ProtectionKind
{
    /// <summary>No restriction.</summary>
    None,

    /// <summary>Nothing may change except the <see cref="EditableRangeStart">editable ranges</see>.</summary>
    ReadOnly,

    /// <summary>Only comments may be added, and the editable ranges changed.</summary>
    Comments,

    /// <summary>Every change is tracked and cannot be accepted or rejected.</summary>
    TrackedChanges,

    /// <summary>Only form fields may be filled in.</summary>
    Forms,
}

/// <summary>
/// The document's editing restriction: what may change and the hash of the password that lifts it. Egap does not enforce the
/// restriction; it reads, writes and keeps it through conversion, so that Word and other editors enforce it, and
/// <see cref="Verify"/> checks a password against the hash.
/// </summary>
/// <remarks>
/// The hash is Word's: the password is first turned into Word's legacy 32-bit key, then hashed with the salt and hashed again
/// <see cref="SpinCount"/> times ([MS-OFFCRYPTO] 2.3.7.1 to 2.3.7.4, ECMA-376 Part 1, 17.15.1.29).
/// </remarks>
public sealed class DocumentProtection
{
    /// <summary>The most spins <see cref="Verify"/> runs; a file asking for more is refused rather than hashed for minutes.</summary>
    public const int MaxSpinCount = 10_000_000;

    /// <summary>Word's spin count, used by <see cref="Protect"/>.</summary>
    public const int DefaultSpinCount = 100_000;

    private const string DefaultAlgorithm = "SHA-512";

    private static readonly ushort[] InitialCode =
    [
        0xE1F0, 0x1D0F, 0xCC9C, 0x84C0, 0x110C, 0x0E10, 0xF1CE, 0x313E, 0x1872, 0xE139, 0xD40F, 0x84F9, 0x280C, 0xA96A, 0x4EC3,
    ];

    private static readonly ushort[] XorMatrix =
    [
        0xAEFC, 0x4DD9, 0x9BB2, 0x2745, 0x4E8A, 0x9D14, 0x2A09, 0x7B61, 0xF6C2, 0xFDA5, 0xEB6B, 0xC6F7, 0x9DCF, 0x2BBF, 0x4563,
        0x8AC6, 0x05AD, 0x0B5A, 0x16B4, 0x2D68, 0x5AD0, 0x0375, 0x06EA, 0x0DD4, 0x1BA8, 0x3750, 0x6EA0, 0xDD40, 0xD849, 0xA0B3,
        0x5147, 0xA28E, 0x553D, 0xAA7A, 0x44D5, 0x6F45, 0xDE8A, 0xAD35, 0x4A4B, 0x9496, 0x390D, 0x721A, 0xEB23, 0xC667, 0x9CEF,
        0x29FF, 0x53FE, 0xA7FC, 0x5FD9, 0x47D3, 0x8FA6, 0x0F6D, 0x1EDA, 0x3DB4, 0x7B68, 0xF6D0, 0xB861, 0x60E3, 0xC1C6, 0x93AD,
        0x377B, 0x6EF6, 0xDDEC, 0x45A0, 0x8B40, 0x06A1, 0x0D42, 0x1A84, 0x3508, 0x6A10, 0xAA51, 0x4483, 0x8906, 0x022D, 0x045A,
        0x08B4, 0x1168, 0x76B4, 0xED68, 0xCAF1, 0x85C3, 0x1BA7, 0x374E, 0x6E9C, 0x3730, 0x6E60, 0xDCC0, 0xA9A1, 0x4363, 0x86C6,
        0x1DAD, 0x3331, 0x6662, 0xCCC4, 0x89A9, 0x0373, 0x06E6, 0x0DCC, 0x1021, 0x2042, 0x4084, 0x8108, 0x1231, 0x2462, 0x48C4,
    ];

    private readonly Document document;
    private ProtectionKind kind;
    private bool isEnforced;
    private string? hashAlgorithm;
    private byte[]? hash;
    private byte[]? salt;
    private int spinCount;

    internal DocumentProtection(Document document)
    {
        this.document = document;
    }

    /// <summary>What the restriction lets a reader change; <see cref="ProtectionKind.None"/> when there is none.</summary>
    public ProtectionKind Kind => kind;

    /// <summary>
    /// The restriction is on. A file may name a <see cref="Kind"/> that is not enforced, which an editor offers as the choice
    /// for the next time the document is protected.
    /// </summary>
    public bool IsEnforced => isEnforced;

    /// <summary>A password lifts the restriction; without one, anyone may switch it off.</summary>
    public bool HasPassword => hash is not null;

    /// <summary>The hash algorithm, such as "SHA-512" or "SHA-1"; null without a password.</summary>
    public string? HashAlgorithm => hashAlgorithm;

    /// <summary>The password's hash; empty without a password.</summary>
    public ReadOnlyMemory<byte> Hash => hash;

    /// <summary>The salt hashed in front of the password; empty without a password.</summary>
    public ReadOnlyMemory<byte> Salt => salt;

    /// <summary>How many times the hash is hashed again; 0 without a password.</summary>
    public int SpinCount => spinCount;

    /// <summary>The hash is stored in ECMA-376's newer attributes (algorithmName, hashValue) rather than Word's own.</summary>
    internal bool ModernAttributes { get; set; }

    /// <summary>
    /// Restricts editing to <paramref name="kind"/>, with a SHA-512 hash of <paramref name="password"/> under a new random
    /// salt and <see cref="DefaultSpinCount"/> spins, as Word makes it. A null or empty password lets anyone switch it off.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="ProtectionKind.None"/> or not defined.</exception>
    public void Protect(ProtectionKind kind, string? password = null)
    {
        if (kind == ProtectionKind.None || !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        this.kind = kind;
        isEnforced = true;
        ModernAttributes = false;
        if (string.IsNullOrEmpty(password))
        {
            ClearHash();
        }
        else
        {
            byte[] newSalt = RandomNumberGenerator.GetBytes(16);
            hashAlgorithm = DefaultAlgorithm;
            salt = newSalt;
            spinCount = DefaultSpinCount;
            hash = ComputeHash(DefaultAlgorithm, LegacyKey(password), newSalt, DefaultSpinCount);
        }

        Changed();
    }

    /// <summary>Removes the restriction and its password hash.</summary>
    public void Unprotect()
    {
        if (kind == ProtectionKind.None && !isEnforced && hash is null)
        {
            return;
        }

        kind = ProtectionKind.None;
        isEnforced = false;
        ModernAttributes = false;
        ClearHash();
        Changed();
    }

    /// <summary>
    /// Whether <paramref name="password"/> lifts the restriction: true when it matches the hash, or when there is no password.
    /// </summary>
    /// <exception cref="DocumentException">The hash uses an algorithm .NET does not have, or more than <see cref="MaxSpinCount"/> spins.</exception>
    public bool Verify(string? password)
    {
        if (hash is null || salt is null || hashAlgorithm is null)
        {
            return true;
        }

        if (spinCount is < 0 or > MaxSpinCount)
        {
            throw new DocumentException(string.Create(CultureInfo.InvariantCulture, $"The protection hash asks for {spinCount} spins; at most {MaxSpinCount} are run."));
        }

        password ??= "";

        // Word hashes its legacy key for the password; ECMA-376's strict reading hashes the password itself, so both are tried.
        return Matches(LegacyKey(password)) || (password.Length > 0 && Matches(Encoding.Unicode.GetBytes(password)));

        bool Matches(byte[] input)
        {
            return CryptographicOperations.FixedTimeEquals(ComputeHash(hashAlgorithm, input, salt, spinCount), hash);
        }
    }

    /// <summary>Sets what a reader found; a hash is kept only when its algorithm, hash and salt are all there.</summary>
    internal void Set(ProtectionKind kind, bool enforced, string? algorithm, byte[]? hash, byte[]? salt, int spinCount, bool modern = false)
    {
        this.kind = Enum.IsDefined(kind) ? kind : ProtectionKind.None;
        isEnforced = enforced && this.kind != ProtectionKind.None;
        ModernAttributes = modern;
        if (algorithm is null || hash is not { Length: > 0 } || salt is null)
        {
            ClearHash();
        }
        else
        {
            hashAlgorithm = algorithm;
            this.hash = hash;
            this.salt = salt;
            this.spinCount = Math.Max(spinCount, 0);
        }

        Changed();
    }

    /// <summary>Same restriction and hash.</summary>
    internal bool SameAs(DocumentProtection other)
    {
        return kind == other.kind && isEnforced == other.isEnforced && hashAlgorithm == other.hashAlgorithm && spinCount == other.spinCount
            && Hash.Span.SequenceEqual(other.Hash.Span) && Salt.Span.SequenceEqual(other.Salt.Span);
    }

    internal void CopyFrom(DocumentProtection source)
    {
        kind = source.kind;
        isEnforced = source.isEnforced;
        hashAlgorithm = source.hashAlgorithm;
        hash = source.hash;
        salt = source.salt;
        spinCount = source.spinCount;
        ModernAttributes = source.ModernAttributes;
    }

    /// <summary>
    /// Word's legacy key for a password ([MS-OFFCRYPTO] 2.3.7.1 to 2.3.7.4): the password's first 15 characters as single bytes
    /// give a 16-bit XOR key and a 16-bit verifier; the 32-bit key's little-endian bytes, written as upper-case hex, are
    /// hashed as UTF-16. An empty password gives key 0.
    /// </summary>
    internal static byte[] LegacyKey(string password)
    {
        int length = Math.Min(password.Length, 15);
        Span<byte> bytes = stackalloc byte[15];
        for (int i = 0; i < length; i++)
        {
            char c = password[i];
            byte low = (byte)c;
            bytes[i] = low != 0 ? low : (byte)(c >> 8);
        }

        uint key = 0;
        if (length > 0)
        {
            int xorKey = InitialCode[length - 1];
            int element = 0x68;
            for (int i = length - 1; i >= 0; i--)
            {
                int ch = bytes[i];
                for (int bit = 0; bit < 7; bit++)
                {
                    if ((ch & 0x40) != 0)
                    {
                        xorKey ^= XorMatrix[element];
                    }

                    ch = (ch << 1) & 0xFF;
                    element--;
                }
            }

            int verifier = 0;
            for (int i = length; i >= 0; i--)
            {
                int b = i == 0 ? length : bytes[i - 1];
                verifier = ((verifier & 0x4000) != 0 ? 1 : 0) | ((verifier << 1) & 0x7FFF);
                verifier ^= b;
            }

            verifier ^= 0xCE4B;
            key = ((uint)xorKey << 16) | (uint)verifier;
        }

        Span<byte> keyBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, key);
        return Encoding.Unicode.GetBytes(Convert.ToHexString(keyBytes));
    }

    /// <summary>H(salt + input), then H(previous + spin index as 32-bit little-endian) <paramref name="spins"/> times.</summary>
    internal static byte[] ComputeHash(string algorithm, byte[] input, byte[] salt, int spins)
    {
        using IncrementalHash hasher = IncrementalHash.CreateHash(AlgorithmName(algorithm));
        hasher.AppendData(salt);
        hasher.AppendData(input);
        byte[] current = hasher.GetHashAndReset();
        Span<byte> index = stackalloc byte[4];
        for (int i = 0; i < spins; i++)
        {
            hasher.AppendData(current);
            BinaryPrimitives.WriteInt32LittleEndian(index, i);
            hasher.AppendData(index);
            if (!hasher.TryGetHashAndReset(current, out _))
            {
                throw new CryptographicException("The hash did not fit.");
            }
        }

        return current;
    }

    /// <summary>The ECMA-376 name of Word's cryptAlgorithmSid, or null for one .NET does not have (MD2, MD4, RIPEMD).</summary>
    internal static string? AlgorithmFromSid(int sid)
    {
        return sid switch
        {
            3 => "MD5",
            4 => "SHA-1",
            12 => "SHA-256",
            13 => "SHA-384",
            14 => "SHA-512",
            _ => null,
        };
    }

    /// <summary>Word's cryptAlgorithmSid for an algorithm name, or 0.</summary>
    internal static int SidFromAlgorithm(string algorithm)
    {
        return algorithm.ToUpperInvariant() switch
        {
            "MD5" => 3,
            "SHA-1" or "SHA1" => 4,
            "SHA-256" or "SHA256" => 12,
            "SHA-384" or "SHA384" => 13,
            "SHA-512" or "SHA512" => 14,
            _ => 0,
        };
    }

    private static HashAlgorithmName AlgorithmName(string algorithm)
    {
        return algorithm.ToUpperInvariant() switch
        {
            "MD5" => HashAlgorithmName.MD5,
            "SHA-1" or "SHA1" => HashAlgorithmName.SHA1,
            "SHA-256" or "SHA256" => HashAlgorithmName.SHA256,
            "SHA-384" or "SHA384" => HashAlgorithmName.SHA384,
            "SHA-512" or "SHA512" => HashAlgorithmName.SHA512,
            _ => throw new DocumentException($"The protection hash uses {algorithm}, which Egap cannot check."),
        };
    }

    private void ClearHash()
    {
        hashAlgorithm = null;
        hash = null;
        salt = null;
        spinCount = 0;
    }

    private void Changed()
    {
        document.NotifyFormattingChanged("Protection");
    }
}
