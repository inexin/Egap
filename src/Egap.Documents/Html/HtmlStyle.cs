using System.Globalization;

using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>
/// An element's computed style with what the reader resolves itself (Egami.Css leaves units to the consumer): its font size,
/// lengths in points, colours, and the CSS keywords the mapping reads.
/// </summary>
internal sealed class HtmlStyle
{
    /// <summary>Points per CSS pixel.</summary>
    public const double PointsPerPixel = 0.75;

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

    /// <summary>A keyword property's value, lowercased; null when it has none or is not a single identifier.</summary>
    public string? Keyword(string property)
    {
        if (!Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value))
        {
            return null;
        }

        CssComponentValue? first = value.FirstOrDefault(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace });
        return first is CssTokenValue { Token.Kind: CssTokenKind.Ident } ident ? ident.Token.Value.ToLowerInvariant() : null;
    }

    /// <summary>A property's identifiers, lowercased (for text-decoration-line and the like).</summary>
    public IReadOnlyList<string> Keywords(string property)
    {
        if (!Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value))
        {
            return [];
        }

        return [.. value.OfType<CssTokenValue>().Where(v => v.Token.Kind == CssTokenKind.Ident).Select(v => v.Token.Value.ToLowerInvariant())];
    }

    public string? Text(string property)
    {
        return Computed.GetValueText(property);
    }

    /// <summary>A length property in points; percentages of <paramref name="percentBase"/>; null for auto, none or no value.</summary>
    public double? Length(string property, double percentBase = 0)
    {
        return Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value) ? Length(value, percentBase) : null;
    }

    public double? Length(IReadOnlyList<CssComponentValue> value, double percentBase = 0)
    {
        CssComponentValue[] items = [.. value.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
        if (items.Length != 1)
        {
            return null;
        }

        return Length(items[0], percentBase);
    }

    public double? Length(CssComponentValue item, double percentBase)
    {
        if (item is CssTokenValue { Token: var token })
        {
            switch (token.Kind)
            {
                case CssTokenKind.Number when token.Number == 0:
                    return 0;
                case CssTokenKind.Percentage:
                    return token.Number / 100 * percentBase;
            }
        }

        if (CssLength.TryParse(item, out CssLength length))
        {
            return ToPoints(length, FontSize, RootSize);
        }

        if (item is CssFunction { Name: var name } function && name.Equals("calc", StringComparison.OrdinalIgnoreCase))
        {
            // calc() with one term is common enough ("calc(100% - 2em)" is not); the rest counts as no value.
            CssComponentValue[] terms = [.. function.Arguments.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
            return terms.Length == 1 ? Length(terms[0], percentBase) : null;
        }

        return null;
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
        if (!Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value) || !CssColor.TryParse(Trim(value), out CssColor color))
        {
            return null;
        }

        if (color.IsCurrentColor)
        {
            return property == "color" ? Parent?.Color("color") : Color("color");
        }

        (byte r, byte g, byte b, byte a) = color.ToRgba8();
        return a < 8 ? null : DocumentColor.FromRgb(new Egami.Pixels.Rgba8(r, g, b, 255));
    }

    /// <summary>The font families in order, generic families mapped to the faces Word documents use.</summary>
    public string? FontFamily()
    {
        if (!Computed.TryGetValue("font-family", out IReadOnlyList<CssComponentValue>? value) || !CssFontFamilyList.TryParse(Trim(value), out CssFontFamilyList? families))
        {
            return null;
        }

        foreach (CssFontFamily entry in families.Families)
        {
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

        return null;
    }

    public bool Bold => Computed.TryGetValue("font-weight", out IReadOnlyList<CssComponentValue>? value) && CssFontWeight.TryParse(Trim(value), out CssFontWeight weight)
        ? WeightOf(weight) >= 600
        : Keyword("font-weight") is "bold" or "bolder";

    public bool Italic => Keyword("font-style") is "italic" or "oblique";

    private static double WeightOf(CssFontWeight weight)
    {
        // bolder and lighter are relative; the cascade resolves them where it can, and "bolder" alone reads as bold.
        string text = weight.ToString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number
            : text.Contains("bold", StringComparison.OrdinalIgnoreCase) ? 700 : 400;
    }

    private static IReadOnlyList<CssComponentValue> Trim(IReadOnlyList<CssComponentValue> value)
    {
        return [.. value.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
    }

    private static readonly Dictionary<string, double> SizeKeywords = new(StringComparer.OrdinalIgnoreCase)
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

        if (!computed.TryGetValue("font-size", out IReadOnlyList<CssComponentValue>? value))
        {
            return parentSize;
        }

        IReadOnlyList<CssComponentValue> items = Trim(value);
        if (items is [CssTokenValue { Token: var token }])
        {
            switch (token.Kind)
            {
                case CssTokenKind.Ident when SizeKeywords.TryGetValue(token.Value, out double size):
                    return size;
                case CssTokenKind.Ident when token.Value.Equals("larger", StringComparison.OrdinalIgnoreCase):
                    return parentSize * 1.2;
                case CssTokenKind.Ident when token.Value.Equals("smaller", StringComparison.OrdinalIgnoreCase):
                    return parentSize / 1.2;
                case CssTokenKind.Percentage:
                    return parentSize * token.Number / 100;
            }
        }

        if (items is [var single] && CssLength.TryParse(single, out CssLength length))
        {
            // em and ex in font-size are of the parent's size.
            return Math.Max(1, ToPoints(length, parentSize, rootSize));
        }

        return parentSize;
    }
}
