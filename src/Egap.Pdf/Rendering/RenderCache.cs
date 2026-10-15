namespace Egap.Pdf.Rendering;

/// <summary>
/// What every render of one document's pages shares, held by the document so callers cannot forget to share it (design
/// Q3): colour spaces now; fonts and decoded images join it in later tasks.
/// </summary>
internal sealed class RenderCache
{
    public ColorCache Colors { get; } = new();
}
