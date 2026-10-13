using System.Runtime.CompilerServices;
using System.Text;

using Egami.Fonts;

using Egap.Pdf.Fonts;

namespace Egap.Pdf;

/// <summary>The kind of a PDF font, its <c>/Subtype</c> (9.5).</summary>
public enum PdfFontType
{
    /// <summary>A subtype Egap does not know, or none.</summary>
    Unknown,

    /// <summary>A Type 1 font (9.6.2), embedded or one of the standard 14.</summary>
    Type1,

    /// <summary>A multiple master Type 1 font instance (9.6.2.3).</summary>
    MMType1,

    /// <summary>A TrueType font (9.6.3).</summary>
    TrueType,

    /// <summary>A Type 3 font, whose glyphs are content streams (9.6.4).</summary>
    Type3,

    /// <summary>A composite font (9.7): a CMap and a CIDFont, for CJK text and embedded subsets with two-byte codes.</summary>
    Type0,
}

/// <summary>The flags of a font descriptor (9.8.2, table 121).</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The spec names these the font flags.")]
public enum PdfFontFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>All glyphs have the same width.</summary>
    FixedPitch = 1,

    /// <summary>Glyphs have serifs.</summary>
    Serif = 1 << 1,

    /// <summary>The font has glyphs outside the Standard Latin set.</summary>
    Symbolic = 1 << 2,

    /// <summary>Glyphs look handwritten.</summary>
    Script = 1 << 3,

    /// <summary>The font uses only the Standard Latin set.</summary>
    Nonsymbolic = 1 << 5,

    /// <summary>Glyphs slant.</summary>
    Italic = 1 << 6,

    /// <summary>The font has no lowercase letters.</summary>
    AllCap = 1 << 16,

    /// <summary>Lowercase letters are small capitals.</summary>
    SmallCap = 1 << 17,

    /// <summary>Bold glyphs are drawn bolder at small sizes.</summary>
    ForceBold = 1 << 18,
}

/// <summary>
/// A font a page's text uses (Phase 3 design Q7): read-only. It says what kind of font it is, its names, flags and metrics,
/// how its strings split into codes, each code's width and glyph name, and the text a code stands for, through the font's
/// ToUnicode map first, then its encoding's glyph names through the Adobe Glyph List. Fonts are read leniently: what a file
/// gets wrong gives no answer rather than an exception.
/// </summary>
public sealed class PdfFont
{
    private static readonly ConditionalWeakTable<PdfDictionary, PdfFont> Cache = [];

    private readonly PdfDictionary? descriptor;
    private readonly PdfDictionary? descendant;
    private readonly List<PdfWarning> warnings = [];
    private readonly Lazy<FontProgram?> program;
    private readonly Lazy<string?[]?> encoding;
    private readonly CMap? toUnicode;
    private readonly bool identityToUnicode;
    private Dictionary<ushort, int>? programUnicode;
    private readonly CMap? codes;
    private readonly Standard14Font? standard;
    private readonly double[] widths = [];
    private readonly int firstChar;
    private readonly double missingWidth;
    private readonly Dictionary<int, double>? cidWidths;

    private PdfFont(PdfDictionary dictionary)
    {
        Dictionary = dictionary;
        Type = dictionary.GetName("Subtype")?.Value switch
        {
            "Type1" => PdfFontType.Type1,
            "MMType1" => PdfFontType.MMType1,
            "TrueType" => PdfFontType.TrueType,
            "Type3" => PdfFontType.Type3,
            "Type0" => PdfFontType.Type0,
            _ => PdfFontType.Unknown,
        };
        BaseFont = dictionary.GetName("BaseFont")?.Value;
        descendant = Type == PdfFontType.Type0 && dictionary.Get<PdfArray>("DescendantFonts") is { Count: > 0 } descendants
            ? descendants.Get<PdfDictionary>(0)
            : null;
        descriptor = (descendant ?? dictionary).Get<PdfDictionary>("FontDescriptor");
        IsEmbedded = Type == PdfFontType.Type3
            || (descriptor is not null && (descriptor.Get("FontFile") ?? descriptor.Get("FontFile2") ?? descriptor.Get("FontFile3")) is not null);
        standard = IsEmbedded || Type is PdfFontType.Type3 or PdfFontType.Type0 ? null : Standard14Font.Get(BaseFont);
        PdfObject? toUnicodeEntry = dictionary.Get("ToUnicode")?.Resolve();
        toUnicode = toUnicodeEntry is PdfStream map ? ReadCMap(map) : null;
        identityToUnicode = toUnicodeEntry is PdfName { Value: "Identity-H" or "Identity-V" };
        program = new(() => FontProgram.Load(descriptor, warnings));
        PdfObject? encodingValue = dictionary.Get("Encoding")?.Resolve();
        encoding = new(() => Type == PdfFontType.Type0 ? null : SimpleEncoding(encodingValue));
        if (Type == PdfFontType.Type0)
        {
            PdfObject? encodingEntry = dictionary.Get("Encoding")?.Resolve();
            codes = encodingEntry is PdfStream embedded ? ReadCMap(embedded) : null;
            string? cmapName = (encodingEntry as PdfName)?.Value ?? codes?.Name;
            IsVertical = codes?.IsVertical == true || cmapName?.EndsWith("-V", StringComparison.Ordinal) == true;
            missingWidth = descendant?.GetNumber("DW") ?? 1000;
            cidWidths = ReadW(descendant?.Get<PdfArray>("W"));
            return;
        }

        firstChar = (int)Math.Clamp(dictionary.GetInt("FirstChar") ?? 0, 0, 255);
        if (dictionary.Get<PdfArray>("Widths") is PdfArray list)
        {
            widths = new double[Math.Min(list.Count, 256)];
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = list.GetNumber(i) ?? 0;
            }
        }

        missingWidth = descriptor?.GetNumber("MissingWidth") ?? 0;
        if (Type == PdfFontType.Type3)
        {
            // Type 3 widths are in glyph space: scale them by the font matrix into thousandths of text space.
            double scale = FontMatrix.A;
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] *= scale * 1000;
            }

            missingWidth *= scale * 1000;
        }

    }

    /// <summary>The font dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The kind of font.</summary>
    public PdfFontType Type { get; }

    /// <summary>The <c>/BaseFont</c> name as written, with a subset tag when it has one; null for a Type 3 font without one.</summary>
    public string? BaseFont { get; }

    /// <summary>The font's name without a subset tag (<c>ABCDEF+</c>).</summary>
    public string? Name => IsSubset ? BaseFont![7..] : BaseFont;

    /// <summary>Whether the base name carries a subset tag: six capital letters and a plus sign (9.6.4).</summary>
    public bool IsSubset => BaseFont is { Length: > 7 } name && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper);

    /// <summary>Whether the file holds the font's glyphs: an embedded font program, or a Type 3 font's glyph procedures.</summary>
    public bool IsEmbedded { get; }

    /// <summary>Whether the font is one of the standard 14 and not embedded, so a viewer supplies it.</summary>
    public bool IsStandard14 => standard is not null;

    /// <summary>Whether codes may take more than one byte: a Type 0 font.</summary>
    public bool IsComposite => Type == PdfFontType.Type0;

    /// <summary>Whether the font writes top to bottom (a Type 0 font with a vertical CMap).</summary>
    public bool IsVertical { get; }

    /// <summary>The descriptor's flags; for a standard 14 font without a descriptor, the flags its metrics imply.</summary>
    public PdfFontFlags Flags => descriptor?.GetInt("Flags") is long flags
        ? (PdfFontFlags)(int)flags
        : standard is null ? PdfFontFlags.None
        : (standard.IsSymbolic ? PdfFontFlags.Symbolic : PdfFontFlags.Nonsymbolic) | (standard.IsFixedPitch ? PdfFontFlags.FixedPitch : 0)
            | (standard.ItalicAngle != 0 ? PdfFontFlags.Italic : 0) | (standard.Name.StartsWith("Times", StringComparison.Ordinal) ? PdfFontFlags.Serif : 0);

    /// <summary>The height above the baseline, in thousandths of text space, from the descriptor or the standard 14 metrics.</summary>
    public double Ascent => descriptor?.GetNumber("Ascent") ?? standard?.Ascent ?? 0;

    /// <summary>The depth below the baseline, a negative number in thousandths of text space.</summary>
    public double Descent => descriptor?.GetNumber("Descent") ?? standard?.Descent ?? 0;

    /// <summary>The height of flat capital letters, in thousandths of text space.</summary>
    public double CapHeight => descriptor?.GetNumber("CapHeight") ?? standard?.CapHeight ?? 0;

    /// <summary>The slant of vertical stems, in degrees counterclockwise from vertical.</summary>
    public double ItalicAngle => descriptor?.GetNumber("ItalicAngle") ?? standard?.ItalicAngle ?? 0;

    /// <summary>The box around every glyph laid on one origin, in glyph space; empty when the font says none.</summary>
    public PdfRectangle BoundingBox => PdfRectangle.FromArray((Type == PdfFontType.Type3 ? Dictionary : descriptor)?.Get<PdfArray>("FontBBox"))
        ?? standard?.BoundingBox ?? default;

    /// <summary>The matrix from glyph space to text space: a Type 3 font's <c>/FontMatrix</c>, else a scale of one thousandth.</summary>
    public PdfMatrix FontMatrix => Type == PdfFontType.Type3 && Dictionary.Get<PdfArray>("FontMatrix") is { Count: 6 } m
        && m.GetNumber(0) is double a && m.GetNumber(1) is double b && m.GetNumber(2) is double c
        && m.GetNumber(3) is double d && m.GetNumber(4) is double e && m.GetNumber(5) is double f
        ? new PdfMatrix(a, b, c, d, e, f)
        : new PdfMatrix(0.001, 0, 0, 0.001, 0, 0);

    /// <summary>The font a dictionary describes; the same object for the same dictionary.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="dictionary"/> is null.</exception>
    public static PdfFont FromDictionary(PdfDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        return Cache.GetValue(dictionary, d => new PdfFont(d));
    }

    /// <summary>
    /// Reads the code at <paramref name="offset"/> in a shown string: one byte for a simple font, as the CMap's codespace
    /// says for a composite one (two bytes for Identity).
    /// </summary>
    /// <param name="bytes">The string's bytes.</param>
    /// <param name="offset">Where the code starts.</param>
    /// <param name="length">How many bytes the code takes.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside <paramref name="bytes"/>.</exception>
    public int ReadCode(ReadOnlySpan<byte> bytes, int offset, out int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, bytes.Length);
        (int code, length) = Next(bytes, offset);
        return code;
    }

    /// <summary>The codes a shown string holds, in order.</summary>
    public IReadOnlyList<int> GetCodes(ReadOnlySpan<byte> bytes)
    {
        List<int> list = [];
        for (int at = 0; at < bytes.Length;)
        {
            (int code, int length) = Next(bytes, at);
            list.Add(code);
            at += length;
        }

        return list;
    }

    /// <summary>A code's advance width in thousandths of text space (for vertical fonts, the horizontal width still).</summary>
    public double GetWidth(int code)
    {
        if (IsComposite)
        {
            int cid = ToCid(code, CodeLength(code));
            return cidWidths is not null && cidWidths.TryGetValue(cid, out double w) ? w : missingWidth;
        }

        int index = code - firstChar;
        if ((uint)index < (uint)widths.Length)
        {
            return widths[index];
        }

        if (standard is not null && GetGlyphName(code) is string glyph)
        {
            return standard.Width(glyph) ?? missingWidth;
        }

        return missingWidth;
    }

    /// <summary>The glyph name a simple font's encoding gives a code, or null (always for a composite font).</summary>
    public string? GetGlyphName(int code)
    {
        return encoding.Value is string?[] names && (uint)code < 256 ? names[code] : null;
    }

    /// <summary>What went wrong reading the font, such as an embedded program that could not be read.</summary>
    public IReadOnlyList<PdfWarning> Warnings => warnings;

    /// <summary>
    /// The embedded font program as an Egami typeface (design Q8): TrueType, OpenType or CFF as stored, Type 1 converted to
    /// CFF; null when the font embeds none or it cannot be read (a warning says why). Loaded once, when first asked.
    /// </summary>
    public Typeface? GetTypeface()
    {
        return program.Value?.Typeface;
    }

    /// <summary>
    /// The glyph in <see cref="GetTypeface"/> a code shows, or null without a program. Simple fonts go through the glyph name
    /// (CFF and Type 1) or the cmaps 9.6.5.4 lists (TrueType); composite fonts through the CID and <c>/CIDToGIDMap</c> or the
    /// CFF charset.
    /// </summary>
    public ushort? GetGlyphId(int code)
    {
        if (program.Value is not FontProgram font)
        {
            return null;
        }

        if (IsComposite)
        {
            int cid = ToCid(code, CodeLength(code));
            if (font.Cff is { IsCidKeyed: true } cidKeyed)
            {
                return cidKeyed.GlyphByCid(cid) ?? 0;
            }

            if (descendant?.Get("CIDToGIDMap")?.Resolve() is PdfStream map)
            {
                byte[] bytes = map.GetDecodedBytes();
                return 2 * cid + 1 < bytes.Length ? (ushort)((bytes[2 * cid] << 8) | bytes[(2 * cid) + 1]) : (ushort)0;
            }

            return (ushort)Math.Clamp(cid, 0, ushort.MaxValue);
        }

        string? name = GetGlyphName(code);

        // An OpenType font with CFF outlines names its glyphs, whatever the subtype says.
        if (font.Cff is { IsCidKeyed: false } named && name is not null && named.GlyphByName(name) is ushort byName)
        {
            return byName;
        }

        if (Type == PdfFontType.TrueType || (font.Cff is null && (font.Cmaps is not null || font.PostNames is not null)))
        {
            return TrueTypeGlyph(font, code, name);
        }

        if (font.Cff is CffInfo cff)
        {
            return (name is not null ? cff.GlyphByName(name) : null)
                ?? (font.BuiltInEncoding is string?[] own && (uint)code < 256 && own[code] is string ownName ? cff.GlyphByName(ownName) : null)
                ?? 0;
        }

        return (ushort)Math.Clamp(code, 0, ushort.MaxValue);
    }

    /// <summary>A simple TrueType font's glyph for a code (9.6.5.4, with pdf.js's allowances for fonts that break the rules).</summary>
    private ushort TrueTypeGlyph(FontProgram font, int code, string? name)
    {
        TrueTypeCmaps cmaps = font.Cmaps ?? TrueTypeCmaps.Read([]);
        bool symbolic = (Flags & PdfFontFlags.Symbolic) != 0;
        bool hasEncoding = Dictionary.Get("Encoding") is not null;
        if ((!symbolic || hasEncoding) && cmaps.Unicode is { } unicode && GlyphList.ToUnicode(name) is { Length: > 0 } text
            && unicode.TryGetValue(char.ConvertToUtf32(text, 0), out ushort byName))
        {
            return byName;
        }

        if (name is not null && font.PostNames?.TryGetValue(name, out ushort named) == true)
        {
            return named;
        }

        if (cmaps.Symbol is { } symbol)
        {
            foreach (int key in (int[])[code, 0xF000 + code, 0xF100 + code, 0xF200 + code])
            {
                if (symbol.TryGetValue(key, out ushort glyph))
                {
                    return glyph;
                }
            }
        }

        if (cmaps.MacRoman is { } mac)
        {
            int macCode = name is not null ? Array.IndexOf(FontEncodings.ForName("MacRomanEncoding")!, name) : -1;
            if (mac.TryGetValue(macCode >= 0 ? macCode : code, out ushort glyph))
            {
                return glyph;
            }
        }

        if (cmaps.Unicode is { } fallback && fallback.TryGetValue(code, out ushort direct))
        {
            return direct;
        }

        // No map reaches it: with no cmap at all, the code is the glyph, as pdf.js reads such fonts.
        return cmaps.Unicode is null && cmaps.Symbol is null && cmaps.MacRoman is null && (uint)code < (uint)font.Typeface.GlyphCount ? (ushort)code : (ushort)0;
    }

    /// <summary>The CID a composite font's CMap maps a code to, or null for a simple font.</summary>
    public int? GetCid(int code)
    {
        return IsComposite ? ToCid(code, CodeLength(code)) : null;
    }

    /// <summary>
    /// The text a code stands for (Phase 3 design Q10): from the font's ToUnicode map, else from its glyph name through the
    /// Adobe Glyph List (names such as <c>a65</c> or <c>g65</c> giving the code they number, as pdf.js and PDFium read
    /// them), else from the embedded TrueType program's Unicode cmap or glyph names read backwards, as pdf.js and PDFium do; null when nothing says.
    /// </summary>
    public string? ToUnicode(int code)
    {
        if (identityToUnicode && code is >= 0 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF))
        {
            return char.ConvertFromUtf32(code);
        }

        if (toUnicode?.ToUnicode(code, IsComposite ? CodeLength(code) : 1) is { Length: > 0 } text)
        {
            return text;
        }

        string? name = GetGlyphName(code);
        if (GlyphList.ToUnicode(name) is string named)
        {
            return named;
        }

        if (NumberedName(name) is int number)
        {
            return number < 256 ? (WinAnsiEncoding.Decode((byte)number) ?? (char)number).ToString() : null;
        }

        return ProgramUnicode(code);
    }

    /// <summary>A glyph name that only numbers a code, such as <c>a65</c>, <c>g65</c> or <c>C65</c>: the number, or null.</summary>
    private static int? NumberedName(string? name)
    {
        if (name is not { Length: >= 2 and <= 6 })
        {
            return null;
        }

        int letters = char.IsAsciiLetter(name[0]) ? (name.Length > 2 && char.IsAsciiLetter(name[1]) ? 2 : 1) : 0;
        return letters > 0 && int.TryParse(name.AsSpan(letters), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) && value < 0x10000 ? value : null;
    }

    /// <summary>The text an embedded TrueType program's Unicode cmap gives the code's glyph, read backwards; null otherwise.</summary>
    private string? ProgramUnicode(int code)
    {
        if (GetGlyphId(code) is not ushort glyph || glyph == 0 || program.Value is not FontProgram font)
        {
            return null;
        }

        if (programUnicode is null)
        {
            // The Unicode cmap read backwards, else the post table's glyph names through the glyph list.
            Dictionary<ushort, int> reverse = [];
            foreach (KeyValuePair<int, ushort> entry in (font.Cmaps?.Unicode ?? []).OrderBy(e => e.Key))
            {
                reverse.TryAdd(entry.Value, entry.Key);
            }

            foreach (KeyValuePair<string, ushort> entry in font.PostNames ?? [])
            {
                if (!reverse.ContainsKey(entry.Value) && GlyphList.ToUnicode(entry.Key) is { Length: > 0 } text && !char.IsSurrogate(text[0]))
                {
                    reverse[entry.Value] = text[0];
                }
            }

            programUnicode = reverse;
        }

        return programUnicode.TryGetValue(glyph, out int cp) && cp is > 0 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF) ? char.ConvertFromUtf32(cp) : null;
    }

    /// <summary>The text a shown string stands for, its codes' texts joined; codes with no text are left out.</summary>
    public string ToUnicode(ReadOnlySpan<byte> bytes)
    {
        StringBuilder text = new();
        for (int at = 0; at < bytes.Length;)
        {
            (int code, int length) = Next(bytes, at);
            text.Append(ToUnicode(code));
            at += length;
        }

        return text.ToString();
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{Type} {BaseFont ?? "(unnamed)"}{(IsEmbedded ? " (embedded)" : string.Empty)}";
    }

    private (int Code, int Length) Next(ReadOnlySpan<byte> bytes, int offset)
    {
        if (!IsComposite)
        {
            return (bytes[offset], 1);
        }

        if (codes is not null)
        {
            return codes.Next(bytes, offset);
        }

        // Identity and the predefined CJK CMaps use two-byte codes; a lone last byte is a code of its own.
        return offset + 1 < bytes.Length ? ((bytes[offset] << 8) | bytes[offset + 1], 2) : (bytes[offset], 1);
    }

    /// <summary>How many bytes a composite code takes: as its CMap's codespace says, else two (Identity).</summary>
    private int CodeLength(int code)
    {
        return codes?.LengthOf(code, 2) ?? (code > 0xFFFF ? 4 : 2);
    }

    private int ToCid(int code, int length)
    {
        return codes is null ? code : codes.ToCid(code, length) ?? 0;
    }

    /// <summary>
    /// A simple font's glyph names by code (9.6.5): a base encoding with <c>/Differences</c> over it. Without a base the font's
    /// own encoding applies: the standard 14 metrics' for a standard font, StandardEncoding for a nonsymbolic one, and none
    /// for a symbolic TrueType font, whose codes go to its cmap directly.
    /// </summary>
    private string?[]? SimpleEncoding(PdfObject? value)
    {
        string?[]? builtIn = standard is not null ? [.. standard.BuiltInEncoding]
            : IsEmbedded && Type is PdfFontType.Type1 or PdfFontType.MMType1 && program.Value?.BuiltInEncoding is string?[] own ? own
            : (Flags & PdfFontFlags.Symbolic) != 0 && (Flags & PdfFontFlags.Nonsymbolic) == 0 ? null
            : Type == PdfFontType.Type3 ? null
            : FontEncodings.Standard;
        if (value is PdfName name)
        {
            return FontEncodings.ForName(name.Value) is string?[] table ? [.. table] : builtIn;
        }

        if (value is not PdfDictionary dictionary)
        {
            return builtIn;
        }

        string?[] names = FontEncodings.ForName(dictionary.GetName("BaseEncoding")?.Value) is string?[] baseTable ? [.. baseTable]
            : builtIn is not null ? [.. builtIn] : new string?[256];
        if (dictionary.Get<PdfArray>("Differences") is PdfArray differences)
        {
            int code = 0;
            foreach (PdfObject item in differences)
            {
                switch (item.Resolve())
                {
                    case PdfInteger number:
                        code = (int)Math.Clamp(number.Value, 0, 256);
                        break;
                    case PdfReal real:
                        code = (int)Math.Clamp(real.Value, 0, 256);
                        break;
                    case PdfName glyph when code < 256:
                        names[code++] = glyph.Value;
                        break;
                }
            }
        }

        return names;
    }

    private static CMap ReadCMap(PdfStream stream)
    {
        try
        {
            return CMap.Parse(stream.GetDecodedBytes());
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException)
        {
            return CMap.Parse([]);
        }
    }

    /// <summary>Reads a CIDFont's <c>/W</c> array: <c>c [w1 w2 ...]</c> and <c>cFirst cLast w</c> runs (9.7.4.3).</summary>
    private static Dictionary<int, double>? ReadW(PdfArray? w)
    {
        if (w is null)
        {
            return null;
        }

        Dictionary<int, double> result = [];
        const int MaxEntries = 1 << 20;
        int i = 0;
        while (i < w.Count && result.Count < MaxEntries)
        {
            if (w.GetInt(i) is not long first)
            {
                break;
            }

            if (i + 1 < w.Count && w.Get(i + 1).Resolve() is PdfArray list)
            {
                for (int k = 0; k < list.Count && result.Count < MaxEntries; k++)
                {
                    result[(int)Math.Clamp(first + k, 0, int.MaxValue)] = list.GetNumber(k) ?? 0;
                }

                i += 2;
            }
            else if (i + 2 < w.Count && w.GetInt(i + 1) is long last && w.GetNumber(i + 2) is double width)
            {
                for (long c = Math.Max(first, 0); c <= last && result.Count < MaxEntries; c++)
                {
                    result[(int)Math.Min(c, int.MaxValue)] = width;
                }

                i += 3;
            }
            else
            {
                break;
            }
        }

        return result;
    }
}
