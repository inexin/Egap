using Egami;
using Egami.Fonts;

namespace Egap.Pdf.Fonts;

/// <summary>
/// A font's embedded program (9.9) loaded as an Egami <see cref="Typeface"/>, as hub Q5 decided: TrueType from
/// <c>/FontFile2</c>, OpenType and bare CFF from <c>/FontFile3</c>, Type 1 from <c>/FontFile</c> through a conversion to CFF
/// (design Q8). With it come what PDF needs to reach a glyph and Egami does not say: a CFF font's glyph names, CIDs and
/// built-in encoding, a Type 1 font's built-in encoding, and a TrueType font's symbol and Mac Roman cmaps.
/// </summary>
internal sealed class FontProgram
{
    private FontProgram(Typeface typeface)
    {
        Typeface = typeface;
    }

    public Typeface Typeface { get; }

    /// <summary>The CFF glyph names, CIDs and encoding, for CFF and converted Type 1 programs and OpenType with a CFF table.</summary>
    public CffInfo? Cff { get; private init; }

    /// <summary>The cmaps a simple TrueType font uses, for sfnt programs with a <c>cmap</c> table.</summary>
    public TrueTypeCmaps? Cmaps { get; private init; }

    /// <summary>A TrueType font's glyph names from its <c>post</c> table, or null.</summary>
    public Dictionary<string, ushort>? PostNames { get; private init; }

    /// <summary>The font's own encoding: a Type 1 program's, or a name-keyed CFF font's.</summary>
    public string?[]? BuiltInEncoding { get; private init; }

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
                if (file3.Dictionary.GetName("Subtype")?.Value == "OpenType" || IsSfnt(data))
                {
                    return Sfnt(data);
                }

                CffInfo cff = CffInfo.Read(data);
                return new FontProgram(Typeface.FromCff(data)) { Cff = cff, BuiltInEncoding = cff.BuiltInEncoding };
            }

            if (descriptor.Get("FontFile")?.Resolve() is PdfStream type1)
            {
                byte[] data = type1.GetDecodedBytes();
                if (IsSfnt(data))
                {
                    return Sfnt(data);
                }

                if (data is [1, 0, _, _, ..])
                {
                    // Bare CFF filed as Type 1, as some producers do; pdf.js reads it by its header too.
                    CffInfo bare = CffInfo.Read(data);
                    return new FontProgram(Typeface.FromCff(data)) { Cff = bare, BuiltInEncoding = bare.BuiltInEncoding };
                }

                Type1Font font = Type1Font.Read(data);
                return new FontProgram(Typeface.FromCff(font.Cff)) { Cff = CffInfo.Read(font.Cff), BuiltInEncoding = font.BuiltInEncoding };
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
            CffInfo bare = CffInfo.Read(data);
            return new FontProgram(Typeface.FromCff(data)) { Cff = bare, BuiltInEncoding = bare.BuiltInEncoding };
        }

        Type1Font font = Type1Font.Read(data);
        return new FontProgram(Typeface.FromCff(font.Cff)) { Cff = CffInfo.Read(font.Cff), BuiltInEncoding = font.BuiltInEncoding };
    }

    /// <summary>A typeface Egami loaded, such as a system font, with its cmaps and glyph names read.</summary>
    public static FontProgram FromTypeface(Typeface face)
    {
        return Sfnt(face);
    }

    private static FontProgram Sfnt(byte[] data)
    {
        return Sfnt(Typeface.FromMemory(data));
    }

    private static FontProgram Sfnt(Typeface face)
    {
        CffInfo? cff = face.TryGetTable(new FontTag("CFF "), out ReadOnlyMemory<byte> table) ? TryCff(table.Span) : null;
        TrueTypeCmaps? cmaps = face.TryGetTable(new FontTag("cmap"), out ReadOnlyMemory<byte> cmap) ? TrueTypeCmaps.Read(cmap.Span) : null;
        Dictionary<string, ushort>? post = face.TryGetTable(new FontTag("post"), out ReadOnlyMemory<byte> names) ? TrueTypePost.Read(names.Span) : null;
        return new FontProgram(face) { Cff = cff, Cmaps = cmaps, PostNames = post, BuiltInEncoding = cff?.BuiltInEncoding };
    }

    private static CffInfo? TryCff(ReadOnlySpan<byte> data)
    {
        try
        {
            return CffInfo.Read(data);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool IsSfnt(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 && (data[..4].SequenceEqual((byte[])[0, 1, 0, 0]) || data[..4].SequenceEqual("true"u8) || data[..4].SequenceEqual("OTTO"u8));
    }
}
