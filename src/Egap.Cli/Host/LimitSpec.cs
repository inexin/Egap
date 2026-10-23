using Egap.Pdf;

namespace Egap.Cli.Host;

/// <summary>
/// Parses <c>--limit</c> and <c>EGAP_LIMITS</c> (CLI design Q12): comma-separated <c>name=value</c> pairs over
/// <see cref="PdfReadOptions"/>. Names: <c>objects</c> (a count such as <c>2M</c>), <c>decoded</c> (bytes one stream may decode
/// to, such as <c>512MB</c>), <c>ratio</c> (the most a stream may grow when decoded), <c>depth</c> (nesting of arrays and
/// dictionaries) and <c>xref</c> (cross-reference sections followed).
/// </summary>
internal static class LimitSpec
{
    /// <summary>The names a limit takes.</summary>
    public static readonly string[] Names = ["objects", "decoded", "ratio", "depth", "xref"];

    /// <summary>Applies a limit list to read options; <see langword="null"/> or empty text changes nothing.</summary>
    public static PdfReadOptions Apply(PdfReadOptions options, string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return options;
        }

        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new UsageException($"{source}: '{part}' is not name=value; names are {string.Join(", ", Names)}.");
            }

            string name = part[..equals].Trim().ToLowerInvariant();
            string value = part[(equals + 1)..].Trim();
            options = name switch
            {
                "objects" => options with { MaxObjects = Int(LimitText.Count(value, source)) },
                "decoded" => options with { MaxDecodedStreamBytes = LimitText.Bytes(value, source) },
                "ratio" => options with { MaxDecompressionRatio = Int(LimitText.Count(value, source)) },
                "depth" => options with { MaxNestingDepth = Int(LimitText.Count(value, source)) },
                "xref" => options with { MaxXrefSections = Int(LimitText.Count(value, source)) },
                _ => throw new UsageException($"{source}: unknown limit '{name}'; names are {string.Join(", ", Names)}."),
            };
        }

        return options;
    }

    private static int Int(long value) => (int)Math.Min(int.MaxValue, value);
}
