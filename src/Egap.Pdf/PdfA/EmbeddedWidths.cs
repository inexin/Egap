using Egami.Fonts;

namespace Egap.Pdf.PdfA;

/// <summary>
/// The agreement PDF/A asks between a font's widths and its embedded program's advances (ISO 19005-2 6.2.11.5). For an
/// embedded TrueType program, simple or CID-keyed with Identity encoding, the program's advances are set to the PDF's
/// widths, which is how text is placed, so nothing moves on the page.
/// </summary>
internal static class EmbeddedWidths
{
    /// <summary>
    /// The glyphs of the shown <paramref name="codes"/> whose advance differs from the width the font gives their code, by
    /// more than a thousandth of the em; null when two codes showing one glyph give it different widths, which no program
    /// can agree with.
    /// </summary>
    public static Dictionary<ushort, double>? Mismatches(PdfDictionary font, IEnumerable<int> codes)
    {
        PdfFont parsed = PdfFont.FromDictionary(font);
        if (parsed.GetTypeface() is not Typeface face || parsed.Type == PdfFontType.Type3)
        {
            return [];
        }

        Font metrics = new(face, 1000);
        Dictionary<ushort, double> wanted = [];
        foreach (int code in codes)
        {
            if (parsed.GetGlyphId(code) is not ushort glyph || glyph == 0 || glyph >= face.GlyphCount)
            {
                continue;
            }

            double width = parsed.GetWidth(code);
            if (wanted.TryGetValue(glyph, out double other))
            {
                if (Math.Abs(other - width) > 1)
                {
                    return null;
                }

                continue;
            }

            wanted[glyph] = width;
        }

        return wanted.Where(w => Math.Abs(metrics.GetAdvance(w.Key) - w.Value) > 1).ToDictionary(w => w.Key, w => w.Value);
    }

    /// <summary>
    /// How many of the shown codes reach no glyph in the embedded program (or .notdef), which PDF/A forbids (ISO 19005-2
    /// 6.2.11.4.1, 6.2.11.8); null when the program cannot be read.
    /// </summary>
    public static int? Missing(PdfDictionary font, IEnumerable<int> codes)
    {
        PdfFont parsed = PdfFont.FromDictionary(font);
        if (parsed.Type == PdfFontType.Type3 || parsed.GetTypeface() is not Typeface face)
        {
            return null;
        }

        return codes.Count(code => parsed.GetGlyphId(code) is not ushort glyph || glyph == 0 || glyph >= face.GlyphCount);
    }

    /// <summary>
    /// Whether every width can be met in the program's units: an advance is a whole number of font units, so a program with
    /// few units to the em (some have 256) cannot hold a width to within the thousandth of an em PDF/A allows.
    /// </summary>
    public static bool Achievable(PdfDictionary font, Dictionary<ushort, double> advances)
    {
        if (PdfFont.FromDictionary(font).GetTypeface() is not Typeface face)
        {
            return false;
        }

        int units = face.UnitsPerEm;
        return advances.Values.All(w => Math.Abs((Math.Round(w * units / 1000) * 1000 / units) - w) <= 1);
    }

    /// <summary>The font's TrueType program stream, when <see cref="Fix"/> could rewrite it.</summary>
    public static PdfStream? Program(PdfDictionary font)
    {
        return TrueTypeFile(font);
    }

    /// <summary>Whether <see cref="Fix"/> can write advances into the font's program: a TrueType program, simple or Identity CID-keyed.</summary>
    public static bool CanFix(PdfDictionary font)
    {
        try
        {
            return TrueTypeFile(font) is PdfStream file && SfntMetrics.CanSetAdvances(file.GetDecodedBytes());
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return false;
        }
    }

    /// <summary>Writes the advances into the embedded program.</summary>
    public static void Fix(PdfDictionary font, Dictionary<ushort, double> advances)
    {
        PdfFont parsed = PdfFont.FromDictionary(font);
        if (TrueTypeFile(font) is not PdfStream file || parsed.GetTypeface() is not Typeface face)
        {
            return;
        }

        byte[] program = SfntMetrics.SetAdvances(file.GetDecodedBytes(), advances, face.UnitsPerEm);
        file.Dictionary.Remove("DecodeParms");
        file.SetDecodedBytes(program);
        file.Dictionary["Length1"] = new PdfInteger(program.Length);
        PdfFont.Forget(font);
        if (font.Get<PdfArray>("DescendantFonts")?.Get<PdfDictionary>(0) is PdfDictionary descendant)
        {
            PdfFont.Forget(descendant);
        }
    }

    /// <summary>The FontFile2 stream of a simple TrueType font or of a Type 0 font's CIDFontType2 with Identity encoding.</summary>
    private static PdfStream? TrueTypeFile(PdfDictionary font)
    {
        string? subtype = font.GetName("Subtype")?.Value;
        if (subtype == "TrueType")
        {
            return font.Get<PdfDictionary>("FontDescriptor")?.Get<PdfStream>("FontFile2");
        }

        if (subtype == "Type0" && font.GetName("Encoding")?.Value is "Identity-H" or "Identity-V"
            && font.Get<PdfArray>("DescendantFonts")?.Get<PdfDictionary>(0) is PdfDictionary cid && cid.GetName("Subtype")?.Value == "CIDFontType2")
        {
            return cid.Get<PdfDictionary>("FontDescriptor")?.Get<PdfStream>("FontFile2");
        }

        return null;
    }
}
