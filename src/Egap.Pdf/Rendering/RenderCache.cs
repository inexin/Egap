using System.Runtime.CompilerServices;

using Egap.Pdf.Fonts;

namespace Egap.Pdf.Rendering;

/// <summary>
/// What every render of one document's pages shares, held by the document so callers cannot forget to share it (design
/// Q3): colour spaces now; fonts and decoded images join it in later tasks.
/// </summary>
internal sealed class RenderCache
{
    private readonly ConditionalWeakTable<PdfFont, SubstituteFont?> withSystem = [];
    private readonly ConditionalWeakTable<PdfFont, SubstituteFont?> bundledOnly = [];

    public ColorCache Colors { get; } = new();

    /// <summary>The substitute for a font with no usable program, chosen once per font and setting (design Q5).</summary>
    public SubstituteFont? Substitute(PdfFont font, bool systemFonts)
    {
        return (systemFonts ? withSystem : bundledOnly).GetValue(font, f => SubstituteFonts.For(f, systemFonts));
    }
}
