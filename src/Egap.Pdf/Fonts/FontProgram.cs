using Egami;
using Egami.Fonts;

namespace Egap.Pdf.Fonts;

/// <summary>
/// A font's embedded program (9.9) loaded as an Egami <see cref="Typeface"/>, as hub Q5 decided: TrueType from
/// <c>/FontFile2</c>, OpenType and bare CFF from <c>/FontFile3</c>, Type 1 from <c>/FontFile</c> through a conversion to CFF
/// (design Q8). Glyph names, CIDs, cmap subtables and a CFF font's own encoding come from the typeface (Egami.Fonts 0.2); what
/// stays here is which of them a PDF font uses, and a Type 1 program's built-in encoding.
/// </summary>
internal sealed class FontProgram
{
    private FontProgram(Typeface typeface, bool cff)
    {
        Typeface = typeface;
        IsCff = cff;
        Cmaps = cff && !typeface.HasCharacterMap ? null : new TrueTypeCmaps(typeface);
    }

    public Typeface Typeface { get; }

    /// <summary>Whether the glyphs are CFF: a bare CFF or converted Type 1 program, or OpenType with a CFF table.</summary>
    public bool IsCff { get; }

    /// <summary>Whether the program is a CID-keyed CFF font, whose glyphs are found by CID rather than by name.</summary>
    public bool IsCidKeyed => Typeface.CidSystemInfo is not null;

    /// <summary>The cmap subtables a simple TrueType font uses; null for a CFF program without a cmap table.</summary>
    public TrueTypeCmaps? Cmaps { get; }

    /// <summary>The font's own encoding as glyph names by code: a Type 1 program's, or a name-keyed CFF font's.</summary>
    public string?[]? BuiltInEncoding { get; private init; }

    /// <summary>The glyph with a name, from a CFF charset or a TrueType <c>post</c> table; null when none has it.</summary>
    public ushort? GlyphByName(string name)
    {
        return Typeface.TryGetGlyphByName(name, out ushort glyph) ? glyph : null;
    }

    /// <summary>The glyph with a CID in a CID-keyed CFF font, or null.</summary>
    public ushort? GlyphByCid(int cid)
    {
        return Typeface.TryGetGlyphByCid(cid, out ushort glyph) ? glyph : null;
    }

    /// <summary>
    /// Loads the program a font descriptor embeds; null when it embeds none. A program that cannot be read gives a warning
    /// and null, never an exception.
    /// </summary>
    public static FontProgram? Load(PdfDictionary? descriptor, List<PdfWarning> warnings)
    {
        if (descriptor is null)
        {
            return null;
        }

        try
        {
            if (descriptor.Get("FontFile2")?.Resolve() is PdfStream trueType)
            {
                return Sfnt(trueType.GetDecodedBytes());
            }

            if (descriptor.Get("FontFile3")?.Resolve() is PdfStream file3)
            {
                byte[] data = file3.GetDecodedBytes();
                return file3.Dictionary.GetName("Subtype")?.Value == "OpenType" || IsSfnt(data) ? Sfnt(data) : Cff(data);
            }

            if (descriptor.Get("FontFile")?.Resolve() is PdfStream type1)
            {
                // Bare CFF filed as Type 1, as some producers do, is read by its header as pdf.js does.
                return FromFile(type1.GetDecodedBytes());
            }
        }
        catch (Exception e) when (e is EgamiException or InvalidDataException or PdfException or IOException or ArgumentException)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.FontProgramUnreadable, $"The embedded font program of {descriptor.GetName("FontName")?.Value ?? "a font"} could not be read: {e.Message}"));
        }

        return null;
    }

    /// <summary>A font file as it is stored on disk: TrueType or OpenType, CFF, or Type 1 as PFB or PFA.</summary>
    public static FontProgram FromFile(byte[] data)
    {
        if (IsSfnt(data))
        {
            return Sfnt(data);
        }

        if (data is [1, 0, _, _, ..])
        {
            return Cff(data);
        }

        Type1Font font = Type1Font.Read(data);
        return new FontProgram(Typeface.FromCff(font.Cff), cff: true) { BuiltInEncoding = font.BuiltInEncoding };
    }

    /// <summary>A typeface Egami loaded, such as a system font.</summary>
    public static FontProgram FromTypeface(Typeface face)
    {
        return Sfnt(face);
    }

    private static FontProgram Cff(byte[] data)
    {
        Typeface face = Typeface.FromCff(data);
        return new FontProgram(face, cff: true) { BuiltInEncoding = OwnEncoding(face) };
    }

    private static FontProgram Sfnt(byte[] data)
    {
        return Sfnt(Typeface.FromMemory(data));
    }

    private static FontProgram Sfnt(Typeface face)
    {
        bool cff = face.TryGetTable(new FontTag("CFF "), out _);
        return new FontProgram(face, cff) { BuiltInEncoding = cff ? OwnEncoding(face) : null };
    }

    /// <summary>
    /// A name-keyed CFF font's own encoding as names by code: the Standard or Expert Encoding by name, so codes keep their
    /// names whether or not the font has those glyphs, else each code's glyph named from the charset; null for other fonts.
    /// </summary>
    private static string?[]? OwnEncoding(Typeface face)
    {
        if (face.BuiltInEncoding is not CharacterMap map)
        {
            return null;
        }

        switch (map.EncodingId)
        {
            case 0:
                return FontEncodings.Standard;
            case 1:
                return [.. CffStandardStrings.ExpertEncoding.Select(n => n.Length == 0 || n == ".notdef" ? null : n)];
            default:
                string?[] names = new string?[256];
                foreach ((int code, ushort glyph) in map.GetMappings())
                {
                    if ((uint)code < 256 && glyph != 0)
                    {
                        names[code] = face.GetGlyphName(glyph);
                    }
                }

                return names;
        }
    }

    private static bool IsSfnt(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 && (data[..4].SequenceEqual((byte[])[0, 1, 0, 0]) || data[..4].SequenceEqual("true"u8) || data[..4].SequenceEqual("OTTO"u8));
    }
}
