using System.Text;

using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>The walk from boxes to blocks and inlines.</summary>
internal sealed partial class HtmlReader
{
    /// <summary>A story being filled (the body or a table cell): its open paragraph and what waits for the next one.</summary>
    private sealed class Flow(BlockContainer container, double width)
    {
        public BlockContainer Container { get; } = container;

        /// <summary>The width the flow's blocks get, in points, for percentages.</summary>
        public double Width { get; } = width;

        public Paragraph? Current { get; set; }

        /// <summary>The paragraph's inlines new content goes to (the paragraph's, or a link's in it).</summary>
        public NodeCollection<Inline>? Target { get; set; }

        /// <summary>The link href the target belongs to.</summary>
        public string? TargetLink { get; set; }

        /// <summary>Collapsed vertical margins waiting for the next paragraph.</summary>
        public double PendingSpace { get; set; }

        public bool PendingPageBreak { get; set; }

        public List<string> PendingBookmarks { get; } = [];

        /// <summary>Whether collapsible white space is dropped here: at a line's start or after a space.</summary>
        public bool AtLineStart { get; set; } = true;

        public Paragraph? Last { get; set; }
    }

    /// <summary>The nearest block box: its style and indents, and the paragraphs made in it, for borders and list labels.</summary>
    private sealed class Frame(HtmlElement element, HtmlStyle style, Frame? parent)
    {
        public HtmlElement Element { get; } = element;

        public HtmlStyle Style { get; } = style;

        public Frame? Parent { get; } = parent;

        public double Left { get; set; }

        public double Right { get; set; }

        public List<Paragraph> Paragraphs { get; } = [];

        /// <summary>The list item whose first paragraph takes the label: list, level, value.</summary>
        public (List List, int Level)? Item { get; set; }

        public bool KeepTogether { get; set; }

        public Shading? Background { get; set; }

        public int? HeadingLevel { get; set; }

        public ListState? Lists { get; set; }
    }

    /// <summary>The list an ol or ul belongs to (one model list per outermost list) and the level it is at.</summary>
    private sealed class ListState(List list, int level, ListState? outer)
    {
        public List List { get; } = list;

        public int Level { get; } = level;

        public ListState? Outer { get; } = outer;

        public HashSet<int> Configured { get; } = outer?.Configured ?? [];
    }

    private bool quirks;

    private void Build(HtmlDocument dom)
    {
        Section section = document.AddSection();
        if (dom.DocumentElement is not { } html)
        {
            ApplyPage(section, null);
            return;
        }

        quirks = dom.QuirksMode == HtmlQuirksMode.Quirks;
        HtmlStyle rootStyle = StyleOf(html, null);
        ApplyPage(section, FirstPageName(html, rootStyle, 0));
        double width = (section.PageSetup.PageWidth ?? 612) - (section.PageSetup.MarginLeft ?? 72) - (section.PageSetup.MarginRight ?? 72);
        Flow flow = new(section.Body, width);
        Frame frame = new(html, rootStyle, null);
        Children(html, rootStyle, flow, frame);
        Close(flow);
        FinishFlow(flow);
    }

    /// <summary>
    /// The page name the first content asks for (the CSS page property, as Word's HTML puts on its "Section1" division), so
    /// the @page rules for that name apply, as Chromium applies them.
    /// </summary>
    private string? FirstPageName(HtmlElement element, HtmlStyle style, int level)
    {
        if (level >= options.Limits.MaxDepth)
        {
            return null;
        }

        // Page names are case-sensitive, so the value is read as written.
        if (style.Text("page")?.Trim() is { Length: > 0 } name && !name.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        foreach (HtmlNode child in element.Children)
        {
            if (child is HtmlText text && !string.IsNullOrWhiteSpace(text.Data))
            {
                // Content before any named box: the first page is unnamed.
                return null;
            }

            if (child is not HtmlElement childElement || childElement.NamespaceUri != HtmlElement.HtmlNamespace || childElement.IsHtml("head"))
            {
                continue;
            }

            HtmlStyle childStyle = StyleOf(childElement, style);
            if (childStyle.Keyword("display") == "none")
            {
                continue;
            }

            if (FirstPageName(childElement, childStyle, level + 1) is { } found)
            {
                return found;
            }

            if (childElement.Descendants().Any(d => d is HtmlText t && !string.IsNullOrWhiteSpace(t.Data)) || childElement.IsHtml("img"))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The @page rules for a first page (of the given name): size and margins.</summary>
    private void ApplyPage(Section section, string? name)
    {
        // A browser's print defaults where the page sets nothing: Letter with 0.4 inch margins (Chromium's print to PDF).
        section.PageSetup.PageWidth = 612;
        section.PageSetup.PageHeight = 792;
        section.PageSetup.MarginTop = 28.8;
        section.PageSetup.MarginBottom = 28.8;
        section.PageSetup.MarginLeft = 28.8;
        section.PageSetup.MarginRight = 28.8;
        section.PageSetup.HeaderDistance = 0;
        section.PageSetup.FooterDistance = 0;

        CssPageStyle page = cascade.CascadePage(new CssPage(name, IsFirst: true, IsLeft: false));
        if (page.Page.TryGetDeclaration("size", out CssDeclaration? size) && CssPageSize.TryParse(size!.Value, out CssPageSize pageSize) && !pageSize.IsAuto)
        {
            (double width, double height) = pageSize.Name?.ToLowerInvariant() switch
            {
                "a5" => (419.53, 595.28),
                "a4" => (595.28, 841.89),
                "a3" => (841.89, 1190.55),
                "b5" => (498.9, 708.66),
                "b4" => (708.66, 1000.63),
                "letter" => (612.0, 792.0),
                "legal" => (612.0, 1008.0),
                "ledger" => (792.0, 1224.0),
                _ => (pageSize.Width is { } w ? HtmlStyle.ToPoints(w, 12, 12) : 612, pageSize.Height is { } h ? HtmlStyle.ToPoints(h, 12, 12) : pageSize.Width is { } w2 ? HtmlStyle.ToPoints(w2, 12, 12) : 792),
            };
            if (pageSize.Orientation == CssPageOrientation.Landscape || (pageSize.Orientation == CssPageOrientation.Portrait && width > height))
            {
                (width, height) = (height, width);
            }

            section.PageSetup.PageWidth = width;
            section.PageSetup.PageHeight = height;
            section.PageSetup.Orientation = width > height ? PageOrientation.Landscape : PageOrientation.Portrait;
        }

        double pageWidth = section.PageSetup.PageWidth ?? 612;
        foreach ((string property, Action<double> set) in (ReadOnlySpan<(string, Action<double>)>)
            [
                ("margin-top", v => section.PageSetup.MarginTop = v),
                ("margin-bottom", v => section.PageSetup.MarginBottom = v),
                ("margin-left", v => section.PageSetup.MarginLeft = v),
                ("margin-right", v => section.PageSetup.MarginRight = v),
            ])
        {
            if (page.Page.TryGetDeclaration(property, out CssDeclaration? declaration) && declaration is not null)
            {
                CssComponentValue[] items = [.. declaration.Value.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
                if (items.Length == 1)
                {
                    double? points = items[0] is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } token } ? token.Number / 100 * pageWidth
                        : CssLength.TryParse(items[0], out CssLength length) ? HtmlStyle.ToPoints(length, 12, 12)
                        : items[0] is CssTokenValue { Token: { Kind: CssTokenKind.Number, Number: 0 } } ? 0 : null;
                    if (points is { } value)
                    {
                        // Negative page margins (bleed) are not something a page setup holds.
                        set(Math.Max(0, value));
                    }
                }
            }
        }
    }

    /// <summary>How deep the walk is in elements, against <see cref="DocumentLimits.MaxDepth"/>.</summary>
    private int depth;

    private void Children(HtmlNode parent, HtmlStyle parentStyle, Flow flow, Frame frame)
    {
        if (depth >= options.Limits.MaxDepth)
        {
            // Hostile nesting: what lies deeper is kept as its text, without walking it as structure.
            Warn(ConversionWarningKind.LayoutApproximated, "Elements nested deeper than the limit, read as plain text");
            AddText(string.Concat(parent.Descendants().OfType<HtmlText>().Select(t => t.Data)), parentStyle, flow, frame);
            return;
        }

        depth++;
        try
        {
            ChildrenWithin(parent, parentStyle, flow, frame);
        }
        finally
        {
            depth--;
        }
    }

    private void ChildrenWithin(HtmlNode parent, HtmlStyle parentStyle, Flow flow, Frame frame)
    {
        foreach (HtmlNode child in parent.Children)
        {
            switch (child)
            {
                case HtmlText text:
                    AddText(text.Data, parentStyle, flow, frame);
                    break;
                case HtmlElement element:
                    Element(element, parentStyle, flow, frame);
                    break;
            }
        }
    }

    private void Element(HtmlElement element, HtmlStyle parentStyle, Flow flow, Frame frame)
    {
        if (element.NamespaceUri != HtmlElement.HtmlNamespace)
        {
            if (element.LocalName == "svg")
            {
                Warn(ConversionWarningKind.DrawingDropped, "Inline SVG drawings");
            }

            return;
        }

        HtmlStyle style = StyleOf(element, parentStyle);
        string display = style.Keyword("display") ?? "inline";
        if (display == "none")
        {
            return;
        }

        if (element.GetAttribute("id", null) is { Length: > 0 } id)
        {
            flow.PendingBookmarks.Add(id);
            if (flow.Current is not null && !IsBlockDisplay(display))
            {
                FlushBookmarks(flow);
            }
        }

        if (style.Keyword("position") is "absolute" or "fixed")
        {
            Warn(ConversionWarningKind.LayoutApproximated, "Absolutely positioned boxes, placed in the flow");
        }

        switch (element.LocalName)
        {
            case "br":
                Target(flow, frame).Add(new Break(document, BreakKind.Line));
                flow.AtLineStart = true;
                return;
            case "img":
                Image(element, style, flow, frame);
                return;
            case "input" or "select" or "button" or "textarea" or "iframe" or "object" or "embed" or "video" or "audio" or "canvas":
                if (element.LocalName is "textarea" or "button")
                {
                    break;
                }

                Warn(ConversionWarningKind.DrawingDropped, "Form controls and embedded content");
                return;
        }

        if (display == "contents")
        {
            Children(element, style, flow, frame);
            return;
        }

        if (display is "table" or "inline-table")
        {
            BuildTable(element, style, flow, frame);
            return;
        }

        if (IsBlockDisplay(display))
        {
            Block(element, style, display, flow, frame);
            return;
        }

        InlineElement(element, style, flow, frame);
    }

    private static bool IsBlockDisplay(string display)
    {
        return display is "block" or "list-item" or "flow-root" or "flex" or "grid" or "table-caption" or "table-row-group"
            or "table-header-group" or "table-footer-group" or "table-row" or "table-cell" or "table-column-group" or "table-column"
            or "run-in" or "ruby-base-container";
    }

    // ---- Blocks -------------------------------------------------------------------------------------------------------

    private void Block(HtmlElement element, HtmlStyle style, string display, Flow flow, Frame parent)
    {
        Close(flow);
        if (display is "flex" or "grid")
        {
            Warn(ConversionWarningKind.LayoutApproximated, "Flexbox and grid containers, laid out as stacked blocks");
        }

        double width = flow.Width;
        double marginTop = style.Length("margin-top", width) ?? 0;
        double marginBottom = style.Length("margin-bottom", width) ?? 0;

        // The margin collapsing quirk (HTML Standard 15.3.10): in quirks mode, a body's or cell's first child loses its default
        // top margin, and its last child its default bottom margin.
        if (quirks && element.Parent is HtmlElement container && container.LocalName is "body" or "td" or "th")
        {
            if (style.FromUserAgent("margin-top") && !Substantial(element, -1))
            {
                marginTop = 0;
            }

            if (style.FromUserAgent("margin-bottom") && !Substantial(element, 1))
            {
                marginBottom = 0;
            }
        }
        double marginLeft = style.Length("margin-left", width) ?? 0;
        double marginRight = style.Length("margin-right", width) ?? 0;
        double paddingTop = style.Length("padding-top", width) ?? 0;
        double paddingBottom = style.Length("padding-bottom", width) ?? 0;
        double paddingLeft = style.Length("padding-left", width) ?? 0;
        double paddingRight = style.Length("padding-right", width) ?? 0;
        Border? borderTop = BorderOf(style, "top");
        Border? borderBottom = BorderOf(style, "bottom");
        Border? borderLeft = BorderOf(style, "left");
        Border? borderRight = BorderOf(style, "right");

        Frame frame = new(element, style, parent)
        {
            Left = parent.Left + marginLeft + paddingLeft + (borderLeft?.Width ?? 0),
            Right = parent.Right + marginRight + paddingRight + (borderRight?.Width ?? 0),
            KeepTogether = parent.KeepTogether || style.Keyword("break-inside") is "avoid" or "avoid-page" || style.Keyword("page-break-inside") == "avoid",
            Background = style.Color("background-color") is { } background ? Shading.Solid(background) : parent.Background,
            Lists = parent.Lists,
            HeadingLevel = element.LocalName is ['h', >= '1' and <= '6'] ? element.LocalName[1] - '0' : null,
        };

        // Lists: an ol or ul starts a level; a list item's first paragraph takes the label.
        if (element.LocalName is "ol" or "ul" or "menu" or "dir")
        {
            int level = parent.Lists is { } outer ? Math.Min(outer.Level + 1, 8) : 0;
            List list = parent.Lists?.List ?? (element.LocalName == "ol" ? document.Lists.AddNumbered() : document.Lists.AddBulleted());
            frame.Lists = new ListState(list, level, parent.Lists);
            if (element.LocalName == "ol" && element.GetAttribute("start", null) is { } start && int.TryParse(start, out int startAt) && level == 0)
            {
                list.Definition.Levels[0].StartAt = startAt;
            }
        }

        if (display == "list-item")
        {
            ListState lists = frame.Lists ?? new ListState(document.Lists.AddBulleted(), 0, null);
            frame.Lists = lists;
            string type = style.Keyword("list-style-type") ?? "disc";
            if (type != "none")
            {
                ConfigureLevel(lists, type);
                frame.Item = (lists.List, lists.Level);
            }
        }

        if (style.Keyword("break-before") is "page" or "always" or "left" or "right" or "recto" or "verso"
            || style.Keyword("page-break-before") is "always" or "left" or "right")
        {
            flow.PendingPageBreak = true;
        }

        flow.PendingSpace = Collapse(flow.PendingSpace, marginTop) + (borderTop is null ? paddingTop : 0);
        Children(element, style, flow, frame);
        Close(flow);
        flow.PendingSpace = (borderBottom is null ? paddingBottom : 0) + Math.Max(0, marginBottom) + Math.Min(0, marginBottom);

        // The box's borders on its paragraphs: top on the first, bottom on the last, the sides on all.
        if (frame.Paragraphs.Count > 0)
        {
            if (borderTop is not null)
            {
                frame.Paragraphs[0].Format.BorderTop = borderTop with { Space = paddingTop };
            }

            if (borderBottom is not null)
            {
                frame.Paragraphs[^1].Format.BorderBottom = borderBottom with { Space = paddingBottom };
            }

            foreach (Paragraph paragraph in frame.Paragraphs)
            {
                if (borderLeft is not null)
                {
                    paragraph.Format.BorderLeft = borderLeft with { Space = paddingLeft };
                }

                if (borderRight is not null)
                {
                    paragraph.Format.BorderRight = borderRight with { Space = paddingRight };
                }
            }

            if (style.Keyword("page-break-after") == "avoid" || style.Keyword("break-after") is "avoid" or "avoid-page")
            {
                frame.Paragraphs[^1].Format.KeepWithNext = true;
            }
        }
        else if (borderTop is not null || borderBottom is not null)
        {
            // An empty box with a border (a rule): one empty paragraph that shows it.
            Paragraph rule = NewParagraph(flow, frame);
            rule.Format.BorderBottom = (borderBottom ?? borderTop)! with { Space = 0 };
            Close(flow);
        }

        if (style.Keyword("break-after") is "page" or "always" or "left" or "right" or "recto" or "verso"
            || style.Keyword("page-break-after") is "always" or "left" or "right")
        {
            flow.PendingPageBreak = true;
        }
    }

    /// <summary>Whether the element has a substantial sibling before (-1) or after (1) it: an element or text with more than white space.</summary>
    private static bool Substantial(HtmlElement element, int direction)
    {
        IReadOnlyList<HtmlNode> siblings = element.Parent!.Children;
        for (int i = element.Index + direction; i >= 0 && i < siblings.Count; i += direction)
        {
            switch (siblings[i])
            {
                case HtmlElement:
                    return true;
                case HtmlText text when !string.IsNullOrWhiteSpace(text.Data):
                    return true;
            }
        }

        return false;
    }

    private static double Collapse(double pending, double margin)
    {
        return margin >= 0 ? Math.Max(pending, margin) : pending + margin;
    }

    private static Border? BorderOf(HtmlStyle style, string side)
    {
        string? kind = style.Keyword($"border-{side}-style");
        if (kind is null or "none" or "hidden")
        {
            return null;
        }

        double width = style.Length($"border-{side}-width") ?? (style.Keyword($"border-{side}-width") switch
        {
            "thin" => 0.75,
            "thick" => 3.75,
            _ => 2.25,
        });
        if (width <= 0)
        {
            return null;
        }

        BorderStyle line = kind switch
        {
            "dotted" => BorderStyle.Dotted,
            "dashed" => BorderStyle.Dashed,
            "double" => BorderStyle.Double,
            "groove" or "ridge" or "inset" or "outset" => BorderStyle.Single,
            _ => BorderStyle.Single,
        };
        return new Border(line, width, style.Color($"border-{side}-color") ?? style.Color("color") ?? default);
    }

    private static void ConfigureLevel(ListState lists, string type)
    {
        int key = (lists.List.Id * 16) + lists.Level;
        if (!lists.Configured.Add(key))
        {
            return;
        }

        ListLevel level = lists.List.Definition.Levels[lists.Level];
        (NumberStyle number, string text, string? font) = type switch
        {
            "decimal" => (NumberStyle.Decimal, $"%{lists.Level + 1}.", null),
            "decimal-leading-zero" => (NumberStyle.DecimalZero, $"%{lists.Level + 1}.", null),
            "lower-alpha" or "lower-latin" => (NumberStyle.LowerLetter, $"%{lists.Level + 1}.", null),
            "upper-alpha" or "upper-latin" => (NumberStyle.UpperLetter, $"%{lists.Level + 1}.", null),
            "lower-roman" => (NumberStyle.LowerRoman, $"%{lists.Level + 1}.", null),
            "upper-roman" => (NumberStyle.UpperRoman, $"%{lists.Level + 1}.", null),
            "circle" => (NumberStyle.Bullet, "o", "Courier New"),
            "square" => (NumberStyle.Bullet, "▪", null),
            _ => (NumberStyle.Bullet, "•", null),
        };
        level.NumberStyle = number;
        level.LevelText = text;
        if (font is not null)
        {
            level.RunFormat.FontAscii = font;
            level.RunFormat.FontHighAnsi = font;
        }
    }

    // ---- Paragraphs ---------------------------------------------------------------------------------------------------

    /// <summary>The open paragraph's inlines, opening a paragraph (and a link in it) when there is none.</summary>
    private NodeCollection<Inline> Target(Flow flow, Frame frame, string? link = null)
    {
        if (flow.Current is null)
        {
            NewParagraph(flow, frame);
        }

        if (link != flow.TargetLink)
        {
            if (link is null)
            {
                flow.Target = flow.Current!.Inlines;
            }
            else
            {
                Hyperlink hyperlink = new(document);
                if (link.StartsWith('#'))
                {
                    hyperlink.Anchor = link[1..];
                }
                else
                {
                    hyperlink.Address = link;
                }

                flow.Current!.Inlines.Add(hyperlink);
                flow.Target = hyperlink.Inlines;
            }

            flow.TargetLink = link;
        }

        return flow.Target!;
    }

    private Paragraph NewParagraph(Flow flow, Frame frame)
    {
        Paragraph paragraph = new(document);
        flow.Container.Blocks.Add(paragraph);
        flow.Current = paragraph;
        flow.Target = paragraph.Inlines;
        flow.TargetLink = null;
        flow.AtLineStart = true;
        flow.Last = paragraph;

        HtmlStyle style = frame.Style;
        ParagraphFormat format = paragraph.Format;
        format.SpaceBefore = Math.Max(0, flow.PendingSpace);
        format.SpaceAfter = 0;
        flow.PendingSpace = 0;
        format.Alignment = style.Keyword("text-align") switch
        {
            "center" or "-webkit-center" => ParagraphAlignment.Center,
            "right" or "end" or "-webkit-right" => ParagraphAlignment.Right,
            "justify" => ParagraphAlignment.Justify,
            _ => ParagraphAlignment.Left,
        };
        format.LeftIndent = frame.Left;
        format.RightIndent = frame.Right;
        if (style.Length("text-indent", flow.Width) is { } indent && indent != 0)
        {
            format.FirstLineIndent = indent;
        }

        LineHeight(style, format);
        if (flow.PendingPageBreak)
        {
            format.PageBreakBefore = true;
            flow.PendingPageBreak = false;
        }

        if (frame.KeepTogether)
        {
            format.KeepTogether = true;
        }

        if (frame.Background is { } shading)
        {
            format.Shading = shading;
        }

        format.WidowControl = true;

        // The nearest heading or list item among the boxes it is in.
        for (Frame? f = frame; f is not null; f = f.Parent)
        {
            if (f.HeadingLevel is { } level && f.Paragraphs.Count == 0)
            {
                paragraph.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Heading1 + (level - 1));
                format.OutlineLevel = level - 1;
                break;
            }
        }

        for (Frame? f = frame; f is not null; f = f.Parent)
        {
            if (f.Item is { } item)
            {
                if (f.Paragraphs.Count == 0)
                {
                    paragraph.SetList(item.List, item.Level);
                    format.LeftIndent = frame.Left;
                    format.FirstLineIndent = -Math.Min(18, frame.Left);
                }

                break;
            }
        }

        for (Frame? f = frame; f is not null; f = f.Parent)
        {
            f.Paragraphs.Add(paragraph);
        }

        paragraph.Extensions.Set(HtmlExtension.Format, new HtmlExtension(frame.Element.LocalName, frame.Element.Id, frame.Element.GetAttribute("class", null), frame.Element.GetAttribute("style", null)));
        FlushBookmarks(flow);
        return paragraph;
    }

    private static void LineHeight(HtmlStyle style, ParagraphFormat format)
    {
        if (!style.Computed.TryGetValue("line-height", out IReadOnlyList<CssComponentValue>? value))
        {
            return;
        }

        CssComponentValue[] items = [.. value.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
        if (items is [CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident }] && ident.Value.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            format.LineSpacingRule = LineSpacingRule.Multiple;
            format.LineSpacing = 1;
            return;
        }

        double? points = items is [CssTokenValue { Token: { Kind: CssTokenKind.Number } number }] ? number.Number * style.FontSize : style.Length(value, style.FontSize);
        if (points is { } height && height > 0)
        {
            // A taller line than the font's own takes the height; the text sets the line where it is taller (an image).
            format.LineSpacingRule = LineSpacingRule.AtLeast;
            format.LineSpacing = height;
        }
    }

    private static void FlushBookmarks(Flow flow)
    {
        if (flow.Current is null || flow.PendingBookmarks.Count == 0)
        {
            return;
        }

        foreach (string name in flow.PendingBookmarks)
        {
            flow.Target!.Add(new BookmarkStart(flow.Current.Document, name));
        }

        flow.PendingBookmarks.Clear();
    }

    /// <summary>Ends the open paragraph: collapsible white space at its end is dropped.</summary>
    private void Close(Flow flow)
    {
        if (flow.Current is not { } paragraph)
        {
            return;
        }

        TrimEnd(paragraph.Inlines);
        BrowserNormalLineHeight(paragraph);
        flow.Current = null;
        flow.Target = null;
        flow.TargetLink = null;
        flow.AtLineStart = true;
    }

    /// <summary>
    /// line-height: normal as browsers make it: the font's ascent, descent and line gap each rounded to whole CSS pixels
    /// (Blink's font metrics), so lines are a little shorter than the unrounded single spacing Word uses. Set exactly where
    /// every run is text, so nothing taller is cut.
    /// </summary>
    private void BrowserNormalLineHeight(Paragraph paragraph)
    {
        if (paragraph.Format is not { LineSpacingRule: LineSpacingRule.Multiple, LineSpacing: 1 })
        {
            return;
        }

        double pixels = 0;
        foreach (Inline inline in paragraph.Descendants<Inline>())
        {
            switch (inline)
            {
                case Run run:
                    if (run.Format.Size is not { } size || run.Format.FontAscii is not { } family)
                    {
                        return;
                    }

                    metricsFonts ??= new Layout.FontProvider(document, new Layout.FontSettings { UseSystemFonts = false });
                    Egami.Fonts.Typeface face = metricsFonts.Resolve(family, default).Typeface;
                    Egami.Fonts.VerticalMetrics hhea = face.HheaMetrics;
                    double em = size / HtmlStyle.PointsPerPixel / face.UnitsPerEm;
                    pixels = Math.Max(pixels, Math.Round(hhea.Ascent * em) + Math.Round(-hhea.Descent * em) + Math.Round(Math.Max(0, hhea.LineGap) * em));
                    break;
                case Hyperlink or BookmarkStart or Break:
                    break;
                default:
                    return;
            }
        }

        if (pixels > 0)
        {
            paragraph.Format.LineSpacingRule = LineSpacingRule.Exactly;
            paragraph.Format.LineSpacing = pixels * HtmlStyle.PointsPerPixel;
        }
    }

    private Layout.FontProvider? metricsFonts;

    private static void TrimEnd(NodeCollection<Inline> inlines)
    {
        for (int i = inlines.Count - 1; i >= 0; i--)
        {
            switch (inlines[i])
            {
                case Run run when run.Format.Hidden != true:
                    string trimmed = run.Text.TrimEnd(' ');
                    if (trimmed.Length > 0)
                    {
                        run.Text = trimmed;
                        return;
                    }

                    run.Remove();
                    continue;
                case Hyperlink link:
                    TrimEnd(link.Inlines);
                    return;
                case BookmarkStart:
                    continue;
                case Break { Kind: BreakKind.Line } trailing when i > 0:
                    // A line break that ends a block opens no further line in a browser (a pre's last newline, a final br).
                    trailing.Remove();
                    return;
                default:
                    return;
            }
        }
    }

    /// <summary>At the end of a story: its last margin goes on its last paragraph, and an empty story gets one paragraph.</summary>
    private static void FinishFlow(Flow flow)
    {
        if (flow.Container.Blocks.Count == 0)
        {
            flow.Container.Blocks.Add(new Paragraph(flow.Container.Document));
        }
    }
}
