using System.Text;

using Egami.Fonts;

namespace Egap.Pdf.Ocr;

/// <summary>
/// The glyphless font of the OCR layer (Phase 5 design Q16), built by Egap: a Type 0 font with Identity-H encoding whose
/// codes are UTF-16 code units, every one mapped (<c>/CIDToGIDMap</c>) to a single glyph half an em wide, and a ToUnicode
/// map giving each code back as itself. The program is Liberation Sans cut down to its .notdef and its capital I, the I's
/// advance set to the half em the widths say; the layer shows it in render mode 3, so nothing is drawn. It is embedded
/// whole and satisfies PDF/A's font rules. One font per document.
/// </summary>
internal static class GlyphlessFont
{
    private static readonly object Key = new();

    public static PdfReference For(PdfDocument document)
    {
        return document.WrittenResources.ForInstance(Key, () => new Holder(Build(document))).Reference;
    }

    private sealed class Holder(PdfReference reference)
    {
        public PdfReference Reference { get; } = reference;
    }

    private static PdfReference Build(PdfDocument document)
    {
        Typeface face = global::Egami.Fonts.Substitutes.SubstituteFonts.Find("Helvetica", FontStyle.Normal) ?? throw new PdfException("The bundled fonts are missing.");
        // Glyph 1 has an outline, never drawn in render mode 3, so readers that size text by its outline (PDFium) still see
        // rotated words, which they drop when the glyph has no width or height at all.
        ushort mark = face.GetGlyph('I');
        FontSubset subset = FontSubsetter.Subset(face, [0, mark], new SubsetOptions { Format = SubsetFormat.TrueType, KeepCmap = false });
        byte[] program = PdfA.SfntMetrics.SetAdvances(subset.Data.ToArray(), new Dictionary<ushort, double> { [0] = 500, [1] = 500 }, face.UnitsPerEm);
        PdfStream file = new(new PdfDictionary { ["Length1"] = new PdfInteger(program.Length) });
        file.SetDecodedBytes(program);

        // Every CID to glyph 1: 65536 two-byte entries.
        byte[] map = new byte[65536 * 2];
        for (int i = 1; i < map.Length; i += 2)
        {
            map[i] = 1;
        }

        PdfStream cidToGid = new();
        cidToGid.SetDecodedBytes(map);
        PdfDictionary descriptor = new()
        {
            ["Type"] = PdfName.Get("FontDescriptor"),
            ["FontName"] = PdfName.Get("GlyphLessFont"),
            ["Flags"] = new PdfInteger(4),
            ["FontBBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(500), new PdfInteger(1000) },
            ["ItalicAngle"] = new PdfInteger(0),
            ["Ascent"] = new PdfInteger(1000),
            // Not 0, which text extractors take as "not given" and replace with a guess below the baseline.
            ["Descent"] = new PdfInteger(-1),
            ["CapHeight"] = new PdfInteger(1000),
            ["StemV"] = new PdfInteger(80),
            ["FontFile2"] = document.Objects.Add(file),
        };
        PdfDictionary cidFont = new()
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("CIDFontType2"),
            ["BaseFont"] = PdfName.Get("GlyphLessFont"),
            ["CIDSystemInfo"] = new PdfDictionary
            {
                ["Registry"] = PdfString.FromText("Adobe"),
                ["Ordering"] = PdfString.FromText("Identity"),
                ["Supplement"] = new PdfInteger(0),
            },
            ["FontDescriptor"] = document.Objects.Add(descriptor),
            ["DW"] = new PdfInteger(500),
            ["CIDToGIDMap"] = document.Objects.Add(cidToGid),
        };
        PdfStream toUnicode = new();
        toUnicode.SetDecodedBytes(Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n"
            + "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n"
            + "2 beginbfrange\n<0000> <D7FF> <0000>\n<E000> <FFFF> <E000>\nendbfrange\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n"));
        return document.Objects.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type0"),
            ["BaseFont"] = PdfName.Get("GlyphLessFont"),
            ["Encoding"] = PdfName.Get("Identity-H"),
            ["DescendantFonts"] = new PdfArray { document.Objects.Add(cidFont) },
            ["ToUnicode"] = document.Objects.Add(toUnicode),
        });
    }
}
