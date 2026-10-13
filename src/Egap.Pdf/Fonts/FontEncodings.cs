namespace Egap.Pdf.Fonts;

/// <summary>
/// The base encodings of simple fonts (Annex D): StandardEncoding (read from the standard 14 metrics, whose Latin fonts use
/// it), WinAnsiEncoding, MacRomanEncoding (with the symbols of Mac OS Roman that Annex D leaves out, as pdf.js keeps them),
/// and the built-in encodings of Symbol and ZapfDingbats. Each is 256 glyph names by code, null where a code has none.
/// </summary>
internal static class FontEncodings
{
    private static readonly Lazy<string?[]> StandardTable = new(() => [.. Standard14Font.Get("Helvetica")!.BuiltInEncoding]);
    private static readonly Lazy<string?[]> WinAnsiTable = new(() => [.. Enumerable.Range(0, 256).Select(c => WinAnsiEncoding.GlyphName((byte)c))]);
    private static readonly Lazy<string?[]> MacRomanTable = new(BuildMacRoman);
    private static readonly Lazy<string?[]> SymbolTable = new(() => [.. Standard14Font.Get("Symbol")!.BuiltInEncoding]);
    private static readonly Lazy<string?[]> DingbatsTable = new(() => [.. Standard14Font.Get("ZapfDingbats")!.BuiltInEncoding]);

    /// <summary>Mac OS Roman above 0x7F: each code's glyph name and the character it stands for.</summary>
    private static readonly (string Name, int Char)[] MacUpper =
    [
        ("Adieresis", 0xC4), ("Aring", 0xC5), ("Ccedilla", 0xC7), ("Eacute", 0xC9), ("Ntilde", 0xD1), ("Odieresis", 0xD6), ("Udieresis", 0xDC),
        ("aacute", 0xE1), ("agrave", 0xE0), ("acircumflex", 0xE2), ("adieresis", 0xE4), ("atilde", 0xE3), ("aring", 0xE5), ("ccedilla", 0xE7),
        ("eacute", 0xE9), ("egrave", 0xE8), ("ecircumflex", 0xEA), ("edieresis", 0xEB), ("iacute", 0xED), ("igrave", 0xEC), ("icircumflex", 0xEE),
        ("idieresis", 0xEF), ("ntilde", 0xF1), ("oacute", 0xF3), ("ograve", 0xF2), ("ocircumflex", 0xF4), ("odieresis", 0xF6), ("otilde", 0xF5),
        ("uacute", 0xFA), ("ugrave", 0xF9), ("ucircumflex", 0xFB), ("udieresis", 0xFC), ("dagger", 0x2020), ("degree", 0xB0), ("cent", 0xA2),
        ("sterling", 0xA3), ("section", 0xA7), ("bullet", 0x2022), ("paragraph", 0xB6), ("germandbls", 0xDF), ("registered", 0xAE),
        ("copyright", 0xA9), ("trademark", 0x2122), ("acute", 0xB4), ("dieresis", 0xA8), ("notequal", 0x2260), ("AE", 0xC6), ("Oslash", 0xD8),
        ("infinity", 0x221E), ("plusminus", 0xB1), ("lessequal", 0x2264), ("greaterequal", 0x2265), ("yen", 0xA5), ("mu", 0xB5),
        ("partialdiff", 0x2202), ("summation", 0x2211), ("product", 0x220F), ("pi", 0x3C0), ("integral", 0x222B), ("ordfeminine", 0xAA),
        ("ordmasculine", 0xBA), ("Omega", 0x3A9), ("ae", 0xE6), ("oslash", 0xF8), ("questiondown", 0xBF), ("exclamdown", 0xA1),
        ("logicalnot", 0xAC), ("radical", 0x221A), ("florin", 0x192), ("approxequal", 0x2248), ("Delta", 0x2206), ("guillemotleft", 0xAB),
        ("guillemotright", 0xBB), ("ellipsis", 0x2026), ("space", 0x20), ("Agrave", 0xC0), ("Atilde", 0xC3), ("Otilde", 0xD5), ("OE", 0x152),
        ("oe", 0x153), ("endash", 0x2013), ("emdash", 0x2014), ("quotedblleft", 0x201C), ("quotedblright", 0x201D), ("quoteleft", 0x2018),
        ("quoteright", 0x2019), ("divide", 0xF7), ("lozenge", 0x25CA), ("ydieresis", 0xFF), ("Ydieresis", 0x178), ("fraction", 0x2044),
        ("currency", 0xA4), ("guilsinglleft", 0x2039), ("guilsinglright", 0x203A), ("fi", 0xFB01), ("fl", 0xFB02), ("daggerdbl", 0x2021),
        ("periodcentered", 0xB7), ("quotesinglbase", 0x201A), ("quotedblbase", 0x201E), ("perthousand", 0x2030), ("Acircumflex", 0xC2),
        ("Ecircumflex", 0xCA), ("Aacute", 0xC1), ("Edieresis", 0xCB), ("Egrave", 0xC8), ("Iacute", 0xCD), ("Icircumflex", 0xCE),
        ("Idieresis", 0xCF), ("Igrave", 0xCC), ("Oacute", 0xD3), ("Ocircumflex", 0xD4), ("apple", 0xF8FF), ("Ograve", 0xD2), ("Uacute", 0xDA),
        ("Ucircumflex", 0xDB), ("Ugrave", 0xD9), ("dotlessi", 0x131), ("circumflex", 0x2C6), ("tilde", 0x2DC), ("macron", 0xAF), ("breve", 0x2D8),
        ("dotaccent", 0x2D9), ("ring", 0x2DA), ("cedilla", 0xB8), ("hungarumlaut", 0x2DD), ("ogonek", 0x2DB), ("caron", 0x2C7),
    ];

    /// <summary>A base encoding by its PDF name, or null for a name PDF does not define (MacExpertEncoding is not bundled).</summary>
    public static string?[]? ForName(string? name)
    {
        return name switch
        {
            "StandardEncoding" => StandardTable.Value,
            "WinAnsiEncoding" => WinAnsiTable.Value,
            "MacRomanEncoding" => MacRomanTable.Value,
            _ => null,
        };
    }

    public static string?[] Standard => StandardTable.Value;

    public static string?[] Symbol => SymbolTable.Value;

    public static string?[] ZapfDingbats => DingbatsTable.Value;

    /// <summary>The glyph names of WinAnsi and MacRoman with the characters they stand for, for names without the glyph list.</summary>
    public static IEnumerable<(string Name, char Char)> LatinNames()
    {
        for (int code = 0x20; code < 0x7F; code++)
        {
            yield return (WinAnsiEncoding.GlyphName((byte)code)!, (char)code);
        }

        for (int code = 0x80; code <= 0xFF; code++)
        {
            if (WinAnsiEncoding.GlyphName((byte)code) is string name && WinAnsiEncoding.Decode((byte)code) is char c)
            {
                yield return (name, c);
            }
        }

        foreach ((string name, int c) in MacUpper)
        {
            yield return (name, (char)c);
        }
    }

    private static string?[] BuildMacRoman()
    {
        string?[] table = new string?[256];
        for (int code = 0x20; code < 0x7F; code++)
        {
            table[code] = WinAnsiEncoding.GlyphName((byte)code);
        }

        for (int i = 0; i < MacUpper.Length; i++)
        {
            table[0x80 + i] = MacUpper[i].Name;
        }

        return table;
    }
}
