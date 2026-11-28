using System.Globalization;
using System.Xml.Linq;

using Egami.Pixels;

namespace Egap.Documents.DrawingML;

/// <summary>The DrawingML main namespace, <c>a:</c>, which charts, shapes and SmartArt share.</summary>
internal static class DrawingNs
{
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>EMUs in a point.</summary>
    public const double EmuPerPoint = 12700;

    /// <summary>A number as DrawingML writes it, or null.</summary>
    public static double? Double(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
}

/// <summary>How a DrawingML colour is given.</summary>
internal enum DrawingColorKind
{
    /// <summary><c>a:srgbClr</c>: a fixed colour.</summary>
    Rgb,

    /// <summary><c>a:schemeClr</c>: a theme slot.</summary>
    Theme,

    /// <summary><c>a:sysClr</c> or <c>a:prstClr</c>: a system or preset colour by name.</summary>
    Named,
}

/// <summary>A colour element found in a holder: how it is given, its fixed value, its name, and the element whose children are its changes.</summary>
internal readonly record struct DrawingColorElement(DrawingColorKind Kind, Rgba8 Rgb, string Name, XElement Element);

/// <summary>
/// DrawingML colours (ECMA-376 part 1, 20.1.2.3): finding the colour element in a fill, a stop or the like, reading its changes, writing
/// it back, and turning it into pixels against a theme.
/// </summary>
internal static class DrawingColors
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    /// <summary>The first colour element inside <paramref name="holder"/> this reader knows, or null.</summary>
    public static DrawingColorElement? Find(XElement? holder)
    {
        foreach (XElement element in holder?.Elements() ?? [])
        {
            if (element.Name.Namespace != DrawingNs.A)
            {
                continue;
            }

            string? value = (string?)element.Attribute("val");
            switch (element.Name.LocalName)
            {
                case "srgbClr":
                    return new DrawingColorElement(DrawingColorKind.Rgb, Hex(value), "", element);
                case "schemeClr":
                    return new DrawingColorElement(DrawingColorKind.Theme, default, value ?? "", element);
                case "sysClr":
                    return new DrawingColorElement(DrawingColorKind.Named, Hex((string?)element.Attribute("lastClr")), value ?? "", element);
                case "prstClr":
                    return new DrawingColorElement(DrawingColorKind.Named, default, value ?? "", element);
                case "hslClr":
                    {
                        // Hue in 60,000ths of a degree, saturation and luminance in thousandths of a percent.
                        double hue = (DrawingNs.Double((string?)element.Attribute("hue")) ?? 0) / 21600000;
                        double sat = (DrawingNs.Double((string?)element.Attribute("sat")) ?? 0) / 100000;
                        double lum = (DrawingNs.Double((string?)element.Attribute("lum")) ?? 0) / 100000;
                        return new DrawingColorElement(DrawingColorKind.Rgb, FromHsl(hue, Math.Clamp(sat, 0, 1), Math.Clamp(lum, 0, 1), 255), "", element);
                    }

                case "scrgbClr":
                    {
                        // Linear-light red, green and blue in thousandths of a percent.
                        byte Encoded(string name)
                        {
                            double c = Math.Clamp((DrawingNs.Double((string?)element.Attribute(name)) ?? 0) / 100000, 0, 1);
                            return Byte(255 * (c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055));
                        }

                        return new DrawingColorElement(DrawingColorKind.Rgb, new Rgba8(Encoded("r"), Encoded("g"), Encoded("b"), 255), "", element);
                    }
            }
        }

        return null;
    }

    /// <summary>
    /// One change under a colour element, as a fraction (1 being 100 %); false for one with no number. Hue and the like are in degrees
    /// or flags in the file; those are kept as written, a hundred-thousandth of the stored value.
    /// </summary>
    public static bool TryChange(XElement change, out string name, out double value)
    {
        name = change.Name.LocalName;
        if (DrawingNs.Double((string?)change.Attribute("val")) is { } stored)
        {
            value = stored / 100000;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>A colour element for <paramref name="kind"/>, without its changes.</summary>
    public static XElement Element(DrawingColorKind kind, Rgba8 rgb, string name) => kind switch
    {
        DrawingColorKind.Rgb => new XElement(DrawingNs.A + "srgbClr", new XAttribute("val", rgb.ToHex()[1..7])),
        DrawingColorKind.Theme => new XElement(DrawingNs.A + "schemeClr", new XAttribute("val", name)),
        _ when rgb.A != 0 => new XElement(DrawingNs.A + "sysClr", new XAttribute("val", name), new XAttribute("lastClr", rgb.ToHex()[1..7])),
        _ => new XElement(DrawingNs.A + "prstClr", new XAttribute("val", name)),
    };

    /// <summary>A change element, such as <c>&lt;a:lumMod val="75000"/&gt;</c>.</summary>
    public static XElement ChangeElement(string name, double value) =>
        new(DrawingNs.A + name, new XAttribute("val", Math.Round(value * 100000).ToString("0", CultureInfo.InvariantCulture)));

    /// <summary>
    /// The colour inside <paramref name="holder"/> in pixels, after its changes; null when it holds none. A <c>phClr</c> scheme colour
    /// stands for <paramref name="placeholder"/>, the colour a style reference gives, and is black without one.
    /// </summary>
    public static Rgba8? Resolve(XElement? holder, Theme theme, Rgba8? placeholder)
    {
        if (Find(holder) is not { } found)
        {
            return null;
        }

        Rgba8 color = found is { Kind: DrawingColorKind.Theme, Name: "phClr" } ? placeholder ?? new Rgba8(0, 0, 0, 255) : Start(found.Kind, found.Rgb, found.Name, theme);
        // The changes apply in the order the file gives them (ECMA-376 Part 1, 20.1.2.3); those without a value (comp, gray,
        // inv, gamma, invGamma) are flags.
        foreach (XElement change in found.Element.Elements())
        {
            if (TryChange(change, out string name, out double value) || change.Name.LocalName is "comp" or "gray" or "inv" or "gamma" or "invGamma")
            {
                color = Apply(color, name, value);
            }
        }

        return color;
    }

    /// <summary>The colour a colour element starts from before its changes: a theme slot, a fixed value or a named colour.</summary>
    public static Rgba8 Start(DrawingColorKind kind, Rgba8 rgb, string name, Theme theme) => kind switch
    {
        DrawingColorKind.Rgb => rgb,
        DrawingColorKind.Theme => Slot(theme, name),
        _ => rgb.A != 0 ? rgb : Named(name),
    };

    /// <summary>The theme colour a <c>schemeClr</c> names; dark 1 for a name it does not know.</summary>
    public static Rgba8 Slot(Theme theme, string name)
    {
        // A slide's colour map sends the mapped names (tx1, bg1, accent1 and the rest) to the theme colours it chooses.
        if (theme.SchemeMap is { } map && map.TryGetValue(name, out ThemeColor mapped))
        {
            return theme.Colors[mapped];
        }

        return SlotOf(theme, name);
    }

    private static Rgba8 SlotOf(Theme theme, string name) => name switch
    {
        "dk1" or "tx1" => theme.Colors[ThemeColor.Dark1],
        "lt1" or "bg1" => theme.Colors[ThemeColor.Light1],
        "dk2" or "tx2" => theme.Colors[ThemeColor.Dark2],
        "lt2" or "bg2" => theme.Colors[ThemeColor.Light2],
        "accent1" => theme.Colors[ThemeColor.Accent1],
        "accent2" => theme.Colors[ThemeColor.Accent2],
        "accent3" => theme.Colors[ThemeColor.Accent3],
        "accent4" => theme.Colors[ThemeColor.Accent4],
        "accent5" => theme.Colors[ThemeColor.Accent5],
        "accent6" => theme.Colors[ThemeColor.Accent6],
        "hlink" => theme.Colors[ThemeColor.Hyperlink],
        "folHlink" => theme.Colors[ThemeColor.FollowedHyperlink],
        _ => theme.Colors[ThemeColor.Dark1],
    };

    /// <summary>A system or preset colour by name; black for one it does not know.</summary>
    public static Rgba8 Named(string name) => DrawingPresetColors.Find(name) ?? new Rgba8(0, 0, 0, 255);

    /// <summary>Applies one colour change, such as <c>lumMod</c> 0.75; a change it does not know leaves the colour as it is.</summary>
    public static Rgba8 Apply(Rgba8 color, string name, double value)
    {
        switch (name)
        {
            case "alpha":
                return new Rgba8(color.R, color.G, color.B, Byte(255 * value));
            case "alphaMod":
                return new Rgba8(color.R, color.G, color.B, Byte(color.A * value));
            case "alphaOff":
                return new Rgba8(color.R, color.G, color.B, Byte(color.A + (255 * value)));
            case "tint":
                // Towards white, in linear light, as DrawingML's tint is.
                return Linear(color, c => 1 - ((1 - c) * value));
            case "shade":
                return Linear(color, c => c * value);
            case "gray":
                {
                    // Rec. 709's weights on the encoded values, as PowerPoint greys.
                    byte level = Byte((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B));
                    return new Rgba8(level, level, level, color.A);
                }

            case "inv":
                // The inverse in linear light, as PowerPoint inverts.
                return Linear(color, c => 1 - c);
            case "comp":
                {
                    (double h, double s, double l) = ToHsl(color);
                    return FromHsl(h + 0.5, s, l, color.A);
                }

            case "gamma":
                // The colour taken as linear light and given its sRGB encoding: lighter.
                return Channels(color, c => c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055);
            case "invGamma":
                // The colour taken as sRGB and given as linear light: darker.
                return Channels(color, c => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
            case "red" or "green" or "blue" or "redMod" or "greenMod" or "blueMod" or "redOff" or "greenOff" or "blueOff":
                return Channel(color, name, value);
        }

        (double hue, double saturation, double luminance) = ToHsl(color);
        switch (name)
        {
            case "lumMod":
                luminance *= value;
                break;
            case "lumOff":
                luminance += value;
                break;
            case "satMod":
                saturation *= value;
                break;
            case "satOff":
                saturation += value;
                break;
            case "hueMod":
                hue *= value;
                break;
            case "hueOff":
                // The file gives degrees times 60,000; the value here is a hundred-thousandth of that.
                hue += value * 100000 / 21600000;
                break;
            case "hue":
                hue = value * 100000 / 21600000;
                break;
            case "sat":
                saturation = value;
                break;
            case "lum":
                luminance = value;
                break;
            default:
                return color;
        }

        // A saturation past 1 is kept and the channels clipped, as PowerPoint does: a doubled saturation reaches pure hues.
        return FromHsl(hue, Math.Max(saturation, 0), Math.Clamp(luminance, 0, 1), color.A);
    }

    /// <summary>One channel set, scaled or moved, in linear light as DrawingML's red, green and blue changes are.</summary>
    private static Rgba8 Channel(Rgba8 color, string name, double value)
    {
        byte Change(byte channel)
        {
            double c = channel / 255.0;
            double linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            linear = name.EndsWith("Mod", StringComparison.Ordinal) ? linear * value : name.EndsWith("Off", StringComparison.Ordinal) ? linear + value : value;
            linear = Math.Clamp(linear, 0, 1);
            return Byte(255 * (linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055));
        }

        return name[0] switch
        {
            'r' => new Rgba8(Change(color.R), color.G, color.B, color.A),
            'g' => new Rgba8(color.R, Change(color.G), color.B, color.A),
            _ => new Rgba8(color.R, color.G, Change(color.B), color.A),
        };
    }

    /// <summary>Each colour channel through a curve on 0 to 1.</summary>
    private static Rgba8 Channels(Rgba8 color, Func<double, double> curve) =>
        new(Byte(255 * curve(color.R / 255.0)), Byte(255 * curve(color.G / 255.0)), Byte(255 * curve(color.B / 255.0)), color.A);

    /// <summary>Blends two colours: none of <paramref name="other"/> at 0, all of it at 1. The alpha is <paramref name="color"/>'s.</summary>
    public static Rgba8 Mix(Rgba8 color, Rgba8 other, double amount) => new(
        Byte(color.R + ((other.R - color.R) * amount)),
        Byte(color.G + ((other.G - color.G) * amount)),
        Byte(color.B + ((other.B - color.B) * amount)),
        color.A);

    /// <summary>Scales a colour's channels towards black.</summary>
    public static Rgba8 Scale(Rgba8 color, double factor) => new(Byte(color.R * factor), Byte(color.G * factor), Byte(color.B * factor), color.A);

    private static Rgba8 Hex(string? text)
    {
        if (text is { Length: 6 } && text.All(char.IsAsciiHexDigit))
        {
            return Rgba8.FromHex(text);
        }

        return default;
    }

    private static Rgba8 Linear(Rgba8 color, Func<double, double> change)
    {
        return new Rgba8(One(color.R), One(color.G), One(color.B), color.A);

        byte One(byte channel)
        {
            double c = channel / 255.0;
            double linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            double changed = Math.Clamp(change(linear), 0, 1);
            return Byte(255 * (changed <= 0.0031308 ? changed * 12.92 : (1.055 * Math.Pow(changed, 1 / 2.4)) - 0.055));
        }
    }

    internal static (double H, double S, double L) ToHsl(Rgba8 color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double d = max - min;
        if (d < 1e-9)
        {
            return (0, 0, l);
        }

        double s = d / (1 - Math.Abs((2 * l) - 1));
        double h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        return (h / 6, s, l);
    }

    internal static Rgba8 FromHsl(double h, double s, double l, byte alpha)
    {
        h -= Math.Floor(h);
        double c = (1 - Math.Abs((2 * l) - 1)) * s;
        double m = l - (c / 2);
        return new Rgba8(Byte((Part(h + (1.0 / 3)) + m) * 255), Byte((Part(h) + m) * 255), Byte((Part(h - (1.0 / 3)) + m) * 255), alpha);

        double Part(double t)
        {
            t -= Math.Floor(t);
            return t < 1.0 / 6 ? c * 6 * t : t < 0.5 ? c : t < 2.0 / 3 ? c * ((2.0 / 3) - t) * 6 : 0;
        }
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(double.IsNaN(value) ? 0 : value), 0, 255);
}
