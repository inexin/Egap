using System.Buffers.Binary;
using System.Text;

namespace Egap.Pdf.Fonts;

/// <summary>
/// A TrueType font's glyph names from its <c>post</c> table, formats 1 and 2, for simple fonts whose encoding names glyphs
/// the cmaps cannot reach (or that have no cmap at all, as pdf.js allows). The 258 standard Macintosh names are built in.
/// </summary>
internal static class TrueTypePost
{
    private static readonly string[] MacNames =
    [
        ".notdef", ".null", "nonmarkingreturn", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quotesingle",
        "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four", "five",
        "six", "seven", "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at", "A", "B", "C", "D", "E", "F",
        "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "bracketleft", "backslash",
        "bracketright", "asciicircum", "underscore", "grave", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o",
        "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "braceleft", "bar", "braceright", "asciitilde", "Adieresis", "Aring",
        "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute", "agrave", "acircumflex", "adieresis", "atilde", "aring",
        "ccedilla", "eacute", "egrave", "ecircumflex", "edieresis", "iacute", "igrave", "icircumflex", "idieresis", "ntilde", "oacute",
        "ograve", "ocircumflex", "odieresis", "otilde", "uacute", "ugrave", "ucircumflex", "udieresis", "dagger", "degree", "cent",
        "sterling", "section", "bullet", "paragraph", "germandbls", "registered", "copyright", "trademark", "acute", "dieresis",
        "notequal", "AE", "Oslash", "infinity", "plusminus", "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation",
        "product", "pi", "integral", "ordfeminine", "ordmasculine", "Omega", "ae", "oslash", "questiondown", "exclamdown", "logicalnot",
        "radical", "florin", "approxequal", "Delta", "guillemotleft", "guillemotright", "ellipsis", "nonbreakingspace", "Agrave",
        "Atilde", "Otilde", "OE", "oe", "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright", "divide",
        "lozenge", "ydieresis", "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright", "fi", "fl", "daggerdbl",
        "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex", "Aacute", "Edieresis",
        "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex", "apple", "Ograve", "Uacute", "Ucircumflex",
        "Ugrave", "dotlessi", "circumflex", "tilde", "macron", "breve", "dotaccent", "ring", "cedilla", "hungarumlaut", "ogonek",
        "caron", "Lslash", "lslash", "Scaron", "scaron", "Zcaron", "zcaron", "brokenbar", "Eth", "eth", "Yacute", "yacute", "Thorn",
        "thorn", "minus", "multiply", "onesuperior", "twosuperior", "threesuperior", "onehalf", "onequarter", "threequarters", "franc",
        "Gbreve", "gbreve", "Idotaccent", "Scedilla", "scedilla", "Cacute", "cacute", "Ccaron", "ccaron", "dcroat",
    ];

    /// <summary>Glyph names to glyphs, the first glyph of each name; null when the table has no names.</summary>
    public static Dictionary<string, ushort>? Read(ReadOnlySpan<byte> post)
    {
        if (post.Length < 32)
        {
            return null;
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(post);
        Dictionary<string, ushort> glyphs = new(StringComparer.Ordinal);
        if (version == 0x00010000)
        {
            for (int i = 0; i < MacNames.Length; i++)
            {
                glyphs.TryAdd(MacNames[i], (ushort)i);
            }

            return glyphs;
        }

        if (version != 0x00020000 || post.Length < 34)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(post[32..]);
        int at = 34 + (count * 2);
        if (at > post.Length)
        {
            return null;
        }

        List<string> custom = [];
        while (at < post.Length)
        {
            int length = post[at++];
            if (at + length > post.Length)
            {
                break;
            }

            custom.Add(Encoding.Latin1.GetString(post.Slice(at, length)));
            at += length;
        }

        for (int glyph = 0; glyph < count; glyph++)
        {
            int index = BinaryPrimitives.ReadUInt16BigEndian(post[(34 + (glyph * 2))..]);
            string? name = index < MacNames.Length ? MacNames[index] : index - MacNames.Length < custom.Count ? custom[index - MacNames.Length] : null;
            if (name is not null)
            {
                glyphs.TryAdd(name, (ushort)glyph);
            }
        }

        return glyphs;
    }
}
