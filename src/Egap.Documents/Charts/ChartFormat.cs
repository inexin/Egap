using System.Collections.ObjectModel;
using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents.DrawingML;

namespace Egap.Documents.Charts;

/// <summary>How a <see cref="ChartColor"/> is given.</summary>
public enum ChartColorKind
{
    /// <summary>A fixed colour.</summary>
    Rgb,

    /// <summary>A colour of the theme, so a theme change recolours it.</summary>
    Theme,

    /// <summary>A colour of the operating system's, or one of DrawingML's named colours, with the value the file stored if any.</summary>
    Named,
}

/// <summary>One change DrawingML applies to a colour, such as <c>lumMod</c> 0.75 or <c>alpha</c> 0.5.</summary>
/// <param name="Name">The change's name in the file: <c>lumMod</c>, <c>lumOff</c>, <c>tint</c>, <c>shade</c>, <c>alpha</c> and so on.</param>
/// <param name="Value">Its value as a fraction, 1 being 100 %.</param>
public readonly record struct ChartColorChange(string Name, double Value);

/// <summary>A colour in a chart: fixed or from the theme, with the changes DrawingML applies to it, in order.</summary>
public sealed class ChartColor : IEquatable<ChartColor>
{
    private static readonly string[] ThemeNames = ["dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];

    private ChartColor(ChartColorKind kind, Rgba8 rgb, string name, IList<ChartColorChange> changes)
    {
        Kind = kind;
        Rgb = rgb;
        Name = name;
        Changes = new ReadOnlyCollection<ChartColorChange>(changes);
    }

    /// <summary>How the colour is given.</summary>
    public ChartColorKind Kind { get; }

    /// <summary>A fixed colour; for a named colour, the value the file stored, if any.</summary>
    public Rgba8 Rgb { get; }

    /// <summary>The colour's name in the file, such as <c>accent1</c>, <c>tx1</c> or <c>windowText</c>; empty for a fixed colour.</summary>
    public string Name { get; }

    /// <summary>The changes applied to the colour, in order.</summary>
    public IReadOnlyList<ChartColorChange> Changes { get; }

    /// <summary>A fixed colour. Its alpha, when not opaque, is kept as a change.</summary>
    public static ChartColor FromRgb(Rgba8 rgb)
    {
        return new(ChartColorKind.Rgb, new Rgba8(rgb.R, rgb.G, rgb.B, 255), "", rgb.A == 255 ? [] : [new ChartColorChange("alpha", rgb.A / 255.0)]);
    }

    /// <summary>A fixed colour from hex, such as "#C00000" or "C00000".</summary>
    public static ChartColor FromHex(string hex) => FromRgb(Rgba8.FromHex(hex));

    /// <summary>A theme colour, with Excel's luminance changes: darker 25 % is <c>lumMod</c> 0.75; lighter 40 % is <c>lumMod</c> 0.6 and <c>lumOff</c> 0.4.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The theme colour is not one of the twelve.</exception>
    public static ChartColor FromTheme(ThemeColor theme, double? lumMod = null, double? lumOff = null)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        List<ChartColorChange> changes = [];
        if (lumMod is { } mod)
        {
            changes.Add(new ChartColorChange("lumMod", mod));
        }

        if (lumOff is { } off)
        {
            changes.Add(new ChartColorChange("lumOff", off));
        }

        return new(ChartColorKind.Theme, default, ThemeNames[(int)theme], changes);
    }

    /// <inheritdoc/>
    public bool Equals(ChartColor? other) =>
        other is not null && Kind == other.Kind && Rgb == other.Rgb && Name == other.Name && Changes.SequenceEqual(other.Changes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ChartColor);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Rgb, Name, Changes.Count);

    /// <inheritdoc/>
    public override string ToString() =>
        (Kind == ChartColorKind.Rgb ? Rgb.ToHex()[..7] : Name) + string.Concat(Changes.Select(c => $" {c.Name} {c.Value:0.###}"));

    /// <summary>The colour inside a fill, a gradient stop or the like; null when there is none this model knows.</summary>
    internal static ChartColor? Read(XElement? holder)
    {
        if (DrawingColors.Find(holder) is not { } found)
        {
            return null;
        }

        List<ChartColorChange> changes = [];
        foreach (XElement change in found.Element.Elements())
        {
            if (DrawingColors.TryChange(change, out string name, out double value))
            {
                changes.Add(new ChartColorChange(name, value));
            }
        }

        ChartColorKind kind = found.Kind switch
        {
            DrawingColorKind.Rgb => ChartColorKind.Rgb,
            DrawingColorKind.Theme => ChartColorKind.Theme,
            _ => ChartColorKind.Named,
        };
        return new(kind, found.Rgb, found.Name, changes);
    }

    internal XElement Write()
    {
        DrawingColorKind kind = Kind switch
        {
            ChartColorKind.Rgb => DrawingColorKind.Rgb,
            ChartColorKind.Theme => DrawingColorKind.Theme,
            _ => DrawingColorKind.Named,
        };
        XElement element = DrawingColors.Element(kind, Rgb, Name);
        foreach (ChartColorChange change in Changes)
        {
            element.Add(DrawingColors.ChangeElement(change.Name, change.Value));
        }

        return element;
    }

    /// <summary>The colour in pixels against <paramref name="theme"/>, after its changes.</summary>
    internal Rgba8 Resolve(Theme theme)
    {
        DrawingColorKind kind = Kind switch
        {
            ChartColorKind.Rgb => DrawingColorKind.Rgb,
            ChartColorKind.Theme => DrawingColorKind.Theme,
            _ => DrawingColorKind.Named,
        };
        Rgba8 color = DrawingColors.Start(kind, Rgb, Name, theme);
        foreach (ChartColorChange change in Changes)
        {
            color = DrawingColors.Apply(color, change.Name, change.Value);
        }

        return color;
    }
}

/// <summary>What fills a chart element.</summary>
public enum ChartFillKind
{
    /// <summary>Nothing is set: the chart's style decides.</summary>
    Automatic,

    /// <summary>No fill.</summary>
    None,

    /// <summary>One colour.</summary>
    Solid,

    /// <summary>A gradient.</summary>
    Gradient,

    /// <summary>A pattern.</summary>
    Pattern,

    /// <summary>A picture.</summary>
    Picture,

    /// <summary>The fill of the group it belongs to.</summary>
    Group,
}

/// <summary>What draws a chart element's outline or a line.</summary>
public enum ChartLineKind
{
    /// <summary>Nothing is set: the chart's style decides.</summary>
    Automatic,

    /// <summary>No line.</summary>
    None,

    /// <summary>One colour.</summary>
    Solid,

    /// <summary>A gradient.</summary>
    Gradient,

    /// <summary>A pattern.</summary>
    Pattern,
}

/// <summary>A line's dashes.</summary>
public enum ChartLineDash
{
    /// <summary>No dashes.</summary>
    Solid,

    /// <summary>Dots.</summary>
    Dot,

    /// <summary>Dashes.</summary>
    Dash,

    /// <summary>Long dashes.</summary>
    LargeDash,

    /// <summary>Dash and dot.</summary>
    DashDot,

    /// <summary>Long dash and dot.</summary>
    LargeDashDot,

    /// <summary>Long dash and two dots.</summary>
    LargeDashDotDot,

    /// <summary>Short dashes.</summary>
    SystemDash,

    /// <summary>Short dots.</summary>
    SystemDot,

    /// <summary>Short dash and dot.</summary>
    SystemDashDot,

    /// <summary>Short dash and two dots.</summary>
    SystemDashDotDot,
}

/// <summary>One colour of a gradient.</summary>
/// <param name="Position">Where it sits, from 0 to 1.</param>
/// <param name="Color">The colour, or null when the file gives one this model does not read.</param>
public readonly record struct ChartGradientStop(double Position, ChartColor? Color);

/// <summary>The fill and outline of a chart element. What this does not name, such as shadows, stays as the file had it.</summary>
public sealed class ChartFormat
{
    private static readonly ChartNames<ChartLineDash> Dashes = new(
        (ChartLineDash.Solid, "solid"), (ChartLineDash.Dot, "dot"), (ChartLineDash.Dash, "dash"), (ChartLineDash.LargeDash, "lgDash"),
        (ChartLineDash.DashDot, "dashDot"), (ChartLineDash.LargeDashDot, "lgDashDot"), (ChartLineDash.LargeDashDotDot, "lgDashDotDot"),
        (ChartLineDash.SystemDash, "sysDash"), (ChartLineDash.SystemDot, "sysDot"), (ChartLineDash.SystemDashDot, "sysDashDot"), (ChartLineDash.SystemDashDotDot, "sysDashDotDot"));

    private readonly ChartNode properties;

    internal ChartFormat(ChartNode holder)
    {
        properties = holder["spPr"];
    }

    /// <summary>What fills the element. Setting anything other than automatic or none is done through <see cref="FillColor"/>.</summary>
    /// <exception cref="ArgumentException">The value set is not automatic or none.</exception>
    public ChartFillKind FillKind
    {
        get => Fill(properties.Find()) switch
        {
            "noFill" => ChartFillKind.None,
            "solidFill" => ChartFillKind.Solid,
            "gradFill" => ChartFillKind.Gradient,
            "blipFill" => ChartFillKind.Picture,
            "pattFill" => ChartFillKind.Pattern,
            "grpFill" => ChartFillKind.Group,
            _ => ChartFillKind.Automatic,
        };
        set
        {
            if (value is not (ChartFillKind.Automatic or ChartFillKind.None))
            {
                throw new ArgumentException("Set FillColor for a solid fill; other fills are kept from the file.", nameof(value));
            }

            SetFill(properties, value == ChartFillKind.None ? new XElement(ChartNs.A + "noFill") : null);
        }
    }

    /// <summary>The colour of a solid fill, or null for any other. Setting a colour makes the fill solid; setting null makes it automatic.</summary>
    public ChartColor? FillColor
    {
        get => ChartColor.Read(properties.Find()?.Element(ChartNs.A + "solidFill"));
        set => SetFill(properties, value is null ? null : new XElement(ChartNs.A + "solidFill", value.Write()));
    }

    /// <summary>A gradient fill's colours in order; empty for any other fill.</summary>
    public IReadOnlyList<ChartGradientStop> GradientStops => Stops(properties.Find()?.Element(ChartNs.A + "gradFill"));

    /// <summary>A linear gradient's angle in degrees clockwise from left to right, or null.</summary>
    public double? GradientAngle =>
        DrawingFills.Angle(properties.Find()?.Element(ChartNs.A + "gradFill"));

    /// <summary>A pattern fill's name in the file, such as <c>pct50</c>, or null for any other fill.</summary>
    public string? PatternName => (string?)properties.Find()?.Element(ChartNs.A + "pattFill")?.Attribute("prst");

    /// <summary>A pattern fill's foreground colour, or null.</summary>
    public ChartColor? PatternForeground => ChartColor.Read(properties.Find()?.Element(ChartNs.A + "pattFill")?.Element(ChartNs.A + "fgClr"));

    /// <summary>A pattern fill's background colour, or null.</summary>
    public ChartColor? PatternBackground => ChartColor.Read(properties.Find()?.Element(ChartNs.A + "pattFill")?.Element(ChartNs.A + "bgClr"));

    /// <summary>What draws the outline. Setting anything other than automatic or none is done through <see cref="LineColor"/>.</summary>
    /// <exception cref="ArgumentException">The value set is not automatic or none.</exception>
    public ChartLineKind LineKind
    {
        get => Fill(properties.Find()?.Element(ChartNs.A + "ln")) switch
        {
            "noFill" => ChartLineKind.None,
            "solidFill" => ChartLineKind.Solid,
            "gradFill" => ChartLineKind.Gradient,
            "pattFill" => ChartLineKind.Pattern,
            _ => ChartLineKind.Automatic,
        };
        set
        {
            if (value is not (ChartLineKind.Automatic or ChartLineKind.None))
            {
                throw new ArgumentException("Set LineColor for a solid line; other lines are kept from the file.", nameof(value));
            }

            SetFill(Line, value == ChartLineKind.None ? new XElement(ChartNs.A + "noFill") : null);
        }
    }

    /// <summary>The colour of a solid line, or null for any other. Setting a colour makes the line solid; setting null makes it automatic.</summary>
    public ChartColor? LineColor
    {
        get => ChartColor.Read(properties.Find()?.Element(ChartNs.A + "ln")?.Element(ChartNs.A + "solidFill"));
        set => SetFill(Line, value is null ? null : new XElement(ChartNs.A + "solidFill", value.Write()));
    }

    /// <summary>The line's width in points, or null when the chart's style decides.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is negative or above 1584 points.</exception>
    public double? LineWidth
    {
        get => Line.DoubleAttribute("w") / 12700;
        set
        {
            if (value is < 0 or > 1584)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A line's width is from 0 to 1584 points.");
            }

            Line.SetAttribute("w", value is { } w ? Math.Round(w * 12700).ToString("0", System.Globalization.CultureInfo.InvariantCulture) : null);
        }
    }

    /// <summary>The line's dashes, or null when the chart's style decides.</summary>
    public ChartLineDash? LineDash
    {
        get => Dashes.Parse((string?)Line.Find()?.Element(ChartNs.A + "prstDash")?.Attribute("val"));
        set
        {
            if (value is null)
            {
                Line.Find()?.Element(ChartNs.A + "prstDash")?.Remove();
                return;
            }

            Line.Child(ChartNs.A + "prstDash").Ensure().SetAttributeValue("val", Dashes.Name(value));
        }
    }

    private ChartNode Line => properties.Child(ChartNs.A + "ln");

    /// <summary>The file's name for a preset dash, such as <c>sysDash</c>; null for none.</summary>
    internal static string? DashPreset(ChartLineDash? dash) => Dashes.Name(dash);

    internal static IReadOnlyList<ChartGradientStop> Stops(XElement? gradient)
    {
        List<ChartGradientStop> stops = [];
        foreach ((double position, XElement stop) in DrawingFills.Stops(gradient))
        {
            stops.Add(new ChartGradientStop(position, ChartColor.Read(stop)));
        }

        return stops;
    }

    private static string? Fill(XElement? holder) => DrawingFills.Kind(holder);

    private static void SetFill(ChartNode holder, XElement? fill)
    {
        if (holder.Find() is { } found)
        {
            found.Elements().Where(DrawingFills.IsFill).Remove();
        }

        if (fill is not null)
        {
            ChartOrder.Insert(holder.Ensure(), fill);
        }
    }
}

/// <summary>How a chart element's text looks. What this does not name stays as the file had it.</summary>
public sealed class ChartTextFormat
{
    private readonly ChartNode body;

    internal ChartTextFormat(ChartNode body)
    {
        this.body = body;
    }

    /// <summary>The size in points, or null when it comes from further out.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is outside 1 to 4000 points.</exception>
    public double? Size
    {
        get => ChartText.Double(Properties(false)?.Attribute("sz")?.Value) / 100;
        set
        {
            if (value is < 1 or > 4000)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A font size is from 1 to 4000 points.");
            }

            Set("sz", value is { } size ? Math.Round(size * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) : null);
        }
    }

    /// <summary>Whether the text is bold, or null when it comes from further out.</summary>
    public bool? Bold
    {
        get => ChartText.Bool(Properties(false)?.Attribute("b")?.Value);
        set => Set("b", value is { } b ? (b ? "1" : "0") : null);
    }

    /// <summary>Whether the text is italic, or null when it comes from further out.</summary>
    public bool? Italic
    {
        get => ChartText.Bool(Properties(false)?.Attribute("i")?.Value);
        set => Set("i", value is { } i ? (i ? "1" : "0") : null);
    }

    /// <summary>The font's name, such as <c>Arial</c> or the theme's <c>+mn-lt</c>, or null when it comes from further out.</summary>
    public string? FontName
    {
        get => (string?)Properties(false)?.Element(ChartNs.A + "latin")?.Attribute("typeface");
        set
        {
            RunProperties().Elements(ChartNs.A + "latin").Remove();
            if (value is null)
            {
                Properties(false)?.Element(ChartNs.A + "latin")?.Remove();
                return;
            }

            XElement properties = Properties(true)!;
            if (properties.Element(ChartNs.A + "latin") is not { } latin)
            {
                latin = new XElement(ChartNs.A + "latin");
                ChartOrder.Insert(properties, latin);
            }

            latin.SetAttributeValue("typeface", value);
        }
    }

    /// <summary>The colour, or null when it comes from further out.</summary>
    public ChartColor? Color
    {
        get => ChartColor.Read(Properties(false)?.Element(ChartNs.A + "solidFill") ?? RunProperties().Elements(ChartNs.A + "solidFill").FirstOrDefault());
        set
        {
            RunProperties().Elements(ChartNs.A + "solidFill").Remove();
            Properties(false)?.Element(ChartNs.A + "solidFill")?.Remove();
            if (value is not null)
            {
                ChartOrder.Insert(Properties(true)!, new XElement(ChartNs.A + "solidFill", value.Write()));
            }
        }
    }

    /// <summary>The text's rotation in degrees clockwise, or null when the chart decides.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The angle is outside -90 to 90.</exception>
    public double? Rotation
    {
        get
        {
            // -60000000 is Excel's mark for a rotation left to the chart.
            double? angle = ChartText.Double((string?)body.Find()?.Element(ChartNs.A + "bodyPr")?.Attribute("rot"));
            return angle is null or -60000000 ? null : angle / 60000;
        }
        set
        {
            if (value is < -90 or > 90)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A text's rotation is from -90 to 90 degrees.");
            }

            if (value is not null || body.Exists)
            {
                EnsureBody().Element(ChartNs.A + "bodyPr")!.SetAttributeValue("rot", value is { } angle ? Math.Round(angle * 60000).ToString("0", System.Globalization.CultureInfo.InvariantCulture) : null);
            }
        }
    }

    private XElement EnsureBody()
    {
        XElement holder = body.Ensure();
        if (holder.Element(ChartNs.A + "bodyPr") is null)
        {
            ChartOrder.Insert(holder, new XElement(ChartNs.A + "bodyPr"));
        }

        if (holder.Element(ChartNs.A + "p") is null)
        {
            ChartOrder.Insert(holder, new XElement(ChartNs.A + "p", new XElement(ChartNs.A + "pPr", new XElement(ChartNs.A + "defRPr")), new XElement(ChartNs.A + "endParaRPr", new XAttribute("lang", "en-US"))));
        }

        return holder;
    }

    /// <summary>The first paragraph's default run properties; with a run in the paragraph, the run's where it says more.</summary>
    private XElement? Properties(bool make)
    {
        XElement? paragraph = (make ? EnsureBody() : body.Find())?.Element(ChartNs.A + "p");
        if (paragraph is null)
        {
            return null;
        }

        XElement? properties = paragraph.Element(ChartNs.A + "pPr")?.Element(ChartNs.A + "defRPr");
        if (properties is null && make)
        {
            if (paragraph.Element(ChartNs.A + "pPr") is not { } pPr)
            {
                pPr = new XElement(ChartNs.A + "pPr");
                paragraph.AddFirst(pPr);
            }

            properties = new XElement(ChartNs.A + "defRPr");
            pPr.AddFirst(properties);
        }

        return properties;
    }

    private void Set(string attribute, string? value)
    {
        if (value is not null || body.Exists)
        {
            Properties(value is not null)?.SetAttributeValue(attribute, value);
        }

        // A run that says otherwise would hide the change.
        foreach (XElement run in RunProperties())
        {
            run.SetAttributeValue(attribute, null);
        }
    }

    /// <summary>What the text's runs say for themselves, which would hide a change to the default.</summary>
    private IEnumerable<XElement> RunProperties() => body.Find()?.Elements(ChartNs.A + "p").Elements(ChartNs.A + "r").Elements(ChartNs.A + "rPr") ?? [];
}
