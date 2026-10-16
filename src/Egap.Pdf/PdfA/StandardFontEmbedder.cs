using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egami.Fonts;

using Egap.Pdf.Fonts;

namespace Egap.Pdf.PdfA;

/// <summary>
/// Embeds simple fonts that PDF/A forbids leaving out (ISO 19005-2 6.2.11.4; Phase 5 design Q12). The standard 14:
/// Helvetica, Times and Courier as Liberation subsets (metric-compatible, from Egap.Fonts) written as TrueType fonts with
/// WinAnsi encoding, Symbol and ZapfDingbats as Foxit's compact (CFF) Type 1 programs (BSD-3-Clause, bundled since Phase 4).
/// Other fonts: the same font installed on this machine, or with substitution allowed the bundled font the name suggests,
/// with the PDF's widths written into the subset's metrics so text keeps its place and the program agrees with the widths.
/// The font dictionary is changed in place, so everything using it sees the embedded font.
/// </summary>
internal static class StandardFontEmbedder
{
    /// <summary>The program a font that is not embedded would get, and how.</summary>
    /// <param name="Face">The face embedded.</param>
    /// <param name="Standard">The standard 14 font it stands for, when it is one.</param>
    /// <param name="KeepWidths">Whether the PDF's widths stay, the subset's advances set to them.</param>
    /// <param name="Serif">Whether the descriptor flags the font serif.</param>
    /// <param name="Note">How the report says what was embedded, with {0} for the font's name.</param>
    internal sealed record Plan(Typeface Face, Standard14Font? Standard, bool KeepWidths, bool Serif, string Note)
    {
        /// <summary>Whether the face is Foxit's Symbol or ZapfDingbats, embedded whole as compact Type 1.</summary>
        public bool Symbolic => Standard is { IsSymbolic: true };
    }

    /// <summary>
    /// The program for a simple font that has no program of its own: the standard 14 and the names metric-compatible with
    /// them get Liberation or Foxit's fonts; other fonts the same installed font, or with substitution allowed the bundled
    /// font the name suggests. Null when no program may be used.
    /// </summary>
    public static Plan? PlanFor(PdfDictionary font, PdfAConvertOptions options)
    {
        string? subtype = font.GetName("Subtype")?.Value;
        if (subtype is not ("Type1" or "MMType1" or "TrueType") || IsEmbedded(font))
        {
            return null;
        }

        PdfFont parsed = PdfFont.FromDictionary(font);
        string name = font.GetName("BaseFont")?.Value ?? "(unnamed)";
        if (Standard14Font.Get(name) is Standard14Font standard)
        {
            if (standard.IsSymbolic)
            {
                return Bundled(standard.Name == "Symbol" ? "FoxitSymbol.pfb" : "FoxitDingbats.pfb") is byte[] program
                    ? new Plan(FontProgram.FromFile(program).Typeface, standard, false, false, $"embedded Foxit's {standard.Name} for {{0}}")
                    : null;
            }

            return StandardFace(standard) is Typeface face
                ? new Plan(face, standard, false, standard.Name.StartsWith("Times", StringComparison.Ordinal), $"embedded {face.FullName} for {{0}}")
                : null;
        }

        if (MetricFace(name, parsed.Flags) is Typeface metric)
        {
            return new Plan(metric, null, true, metric.FamilyName.Contains("Serif", StringComparison.Ordinal), $"embedded {metric.FullName}, metric-compatible, for {{0}}");
        }

        if ((parsed.Flags & PdfFontFlags.Symbolic) != 0 && (parsed.Flags & PdfFontFlags.Nonsymbolic) == 0)
        {
            return null;
        }

        SubstituteFont? substitute = (options.UseSystemFonts ? SubstituteFonts.SystemFont(parsed) : null) ?? SubstituteFonts.For(parsed, options.UseSystemFonts);
        bool system = substitute is { Synthesis: FontSynthesis.None } && substitute.Source.StartsWith("system ", StringComparison.Ordinal);
        if (substitute is null || !(system || options.AllowFontSubstitution) || substitute.Program.Typeface is not { Outlines: FontOutlines.TrueType } chosen || UnicodeMap(chosen) is null)
        {
            return null;
        }

        return new Plan(chosen, null, true, (parsed.Flags & PdfFontFlags.Serif) != 0,
            system ? $"embedded the system font {chosen.FullName} for {{0}}" : $"substituted {chosen.FullName} for {{0}}, keeping its widths");
    }

    /// <summary>
    /// Each code's glyph in the planned face, with the glyph name the new encoding gives the code and the text it stands
    /// for. A TrueType face goes by name through Unicode, a code the font leaves unnamed reading as WinAnsi (what Windows
    /// producers meant); a symbol face by name, through its own encoding when the stated one names glyphs it lacks.
    /// </summary>
    public static Dictionary<int, (string Name, string? Text, ushort Glyph)> Codes(PdfDictionary font, Plan plan)
    {
        PdfFont parsed = PdfFont.FromDictionary(font);
        Typeface face = plan.Face;
        Dictionary<int, (string Name, string? Text, ushort Glyph)> codes = [];

        // Readers reach a non-symbolic TrueType font's glyphs through its Unicode cmap, so only that one counts.
        CharacterMap? unicode = plan.Symbolic ? null : UnicodeMap(face);
        for (int code = 0; code < 256; code++)
        {
            if (plan.Symbolic)
            {
                IReadOnlyList<string?> own = plan.Standard!.BuiltInEncoding;
                string? named = parsed.GetGlyphName(code) is string stated && face.TryGetGlyphByName(stated, out ushort statedId) && statedId != 0
                    ? stated
                    : own.Count > code ? own[code] : null;
                if (named is string glyph && glyph != ".notdef" && face.TryGetGlyphByName(glyph, out ushort id) && id != 0)
                {
                    codes[code] = (glyph, GlyphList.ToUnicode(glyph, dingbats: plan.Standard.Name != "Symbol") is { Length: > 0 } text ? text : null, id);
                }

                continue;
            }

            // The font's own name first, then WinAnsi's, which the new encoding is built on.
            foreach (string? candidate in (string?[])[parsed.GetGlyphName(code), WinAnsiEncoding.GlyphName((byte)code)])
            {
                if (candidate is string glyph && glyph != ".notdef" && GlyphList.ToUnicode(glyph) is { Length: > 0 } text
                    && unicode?.GetGlyph(char.ConvertToUtf32(text, 0)) is ushort id && id != 0)
                {
                    codes[code] = (glyph, text, id);
                    break;
                }
            }
        }

        return codes;
    }

    /// <summary>The face's Unicode cmap (Windows Unicode BMP or full, or a Unicode platform table), or null for a symbol font.</summary>
    private static CharacterMap? UnicodeMap(Typeface face)
    {
        return face.CharacterMaps.FirstOrDefault(m => m.PlatformId == 3 && m.EncodingId is 1 or 10) ?? face.CharacterMaps.FirstOrDefault(m => m.PlatformId == 0);
    }

    /// <summary>Embeds the planned program; returns what was embedded, or null when the face shows none of the codes.</summary>
    public static string? TryEmbed(PdfDocument document, PdfDictionary font, PdfAConvertOptions options)
    {
        if (PlanFor(font, options) is not Plan plan)
        {
            return null;
        }

        string name = font.GetName("BaseFont")?.Value ?? "(unnamed)";
        Dictionary<int, (string Name, string? Text, ushort Glyph)> codes = Codes(font, plan);
        if (codes.Count == 0)
        {
            return null;
        }

        if (plan.Symbolic)
        {
            EmbedSymbolic(document, font, plan, codes);
        }
        else
        {
            EmbedTrueType(document, font, plan, codes);
        }

        PdfFont.Forget(font);
        return string.Format(CultureInfo.InvariantCulture, plan.Note, name);
    }

    /// <summary>
    /// The bundled face metric-compatible with a font named as Arial, Helvetica, Times (New Roman) or Courier (New) in a
    /// plain style, however the name is spelled (<c>ArialMS</c>, <c>TimesNewRomanPS-BoldMT</c>, <c>Arial,Italic</c>); null
    /// for any other name, such as Arial Narrow or Helvetica Black, which Liberation does not match.
    /// </summary>
    internal static Typeface? MetricFace(string name, PdfFontFlags flags)
    {
        if (name.Length > 7 && name[6] == '+')
        {
            name = name[7..];
        }

        string letters = new([.. name.Where(char.IsLetter).Select(char.ToLowerInvariant)]);
        foreach ((string family, string alias) in (ReadOnlySpan<(string, string)>)[("timesnewroman", "Times"), ("times", "Times"), ("couriernew", "Courier"), ("courier", "Courier"), ("arial", "Helvetica"), ("helvetica", "Helvetica")])
        {
            if (!letters.StartsWith(family, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = letters[family.Length..];
            bool bold = (flags & PdfFontFlags.ForceBold) != 0, italic = (flags & PdfFontFlags.Italic) != 0;
            bool changed = true;
            while (rest.Length > 0 && changed)
            {
                changed = false;
                foreach (string token in (ReadOnlySpan<string>)["psmt", "mt", "ms", "ps", "bold", "italic", "oblique", "roman", "regular"])
                {
                    if (rest.StartsWith(token, StringComparison.Ordinal))
                    {
                        bold |= token == "bold";
                        italic |= token is "italic" or "oblique";
                        rest = rest[token.Length..];
                        changed = true;
                        break;
                    }
                }
            }

            return rest.Length == 0 ? Egap.Fonts.StandardFonts.Find(alias, new FontStyle(bold ? 700 : 400, 5, italic ? FontSlant.Italic : FontSlant.Upright)) : null;
        }

        return null;
    }

    private static Typeface? StandardFace(Standard14Font standard)
    {
        string name = standard.Name;
        bool bold = name.Contains("Bold", StringComparison.Ordinal);
        bool italic = name.Contains("Oblique", StringComparison.Ordinal) || name.Contains("Italic", StringComparison.Ordinal);
        return Egap.Fonts.StandardFonts.Find(name, new FontStyle(bold ? 700 : 400, 5, italic ? FontSlant.Italic : FontSlant.Upright));
    }

    /// <summary>Whether a font dictionary carries a font program, its descendant's for a composite font.</summary>
    public static bool IsEmbedded(PdfDictionary font)
    {
        PdfDictionary? descriptor = font.Get<PdfDictionary>("FontDescriptor")
            ?? font.Get<PdfArray>("DescendantFonts")?.Get<PdfDictionary>(0)?.Get<PdfDictionary>("FontDescriptor");
        return descriptor is not null && (descriptor.ContainsKey("FontFile") || descriptor.ContainsKey("FontFile2") || descriptor.ContainsKey("FontFile3"));
    }

    /// <summary>
    /// Embeds a TrueType subset for a simple font, with WinAnsi as the base encoding and the codes it names differently as
    /// Differences. With <see cref="Plan.KeepWidths"/> the font's own widths stay and the subset's advances are set to them;
    /// otherwise the widths are the face's.
    /// </summary>
    private static void EmbedTrueType(PdfDocument document, PdfDictionary font, Plan plan, Dictionary<int, (string Name, string? Text, ushort Glyph)> codes)
    {
        Typeface face = plan.Face;
        bool serif = plan.Serif, keepWidths = plan.KeepWidths;
        bool italic = face.Style.Slant != FontSlant.Upright;
        List<ushort> used = [0, .. codes.Values.Select(c => c.Glyph).Distinct().Order()];
        string tag = Tag(used);
        FontSubset subset = FontSubsetter.Subset(face, used, new SubsetOptions { Format = SubsetFormat.TrueType, FontNamePrefix = tag + "+" });
        Font metrics = new(face, 1000);
        Dictionary<int, double> widths = codes.ToDictionary(c => c.Key, c => (double)metrics.GetAdvance(c.Value.Glyph));
        byte[] program = subset.Data.ToArray();
        if (keepWidths && font.Get<PdfArray>("Widths") is PdfArray pdfWidths && font.GetInt("FirstChar") is long first)
        {
            // The subset numbers glyphs densely in ascending order; each takes the width of the first code showing it.
            Dictionary<ushort, double> advances = [];
            foreach ((int code, (_, _, ushort glyph)) in codes.OrderBy(c => c.Key))
            {
                long index = code - first;
                if (index >= 0 && index < pdfWidths.Count && pdfWidths.GetNumber((int)index) is double width)
                {
                    widths[code] = width;
                    advances.TryAdd((ushort)used.IndexOf(glyph), width);
                }
            }

            program = SfntMetrics.SetAdvances(program, advances, face.UnitsPerEm);

            // Every code takes its glyph's advance, so codes sharing a glyph agree and the rest keep the face's.
            foreach ((int code, (_, _, ushort glyph)) in codes)
            {
                widths[code] = advances.TryGetValue((ushort)used.IndexOf(glyph), out double advance) ? advance : metrics.GetAdvance(glyph);
            }
        }

        PdfStream file = new(new PdfDictionary { ["Length1"] = new PdfInteger(program.Length) });
        file.SetDecodedBytes(program);

        PdfArray differences = [];
        int last = -2;
        foreach ((int code, (string glyph, _, _)) in codes.OrderBy(c => c.Key))
        {
            if (WinAnsiEncoding.GlyphName((byte)code) != glyph)
            {
                if (code != last + 1)
                {
                    differences.Add(new PdfInteger(code));
                }

                differences.Add(PdfName.Get(glyph));
                last = code;
            }
        }

        PdfDictionary descriptor = Descriptor(tag + "+" + face.PostScriptName, metrics, used, 32 | (face.IsFixedPitch ? 1 : 0) | (serif ? 2 : 0) | (italic ? 64 : 0));
        descriptor["FontFile2"] = document.Objects.Add(file);
        font["Subtype"] = PdfName.Get("TrueType");
        font["BaseFont"] = PdfName.Get(tag + "+" + face.PostScriptName);
        font["Encoding"] = differences.Count == 0 ? PdfName.Get("WinAnsiEncoding") : new PdfDictionary { ["Type"] = PdfName.Get("Encoding"), ["BaseEncoding"] = PdfName.Get("WinAnsiEncoding"), ["Differences"] = differences };
        font["FontDescriptor"] = document.Objects.Add(descriptor);
        SetWidths(font, widths);
        font["ToUnicode"] = ToUnicode(document, codes.Where(c => c.Value.Text is not null).ToDictionary(c => c.Key, c => c.Value.Text!));
    }

    /// <summary>
    /// Embeds Foxit's Symbol or ZapfDingbats whole, with an encoding naming each code's glyph so readers find the same
    /// glyphs the codes were resolved to.
    /// </summary>
    private static void EmbedSymbolic(PdfDocument document, PdfDictionary font, Plan plan, Dictionary<int, (string Name, string? Text, ushort Glyph)> codes)
    {
        Typeface face = plan.Face;
        Font metrics = new(face, 1000);
        byte[] program = Bundled(plan.Standard!.Name == "Symbol" ? "FoxitSymbol.pfb" : "FoxitDingbats.pfb")!;
        PdfStream file = new(new PdfDictionary { ["Subtype"] = PdfName.Get("Type1C") });
        file.SetDecodedBytes(program);
        PdfDictionary descriptor = Descriptor(face.PostScriptName, metrics, [.. Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g)], 4);
        descriptor["FontFile3"] = document.Objects.Add(file);
        PdfArray differences = [];
        int last = -2;
        foreach ((int code, (string glyph, _, _)) in codes.OrderBy(c => c.Key))
        {
            if (code != last + 1)
            {
                differences.Add(new PdfInteger(code));
            }

            differences.Add(PdfName.Get(glyph));
            last = code;
        }

        font["Subtype"] = PdfName.Get("Type1");
        font["BaseFont"] = PdfName.Get(face.PostScriptName);
        font["Encoding"] = new PdfDictionary { ["Type"] = PdfName.Get("Encoding"), ["Differences"] = differences };
        font["FontDescriptor"] = document.Objects.Add(descriptor);
        SetWidths(font, codes.ToDictionary(c => c.Key, c => (double)metrics.GetAdvance(c.Value.Glyph)));
        if (codes.Any(c => c.Value.Text is not null))
        {
            font["ToUnicode"] = ToUnicode(document, codes.Where(c => c.Value.Text is not null).ToDictionary(c => c.Key, c => c.Value.Text!));
        }
    }

    private static PdfDictionary Descriptor(string name, Font metrics, List<ushort> glyphs, int flags)
    {
        FontExtents extents = metrics.Extents;
        GlyphExtents box = glyphs.Select(g => metrics.GetGlyphExtents(g)).Where(e => !e.IsEmpty).DefaultIfEmpty(new GlyphExtents(0, -200, 1000, 800))
            .Aggregate((a, b) => new GlyphExtents(Math.Min(a.XMin, b.XMin), Math.Min(a.YMin, b.YMin), Math.Max(a.XMax, b.XMax), Math.Max(a.YMax, b.YMax)));
        return new PdfDictionary
        {
            ["Type"] = PdfName.Get("FontDescriptor"),
            ["FontName"] = PdfName.Get(name),
            ["Flags"] = new PdfInteger(flags),
            ["FontBBox"] = new PdfArray { Number(box.XMin), Number(box.YMin), Number(box.XMax), Number(box.YMax) },
            ["ItalicAngle"] = Number(extents.ItalicAngle),
            ["Ascent"] = Number(extents.Ascent),
            ["Descent"] = Number(-Math.Abs(extents.Descent)),
            ["CapHeight"] = Number(extents.CapHeight > 0 ? extents.CapHeight : extents.Ascent),
            ["StemV"] = new PdfInteger(80),
        };
    }

    /// <summary><c>/FirstChar</c>, <c>/LastChar</c> and <c>/Widths</c> over the codes that have glyphs, 0 for the gaps.</summary>
    private static void SetWidths(PdfDictionary font, Dictionary<int, double> widths)
    {
        int first = widths.Keys.Min(), last = widths.Keys.Max();
        PdfArray array = [];
        for (int code = first; code <= last; code++)
        {
            array.Add(Number(widths.TryGetValue(code, out double width) ? width : 0));
        }

        font["FirstChar"] = new PdfInteger(first);
        font["LastChar"] = new PdfInteger(last);
        font["Widths"] = array;
    }

    /// <summary>A ToUnicode CMap (9.10.3) for one-byte codes.</summary>
    internal static PdfReference ToUnicode(PdfDocument document, Dictionary<int, string> texts, int bytes = 1)
    {
        string format = bytes == 1 ? "X2" : "X4";
        StringBuilder cmap = new();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append(CultureInfo.InvariantCulture, $"/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<{0.ToString(format, CultureInfo.InvariantCulture)}> <{(bytes == 1 ? 0xFF : 0xFFFF).ToString(format, CultureInfo.InvariantCulture)}>\nendcodespacerange\n");
        foreach (KeyValuePair<int, string>[] chunk in texts.OrderBy(t => t.Key).Chunk(100))
        {
            cmap.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach ((int code, string text) in chunk)
            {
                cmap.Append(CultureInfo.InvariantCulture, $"<{code.ToString(format, CultureInfo.InvariantCulture)}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))}>\n");
            }

            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        PdfStream stream = new();
        stream.SetDecodedBytes(Encoding.ASCII.GetBytes(cmap.ToString()));
        return document.Objects.Add(stream);
    }

    /// <summary>A bundled font program's bytes: Foxit's symbol fonts are bare CFF despite their file names.</summary>
    private static byte[]? Bundled(string file)
    {
        using Stream? stream = typeof(StandardFontEmbedder).Assembly.GetManifestResourceStream("Egap.Pdf.Fonts.Substitutes." + file);
        if (stream is null)
        {
            return null;
        }

        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The six-letter subset tag (9.6.4), from a hash of the glyphs kept, so the same subset gets the same tag.</summary>
    private static string Tag(List<ushort> glyphs)
    {
        byte[] bytes = new byte[glyphs.Count * 2];
        for (int i = 0; i < glyphs.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), glyphs[i]);
        }

        byte[] hash = SHA256.HashData(bytes);
        return string.Concat(hash.Take(6).Select(b => (char)('A' + (b % 26))));
    }

    private static PdfObject Number(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 1e-9 ? new PdfInteger((long)Math.Round(value)) : new PdfReal(Math.Round(value, 3));
    }
}
