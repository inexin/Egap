using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egami.Fonts;

using Egap.Pdf.Annotations;

namespace Egap.Pdf.Optimizing;

/// <summary>
/// Font subsetting (Phase 7 design Q6, Build Plan task 7, review R1): embedded TrueType and CFF programs are cut to the glyphs
/// the document shows, programs already tagged as subsets too, since producers often embed far more than they show; a cut is
/// kept only when it saves 10% or more. Egami's subsetter does the cut, keeping glyph IDs so content streams, encodings and
/// CIDToGIDMaps stay as they are. Identical programs were merged before (task 6), so a program shared by several fonts is cut once to what all of
/// them use. A font is left whole when it may still type new text or show glyphs the walk cannot see: fonts in the form's
/// default resources or a widget's appearance, fonts in tiling patterns and soft masks, and every font of content that
/// cannot be read. Fonts are never unembedded. Each result is checked: every code shown must reach a glyph with the same
/// outline and advance as before, or the font is kept as it was. Where keeping glyph IDs does not pass (name-keyed CFF),
/// the glyphs are renumbered and checked again: such fonts find glyphs by name, which the cut charset keeps.
/// </summary>
internal static class FontSubsetter
{
    /// <summary>A cut program is kept only at this share of the original's size or less.</summary>
    internal const double MinShare = 0.9;

    /// <summary>Subsets what can be subset; how many programs were cut and the bytes they no longer hold.</summary>
    /// <param name="document">The document.</param>
    /// <param name="keepCharSets">
    /// True for PDF/A-1, whose subset fonts must list their glyphs in a CIDSet or CharSet: then nothing is cut.
    /// </param>
    public static (int Fonts, long Bytes) Run(PdfDocument document, bool keepCharSets = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (keepCharSets)
        {
            return (0, 0);
        }

        if (!HasCandidates(document))
        {
            return (0, 0);
        }

        Dictionary<PdfDictionary, HashSet<int>> codes = new(ReferenceEqualityComparer.Instance);
        HashSet<PdfDictionary> excluded = new(ReferenceEqualityComparer.Instance);
        Collect(document, codes, excluded);

        // Every font dictionary that shows glyphs from a program, by the program's stream.
        Dictionary<PdfStream, List<(PdfDictionary Font, HashSet<int> Codes)>> users = new(ReferenceEqualityComparer.Instance);
        HashSet<PdfStream> blocked = new(ReferenceEqualityComparer.Instance);
        foreach ((PdfDictionary font, HashSet<int> used) in codes)
        {
            if (Program(font) is not (PdfStream program, _))
            {
                continue;
            }

            if (excluded.Contains(font))
            {
                blocked.Add(program);
                continue;
            }

            if (!users.TryGetValue(program, out var list))
            {
                users[program] = list = [];
            }

            list.Add((font, used));
        }

        foreach (PdfDictionary font in excluded)
        {
            if (Program(font) is (PdfStream program, _))
            {
                blocked.Add(program);
            }
        }

        int fonts = 0;
        long bytes = 0;
        foreach ((PdfStream program, List<(PdfDictionary Font, HashSet<int> Codes)> list) in users)
        {
            if (blocked.Contains(program) || list.Any(u => PdfFont.FromDictionary(u.Font) is { IsEmbedded: false }))
            {
                continue;
            }

            long before = program.EncodedLength;
            if (Subset(program, list))
            {
                fonts++;
                bytes += before - program.EncodedLength;
            }
        }

        return (fonts, bytes);
    }

    /// <summary>
    /// Whether any font object holds a program that could be cut: embedded, not a subset already, and in a format written
    /// back. Without one the walk over every page that finds the codes shown is not needed.
    /// </summary>
    private static bool HasCandidates(PdfDocument document)
    {
        foreach (PdfReference reference in document.Objects)
        {
            if (document.Objects[reference] is PdfDictionary font && Program(font) is not null
                && PdfFont.FromDictionary(font) is { IsEmbedded: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The embedded program of a font and the subset format it is written back in; null for none or Type 1.</summary>
    internal static (PdfStream Program, SubsetFormat Format)? Program(PdfDictionary font)
    {
        PdfDictionary? descriptor = font.GetName("Subtype")?.Value == "Type0"
            ? (font.Get<PdfArray>("DescendantFonts")?.Get(0)?.Resolve() as PdfDictionary)?.Get<PdfDictionary>("FontDescriptor")
            : font.Get<PdfDictionary>("FontDescriptor");
        if (descriptor?.Get("FontFile2")?.Resolve() is PdfStream trueType)
        {
            return (trueType, SubsetFormat.TrueType);
        }

        if (descriptor?.Get("FontFile3")?.Resolve() is PdfStream file3)
        {
            return file3.Dictionary.GetName("Subtype")?.Value switch
            {
                "Type1C" or "CIDFontType0C" => (file3, SubsetFormat.BareCff),
                "OpenType" => (file3, SubsetFormat.Auto),
                _ => null,
            };
        }

        return null;
    }

    private static bool Subset(PdfStream program, List<(PdfDictionary Font, HashSet<int> Codes)> users)
    {
        (_, SubsetFormat format) = Program(users[0].Font)!.Value;
        byte[] original;
        Typeface face;
        HashSet<ushort> glyphs = [0];

        // The glyph each font shows for each code in the whole program, which the cut program must show again.
        List<Dictionary<int, (ushort Glyph, string? Text)>> shown = [];
        try
        {
            original = program.GetDecodedBytes();
            foreach ((PdfDictionary font, HashSet<int> used) in users)
            {
                PdfFont reader = PdfFont.FromDictionary(font);
                Dictionary<int, (ushort, string?)> map = [];
                foreach (int code in used)
                {
                    ushort glyph = reader.GetGlyphId(code) ?? 0;
                    map[code] = (glyph, reader.ToUnicode(code));
                    glyphs.Add(glyph);
                }

                shown.Add(map);
            }

            face = PdfFont.FromDictionary(users[0].Font).GetTypeface() ?? throw new PdfException("No program.");
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or Egami.EgamiException or ArgumentException)
        {
            return false;
        }

        if (glyphs.Count >= face.GlyphCount)
        {
            return false;
        }

        byte[] encoded = program.GetEncodedBytes();
        PdfObject? filter = program.Dictionary.Get("Filter"), parms = program.Dictionary.Get("DecodeParms"), length1 = program.Dictionary.Get("Length1");

        // Glyph IDs kept where the format allows, so codes reach the same glyphs; else renumbered, for CFF fonts whose glyphs
        // are found by name or CID, which the cut charset keeps.
        foreach (bool retain in new[] { true, false })
        {
            FontSubset subset;
            try
            {
                subset = Egami.Fonts.FontSubsetter.Subset(face, glyphs, new SubsetOptions { Format = format, RetainGlyphIds = retain, KeepCmap = true });
            }
            catch (Exception e) when (e is Egami.EgamiException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                continue;
            }

            // A cut must save something worth a rewrite: a program cut before, by Egap or its producer, often cannot.
            if (subset.Data.Length > original.Length * MinShare)
            {
                continue;
            }

            program.Dictionary.Remove("DecodeParms");
            program.SetDecodedBytes(subset.Data.Span);
            if (format == SubsetFormat.TrueType)
            {
                program.Dictionary["Length1"] = new PdfInteger(subset.Data.Length);
            }

            if (SameGlyphs(face, users, shown))
            {
                string tag = Tag(glyphs);
                foreach ((PdfDictionary font, _) in users)
                {
                    Rename(font, tag);
                    PdfFont.Forget(font);
                }

                return true;
            }

            program.Dictionary.Remove("Filter");
            program.Dictionary.Remove("Length1");
            if (filter is not null)
            {
                program.Dictionary["Filter"] = filter;
            }

            if (parms is not null)
            {
                program.Dictionary["DecodeParms"] = parms;
            }

            if (length1 is not null)
            {
                program.Dictionary["Length1"] = length1;
            }

            program.SetEncodedBytes(encoded);
            foreach ((PdfDictionary font, _) in users)
            {
                PdfFont.Forget(font);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether every code the fonts show reaches, in the cut program, a glyph with the outline and advance the code's glyph had
    /// in the whole one, and reads as the same text.
    /// </summary>
    private static bool SameGlyphs(Typeface before, List<(PdfDictionary Font, HashSet<int> Codes)> users, List<Dictionary<int, (ushort Glyph, string? Text)>> shown)
    {
        for (int u = 0; u < users.Count; u++)
        {
            PdfDictionary font = users[u].Font;
            PdfFont.Forget(font);
            try
            {
                PdfFont reader = PdfFont.FromDictionary(font);
                Typeface? after = reader.GetTypeface();
                if (after is null || after.UnitsPerEm != before.UnitsPerEm)
                {
                    return false;
                }

                foreach ((int code, (ushort old, string? text)) in shown[u])
                {
                    // Text without a ToUnicode map comes from the program's cmap or names: it must read the same too.
                    ushort glyph = reader.GetGlyphId(code) ?? 0;
                    if ((glyph == 0) != (old == 0) || !Same(before, old, after, glyph) || reader.ToUnicode(code) != text)
                    {
                        return false;
                    }
                }
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or Egami.EgamiException or ArgumentException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Same(Typeface before, ushort old, Typeface after, ushort glyph)
    {
        if (old >= before.GlyphCount || glyph >= after.GlyphCount || before.GetAdvance(old) != after.GetAdvance(glyph))
        {
            return false;
        }

        OutlineText a = new(), b = new();
        before.GetGlyphOutline(old, a);
        after.GetGlyphOutline(glyph, b);
        return a.ToString() == b.ToString();
    }

    /// <summary>A subset tag of six capital letters from the glyphs kept (9.6.4), the same for the same subset.</summary>
    private static string Tag(HashSet<ushort> glyphs)
    {
        byte[] hash = SHA256.HashData([.. glyphs.Order().SelectMany(BitConverter.GetBytes)]);
        return string.Concat(hash.Take(6).Select(b => (char)('A' + (b % 26))));
    }

    /// <summary>Puts the subset tag before the font's name, its descendant's and its descriptor's.</summary>
    /// <summary>Whether a font name already starts with a subset tag: six capital letters and a plus sign (9.6.4).</summary>
    private static bool Tagged(string name) => name.Length > 7 && name[6] == '+' && name.AsSpan(0, 6).ContainsAnyExceptInRange('A', 'Z') is false;

    private static void Rename(PdfDictionary font, string tag)
    {
        PdfDictionary? descendant = font.Get<PdfArray>("DescendantFonts")?.Get(0)?.Resolve() as PdfDictionary;
        foreach (PdfDictionary dictionary in new[] { font, descendant }.OfType<PdfDictionary>())
        {
            if (dictionary.GetName("BaseFont")?.Value is string name && !Tagged(name))
            {
                dictionary["BaseFont"] = PdfName.Get(tag + "+" + name);
            }
        }

        PdfDictionary? descriptor = (descendant ?? font).Get<PdfDictionary>("FontDescriptor");
        if (descriptor?.GetName("FontName")?.Value is string fontName && !Tagged(fontName))
        {
            descriptor["FontName"] = PdfName.Get(tag + "+" + fontName);
        }

        // A glyph list would now name glyphs the program no longer has; both are optional outside PDF/A-1.
        descriptor?.Remove("CIDSet");
        descriptor?.Remove("CharSet");
    }

    /// <summary>
    /// The codes each font shows on every page and annotation appearance, and the fonts to keep whole: in the form's default
    /// resources and widget appearances, in tiling patterns and soft masks, and in content that cannot be read.
    /// </summary>
    private static void Collect(PdfDocument document, Dictionary<PdfDictionary, HashSet<int>> codes, HashSet<PdfDictionary> excluded)
    {
        HashSet<PdfObject> seen = new(ReferenceEqualityComparer.Instance);
        if (document.Catalog.Get<PdfDictionary>("AcroForm")?.Get<PdfDictionary>("DR") is PdfDictionary formResources)
        {
            AllFonts(formResources, excluded, seen, 0);
        }

        foreach (PdfPage page in document.Pages)
        {
            CodeWalker walker = new(codes, excluded);
            try
            {
                walker.Walk(page);
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                AllFonts(page.Resources, excluded, new(ReferenceEqualityComparer.Instance), 0);
            }

            foreach (PdfAnnotation annotation in page.Annotations)
            {
                if (annotation.Dictionary.Get<PdfDictionary>("AP") is not PdfDictionary ap)
                {
                    continue;
                }

                bool widget = annotation.Subtype == "Widget";
                foreach (PdfStream appearance in Appearances(ap))
                {
                    PdfDictionary? resources = appearance.Dictionary.Get<PdfDictionary>("Resources");
                    if (widget)
                    {
                        AllFonts(resources, excluded, new(ReferenceEqualityComparer.Instance), 0);
                        continue;
                    }

                    try
                    {
                        new CodeWalker(codes, excluded).Walk(PdfContent.Read(appearance), resources, PdfMatrix.Identity, document.Options);
                    }
                    catch (PdfException e) when (e is not PdfLimitException)
                    {
                        AllFonts(resources, excluded, new(ReferenceEqualityComparer.Instance), 0);
                    }
                }
            }
        }
    }

    private static IEnumerable<PdfStream> Appearances(PdfDictionary ap)
    {
        foreach (KeyValuePair<PdfName, PdfObject> entry in ap.Entries)
        {
            switch (entry.Value.Resolve())
            {
                case PdfStream stream:
                    yield return stream;
                    break;
                case PdfDictionary states:
                    foreach (PdfStream state in states.Entries.Select(s => s.Value.Resolve()).OfType<PdfStream>())
                    {
                        yield return state;
                    }

                    break;
            }
        }
    }

    /// <summary>Every font reachable from resources, through forms, patterns, soft masks and Type 3 fonts.</summary>
    internal static void AllFonts(PdfDictionary? resources, HashSet<PdfDictionary> fonts, HashSet<PdfObject> seen, int depth)
    {
        if (resources is null || depth > 32 || !seen.Add(resources))
        {
            return;
        }

        foreach (PdfDictionary font in (resources.Get<PdfDictionary>("Font")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfDictionary>())
        {
            if (fonts.Add(font))
            {
                AllFonts(font.Get<PdfDictionary>("Resources"), fonts, seen, depth + 1);
            }
        }

        foreach (string category in new[] { "XObject", "Pattern" })
        {
            foreach (PdfObject value in (resources.Get<PdfDictionary>(category)?.Entries ?? []).Select(e => e.Value.Resolve()))
            {
                if (value is PdfStream stream)
                {
                    AllFonts(stream.Dictionary.Get<PdfDictionary>("Resources"), fonts, seen, depth + 1);
                }
            }
        }

        foreach (PdfDictionary state in (resources.Get<PdfDictionary>("ExtGState")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfDictionary>())
        {
            if (state.Get<PdfDictionary>("SMask")?.Get("G")?.Resolve() is PdfStream group)
            {
                AllFonts(group.Dictionary.Get<PdfDictionary>("Resources"), fonts, seen, depth + 1);
            }
        }
    }

    /// <summary>A walker that notes each glyph's code under its font, and the fonts of patterns and soft masks it meets.</summary>
    private sealed class CodeWalker(Dictionary<PdfDictionary, HashSet<int>> codes, HashSet<PdfDictionary> excluded) : PdfContentWalker
    {
        private readonly HashSet<PdfObject> seen = new(ReferenceEqualityComparer.Instance);

        protected override void OnGlyph(PdfGlyph glyph)
        {
            if (State.Font is not PdfDictionary font)
            {
                return;
            }

            if (!codes.TryGetValue(font, out HashSet<int>? used))
            {
                codes[font] = used = [];
            }

            used.Add(glyph.Code);
        }

        protected override void OnOperation(PdfOperation operation)
        {
            // Patterns and soft masks are drawn where the walk does not go: their fonts stay whole.
            if (Resources is PdfDictionary resources && (resources.ContainsKey("Pattern") || resources.ContainsKey("ExtGState")) && seen.Add(resources))
            {
                foreach (PdfStream pattern in (resources.Get<PdfDictionary>("Pattern")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfStream>())
                {
                    AllFonts(pattern.Dictionary.Get<PdfDictionary>("Resources"), excluded, new(ReferenceEqualityComparer.Instance), 0);
                }

                foreach (PdfDictionary state in (resources.Get<PdfDictionary>("ExtGState")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfDictionary>())
                {
                    if (state.Get<PdfDictionary>("SMask")?.Get("G")?.Resolve() is PdfStream group)
                    {
                        AllFonts(group.Dictionary.Get<PdfDictionary>("Resources"), excluded, new(ReferenceEqualityComparer.Instance), 0);
                    }
                }
            }
        }
    }

    /// <summary>A glyph outline written out as text, to compare two outlines exactly.</summary>
    private sealed class OutlineText : IGlyphOutlineSink
    {
        private readonly StringBuilder text = new();

        public void MoveTo(float x, float y) => text.Append(CultureInfo.InvariantCulture, $"M{x} {y}");

        public void LineTo(float x, float y) => text.Append(CultureInfo.InvariantCulture, $"L{x} {y}");

        public void QuadTo(float cx, float cy, float x, float y) => text.Append(CultureInfo.InvariantCulture, $"Q{cx} {cy} {x} {y}");

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => text.Append(CultureInfo.InvariantCulture, $"C{c1x} {c1y} {c2x} {c2y} {x} {y}");

        public void Close() => text.Append('Z');

        public override string ToString() => text.ToString();
    }
}
