namespace Egap.Pdf.Conversion;

/// <summary>
/// What a PDF font says about its face (design Q6): its family name with the subset prefix and style suffix removed, and
/// whether it is bold, italic or monospaced, from its name, its flags and its weight, the name winning when it says.
/// </summary>
internal static class FontTraits
{
    /// <summary>What each font says, worked out once: fonts are shared by every glyph set in them.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfFont, Traits> Cache = [];

    private sealed record Traits(string Family, bool Bold, bool Italic, bool Monospace);

    private static Traits Of(PdfFont font)
    {
        return Cache.GetValue(font, f => new Traits(NameFamily(f), Says(f, BoldWords) || f.Flags.HasFlag(PdfFontFlags.ForceBold),
            Says(f, ItalicWords) || f.Flags.HasFlag(PdfFontFlags.Italic) || Math.Abs(f.ItalicAngle) > 1, Says(f, MonoWords) || f.Flags.HasFlag(PdfFontFlags.FixedPitch)));
    }

    private static readonly string[] BoldWords = ["bold", "black", "heavy", "semibold", "demibold", "demi", "extrabold", "ultrabold"];
    private static readonly string[] ItalicWords = ["italic", "oblique", "slanted", "inclined"];
    private static readonly string[] MonoWords = ["mono", "courier", "consola", "code", "typewriter", "fixed"];

    /// <summary>The family: the name without its six-letter subset prefix and its style part ("ABCDEF+Arial-BoldMT" gives "Arial").</summary>
    public static string Family(PdfFont? font)
    {
        return font is null ? "" : Of(font).Family;
    }

    private static string NameFamily(PdfFont? font)
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
        return font is not null && Of(font).Bold;
    }

    public static bool IsItalic(PdfFont? font)
    {
        return font is not null && Of(font).Italic;
    }

    public static bool IsMonospace(PdfFont? font)
    {
        return font is not null && Of(font).Monospace;
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
