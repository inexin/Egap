namespace Egap.Pdf.Rendering;

/// <summary>What a render needs to know beyond the page and the canvas; filled from the public render options.</summary>
internal sealed record RenderSettings
{
    public bool AntialiasPaths { get; init; } = true;

    public bool AntialiasText { get; init; } = true;

    public bool AntialiasImages { get; init; } = true;
}
