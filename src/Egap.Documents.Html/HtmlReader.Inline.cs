using System.Globalization;
using System.Text;

using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>Inline content, pictures and tables.</summary>
internal sealed partial class HtmlReader
{
    /// <summary>The href of the link the walk is in, which new runs go into.</summary>
    private string? currentLink;

    private void InlineElement(HtmlElement element, HtmlStyle style, Flow flow, Frame frame)
    {
        string? outer = currentLink;
        if (element.IsHtml("a") && element.GetAttribute("href", null) is { } href)
        {
            currentLink = href;
        }

        if (style.Keyword("display") is "inline-flex" or "inline-grid")
        {
            Warn(ConversionWarningKind.LayoutApproximated, "Flexbox and grid containers, laid out as stacked blocks");
        }

        if (element.IsHtml("q"))
        {
            AddText("“", style, flow, frame);
        }

        Children(element, style, flow, frame);
        if (element.IsHtml("q"))
        {
            AddText("”", style, flow, frame);
        }

        currentLink = outer;
    }

    /// <summary>
    /// A form control as an inline content control holding its value, so it keeps its place in the text and an export can
    /// make a PDF form field of it: text inputs and text areas as plain or rich text, check boxes and radio buttons as check
    /// boxes (radio buttons in a <see cref="ContentControlProperties.RadioGroup"/>), selects as drop-down lists. An empty
    /// control holds no-break spaces as wide as its <c>size</c> or <c>cols</c> and as tall as its <c>rows</c>. False for
    /// buttons, files and hidden inputs, which are left to the caller.
    /// </summary>
    private bool FormControl(HtmlElement element, HtmlStyle style, Flow flow, Frame frame)
    {
        string type = element.IsHtml("input") ? (element.GetAttribute("type", null) ?? "text").Trim().ToLowerInvariant() : element.LocalName;
        if (type is "hidden")
        {
            return true;
        }

        if (type is "submit" or "reset" or "button" or "image" or "file" or "range" or "color")
        {
            return false;
        }

        InlineContentControl control = new(document);
        ContentControlProperties properties = control.Properties;
        properties.Tag = element.GetAttribute("name", null) ?? element.Id;
        properties.Title = element.GetAttribute("title", null) ?? element.GetAttribute("placeholder", null);
        List<string> lines = [];
        switch (type)
        {
            case "checkbox" or "radio":
                properties.Kind = ContentControlKind.CheckBox;
                properties.IsChecked = element.GetAttribute("checked", null) is not null;
                if (type == "radio")
                {
                    properties.RadioGroup = properties.Tag ?? "radio";
                    properties.Tag = element.GetAttribute("value", null) ?? "on";
                }

                lines.Add("\u2002\u2002");
                break;
            case "select":
                properties.Kind = ContentControlKind.DropDownList;
                List<HtmlElement> options = [.. element.Descendants().OfType<HtmlElement>().Where(e => e.IsHtml("option"))];
                properties.Items = [.. options.Select(OptionText)];
                HtmlElement? selected = options.FirstOrDefault(o => o.GetAttribute("selected", null) is not null) ?? options.FirstOrDefault();
                string shown = selected is null ? string.Empty : OptionText(selected);
                int width = Math.Max(4, properties.Items.Count > 0 ? properties.Items.Max(i => i.Length) + 2 : 4);
                lines.Add(shown.Length >= width ? shown : shown + new string('\u00A0', width - shown.Length));
                break;
            case "textarea":
                properties.Kind = ContentControlKind.RichText;
                int cols = int.TryParse(element.GetAttribute("cols", null), out int c) && c > 0 ? Math.Min(c, 200) : 20;
                int rows = int.TryParse(element.GetAttribute("rows", null), out int r) && r > 0 ? Math.Min(r, 100) : 2;
                lines.AddRange(TextOf(element).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
                while (lines.Count < rows)
                {
                    lines.Add(string.Empty);
                }

                for (int i = 0; i < lines.Count; i++)
                {
                    lines[i] = lines[i].Length >= cols ? lines[i] : lines[i] + new string('\u00A0', cols - lines[i].Length);
                }

                break;
            default:
                properties.Kind = type == "date" ? ContentControlKind.Date : ContentControlKind.PlainText;
                string value = type == "password" ? string.Empty : element.GetAttribute("value", null) ?? string.Empty;
                int size = int.TryParse(element.GetAttribute("size", null), out int n) && n > 0 ? Math.Min(n, 200) : 20;
                lines.Add(value.Length >= size ? value : value + new string('\u00A0', size - value.Length));
                break;
        }

        properties.ShowingPlaceholder = lines.All(l => l.Trim('\u00A0', '\u2002', ' ').Length == 0) && type is not ("checkbox" or "radio");
        RunFormat format = RunFormatOf(style);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                control.Inlines.Add(new Break(document, BreakKind.Line));
            }

            Run run = new(document, lines[i].Length == 0 ? "\u00A0" : lines[i]);
            run.Format.CopyRaw(format);
            control.Inlines.Add(run);
        }

        Target(flow, frame, currentLink).Add(control);
        flow.AtLineStart = false;
        return true;

        static string OptionText(HtmlElement option) => (option.GetAttribute("label", null) ?? TextOf(option)).Trim();

        static string TextOf(HtmlElement e) => string.Concat(e.Descendants().OfType<HtmlText>().Select(t => t.Data));
    }

    /// <summary>Text with CSS white-space rules: collapsed (normal, nowrap), kept with line breaks (pre, pre-wrap), or pre-line.</summary>
    private void AddText(string data, HtmlStyle style, Flow flow, Frame frame)
    {
        string space = style.Keyword("white-space") ?? "normal";
        bool keepSpaces = space is "pre" or "pre-wrap" or "break-spaces";
        bool keepNewlines = keepSpaces || space == "pre-line";
        StringBuilder text = new(data.Length);
        foreach (char c in data)
        {
            if (c == '\n' && keepNewlines)
            {
                Flush();
                Target(flow, frame, currentLink).Add(new Break(document, BreakKind.Line));
                flow.AtLineStart = true;
                continue;
            }

            if (!keepSpaces && c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (!flow.AtLineStart)
                {
                    text.Append(' ');
                    flow.AtLineStart = true;
                }

                continue;
            }

            text.Append(c == '\t' && keepSpaces ? '\t' : c);
            flow.AtLineStart = keepSpaces && c == ' ';
            if (!keepSpaces)
            {
                flow.AtLineStart = false;
            }
        }

        Flush();

        void Flush()
        {
            if (text.Length == 0)
            {
                return;
            }

            string value = style.Keyword("text-transform") switch
            {
                "lowercase" => text.ToString().ToLowerInvariant(),
                "capitalize" => Capitalize(text.ToString()),
                _ => text.ToString(),
            };
            text.Clear();
            Run run = new(document, value);
            run.Format.CopyRaw(RunFormatOf(style));

            // Starting a paragraph marks the flow at a line start; the text just written decides that instead.
            bool atLineStart = flow.AtLineStart;
            Target(flow, frame, currentLink).Add(run);
            flow.AtLineStart = atLineStart;
        }
    }

    /// <summary>
    /// The OpenType features CSS asks for: <c>font-variant-ligatures</c> and <c>font-variant-numeric</c> first, then
    /// <c>font-feature-settings</c> (<c>"liga" 0, "tnum"</c>), which wins.
    /// </summary>
    private static FontFeatures? FeaturesOf(HtmlStyle style)
    {
        List<(string, int)> features = [];
        foreach (string ligatures in style.Keywords("font-variant-ligatures"))
        {
            switch (ligatures)
            {
                case "none":
                    features.AddRange([("liga", 0), ("clig", 0), ("dlig", 0), ("hlig", 0), ("calt", 0)]);
                    break;
                case "no-common-ligatures":
                    features.AddRange([("liga", 0), ("clig", 0)]);
                    break;
                case "common-ligatures":
                    features.AddRange([("liga", 1), ("clig", 1)]);
                    break;
                case "discretionary-ligatures":
                    features.Add(("dlig", 1));
                    break;
                case "historical-ligatures":
                    features.Add(("hlig", 1));
                    break;
            }
        }

        foreach (string numeric in style.Keywords("font-variant-numeric"))
        {
            switch (numeric)
            {
                case "lining-nums": features.Add(("lnum", 1)); break;
                case "oldstyle-nums": features.Add(("onum", 1)); break;
                case "proportional-nums": features.Add(("pnum", 1)); break;
                case "tabular-nums": features.Add(("tnum", 1)); break;
                case "slashed-zero": features.Add(("zero", 1)); break;
                case "diagonal-fractions": features.Add(("frac", 1)); break;
                case "ordinal": features.Add(("ordn", 1)); break;
            }
        }

        if (style.Text("font-feature-settings") is { } settings && settings.Trim() != "normal")
        {
            foreach (string item in settings.Split(','))
            {
                string[] parts = item.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts[0].Trim('"', '\'').Length != 4)
                {
                    continue;
                }

                int value = parts.Length < 2 ? 1 : parts[1] switch
                {
                    "on" => 1,
                    "off" => 0,
                    _ => int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0 ? n : 1,
                };
                features.Add((parts[0].Trim('"', '\''), value));
            }
        }

        try
        {
            return features.Count == 0 ? null : FontFeatures.Of(features);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Capitalize(string text)
    {
        char[] chars = text.ToCharArray();
        bool start = true;
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                if (start)
                {
                    chars[i] = char.ToUpperInvariant(chars[i]);
                }

                start = false;
            }
            else
            {
                start = char.IsWhiteSpace(chars[i]) || chars[i] is '-' or '(';
            }
        }

        return new string(chars);
    }

    /// <summary>The direct formatting an element's computed style gives its text.</summary>
    private RunFormat RunFormatOf(HtmlStyle style)
    {
        if (runFormats.TryGetValue(style, out RunFormat? cached))
        {
            return cached;
        }

        RunFormat format = new();
        if (style.FontFamily() is { } family)
        {
            format.FontAscii = family;
            format.FontHighAnsi = family;
            format.FontComplexScript = family;
        }

        format.Size = Math.Round(style.FontSize * 2) / 2;

        // Browsers kern text (font-kerning: auto); Word's runs kern only from the size w:kern gives.
        if (style.Keyword("font-kerning") != "none")
        {
            format.Kerning = 1;
        }
        format.Bold = style.Bold;
        format.Italic = style.Italic;
        if (style.Color("color") is { } color)
        {
            format.Color = color;
        }

        IReadOnlyList<string> lines = style.Keywords("text-decoration-line") is { Count: > 0 } longhand ? longhand : style.Keywords("text-decoration");
        if (lines.Contains("underline"))
        {
            format.Underline = style.Keywords("text-decoration-style") switch
            {
                var s when s.Contains("double") => UnderlineStyle.Double,
                var s when s.Contains("dotted") || style.Keywords("text-decoration").Contains("dotted") => UnderlineStyle.Dotted,
                var s when s.Contains("dashed") => UnderlineStyle.Dash,
                var s when s.Contains("wavy") => UnderlineStyle.Wave,
                _ => UnderlineStyle.Single,
            };
        }

        if (lines.Contains("line-through"))
        {
            format.Strikethrough = true;
        }

        if (lines.Contains("overline"))
        {
            format.Overline = true;
        }

        if (style.Keyword("word-spacing") != "normal" && style.Length("word-spacing") is { } wordSpacing && wordSpacing != 0)
        {
            format.WordSpacing = wordSpacing;
        }

        if (FeaturesOf(style) is { Count: > 0 } features)
        {
            format.Features = features;
        }

        switch (style.Keyword("vertical-align"))
        {
            case "super":
                format.VerticalPosition = VerticalTextPosition.Superscript;
                format.Size = Math.Round(style.Parent?.FontSize ?? style.FontSize) * 1.0;
                break;
            case "sub":
                format.VerticalPosition = VerticalTextPosition.Subscript;
                format.Size = Math.Round(style.Parent?.FontSize ?? style.FontSize) * 1.0;
                break;
        }

        if (style.Keyword("display") is not ("block" or "list-item" or "table-cell") && style.Color("background-color") is { } background)
        {
            format.Shading = Shading.Solid(background);
        }

        if (style.Keyword("text-transform") == "uppercase")
        {
            format.AllCaps = true;
        }

        if (style.Keyword("font-variant-caps") is "small-caps" or "all-small-caps" || style.Keyword("font-variant") == "small-caps")
        {
            format.SmallCaps = true;
        }

        if (style.Length("letter-spacing") is { } spacing && spacing != 0)
        {
            format.Spacing = spacing;
        }

        if (style.Keyword("visibility") == "hidden")
        {
            format.Hidden = true;
        }

        if (style.Computed.GetValueText("direction") is "rtl")
        {
            format.RightToLeft = true;
        }

        if (style.Computed.GetValueText("-egap-lang") is null && LanguageOf(style) is { } language)
        {
            format.Language = language;
        }

        runFormats[style] = format;
        return format;
    }

    private string? LanguageOf(HtmlStyle style)
    {
        foreach ((HtmlElement element, HtmlStyle elementStyle) in styles)
        {
            if (ReferenceEquals(elementStyle, style))
            {
                return ((ICssElement)element).Language;
            }
        }

        return null;
    }

    // ---- Pictures -----------------------------------------------------------------------------------------------------

    private void Image(HtmlElement element, HtmlStyle style, Flow flow, Frame frame)
    {
        string? source = element.GetAttribute("src", null);
        byte[]? bytes = source is null ? null : Fetch(options, source);
        string? alt = element.GetAttribute("alt", null);
        if (bytes is null)
        {
            if (source is not null)
            {
                Warn(ConversionWarningKind.ResourceNotLoaded, "Style sheets and pictures the resolver did not give");
            }

            // A picture that cannot be shown shows its alternative text, as browsers do.
            if (!string.IsNullOrEmpty(alt))
            {
                AddText(alt, style, flow, frame);
            }

            return;
        }

        string contentType = ContentTypeOf(bytes, source!);
        double? width = style.Length("width", flow.Width);
        double? height = style.Length("height", flow.Width);
        (double intrinsicWidth, double intrinsicHeight) = ImageSize.Intrinsic(bytes);
        if (width is null && height is null)
        {
            (width, height) = (intrinsicWidth, intrinsicHeight);
        }
        else if (width is null)
        {
            width = intrinsicHeight > 0 ? height!.Value * intrinsicWidth / intrinsicHeight : height;
        }
        else if (height is null)
        {
            height = intrinsicWidth > 0 ? width.Value * intrinsicHeight / intrinsicWidth : width;
        }

        double maxWidth = Math.Max(1, flow.Width - frame.Left - frame.Right);
        if (style.Length("max-width", flow.Width) is { } max)
        {
            maxWidth = Math.Min(maxWidth, max);
        }

        if (width > maxWidth && width > 0)
        {
            height = height * maxWidth / width;
            width = maxWidth;
        }

        Picture picture = new(document, new ImageData(bytes, contentType), Math.Max(1, width ?? 1), Math.Max(1, height ?? 1))
        {
            Description = alt,
            Title = element.GetAttribute("title", null),
            SourceUrl = source!.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? null : source,
        };
        if (style.Keyword("float") is "left" or "right")
        {
            picture.Floating = new FloatingPlacement
            {
                HorizontalAnchor = HorizontalAnchor.Column,
                HorizontalAlignment = style.Keyword("float") == "left" ? FloatingAlignment.Start : FloatingAlignment.End,
                VerticalAnchor = VerticalAnchor.Paragraph,
                Wrap = WrapMode.Square,
                DistanceLeft = style.Length("margin-left", flow.Width) ?? 0,
                DistanceRight = style.Length("margin-right", flow.Width) ?? 0,
                DistanceTop = style.Length("margin-top", flow.Width) ?? 0,
                DistanceBottom = style.Length("margin-bottom", flow.Width) ?? 0,
            };
        }

        Target(flow, frame, currentLink).Add(picture);
        flow.AtLineStart = false;
    }

    private static string ContentTypeOf(byte[] bytes, string source)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            DecodeDataUrl(source, out string type);
            return type;
        }

        ReadOnlySpan<byte> head = bytes;
        return head switch
        {
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
            [0xFF, 0xD8, ..] => "image/jpeg",
            [(byte)'G', (byte)'I', (byte)'F', ..] => "image/gif",
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
            [(byte)'B', (byte)'M', ..] => "image/bmp",
            _ when source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }

    // ---- Tables -------------------------------------------------------------------------------------------------------

    private void BuildTable(HtmlElement element, HtmlStyle style, Flow flow, Frame frame)
    {
        Close(flow);
        HtmlElement[] children = [.. element.Children.OfType<HtmlElement>()];
        foreach (HtmlElement caption in children.Where(c => c.IsHtml("caption")))
        {
            HtmlStyle captionStyle = StyleOf(caption, style);
            Block(caption, captionStyle, "block", flow, frame);
        }

        Table table = new(document);
        flow.Container.Blocks.Add(table);
        TableFormat format = table.Format;
        double available = Math.Max(1, flow.Width - frame.Left - frame.Right);
        if (style.Computed.TryGetValue("width", out IReadOnlyList<CssComponentValue>? widthValue)
            && widthValue.FirstOrDefault(v => v is CssTokenValue { Token.Kind: not CssTokenKind.Whitespace }) is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } percent })
        {
            format.Width = TableWidth.Percent(percent.Number);
        }
        else if (style.Length("width", available) is { } width)
        {
            // A table wider than the page is fitted to it, as print shrinks a page to fit.
            format.Width = TableWidth.Points(Math.Min(width, available));
        }

        bool autoLeft = style.Keyword("margin-left") == "auto";
        bool autoRight = style.Keyword("margin-right") == "auto";
        format.Alignment = autoLeft && autoRight ? TableAlignment.Center : autoLeft ? TableAlignment.Right : TableAlignment.Left;
        format.Indent = frame.Left + (style.Length("margin-left", flow.Width) ?? 0);
        format.CellMarginLeft = 0;
        format.CellMarginRight = 0;
        format.CellMarginTop = 0;
        format.CellMarginBottom = 0;
        if (style.Keyword("border-collapse") != "collapse" && style.Length("border-spacing") is { } spacing && spacing > 0)
        {
            format.CellSpacing = spacing / 2;
        }

        Border? tableBorder = BorderOf(style, "top");
        if (tableBorder is not null)
        {
            format.BorderTop = tableBorder;
            format.BorderBottom = BorderOf(style, "bottom");
            format.BorderLeft = BorderOf(style, "left");
            format.BorderRight = BorderOf(style, "right");
        }

        if (style.Color("background-color") is { } background)
        {
            format.Shading = Shading.Solid(background);
        }

        // Rows in rendering order: header groups first, then bodies and loose rows, then footers.
        List<(HtmlElement Row, HtmlStyle Style, bool Header)> rows = [];
        List<(HtmlElement Row, HtmlStyle Style, bool Header)> footers = [];
        List<(HtmlElement Row, HtmlStyle Style, bool Header)> headers = [];
        foreach (HtmlElement child in children)
        {
            HtmlStyle childStyle = StyleOf(child, style);
            string display = childStyle.Keyword("display") ?? "";
            switch (display)
            {
                case "table-row":
                    rows.Add((child, childStyle, false));
                    break;
                case "table-header-group" or "table-row-group" or "table-footer-group":
                    List<(HtmlElement, HtmlStyle, bool)> target = display switch
                    {
                        "table-header-group" => headers,
                        "table-footer-group" => footers,
                        _ => rows,
                    };
                    foreach (HtmlElement row in child.Children.OfType<HtmlElement>())
                    {
                        HtmlStyle rowStyle = StyleOf(row, childStyle);
                        if (rowStyle.Keyword("display") == "table-row")
                        {
                            target.Add((row, rowStyle, display == "table-header-group"));
                        }
                    }

                    break;
            }
        }

        List<(int Column, int Span, int Remaining)> spans = [];
        foreach ((HtmlElement rowElement, HtmlStyle rowStyle, bool header) in headers.Concat(rows).Concat(footers))
        {
            Row row = new(document);
            table.Rows.Add(row);
            if (header)
            {
                row.Format.IsHeader = true;
            }

            if (rowStyle.Length("height") is { } rowHeight && rowHeight > 0)
            {
                row.Format.Height = rowHeight;
                row.Format.HeightRule = RowHeightRule.AtLeast;
            }

            if (rowStyle.Keyword("break-inside") is "avoid" or "avoid-page" || rowStyle.Keyword("page-break-inside") == "avoid")
            {
                row.Format.CantSplit = true;
            }

            int column = 0;
            foreach (HtmlElement cellElement in rowElement.Children.OfType<HtmlElement>())
            {
                HtmlStyle cellStyle = StyleOf(cellElement, rowStyle);
                if (cellStyle.Keyword("display") != "table-cell")
                {
                    continue;
                }

                column = FillSpans(row, spans, column);
                int colspan = Math.Clamp(IntAttribute(cellElement, "colspan") ?? 1, 1, 1000);
                int rowspan = Math.Clamp(IntAttribute(cellElement, "rowspan") ?? 1, 0, 65534);
                Cell cell = new(document);
                row.Cells.Add(cell);
                if (colspan > 1)
                {
                    cell.Format.GridSpan = colspan;
                }

                if (rowspan != 1)
                {
                    cell.Format.VerticalMerge = VerticalMerge.Restart;
                    spans.Add((column, colspan, rowspan == 0 ? int.MaxValue : rowspan - 1));
                }

                FillCell(cell, cellElement, cellStyle, available);
                column += colspan;
            }

            FillSpans(row, spans, int.MaxValue);
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                if (spans[i].Remaining <= 0)
                {
                    spans.RemoveAt(i);
                }
            }

            if (row.Cells.Count == 0)
            {
                row.Cells.Add(new Cell(document)).AddParagraph();
            }
        }

        if (table.Rows.Count == 0)
        {
            flow.Container.Blocks.Remove(table);
        }

        flow.PendingSpace = Math.Max(0, style.Length("margin-bottom", flow.Width) ?? 0);
    }

    /// <summary>Continuation cells for row spans that cover the columns up to <paramref name="before"/>; the next free column.</summary>
    private int FillSpans(Row row, List<(int Column, int Span, int Remaining)> spans, int before)
    {
        int column = row.Cells.Sum(c => c.Format.GridSpan ?? 1);
        while (true)
        {
            int index = spans.FindIndex(s => s.Column == column && s.Remaining > 0);
            if (index < 0 || column >= before)
            {
                return column;
            }

            (int start, int span, int remaining) = spans[index];
            Cell continuation = new(document);
            row.Cells.Add(continuation);
            continuation.Format.VerticalMerge = VerticalMerge.Continue;
            if (span > 1)
            {
                continuation.Format.GridSpan = span;
            }

            continuation.AddParagraph();
            spans[index] = (start, span, remaining - 1);
            column += span;
        }
    }

    private static int? IntAttribute(HtmlElement element, string name)
    {
        return element.GetAttribute(name, null) is { } value && int.TryParse(value.Trim(), out int number) ? number : null;
    }

    private void FillCell(Cell cell, HtmlElement element, HtmlStyle style, double tableWidth)
    {
        CellFormat format = cell.Format;
        format.MarginLeft = style.Length("padding-left", tableWidth) ?? 0;
        format.MarginRight = style.Length("padding-right", tableWidth) ?? 0;
        format.MarginTop = style.Length("padding-top", tableWidth) ?? 0;
        format.MarginBottom = style.Length("padding-bottom", tableWidth) ?? 0;
        format.BorderTop = BorderOf(style, "top");
        format.BorderBottom = BorderOf(style, "bottom");
        format.BorderLeft = BorderOf(style, "left");
        format.BorderRight = BorderOf(style, "right");
        if (style.Color("background-color") is { } background)
        {
            format.Shading = Shading.Solid(background);
        }

        format.VerticalAlignment = style.Keyword("vertical-align") switch
        {
            "top" => VerticalAlignment.Top,
            "bottom" => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };
        if (style.Computed.TryGetValue("width", out IReadOnlyList<CssComponentValue>? widthValue)
            && widthValue.FirstOrDefault(v => v is CssTokenValue { Token.Kind: not CssTokenKind.Whitespace }) is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } percent })
        {
            format.Width = TableWidth.Percent(percent.Number);
        }
        else if (style.Length("width", tableWidth) is { } width)
        {
            format.Width = TableWidth.Points(width);
        }

        if (style.Keyword("white-space") == "nowrap")
        {
            format.NoWrap = true;
        }

        Flow flow = new(cell, Math.Max(1, tableWidth));
        Frame frame = new(element, style, null);
        string? outerLink = currentLink;
        Children(element, style, flow, frame);
        currentLink = outerLink;
        Close(flow);
        FinishFlow(flow);
    }
}
