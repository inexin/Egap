using Egami.Pixels;

using Egap.Documents.DrawingML;

namespace Egap.Documents.Charts;

/// <summary>Turns a chart's colours into pixels: its theme slots and colour changes, and the colours its style number gives what it leaves automatic.</summary>
internal sealed class ChartPalette(Theme theme, int style)
{
    internal static readonly ThemeColor[] Accents =
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

    /// <summary>The thin line round a 3D area with no line of its own: the background's colour.</summary>
    public Rgba8 AutomaticEdge => Light;

    /// <summary>The colour <paramref name="color"/> stands for.</summary>
    public Rgba8 Resolve(ChartColor color) => color.Resolve(theme);

    /// <summary>
    /// The colour an automatic series or point takes, by its index out of <paramref name="count"/>. Style 2 and every eighth after it
    /// runs through the accents, darker and lighter each time round; style 1 runs through greys and the others through one accent's shades.
    /// </summary>
    public Rgba8 Series(int index, int count)
    {
        Rgba8 color = Picked(Math.Max(index, 0), Math.Max(count, Math.Max(index, 0) + 1));
        return style is >= SoftStylesFrom and <= SoftStylesTo ? Mix(color, new Rgba8(255, 255, 255, 255), SoftShare) : color;
    }

    private Rgba8 Picked(int index, int count)
    {
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

    private const int SoftStylesFrom = 17;
    private const int SoftStylesTo = 24;
    private const double SoftShare = 0.4;

    /// <summary>Blends two colours: none of <paramref name="other"/> at 0, all of it at 1.</summary>
    public static Rgba8 Mix(Rgba8 color, Rgba8 other, double amount) => DrawingColors.Mix(color, other, amount);

    private static Rgba8 Scale(Rgba8 color, double factor) => DrawingColors.Scale(color, factor);
}
