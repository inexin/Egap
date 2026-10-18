using System.Collections.Concurrent;

using Egami.Fonts;

namespace Egap.Pdf.Fonts;

/// <summary>A font program that stands in for one that is not embedded, with the bold or italic Egami adds to it.</summary>
internal sealed record SubstituteFont(FontProgram Program, FontSynthesis Synthesis, string Source);

/// <summary>
/// Substitutes for fonts that are not embedded (Phase 4 design Q5). The standard 14 and their common aliases (Arial, Times
/// New Roman, Courier New) always get a bundled font, so they look the same on every machine: Liberation Sans, Serif and
/// Mono from Egami.Fonts.Substitutes, and Foxit's Symbol and ZapfDingbats (BSD-3-Clause, as PDFium and pdf.js ship them).
/// Other names are looked up among the system fonts when allowed, by family then style, and otherwise get the bundled font
/// their name or descriptor flags suggest (fixed pitch, serif, sans), bold and italic as the name and flags say. Glyphs are
/// always placed by the PDF's widths, so lines keep their length whatever the substitute's shapes.
/// </summary>
internal static class SubstituteFonts
{
    private enum Family
    {
        Sans,
        Serif,
        Fixed,
        Symbol,
        Dingbats,
    }

    private static readonly ConcurrentDictionary<string, FontProgram?> Bundled = new(StringComparer.Ordinal);

    /// <summary>The substitute for a font with no usable program of its own, or null for a Type 3 font.</summary>
    public static SubstituteFont? For(PdfFont font, bool systemFonts)
    {
        if (font.Type == PdfFontType.Type3)
        {
            return null;
        }

        string name = Clean(font.Name ?? font.BaseFont ?? "");
        string lower = name.ToLowerInvariant();
        PdfFontFlags flags = font.Flags;
        bool bold = lower.Contains("bold", StringComparison.Ordinal) || lower.Contains("black", StringComparison.Ordinal) || lower.Contains("heavy", StringComparison.Ordinal)
            || lower.Contains("semibold", StringComparison.Ordinal) || lower.Contains("demi", StringComparison.Ordinal)
            || (flags & PdfFontFlags.ForceBold) != 0 || Weight(font) >= 600;
        bool italic = lower.Contains("italic", StringComparison.Ordinal) || lower.Contains("oblique", StringComparison.Ordinal)
            || (flags & PdfFontFlags.Italic) != 0 || font.ItalicAngle != 0;

        Family? standard = StandardFamily(lower);
        if (standard is null && systemFonts && FamilyName(name) is { Length: > 0 } family)
        {
            FontStyle style = new(bold ? 700 : 400, 5, italic ? FontSlant.Italic : FontSlant.Upright);
            if (FontCollection.System.Match(family, style) is FontMatch match && Matches(match.Typeface, family) && Program(match.Typeface) is FontProgram system)
            {
                return new SubstituteFont(system, match.Synthesis, "system " + family);
            }
        }

        Family chosen = standard ?? Classify(lower, flags);
        return Bundle(chosen, bold, italic);
    }

    /// <summary>
    /// The installed font a font names, by family then style, without the bundled stand-ins the standard 14 names get:
    /// what PDF/A conversion embeds as the same font (Phase 5 design Q12). Null when none is installed or styles must be
    /// synthesized.
    /// </summary>
    public static SubstituteFont? SystemFont(PdfFont font)
    {
        string name = Clean(font.Name ?? font.BaseFont ?? "");
        string lower = name.ToLowerInvariant();
        bool bold = lower.Contains("bold", StringComparison.Ordinal) || lower.Contains("black", StringComparison.Ordinal) || lower.Contains("heavy", StringComparison.Ordinal)
            || (font.Flags & PdfFontFlags.ForceBold) != 0 || Weight(font) >= 600;
        bool italic = lower.Contains("italic", StringComparison.Ordinal) || lower.Contains("oblique", StringComparison.Ordinal) || (font.Flags & PdfFontFlags.Italic) != 0 || font.ItalicAngle != 0;
        if (FamilyName(name) is not { Length: > 0 } family)
        {
            return null;
        }

        FontStyle style = new(bold ? 700 : 400, 5, italic ? FontSlant.Italic : FontSlant.Upright);
        return FontCollection.System.Match(family, style) is FontMatch { Synthesis: FontSynthesis.None } match && Matches(match.Typeface, family) && Program(match.Typeface) is FontProgram system
            ? new SubstituteFont(system, FontSynthesis.None, "system " + family)
            : null;
    }

    /// <summary>The standard 14 family a name is, with the aliases viewers treat the same; null for other names.</summary>
    private static Family? StandardFamily(string lower)
    {
        string bare = lower.Replace("-", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        if (bare.StartsWith("helvetica", StringComparison.Ordinal) || bare.StartsWith("arial", StringComparison.Ordinal))
        {
            return Family.Sans;
        }

        if (bare.StartsWith("times", StringComparison.Ordinal))
        {
            return Family.Serif;
        }

        if (bare.StartsWith("courier", StringComparison.Ordinal))
        {
            return Family.Fixed;
        }

        if (bare is "symbol" or "symbolmt")
        {
            return Family.Symbol;
        }

        return bare.StartsWith("zapfdingbats", StringComparison.Ordinal) || bare.StartsWith("itczapfdingbats", StringComparison.Ordinal) ? Family.Dingbats : null;
    }

    /// <summary>A bundled family for any other name: by words in the name, then by the descriptor's flags.</summary>
    private static Family Classify(string lower, PdfFontFlags flags)
    {
        if (lower.Contains("mono", StringComparison.Ordinal) || lower.Contains("courier", StringComparison.Ordinal) || lower.Contains("consol", StringComparison.Ordinal)
            || lower.Contains("typewriter", StringComparison.Ordinal) || (flags & PdfFontFlags.FixedPitch) != 0)
        {
            return Family.Fixed;
        }

        if (lower.Contains("sans", StringComparison.Ordinal) || lower.Contains("gothic", StringComparison.Ordinal) || lower.Contains("grotesk", StringComparison.Ordinal))
        {
            return Family.Sans;
        }

        if (lower.Contains("serif", StringComparison.Ordinal) || lower.Contains("roman", StringComparison.Ordinal) || lower.Contains("mincho", StringComparison.Ordinal)
            || lower.Contains("georgia", StringComparison.Ordinal) || lower.Contains("garamond", StringComparison.Ordinal) || lower.Contains("palatino", StringComparison.Ordinal)
            || lower.Contains("bookman", StringComparison.Ordinal) || lower.Contains("cambria", StringComparison.Ordinal) || lower.Contains("century", StringComparison.Ordinal)
            || (flags & PdfFontFlags.Serif) != 0)
        {
            return Family.Serif;
        }

        return Family.Sans;
    }

    private static SubstituteFont? Bundle(Family family, bool bold, bool italic)
    {
        if (family is Family.Symbol or Family.Dingbats)
        {
            string file = family == Family.Symbol ? "FoxitSymbol.pfb" : "FoxitDingbats.pfb";
            return Load(file) is FontProgram foxit ? new SubstituteFont(foxit, FontSynthesis.None, file) : null;
        }

        // Liberation, metric-compatible with Helvetica, Times and Courier, from Egami.Fonts.Substitutes.
        string name = family switch { Family.Serif => "Times", Family.Fixed => "Courier", _ => "Helvetica" };
        FontStyle style = new(bold ? 700 : 400, 5, italic ? FontSlant.Italic : FontSlant.Upright);
        return global::Egami.Fonts.Substitutes.SubstituteFonts.Find(name, style) is Typeface typeface && Program(typeface) is FontProgram program
            ? new SubstituteFont(program, FontSynthesis.None, typeface.PostScriptName)
            : null;
    }

    private static FontProgram? Load(string file)
    {
        return Bundled.GetOrAdd(file, f =>
        {
            using Stream? stream = typeof(SubstituteFonts).Assembly.GetManifestResourceStream("Egap.Pdf.Fonts.Substitutes." + f);
            if (stream is null)
            {
                return null;
            }

            using MemoryStream copy = new();
            stream.CopyTo(copy);
            return FontProgram.FromFile(copy.ToArray());
        });
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Typeface, FontProgram> Programs = [];

    /// <summary>A system or bundled substitute typeface as a font program, read once, so its cmaps and glyph names are read as an embedded one's would be.</summary>
    private static FontProgram? Program(Typeface typeface)
    {
        try
        {
            return Programs.GetValue(typeface, FontProgram.FromTypeface);
        }
        catch (Exception e) when (e is Egami.EgamiException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether a match is the family asked for, not the collection's fallback for an unknown name.</summary>
    private static bool Matches(Typeface typeface, string family)
    {
        string wanted = family.Replace(" ", "", StringComparison.Ordinal);
        return typeface.FamilyName.Replace(" ", "", StringComparison.Ordinal).Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || typeface.PostScriptName.StartsWith(wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name without a subset tag or spaces.</summary>
    private static string Clean(string name)
    {
        if (name.Length > 7 && name[6] == '+')
        {
            name = name[7..];
        }

        return name.Replace(" ", "", StringComparison.Ordinal);
    }

    /// <summary>The family part of a PostScript-style name: before a comma or hyphen, without the MT and PS suffixes.</summary>
    private static string FamilyName(string name)
    {
        int cut = name.IndexOfAny([',', '-']);
        string family = cut > 0 ? name[..cut] : name;
        foreach (string suffix in (string[])["PSMT", "MT", "PS"])
        {
            if (family.Length > suffix.Length && family.EndsWith(suffix, StringComparison.Ordinal))
            {
                family = family[..^suffix.Length];
                break;
            }
        }

        return family;
    }

    private static double Weight(PdfFont font)
    {
        return font.Descriptor?.GetNumber("FontWeight") ?? 0;
    }
}
