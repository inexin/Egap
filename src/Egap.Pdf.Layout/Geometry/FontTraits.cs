namespace Egap.Pdf.Layout;

/// <summary>
/// What a PDF font says about its face (design Q6): its family name with the subset prefix and style suffix removed, and
/// whether it is bold, italic or monospaced, from its name, its flags and its weight, the name winning when it says.
/// </summary>
internal static class FontTraits
{
    private static readonly string[] BoldWords = ["bold", "black", "heavy", "semibold", "demibold", "demi", "extrabold", "ultrabold"];
    private static readonly string[] ItalicWords = ["italic", "oblique", "slanted", "inclined"];
    private static readonly string[] MonoWords = ["mono", "courier", "consola", "code", "typewriter", "fixed"];

    /// <summary>The family: the name without its six-letter subset prefix and its style part ("ABCDEF+Arial-BoldMT" gives "Arial").</summary>
    public static string Family(PdfFont? font)
    {
        string name = font?.BaseFont ?? font?.Name ?? "";
        if (name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper))
        {
            name = name[7..];
        }

        int cut = name.IndexOfAny(['-', ',']);
        if (cut > 0)
        {
            name = name[..cut];
        }

        foreach (string suffix in (string[])["MT", "PS", "PSMT"])
        {
            if (name.Length > suffix.Length + 2 && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
            }
        }

        return name;
    }

    public static bool IsBold(PdfFont? font)
    {
        return font is not null && (Says(font, BoldWords) || font.Flags.HasFlag(PdfFontFlags.ForceBold));
    }

    public static bool IsItalic(PdfFont? font)
    {
        return font is not null && (Says(font, ItalicWords) || font.Flags.HasFlag(PdfFontFlags.Italic) || Math.Abs(font.ItalicAngle) > 1);
    }

    public static bool IsMonospace(PdfFont? font)
    {
        return font is not null && (Says(font, MonoWords) || font.Flags.HasFlag(PdfFontFlags.FixedPitch));
    }

    /// <summary>Whether a glyph is drawn bold: its font is, or it is filled and stroked (render mode 2), as synthetic bold is.</summary>
    public static bool IsBold(PageGlyph glyph)
    {
        return glyph.RenderMode == 2 || IsBold(glyph.Font);
    }

    private static bool Says(PdfFont font, string[] words)
    {
        string name = (font.BaseFont ?? font.Name ?? "").ToLowerInvariant();
        int plus = name.IndexOf('+', StringComparison.Ordinal);
        name = plus == 6 ? name[7..] : name;
        return words.Any(w => name.Contains(w, StringComparison.Ordinal));
    }
}
