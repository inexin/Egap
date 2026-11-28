using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>
/// An element's computed style with the Word mappings the reader adds to Egami.Css's typed getters: lengths in points (a CSS
/// pixel is 0.75 points and the viewport is a Letter page), font sizes in points, colours as document colours, and generic
/// font families as the faces Word documents use.
/// </summary>
internal sealed class HtmlStyle
{
    /// <summary>Points per CSS pixel.</summary>
    public const double PointsPerPixel = ImageSize.PointsPerPixel;

    /// <summary>The medium font size: 16 CSS pixels.</summary>
    public const double MediumSize = 12;

    public HtmlStyle(CssComputedStyle computed, HtmlStyle? parent, double rootSize, CssCascadedStyle? cascaded = null)
    {
        Computed = computed;
        Cascaded = cascaded;
        Parent = parent;
        FontSize = ResolveFontSize(computed, parent?.FontSize ?? MediumSize, rootSize);
        RootSize = parent is null ? FontSize : rootSize;
    }

    public CssComputedStyle Computed { get; }

    /// <summary>The cascaded style, for where a value came from (the quirks look for the user agent's own margins).</summary>
    public CssCascadedStyle? Cascaded { get; }

    /// <summary>Whether a property's value is the user agent style sheet's (or the initial value).</summary>
    public bool FromUserAgent(string property)
    {
        return Cascaded is null || !Cascaded.TryGetDeclaration(property, out _, out CssOrigin origin) || origin == CssOrigin.UserAgent;
    }

    public HtmlStyle? Parent { get; }

    /// <summary>The font size in points.</summary>
    public double FontSize { get; }

    /// <summary>The root element's font size in points, for rem.</summary>
    public double RootSize { get; }

    /// <summary>
    /// A keyword property's value, lowercased; for a value of several words, its first (<c>display: block flow</c> reads as
    /// block); null when it has none or does not start with an identifier.
    /// </summary>
    public string? Keyword(string property)
    {
        if (Computed.TryGetKeyword(property, out string? keyword))
        {
            return keyword;
        }

        if (!Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value))
        {
            return null;
        }

        for (int i = 0; i < value.Count; i++)
        {
            if (value[i] is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                return value[i] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } ? ident.Value.ToLowerInvariant() : null;
            }
        }

        return null;
    }

    /// <summary>A property's identifiers, lowercased (for text-decoration-line and the like).</summary>
    public IReadOnlyList<string> Keywords(string property)
    {
        return Computed.GetKeywords(property);
    }

    public string? Text(string property)
    {
        return Computed.GetValueText(property);
    }

    /// <summary>An integer property such as z-index; null for auto, none or no value.</summary>
    public int? Integer(string property)
    {
        return Computed.TryGetInteger(property, out int value) ? value : null;
    }

    /// <summary>A length property in points; percentages of <paramref name="percentBase"/>; null for auto, none or no value.</summary>
    public double? Length(string property, double percentBase = 0)
    {
        if (Computed.TryGetLength(property, out CssLength length))
        {
            return Points(length, percentBase);
        }

        // Unitless numbers (quirks) and calc() with one term.
        return Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value) ? Length(value, percentBase) : null;
    }

    public double? Length(IReadOnlyList<CssComponentValue> value, double percentBase = 0)
    {
        CssComponentValue? single = null;
        for (int i = 0; i < value.Count; i++)
        {
            if (value[i] is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                continue;
            }

            if (single is not null)
            {
                return null;
            }

            single = value[i];
        }

        return single is null ? null : Length(single, percentBase);
    }

    public double? Length(CssComponentValue item, double percentBase)
    {
        if (CssLength.TryParse(item, out CssLength length))
        {
            return Points(length, percentBase);
        }

        if (item is CssFunction { Name: var name } function && name.Equals("calc", StringComparison.OrdinalIgnoreCase))
        {
            // calc() with one term is common enough ("calc(100% - 2em)" is not); the rest counts as no value.
            return Length(function.Arguments, percentBase);
        }

        return null;
    }

    private double Points(CssLength length, double percentBase)
    {
        return length.Unit switch
        {
            CssUnit.Percent => length.Value / 100 * percentBase,
            CssUnit.None when length.Value == 0 => 0,
            _ => ToPoints(length, FontSize, RootSize),
        };
    }

    public static double ToPoints(CssLength length, double fontSize, double rootSize)
    {
        double v = length.Value;
        return length.Unit switch
        {
            CssUnit.Px => v * PointsPerPixel,
            CssUnit.Pt => v,
            CssUnit.Pc => v * 12,
            CssUnit.In => v * 72,
            CssUnit.Cm => v * 72 / 2.54,
            CssUnit.Mm => v * 72 / 25.4,
            CssUnit.Q => v * 72 / 101.6,
            CssUnit.Em => v * fontSize,
            CssUnit.Rem => v * rootSize,
            CssUnit.Ex or CssUnit.Ch or CssUnit.Rex or CssUnit.Rch => v * fontSize / 2,
            CssUnit.Lh or CssUnit.Rlh => v * fontSize * 1.2,
            CssUnit.Cap or CssUnit.Rcap => v * fontSize * 0.7,
            CssUnit.Ic or CssUnit.Ric => v * fontSize,

            // Viewport units of a printed page: a Letter page's 816 by 1056 CSS pixels.
            CssUnit.Vw or CssUnit.Svw or CssUnit.Lvw or CssUnit.Dvw or CssUnit.Vi => v * 816 / 100 * PointsPerPixel,
            CssUnit.Vh or CssUnit.Svh or CssUnit.Lvh or CssUnit.Dvh or CssUnit.Vb => v * 1056 / 100 * PointsPerPixel,
            CssUnit.Vmin => v * 816 / 100 * PointsPerPixel,
            CssUnit.Vmax => v * 1056 / 100 * PointsPerPixel,
            _ => v * PointsPerPixel,
        };
    }

    /// <summary>A colour property, with currentcolor resolved; null for transparent or none.</summary>
    public DocumentColor? Color(string property)
    {
        if (!Computed.TryGetColor(property, out CssColor color))
        {
            return null;
        }

        (byte r, byte g, byte b, byte a) = color.ToRgba8();
        return a < 8 ? null : DocumentColor.FromRgb(new Egami.Pixels.Rgba8(r, g, b, 255));
    }

    /// <summary>The first font family, a generic family mapped to the face Word documents use.</summary>
    public string? FontFamily()
    {
        if (!Computed.TryGetFontFamilies(out CssFontFamilyList? families) || families.Families.Count == 0)
        {
            return null;
        }

        CssFontFamily entry = families.Families[0];
        string family = entry.Name;
        string? generic = !entry.IsGeneric ? null : family.ToLowerInvariant() switch
        {
            "serif" or "ui-serif" => "Times New Roman",
            "sans-serif" or "ui-sans-serif" or "system-ui" or "-apple-system" => "Arial",
            "monospace" or "ui-monospace" => "Courier New",
            "cursive" => "Comic Sans MS",
            "fantasy" => "Impact",
            _ => null,
        };
        return generic ?? family;
    }

    public bool Bold => Computed.GetFontWeight() >= 600;

    public bool Italic => Keyword("font-style") is "italic" or "oblique";

    // The absolute-size keywords in CSS pixels. These are the sizes Egap has always used (small is 13px); CSS Fonts 4's
    // scaling factors, which CssComputedStyle.GetFontSize uses, give other sizes for all but medium and the largest three.
    private static readonly Dictionary<string, double> SizeKeywords = new(StringComparer.Ordinal)
    {
        ["xx-small"] = 9 * PointsPerPixel,
        ["x-small"] = 10 * PointsPerPixel,
        ["small"] = 13 * PointsPerPixel,
        ["medium"] = 16 * PointsPerPixel,
        ["large"] = 18 * PointsPerPixel,
        ["x-large"] = 24 * PointsPerPixel,
        ["xx-large"] = 32 * PointsPerPixel,
        ["xxx-large"] = 48 * PointsPerPixel,
    };

    private static double ResolveFontSize(CssComputedStyle computed, double parentSize, double rootSize)
    {
        if (!computed.IsSetOnElement("font-size") && computed.Parent is not null)
        {
            return parentSize;
        }

        if (computed.TryGetKeyword("font-size", out string? keyword))
        {
            return keyword switch
            {
                "larger" => parentSize * 1.2,
                "smaller" => parentSize / 1.2,
                _ => SizeKeywords.TryGetValue(keyword, out double size) ? size : parentSize,
            };
        }

        if (computed.TryGetLength("font-size", out CssLength length))
        {
            // em and ex in font-size are of the parent's size.
            return length.Unit == CssUnit.Percent ? parentSize * length.Value / 100 : Math.Max(1, ToPoints(length, parentSize, rootSize));
        }

        // A unitless number (quirks) reads as pixels.
        return computed.TryGetValue("font-size", out IReadOnlyList<CssComponentValue>? value) && CssLength.TryParse(value, out length)
            ? Math.Max(1, ToPoints(length, parentSize, rootSize))
            : parentSize;
    }
}
