using System.Globalization;
using System.Text;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// Styles (ODF 1.3 Part 3, 16): common styles become the model's styles, automatic styles direct formatting, default styles
/// the document's defaults, list styles list definitions, and page layouts with master pages the sections' page setup,
/// headers and footers.
/// </summary>
internal sealed partial class OdtReader
{
    private readonly Dictionary<(string Family, string Name), OdfStyle> commonStyles = [];
    private readonly Dictionary<string, XElement> defaultStyles = new(StringComparer.Ordinal);
    private Dictionary<(string Family, string Name), OdfStyle> contentAuto = [];
    private readonly Dictionary<(string Family, string Name), OdfStyle> masterAuto = [];
    private Dictionary<(string Family, string Name), OdfStyle> autoScope = [];
    private readonly Dictionary<string, string> fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> commonListStyles = new(StringComparer.Ordinal);
    private Dictionary<string, XElement> contentListStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> masterListStyles = new(StringComparer.Ordinal);
    private Dictionary<string, XElement> listScope = new(StringComparer.Ordinal);
    private readonly Dictionary<XElement, ListDefinition> listDefinitions = [];
    private readonly Dictionary<string, XElement> pageLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> masterPages = new(StringComparer.Ordinal);
    private string? firstMasterPage;
    private List? outlineList;
    private bool tabsRelativeToIndent = true;

    /// <summary>One ODF style: its element, name and family, and for a common style the model style made from it.</summary>
    private sealed class OdfStyle(XElement element, string name, string family, bool automatic)
    {
        public XElement Element { get; } = element;

        public string Name { get; } = name;

        public string Family { get; } = family;

        public bool Automatic { get; } = automatic;

        public string? ParentName => Element.Attr(StyleNs, "parent-style-name");

        public Style? Model { get; set; }

        public XElement? Properties(string kind) => Element.Element(StyleNs + kind);
    }

    private void ReadStyleTables()
    {
        // Font faces name the fonts style:font-name refers to (ODF 1.3 Part 3, 16.23).
        foreach (XElement? root in new[] { package.Styles, package.Content })
        {
            if (root?.Element(OfficeNs + "font-face-decls") is not { } decls)
            {
                continue;
            }

            foreach (XElement face in decls.Elements(StyleNs + "font-face"))
            {
                if (face.Attr(StyleNs, "name") is { } name && face.Attr(SvgNs, "font-family") is { } family)
                {
                    fonts.TryAdd(name, family.Trim().Trim('\'', '"'));
                }
            }
        }

        if (package.Styles?.Element(OfficeNs + "styles") is { } common)
        {
            foreach (XElement element in common.Elements())
            {
                Count();
                if (element.Name == StyleNs + "style")
                {
                    Add(commonStyles, element, automatic: false);
                }
                else if (element.Name == StyleNs + "default-style" && element.Attr(StyleNs, "family") is { } family)
                {
                    defaultStyles.TryAdd(family, element);
                }
                else if (element.Name == TextNs + "list-style" && element.Attr(StyleNs, "name") is { } list)
                {
                    commonListStyles.TryAdd(list, element);
                }
                else if (element.Name == TextNs + "outline-style")
                {
                    ReadOutline(element);
                }
                else if (element.Name == TextNs + "notes-configuration")
                {
                    ReadNotesConfiguration(element);
                }
            }
        }

        if (package.Styles?.Element(OfficeNs + "automatic-styles") is { } stylesAuto)
        {
            ReadAutomatic(stylesAuto, masterAuto, masterListStyles);
        }

        if (package.IsFlat)
        {
            contentAuto = masterAuto;
            contentListStyles = masterListStyles;
        }
        else if (package.Content.Element(OfficeNs + "automatic-styles") is { } auto)
        {
            ReadAutomatic(auto, contentAuto, contentListStyles);
        }

        if (package.Styles?.Element(OfficeNs + "master-styles") is { } masters)
        {
            foreach (XElement master in masters.Elements(StyleNs + "master-page"))
            {
                if (master.Attr(StyleNs, "name") is { } name && masterPages.TryAdd(name, master))
                {
                    firstMasterPage ??= name;
                }
            }
        }

        autoScope = contentAuto;
        listScope = contentListStyles;
        MakeModelStyles();
    }

    private void ReadAutomatic(XElement auto, Dictionary<(string, string), OdfStyle> styles, Dictionary<string, XElement> lists)
    {
        foreach (XElement element in auto.Elements())
        {
            Count();
            if (element.Name == StyleNs + "style")
            {
                Add(styles, element, automatic: true);
            }
            else if (element.Name == TextNs + "list-style" && element.Attr(StyleNs, "name") is { } list)
            {
                lists.TryAdd(list, element);
            }
            else if (element.Name == StyleNs + "page-layout" && element.Attr(StyleNs, "name") is { } layout)
            {
                pageLayouts.TryAdd(layout, element);
            }
        }
    }

    private static void Add(Dictionary<(string, string), OdfStyle> styles, XElement element, bool automatic)
    {
        if (element.Attr(StyleNs, "name") is { } name && element.Attr(StyleNs, "family") is { } family)
        {
            styles.TryAdd((family, name), new OdfStyle(element, name, family, automatic));
        }
    }

    /// <summary>The style <paramref name="name"/> of <paramref name="family"/>: an automatic one of the current scope, else a common one.</summary>
    private OdfStyle? Find(string family, string? name)
    {
        if (name is null)
        {
            return null;
        }

        return autoScope.GetValueOrDefault((family, name)) ?? commonStyles.GetValueOrDefault((family, name));
    }

    private OdfStyle? ParentOf(OdfStyle style)
    {
        // A style's parent is a common style (ODF 1.3 Part 3, 19.510); an automatic one is taken too, for files that do so.
        return style.ParentName is { } parent && parent != style.Name ? Find(style.Family, parent) : null;
    }

    /// <summary>The style and its parents, nearest first, stopping at a loop.</summary>
    private IEnumerable<OdfStyle> Chain(OdfStyle? style)
    {
        HashSet<OdfStyle> seen = [];
        for (OdfStyle? s = style; s is not null && seen.Add(s); s = ParentOf(s))
        {
            yield return s;
        }
    }

    /// <summary>An attribute of a properties element, from the style, its parents or the family's default style.</summary>
    private string? Inherited(OdfStyle? style, string family, string properties, XNamespace ns, string attribute)
    {
        foreach (OdfStyle s in Chain(style))
        {
            if (s.Properties(properties)?.Attr(ns, attribute) is { } value)
            {
                return value;
            }
        }

        return defaultStyles.GetValueOrDefault(family)?.Element(StyleNs + properties)?.Attr(ns, attribute);
    }

    private void MakeModelStyles()
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        if (defaultStyles.TryGetValue("paragraph", out XElement? defaults))
        {
            ApplyParagraph(defaults.Element(StyleNs + "paragraph-properties"), document.Styles.DefaultParagraphFormat, null, null);
            ApplyText(defaults.Element(StyleNs + "text-properties"), document.Styles.DefaultRunFormat, 12);
            if (OdfUnits.Length(defaults.Element(StyleNs + "paragraph-properties")?.Attr(StyleNs, "tab-stop-distance")) is > 0 and var distance)
            {
                document.Settings.DefaultTabStop = distance;
            }
        }

        // ODF's defaults when the file gives none: 12 point text (LibreOffice's), single spacing.
        document.Styles.DefaultRunFormat.Size ??= 12;

        List<OdfStyle> made = [];
        foreach (OdfStyle style in commonStyles.Values)
        {
            if (made.Count >= MaxEntries)
            {
                break;
            }

            string display = style.Element.Attr(StyleNs, "display-name") ?? DecodeName(style.Name);
            switch (style.Family)
            {
                case "paragraph":
                    {
                        BuiltInStyle? builtIn = BuiltInParagraph(display) ?? BuiltInParagraph(DecodeName(style.Name));
                        (string id, string name) = Names(builtIn, display, ids);
                        style.Model = document.Styles.AddParagraphStyle(id, name);
                        made.Add(style);
                        break;
                    }

                case "text":
                    {
                        BuiltInStyle? builtIn = BuiltInCharacter(display) ?? BuiltInCharacter(DecodeName(style.Name));
                        (string id, string name) = Names(builtIn, display, ids);
                        style.Model = document.Styles.AddCharacterStyle(id, name);
                        made.Add(style);
                        break;
                    }
            }
        }

        foreach (OdfStyle style in made)
        {
            if (ParentOf(style) is { Model: { } parent } && parent.Type == style.Model!.Type)
            {
                try
                {
                    style.Model.BasedOn = parent;
                }
                catch (ArgumentException)
                {
                    // A loop: the style stands alone.
                }
            }
        }

        foreach (OdfStyle style in made)
        {
            double parentSize = SizeOf(ParentOf(style));
            switch (style.Model)
            {
                case ParagraphStyle paragraph:
                    ApplyParagraph(style.Properties("paragraph-properties"), paragraph.ParagraphFormat, style, null);
                    ApplyText(style.Properties("text-properties"), paragraph.RunFormat, parentSize);
                    if (OdfUnits.Int(style.Element.Attr(StyleNs, "default-outline-level")) is >= 1 and <= 10 and var level)
                    {
                        paragraph.ParagraphFormat.OutlineLevel = Math.Min(level, 9) - 1;
                        if (outlineList is not null && level <= 9)
                        {
                            paragraph.ParagraphFormat.List = outlineList;
                            paragraph.ParagraphFormat.ListLevel = level - 1;
                        }
                    }

                    break;
                case CharacterStyle character:
                    ApplyText(style.Properties("text-properties"), character.RunFormat, parentSize);
                    break;
            }
        }

        foreach (OdfStyle style in made)
        {
            if (style.Model is ParagraphStyle && style.Element.Attr(StyleNs, "next-style-name") is { } next
                && Find("paragraph", next)?.Model is ParagraphStyle nextStyle && style.Model is ParagraphStyle paragraph)
            {
                paragraph.Next = nextStyle;
            }
        }

        // "Standard" (shown as "Default Paragraph Style") is what every other paragraph style builds on.
        if (Find("paragraph", "Standard")?.Model is ParagraphStyle standard)
        {
            document.Styles.SetDefault(standard);
        }
        else if (document.Styles.DefaultParagraphStyle is null)
        {
            ParagraphStyle normal = document.Styles.AddParagraphStyle(UniqueId("Normal", ids), "Normal");
            document.Styles.SetDefault(normal);
        }
    }

    private static (string Id, string Name) Names(BuiltInStyle? builtIn, string display, HashSet<string> ids)
    {
        if (builtIn is { } b && ids.Add(BuiltInStyles.Id(b)))
        {
            return (BuiltInStyles.Id(b), BuiltInStyles.Name(b));
        }

        StringBuilder id = new();
        foreach (char c in display)
        {
            if (char.IsLetterOrDigit(c))
            {
                id.Append(c);
            }
        }

        return (UniqueId(id.Length > 0 ? id.ToString() : "Style", ids), display.Length > 0 ? display : "Style");
    }

    private static string UniqueId(string id, HashSet<string> ids)
    {
        string unique = id;
        for (int i = 1; !ids.Add(unique); i++)
        {
            unique = id + i.ToString(CultureInfo.InvariantCulture);
        }

        return unique;
    }

    /// <summary>A style name with ODF's escapes undone: "Heading_20_1" is "Heading 1" (_xx_ is the character with hex code xx).</summary>
    private static string DecodeName(string name)
    {
        if (!name.Contains('_', StringComparison.Ordinal))
        {
            return name;
        }

        StringBuilder decoded = new(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            int close = name[i] == '_' ? name.IndexOf('_', i + 1) : -1;
            if (close > i + 1 && close - i <= 7 && int.TryParse(name.AsSpan(i + 1, close - i - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)
                && code is > 0 and <= 0xFFFF)
            {
                decoded.Append((char)code);
                i = close;
            }
            else
            {
                decoded.Append(name[i]);
            }
        }

        return decoded.ToString();
    }

    // LibreOffice's names for the styles Word has built in.
    private static BuiltInStyle? BuiltInParagraph(string name) => name switch
    {
        "Standard" or "Default Paragraph Style" or "Default Style" => BuiltInStyle.Normal,
        "Title" => BuiltInStyle.Title,
        "Subtitle" => BuiltInStyle.Subtitle,
        "Quotations" or "Quote" => BuiltInStyle.Quote,
        "Caption" => BuiltInStyle.Caption,
        "Header" => BuiltInStyle.Header,
        "Footer" => BuiltInStyle.Footer,
        "Footnote" or "Footnote Text" => BuiltInStyle.FootnoteText,
        "Endnote" or "Endnote Text" => BuiltInStyle.EndnoteText,
        "Preformatted Text" => BuiltInStyle.HtmlPreformatted,
        "List Paragraph" => BuiltInStyle.ListParagraph,
        _ when name.StartsWith("Heading ", StringComparison.Ordinal) && int.TryParse(name.AsSpan(8), CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 9
            => BuiltInStyle.Heading1 + (n - 1),
        _ when name.StartsWith("Contents ", StringComparison.Ordinal) && int.TryParse(name.AsSpan(9), CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 9
            => BuiltInStyle.Toc1 + (n - 1),
        _ => null,
    };

    private static BuiltInStyle? BuiltInCharacter(string name) => name switch
    {
        "Strong Emphasis" or "Strong" => BuiltInStyle.Strong,
        "Emphasis" => BuiltInStyle.Emphasis,
        "Internet link" or "Internet Link" or "Hyperlink" => BuiltInStyle.Hyperlink,
        "Visited Internet Link" or "FollowedHyperlink" => BuiltInStyle.FollowedHyperlink,
        "Footnote anchor" or "Footnote Symbol" => BuiltInStyle.FootnoteReference,
        "Endnote anchor" or "Endnote Symbol" => BuiltInStyle.EndnoteReference,
        "Source Text" => BuiltInStyle.HtmlCode,
        _ => null,
    };

    /// <summary>The font size in points a style gives text, following parents, percentages and the default style.</summary>
    private double SizeOf(OdfStyle? style, string attribute = "font-size", int depth = 0)
    {
        XNamespace ns = attribute == "font-size" ? FoNs : StyleNs;
        foreach (OdfStyle s in Chain(style))
        {
            if (s.Properties("text-properties")?.Attr(ns, attribute) is { } value)
            {
                if (OdfUnits.Length(value) is > 0 and var points)
                {
                    return points;
                }

                if (OdfUnits.Percent(value) is > 0 and var percent && depth < 32)
                {
                    return percent / 100 * SizeOf(ParentOf(s), attribute, depth + 1);
                }
            }
        }

        return document.Styles.DefaultRunFormat.Size ?? 12;
    }

    /// <summary>
    /// Applies an automatic style and the automatic styles it builds on to direct formatting, and returns the common style
    /// under them (whose model style the paragraph or run takes), or null.
    /// </summary>
    private OdfStyle? ApplyAutomatic(OdfStyle? style, ParagraphFormat? paragraph, RunFormat? run, double parentSize)
    {
        if (style is null)
        {
            return null;
        }

        if (!style.Automatic)
        {
            return style;
        }

        List<OdfStyle> automatic = [];
        OdfStyle? common = null;
        foreach (OdfStyle s in Chain(style))
        {
            if (!s.Automatic)
            {
                common = s;
                break;
            }

            automatic.Add(s);
        }

        for (int i = automatic.Count - 1; i >= 0; i--)
        {
            if (paragraph is not null)
            {
                ApplyParagraph(automatic[i].Properties("paragraph-properties"), paragraph, automatic[i], null);
            }

            if (run is not null)
            {
                ApplyText(automatic[i].Properties("text-properties"), run, parentSize);
            }
        }

        return common;
    }

    /// <summary>Maps <c>style:text-properties</c> (ODF 1.3 Part 3, 17.10) onto run formatting.</summary>
    private void ApplyText(XElement? props, RunFormat f, double parentSize)
    {
        if (props is null)
        {
            return;
        }

        foreach (XAttribute a in props.Attributes())
        {
            string v = a.Value;
            XNamespace ns = a.Name.Namespace;
            string name = a.Name.LocalName;
            if (ns == FoNs)
            {
                switch (name)
                {
                    case "font-weight":
                        f.Bold = IsBold(v);
                        break;
                    case "font-style":
                        f.Italic = v is "italic" or "oblique";
                        break;
                    case "font-size":
                        f.Size = FontSize(v, parentSize);
                        break;
                    case "color":
                        f.Color = OdfUnits.Color(v) is { } color ? DocumentColor.FromRgb(color) : null;
                        break;
                    case "background-color":
                        f.Shading = OdfUnits.Color(v) is { } back ? Shading.Solid(DocumentColor.FromRgb(back)) : v == "transparent" ? new Shading(ShadingPattern.Nil) : null;
                        break;
                    case "text-transform":
                        f.AllCaps = v == "uppercase" ? true : v == "none" ? false : null;
                        break;
                    case "font-variant":
                        f.SmallCaps = v == "small-caps";
                        break;
                    case "letter-spacing":
                        f.Spacing = v == "normal" ? 0 : OdfUnits.Length(v);
                        break;
                    case "text-shadow":
                        f.Shadow = v != "none";
                        break;
                    case "font-family":
                        f.FontAscii = f.FontHighAnsi = v.Trim().Trim('\'', '"');
                        break;
                    case "language" or "country":
                        f.Language = Language(props, FoNs, "language", "country") ?? f.Language;
                        if (props.Attr(FoNs, "language") == "zxx")
                        {
                            f.NoProof = true;
                        }

                        break;
                    case "hyphenate":
                        break;
                }
            }
            else if (ns == StyleNs)
            {
                switch (name)
                {
                    case "font-weight-complex":
                        f.BoldComplexScript = IsBold(v);
                        break;
                    case "font-style-complex":
                        f.ItalicComplexScript = v is "italic" or "oblique";
                        break;
                    case "font-size-complex":
                        f.SizeComplexScript = FontSize(v, parentSize);
                        break;
                    case "font-name":
                        if (fonts.TryGetValue(v, out string? family) || (family = v) is not null)
                        {
                            f.FontAscii = f.FontHighAnsi = family;
                        }

                        break;
                    case "font-name-asian":
                        f.FontEastAsia = fonts.GetValueOrDefault(v, v);
                        break;
                    case "font-name-complex":
                        f.FontComplexScript = fonts.GetValueOrDefault(v, v);
                        break;
                    case "font-family-asian":
                        f.FontEastAsia = v.Trim().Trim('\'', '"');
                        break;
                    case "font-family-complex":
                        f.FontComplexScript = v.Trim().Trim('\'', '"');
                        break;
                    case "text-underline-style" or "text-underline-type" or "text-underline-width" or "text-underline-mode":
                        f.Underline = Underline(props);
                        break;
                    case "text-underline-color":
                        f.UnderlineColor = OdfUnits.Color(v) is { } underline ? DocumentColor.FromRgb(underline) : DocumentColor.Auto;
                        break;
                    case "text-line-through-style" or "text-line-through-type":
                        bool through = (props.Attr(StyleNs, "text-line-through-style") ?? "none") != "none"
                            || props.Attr(StyleNs, "text-line-through-type") is "single" or "double";
                        bool twice = props.Attr(StyleNs, "text-line-through-type") == "double";
                        f.Strikethrough = through && !twice;
                        f.DoubleStrikethrough = through && twice;
                        break;
                    case "text-overline-style":
                        f.Overline = v != "none";
                        break;
                    case "font-relief":
                        f.Emboss = v == "embossed";
                        f.Imprint = v == "engraved";
                        break;
                    case "text-outline":
                        f.Outline = v == "true";
                        break;
                    case "text-position":
                        ApplyPosition(v, f, parentSize);
                        break;
                    case "text-scale":
                        f.Scale = OdfUnits.Percent(v) is > 0 and var scale ? scale : null;
                        break;
                    case "letter-kerning":
                        f.Kerning = v == "true" ? 1 : 0;
                        break;
                    case "use-window-font-color":
                        if (v == "true")
                        {
                            f.Color = DocumentColor.Auto;
                        }

                        break;
                    case "language-asian" or "country-asian":
                        f.LanguageEastAsia = Language(props, StyleNs, "language-asian", "country-asian") ?? f.LanguageEastAsia;
                        break;
                    case "language-complex" or "country-complex":
                        f.LanguageBidi = Language(props, StyleNs, "language-complex", "country-complex") ?? f.LanguageBidi;
                        break;
                }
            }
            else if (ns == TextNs && name == "display")
            {
                f.Hidden = v == "none";
            }
        }
    }

    private static bool IsBold(string value)
    {
        return value == "bold" || (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight) && weight >= 600);
    }

    private static double? FontSize(string value, double parentSize)
    {
        if (OdfUnits.Length(value) is > 0 and var points)
        {
            return Math.Min(points, 1638);
        }

        return OdfUnits.Percent(value) is > 0 and var percent ? Math.Min(percent / 100 * parentSize, 1638) : null;
    }

    private static string? Language(XElement props, XNamespace ns, string language, string country)
    {
        string? lang = props.Attr(ns, language);
        if (lang is null or "none" or "zxx" or "")
        {
            return null;
        }

        string? region = props.Attr(ns, country);
        return region is null or "none" or "" ? lang : lang + "-" + region;
    }

    private static UnderlineStyle Underline(XElement props)
    {
        string style = props.Attr(StyleNs, "text-underline-style") ?? (props.Attr(StyleNs, "text-underline-type") is "single" or "double" ? "solid" : "none");
        if (style == "none" || props.Attr(StyleNs, "text-underline-type") == "none")
        {
            return UnderlineStyle.None;
        }

        bool bold = props.Attr(StyleNs, "text-underline-width") is "bold" or "thick" or "medium";
        bool twice = props.Attr(StyleNs, "text-underline-type") == "double";
        if (props.Attr(StyleNs, "text-underline-mode") == "skip-white-space" && style == "solid" && !twice && !bold)
        {
            return UnderlineStyle.Words;
        }

        return style switch
        {
            "dotted" => bold ? UnderlineStyle.DottedHeavy : UnderlineStyle.Dotted,
            "dash" => bold ? UnderlineStyle.DashedHeavy : UnderlineStyle.Dash,
            "long-dash" => bold ? UnderlineStyle.DashLongHeavy : UnderlineStyle.DashLong,
            "dot-dash" => bold ? UnderlineStyle.DashDotHeavy : UnderlineStyle.DotDash,
            "dot-dot-dash" => bold ? UnderlineStyle.DashDotDotHeavy : UnderlineStyle.DotDotDash,
            "wave" => twice ? UnderlineStyle.WavyDouble : bold ? UnderlineStyle.WavyHeavy : UnderlineStyle.Wave,
            _ => twice ? UnderlineStyle.Double : bold ? UnderlineStyle.Thick : UnderlineStyle.Single,
        };
    }

    // style:text-position (ODF 1.3 Part 3, 20.374): "super" or "sub" or a percentage of the font height to raise, then the
    // relative size; a smaller size is a superscript or subscript, a full size a raised or lowered baseline.
    private static void ApplyPosition(string value, RunFormat f, double parentSize)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        double raise = parts[0] switch
        {
            "super" => 33,
            "sub" => -33,
            _ => OdfUnits.Percent(parts[0]) ?? 0,
        };
        double size = parts.Length > 1 ? OdfUnits.Percent(parts[1]) ?? 100 : 100;
        if (raise == 0)
        {
            f.VerticalPosition = VerticalTextPosition.Baseline;
            f.Position = null;
        }
        else if (size < 100)
        {
            f.VerticalPosition = raise > 0 ? VerticalTextPosition.Superscript : VerticalTextPosition.Subscript;
        }
        else
        {
            f.Position = Math.Clamp(raise / 100 * (f.Size ?? parentSize), -1584, 1584);
        }
    }

    /// <summary>
    /// Maps <c>style:paragraph-properties</c> (ODF 1.3 Part 3, 17.6) onto paragraph formatting. <paramref name="style"/> is the
    /// style the properties belong to, for the left margin tab stops count from; <paramref name="paragraphStyle"/> the
    /// paragraph's own, for direct formatting.
    /// </summary>
    private void ApplyParagraph(XElement? props, ParagraphFormat f, OdfStyle? style, OdfStyle? paragraphStyle)
    {
        if (props is null)
        {
            return;
        }

        foreach (XAttribute a in props.Attributes())
        {
            string v = a.Value;
            XNamespace ns = a.Name.Namespace;
            string name = a.Name.LocalName;
            if (ns == FoNs)
            {
                switch (name)
                {
                    case "text-align":
                        f.Alignment = v switch
                        {
                            "center" => ParagraphAlignment.Center,
                            "end" or "right" => ParagraphAlignment.Right,
                            "justify" => props.Attr(FoNs, "text-align-last") == "justify" ? ParagraphAlignment.Distribute : ParagraphAlignment.Justify,
                            _ => ParagraphAlignment.Left,
                        };
                        break;
                    case "margin-left":
                        f.LeftIndent = OdfUnits.Length(v) ?? f.LeftIndent;
                        break;
                    case "margin-right":
                        f.RightIndent = OdfUnits.Length(v) ?? f.RightIndent;
                        break;
                    case "text-indent":
                        f.FirstLineIndent = OdfUnits.Length(v) ?? f.FirstLineIndent;
                        break;
                    case "margin-top":
                        f.SpaceBefore = OdfUnits.Length(v) is >= 0 and var before ? before : f.SpaceBefore;
                        break;
                    case "margin-bottom":
                        f.SpaceAfter = OdfUnits.Length(v) is >= 0 and var after ? after : f.SpaceAfter;
                        break;
                    case "line-height":
                        if (OdfUnits.Percent(v) is > 0 and var percent)
                        {
                            f.LineSpacing = percent / 100;
                            f.LineSpacingRule = LineSpacingRule.Multiple;
                        }
                        else if (OdfUnits.Length(v) is > 0 and var exactly)
                        {
                            f.LineSpacing = exactly;
                            f.LineSpacingRule = LineSpacingRule.Exactly;
                        }
                        else if (v == "normal")
                        {
                            f.LineSpacing = 1;
                            f.LineSpacingRule = LineSpacingRule.Multiple;
                        }

                        break;
                    case "keep-with-next":
                        f.KeepWithNext = v == "always";
                        break;
                    case "keep-together":
                        f.KeepTogether = v == "always";
                        break;
                    case "break-before":
                        f.PageBreakBefore = v is "page" or "even-page" or "odd-page";
                        break;
                    case "widows" or "orphans":
                        f.WidowControl = OdfUnits.Int(v) is > 0;
                        break;
                    case "background-color":
                        f.Shading = OdfUnits.Color(v) is { } back ? Shading.Solid(DocumentColor.FromRgb(back)) : v == "transparent" ? new Shading(ShadingPattern.Nil) : null;
                        break;
                    case "border" or "border-top" or "border-bottom" or "border-left" or "border-right":
                        ApplyParagraphBorders(props, f);
                        break;
                    case "hyphenation-ladder-count":
                        break;
                }
            }
            else if (ns == StyleNs)
            {
                switch (name)
                {
                    case "line-height-at-least":
                        if (OdfUnits.Length(v) is > 0 and var least)
                        {
                            f.LineSpacing = least;
                            f.LineSpacingRule = LineSpacingRule.AtLeast;
                        }

                        break;
                    case "writing-mode":
                        f.Bidi = v is "rl-tb" or "rl";
                        break;
                    case "snap-to-layout-grid":
                        f.SnapToGrid = v == "true";
                        break;
                    case "contextual-spacing":
                        f.ContextualSpacing = v == "true";
                        break;
                    case "page-number":
                        break;
                }
            }
            else if (ns == TextNs && name == "number-lines")
            {
                f.SuppressLineNumbers = v == "false";
            }
        }

        if (props.Element(StyleNs + "tab-stops") is { } tabs)
        {
            f.TabStops = TabStops(tabs, style ?? paragraphStyle, props);
        }
    }

    private static void ApplyParagraphBorders(XElement props, ParagraphFormat f)
    {
        string? all = props.Attr(FoNs, "border");
        string? allWidths = props.Attr(StyleNs, "border-line-width");
        double padding = OdfUnits.Length(props.Attr(FoNs, "padding")) ?? 0;
        bool shadow = props.Attr(StyleNs, "shadow") is { } s && s != "none";
        Border? Side(string side) => OdfUnits.Border(props.Attr(FoNs, "border-" + side) ?? all, props.Attr(StyleNs, "border-line-width-" + side) ?? allWidths,
            OdfUnits.Length(props.Attr(FoNs, "padding-" + side)) ?? padding, shadow);
        f.BorderTop = Side("top") ?? f.BorderTop;
        f.BorderBottom = Side("bottom") ?? f.BorderBottom;
        f.BorderLeft = Side("left") ?? f.BorderLeft;
        f.BorderRight = Side("right") ?? f.BorderRight;
    }

    private List<TabStop> TabStops(XElement tabs, OdfStyle? style, XElement props)
    {
        // Tab positions count from the paragraph's left margin when the document says so (LibreOffice's TabsRelativeToIndent,
        // the default), and from the page margin as the model's do otherwise.
        double offset = 0;
        if (tabsRelativeToIndent)
        {
            offset = OdfUnits.Length(props.Attr(FoNs, "margin-left") ?? Inherited(style, "paragraph", "paragraph-properties", FoNs, "margin-left")) ?? 0;
        }

        List<TabStop> stops = [];
        foreach (XElement tab in tabs.Elements(StyleNs + "tab-stop"))
        {
            if (stops.Count >= 64 || OdfUnits.Length(tab.Attr(StyleNs, "position")) is not { } position)
            {
                continue;
            }

            TabAlignment alignment = tab.Attr(StyleNs, "type") switch
            {
                "center" => TabAlignment.Center,
                "right" => TabAlignment.Right,
                "char" => TabAlignment.Decimal,
                _ => TabAlignment.Left,
            };
            string? leaderText = tab.Attr(StyleNs, "leader-text");
            string? leaderStyle = tab.Attr(StyleNs, "leader-style");
            TabLeader leader = leaderText switch
            {
                "." => TabLeader.Dot,
                "-" => TabLeader.Hyphen,
                "_" => TabLeader.Underscore,
                "·" => TabLeader.MiddleDot,
                _ => leaderStyle switch
                {
                    "dotted" => TabLeader.Dot,
                    "dash" or "long-dash" => TabLeader.Hyphen,
                    "solid" => TabLeader.Underscore,
                    _ => TabLeader.None,
                },
            };
            stops.Add(new TabStop(Math.Max(0, position + offset), alignment, leader));
        }

        return stops;
    }

    /// <summary>The list definition made from a list style element, made on first use (ODF 1.3 Part 3, 16.30).</summary>
    private ListDefinition DefinitionOf(XElement listStyle)
    {
        if (listDefinitions.TryGetValue(listStyle, out ListDefinition? definition))
        {
            return definition;
        }

        if (listDefinitions.Count >= MaxEntries)
        {
            throw new DocumentLimitException($"The document has more than {MaxEntries} list styles.");
        }

        definition = document.Lists.AddDefinition(++definitionIds);
        definition.Name = listStyle.Attr(StyleNs, "display-name") ?? (listStyle.Attr(StyleNs, "name") is { } n ? DecodeName(n) : null);
        listDefinitions[listStyle] = definition;
        foreach (XElement levelStyle in listStyle.Elements())
        {
            if (OdfUnits.Int(levelStyle.Attr(TextNs, "level")) is not { } oneBased || oneBased is < 1 or > 10)
            {
                continue;
            }

            int index = Math.Min(oneBased, 9) - 1;
            ListLevel level = definition.Levels[index];
            switch (levelStyle.Name.LocalName)
            {
                case "list-level-style-number" or "outline-level-style":
                    {
                        level.NumberStyle = OdfUnits.NumberStyleOf(levelStyle.Attr(StyleNs, "num-format"));
                        level.StartAt = Math.Clamp(OdfUnits.Int(levelStyle.Attr(TextNs, "start-value")) ?? 1, 0, 32767);
                        int shown = Math.Clamp(OdfUnits.Int(levelStyle.Attr(TextNs, "display-levels")) ?? 1, 1, index + 1);
                        StringBuilder text = new(levelStyle.Attr(StyleNs, "num-prefix") ?? "");
                        if (level.NumberStyle != NumberStyle.None)
                        {
                            for (int l = index + 1 - shown; l <= index; l++)
                            {
                                text.Append('%').Append(l + 1);
                                if (l < index)
                                {
                                    text.Append('.');
                                }
                            }
                        }

                        text.Append(levelStyle.Attr(StyleNs, "num-suffix") ?? "");
                        level.LevelText = text.ToString();
                        break;
                    }

                case "list-level-style-bullet":
                    level.NumberStyle = NumberStyle.Bullet;
                    level.LevelText = levelStyle.Attr(TextNs, "bullet-char") is { Length: > 0 } bullet ? bullet : "•";
                    break;
                case "list-level-style-image":
                    level.NumberStyle = NumberStyle.Bullet;
                    level.LevelText = "•";
                    break;
                default:
                    continue;
            }

            if (Find("text", levelStyle.Attr(TextNs, "style-name")) is { } labelStyle)
            {
                foreach (OdfStyle s in Chain(labelStyle).Reverse())
                {
                    ApplyText(s.Properties("text-properties"), level.RunFormat, 12);
                }
            }

            ApplyText(levelStyle.Element(StyleNs + "text-properties"), level.RunFormat, 12);
            if (level.RunFormat.Size is not null && levelStyle.Element(StyleNs + "text-properties")?.Attr(FoNs, "font-size") is { } relative
                && OdfUnits.Percent(relative) is not null)
            {
                // A bullet's relative size is of the paragraph's text, which the level does not know: it is left to the text.
                level.RunFormat.Size = null;
            }

            ApplyLevelPosition(levelStyle.Element(StyleNs + "list-level-properties"), level);
        }

        return definition;
    }

    private static void ApplyLevelPosition(XElement? props, ListLevel level)
    {
        if (props is null)
        {
            level.Suffix = ListLabelSuffix.Tab;
            return;
        }

        level.Alignment = props.Attr(FoNs, "text-align") switch
        {
            "center" => ParagraphAlignment.Center,
            "end" or "right" => ParagraphAlignment.Right,
            _ => ParagraphAlignment.Left,
        };
        if (props.Attr(TextNs, "list-level-position-and-space-mode") == "label-alignment"
            && props.Element(StyleNs + "list-level-label-alignment") is { } alignment)
        {
            level.Suffix = alignment.Attr(TextNs, "label-followed-by") switch
            {
                "space" => ListLabelSuffix.Space,
                "nothing" => ListLabelSuffix.Nothing,
                _ => ListLabelSuffix.Tab,
            };
            double left = OdfUnits.Length(alignment.Attr(FoNs, "margin-left")) ?? 0;
            level.ParagraphFormat.LeftIndent = left;
            level.ParagraphFormat.FirstLineIndent = OdfUnits.Length(alignment.Attr(FoNs, "text-indent")) ?? 0;
            if (level.Suffix == ListLabelSuffix.Tab && OdfUnits.Length(alignment.Attr(TextNs, "list-tab-stop-position")) is { } tab && Math.Abs(tab - left) > 0.01)
            {
                level.ParagraphFormat.TabStops = [new TabStop(Math.Max(0, tab))];
            }
        }
        else
        {
            // The older mode: the label starts space-before from the margin, the text min-label-width after it.
            double before = OdfUnits.Length(props.Attr(TextNs, "space-before")) ?? 0;
            double width = OdfUnits.Length(props.Attr(TextNs, "min-label-width")) ?? 0;
            level.Suffix = ListLabelSuffix.Tab;
            level.ParagraphFormat.LeftIndent = before + width;
            level.ParagraphFormat.FirstLineIndent = -width;
        }
    }

    // The outline style numbers headings (ODF 1.3 Part 3, 16.32): a list only when some level shows a number or text.
    private void ReadOutline(XElement outline)
    {
        bool numbered = outline.Elements(TextNs + "outline-level-style").Any(l =>
            l.Attr(StyleNs, "num-format") is { Length: > 0 } || l.Attr(StyleNs, "num-prefix") is { Length: > 0 } || l.Attr(StyleNs, "num-suffix") is { Length: > 0 });
        if (numbered)
        {
            ListDefinition definition = DefinitionOf(outline);
            definition.Kind = ListKind.Multilevel;
            outlineList = document.Lists.Add(++listIds, definition);
        }
    }

    private void ReadNotesConfiguration(XElement configuration)
    {
        bool endnotes = configuration.Attr(TextNs, "note-class") == "endnote";
        NumberStyle style = OdfUnits.NumberStyleOf(configuration.Attr(StyleNs, "num-format") ?? "1");
        int start = Math.Clamp((OdfUnits.Int(configuration.Attr(TextNs, "start-value")) ?? 0) + 1, 1, 32767);
        if (endnotes)
        {
            document.Settings.EndnoteNumberStyle = style == NumberStyle.None ? NumberStyle.LowerRoman : style;
            document.Settings.EndnoteStartAt = start;
        }
        else
        {
            document.Settings.FootnoteNumberStyle = style == NumberStyle.None ? NumberStyle.Decimal : style;
            document.Settings.FootnoteStartAt = start;
        }
    }

    /// <summary>
    /// Gives <paramref name="section"/> the page setup, headers and footers of master page <paramref name="name"/> (ODF 1.3
    /// Part 3, 16.9). A master whose next master is another one is a first page: its headers are the first page's and the
    /// next master's the rest.
    /// </summary>
    private void ApplyMaster(Section section, string name)
    {
        if (!masterPages.TryGetValue(name, out XElement? master))
        {
            return;
        }

        XElement rest = master;
        if (master.Attr(StyleNs, "next-style-name") is { } next && next != name && masterPages.TryGetValue(next, out XElement? following))
        {
            rest = following;
            section.PageSetup.DifferentFirstPage = true;
        }

        ApplyPageLayout(section.PageSetup, rest);
        Dictionary<(string, string), OdfStyle> savedStyles = autoScope;
        Dictionary<string, XElement> savedLists = listScope;
        autoScope = masterAuto;
        listScope = masterListStyles;
        try
        {
            ReadHeaderFooter(section, rest, "header", HeaderFooterKind.HeaderPrimary, HeaderFooterKind.HeaderEven, HeaderFooterKind.HeaderFirst);
            ReadHeaderFooter(section, rest, "footer", HeaderFooterKind.FooterPrimary, HeaderFooterKind.FooterEven, HeaderFooterKind.FooterFirst);
            if (rest != master)
            {
                ReadFirst(section, master, "header", HeaderFooterKind.HeaderFirst);
                ReadFirst(section, master, "footer", HeaderFooterKind.FooterFirst);
            }
        }
        finally
        {
            autoScope = savedStyles;
            listScope = savedLists;
        }
    }

    private void ReadHeaderFooter(Section section, XElement master, string what, HeaderFooterKind primary, HeaderFooterKind even, HeaderFooterKind first)
    {
        if (Shown(master.Element(StyleNs + what)) is { } main)
        {
            ReadStory(main, section.HeadersFooters.GetOrAdd(primary).Blocks);
            headerFootersSeen.Add(primary);
        }
        else if (headerFootersSeen.Contains(primary))
        {
            // An empty one, so the section does not show the previous section's.
            section.HeadersFooters.GetOrAdd(primary).Blocks.AddTrusted(new Paragraph(document));
        }

        if (Shown(master.Element(StyleNs + what + "-left")) is { } left)
        {
            document.Settings.EvenAndOddHeaders = true;
            ReadStory(left, section.HeadersFooters.GetOrAdd(even).Blocks);
        }
        else if (document.Settings.EvenAndOddHeaders && Shown(master.Element(StyleNs + what)) is { } same)
        {
            ReadStory(same, section.HeadersFooters.GetOrAdd(even).Blocks);
        }

        if (Shown(master.Element(StyleNs + what + "-first")) is { } firstPage)
        {
            section.PageSetup.DifferentFirstPage = true;
            ReadStory(firstPage, section.HeadersFooters.GetOrAdd(first).Blocks);
        }
    }

    private void ReadFirst(Section section, XElement master, string what, HeaderFooterKind kind)
    {
        HeaderFooter story = section.HeadersFooters.GetOrAdd(kind);
        story.Blocks.Clear();
        if (Shown(master.Element(StyleNs + what)) is { } content)
        {
            ReadStory(content, story.Blocks);
        }

        if (story.Blocks.Count == 0)
        {
            story.Blocks.AddTrusted(new Paragraph(document));
        }
    }

    private static XElement? Shown(XElement? element) => element is not null && element.Attr(StyleNs, "display") != "false" ? element : null;

    private void ApplyPageLayout(PageSetup setup, XElement master)
    {
        if (master.Attr(StyleNs, "page-layout-name") is not { } layoutName || !pageLayouts.TryGetValue(layoutName, out XElement? layout))
        {
            return;
        }

        if (layout.Attr(StyleNs, "page-usage") == "mirrored")
        {
            document.Settings.MirrorMargins = true;
        }

        XElement? props = layout.Element(StyleNs + "page-layout-properties");
        if (props is not null)
        {
            setup.PageWidth = OdfUnits.Length(props.Attr(FoNs, "page-width")) is > 0 and var width ? width : setup.PageWidth;
            setup.PageHeight = OdfUnits.Length(props.Attr(FoNs, "page-height")) is > 0 and var height ? height : setup.PageHeight;
            setup.Orientation = props.Attr(StyleNs, "print-orientation") switch
            {
                "landscape" => PageOrientation.Landscape,
                "portrait" => PageOrientation.Portrait,
                _ => setup.Orientation,
            };
            setup.MarginLeft = Margin(props, "left") ?? setup.MarginLeft;
            setup.MarginRight = Margin(props, "right") ?? setup.MarginRight;
            setup.MarginTop = Margin(props, "top") ?? 0;
            setup.MarginBottom = Margin(props, "bottom") ?? 0;
            if (props.Attr(StyleNs, "num-format") is { Length: > 0 } format)
            {
                setup.PageNumberStyle = OdfUnits.NumberStyleOf(format);
            }

            if (props.Attr(StyleNs, "writing-mode") is "rl-tb" or "rl")
            {
                setup.Bidi = true;
            }

            if (props.Element(StyleNs + "columns") is { } columns)
            {
                ApplyColumns(columns, setup);
            }
        }

        // ODF's page margins hold the header and footer, Word's are where the body starts: the header's height moves the body
        // down, and the header sits at the page margin. The height counts the spacing to the body, as LibreOffice writes it.
        double top = setup.MarginTop ?? 0;
        double bottom = setup.MarginBottom ?? 0;
        setup.HeaderDistance = Math.Min(top, 36);
        setup.FooterDistance = Math.Min(bottom, 36);
        if (Shown(master.Element(StyleNs + "header")) is not null
            && layout.Element(StyleNs + "header-style")?.Element(StyleNs + "header-footer-properties") is { } header)
        {
            setup.HeaderDistance = top;
            setup.MarginTop = top + HeaderHeight(header, "margin-bottom");
        }

        if (Shown(master.Element(StyleNs + "footer")) is not null
            && layout.Element(StyleNs + "footer-style")?.Element(StyleNs + "header-footer-properties") is { } footer)
        {
            setup.FooterDistance = bottom;
            setup.MarginBottom = bottom + HeaderHeight(footer, "margin-top");
        }
    }

    private static double HeaderHeight(XElement properties, string spacing)
    {
        double height = OdfUnits.Length(properties.Attr(FoNs, "min-height") ?? properties.Attr(SvgNs, "height")) ?? 0;
        return Math.Max(height, OdfUnits.Length(properties.Attr(FoNs, spacing)) ?? 0);
    }

    private static double? Margin(XElement props, string side)
    {
        return OdfUnits.Length(props.Attr(FoNs, "margin-" + side) ?? props.Attr(FoNs, "margin")) is { } margin ? Math.Max(0, margin) : null;
    }

    private static void ApplyColumns(XElement columns, PageSetup setup)
    {
        int count = Math.Clamp(OdfUnits.Int(columns.Attr(FoNs, "column-count")) ?? 1, 1, 45);
        setup.ColumnCount = count;
        if (count <= 1)
        {
            return;
        }

        setup.ColumnSpacing = OdfUnits.Length(columns.Attr(FoNs, "column-gap")) ?? 0;
        setup.ColumnSeparator = columns.Element(StyleNs + "column-sep") is { } separator && separator.Attr(StyleNs, "style") != "none";
        List<XElement> each = [.. columns.Elements(StyleNs + "column")];
        if (each.Count == count && each.Select(c => c.Attr(StyleNs, "rel-width")).Distinct().Count() > 1)
        {
            // Unequal columns: the relative widths and the gaps around each column (ODF 1.3 Part 3, 17.13).
            List<TextColumn> list = [];
            double total = each.Sum(c => OdfUnits.Int(c.Attr(StyleNs, "rel-width")?.TrimEnd('*')) ?? 0);
            double textWidth = Math.Max(1, (setup.PageWidth ?? 612) - (setup.MarginLeft ?? 72) - (setup.MarginRight ?? 72));
            for (int i = 0; i < each.Count && total > 0; i++)
            {
                double share = (OdfUnits.Int(each[i].Attr(StyleNs, "rel-width")?.TrimEnd('*')) ?? 0) / total * textWidth;
                double start = OdfUnits.Length(each[i].Attr(FoNs, "start-indent")) ?? 0;
                double end = OdfUnits.Length(each[i].Attr(FoNs, "end-indent")) ?? 0;
                double after = i + 1 < each.Count ? end + (OdfUnits.Length(each[i + 1].Attr(FoNs, "start-indent")) ?? 0) : 0;
                list.Add(new TextColumn(Math.Max(1, share - start - end), after));
            }

            if (list.Count == count)
            {
                setup.EqualColumnWidths = false;
                setup.Columns = list;
            }
        }
    }

    private void ReadSettings()
    {
        XElement? settings = package.Settings?.Element(OfficeNs + "settings");
        if (settings is null)
        {
            return;
        }

        foreach (XElement item in settings.Descendants(ConfigNs + "config-item"))
        {
            if (item.Attr(ConfigNs, "name") == "TabsRelativeToIndent" && OdfUnits.Bool(item.Value) is { } relative)
            {
                tabsRelativeToIndent = relative;
                return;
            }
        }
    }

    private void ReadMeta()
    {
        XElement? meta = package.Meta?.Element(OfficeNs + "meta");
        if (meta is null)
        {
            return;
        }

        DocumentProperties properties = document.Properties;
        List<string> keywords = [];
        int custom = 0;
        foreach (XElement element in meta.Elements())
        {
            string value = element.Value;
            switch (element.Name.LocalName)
            {
                case "title" when element.Name.Namespace == DcNs:
                    properties.Title = value;
                    break;
                case "subject" when element.Name.Namespace == DcNs:
                    properties.Subject = value;
                    break;
                case "description" when element.Name.Namespace == DcNs:
                    properties.Comments = value;
                    break;
                case "creator" when element.Name.Namespace == DcNs:
                    properties.LastSavedBy = value;
                    break;
                case "date" when element.Name.Namespace == DcNs:
                    properties.Saved = OdfUnits.Date(value);
                    break;
                case "initial-creator":
                    properties.Author = value;
                    break;
                case "creation-date":
                    properties.Created = OdfUnits.Date(value);
                    break;
                case "print-date":
                    properties.Printed = OdfUnits.Date(value);
                    break;
                case "keyword":
                    keywords.Add(value);
                    break;
                case "user-defined" when custom++ < MaxEntries:
                    if (element.Attr(MetaNs, "name") is { Length: > 0 } name)
                    {
                        properties.SetCustom(name, value);
                    }

                    break;
            }
        }

        if (keywords.Count > 0)
        {
            properties.Keywords = string.Join(", ", keywords);
        }
    }
}
