using System.Text;
using System.Xml.Linq;

using Egami;
using Egami.Pixels;

namespace Egap.Documents.DrawingML;

/// <summary>
/// A blip's colour changes (ECMA-376 Part 1, 20.1.8), applied to a picture's pixels in the order the file gives them: grayscale
/// (<c>grayscl</c>), black and white (<c>biLevel</c>), <c>duotone</c>, colour change (<c>clrChange</c>, PowerPoint's "set
/// transparent colour"), colour replace (<c>clrRepl</c>), brightness and contrast (<c>lum</c>), <c>hsl</c>, <c>tint</c>, and the
/// alpha changes (<c>alphaBiLevel</c>, <c>alphaCeiling</c>, <c>alphaFloor</c>, <c>alphaInv</c>, <c>alphaRepl</c>). The fixed
/// transparency (<c>alphaModFix</c>) is drawn as the picture's opacity instead.
/// </summary>
internal static class BlipAdjustments
{
    private static readonly XNamespace A = DrawingNs.A;

    private static readonly HashSet<string> Names =
    [
        "grayscl", "biLevel", "duotone", "clrChange", "clrRepl", "lum", "hsl", "tint", "alphaBiLevel", "alphaCeiling", "alphaFloor",
        "alphaInv", "alphaRepl",
    ];

    /// <summary>Whether the blip holds a change this applies to pixels.</summary>
    public static bool Has(XElement blip) => blip.Elements().Any(e => e.Name.Namespace == A && Names.Contains(e.Name.LocalName));

    /// <summary>A key for the blip's changes, to keep the changed picture by.</summary>
    public static string Key(XElement blip)
    {
        StringBuilder key = new();
        foreach (XElement change in blip.Elements())
        {
            if (change.Name.Namespace == A && Names.Contains(change.Name.LocalName))
            {
                key.Append(change.ToString(SaveOptions.DisableFormatting));
            }
        }

        return key.ToString();
    }

    /// <summary>A copy of <paramref name="source"/> with the blip's changes applied.</summary>
    public static Raster<Rgba8> Apply(Raster<Rgba8> source, XElement blip, Theme theme)
    {
        Raster<Rgba8> result = source.Copy();
        foreach (XElement change in blip.Elements())
        {
            if (change.Name.Namespace != A)
            {
                continue;
            }

            Func<Rgba8, Rgba8>? map = change.Name.LocalName switch
            {
                "grayscl" => Grayscale,
                "biLevel" => BiLevel(Fraction(change, "thresh", 0.5)),
                "duotone" => Duotone(change, theme),
                "clrChange" => ColorChange(change, theme),
                "clrRepl" => DrawingColors.Resolve(change, theme, null) is { } with ? p => new Rgba8(with.R, with.G, with.B, p.A) : null,
                "lum" => Luminance(Fraction(change, "bright", 0), Fraction(change, "contrast", 0)),
                "hsl" => Hsl((DrawingNs.Double((string?)change.Attribute("hue")) ?? 0) / 21600000, Fraction(change, "sat", 0), Fraction(change, "lum", 0)),
                "tint" => Tint((DrawingNs.Double((string?)change.Attribute("hue")) ?? 0) / 21600000, Fraction(change, "amt", 0)),
                "alphaBiLevel" => AlphaBiLevel(Fraction(change, "thresh", 0.5)),
                "alphaCeiling" => p => p.A > 0 ? p with { A = 255 } : p,
                "alphaFloor" => p => p.A < 255 ? p with { A = 0 } : p,
                "alphaInv" => AlphaInverse,
                "alphaRepl" => AlphaReplace(Fraction(change, "a", 1)),
                _ => null,
            };
            if (map is not null)
            {
                for (int y = 0; y < result.Height; y++)
                {
                    Span<Rgba8> row = result.Row(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = map(row[x]);
                    }
                }
            }
        }

        return result;
    }

    private static double Fraction(XElement element, string name, double fallback) =>
        DrawingNs.Double((string?)element.Attribute(name)) is { } value ? value / 100000 : fallback;

    /// <summary>A pixel's luma by Rec. 709's weights on its encoded values, as PowerPoint greys pictures.</summary>
    private static double Luma(Rgba8 p) => ((0.2126 * p.R) + (0.7152 * p.G) + (0.0722 * p.B)) / 255;

    private static Rgba8 Grayscale(Rgba8 p)
    {
        byte level = Byte(Luma(p) * 255);
        return new Rgba8(level, level, level, p.A);
    }

    private static Func<Rgba8, Rgba8> BiLevel(double threshold) => p => Luma(p) >= threshold ? new Rgba8(255, 255, 255, p.A) : new Rgba8(0, 0, 0, p.A);

    /// <summary>A duotone: each pixel's luminance placed between the first colour (black's) and the second (white's).</summary>
    private static Func<Rgba8, Rgba8>? Duotone(XElement change, Theme theme)
    {
        List<Rgba8> colors = [];
        foreach (XElement color in change.Elements())
        {
            if (DrawingColors.Resolve(new XElement(A + "x", new XElement(color)), theme, null) is { } resolved)
            {
                colors.Add(resolved);
            }
        }

        if (colors.Count < 2)
        {
            return null;
        }

        Rgba8 dark = colors[0];
        Rgba8 light = colors[1];
        return p =>
        {
            double t = Luma(p);
            return new Rgba8(Byte(dark.R + ((light.R - dark.R) * t)), Byte(dark.G + ((light.G - dark.G) * t)), Byte(dark.B + ((light.B - dark.B) * t)), p.A);
        };
    }

    /// <summary>A colour change: pixels of the from colour become the to colour, its transparency too; alpha is compared only with <c>useA</c>.</summary>
    private static Func<Rgba8, Rgba8>? ColorChange(XElement change, Theme theme)
    {
        if (DrawingColors.Resolve(change.Element(A + "clrFrom"), theme, null) is not { } from || DrawingColors.Resolve(change.Element(A + "clrTo"), theme, null) is not { } to)
        {
            return null;
        }

        bool useAlpha = (string?)change.Attribute("useA") is not ("0" or "false");
        return p => p.R == from.R && p.G == from.G && p.B == from.B && (!useAlpha || p.A == from.A) ? new Rgba8(to.R, to.G, to.B, (byte)(to.A * p.A / 255)) : p;
    }

    /// <summary>
    /// Brightness and contrast, each from -1 to 1: contrast spreads the levels from the middle grey (up to doubling them and
    /// more as it nears 1, flattening them as it nears -1), and brightness moves them all, half before the contrast and half
    /// after, as PowerPoint's levels measure.
    /// </summary>
    private static Func<Rgba8, Rgba8> Luminance(double bright, double contrast)
    {
        double factor = contrast >= 0 ? 1 / Math.Max(1e-3, 1 - contrast) : 1 + contrast;
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = Byte(255 * ((((i / 255.0) + (bright / 2) - 0.5) * factor) + 0.5 + (bright / 2)));
        }

        return p => new Rgba8(table[p.R], table[p.G], table[p.B], p.A);
    }

    private static Func<Rgba8, Rgba8> Hsl(double hue, double saturation, double luminance) => p =>
    {
        (double h, double s, double l) = DrawingColors.ToHsl(p);
        return DrawingColors.FromHsl(h + hue, Math.Clamp(s + saturation, 0, 1), Math.Clamp(l + luminance, 0, 1), p.A);
    };

    /// <summary>
    /// A tint: each pixel's hue turned the amount's share of the way towards the tint's hue, the short way round, its saturation
    /// and lightness kept, as PowerPoint measures; a negative amount turns it away.
    /// </summary>
    private static Func<Rgba8, Rgba8> Tint(double hue, double amount) => p =>
    {
        (double h, double s, double l) = DrawingColors.ToHsl(p);
        double turn = hue - h;
        turn -= Math.Round(turn);
        return DrawingColors.FromHsl(h + (turn * Math.Clamp(amount, -1, 1)), s, l, p.A);
    };

    /// <summary>The alpha inverted, the colour first taken premultiplied, as PowerPoint inverts: what was clear turns black.</summary>
    private static Rgba8 AlphaInverse(Rgba8 p) =>
        new((byte)(p.R * p.A / 255), (byte)(p.G * p.A / 255), (byte)(p.B * p.A / 255), (byte)(255 - p.A));

    private static Func<Rgba8, Rgba8> AlphaBiLevel(double threshold) => p => p with { A = p.A / 255.0 >= threshold ? (byte)255 : (byte)0 };

    private static Func<Rgba8, Rgba8> AlphaReplace(double alpha)
    {
        byte value = Byte(alpha * 255);
        return p => p with { A = value };
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(double.IsNaN(value) ? 0 : value), 0, 255);
}
