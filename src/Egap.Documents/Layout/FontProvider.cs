using System.Collections.Concurrent;

using Egami.Fonts;
using Egami.Fonts.Shaping;

using Egap.Fonts;

namespace Egap.Documents.Layout;

/// <summary>
/// The font chain of <see cref="FontSettings"/> for one document (Build Plan task 15): resolves a family and style to a face
/// with the synthesis it needs, builds the per-script fallback for glyphs a font lacks, and records each substitution once.
/// Thread-safe; results are cached per family and style.
/// </summary>
internal sealed class FontProvider
{
    private readonly FontSettings settings;
    private readonly List<FontCollection> chain = [];
    private readonly ConcurrentDictionary<(string Family, FontStyle Style), FontMatch> cache = new();
    private readonly ConcurrentDictionary<FontSubstitution, byte> substitutions = new();
    private readonly Lazy<FontFallback> fallback;

    public FontProvider(Document document, FontSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.settings = settings ?? FontSettings.Default;
        if (document.EmbeddedFonts.Count > 0)
        {
            FontCollection embedded = new();
            foreach (EmbeddedFont font in document.EmbeddedFonts)
            {
                try
                {
                    foreach (Typeface face in FontLoader.LoadAll(font.Data))
                    {
                        embedded.Add(face);
                    }
                }
                catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
                {
                    // A damaged embedded font is skipped; the chain goes on to the next source.
                }
            }

            chain.Add(embedded);
        }

        chain.AddRange(this.settings.Collections);
        if (this.settings.UseSystemFonts)
        {
            chain.Add(FontCollection.System);
        }

        fallback = new Lazy<FontFallback>(() =>
        {
            List<FontFamily> families = [.. chain.SelectMany(c => c.Families)];
            if (this.settings.UseStandardSubstitutes)
            {
                families.AddRange(StandardFonts.Collection.Families);
            }

            return new FontFallback(families, this.settings.UseSystemFonts ? SystemFontFallback.Instance : null);
        });
    }

    /// <summary>The fonts used in place of the ones asked so far, each once, in the order first met.</summary>
    public IReadOnlyList<FontSubstitution> Substitutions => [.. substitutions.Keys];

    /// <summary>The fallback for characters the chosen font has no glyph for, searched by script.</summary>
    public FontFallback Fallback => fallback.Value;

    /// <summary>The face for <paramref name="family"/> in <paramref name="style"/>, with any synthesis it needs.</summary>
    public FontMatch Resolve(string? family, FontStyle style)
    {
        string name = string.IsNullOrWhiteSpace(family) ? "" : family.Trim();
        return cache.GetOrAdd((name, style), key => Find(key.Family, key.Style));
    }

    /// <summary>Records that characters of <paramref name="requested"/> came from <paramref name="used"/>.</summary>
    public void NoteGlyphFallback(string requested, FontStyle style, string used)
    {
        substitutions.TryAdd(new FontSubstitution(requested, style, used, FontSubstitutionReason.GlyphFallback), 0);
    }

    private FontMatch Find(string family, FontStyle style)
    {
        if (family.Length > 0)
        {
            if (Search(family, style) is { } direct)
            {
                if (direct.Synthesis != FontSynthesis.None)
                {
                    Note(family, style, direct.Typeface.FamilyName, FontSubstitutionReason.Synthesized);
                }

                return direct;
            }

            if (settings.Substitutions.TryGetValue(family, out string? alternative) && Search(alternative, style) is { } substituted)
            {
                Note(family, style, substituted.Typeface.FamilyName, FontSubstitutionReason.Missing);
                return substituted;
            }

            if (settings.UseStandardSubstitutes && StandardFonts.Find(family, style) is { } standard)
            {
                Note(family, style, standard.FamilyName, FontSubstitutionReason.Missing);
                return StandardFonts.Collection.Match(standard.FamilyName, style) ?? new FontMatch(standard, FontSynthesis.None);
            }
        }

        FontMatch last = LastResort(style);
        if (family.Length > 0)
        {
            Note(family, style, last.Typeface.FamilyName, FontSubstitutionReason.Missing);
        }

        return last;
    }

    private FontMatch? Search(string family, FontStyle style)
    {
        foreach (FontCollection collection in chain)
        {
            if (collection.Match(family, style) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private FontMatch LastResort(FontStyle style)
    {
        if (settings.LastResortFamily is { } family && Search(family, style) is { } chosen)
        {
            return chosen;
        }

        // Liberation Sans ships with Egap.Fonts, so there is always a face, whatever the settings and the machine.
        return StandardFonts.Collection.Match("Liberation Sans", style)
            ?? new FontMatch(StandardFonts.Find("Arial", style)!, FontSynthesis.None);
    }

    private void Note(string requested, FontStyle style, string used, FontSubstitutionReason reason)
    {
        substitutions.TryAdd(new FontSubstitution(requested, style, used, reason), 0);
    }
}
