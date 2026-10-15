namespace Egap.Pdf.Rendering;

/// <summary>What a render needs to know beyond the page and the canvas; filled from the public render options.</summary>
internal sealed record RenderSettings
{
    public bool AntialiasPaths { get; init; } = true;

    public bool AntialiasText { get; init; } = true;

    public bool AntialiasImages { get; init; } = true;

    public PdfRenderIntent Intent { get; init; }

    public bool SystemFonts { get; init; } = true;

    public bool OutputIntents { get; init; } = true;

    public bool Strict { get; init; }

    public long MaxOperations { get; init; } = PdfRenderOptions.Default.MaxOperations;

    public long MaxImagePixels { get; init; } = PdfRenderOptions.Default.MaxImagePixels;

    public CancellationToken Cancellation { get; init; }

    /// <summary>Which optional content shows; null when everything does.</summary>
    public OptionalContent? Layers { get; init; }

    public long ImageCacheLimit { get; init; } = PdfRenderOptions.Default.ImageCacheLimit;

    public static RenderSettings From(PdfRenderOptions options)
    {
        return new RenderSettings
        {
            AntialiasPaths = (options.Antialiasing & PdfAntialiasing.Paths) != 0,
            AntialiasText = (options.Antialiasing & PdfAntialiasing.Text) != 0,
            AntialiasImages = (options.Antialiasing & PdfAntialiasing.Images) != 0,
            Intent = options.Intent,
            SystemFonts = options.UseSystemFonts,
            OutputIntents = options.UseOutputIntents,
            Strict = options.Strict,
            MaxOperations = options.MaxOperations,
            MaxImagePixels = options.MaxImagePixels,
            ImageCacheLimit = options.ImageCacheLimit,
        };
    }
}
