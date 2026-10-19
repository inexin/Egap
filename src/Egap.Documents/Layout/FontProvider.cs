using System.Collections.Concurrent;

using Egami.Fonts;
using Egami.Fonts.Shaping;
using Egami.Fonts.Substitutes;

namespace Egap.Documents.Layout;

/// <summary>
/// The font chain of <see cref="FontSettings"/> for one document (Build Plan task 15): resolves a family and style to a face
/// with the synthesis it needs, builds the per-script fallback for glyphs a font lacks, and records each substitution once.
/// Thread-safe; results are cached per family and style.
/// </summary>
internal sealed class FontProvider
{
    private readonly FontSettings settings;

    /// <summary>The settings' substitutions, keys ignoring case.</summary>
    private readonly Dictionary<string, string> replacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FontCollection> chain = [];
    private readonly ConcurrentDictionary<(string Family, FontStyle Style), FontMatch> cache = new();
    private readonly ConcurrentDictionary<FontSubstitution, byte> substitutions = new();
    private readonly ConcurrentDictionary<string, byte> lastResort = new(StringComparer.OrdinalIgnoreCase);
    private readonly Document document;
    private readonly Lazy<FontFallback> fallback;

    /// <summary>
    /// The fonts Office downloads on demand (Aptos, the default theme font since 2023, among them), which Word uses but Windows
    /// does not list as installed: %LOCALAPPDATA%\Microsoft\FontCache\4\CloudFonts. Null where there are none.
    /// </summary>
    private static readonly Lazy<FontCollection?> OfficeCloudFonts = new(() =>
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "FontCache", "4", "CloudFonts");
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
        {
            return null;
        }

        FontCollection cloud = new();
        foreach (string file in Directory.EnumerateFiles(folder, "*.ttf", SearchOption.AllDirectories))
        {
            try
            {
                cloud.AddFile(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Egami.EgamiException)
            {
                // A file still downloading or damaged is left out.
            }
        }

        return cloud.Count > 0 ? cloud : null;
    });

    public FontProvider(Document document, FontSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document;
        this.settings = settings ?? FontSettings.Default;
        foreach (KeyValuePair<string, string> substitution in this.settings.Substitutions)
        {
            replacements.TryAdd(substitution.Key, substitution.Value);
        }

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
            if (OfficeCloudFonts.Value is { } cloud)
            {
                chain.Add(cloud);
            }
        }

        fallback = new Lazy<FontFallback>(() =>
        {
            List<FontFamily> families = [.. chain.SelectMany(c => c.Families)];
            if (this.settings.UseStandardSubstitutes)
            {
                families.AddRange(SubstituteFonts.Collection.Families);
            }

            return new FontFallback(families, this.settings.UseSystemFonts ? SystemFontFallback.Instance : null);
        });
    }

    /// <summary>The fonts used in place of the ones asked so far, each once, in the order first met.</summary>
    public IReadOnlyList<FontSubstitution> Substitutions => [.. substitutions.Keys];

    /// <summary>The families nothing in the chain could stand in for, so the last resort was used.</summary>
    public IReadOnlyCollection<string> LastResortFamilies => [.. lastResort.Keys];

    /// <summary>The fallback for characters the chosen font has no glyph for, searched by script.</summary>
    public FontFallback Fallback => fallback.Value;

    /// <summary>The face for <paramref name="family"/> in <paramref name="style"/>, with any synthesis it needs.</summary>
    public FontMatch Resolve(string? family, FontStyle style)
    {
        string name = string.IsNullOrWhiteSpace(family) ? "" : family.Trim();
        return cache.GetOrAdd((name, style), key => Find(key.Family, key.Style));
    }

    /// <summary>The font family the chain gives for <paramref name="family"/>, for Egami's text styles; null for an empty name.</summary>
    public FontFamily? ResolveFamily(string? family, FontStyle style)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        string used = Resolve(family, style).Typeface.FamilyName;
        foreach (FontCollection collection in chain)
        {
            if (collection.TryGetFamily(used, out FontFamily found))
            {
                return found;
            }
        }

        return SubstituteFonts.Collection.TryGetFamily(used, out FontFamily standard) ? standard : null;
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

            if (replacements.TryGetValue(family, out string? alternative) && Search(alternative, style) is { } substituted)
            {
                Note(family, style, substituted.Typeface.FamilyName, FontSubstitutionReason.Missing);
                return substituted;
            }

            if (settings.UseStandardSubstitutes && SubstituteFonts.Find(family, style) is { } standard)
            {
                Note(family, style, standard.FamilyName, FontSubstitutionReason.Missing);
                return SubstituteFonts.Collection.Match(standard.FamilyName, style) ?? new FontMatch(standard, FontSynthesis.None);
            }

            // Word's own substitution from the font table: the alternative name, then a face of the font's kind.
            if (document.FontHints.TryGetValue(family, out (string? AltName, string? Kind) hint))
            {
                string? kind = hint.Kind switch
                {
                    "roman" => "Times New Roman",
                    "swiss" => "Arial",
                    "modern" => "Courier New",
                    _ => null,
                };
                foreach (string? candidate in (ReadOnlySpan<string?>)[hint.AltName, kind])
                {
                    if (string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate, family, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (Search(candidate, style) is { } alternate)
                    {
                        Note(family, style, alternate.Typeface.FamilyName, FontSubstitutionReason.Missing);
                        return alternate;
                    }

                    if (settings.UseStandardSubstitutes && SubstituteFonts.Find(candidate, style) is { } twin)
                    {
                        Note(family, style, twin.FamilyName, FontSubstitutionReason.Missing);
                        return SubstituteFonts.Collection.Match(twin.FamilyName, style) ?? new FontMatch(twin, FontSynthesis.None);
                    }
                }
            }
        }

        FontMatch last = LastResort(style);
        if (family.Length > 0)
        {
            Note(family, style, last.Typeface.FamilyName, FontSubstitutionReason.Missing);
            lastResort.TryAdd(family, 0);
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

        // Liberation Sans ships with Egami.Fonts.Substitutes, so there is always a face, whatever the settings and the machine.
        return SubstituteFonts.Collection.Match("Liberation Sans", style)
            ?? new FontMatch(SubstituteFonts.Find("Arial", style)!, FontSynthesis.None);
    }

    private void Note(string requested, FontStyle style, string used, FontSubstitutionReason reason)
    {
        substitutions.TryAdd(new FontSubstitution(requested, style, used, reason), 0);
    }
}
