using Egami.Fonts;

namespace Egap.Pdf.Fonts;

/// <summary>
/// The character maps of a TrueType font a simple PDF font reaches its glyphs through (9.6.5.4), chosen from the typeface's
/// subtables: the Windows symbol map (3,0), the Windows Unicode map (3,10 or 3,1) or else a Unicode-platform map (0,*), as
/// pdf.js reads such fonts, and the Macintosh Roman map (1,0).
/// </summary>
internal sealed class TrueTypeCmaps
{
    public TrueTypeCmaps(Typeface face)
    {
        Symbol = face.FindCharacterMap(3, 0);
        Unicode = face.FindCharacterMap(3, 10) ?? face.FindCharacterMap(3, 1) ?? face.CharacterMaps.FirstOrDefault(m => m.PlatformId == 0);
        MacRoman = face.FindCharacterMap(1, 0);
    }

    /// <summary>The (3,0) map, codes to glyphs, or null.</summary>
    public CharacterMap? Symbol { get; }

    /// <summary>The (3,10) or (3,1) map, Unicode to glyphs, or else a (0,*) map; null when the font has neither.</summary>
    public CharacterMap? Unicode { get; }

    /// <summary>The (1,0) map, Mac Roman codes to glyphs, or null.</summary>
    public CharacterMap? MacRoman { get; }

    /// <summary>Whether the font has none of the three.</summary>
    public bool IsEmpty => Symbol is null && Unicode is null && MacRoman is null;

    /// <summary>The glyph a map gives a code, false when it gives none (or .notdef).</summary>
    public static bool TryGet(CharacterMap? map, int code, out ushort glyph)
    {
        glyph = map?.GetGlyph(code) ?? 0;
        return glyph != 0;
    }
}
