using Egami.Pixels;

namespace Egap.Documents.Charts;

/// <summary>Turns a chart's colours into pixels: its theme slots and colour changes, and the colours its style number gives what it leaves automatic.</summary>
internal sealed class ChartPalette(Theme theme, int style)
{
    private static readonly ThemeColor[] Accents =
    [
        ThemeColor.Accent1, ThemeColor.Accent2, ThemeColor.Accent3, ThemeColor.Accent4, ThemeColor.Accent5, ThemeColor.Accent6,
    ];

    /// <summary>The chart's dark text colour.</summary>
    public Rgba8 Dark => theme.Colors[ThemeColor.Dark1];

    /// <summary>The chart's light background colour.</summary>
    public Rgba8 Light => theme.Colors[ThemeColor.Light1];

    /// <summary>Styles 41 to 48 draw light marks on a dark chart area.</summary>
    public bool IsDarkStyle => style is >= 41 and <= 48;

    /// <summary>What axes, gridlines and the chart's frame draw with unless they say otherwise: the text colour at three quarters tint.</summary>
    public Rgba8 AutomaticLine => Mix(Dark, Light, 0.4745);

    /// <summary>The colour <paramref name="color"/> stands for.</summary>
    public Rgba8 Resolve(ChartColor color)
    {
        Rgba8 start = color.Kind switch
        {
            ChartColorKind.Rgb => color.Rgb,
            ChartColorKind.Theme => Slot(color.Name),
            _ => color.Rgb.A != 0 ? color.Rgb : Named(color.Name),
        };
        foreach (ChartColorChange change in color.Changes)
        {
            start = Apply(start, change);
        }

        return start;
    }

    /// <summary>
    /// The colour an automatic series or point takes, by its index out of <paramref name="count"/>. Style 2 and every eighth after it
    /// runs through the accents, darker and lighter each time round; style 1 runs through greys and the others through one accent's shades.
    /// </summary>
    public Rgba8 Series(int index, int count)
    {
        index = Math.Max(index, 0);
        count = Math.Max(count, index + 1);
        int family = (Math.Clamp(style, 1, 48) - 1) % 8;
        if (family == 1)
        {
            Rgba8 accent = theme.Colors[Accents[index % 6]];
            int round = index / 6;
            return round == 0 ? accent : round % 2 == 1 ? Scale(accent, Math.Max(0.35, 1 - (0.2 * round))) : Mix(accent, new Rgba8(255, 255, 255, 255), Math.Min(0.8, 0.2 * round));
        }

        Rgba8 start = family == 0 ? Dark : theme.Colors[Accents[family - 2]];
        double place = (index + 1.0) / (count + 1.0);
        return place < 0.5 ? Scale(start, 0.5 + place) : Mix(start, new Rgba8(255, 255, 255, 255), (place - 0.5) * 1.2);
    }

    /// <summary>Blends two colours: none of <paramref name="other"/> at 0, all of it at 1.</summary>
    public static Rgba8 Mix(Rgba8 color, Rgba8 other, double amount) => new(
        Byte(color.R + ((other.R - color.R) * amount)),
        Byte(color.G + ((other.G - color.G) * amount)),
        Byte(color.B + ((other.B - color.B) * amount)),
        color.A);

    private static Rgba8 Scale(Rgba8 color, double factor) => new(Byte(color.R * factor), Byte(color.G * factor), Byte(color.B * factor), color.A);

    private Rgba8 Slot(string name) => name switch
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
        _ => Dark,
    };

    private static Rgba8 Named(string name) => name switch
    {
        "window" or "white" or "btnHighlight" => new Rgba8(255, 255, 255, 255),
        "red" => new Rgba8(255, 0, 0, 255),
        "green" => new Rgba8(0, 128, 0, 255),
        "lime" => new Rgba8(0, 255, 0, 255),
        "blue" => new Rgba8(0, 0, 255, 255),
        "yellow" => new Rgba8(255, 255, 0, 255),
        "cyan" or "aqua" => new Rgba8(0, 255, 255, 255),
        "magenta" or "fuchsia" => new Rgba8(255, 0, 255, 255),
        "gray" or "grey" => new Rgba8(128, 128, 128, 255),
        "silver" => new Rgba8(192, 192, 192, 255),
        "orange" => new Rgba8(255, 165, 0, 255),
        _ => new Rgba8(0, 0, 0, 255),
    };

    private static Rgba8 Apply(Rgba8 color, ChartColorChange change)
    {
        double value = change.Value;
        switch (change.Name)
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
                    byte level = Byte((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B));
                    return new Rgba8(level, level, level, color.A);
                }

            case "inv":
                return new Rgba8((byte)(255 - color.R), (byte)(255 - color.G), (byte)(255 - color.B), color.A);
            case "comp":
                {
                    (double h, double s, double l) = ToHsl(color);
                    return FromHsl(h + 0.5, s, l, color.A);
                }
        }

        (double hue, double saturation, double luminance) = ToHsl(color);
        switch (change.Name)
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
            default:
                return color;
        }

        return FromHsl(hue, Math.Clamp(saturation, 0, 1), Math.Clamp(luminance, 0, 1), color.A);
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

    private static (double H, double S, double L) ToHsl(Rgba8 color)
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

    private static Rgba8 FromHsl(double h, double s, double l, byte alpha)
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
