using System.Globalization;
using System.Text;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// Styles (ODF 1.3 Part 3, 16): the model's paragraph and character styles become common styles, the document's defaults the
/// default styles, list definitions list styles, and direct formatting automatic styles, shared by all that look alike.
/// </summary>
internal sealed partial class OdtWriter
{
    /// <summary>The automatic styles of one file: <c>content.xml</c>'s, or <c>styles.xml</c>'s for headers and footers.</summary>
    private sealed class AutoStyles(string prefix)
    {
        private readonly Dictionary<string, string> byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);

        public List<XElement> Elements { get; } = [];

        /// <summary>The name of a style like <paramref name="style"/>, which has an empty name: an earlier one's, or this one's, newly named.</summary>
        public string Add(XElement style, string family)
        {
            string key = style.ToString(SaveOptions.DisableFormatting);
            if (byKey.TryGetValue(key, out string? name))
            {
                return name;
            }

            // Counted by prefix: date, time and number styles share theirs.
            string start = FamilyPrefix(family);
            int count = counts.GetValueOrDefault(start) + 1;
            counts[start] = count;
            name = prefix + start + count.ToString(CultureInfo.InvariantCulture);
            style.SetAttributeValue(StyleNs + "name", name);
            Elements.Add(style);
            byKey[key] = name;
            return name;
        }

        private static string FamilyPrefix(string family) => family switch
        {
            "paragraph" => "P",
            "text" => "T",
            "table" => "Table",
            "table-column" => "Co",
            "table-row" => "Ro",
            "table-cell" => "Ce",
            "graphic" => "fr",
            "section" => "Sect",
            "page-layout" => "pm",
            "list-style" => "L",
            "date-style" or "time-style" or "number-style" or "percentage-style" or "currency-style" or "boolean-style" or "text-style" => "N",
            _ => "S",
        };
    }

    private readonly AutoStyles contentStyles = new("");
    private readonly AutoStyles masterStyles = new("M");
    private AutoStyles scope;

    private readonly Dictionary<Style, string> styleNames = [];
    private readonly HashSet<string> usedStyleNames = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> fontFaces = new(StringComparer.Ordinal);
    private readonly Dictionary<ListDefinition, string> listStyleNames = [];
    private readonly List<XElement> listStyles = [];
    private ParagraphStyle? defaultParagraphStyle;
    private List? outlineList;

    /// <summary>The model's built-in styles by id, and the names LibreOffice gives them.</summary>
    private static readonly Dictionary<string, (string Name, string Display)> BuiltInNames = MakeBuiltInNames();

    private static Dictionary<string, (string, string)> MakeBuiltInNames()
    {
        Dictionary<string, (string, string)> names = new(StringComparer.OrdinalIgnoreCase);
        void Add(BuiltInStyle style, string display) => names[BuiltInStyles.Id(style)] = (Encode(display), display);
        Add(BuiltInStyle.Title, "Title");
        Add(BuiltInStyle.Subtitle, "Subtitle");
        Add(BuiltInStyle.Quote, "Quotations");
        Add(BuiltInStyle.Caption, "Caption");
        Add(BuiltInStyle.Header, "Header");
        Add(BuiltInStyle.Footer, "Footer");
        Add(BuiltInStyle.FootnoteText, "Footnote");
        Add(BuiltInStyle.EndnoteText, "Endnote");
        Add(BuiltInStyle.HtmlPreformatted, "Preformatted Text");
        Add(BuiltInStyle.ListParagraph, "List Paragraph");
        Add(BuiltInStyle.Strong, "Strong Emphasis");
        Add(BuiltInStyle.Emphasis, "Emphasis");
        Add(BuiltInStyle.Hyperlink, "Internet link");
        Add(BuiltInStyle.FollowedHyperlink, "Visited Internet Link");
        Add(BuiltInStyle.FootnoteReference, "Footnote anchor");
        Add(BuiltInStyle.EndnoteReference, "Endnote anchor");
        Add(BuiltInStyle.HtmlCode, "Source Text");
        for (int i = 0; i < 9; i++)
        {
            Add(BuiltInStyle.Heading1 + i, "Heading " + (i + 1).ToString(CultureInfo.InvariantCulture));
            Add(BuiltInStyle.Toc1 + i, "Contents " + (i + 1).ToString(CultureInfo.InvariantCulture));
        }

        return names;
    }

    /// <summary>
    /// A style name as ODF's style names may be (an NCName): each character that may not stand in one is written _xx_, its
    /// hex code, as LibreOffice writes "Heading_20_1" for "Heading 1".
    /// </summary>
    private static string Encode(string name)
    {
        StringBuilder encoded = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool ok = char.IsLetter(c) || c == '_' || (i > 0 && (char.IsDigit(c) || c is '-' or '.'));
            if (ok && !char.IsSurrogate(c))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('_').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append('_');
            }
        }

        return encoded.Length > 0 ? encoded.ToString() : "Style";
    }

    /// <summary>Names every common style, picks the heading numbering, before the body refers to them.</summary>
    private void PrepareStyles()
    {
        defaultParagraphStyle = document.Styles.DefaultParagraphStyle;
        if (defaultParagraphStyle is not null)
        {
            styleNames[defaultParagraphStyle] = "Standard";
            usedStyleNames.Add("paragraph:Standard");
        }

        foreach (Style style in document.Styles)
        {
            if (style is not (ParagraphStyle or CharacterStyle) || styleNames.ContainsKey(style))
            {
                continue;
            }

            string family = style is ParagraphStyle ? "paragraph" : "text";
            string name = BuiltInNames.TryGetValue(style.Id, out (string Name, string) builtIn) ? builtIn.Name : Encode(style.Name);
            string unique = name;
            for (int i = 1; !usedStyleNames.Add(family + ":" + unique); i++)
            {
                unique = name + "_" + i.ToString(CultureInfo.InvariantCulture);
            }

            styleNames[style] = unique;
        }

        // The list the heading styles number with is ODF's outline numbering.
        foreach (Style style in document.Styles)
        {
            if (style is ParagraphStyle { ParagraphFormat: { OutlineLevel: >= 0 and <= 8 } format } && format.List is { } list && list != List.None
                && document.Lists.Contains(list) && (format.ListLevel ?? 0) == format.OutlineLevel)
            {
                outlineList = list;
                break;
            }
        }
    }

    /// <summary>The common style a paragraph's or run's style is written as, or null for none.</summary>
    private string? StyleName(Style? style)
    {
        if (style is { IsInDocument: true } && styleNames.TryGetValue(style, out string? name))
        {
            return name;
        }

        return null;
    }

    private string ParagraphStyleName(ParagraphStyle? style)
    {
        return StyleName(style) ?? "Standard";
    }

    /// <summary>An automatic style of <paramref name="family"/> in the current file, shared with any other like it.</summary>
    private string Auto(string family, string? parent, string? master, params XElement?[] properties)
    {
        XElement style = new(StyleNs + "style", new XAttribute(StyleNs + "name", ""), new XAttribute(StyleNs + "family", family));
        if (parent is not null)
        {
            style.Add(new XAttribute(StyleNs + "parent-style-name", parent));
        }

        if (master is not null)
        {
            style.Add(new XAttribute(StyleNs + "master-page-name", master));
        }

        foreach (XElement? p in properties)
        {
            if (p is not null && (p.HasAttributes || p.HasElements))
            {
                style.Add(p);
            }
        }

        return scope.Add(style, family);
    }

    private string FontFace(string family)
    {
        string clean = Clean(family);
        fontFaces.TryAdd(clean, clean);
        return clean;
    }

    private XElement FontFaces()
    {
        XElement decls = new(OfficeNs + "font-face-decls");
        foreach (string family in fontFaces.Keys)
        {
            decls.Add(new XElement(StyleNs + "font-face", new XAttribute(StyleNs + "name", family),
                new XAttribute(SvgNs + "font-family", family.Contains(' ', StringComparison.Ordinal) ? "'" + family + "'" : family)));
        }

        return decls;
    }

    // ---- Common styles ----

    private XElement WriteCommonStyles()
    {
        AutoStyles saved = scope;
        scope = masterStyles;
        XElement styles = new(OfficeNs + "styles");
        XElement defaults = new(StyleNs + "default-style", new XAttribute(StyleNs + "family", "paragraph"));
        XElement? paragraphDefaults = ParagraphProperties(document.Styles.DefaultParagraphFormat, 0);
        paragraphDefaults ??= new XElement(StyleNs + "paragraph-properties");
        paragraphDefaults.SetAttributeValue(StyleNs + "tab-stop-distance", Pt(document.Settings.DefaultTabStop));
        defaults.Add(paragraphDefaults);
        if (TextProperties(document.Styles.DefaultRunFormat, null) is { } textDefaults)
        {
            defaults.Add(textDefaults);
        }

        styles.Add(defaults);
        if (defaultParagraphStyle is null)
        {
            styles.Add(new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "Standard"), new XAttribute(StyleNs + "family", "paragraph"),
                new XAttribute(StyleNs + "class", "text")));
        }

        foreach (Style style in document.Styles)
        {
            if (style is ParagraphStyle or CharacterStyle && styleNames.TryGetValue(style, out string? name))
            {
                styles.Add(CommonStyle(style, name));
            }
        }

        styles.Add(OutlineStyle());
        styles.Add(listStyles);
        styles.Add(NotesConfiguration(NoteKind.Footnote), NotesConfiguration(NoteKind.Endnote));
        scope = saved;
        return styles;
    }

    private XElement CommonStyle(Style style, string name)
    {
        bool paragraph = style is ParagraphStyle;
        XElement element = new(StyleNs + "style", new XAttribute(StyleNs + "name", name));
        string display = BuiltInNames.TryGetValue(style.Id, out (string, string Display) builtIn) ? builtIn.Display
            : style == defaultParagraphStyle ? "Default Paragraph Style" : style.Name;
        if (Encode(display) != name || display != name)
        {
            element.Add(new XAttribute(StyleNs + "display-name", Clean(display)));
        }

        element.Add(new XAttribute(StyleNs + "family", paragraph ? "paragraph" : "text"));
        if (StyleName(style.BasedOn) is { } parent && style.BasedOn!.Type == style.Type)
        {
            element.Add(new XAttribute(StyleNs + "parent-style-name", parent));
        }

        if (style is ParagraphStyle p)
        {
            if (StyleName(p.Next) is { } next)
            {
                element.Add(new XAttribute(StyleNs + "next-style-name", next));
            }

            if (p.ParagraphFormat.OutlineLevel is >= 0 and <= 8 and int level)
            {
                element.Add(new XAttribute(StyleNs + "default-outline-level", Int(level + 1)));
            }

            if (p.ParagraphFormat.List is { } list && list != List.None && list != outlineList && document.Lists.Contains(list))
            {
                element.Add(new XAttribute(StyleNs + "list-style-name", ListStyleName(list.Definition)));
            }

            element.Add(new XAttribute(StyleNs + "class", p.ParagraphFormat.OutlineLevel is >= 0 and <= 8 ? "chapter" : "text"));
            element.Add(ParagraphProperties(p.ParagraphFormat, IndentOf(p, null)));
            element.Add(TextProperties(p.RunFormat, null));
        }
        else if (style is CharacterStyle c)
        {
            element.Add(TextProperties(c.RunFormat, null));
        }

        return element;
    }

    /// <summary>The left indent a style or a paragraph's direct formatting gives, which ODF's tab positions count from.</summary>
    private double IndentOf(ParagraphStyle? style, ParagraphFormat? direct)
    {
        if (direct?.LeftIndent is { } own)
        {
            return own;
        }

        for (Style? s = style; s is not null; s = s.BasedOn)
        {
            if (s is ParagraphStyle { ParagraphFormat.LeftIndent: { } indent })
            {
                return indent;
            }
        }

        return document.Styles.DefaultParagraphFormat.LeftIndent ?? 0;
    }

    private XElement NotesConfiguration(NoteKind note)
    {
        bool end = note == NoteKind.Endnote;
        DocumentSettings settings = document.Settings;
        return new XElement(TextNs + "notes-configuration",
            new XAttribute(TextNs + "note-class", end ? "endnote" : "footnote"),
            new XAttribute(StyleNs + "num-format", NumFormat(end ? settings.EndnoteNumberStyle : settings.FootnoteNumberStyle)),
            new XAttribute(TextNs + "start-value", Int(Math.Max(0, (end ? settings.EndnoteStartAt : settings.FootnoteStartAt) - 1))),
            end ? null : new XAttribute(TextNs + "footnotes-position", "page"),
            end ? null : new XAttribute(TextNs + "start-numbering-at", "document"));
    }

    // ---- Lists ----

    private string ListStyleName(ListDefinition definition)
    {
        if (listStyleNames.TryGetValue(definition, out string? name))
        {
            return name;
        }

        string display = definition.Name is { Length: > 0 } named ? named : "List " + Int(definition.Id);
        name = Encode("WWNum" + Int(definition.Id));
        listStyleNames[definition] = name;
        XElement style = new(TextNs + "list-style", new XAttribute(StyleNs + "name", name), new XAttribute(StyleNs + "display-name", Clean(display)));
        for (int i = 0; i < definition.Levels.Count && i < 10; i++)
        {
            style.Add(LevelStyle(definition.Levels[i], i, outline: false));
        }

        listStyles.Add(style);
        return name;
    }

    private XElement OutlineStyle()
    {
        XElement style = new(TextNs + "outline-style", new XAttribute(StyleNs + "name", "Outline"));
        for (int i = 0; i < 10; i++)
        {
            if (outlineList is not null && i < 9)
            {
                style.Add(LevelStyle(outlineList.GetLevel(i), i, outline: true));
            }
            else
            {
                style.Add(new XElement(TextNs + "outline-level-style", new XAttribute(TextNs + "level", Int(i + 1)), new XAttribute(StyleNs + "num-format", "")));
            }
        }

        return style;
    }

    /// <summary>One level of a list style (ODF 1.3 Part 3, 16.31 to 16.33), its label from the level text.</summary>
    private XElement LevelStyle(ListLevel level, int index, bool outline)
    {
        XElement element;
        if (level.NumberStyle == NumberStyle.Bullet && !outline)
        {
            string bullet = level.LevelText is { Length: > 0 } text ? char.IsSurrogatePair(text, 0) ? text[..2] : text[..1] : "•";
            element = new XElement(TextNs + "list-level-style-bullet", new XAttribute(TextNs + "level", Int(index + 1)),
                new XAttribute(TextNs + "bullet-char", Clean(bullet)));
        }
        else
        {
            (string prefix, string suffix, int shown) = LabelParts(level.LevelText ?? "", index);
            element = new XElement(TextNs + (outline ? "outline-level-style" : "list-level-style-number"), new XAttribute(TextNs + "level", Int(index + 1)));
            if (prefix.Length > 0)
            {
                element.Add(new XAttribute(StyleNs + "num-prefix", Clean(prefix)));
            }

            if (suffix.Length > 0)
            {
                element.Add(new XAttribute(StyleNs + "num-suffix", Clean(suffix)));
            }

            element.Add(new XAttribute(StyleNs + "num-format", shown == 0 ? "" : NumFormat(level.NumberStyle)));
            if (shown > 1)
            {
                element.Add(new XAttribute(TextNs + "display-levels", Int(shown)));
            }

            if (level.StartAt != 1)
            {
                // A level that counts from 0 needs ODF 1.4; before it, levels count from 1 at the least.
                element.Add(new XAttribute(TextNs + "start-value", Int(Math.Max(version == "1.4" ? 0 : 1, level.StartAt))));
            }
        }

        XElement properties = new(StyleNs + "list-level-properties", new XAttribute(TextNs + "list-level-position-and-space-mode", "label-alignment"));
        if (level.Alignment is ParagraphAlignment.Center or ParagraphAlignment.Right)
        {
            properties.Add(new XAttribute(FoNs + "text-align", level.Alignment == ParagraphAlignment.Center ? "center" : "end"));
        }

        double left = level.ParagraphFormat.LeftIndent ?? 0;
        XElement alignment = new(StyleNs + "list-level-label-alignment", new XAttribute(TextNs + "label-followed-by", level.Suffix switch
        {
            ListLabelSuffix.Space => "space",
            ListLabelSuffix.Nothing => "nothing",
            _ => "listtab",
        }));
        if (level.Suffix == ListLabelSuffix.Tab)
        {
            double tab = level.ParagraphFormat.TabStops is [{ } first, ..] ? first.Position : left;
            alignment.Add(new XAttribute(TextNs + "list-tab-stop-position", Pt(tab)));
        }

        alignment.Add(new XAttribute(FoNs + "text-indent", Pt(level.ParagraphFormat.FirstLineIndent ?? 0)), new XAttribute(FoNs + "margin-left", Pt(left)));
        properties.Add(alignment);
        element.Add(properties);
        if (TextProperties(level.RunFormat, null) is { } text2)
        {
            element.Add(text2);
        }

        return element;
    }

    /// <summary>
    /// The text before a level's numbers, the text after them and how many levels' numbers it shows: "%1.%2)" at level 1 is
    /// "", ")" and 2. ODF puts dots between the numbers; other separators are lost.
    /// </summary>
    private static (string Prefix, string Suffix, int Shown) LabelParts(string text, int index)
    {
        int first = -1;
        int last = -1;
        int lowest = int.MaxValue;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '%' && char.IsAsciiDigit(text[i + 1]))
            {
                first = first < 0 ? i : first;
                last = i + 2;
                lowest = Math.Min(lowest, text[i + 1] - '1');
            }
        }

        if (first < 0)
        {
            return (text, "", 0);
        }

        int shown = Math.Clamp(index - lowest + 1, 1, index + 1);
        return (text[..first], text[last..], shown);
    }

    private static string NumFormat(NumberStyle style) => style switch
    {
        NumberStyle.LowerLetter => "a",
        NumberStyle.UpperLetter => "A",
        NumberStyle.LowerRoman => "i",
        NumberStyle.UpperRoman => "I",
        NumberStyle.DecimalZero => "01, 02, 03, ...",
        NumberStyle.CardinalText => "One",
        NumberStyle.OrdinalText => "First",
        NumberStyle.Ordinal => "1st",
        NumberStyle.None => "",
        _ => "1",
    };

    // ---- Properties ----

    /// <summary>
    /// <c>style:paragraph-properties</c> (ODF 1.3 Part 3, 17.6) for paragraph formatting; null when it sets nothing.
    /// <paramref name="indent"/> is the left indent the paragraph has, which tab positions count from.
    /// </summary>
    private XElement? ParagraphProperties(ParagraphFormat f, double indent)
    {
        XElement p = new(StyleNs + "paragraph-properties");
        if (f.Alignment is { } alignment)
        {
            p.Add(new XAttribute(FoNs + "text-align", alignment switch
            {
                ParagraphAlignment.Center => "center",
                ParagraphAlignment.Right => "end",
                ParagraphAlignment.Justify or ParagraphAlignment.Distribute => "justify",
                _ => "start",
            }));
            if (alignment == ParagraphAlignment.Distribute)
            {
                p.Add(new XAttribute(FoNs + "text-align-last", "justify"));
            }
        }

        Length(p, FoNs + "margin-left", f.LeftIndent);
        Length(p, FoNs + "margin-right", f.RightIndent);
        Length(p, FoNs + "text-indent", f.FirstLineIndent);
        Length(p, FoNs + "margin-top", f.SpaceBefore is { } before ? Math.Max(0, before) : null);
        Length(p, FoNs + "margin-bottom", f.SpaceAfter is { } after ? Math.Max(0, after) : null);
        if (f.LineSpacing is { } spacing && spacing > 0)
        {
            switch (f.LineSpacingRule ?? LineSpacingRule.Multiple)
            {
                case LineSpacingRule.Exactly:
                    p.Add(new XAttribute(FoNs + "line-height", Pt(spacing)));
                    break;
                case LineSpacingRule.AtLeast:
                    p.Add(new XAttribute(StyleNs + "line-height-at-least", Pt(spacing)));
                    break;
                default:
                    p.Add(new XAttribute(FoNs + "line-height", Percent(spacing * 100)));
                    break;
            }
        }

        if (f.KeepWithNext is { } keep)
        {
            p.Add(new XAttribute(FoNs + "keep-with-next", keep ? "always" : "auto"));
        }

        if (f.KeepTogether is { } together)
        {
            p.Add(new XAttribute(FoNs + "keep-together", together ? "always" : "auto"));
        }

        if (f.PageBreakBefore is { } pageBreak)
        {
            p.Add(new XAttribute(FoNs + "break-before", pageBreak ? "page" : "auto"));
        }

        if (f.WidowControl is { } widows)
        {
            p.Add(new XAttribute(FoNs + "orphans", widows ? "2" : "0"), new XAttribute(FoNs + "widows", widows ? "2" : "0"));
        }

        if (f.Shading is { } shading)
        {
            p.Add(new XAttribute(FoNs + "background-color", Background(shading)));
        }

        Borders(p, f.BorderTop, f.BorderBottom, f.BorderLeft, f.BorderRight, padding: true);
        if (f.Bidi is { } bidi)
        {
            p.Add(new XAttribute(StyleNs + "writing-mode", bidi ? "rl-tb" : "lr-tb"));
        }

        if (f.SnapToGrid is { } snap)
        {
            p.Add(new XAttribute(StyleNs + "snap-to-layout-grid", snap ? "true" : "false"));
        }

        if (f.ContextualSpacing is { } contextual)
        {
            p.Add(new XAttribute((Odf13 ? StyleNs : LoExtNs) + "contextual-spacing", contextual ? "true" : "false"));
        }

        if (f.SuppressLineNumbers is { } suppress)
        {
            p.Add(new XAttribute(TextNs + "number-lines", suppress ? "false" : "true"), new XAttribute(TextNs + "line-number", "0"));
        }

        if (f.TabStops is { } tabs)
        {
            XElement stops = new(StyleNs + "tab-stops");
            foreach (TabStop tab in tabs)
            {
                if (tab.Alignment is TabAlignment.Clear or TabAlignment.Bar)
                {
                    continue;
                }

                XElement stop = new(StyleNs + "tab-stop", new XAttribute(StyleNs + "position", Pt(tab.Position - indent)));
                switch (tab.Alignment)
                {
                    case TabAlignment.Center:
                        stop.Add(new XAttribute(StyleNs + "type", "center"));
                        break;
                    case TabAlignment.Right:
                        stop.Add(new XAttribute(StyleNs + "type", "right"));
                        break;
                    case TabAlignment.Decimal:
                        stop.Add(new XAttribute(StyleNs + "type", "char"), new XAttribute(StyleNs + "char", "."));
                        break;
                }

                (string? style, string? text) = tab.Leader switch
                {
                    TabLeader.Dot => ("dotted", "."),
                    TabLeader.Hyphen => ("dash", "-"),
                    TabLeader.Underscore or TabLeader.Heavy => ("solid", "_"),
                    TabLeader.MiddleDot => ("dotted", "·"),
                    _ => ((string?)null, (string?)null),
                };
                if (style is not null)
                {
                    stop.Add(new XAttribute(StyleNs + "leader-style", style), new XAttribute(StyleNs + "leader-text", text!));
                }

                stops.Add(stop);
            }

            p.Add(stops);
        }

        return p.HasAttributes || p.HasElements ? p : null;
    }

    private static void Length(XElement element, XName name, double? value)
    {
        if (value is { } v && double.IsFinite(v))
        {
            element.Add(new XAttribute(name, Pt(v)));
        }
    }

    private string Background(Shading shading)
    {
        if (shading.Pattern == ShadingPattern.Nil)
        {
            return "transparent";
        }

        return shading.Pattern == ShadingPattern.Solid
            ? Color(shading.PatternColor) ?? Color(shading.Fill) ?? "#000000"
            : Color(shading.Fill) ?? "transparent";
    }

    /// <summary>Four sides' borders as <c>fo:border-*</c>, with their spacing as padding when <paramref name="padding"/> says so.</summary>
    private void Borders(XElement element, Border? top, Border? bottom, Border? left, Border? right, bool padding)
    {
        bool shadow = false;
        foreach ((string side, Border? border) in new[] { ("top", top), ("bottom", bottom), ("left", left), ("right", right) })
        {
            if (border is null)
            {
                continue;
            }

            element.Add(new XAttribute(FoNs + ("border-" + side), BorderValue(border)));
            if (border.IsVisible && LineWidths(border) is { } widths)
            {
                element.Add(new XAttribute(StyleNs + ("border-line-width-" + side), widths));
            }

            if (padding && border.IsVisible)
            {
                element.Add(new XAttribute(FoNs + ("padding-" + side), Pt(border.Space)));
            }

            shadow |= border.Shadow && border.IsVisible;
        }

        if (shadow)
        {
            element.Add(new XAttribute(StyleNs + "shadow", "#808080 1.5pt 1.5pt"));
        }
    }

    /// <summary>A border as <c>fo:border</c> writes it: "0.5pt solid #000000", or "none".</summary>
    private string BorderValue(Border border)
    {
        if (!border.IsVisible)
        {
            return "none";
        }

        string style = border.Style switch
        {
            BorderStyle.Double or BorderStyle.Triple or BorderStyle.ThinThickSmallGap or BorderStyle.ThickThinSmallGap or BorderStyle.ThinThickThinSmallGap
                or BorderStyle.ThinThickMediumGap or BorderStyle.ThickThinMediumGap or BorderStyle.ThinThickThinMediumGap or BorderStyle.ThinThickLargeGap
                or BorderStyle.ThickThinLargeGap or BorderStyle.ThinThickThinLargeGap or BorderStyle.DoubleWave => "double",
            BorderStyle.Dotted => "dotted",
            BorderStyle.Dashed or BorderStyle.DashDotStroked => "dashed",
            BorderStyle.DashSmallGap => "fine-dashed",
            BorderStyle.DotDash => "dash-dot",
            BorderStyle.DotDotDash => "dash-dot-dot",
            BorderStyle.ThreeDEngrave => "groove",
            BorderStyle.ThreeDEmboss => "ridge",
            BorderStyle.Inset => "inset",
            BorderStyle.Outset => "outset",
            _ => "solid",
        };
        double width = style == "double" ? DoubleWidth(border) : border.Style == BorderStyle.Thick ? Math.Max(border.Width, 1.5) : border.Width;
        return Pt(width) + " " + style + " " + (Color(border.Color) ?? "#000000");
    }

    // A double border's three widths (inner line, gap, outer line), as style:border-line-width gives them; Word's width is one line's.
    private static string? LineWidths(Border border)
    {
        double w = border.Width;
        return border.Style switch
        {
            BorderStyle.Double or BorderStyle.Triple or BorderStyle.DoubleWave => $"{Pt(w)} {Pt(w)} {Pt(w)}",
            BorderStyle.ThinThickSmallGap or BorderStyle.ThinThickMediumGap or BorderStyle.ThinThickLargeGap => $"{Pt(Math.Max(0.05, w / 3))} {Pt(w / 3)} {Pt(w)}",
            BorderStyle.ThickThinSmallGap or BorderStyle.ThickThinMediumGap or BorderStyle.ThickThinLargeGap => $"{Pt(w)} {Pt(w / 3)} {Pt(Math.Max(0.05, w / 3))}",
            BorderStyle.ThinThickThinSmallGap or BorderStyle.ThinThickThinMediumGap or BorderStyle.ThinThickThinLargeGap => $"{Pt(w)} {Pt(w)} {Pt(w)}",
            _ => null,
        };
    }

    private static double DoubleWidth(Border border)
    {
        return LineWidths(border) is { } widths
            ? widths.Split(' ').Sum(w => OdfUnits.Length(w) ?? 0)
            : border.Width * 3;
    }

    /// <summary>
    /// <c>style:text-properties</c> (ODF 1.3 Part 3, 17.10) for run formatting; null when it sets nothing. <paramref name="size"/>
    /// is the size the text has, for a raised or lowered baseline given in points.
    /// </summary>
    private XElement? TextProperties(RunFormat f, double? size)
    {
        XElement t = new(StyleNs + "text-properties");
        if (FontOf(f.FontAscii ?? f.FontHighAnsi, f.ThemeFontAscii ?? f.ThemeFontHighAnsi) is { } font)
        {
            t.Add(new XAttribute(StyleNs + "font-name", FontFace(font)));
        }

        if (f.Color is { } color)
        {
            if (Color(color) is { } rgb)
            {
                t.Add(new XAttribute(FoNs + "color", rgb));
            }
            else
            {
                t.Add(new XAttribute(StyleNs + "use-window-font-color", "true"));
            }
        }

        if (f.AllCaps is { } caps)
        {
            t.Add(new XAttribute(FoNs + "text-transform", caps ? "uppercase" : "none"));
        }

        if (f.SmallCaps is { } small)
        {
            t.Add(new XAttribute(FoNs + "font-variant", small ? "small-caps" : "normal"));
        }

        if (f.Spacing is { } spacing)
        {
            t.Add(new XAttribute(FoNs + "letter-spacing", spacing == 0 ? "normal" : Pt(spacing)));
        }

        if (f.Size is { } points && points > 0)
        {
            t.Add(new XAttribute(FoNs + "font-size", Pt(points)), new XAttribute(StyleNs + "font-size-asian", Pt(points)));
        }

        Language(t, FoNs, "language", "country", f.NoProof == true ? "zxx" : f.Language);
        if (f.Italic is { } italic)
        {
            t.Add(new XAttribute(FoNs + "font-style", italic ? "italic" : "normal"), new XAttribute(StyleNs + "font-style-asian", italic ? "italic" : "normal"));
        }

        if (f.Shadow is { } shadow)
        {
            t.Add(new XAttribute(FoNs + "text-shadow", shadow ? "1pt 1pt" : "none"));
        }

        Underline(t, f);
        if (f.Strikethrough is not null || f.DoubleStrikethrough is not null)
        {
            bool twice = f.DoubleStrikethrough == true;
            bool through = twice || f.Strikethrough == true;
            t.Add(new XAttribute(StyleNs + "text-line-through-style", through ? "solid" : "none"));
            if (through)
            {
                t.Add(new XAttribute(StyleNs + "text-line-through-type", twice ? "double" : "single"));
            }
        }

        if (f.Overline is { } overline)
        {
            t.Add(new XAttribute(StyleNs + "text-overline-style", overline ? "solid" : "none"));
            if (overline)
            {
                t.Add(new XAttribute(StyleNs + "text-overline-width", "auto"), new XAttribute(StyleNs + "text-overline-color", "font-color"));
            }
        }

        if (f.Bold is { } bold)
        {
            t.Add(new XAttribute(FoNs + "font-weight", bold ? "bold" : "normal"), new XAttribute(StyleNs + "font-weight-asian", bold ? "bold" : "normal"));
        }

        if (f.Emboss is not null || f.Imprint is not null)
        {
            t.Add(new XAttribute(StyleNs + "font-relief", f.Emboss == true ? "embossed" : f.Imprint == true ? "engraved" : "none"));
        }

        if (f.Outline is { } outline)
        {
            t.Add(new XAttribute(StyleNs + "text-outline", outline ? "true" : "false"));
        }

        if (f.VerticalPosition is { } position)
        {
            t.Add(new XAttribute(StyleNs + "text-position", position switch
            {
                VerticalTextPosition.Superscript => "super 58%",
                VerticalTextPosition.Subscript => "sub 58%",
                _ => "0% 100%",
            }));
        }
        else if (f.Position is { } raise && raise != 0)
        {
            double basis = f.Size ?? size ?? document.Styles.DefaultRunFormat.Size ?? 12;
            t.Add(new XAttribute(StyleNs + "text-position", Percent(raise / basis * 100) + " 100%"));
        }

        if (f.Scale is { } scale && scale > 0)
        {
            t.Add(new XAttribute(StyleNs + "text-scale", Percent(scale)));
        }

        if (f.Kerning is { } kerning)
        {
            t.Add(new XAttribute(StyleNs + "letter-kerning", kerning > 0 ? "true" : "false"));
        }

        if (FontOf(f.FontEastAsia, f.ThemeFontEastAsia) is { } asian)
        {
            t.Add(new XAttribute(StyleNs + "font-name-asian", FontFace(asian)));
        }

        Language(t, StyleNs, "language-asian", "country-asian", f.LanguageEastAsia);
        if (FontOf(f.FontComplexScript, f.ThemeFontComplexScript) is { } complex)
        {
            t.Add(new XAttribute(StyleNs + "font-name-complex", FontFace(complex)));
        }

        if (f.SizeComplexScript is { } complexSize && complexSize > 0)
        {
            t.Add(new XAttribute(StyleNs + "font-size-complex", Pt(complexSize)));
        }

        Language(t, StyleNs, "language-complex", "country-complex", f.LanguageBidi);
        if (f.ItalicComplexScript is { } italicComplex)
        {
            t.Add(new XAttribute(StyleNs + "font-style-complex", italicComplex ? "italic" : "normal"));
        }

        if (f.BoldComplexScript is { } boldComplex)
        {
            t.Add(new XAttribute(StyleNs + "font-weight-complex", boldComplex ? "bold" : "normal"));
        }

        if (f.Highlight is { } highlight && highlight != HighlightColor.None)
        {
            t.Add(new XAttribute(FoNs + "background-color", Hex(Layout.ParagraphBridge.HighlightRgb(highlight))));
        }
        else if (f.Shading is { } shading)
        {
            t.Add(new XAttribute(FoNs + "background-color", Background(shading)));
        }

        if (f.Hidden is { } hidden)
        {
            t.Add(new XAttribute(TextNs + "display", hidden ? "none" : "true"));
        }

        return t.HasAttributes ? t : null;
    }

    private string? FontOf(string? name, ThemeFont? theme)
    {
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return theme switch
        {
            ThemeFont.MajorLatin => document.Theme.MajorLatinFont,
            ThemeFont.MinorLatin => document.Theme.MinorLatinFont,
            ThemeFont.MajorEastAsia => document.Theme.MajorEastAsiaFont,
            ThemeFont.MinorEastAsia => document.Theme.MinorEastAsiaFont,
            _ => null,
        } is { Length: > 0 } font ? font : null;
    }

    private static void Language(XElement t, XNamespace ns, string language, string country, string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }

        int dash = tag.IndexOf('-', StringComparison.Ordinal);
        string lang = dash < 0 ? tag : tag[..dash];
        string region = dash < 0 ? "none" : tag[(dash + 1)..];
        if (lang.Length == 0 || !lang.All(char.IsAsciiLetter) || !region.All(char.IsAsciiLetterOrDigit))
        {
            return;
        }

        t.Add(new XAttribute(ns + language, lang.ToLowerInvariant()), new XAttribute(ns + country, region == "none" ? "none" : region.ToUpperInvariant()));
    }

    private void Underline(XElement t, RunFormat f)
    {
        if (f.Underline is not { } underline)
        {
            return;
        }

        if (underline == UnderlineStyle.None)
        {
            t.Add(new XAttribute(StyleNs + "text-underline-style", "none"));
            return;
        }

        (string style, bool bold, bool twice, bool words) = underline switch
        {
            UnderlineStyle.Words => ("solid", false, false, true),
            UnderlineStyle.Double => ("solid", false, true, false),
            UnderlineStyle.Thick => ("solid", true, false, false),
            UnderlineStyle.Dotted => ("dotted", false, false, false),
            UnderlineStyle.DottedHeavy => ("dotted", true, false, false),
            UnderlineStyle.Dash => ("dash", false, false, false),
            UnderlineStyle.DashedHeavy => ("dash", true, false, false),
            UnderlineStyle.DashLong => ("long-dash", false, false, false),
            UnderlineStyle.DashLongHeavy => ("long-dash", true, false, false),
            UnderlineStyle.DotDash => ("dot-dash", false, false, false),
            UnderlineStyle.DashDotHeavy => ("dot-dash", true, false, false),
            UnderlineStyle.DotDotDash => ("dot-dot-dash", false, false, false),
            UnderlineStyle.DashDotDotHeavy => ("dot-dot-dash", true, false, false),
            UnderlineStyle.Wave => ("wave", false, false, false),
            UnderlineStyle.WavyHeavy => ("wave", true, false, false),
            UnderlineStyle.WavyDouble => ("wave", false, true, false),
            _ => ("solid", false, false, false),
        };
        t.Add(new XAttribute(StyleNs + "text-underline-style", style), new XAttribute(StyleNs + "text-underline-width", bold ? "bold" : "auto"),
            new XAttribute(StyleNs + "text-underline-color", f.UnderlineColor is { } c && Color(c) is { } rgb ? rgb : "font-color"));
        if (twice)
        {
            t.Add(new XAttribute(StyleNs + "text-underline-type", "double"));
        }

        if (words)
        {
            t.Add(new XAttribute(StyleNs + "text-underline-mode", "skip-white-space"));
        }
    }
}
