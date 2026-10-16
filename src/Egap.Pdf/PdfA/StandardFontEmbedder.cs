using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egami.Fonts;

using Egap.Pdf.Fonts;

namespace Egap.Pdf.PdfA;

/// <summary>
/// Embeds the standard 14 fonts, which PDF/A forbids leaving out (ISO 19005-2 6.2.11.4): Helvetica, Times and Courier as
/// Liberation subsets (metric-compatible, from Egap.Fonts) written as TrueType fonts with WinAnsi encoding, and Symbol and
/// ZapfDingbats as Foxit's compact (CFF) Type 1 programs (BSD-3-Clause, bundled since Phase 4). The font dictionary is changed in place,
/// so everything using it sees the embedded font; widths come from the embedded program, as PDF/A asks.
/// </summary>
internal static class StandardFontEmbedder
{
    /// <summary>
    /// Embeds a simple font that has no program of its own and is one of the standard 14 (or an alias such as Arial).
    /// False, leaving it as it was, for any other font.
    /// </summary>
    public static bool TryEmbed(PdfDocument document, PdfDictionary font)
    {
        string? subtype = font.GetName("Subtype")?.Value;
        if (subtype is not ("Type1" or "MMType1" or "TrueType") || IsEmbedded(font))
        {
            return false;
        }

        PdfFont parsed = PdfFont.FromDictionary(font);
        if (Standard14Font.Get(font.GetName("BaseFont")?.Value) is not Standard14Font standard)
        {
            return false;
        }

        bool done = standard.IsSymbolic ? EmbedSymbolic(document, font, parsed, standard) : EmbedTrueType(document, font, parsed, standard);
        if (done)
        {
            PdfFont.Forget(font);
        }

        return done;
    }

    /// <summary>Whether a font dictionary carries a font program, its descendant's for a composite font.</summary>
    public static bool IsEmbedded(PdfDictionary font)
    {
        PdfDictionary? descriptor = font.Get<PdfDictionary>("FontDescriptor")
            ?? font.Get<PdfArray>("DescendantFonts")?.Get<PdfDictionary>(0)?.Get<PdfDictionary>("FontDescriptor");
        return descriptor is not null && (descriptor.ContainsKey("FontFile") || descriptor.ContainsKey("FontFile2") || descriptor.ContainsKey("FontFile3"));
    }

    private static bool EmbedTrueType(PdfDocument document, PdfDictionary font, PdfFont parsed, Standard14Font standard)
    {
        string name = standard.Name;
        bool bold = name.Contains("Bold", StringComparison.Ordinal);
        bool italic = name.Contains("Oblique", StringComparison.Ordinal) || name.Contains("Italic", StringComparison.Ordinal);
        if (Egap.Fonts.StandardFonts.Find(name, new FontStyle(bold ? 700 : 400, 5, italic ? FontSlant.Italic : FontSlant.Upright)) is not Typeface face)
        {
            return false;
        }

        // Each code's glyph by its name's Unicode value; WinAnsi as the base, the codes it names differently as Differences.
        Dictionary<int, (string Name, string Text, ushort Glyph)> codes = [];
        for (int code = 0; code < 256; code++)
        {
            if (parsed.GetGlyphName(code) is string glyph && glyph != ".notdef" && GlyphList.ToUnicode(glyph) is { Length: > 0 } text
                && face.TryGetGlyph(char.ConvertToUtf32(text, 0), out ushort id) && id != 0)
            {
                codes[code] = (glyph, text, id);
            }
        }

        if (codes.Count == 0)
        {
            return false;
        }

        List<ushort> used = [0, .. codes.Values.Select(c => c.Glyph).Distinct().Order()];
        string tag = Tag(used);
        FontSubset subset = FontSubsetter.Subset(face, used, new SubsetOptions { Format = SubsetFormat.TrueType, FontNamePrefix = tag + "+" });
        PdfStream file = new(new PdfDictionary { ["Length1"] = new PdfInteger(subset.Data.Length) });
        file.SetDecodedBytes(subset.Data.Span);

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

        Font metrics = new(face, 1000);
        PdfDictionary descriptor = Descriptor(tag + "+" + face.PostScriptName, metrics, used, 32 | (face.IsFixedPitch ? 1 : 0) | (name.StartsWith("Times", StringComparison.Ordinal) ? 2 : 0) | (italic ? 64 : 0));
        descriptor["FontFile2"] = document.Objects.Add(file);
        font["Subtype"] = PdfName.Get("TrueType");
        font["BaseFont"] = PdfName.Get(tag + "+" + face.PostScriptName);
        font["Encoding"] = differences.Count == 0 ? PdfName.Get("WinAnsiEncoding") : new PdfDictionary { ["Type"] = PdfName.Get("Encoding"), ["BaseEncoding"] = PdfName.Get("WinAnsiEncoding"), ["Differences"] = differences };
        font["FontDescriptor"] = document.Objects.Add(descriptor);
        SetWidths(font, codes.ToDictionary(c => c.Key, c => (double)metrics.GetAdvance(c.Value.Glyph)));
        font["ToUnicode"] = ToUnicode(document, codes.ToDictionary(c => c.Key, c => c.Value.Text));
        return true;
    }

    private static bool EmbedSymbolic(PdfDocument document, PdfDictionary font, PdfFont parsed, Standard14Font standard)
    {
        string resource = standard.Name == "Symbol" ? "FoxitSymbol.pfb" : "FoxitDingbats.pfb";
        if (Bundled(resource) is not byte[] program)
        {
            return false;
        }

        Typeface face = FontProgram.FromFile(program).Typeface;
        Font metrics = new(face, 1000);
        Dictionary<int, double> widths = [];
        Dictionary<int, string> texts = [];
        for (int code = 0; code < 256; code++)
        {
            if (parsed.GetGlyphName(code) is string glyph && glyph != ".notdef" && face.TryGetGlyphByName(glyph, out ushort id) && id != 0)
            {
                widths[code] = metrics.GetAdvance(id);
                if (GlyphList.ToUnicode(glyph, dingbats: standard.Name != "Symbol") is { Length: > 0 } text)
                {
                    texts[code] = text;
                }
            }
        }

        if (widths.Count == 0)
        {
            return false;
        }

        PdfStream file = new(new PdfDictionary { ["Subtype"] = PdfName.Get("Type1C") });
        file.SetDecodedBytes(program);
        PdfDictionary descriptor = Descriptor(face.PostScriptName, metrics, [.. Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g)], 4);
        descriptor["FontFile3"] = document.Objects.Add(file);
        font["Subtype"] = PdfName.Get("Type1");
        font["BaseFont"] = PdfName.Get(face.PostScriptName);
        font["FontDescriptor"] = document.Objects.Add(descriptor);
        SetWidths(font, widths);
        if (texts.Count > 0)
        {
            font["ToUnicode"] = ToUnicode(document, texts);
        }

        return true;
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
    private static PdfReference ToUnicode(PdfDocument document, Dictionary<int, string> texts)
    {
        StringBuilder cmap = new();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n");
        foreach (KeyValuePair<int, string>[] chunk in texts.OrderBy(t => t.Key).Chunk(100))
        {
            cmap.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach ((int code, string text) in chunk)
            {
                cmap.Append(CultureInfo.InvariantCulture, $"<{code:X2}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))}>\n");
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
