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

    /// <summary>The profile DeviceCMYK goes through without a CMYK output intent; null for FOGRA39.</summary>
    public Egami.Colors.ColorProfile? DefaultCmyk { get; init; }

    public bool Strict { get; init; }

    public bool TransferFunctions { get; init; } = true;

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
            DefaultCmyk = options.DefaultCmykProfile,
            Strict = options.Strict,
            TransferFunctions = options.ApplyTransferFunctions,
            MaxOperations = options.MaxOperations,
            MaxImagePixels = options.MaxImagePixels,
            ImageCacheLimit = options.ImageCacheLimit,
        };
    }
}
