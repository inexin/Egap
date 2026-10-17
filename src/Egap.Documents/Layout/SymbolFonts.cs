using System.Text;

namespace Egap.Documents.Layout;

/// <summary>
/// Word's symbol fonts: Symbol, Wingdings and Webdings keep their glyphs at private-use code points (U+F020 to U+F0FF, the
/// font's code plus F000). Without the font installed those draw as boxes, so the common ones are mapped to the Unicode
/// characters they show; the rest stay as they are for a font that has them.
/// </summary>
internal static class SymbolFonts
{
    // Adobe's Symbol encoding: Greek letters and the mathematical signs Word documents use most.
    private static readonly Dictionary<int, char> Symbol = Build(
        "0x22=\u2200 0x24=\u2203 0x27=\u220B 0x2A=\u2217 0x2D=\u2212 0x40=\u2245 0x5C=\u2234 0x5E=\u22A5 0x60=\u203E " +
        "0x41=\u0391 0x42=\u0392 0x43=\u03A7 0x44=\u0394 0x45=\u0395 0x46=\u03A6 0x47=\u0393 0x48=\u0397 0x49=\u0399 0x4A=\u03D1 " +
        "0x4B=\u039A 0x4C=\u039B 0x4D=\u039C 0x4E=\u039D 0x4F=\u039F 0x50=\u03A0 0x51=\u0398 0x52=\u03A1 0x53=\u03A3 0x54=\u03A4 " +
        "0x55=\u03A5 0x56=\u03C2 0x57=\u03A9 0x58=\u039E 0x59=\u03A8 0x5A=\u0396 0x61=\u03B1 0x62=\u03B2 0x63=\u03C7 0x64=\u03B4 " +
        "0x65=\u03B5 0x66=\u03C6 0x67=\u03B3 0x68=\u03B7 0x69=\u03B9 0x6A=\u03D5 0x6B=\u03BA 0x6C=\u03BB 0x6D=\u03BC 0x6E=\u03BD " +
        "0x6F=\u03BF 0x70=\u03C0 0x71=\u03B8 0x72=\u03C1 0x73=\u03C3 0x74=\u03C4 0x75=\u03C5 0x76=\u03D6 0x77=\u03C9 0x78=\u03BE " +
        "0x79=\u03C8 0x7A=\u03B6 0xA3=\u2264 0xA5=\u221E 0xA7=\u2663 0xA8=\u2666 0xA9=\u2665 0xAA=\u2660 0xAB=\u2194 0xAC=\u2190 " +
        "0xAD=\u2191 0xAE=\u2192 0xAF=\u2193 0xB0=\u00B0 0xB1=\u00B1 0xB3=\u2265 0xB4=\u00D7 0xB5=\u221D 0xB6=\u2202 0xB7=\u2022 " +
        "0xB8=\u00F7 0xB9=\u2260 0xBA=\u2261 0xBB=\u2248 0xBC=\u2026 0xC5=\u2295 0xC6=\u2205 0xC7=\u2229 0xC8=\u222A 0xCE=\u2208 " +
        "0xD6=\u221A 0xD7=\u22C5 0xD8=\u00AC 0xD9=\u2227 0xDA=\u2228 0xDE=\u21D2 0xE0=\u25CA 0xE5=\u2211 0xF2=\u222B");

    // Wingdings: the bullets and check marks of lists.
    private static readonly Dictionary<int, char> Wingdings = Build(
        "0x6C=\u25CF 0x6E=\u25A0 0x6F=\u25A1 0x71=\u2751 0x75=\u25C6 0x76=\u2756 0x77=\u25C6 0xA7=\u25AA 0xA8=\u25FB 0xD8=\u27A2 " +
        "0xE0=\u2192 0xE8=\u2794 0xF0=\u21E8 0xFB=\u2717 0xFC=\u2713 0xFD=\u2612 0xFE=\u2611 0x9F=\u2022 0xA1=\u25CB 0x4A=\u263A");

    /// <summary>The text with a symbol font's private-use characters mapped, or the text itself when there is nothing to map.</summary>
    public static string Map(string text, string font)
    {
        Dictionary<int, char>? table = font.Trim().ToUpperInvariant() switch
        {
            "SYMBOL" => Symbol,
            "WINGDINGS" or "WINGDINGS 2" or "WINGDINGS 3" or "WEBDINGS" => Wingdings,
            _ => null,
        };
        if (table is null || !text.Any(c => c is >= '\uF020' and <= '\uF0FF'))
        {
            return text;
        }

        StringBuilder mapped = new(text.Length);
        foreach (char c in text)
        {
            mapped.Append(c is >= '\uF020' and <= '\uF0FF' && table.TryGetValue(c - 0xF000, out char unicode) ? unicode
                : c is >= '\uF020' and <= '\uF07E' && table == Symbol ? (char)(c - 0xF000) : c);
        }

        return mapped.ToString();
    }

    private static Dictionary<int, char> Build(string pairs)
    {
        Dictionary<int, char> table = [];
        foreach (string pair in pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            table[Convert.ToInt32(pair[..equals], 16)] = pair[equals + 1];
        }

        return table;
    }
}
