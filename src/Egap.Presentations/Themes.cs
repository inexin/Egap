using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents;

namespace Egap.Presentations;

/// <summary>
/// The colour names slides use for text and backgrounds, which a <see cref="ColorMap"/> maps to theme colours
/// (ECMA-376 Part 1, 19.3.1.6, <c>p:clrMap</c>).
/// </summary>
public enum ColorMapSlot
{
    /// <summary>Background 1 (<c>bg1</c>), the light background in most themes.</summary>
    Background1,

    /// <summary>Text 1 (<c>tx1</c>), the dark text in most themes.</summary>
    Text1,

    /// <summary>Background 2 (<c>bg2</c>).</summary>
    Background2,

    /// <summary>Text 2 (<c>tx2</c>).</summary>
    Text2,

    /// <summary>Accent 1.</summary>
    Accent1,

    /// <summary>Accent 2.</summary>
    Accent2,

    /// <summary>Accent 3.</summary>
    Accent3,

    /// <summary>Accent 4.</summary>
    Accent4,

    /// <summary>Accent 5.</summary>
    Accent5,

    /// <summary>Accent 6.</summary>
    Accent6,

    /// <summary>Hyperlink (<c>hlink</c>).</summary>
    Hyperlink,

    /// <summary>Followed hyperlink (<c>folHlink</c>).</summary>
    FollowedHyperlink,
}

/// <summary>
/// A colour map: which theme colour each of the twelve colour names of a slide stands for. A master has one; a layout or slide
/// may override it, as a dark title slide swaps the background and text colours.
/// </summary>
public sealed class ColorMap
{
    private static readonly string[] Attributes = ["bg1", "tx1", "bg2", "tx2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];

    private static readonly ThemeColor[] Standard =
    [
        ThemeColor.Light1, ThemeColor.Dark1, ThemeColor.Light2, ThemeColor.Dark2, ThemeColor.Accent1, ThemeColor.Accent2,
        ThemeColor.Accent3, ThemeColor.Accent4, ThemeColor.Accent5, ThemeColor.Accent6, ThemeColor.Hyperlink, ThemeColor.FollowedHyperlink,
    ];

    private readonly ThemeColor[] colors;

    internal ColorMap(XElement element)
    {
        colors = (ThemeColor[])Standard.Clone();
        for (int i = 0; i < Attributes.Length; i++)
        {
            if (ThemeColorOf((string?)element.Attribute(Attributes[i])) is ThemeColor color)
            {
                colors[i] = color;
            }
        }
    }

    private ColorMap(ThemeColor[] colors)
    {
        this.colors = colors;
    }

    /// <summary>The standard map, light backgrounds and dark text, as PowerPoint's own masters have it.</summary>
    public static ColorMap Default { get; } = new((ThemeColor[])Standard.Clone());

    /// <summary>The theme colour <paramref name="slot"/> stands for.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The slot is not one of <see cref="ColorMapSlot"/>'s values.</exception>
    public ThemeColor this[ColorMapSlot slot] => Enum.IsDefined(slot) ? colors[(int)slot] : throw new ArgumentOutOfRangeException(nameof(slot));

    private static ThemeColor? ThemeColorOf(string? name) => name switch
    {
        "dk1" => ThemeColor.Dark1,
        "lt1" => ThemeColor.Light1,
        "dk2" => ThemeColor.Dark2,
        "lt2" => ThemeColor.Light2,
        "accent1" => ThemeColor.Accent1,
        "accent2" => ThemeColor.Accent2,
        "accent3" => ThemeColor.Accent3,
        "accent4" => ThemeColor.Accent4,
        "accent5" => ThemeColor.Accent5,
        "accent6" => ThemeColor.Accent6,
        "hlink" => ThemeColor.Hyperlink,
        "folHlink" => ThemeColor.FollowedHyperlink,
        _ => null,
    };
}

/// <summary>
/// A slide master's theme (<c>a:theme</c>): its twelve colours and its heading and body fonts, as read from the file. It is
/// read-only here; the theme part is written back as it was.
/// </summary>
public sealed class PresentationTheme
{
    private readonly Theme theme;

    private PresentationTheme(Theme theme, string partName)
    {
        this.theme = theme;
        PartName = partName;
    }

    /// <summary>The theme's name, such as <c>Office Theme</c>.</summary>
    public string Name => theme.Name;

    /// <summary>The name of the theme's part, such as <c>/ppt/theme/theme1.xml</c>.</summary>
    public string PartName { get; }

    /// <summary>The major (headings) font for Latin text.</summary>
    public string MajorLatinFont => theme.MajorLatinFont;

    /// <summary>The minor (body) font for Latin text.</summary>
    public string MinorLatinFont => theme.MinorLatinFont;

    /// <summary>The colour of <paramref name="color"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The colour is not one of <see cref="ThemeColor"/>'s values.</exception>
    public Rgba8 GetColor(ThemeColor color) => theme.Colors[color];

    /// <summary>The colour a slide's colour name stands for under <paramref name="map"/>.</summary>
    /// <exception cref="ArgumentNullException">The map is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The slot is not one of <see cref="ColorMapSlot"/>'s values.</exception>
    public Rgba8 GetColor(ColorMapSlot slot, ColorMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return GetColor(map[slot]);
    }

    /// <summary>The font a <see cref="ThemeFont"/> names, or null when the theme leaves it empty.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The font is not one of <see cref="ThemeFont"/>'s values.</exception>
    public string? GetFont(ThemeFont font) => theme.GetFont(font);

    internal static PresentationTheme Read(XElement root, string partName)
    {
        // Egap.Documents' theme reads the DrawingML theme part as Word and Excel files have it; PowerPoint's is the same part.
        Theme theme = new Document().Theme;
        theme.Read(root);
        return new PresentationTheme(theme, partName);
    }
}
