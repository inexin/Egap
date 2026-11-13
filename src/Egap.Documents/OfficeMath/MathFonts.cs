using Egami.Fonts;
using Egami.Fonts.Substitutes;

using Egap.Documents.Layout;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// The math font chain (Design Q4): the document's math font (<c>m:mathFont</c>, Cambria Math by default) through the
/// document's font chain when the face it finds has an OpenType MATH table, else an installed Cambria Math, else the bundled
/// STIX Two Math, which every machine has.
/// </summary>
internal static class MathFonts
{
    /// <summary>The face equations are set in, with its MATH table.</summary>
    public static (Typeface Face, TypefaceMath Math) Resolve(FontProvider fonts, MathSettings settings)
    {
        foreach (string family in (ReadOnlySpan<string>)[settings.Font, "Cambria Math"])
        {
            Typeface face = fonts.Resolve(family, FontStyle.Normal).Typeface;
            if (face.Math is { } math)
            {
                return (face, math);
            }
        }

        Typeface stix = SubstituteFonts.Find("Cambria Math", FontStyle.Normal)!;
        fonts.NoteMissing(settings.Font, FontStyle.Normal, stix.FamilyName);
        return (stix, stix.Math!);
    }
}
