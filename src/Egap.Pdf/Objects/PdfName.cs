using System.Collections.Concurrent;
using System.Text;
using System.Text.Unicode;

namespace Egap.Pdf;

/// <summary>
/// A PDF name, such as <c>/Type</c>. A name is a sequence of bytes, by convention UTF-8 text; <see cref="Value"/> is that
/// text without the slash.
/// </summary>
/// <remarks>
/// Names compare by their bytes. Common names are interned, so the same name from different files is usually one instance.
/// A string converts to a name implicitly, so <c>dictionary["Type"]</c> works wherever a name is expected.
/// </remarks>
public sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    /// <summary>The most names kept in the shared table; more are still created, just not shared.</summary>
    private const int MaxInterned = 100_000;

    private static readonly ConcurrentDictionary<byte[], PdfName> Interned = new(ByteComparer.Instance);
    private static readonly ConcurrentDictionary<byte[], PdfName>.AlternateLookup<ReadOnlySpan<byte>> InternedBySpan = Interned.GetAlternateLookup<ReadOnlySpan<byte>>();

    private readonly byte[] bytes;
    private readonly int hash;

    private PdfName(byte[] bytes)
    {
        this.bytes = bytes;
        hash = ByteComparer.Hash(bytes);
        // A name that is not UTF-8 shows each byte as one character, so the text still maps back to the bytes.
        Value = Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    /// <summary>The name's text, without the slash: UTF-8 when the bytes are UTF-8, otherwise one character per byte.</summary>
    public string Value { get; }

    /// <summary>The name's bytes, without the slash and with <c>#xx</c> escapes resolved.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>The name for a text, encoded as UTF-8.</summary>
    public static PdfName Get(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Get(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>The name with the given bytes.</summary>
    public static PdfName Get(ReadOnlySpan<byte> bytes)
    {
        if (InternedBySpan.TryGetValue(bytes, out PdfName? name))
        {
            return name;
        }

        name = new PdfName(bytes.ToArray());
        return Interned.Count < MaxInterned ? Interned.GetOrAdd(name.bytes, name) : name;
    }

    /// <summary>The name for a text.</summary>
    public static implicit operator PdfName(string value)
    {
        return Get(value);
    }

    /// <summary>The name for a text, the named alternative to the implicit conversion.</summary>
    public static PdfName FromString(string value)
    {
        return Get(value);
    }

    /// <inheritdoc/>
    public bool Equals(PdfName? other)
    {
        return ReferenceEquals(this, other) || (other is not null && other.hash == hash && other.bytes.AsSpan().SequenceEqual(bytes));
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfName);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return hash;
    }

    /// <summary>Whether two names are equal.</summary>
    public static bool operator ==(PdfName? left, PdfName? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    /// <summary>Whether two names differ.</summary>
    public static bool operator !=(PdfName? left, PdfName? right)
    {
        return !(left == right);
    }

    /// <summary>Compares byte arrays by content, and spans with arrays, for the intern table.</summary>
    private sealed class ByteComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public static int Hash(ReadOnlySpan<byte> bytes)
        {
            HashCode hash = default;
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        public bool Equals(byte[]? x, byte[]? y)
        {
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            return Hash(obj);
        }

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other)
        {
            return alternate.SequenceEqual(other);
        }

        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            return Hash(alternate);
        }

        public byte[] Create(ReadOnlySpan<byte> alternate)
        {
            return alternate.ToArray();
        }
    }
}
