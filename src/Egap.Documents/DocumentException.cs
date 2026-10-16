namespace Egap.Documents;

/// <summary>A file could not be read as the format it was given as: not a package, a part missing or malformed beyond repair.</summary>
public class DocumentException : Exception
{
    /// <summary>A document error with a message.</summary>
    public DocumentException(string message)
        : base(message)
    {
    }

    /// <summary>A document error with a message and the error that caused it.</summary>
    public DocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A file went past one of the reader's limits (Phase 6 design Q9: part sizes, entry counts, compression ratio, nesting), so
/// reading stopped instead of using unbounded memory or time. The limits are in the format's load options.
/// </summary>
public sealed class DocumentLimitException : DocumentException
{
    /// <summary>A limit error with a message.</summary>
    public DocumentLimitException(string message)
        : base(message)
    {
    }
}

/// <summary>The limits a reader keeps to on untrusted files; the defaults suit any real document.</summary>
public sealed class DocumentLimits
{
    /// <summary>The most entries a package may have (default 10,000).</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>The largest uncompressed part, in bytes (default 512 MiB).</summary>
    public long MaxPartSize { get; init; } = 512L * 1024 * 1024;

    /// <summary>The most uncompressed bytes in all parts together (default 2 GiB).</summary>
    public long MaxTotalSize { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>The highest compression ratio a part over 1 MiB may have, against ZIP bombs (default 1,000).</summary>
    public double MaxCompressionRatio { get; init; } = 1000;

    /// <summary>How deep XML elements, tables and other structures may nest (default 256).</summary>
    public int MaxDepth { get; init; } = 256;
}
