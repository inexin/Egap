namespace Egap.Pdf.Rendering;

/// <summary>What a render needs to know beyond the page and the canvas; filled from the public render options.</summary>
internal sealed record RenderSettings
{
    public bool AntialiasPaths { get; init; } = true;

    public bool AntialiasText { get; init; } = true;

    public bool AntialiasImages { get; init; } = true;

    public PdfRenderIntent Intent { get; init; }

    public static RenderSettings From(PdfRenderOptions options)
    {
        return new RenderSettings
        {
            AntialiasPaths = (options.Antialiasing & PdfAntialiasing.Paths) != 0,
            AntialiasText = (options.Antialiasing & PdfAntialiasing.Text) != 0,
            AntialiasImages = (options.Antialiasing & PdfAntialiasing.Images) != 0,
            Intent = options.Intent,
        };
    }
}
