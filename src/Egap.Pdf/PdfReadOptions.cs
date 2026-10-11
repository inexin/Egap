namespace Egap.Pdf;

/// <summary>Options for opening a PDF file: the password, strict mode and the limits that protect against hostile files.</summary>
/// <remarks>
/// The defaults open every ordinary file. Lower the limits when files come from untrusted sources and memory matters more than
/// opening unusually large files; raise them only for files you trust.
/// </remarks>
public sealed class PdfReadOptions
{
    /// <summary>The default options.</summary>
    public static PdfReadOptions Default { get; } = new();

    /// <summary>
    /// The password for an encrypted file, either the user or the owner password. The empty user password is always tried
    /// first, so this is needed only when the file has a non-empty one.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// When <see langword="true"/>, any problem that would otherwise be repaired with a warning throws a
    /// <see cref="PdfException"/> instead. The default is <see langword="false"/>: damaged files open, with every repair
    /// listed as a warning.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>The deepest nesting of arrays and dictionaries inside one object. The default is 128.</summary>
    public int MaxNestingDepth
    {
        get;
        init => field = Positive(value);
    } = 128;

    /// <summary>The most bytes one stream may decode to. The default is 512 MiB.</summary>
    public long MaxDecodedStreamBytes
    {
        get;
        init => field = Positive(value);
    } = 512L * 1024 * 1024;

    /// <summary>
    /// The highest ratio of decoded to encoded bytes for one stream, checked once a stream has decoded to more than 1 MiB.
    /// The default is 10,000.
    /// </summary>
    public int MaxDecompressionRatio
    {
        get;
        init => field = Positive(value);
    } = 10_000;

    /// <summary>The most cross-reference sections (one per save of the file) that are followed. The default is 4,096.</summary>
    public int MaxXrefSections
    {
        get;
        init => field = Positive(value);
    } = 4096;

    /// <summary>The most objects in the object table. The default is 10,000,000.</summary>
    public int MaxObjects
    {
        get;
        init => field = Positive(value);
    } = 10_000_000;

    private static T Positive<T>(T value)
        where T : System.Numerics.INumber<T>
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}
