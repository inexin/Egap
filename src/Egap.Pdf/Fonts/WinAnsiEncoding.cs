namespace Egap.Pdf.Fonts;

/// <summary>
/// WinAnsiEncoding (Annex D.2): Windows code page 1252 as PDF defines it, with each code's glyph name. Codes the code page
/// leaves unused above 0x7E show the bullet, as Annex D says; below 0x20 nothing is encoded.
/// </summary>
internal static class WinAnsiEncoding
{
    /// <summary>The glyph name for each code from 0x20, or null.</summary>
    private static readonly string?[] Names = Build();

    private static readonly Dictionary<char, byte> FromChar = BuildReverse();

    /// <summary>The glyph name for a code, or null when the code encodes nothing.</summary>
    public static string? GlyphName(byte code)
    {
        return code < 0x20 ? null : Names[code - 0x20];
    }

    /// <summary>Encodes text, or returns null when a character has no WinAnsi code.</summary>
    public static byte[]? TryEncode(string text)
    {
        byte[] bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!FromChar.TryGetValue(text[i], out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    /// <summary>The character a code stands for, or null for a code that encodes nothing or only the stand-in bullet.</summary>
    public static char? Decode(byte code)
    {
        foreach (KeyValuePair<char, byte> entry in FromChar)
        {
            if (entry.Value == code)
            {
                return entry.Key;
            }
        }

        return null;
    }

    /// <summary>The code for a character, or null.</summary>
    public static byte? Encode(char c)
    {
        return FromChar.TryGetValue(c, out byte code) ? code : null;
    }

    private static string?[] Build()
    {
        string[] ascii =
        [
            "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quotesingle", "parenleft", "parenright",
            "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four", "five", "six", "seven",
            "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at",
        ];
        List<string?> names = [.. ascii];
        for (char c = 'A'; c <= 'Z'; c++)
        {
            names.Add(c.ToString());
        }

        names.AddRange(["bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "grave"]);
        for (char c = 'a'; c <= 'z'; c++)
        {
            names.Add(c.ToString());
        }

        names.AddRange(["braceleft", "bar", "braceright", "asciitilde", "bullet"]);
        names.AddRange(
        [
            "Euro", "bullet", "quotesinglbase", "florin", "quotedblbase", "ellipsis", "dagger", "daggerdbl", "circumflex", "perthousand",
            "Scaron", "guilsinglleft", "OE", "bullet", "Zcaron", "bullet", "bullet", "quoteleft", "quoteright", "quotedblleft",
            "quotedblright", "bullet", "endash", "emdash", "tilde", "trademark", "scaron", "guilsinglright", "oe", "bullet", "zcaron",
            "Ydieresis", "space", "exclamdown", "cent", "sterling", "currency", "yen", "brokenbar", "section", "dieresis", "copyright",
            "ordfeminine", "guillemotleft", "logicalnot", "hyphen", "registered", "macron", "degree", "plusminus", "twosuperior",
            "threesuperior", "acute", "mu", "paragraph", "periodcentered", "cedilla", "onesuperior", "ordmasculine", "guillemotright",
            "onequarter", "onehalf", "threequarters", "questiondown", "Agrave", "Aacute", "Acircumflex", "Atilde", "Adieresis", "Aring",
            "AE", "Ccedilla", "Egrave", "Eacute", "Ecircumflex", "Edieresis", "Igrave", "Iacute", "Icircumflex", "Idieresis", "Eth",
            "Ntilde", "Ograve", "Oacute", "Ocircumflex", "Otilde", "Odieresis", "multiply", "Oslash", "Ugrave", "Uacute", "Ucircumflex",
            "Udieresis", "Yacute", "Thorn", "germandbls", "agrave", "aacute", "acircumflex", "atilde", "adieresis", "aring", "ae",
            "ccedilla", "egrave", "eacute", "ecircumflex", "edieresis", "igrave", "iacute", "icircumflex", "idieresis", "eth", "ntilde",
            "ograve", "oacute", "ocircumflex", "otilde", "odieresis", "divide", "oslash", "ugrave", "uacute", "ucircumflex", "udieresis",
            "yacute", "thorn", "ydieresis",
        ]);
        return [.. names];
    }

    /// <summary>
    /// Characters to codes: ASCII and Latin-1 as themselves, code page 1252's extra characters at 0x80 to 0x9F. Each
    /// character gets its first code, so the bullet is 0x95 and the space 0x20.
    /// </summary>
    private static Dictionary<char, byte> BuildReverse()
    {
        Dictionary<char, byte> reverse = [];
        for (int code = 0x20; code <= 0x7E; code++)
        {
            reverse[(char)code] = (byte)code;
        }

        ReadOnlySpan<(int Code, char Char)> windows =
        [
            (0x80, '\u20AC'), (0x82, '\u201A'), (0x83, '\u0192'), (0x84, '\u201E'), (0x85, '\u2026'), (0x86, '\u2020'), (0x87, '\u2021'),
            (0x88, '\u02C6'), (0x89, '\u2030'), (0x8A, '\u0160'), (0x8B, '\u2039'), (0x8C, '\u0152'), (0x8E, '\u017D'), (0x91, '\u2018'),
            (0x92, '\u2019'), (0x93, '\u201C'), (0x94, '\u201D'), (0x95, '\u2022'), (0x96, '\u2013'), (0x97, '\u2014'), (0x98, '\u02DC'),
            (0x99, '\u2122'), (0x9A, '\u0161'), (0x9B, '\u203A'), (0x9C, '\u0153'), (0x9E, '\u017E'), (0x9F, '\u0178'),
        ];
        foreach ((int code, char c) in windows)
        {
            reverse[c] = (byte)code;
        }

        for (int code = 0xA0; code <= 0xFF; code++)
        {
            reverse[(char)code] = (byte)code;
        }

        return reverse;
    }
}
