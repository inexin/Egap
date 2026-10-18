using Egami.Fonts;

namespace Egap.Documents.Layout;

/// <summary>
/// Where the layout engine finds fonts (Phase 6 design Q12): the document's embedded fonts first, then the caller's
/// collections, then the system's, then metric-compatible substitutes from Egami.Fonts.Substitutes, then a last resort.
/// Every font the document names and the chain could not give as asked is reported as a <see cref="FontSubstitution"/>.
/// </summary>
public sealed class FontSettings
{
    /// <summary>Fonts the caller supplies, searched after the document's own and before the system's.</summary>
    public IReadOnlyList<FontCollection> Collections { get; init; } = [];

    /// <summary>Whether the system's installed fonts are searched; off for output that is the same on every machine.</summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// Whether the metric-compatible substitutes in Egami.Fonts.Substitutes (Carlito, Caladea, Liberation) stand in for missing
    /// fonts.
    /// </summary>
    public bool UseStandardSubstitutes { get; init; } = true;

    /// <summary>
    /// Family names to try in place of others, searched through the same chain: a document asking for a key gets the value
    /// when the key itself is not found (Word's and LibreOffice's replacement tables work the same way). Keys match
    /// ignoring case.
    /// </summary>
    public IReadOnlyDictionary<string, string> Substitutions { get; init; } = new Dictionary<string, string>();

    /// <summary>The family used when nothing else matches; Liberation Sans from Egami.Fonts.Substitutes when null.</summary>
    public string? LastResortFamily { get; init; }

    /// <summary>The settings a layout uses when none are given: system fonts and the standard substitutes on.</summary>
    public static FontSettings Default { get; } = new();
}

/// <summary>Why a font the document asked for was not used as asked.</summary>
public enum FontSubstitutionReason
{
    /// <summary>The family was not found; another stood in for it.</summary>
    Missing,

    /// <summary>The family was found but not in the weight or slant asked; the face is synthesized (emboldened or slanted).</summary>
    Synthesized,

    /// <summary>Some characters had no glyph in the font and came from a fallback font for their script.</summary>
    GlyphFallback,
}

/// <summary>One font the layout could not use as the document asked, and what it used instead.</summary>
/// <param name="Requested">The family the document named.</param>
/// <param name="Style">The weight, width and slant asked.</param>
/// <param name="Used">The family used.</param>
/// <param name="Reason">Why.</param>
public sealed record FontSubstitution(string Requested, FontStyle Style, string Used, FontSubstitutionReason Reason);
