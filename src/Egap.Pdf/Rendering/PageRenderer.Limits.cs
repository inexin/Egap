namespace Egap.Pdf.Rendering;

/// <summary>
/// Limits (design Q15): operators are counted across the page, its forms, patterns, soft masks and glyph procedures, and
/// past <see cref="RenderSettings.MaxOperations"/> the rest of the page is left out with a warning, or a
/// <see cref="PdfLimitException"/> in strict mode. Every warning, the nested renderers' included, is collected for
/// <see cref="PdfRenderOptions.OnWarning"/>.
/// </summary>
internal sealed partial class PageRenderer
{
    private long operations;

    /// <summary>The warnings of every walk this renderer and the renderers it made have done, in order.</summary>
    public List<PdfWarning> Collected { get; } = [];

    private void CountOperation()
    {
        if (++operations <= settings.MaxOperations)
        {
            return;
        }

        string message = $"The page has more than {settings.MaxOperations:N0} content operators; the rest was left out.";
        if (settings.Strict)
        {
            throw new PdfLimitException(nameof(PdfRenderOptions.MaxOperations), message);
        }

        // Once per render: later counts stop at once.
        if (operations == settings.MaxOperations + 1)
        {
            Warn(PdfWarningCode.MalformedContent, message);
        }

        throw new OperationLimit();
    }

    /// <summary>Ends a walk that passed the operator limit.</summary>
    private sealed class OperationLimit : Exception
    {
    }
}
