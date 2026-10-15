using System.Runtime.CompilerServices;

using Egap.Pdf.Fonts;
using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// What every render of one document's pages shares, held by the document so callers cannot forget to share it (design
/// Q3): colour spaces now; fonts and decoded images join it in later tasks.
/// </summary>
internal sealed class RenderCache(PdfDocument document)
{
    private ColorCache? withIntent;
    private ColorCache? withoutIntent;

    private readonly ConditionalWeakTable<PdfFont, SubstituteFont?> withSystem = [];
    private readonly ConditionalWeakTable<PdfFont, SubstituteFont?> bundledOnly = [];

    /// <summary>The colour spaces, read with the document's output intent or without it (design Q7).</summary>
    public ColorCache Colors(bool outputIntent)
    {
        return outputIntent
            ? LazyInitializer.EnsureInitialized(ref withIntent, () => new ColorCache(ColorContext.ForDocument(document, useOutputIntent: true)))
            : LazyInitializer.EnsureInitialized(ref withoutIntent, () => new ColorCache(new ColorContext()));
    }

    public ImageCache Images { get; } = new();

    private readonly ConditionalWeakTable<PdfObject, ShadingPaint> shadings = [];

    /// <summary>A shading made ready once per document: its function sampled into stops (design Q10).</summary>
    public ShadingPaint Shading(PdfObject shading, Func<ShadingPaint> read)
    {
        return shadings.GetValue(shading, _ => read());
    }

    /// <summary>The substitute for a font with no usable program, chosen once per font and setting (design Q5).</summary>
    public SubstituteFont? Substitute(PdfFont font, bool systemFonts)
    {
        return (systemFonts ? withSystem : bundledOnly).GetValue(font, f => SubstituteFonts.For(f, systemFonts));
    }
}
