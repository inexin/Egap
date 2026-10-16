using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egami.Fonts;

namespace Egap.Pdf.Content;

/// <summary>
/// One font embedded for drawing (Phase 3 design Q15): a Type0 font with Identity-H encoding over a CIDFontType2 (TrueType
/// outlines) or CIDFontType0 (CFF outlines) subset from Egami's subsetter, glyph ids kept, so the codes content shows are the
/// glyph ids. The glyphs, widths and text each glyph stands for are gathered as drawing uses them; the subset, <c>/W</c> and
/// the ToUnicode map are written when the document is saved, and written again when later drawing uses more glyphs.
/// </summary>
internal sealed class EmbeddedFont
{
    private readonly PdfDocument document;
    private readonly Typeface face;
    private readonly Font reference;
    private readonly SortedDictionary<ushort, string?> glyphs = [];
    private readonly PdfDictionary type0;
    private int written = -1;

    private EmbeddedFont(PdfDocument document, Typeface face)
    {
        this.document = document;
        this.face = face;
        reference = new Font(face, 1000);
        type0 = new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type0"),
            ["Encoding"] = PdfName.Get("Identity-H"),
        };
        Reference = document.Objects.Add(type0);
    }

    /// <summary>The Type0 font dictionary, for the resources of whatever shows its glyphs.</summary>
    public PdfReference Reference { get; }

    /// <summary>
    /// Whether a typeface may be embedded: it has outlines, and its licence bits (OS/2 <c>fsType</c>) allow embedding for
    /// print and preview at least, and more than bitmaps.
    /// </summary>
    public static bool CanEmbed(Typeface face)
    {
        FontEmbedding bits = face.Embedding;
        bool restricted = (bits & FontEmbedding.Restricted) != 0 && (bits & (FontEmbedding.PreviewAndPrint | FontEmbedding.Editable)) == 0;
        return face.Outlines != FontOutlines.None && !restricted && (bits & FontEmbedding.BitmapOnly) == 0;
    }

    /// <summary>The document's embedded font for a typeface, made the first time it is used.</summary>
    public static EmbeddedFont For(PdfDocument document, Typeface face)
    {
        return document.WrittenResources.ForInstance(face, () =>
        {
            EmbeddedFont font = new(document, face);
            document.WrittenResources.OnFinish(font.Write);
            return font;
        });
    }

    /// <summary>The glyph's advance in thousandths of the em, as <c>/W</c> gives it.</summary>
    public double Width(ushort glyph)
    {
        return reference.GetAdvance(glyph);
    }

    /// <summary>
    /// Records that a glyph is shown standing for <paramref name="text"/>; <see langword="false"/> when it already stood for
    /// other text, which ToUnicode cannot say, so the caller marks the run with its actual text.
    /// </summary>
    public bool Use(ushort glyph, string? text)
    {
        if (glyphs.TryGetValue(glyph, out string? known))
        {
            if (text is null || known == text)
            {
                return true;
            }

            if (known is null)
            {
                glyphs[glyph] = text;
                return true;
            }

            return false;
        }

        glyphs[glyph] = text;
        return true;
    }

    /// <summary>Writes the subset, its descriptor, widths and ToUnicode map, when glyphs were added since they were last written.</summary>
    public void Write()
    {
        if (written == glyphs.Count)
        {
            return;
        }

        written = glyphs.Count;
        bool cff = face.Outlines == FontOutlines.Cff;
        bool whole = (face.Embedding & FontEmbedding.NoSubsetting) != 0;
        List<ushort> used = whole ? [.. Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g)] : [0, .. glyphs.Keys];
        string tag = Tag(used);
        string name = tag + "+" + PostScriptName(face.PostScriptName);
        FontSubset subset = FontSubsetter.Subset(face, used, new SubsetOptions
        {
            Format = cff ? SubsetFormat.BareCff : SubsetFormat.TrueType,
            RetainGlyphIds = true,
            FontNamePrefix = tag + "+",
        });

        PdfStream file = new(new PdfDictionary());
        file.SetDecodedBytes(subset.Data.Span);
        if (cff)
        {
            file.Dictionary["Subtype"] = PdfName.Get("CIDFontType0C");
        }
        else
        {
            file.Dictionary["Length1"] = new PdfInteger(subset.Data.Length);
        }

        FontExtents extents = reference.Extents;
        GlyphExtents box = used.Select(g => reference.GetGlyphExtents(g)).Where(e => !e.IsEmpty).DefaultIfEmpty(new GlyphExtents(0, -200, 1000, 800))
            .Aggregate((a, b) => new GlyphExtents(Math.Min(a.XMin, b.XMin), Math.Min(a.YMin, b.YMin), Math.Max(a.XMax, b.XMax), Math.Max(a.YMax, b.YMax)));
        PdfDictionary descriptor = new()
        {
            ["Type"] = PdfName.Get("FontDescriptor"),
            ["FontName"] = PdfName.Get(name),
            ["Flags"] = new PdfInteger(4 | (face.IsFixedPitch ? 1 : 0) | (extents.ItalicAngle != 0 ? 64 : 0)),
            ["FontBBox"] = new PdfArray { Number(box.XMin), Number(box.YMin), Number(box.XMax), Number(box.YMax) },
            ["ItalicAngle"] = Number(extents.ItalicAngle),
            ["Ascent"] = Number(extents.Ascent),
            ["Descent"] = Number(-Math.Abs(extents.Descent)),
            ["CapHeight"] = Number(extents.CapHeight > 0 ? extents.CapHeight : extents.Ascent),
            ["StemV"] = new PdfInteger(80),
            [cff ? "FontFile3" : "FontFile2"] = document.Objects.Add(file),
        };
        if (PdfA.PdfAWriter.Part(document.CreatedConformance) == 1)
        {
            // PDF/A-1 asks a subset to list its CIDs (ISO 19005-1 6.3.5): one bit each, the high bit first.
            byte[] bits = new byte[(used.Max() / 8) + 1];
            foreach (ushort glyph in used)
            {
                bits[glyph / 8] |= (byte)(0x80 >> (glyph % 8));
            }

            PdfStream set = new();
            set.SetDecodedBytes(bits);
            descriptor["CIDSet"] = document.Objects.Add(set);
        }
        PdfDictionary cidFont = new()
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get(cff ? "CIDFontType0" : "CIDFontType2"),
            ["BaseFont"] = PdfName.Get(name),
            ["CIDSystemInfo"] = new PdfDictionary
            {
                ["Registry"] = PdfString.FromText("Adobe"),
                ["Ordering"] = PdfString.FromText("Identity"),
                ["Supplement"] = new PdfInteger(0),
            },
            ["FontDescriptor"] = document.Objects.Add(descriptor),
        };
        (double defaultWidth, PdfArray widths) = Widths(used);
        cidFont["DW"] = Number(defaultWidth);
        if (widths.Count > 0)
        {
            cidFont["W"] = widths;
        }

        if (!cff)
        {
            cidFont["CIDToGIDMap"] = PdfName.Get("Identity");
        }

        type0["BaseFont"] = PdfName.Get(name);
        type0["DescendantFonts"] = new PdfArray { document.Objects.Add(cidFont) };
        PdfStream toUnicode = new();
        toUnicode.SetDecodedBytes(ToUnicode());
        type0["ToUnicode"] = document.Objects.Add(toUnicode);

        // A render before this write read the font without its new glyphs; the next one reads it again.
        PdfFont.Forget(type0);
    }

    /// <summary>
    /// <c>/DW</c> and <c>/W</c> (9.7.4.3): the most common width as the default, and the others as runs of consecutive glyphs
    /// with their widths listed.
    /// </summary>
    private (double Default, PdfArray W) Widths(List<ushort> used)
    {
        Dictionary<ushort, double> widths = used.Distinct().ToDictionary(g => g, g => Math.Round(Width(g), 2));
        double common = widths.Values.GroupBy(w => w).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        PdfArray w = [];
        ushort[] sorted = [.. widths.Where(p => p.Value != common).Select(p => p.Key).Order()];
        for (int i = 0; i < sorted.Length;)
        {
            int start = i;
            while (i + 1 < sorted.Length && sorted[i + 1] == sorted[i] + 1)
            {
                i++;
            }

            w.Add(new PdfInteger(sorted[start]));
            w.Add(new PdfArray(sorted[start..(i + 1)].Select(g => (PdfObject)Number(widths[g]))));
            i++;
        }

        return (common, w);
    }

    /// <summary>The ToUnicode CMap (9.10.3): each glyph that stood for text, as two-byte codes and UTF-16BE.</summary>
    private byte[] ToUnicode()
    {
        StringBuilder cmap = new();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        cmap.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        List<(ushort Glyph, string Text)> mapped = [.. glyphs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => (p.Key, p.Value!))];
        foreach ((ushort Glyph, string Text)[] block in mapped.Chunk(100))
        {
            cmap.Append(CultureInfo.InvariantCulture, $"{block.Length} beginbfchar\n");
            foreach ((ushort glyph, string text) in block)
            {
                cmap.Append(CultureInfo.InvariantCulture, $"<{glyph:X4}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))}>\n");
            }

            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(cmap.ToString());
    }

    /// <summary>The six capital letters that tag a subset (9.6.4), from the glyphs it holds, so the same subset gets the same tag.</summary>
    private static string Tag(List<ushort> used)
    {
        byte[] hash = SHA256.HashData(MemoryMarshalBytes(used));
        return string.Concat(hash.Take(6).Select(b => (char)('A' + (b % 26))));
    }

    private static byte[] MemoryMarshalBytes(List<ushort> values)
    {
        byte[] bytes = new byte[values.Count * 2];
        for (int i = 0; i < values.Count; i++)
        {
            bytes[i * 2] = (byte)(values[i] >> 8);
            bytes[(i * 2) + 1] = (byte)values[i];
        }

        return bytes;
    }

    /// <summary>A PostScript name with what a PDF name or font name should not hold taken out.</summary>
    private static string PostScriptName(string name)
    {
        string clean = new([.. name.Where(c => c is > ' ' and < '\x7F' and not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%'))]);
        return clean.Length > 0 ? clean : "Font";
    }

    private static PdfObject Number(double value)
    {
        double rounded = Math.Round(value, 2);
        return rounded == Math.Floor(rounded) && Math.Abs(rounded) < 1e9 ? new PdfInteger((long)rounded) : new PdfReal(rounded);
    }
}
