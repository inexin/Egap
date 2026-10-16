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
    private readonly string? unicodeCodes;
    private readonly string? ordering;
    private Dictionary<ushort, int>? programUnicode;
    private readonly CMap? codes;
    private readonly Standard14Font? standard;

    /// <summary>
    /// For an embedded simple font with no <c>/Widths</c>, the standard 14 metrics its name matches, as pdf.js reads such fonts;
    /// null otherwise.
    /// </summary>
    private readonly Standard14Font? fallbackMetrics;

    /// <summary>Whether a simple font has no <c>/Widths</c>, so widths come from metrics or the program.</summary>
    private readonly bool noWidths;
    private readonly double[] widths = [];
    private readonly int firstChar;
    private readonly double missingWidth;
    private readonly Dictionary<int, double>? cidWidths;
    private readonly Dictionary<int, (double W1, double Vx, double Vy)>? verticalMetrics;
    private readonly (double Vy, double W1) defaultVertical = (880, -1000);

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
        noWidths = Type is not (PdfFontType.Type3 or PdfFontType.Type0) && dictionary.Get<PdfArray>("Widths") is null;
        fallbackMetrics = noWidths && standard is null ? Standard14Font.Get(BaseFont) : null;
        PdfObject? toUnicodeEntry = dictionary.Get("ToUnicode")?.Resolve();
        toUnicode = toUnicodeEntry is PdfStream map ? ReadCMap(map) : null;
        identityToUnicode = toUnicodeEntry is PdfName { Value: "Identity-H" or "Identity-V" };
        program = new(() => FontProgram.Load(descriptor, warnings));
        PdfObject? encodingValue = dictionary.Get("Encoding")?.Resolve();
        encoding = new(() => Type == PdfFontType.Type0 ? null : SimpleEncoding(encodingValue));
        if (Type == PdfFontType.Type0)
        {
            PdfObject? encodingEntry = dictionary.Get("Encoding")?.Resolve();
            // An embedded CMap, a predefined one by name (Identity needs none), and whatever predefined CMap it uses.
            codes = encodingEntry switch
            {
                PdfStream embedded => ReadCMap(embedded),
                PdfName { Value: not ("Identity-H" or "Identity-V") } predefined => PredefinedCMaps.Get(predefined.Value),
                _ => null,
            };
            if (codes?.UseCMap is string use && PredefinedCMaps.Get(use) is CMap parent)
            {
                codes.Include(parent);
            }

            string? cmapName = (encodingEntry as PdfName)?.Value ?? codes?.Name;
            unicodeCodes = cmapName is null ? null
                : cmapName.Contains("-UCS2-", StringComparison.Ordinal) || cmapName.Contains("-UTF16-", StringComparison.Ordinal) ? "UTF16"
                : cmapName.Contains("-UTF32-", StringComparison.Ordinal) ? "UTF32"
                : cmapName.Contains("-UTF8-", StringComparison.Ordinal) ? "UTF8"
                : null;
            ordering = descendant?.Get<PdfDictionary>("CIDSystemInfo")?.GetText("Ordering");
            IsVertical = codes?.IsVertical == true || cmapName?.EndsWith("-V", StringComparison.Ordinal) == true;
            missingWidth = descendant?.GetNumber("DW") ?? 1000;
            cidWidths = ReadW(descendant?.Get<PdfArray>("W"));
            if (IsVertical)
            {
                defaultVertical = descendant?.Get<PdfArray>("DW2") is { Count: 2 } dw2 && dw2.GetNumber(0) is double vy && dw2.GetNumber(1) is double w1 ? (vy, w1) : defaultVertical;
                verticalMetrics = ReadW2(descendant?.Get<PdfArray>("W2"));
            }

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

    /// <summary>The font descriptor: the CIDFont's for a composite font; null when there is none.</summary>
    internal PdfDictionary? Descriptor => descriptor;

    /// <summary>
    /// The glyph a substitute program shows for a code (Phase 4 design Q5): a simple font's glyph by its name, through the
    /// substitute's CFF names or, for TrueType, its Unicode cmap; a symbol font's by the substitute's own encoding; anything
    /// else by the code's Unicode text. 0 (.notdef) when nothing reaches a glyph.
    /// </summary>
    internal ushort GetSubstituteGlyph(FontProgram program, int code)
    {
        if (!IsComposite)
        {
            string? name = GetGlyphName(code);
            if (name is not null && program.GlyphByName(name) is ushort byName)
            {
                return byName;
            }

            if (program is { IsCff: true, IsCidKeyed: false } && program.BuiltInEncoding is string?[] own && (uint)code < 256 && own[code] is string ownName
                && program.GlyphByName(ownName) is ushort builtIn)
            {
                return builtIn;
            }

            if (GlyphList.ToUnicode(name) is { Length: > 0 } text && program.Typeface.GetGlyph(char.ConvertToUtf32(text, 0)) is ushort mapped and not 0)
            {
                return mapped;
            }
        }

        return ToUnicode(code) is { Length: > 0 } unicode && !char.IsSurrogate(unicode, 0) | char.IsSurrogatePair(unicode, 0)
            ? program.Typeface.GetGlyph(char.ConvertToUtf32(unicode, 0))
            : (ushort)0;
    }

    /// <summary>Drops the font read from a dictionary that drawing has just rewritten, so it is read again when next asked.</summary>
    internal static void Forget(PdfDictionary dictionary)
    {
        Cache.Remove(dictionary);
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

        if ((standard ?? fallbackMetrics) is Standard14Font metrics && GetGlyphName(code) is string glyph && metrics.Width(glyph) is int known)
        {
            return known;
        }

        if (standard is null && noWidths && GetTypeface() is Typeface face && GetGlyphId(code) is ushort id and not 0)
        {
            // No widths at all: the embedded program's own advance (pdf.js does the same).
            return face.GetAdvance(id) * 1000.0 / face.UnitsPerEm;
        }

        return missingWidth;
    }

    /// <summary>
    /// A vertical font's metrics for a code (9.7.4.3): the vertical advance (negative, downwards) and the position vector from
    /// the glyph's horizontal origin to its vertical origin, in thousandths of text space; from <c>/W2</c>, else
    /// <c>/DW2</c> (default 880 and −1000) with the vector's x half the horizontal width. Null for a horizontal font.
    /// </summary>
    public PdfVerticalMetrics? GetVerticalMetrics(int code)
    {
        if (!IsVertical)
        {
            return null;
        }

        int cid = ToCid(code, CodeLength(code));
        if (verticalMetrics is not null && verticalMetrics.TryGetValue(cid, out (double W1, double Vx, double Vy) m))
        {
            return new PdfVerticalMetrics(m.W1, m.Vx, m.Vy);
        }

        return new PdfVerticalMetrics(defaultVertical.W1, GetWidth(code) / 2, defaultVertical.Vy);
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
            if (font.IsCidKeyed)
            {
                return font.GlyphByCid(cid) ?? 0;
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
        if (font is { IsCff: true, IsCidKeyed: false } && name is not null && font.GlyphByName(name) is ushort byName)
        {
            return byName;
        }

        if (Type == PdfFontType.TrueType || !font.IsCff)
        {
            return TrueTypeGlyph(font, code, name);
        }

        return (name is not null ? font.GlyphByName(name) : null)
            ?? (font.BuiltInEncoding is string?[] own && (uint)code < 256 && own[code] is string ownName ? font.GlyphByName(ownName) : null)
            ?? 0;
    }

    /// <summary>A simple TrueType font's glyph for a code (9.6.5.4, with pdf.js's allowances for fonts that break the rules).</summary>
    private ushort TrueTypeGlyph(FontProgram font, int code, string? name)
    {
        TrueTypeCmaps? cmaps = font.Cmaps;
        bool symbolic = (Flags & PdfFontFlags.Symbolic) != 0;
        bool hasEncoding = Dictionary.Get("Encoding") is not null;
        if ((!symbolic || hasEncoding) && GlyphList.ToUnicode(name) is { Length: > 0 } text
            && TrueTypeCmaps.TryGet(cmaps?.Unicode, char.ConvertToUtf32(text, 0), out ushort byName))
        {
            return byName;
        }

        if (name is not null && font.GlyphByName(name) is ushort named)
        {
            return named;
        }

        foreach (int key in (int[])[code, 0xF000 + code, 0xF100 + code, 0xF200 + code])
        {
            if (TrueTypeCmaps.TryGet(cmaps?.Symbol, key, out ushort glyph))
            {
                return glyph;
            }
        }

        if (cmaps?.MacRoman is { } mac)
        {
            int macCode = name is not null ? Array.IndexOf(FontEncodings.ForName("MacRomanEncoding")!, name) : -1;
            if (TrueTypeCmaps.TryGet(mac, macCode >= 0 ? macCode : code, out ushort glyph))
            {
                return glyph;
            }
        }

        if (TrueTypeCmaps.TryGet(cmaps?.Unicode, code, out ushort direct))
        {
            return direct;
        }

        // No map reaches it: with no cmap at all, the code is the glyph, as pdf.js reads such fonts.
        return (cmaps?.IsEmpty ?? true) && (uint)code < (uint)font.Typeface.GlyphCount ? (ushort)code : (ushort)0;
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

        if (IsComposite && CompositeUnicode(code) is string fromCid)
        {
            return fromCid;
        }

        string? name = GetGlyphName(code);
        if (GlyphList.ToUnicode(name, dingbats: (BaseFont ?? string.Empty).Contains("Dingbats", StringComparison.Ordinal)) is string named)
        {
            return named;
        }

        if (NumberedName(name) is int number)
        {
            return number < 256 ? (WinAnsiEncoding.Decode((byte)number) ?? (char)number).ToString() : null;
        }

        return ProgramUnicode(code);
    }

    /// <summary>
    /// A composite font's text without a ToUnicode map: codes of a Unicode CMap (Uni...-UCS2, -UTF16, -UTF32, -UTF8) are the
    /// text itself; otherwise the CID through its character collection's table (Japan1, GB1, CNS1, Korea1, KR).
    /// </summary>
    private string? CompositeUnicode(int code)
    {
        switch (unicodeCodes)
        {
            case "UTF16" when code <= 0xFFFF && code is not (>= 0xD800 and <= 0xDFFF):
                return ((char)code).ToString();
            case "UTF16":
                int high = code >> 16, low = code & 0xFFFF;
                return char.IsSurrogatePair((char)high, (char)low) ? new string([(char)high, (char)low]) : null;
            case "UTF32" when code is > 0 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF):
                return char.ConvertFromUtf32(code);
            case "UTF8":
                int length = CodeLength(code);
                byte[] bytes = [.. Enumerable.Range(0, length).Select(i => (byte)(code >> (8 * (length - 1 - i))))];
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                return text.Contains('\uFFFD', StringComparison.Ordinal) ? null : text;
        }

        return ordering is not null && PredefinedCMaps.CidToUnicode(ordering, ToCid(code, CodeLength(code))) is int cp && cp is > 0 and <= 0x10FFFF
            ? char.ConvertFromUtf32(cp)
            : null;
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
            // The Unicode cmap read backwards, else a TrueType font's post names through the glyph list.
            Dictionary<ushort, int> reverse = [];
            foreach ((int code2, ushort glyph2) in (font.Cmaps?.Unicode?.GetMappings() ?? []).OrderBy(e => e.Code))
            {
                reverse.TryAdd(glyph2, code2);
            }

            for (int g = 1; !font.IsCff && g < font.Typeface.GlyphCount; g++)
            {
                if (!reverse.ContainsKey((ushort)g) && font.Typeface.GetGlyphName((ushort)g) is string glyphName
                    && GlyphList.ToUnicode(glyphName) is { Length: > 0 } text && !char.IsSurrogate(text[0]))
                {
                    reverse[(ushort)g] = text[0];
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

    /// <summary>Reads a CIDFont's <c>/W2</c> array: <c>c [w1 vx vy ...]</c> and <c>cFirst cLast w1 vx vy</c> runs (9.7.4.3).</summary>
    private static Dictionary<int, (double, double, double)>? ReadW2(PdfArray? w)
    {
        if (w is null)
        {
            return null;
        }

        Dictionary<int, (double, double, double)> result = [];
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
                for (int k = 0; k + 2 < list.Count && result.Count < MaxEntries; k += 3)
                {
                    result[(int)Math.Clamp(first + (k / 3), 0, int.MaxValue)] = (list.GetNumber(k) ?? -1000, list.GetNumber(k + 1) ?? 0, list.GetNumber(k + 2) ?? 880);
                }

                i += 2;
            }
            else if (i + 4 < w.Count && w.GetInt(i + 1) is long last && w.GetNumber(i + 2) is double w1 && w.GetNumber(i + 3) is double vx && w.GetNumber(i + 4) is double vy)
            {
                for (long c = Math.Max(first, 0); c <= last && result.Count < MaxEntries; c++)
                {
                    result[(int)Math.Min(c, int.MaxValue)] = (w1, vx, vy);
                }

                i += 5;
            }
            else
            {
                break;
            }
        }

        return result;
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
