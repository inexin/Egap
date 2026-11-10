using System.Globalization;
using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.Docx;

namespace Egap.Spreadsheets.Printing;

/// <summary>
/// Turns a <see cref="CellColor"/> into the colour it shows: an RGB value as it is, a theme colour from the workbook's theme
/// part (or the default Office theme), an indexed colour from the file's palette (or the default one), each with its tint
/// applied as ECMA-376 Part 1, 18.8.19 describes it.
/// </summary>
internal sealed class SheetColors
{
    /// <summary>The colours of Excel's current default Office theme, which it also uses for a file without a theme part, in SpreadsheetML's theme index order: lt1, dk1, lt2, dk2, the accents, hlink, folHlink.</summary>
    private static readonly uint[] OfficeTheme =
    [
        0xFFFFFF, 0x000000, 0xE8E8E8, 0x0E2841, 0x156082, 0xE97132, 0x196B24, 0x0F9ED5, 0xA02B93, 0x4EA72E, 0x467886, 0x96607D,
    ];

    /// <summary>The default indexed palette (ECMA-376 Part 1, 18.8.27): 0 to 63, then the system foreground and background.</summary>
    internal static readonly uint[] DefaultPalette =
    [
        0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
        0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
        0x800000, 0x008000, 0x000080, 0x808000, 0x800080, 0x008080, 0xC0C0C0, 0x808080,
        0x9999FF, 0x993366, 0xFFFFCC, 0xCCFFFF, 0x660066, 0xFF8080, 0x0066CC, 0xCCCCFF,
        0x000080, 0xFF00FF, 0xFFFF00, 0x00FFFF, 0x800080, 0x800000, 0x008080, 0x0000FF,
        0x00CCFF, 0xCCFFFF, 0xCCFFCC, 0xFFFF99, 0x99CCFF, 0xFF99CC, 0xCC99FF, 0xFFCC99,
        0x3366FF, 0x33CCCC, 0x99CC00, 0xFFCC00, 0xFF9900, 0xFF6600, 0x666699, 0x969696,
        0x003366, 0x339966, 0x003300, 0x333300, 0x993300, 0x993366, 0x333399, 0x333333,
        0x000000, 0xFFFFFF,
    ];

    private readonly uint[] theme;
    private readonly uint[] palette;

    private SheetColors(uint[] theme, uint[] palette)
    {
        this.theme = theme;
        this.palette = palette;
    }

    /// <summary>The workbook's theme for its charts; null for the default.</summary>
    public Theme? ChartTheme { get; private init; }

    /// <summary>The colours of the default theme and palette, for a workbook made in code.</summary>
    public static SheetColors Default { get; } = new(OfficeTheme, DefaultPalette);

    /// <summary>The colours of <paramref name="book"/>: its theme part and its palette where the file has them.</summary>
    public static SheetColors For(Workbook book)
    {
        uint[]? theme = null;
        Theme? chartTheme = null;
        if (book.Source is { } source)
        {
            try
            {
                if (source.Package.GetRelatedPart(source.WorkbookPart.Relationships, OpcRelationshipTypes.Theme) is { } part)
                {
                    XDocument document = part.LoadXml();
                    theme = ReadTheme(document);
                    if (document.Root is { } root)
                    {
                        chartTheme = new Document().Theme;
                        chartTheme.Read(root);
                    }
                }
            }
            catch (Exception e) when (e is System.Xml.XmlException or DocumentException or InvalidDataException)
            {
                // A damaged theme part leaves the default colours.
            }
        }

        uint[]? palette = book.Styles.Source?.Document.Root is { } styles ? ReadPalette(styles) : null;
        return theme is null && palette is null ? Default : new SheetColors(theme ?? OfficeTheme, palette ?? DefaultPalette) { ChartTheme = chartTheme };
    }

    /// <summary>The colour <paramref name="color"/> shows, or <paramref name="automatic"/> for an automatic colour.</summary>
    public Rgba8 Resolve(CellColor color, Rgba8 automatic)
    {
        uint rgb;
        switch (color.Kind)
        {
            case CellColorKind.Rgb:
                rgb = color.Value & 0xFFFFFF;
                break;
            case CellColorKind.Theme when color.Value < (uint)theme.Length:
                rgb = theme[color.Value];
                break;
            case CellColorKind.Indexed when color.Value < (uint)palette.Length:
                if (color.Value == 64)
                {
                    return automatic;
                }

                rgb = palette[color.Value];
                break;
            default:
                return automatic;
        }

        Rgba8 result = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        return color.Tint == 0 ? result : Tint(result, color.Tint);
    }

    /// <summary>
    /// The colour with its luminance moved by <paramref name="tint"/> (ECMA-376 Part 1, 18.8.19): darker by that share for a
    /// negative tint, lighter towards white for a positive one. Excel works in whole-number HLS with 240 steps, as Windows'
    /// colour dialog does, and drops the luminance's fraction; that gives the colours Excel shows to the last digit. Files hold
    /// tints in 32767ths (Excel's 50 % is 16383/32767), which Excel takes to four decimals first.
    /// </summary>
    public static Rgba8 Tint(Rgba8 color, double tint)
    {
        tint = Math.Round(tint, 4);
        (int h, int l, int s) = ToHls(color.R, color.G, color.B);
        double lum = tint < 0 ? l * (1 + tint) : (l * (1 - tint)) + (HlsMax - (HlsMax * (1 - tint)));
        (int r, int g, int b) = FromHls(h, Math.Clamp((int)lum, 0, HlsMax), s);
        return new Rgba8((byte)r, (byte)g, (byte)b, color.A);
    }

    private const int HlsMax = 240;
    private const int RgbMax = 255;

    private static (int H, int L, int S) ToHls(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int l = (((max + min) * HlsMax) + RgbMax) / (2 * RgbMax);
        if (max == min)
        {
            return (HlsMax * 2 / 3, l, 0);
        }

        int s = l <= HlsMax / 2
            ? (((max - min) * HlsMax) + ((max + min) / 2)) / (max + min)
            : (((max - min) * HlsMax) + (((2 * RgbMax) - max - min) / 2)) / ((2 * RgbMax) - max - min);
        int dr = (((max - r) * (HlsMax / 6)) + ((max - min) / 2)) / (max - min);
        int dg = (((max - g) * (HlsMax / 6)) + ((max - min) / 2)) / (max - min);
        int db = (((max - b) * (HlsMax / 6)) + ((max - min) / 2)) / (max - min);
        int h = r == max ? db - dg : g == max ? (HlsMax / 3) + dr - db : (2 * HlsMax / 3) + dg - dr;
        h = h < 0 ? h + HlsMax : h > HlsMax ? h - HlsMax : h;
        return (h, l, s);
    }

    private static (int R, int G, int B) FromHls(int h, int l, int s)
    {
        if (s == 0)
        {
            int gray = ((l * RgbMax) + (HlsMax / 2)) / HlsMax;
            return (gray, gray, gray);
        }

        int m2 = l <= HlsMax / 2 ? ((l * (HlsMax + s)) + (HlsMax / 2)) / HlsMax : l + s - (((l * s) + (HlsMax / 2)) / HlsMax);
        int m1 = (2 * l) - m2;
        return (Channel(m1, m2, h + (HlsMax / 3)), Channel(m1, m2, h), Channel(m1, m2, h - (HlsMax / 3)));

        static int Channel(int n1, int n2, int hue)
        {
            hue = hue < 0 ? hue + HlsMax : hue > HlsMax ? hue - HlsMax : hue;
            int value = hue < HlsMax / 6 ? n1 + ((((n2 - n1) * hue) + (HlsMax / 12)) / (HlsMax / 6))
                : hue < HlsMax / 2 ? n2
                : hue < HlsMax * 2 / 3 ? n1 + ((((n2 - n1) * ((HlsMax * 2 / 3) - hue)) + (HlsMax / 12)) / (HlsMax / 6))
                : n1;
            return Math.Clamp(((value * RgbMax) + (HlsMax / 2)) / HlsMax, 0, 255);
        }
    }

    /// <summary>The theme's colour scheme in SpreadsheetML's index order, or null when the part has none.</summary>
    internal static uint[]? ReadTheme(XDocument document)
    {
        XElement? scheme = document.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
        if (scheme is null)
        {
            return null;
        }

        string[] order = ["lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];
        uint[] colors = (uint[])OfficeTheme.Clone();
        for (int i = 0; i < order.Length; i++)
        {
            XElement? slot = scheme.Elements().FirstOrDefault(e => e.Name.LocalName == order[i]);
            XElement? value = slot?.Elements().FirstOrDefault();
            string? hex = value?.Name.LocalName switch
            {
                "srgbClr" => (string?)value.Attribute("val"),
                "sysClr" => (string?)value.Attribute("lastClr") ?? ((string?)value.Attribute("val") == "window" ? "FFFFFF" : "000000"),
                _ => null,
            };
            if (hex is { Length: 6 } && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            {
                colors[i] = rgb;
            }
        }

        return colors;
    }

    /// <summary>The style part's own palette (colors/indexedColors), or null when it keeps the default.</summary>
    private static uint[]? ReadPalette(XElement styles)
    {
        XElement? indexed = styles.Elements().FirstOrDefault(e => e.Name.LocalName == "colors")?.Elements().FirstOrDefault(e => e.Name.LocalName == "indexedColors");
        if (indexed is null)
        {
            return null;
        }

        uint[] palette = (uint[])DefaultPalette.Clone();
        int i = 0;
        foreach (XElement entry in indexed.Elements().Where(e => e.Name.LocalName == "rgbColor"))
        {
            if (i >= 64)
            {
                break;
            }

            string? argb = (string?)entry.Attribute("rgb");
            if (argb is { Length: 8 or 6 } && uint.TryParse(argb, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
            {
                palette[i] = value & 0xFFFFFF;
            }

            i++;
        }

        return palette;
    }
}
