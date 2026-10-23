using Egap.Documents;
using Egap.Documents.Layout;
using Egap.Pdf;

namespace Egap.Cli.Host;

/// <summary>
/// One run of egap: the shared host's session (console, folders, verbosity, batches) plus the read options every command opens
/// PDFs with: safety limits from <c>EGAP_LIMITS</c> and <c>--limit</c>, and <c>--strict</c> (CLI design Q12).
/// </summary>
internal sealed class EgapSession : CliHostSession
{
    /// <summary>The extensions egap reads: PDFs and everything <c>convert</c> takes.</summary>
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "html", "htm", "xhtml", "md", "markdown", "docx", "txt", "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "webp",
    };

    public EgapSession(CliConsole console, string workingDirectory, Func<string, string?> environment)
        : base("egap", console, workingDirectory, environment)
    {
    }

    /// <summary>Gets the options PDFs are opened with, without a password.</summary>
    public PdfReadOptions ReadOptions { get; private set; } = PdfReadOptions.Default;

    /// <summary>Gets or sets the shared options of the command tree.</summary>
    public GlobalOptions? Globals { get; set; }

    /// <summary>Gets or sets the steps of a <c>run</c> given as values rather than words (<c>egap mcp</c>'s run tool).</summary>
    public object? Run { get; set; }

    /// <inheritdoc/>
    public override string PatternExample => "*.pdf";

    /// <summary>Sets the verbosity, the limits (from <c>EGAP_LIMITS</c> and then <c>--limit</c>) and strict reading.</summary>
    public void Configure(Verbosity verbosity, string? limits, bool strict)
    {
        Verbosity = verbosity;
        PdfReadOptions options = LimitSpec.Apply(PdfReadOptions.Default, Environment("EGAP_LIMITS"), "EGAP_LIMITS");
        ReadOptions = LimitSpec.Apply(options, limits, "--limit") with { Strict = strict };
    }

    /// <inheritdoc/>
    public override bool IsInput(string path) => Readable.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <inheritdoc/>
    public override int? Failure(Exception error) => FailureOf(error);

    /// <inheritdoc/>
    public override string Message(Exception error)
    {
        return error switch
        {
            PdfLimitException or DocumentLimitException => $"exceeds a safety limit: {error.Message} Raise it with --limit if you trust the file.",
            PdfPasswordException => $"{error.Message} Give the password with --password-env, --password-file or --password.",
            _ => error.Message,
        };
    }

    /// <summary>The exit code an exception from one file maps to, or <see langword="null"/> for a bug, which is not caught.</summary>
    public static int? FailureOf(Exception error)
    {
        return error switch
        {
            PdfLimitException or DocumentLimitException => ExitCodes.Limit,
            PdfException or DocumentException or LayoutException or IOException or UnauthorizedAccessException
                or NotSupportedException or InvalidOperationException or ArgumentException or FormatException => ExitCodes.Failed,
            _ => null,
        };
    }

    /// <summary>Writes the warnings a document collected while it was read and repaired, unless quiet.</summary>
    public void Report(string input, IEnumerable<PdfWarning> warnings)
    {
        foreach (PdfWarning warning in warnings)
        {
            Warn($"{input}: {warning.Code}: {warning.Message}");
        }
    }
}
