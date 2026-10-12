using System.Collections.Concurrent;
using System.Globalization;

namespace Egap.Pdf.Fonts;

/// <summary>
/// One of the 14 standard fonts (9.6.2.2) every PDF reader has, with its metrics from Adobe's Core14 AFM files, which ship
/// unchanged inside Egap.Pdf with their licence (see THIRD-PARTY-NOTICES.md). The twelve Latin fonts encode text with
/// <see cref="WinAnsiEncoding"/>; Symbol and ZapfDingbats use their built-in encodings, by code. Widths are in thousandths
/// of the font size, as in a PDF /Widths array.
/// </summary>
internal sealed class Standard14Font
{
    /// <summary>The 14 base font names (9.6.2.2), in Annex D's order.</summary>
    public static readonly string[] Names =
    [
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic", "Helvetica", "Helvetica-Bold", "Helvetica-Oblique",
        "Helvetica-BoldOblique", "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique", "Symbol", "ZapfDingbats",
    ];

    /// <summary>
    /// Other names writers use for the standard fonts, as pdf.js and PDFBox accept them: Windows TrueType names and the
    /// abbreviations of AcroForm default resources.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["Arial"] = "Helvetica",
        ["Arial,Bold"] = "Helvetica-Bold",
        ["Arial,Italic"] = "Helvetica-Oblique",
        ["Arial,BoldItalic"] = "Helvetica-BoldOblique",
        ["ArialMT"] = "Helvetica",
        ["Arial-BoldMT"] = "Helvetica-Bold",
        ["Arial-ItalicMT"] = "Helvetica-Oblique",
        ["Arial-BoldItalicMT"] = "Helvetica-BoldOblique",
        ["Helv"] = "Helvetica",
        ["HeBo"] = "Helvetica-Bold",
        ["TimesNewRoman"] = "Times-Roman",
        ["TimesNewRoman,Bold"] = "Times-Bold",
        ["TimesNewRoman,Italic"] = "Times-Italic",
        ["TimesNewRoman,BoldItalic"] = "Times-BoldItalic",
        ["TimesNewRomanPSMT"] = "Times-Roman",
        ["TiRo"] = "Times-Roman",
        ["CourierNew"] = "Courier",
        ["CourierNew,Bold"] = "Courier-Bold",
        ["CourierNew,Italic"] = "Courier-Oblique",
        ["CourierNew,BoldItalic"] = "Courier-BoldOblique",
        ["CourierNewPSMT"] = "Courier",
        ["Cour"] = "Courier",
        ["Symb"] = "Symbol",
        ["ZaDb"] = "ZapfDingbats",
    };

    private static readonly ConcurrentDictionary<string, Standard14Font> Loaded = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> widthsByName;
    private readonly int[] widthsByCode;

    private Standard14Font(string name, Dictionary<string, int> widthsByName, string?[] builtInEncoding, Dictionary<string, string> header)
    {
        Name = name;
        this.widthsByName = widthsByName;
        IsSymbolic = name is "Symbol" or "ZapfDingbats";
        widthsByCode = new int[256];
        for (int code = 0; code < 256; code++)
        {
            string? glyph = IsSymbolic ? builtInEncoding[code] : WinAnsiEncoding.GlyphName((byte)code);
            widthsByCode[code] = glyph is not null && widthsByName.TryGetValue(glyph, out int width) ? width : 0;
        }

        double Number(string key, double fallback)
        {
            return header.TryGetValue(key, out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
        }

        Ascent = Number("Ascender", 0);
        Descent = Number("Descender", 0);
        CapHeight = Number("CapHeight", Ascent);
        XHeight = Number("XHeight", 0);
        ItalicAngle = Number("ItalicAngle", 0);
        StemV = Number("StdVW", 80);
        IsFixedPitch = header.GetValueOrDefault("IsFixedPitch") == "true";
        double[] box = (header.GetValueOrDefault("FontBBox") ?? "0 0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        BoundingBox = new PdfRectangle(box[0], box[1], box[2], box[3]);
    }

    /// <summary>The base font name, such as <c>Helvetica</c>.</summary>
    public string Name { get; }

    /// <summary>Whether the font uses its own encoding rather than WinAnsi: Symbol and ZapfDingbats.</summary>
    public bool IsSymbolic { get; }

    public double Ascent { get; }

    public double Descent { get; }

    public double CapHeight { get; }

    public double XHeight { get; }

    public double ItalicAngle { get; }

    public double StemV { get; }

    public bool IsFixedPitch { get; }

    /// <summary>The font's bounding box, in thousandths of the font size.</summary>
    public PdfRectangle BoundingBox { get; }

    /// <summary>
    /// The standard font a base font name means: one of <see cref="Names"/>, or a common alias of one; null for any other
    /// font. A subset prefix (<c>ABCDEF+</c>) is ignored.
    /// </summary>
    public static Standard14Font? Get(string? baseFont)
    {
        if (baseFont is null)
        {
            return null;
        }

        string name = baseFont.Length > 7 && baseFont[6] == '+' ? baseFont[7..] : baseFont;
        name = Aliases.GetValueOrDefault(name, name);
        return Array.IndexOf(Names, name) < 0 ? null : Loaded.GetOrAdd(name, Load);
    }

    /// <summary>The width of a code in the font's encoding, in thousandths of the font size; 0 for a code with no glyph.</summary>
    public int Width(byte code)
    {
        return widthsByCode[code];
    }

    /// <summary>The width of a glyph by its name, or null when the font has no such glyph.</summary>
    public int? Width(string glyphName)
    {
        return widthsByName.TryGetValue(glyphName, out int width) ? width : null;
    }

    /// <summary>
    /// Encodes text for this font: WinAnsi for the Latin fonts; null when a character has no code there, or the font is
    /// Symbol or ZapfDingbats, whose text is given by code.
    /// </summary>
    public byte[]? Encode(string text)
    {
        return IsSymbolic ? null : WinAnsiEncoding.TryEncode(text);
    }

    /// <summary>The width of encoded bytes at a font size, in user space units, with optional character and word spacing.</summary>
    public double Measure(ReadOnlySpan<byte> codes, double fontSize, double characterSpacing = 0, double wordSpacing = 0)
    {
        double width = 0;
        foreach (byte code in codes)
        {
            width += (widthsByCode[code] / 1000.0 * fontSize) + characterSpacing + (code == 0x20 ? wordSpacing : 0);
        }

        return width;
    }

    /// <summary>The width of text at a font size, or null when the font cannot encode it.</summary>
    public double? Measure(string text, double fontSize)
    {
        return Encode(text) is byte[] codes ? Measure(codes, fontSize) : null;
    }

    private static Standard14Font Load(string name)
    {
        using Stream stream = typeof(Standard14Font).Assembly.GetManifestResourceStream($"Egap.Pdf.Fonts.{name}.afm")
            ?? throw new InvalidOperationException($"The metrics of {name} are missing from Egap.Pdf.");
        using StreamReader reader = new(stream, System.Text.Encoding.ASCII);
        Dictionary<string, string> header = new(StringComparer.Ordinal);
        Dictionary<string, int> widths = new(StringComparer.Ordinal);
        string?[] encoding = new string?[256];
        bool inMetrics = false;
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.StartsWith("StartCharMetrics", StringComparison.Ordinal))
            {
                inMetrics = true;
                continue;
            }

            if (line.StartsWith("EndCharMetrics", StringComparison.Ordinal))
            {
                inMetrics = false;
                continue;
            }

            if (!inMetrics)
            {
                int space = line.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0)
                {
                    header.TryAdd(line[..space], line[(space + 1)..].Trim());
                }

                continue;
            }

            // C 32 ; WX 278 ; N space ; B 0 0 0 0 ;
            int code = -1;
            int width = 0;
            string? glyph = null;
            foreach (string part in line.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = part.Split(' ', 2, StringSplitOptions.TrimEntries);
                switch (fields[0])
                {
                    case "C":
                        code = int.Parse(fields[1], CultureInfo.InvariantCulture);
                        break;
                    case "WX":
                        width = (int)double.Parse(fields[1], CultureInfo.InvariantCulture);
                        break;
                    case "N":
                        glyph = fields[1];
                        break;
                }
            }

            if (glyph is not null)
            {
                widths.TryAdd(glyph, width);
                if (code is >= 0 and < 256)
                {
                    encoding[code] = glyph;
                }
            }
        }

        return new Standard14Font(name, widths, encoding, header);
    }
}
