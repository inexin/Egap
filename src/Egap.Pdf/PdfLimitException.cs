namespace Egap.Pdf;

/// <summary>
/// The exception thrown when a file goes past one of the limits in <see cref="PdfReadOptions"/>. It is never a warning, even
/// outside strict mode: going on could exhaust memory or time.
/// </summary>
public sealed class PdfLimitException : PdfException
{
    /// <summary>Creates an exception for the named limit.</summary>
    /// <param name="limit">The name of the <see cref="PdfReadOptions"/> property that was exceeded.</param>
    /// <param name="message">What went past the limit.</param>
    public PdfLimitException(string limit, string message)
        : base(message)
    {
        Limit = limit;
    }

    /// <summary>The name of the <see cref="PdfReadOptions"/> property that was exceeded, such as <c>MaxNestingDepth</c>.</summary>
    public string Limit { get; }
}
