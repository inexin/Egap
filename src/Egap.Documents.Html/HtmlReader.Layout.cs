using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>
/// CSS layout read into the model: flex and grid containers with their items, boxes with rounded corners or shadows as
/// groups with a box, and the box properties they share (margins, padding, borders, background, corners, shadow, width).
/// </summary>
internal sealed partial class HtmlReader
{
    // ---- Boxes --------------------------------------------------------------------------------------------------------

    /// <summary>Whether a block box draws something the paragraph model cannot: rounded corners or a shadow.</summary>
    private static bool NeedsBox(HtmlStyle style)
    {
        return Radius(style) is { IsSquare: false } || Shadow(style) is not null;
    }

    /// <summary>
    /// A box's properties: margins (the top one collapsed with <paramref name="pending"/>), padding, borders, background,
    /// corners, shadow, width (borders and padding included, as box-sizing: border-box has it) and least height.
    /// </summary>
    private static BoxStyle ReadBox(HtmlStyle style, double percentBase, double pending = 0, double indentLeft = 0, double indentRight = 0)
    {
        double Length(string property) => style.Length(property, percentBase) ?? 0;
        BoxStyle box = new()
        {
            MarginTop = Math.Max(0, Collapse(pending, Length("margin-top"))),
            MarginLeft = indentLeft + Length("margin-left"),
            MarginRight = indentRight + Length("margin-right"),
            PaddingTop = Math.Max(0, Length("padding-top")),
            PaddingRight = Math.Max(0, Length("padding-right")),
            PaddingBottom = Math.Max(0, Length("padding-bottom")),
            PaddingLeft = Math.Max(0, Length("padding-left")),
            BorderTop = BorderOf(style, "top"),
            BorderRight = BorderOf(style, "right"),
            BorderBottom = BorderOf(style, "bottom"),
            BorderLeft = BorderOf(style, "left"),
            Background = style.Color("background-color"),
            Shadow = Shadow(style),
        };

        // Widths and heights are of the content box unless box-sizing says border-box; the model's include padding and borders.
        bool contentBox = style.Keyword("box-sizing") != "border-box";
        double across = contentBox ? box.Horizontal : 0, down = contentBox ? box.Vertical : 0;
        if (Percent(style, "width") is double percent)
        {
            box = box with { Width = TableWidth.Percent(percent) };
        }
        else if (style.Length("width", percentBase) is double width && width > 0)
        {
            box = box with { Width = TableWidth.Points(width + across) };
        }

        double height = Math.Max(style.Length("height") ?? 0, style.Length("min-height") ?? 0);
        if (height > 0)
        {
            box = box with { MinHeight = height + down };
        }

        double? known = box.Width.Unit == WidthUnit.Points ? box.Width.Value : null;
        return box with { CornerRadius = Radius(style, known ?? (box.MinHeight > 0 ? box.MinHeight : null)) ?? default };
    }

    /// <summary>A property's percentage, when it is given as one.</summary>
    private static double? Percent(HtmlStyle style, string property)
    {
        return style.Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value)
            && value.FirstOrDefault(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace }) is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } token }
            ? token.Number : null;
    }

    /// <summary>
    /// The corner radii, from the border-*-radius longhands; an elliptical radius takes its first value. A percentage is of
    /// the box's width where it is known; half or more of an unknown width makes the corner as round as the box allows.
    /// </summary>
    private static BoxCorners? Radius(HtmlStyle style, double? size = null)
    {
        double Corner(string property)
        {
            if (!style.Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value)
                || value.FirstOrDefault(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace }) is not { } first)
            {
                return 0;
            }

            if (first is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } percent })
            {
                return size is double known ? known * percent.Number / 100 : percent.Number >= 50 ? 10000 : percent.Number * 2;
            }

            return Math.Max(0, style.Length(first, 0) ?? 0);
        }

        BoxCorners corners = new(Corner("border-top-left-radius"), Corner("border-top-right-radius"), Corner("border-bottom-right-radius"), Corner("border-bottom-left-radius"));
        return corners.IsSquare ? null : corners;
    }

    /// <summary>The first outer shadow of box-shadow: offsets, blur, spread and colour; inset shadows are left out.</summary>
    private static BoxShadow? Shadow(HtmlStyle style)
    {
        if (!style.Computed.TryGetValue("box-shadow", out IReadOnlyList<CssComponentValue>? value))
        {
            return null;
        }

        foreach (List<CssComponentValue> layer in SplitTopLevel(value, CssTokenKind.Comma))
        {
            List<double> lengths = [];
            CssColor? color = null;
            bool inset = false;
            foreach (CssComponentValue item in layer)
            {
                if (item is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } && ident.Value.Equals("inset", StringComparison.OrdinalIgnoreCase))
                {
                    inset = true;
                }
                else if (item is CssTokenValue { Token: { Kind: CssTokenKind.Ident } none } && none.Value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                else if (style.Length(item, 0) is double length)
                {
                    lengths.Add(length);
                }
                else if (CssColor.TryParse(item, out CssColor parsed))
                {
                    color = parsed;
                }
            }

            if (inset || lengths.Count < 2)
            {
                continue;
            }

            (byte r, byte g, byte b, byte a) = color is { IsCurrentColor: false } c ? c.ToRgba8() : style.Color("color") is { } current ? Rgba(current) : ((byte)0, (byte)0, (byte)0, (byte)255);
            return new BoxShadow(lengths[0], lengths[1], lengths.Count > 2 ? Math.Max(0, lengths[2]) : 0, lengths.Count > 3 ? lengths[3] : 0)
            {
                Color = DocumentColor.FromRgb(new Egami.Pixels.Rgba8(r, g, b, 255)),
                Opacity = a / 255.0,
            };
        }

        return null;

        static (byte, byte, byte, byte) Rgba(DocumentColor color)
        {
            Egami.Pixels.Rgba8 rgb = color.Resolve(null, default);
            return (rgb.R, rgb.G, rgb.B, 255);
        }
    }

    /// <summary>A value's items split at top-level <paramref name="separator"/> tokens, white space left out.</summary>
    private static List<List<CssComponentValue>> SplitTopLevel(IReadOnlyList<CssComponentValue> value, CssTokenKind separator, string? delim = null)
    {
        List<List<CssComponentValue>> parts = [[]];
        foreach (CssComponentValue item in value)
        {
            if (item is CssTokenValue { Token: var token } && (token.Kind == separator && (delim is null || token.Value == delim)))
            {
                parts.Add([]);
            }
            else if (item is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                parts[^1].Add(item);
            }
        }

        return parts;
    }

    /// <summary>
    /// A block box with rounded corners or a shadow: its blocks go into a group whose box draws its background, borders and
    /// shadow, and whose padding and borders narrow the text inside.
    /// </summary>
    private void BoxedBlock(HtmlElement element, HtmlStyle style, Flow flow, Frame parent)
    {
        Close(flow);
        double width = flow.Width;
        BoxStyle box = ReadBox(style, width, flow.PendingSpace, parent.Left, parent.Right);
        if (style.Keyword("margin-left") == "auto" && style.Keyword("margin-right") == "auto" && box.Width.Unit == WidthUnit.Points)
        {
            double free = Math.Max(0, width - parent.Left - parent.Right - box.Width.Value);
            box = box with { MarginLeft = parent.Left + (free / 2), MarginRight = parent.Right + (free / 2) };
        }

        flow.PendingSpace = 0;
        if (style.Keyword("break-before") is "page" or "always" or "left" or "right" or "recto" or "verso" || style.Keyword("page-break-before") is "always" or "left" or "right")
        {
            flow.PendingPageBreak = true;
        }

        // Inside the box its padding and borders do the indenting: the text starts from the box's own edges.
        Frame frame = new(element, style, null)
        {
            Lists = parent.Lists,
            HeadingLevel = element.LocalName is ['h', >= '1' and <= '6'] ? element.LocalName[1] - '0' : null,
        };
        double inner = Math.Max(1, box.Width.Unit == WidthUnit.Points ? box.Width.Value - box.Horizontal : width - box.MarginsX - box.Horizontal);
        Flow inside = new(flow.Container, inner) { PendingPageBreak = flow.PendingPageBreak };
        inside.PendingBookmarks.AddRange(flow.PendingBookmarks);
        flow.PendingBookmarks.Clear();
        flow.PendingPageBreak = false;
        int first = flow.Container.Blocks.Count;
        Children(element, style, inside, frame);
        Close(inside);
        ContentControl group = new(document) { Box = box };
        group.Properties.Kind = ContentControlKind.Group;
        foreach (Block block in flow.Container.Blocks.Skip(first).ToList())
        {
            flow.Container.Blocks.Remove(block);
            group.Blocks.Add(block);
        }

        if (style.Keyword("break-inside") is "avoid" or "avoid-page" || style.Keyword("page-break-inside") == "avoid")
        {
            group.Paging = new GroupPaging { KeepTogether = true };
        }

        flow.Container.Blocks.Add(group);
        flow.PendingSpace = Math.Max(0, style.Length("margin-bottom", width) ?? 0);
        if (style.Keyword("break-after") is "page" or "always" or "left" or "right" or "recto" or "verso" || style.Keyword("page-break-after") is "always" or "left" or "right")
        {
            flow.PendingPageBreak = true;
        }
    }

    // ---- Flex and grid containers --------------------------------------------------------------------------------------

    /// <summary>A flex or grid container in the flow: the container block, its box with the flow's margins, and its items.</summary>
    private void LayoutContainer(HtmlElement element, HtmlStyle style, string display, Flow flow, Frame frame)
    {
        Close(flow);
        double width = flow.Width;
        BoxStyle box = ReadBox(style, width, flow.PendingSpace, frame.Left, frame.Right);
        if (style.Keyword("margin-left") == "auto" && style.Keyword("margin-right") == "auto" && box.Width.Unit == WidthUnit.Points)
        {
            double free = Math.Max(0, width - frame.Left - frame.Right - box.Width.Value);
            box = box with { MarginLeft = frame.Left + (free / 2), MarginRight = frame.Right + (free / 2) };
        }

        flow.PendingSpace = 0;
        Block container = Container(element, style, display, box, Math.Max(1, width - box.MarginsX - box.Horizontal), flow, frame);
        int index = flow.Container.Blocks.Count;
        flow.Container.Blocks.Add(container);
        if (style.Keyword("break-inside") is "avoid" or "avoid-page" || style.Keyword("page-break-inside") == "avoid")
        {
            Group(flow.Container.Blocks, index);
        }

        flow.PendingSpace = Math.Max(0, style.Length("margin-bottom", width) ?? 0);
    }

    /// <summary>A flex or grid container read from an element, with its items; <paramref name="flow"/> takes its out-of-flow children.</summary>
    private Block Container(HtmlElement element, HtmlStyle style, string display, BoxStyle? box, double inner, Flow flow, Frame frame)
    {
        if (display is "grid" or "inline-grid")
        {
            GridLayout layout = ReadGridLayout(style, inner);
            GridContainer grid = new(document) { Layout = layout, Box = box };
            int columns = Math.Max(layout.Columns.Count, layout.Areas.Select(a => a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length).DefaultIfEmpty(0).Max());
            int rows = Math.Max(layout.Rows.Count, layout.Areas.Count);
            ReadItems(element, style, inner, flow, frame, () => grid.Items.Add(new GridItem(document)), (item, childStyle) =>
            {
                GridItem gridItem = (GridItem)item;
                ReadGridPlacement(gridItem, childStyle, columns, rows);
                gridItem.JustifySelf = Alignment(childStyle.Keyword("justify-self") ?? PlaceKeyword(childStyle, "place-self", 1));
                gridItem.AlignSelf = Alignment(childStyle.Keyword("align-self") ?? PlaceKeyword(childStyle, "place-self", 0));
            });
            return grid;
        }

        FlexContainer flex = new(document) { Layout = ReadFlexLayout(style, inner), Box = box };
        ReadItems(element, style, inner, flow, frame, () => flex.Items.Add(new FlexItem(document)), (item, childStyle) => ReadFlexItem((FlexItem)item, childStyle, inner));
        return flex;
    }

    /// <summary>
    /// The container's in-flow children as items: each element is one (blockified), each run of text an anonymous one;
    /// absolutely positioned children and running elements leave the flow as they do elsewhere.
    /// </summary>
    private void ReadItems(HtmlElement element, HtmlStyle style, double inner, Flow flow, Frame frame, Func<BlockContainer> add, Action<BlockContainer, HtmlStyle> configure)
    {
        if (depth >= options.Limits.MaxDepth)
        {
            // Hostile nesting: what lies deeper is one item holding its text.
            Warn(ConversionWarningKind.LayoutApproximated, "Elements nested deeper than the limit, read as plain text");
            Flow deep = new(add(), inner);
            AddText(string.Concat(element.Descendants().OfType<HtmlText>().Select(t => t.Data)), style, deep, new Frame(element, style, null));
            Close(deep);
            return;
        }

        depth++;
        try
        {
            foreach (HtmlNode child in element.Children)
            {
                if (child is HtmlText text)
                {
                    if (!string.IsNullOrWhiteSpace(text.Data))
                    {
                        BlockContainer anonymous = add();
                        Flow itemFlow = new(anonymous, inner);
                        AddText(text.Data, style, itemFlow, new Frame(element, style, null));
                        Close(itemFlow);
                    }

                    continue;
                }

                if (child is not HtmlElement childElement || childElement.NamespaceUri != HtmlElement.HtmlNamespace)
                {
                    continue;
                }

                HtmlStyle childStyle = StyleOf(childElement, style);
                if (childStyle.Keyword("display") == "none" || childStyle.Keyword("position") is "absolute" or "fixed" || RunningName(childStyle) is not null)
                {
                    // Out of the container's flow: placed (or dropped) as the outer flow places it.
                    Element(childElement, style, flow, frame);
                    continue;
                }

                BlockContainer item = add();
                BoxStyle box = ReadBox(childStyle, inner);
                switch (item)
                {
                    case FlexItem flexItem:
                        flexItem.Box = box;
                        break;
                    case GridItem gridItem:
                        gridItem.Box = box;
                        break;
                }

                configure(item, childStyle);
                FillItem(item, childElement, childStyle, Math.Max(1, (box.Width.Unit == WidthUnit.Points ? box.Width.Value : inner) - box.Horizontal), flow);
            }
        }
        finally
        {
            depth--;
        }
    }

    /// <summary>An item's content: the element's children, or the element itself where it is inline content, a picture, a table or a container.</summary>
    private void FillItem(BlockContainer item, HtmlElement element, HtmlStyle style, double width, Flow outer)
    {
        Flow flow = new(item, width);
        Frame frame = new(element, style, null) { HeadingLevel = element.LocalName is ['h', >= '1' and <= '6'] ? element.LocalName[1] - '0' : null };
        if (element.GetAttribute("id", null) is { Length: > 0 } id)
        {
            flow.PendingBookmarks.Add(id);
        }

        string display = style.Keyword("display") ?? "inline";
        string? outerLink = currentLink;
        switch (display)
        {
            case "flex" or "grid" or "inline-flex" or "inline-grid":
                // The item is a container too: its box is the item's, its layout a container inside it.
                flow.Container.Blocks.Add(Container(element, style, display, null, width, outer, frame));
                break;
            case "table" or "inline-table":
                BuildTable(element, style, flow, frame);
                break;
            case "inline" or "inline-block" when element.LocalName is not ("img" or "br" or "input" or "select" or "textarea" or "button"):
                InlineElement(element, style, flow, frame);
                break;
            default:
                if (element.LocalName is "img" or "br" or "input" or "select" or "textarea")
                {
                    Element(element, style.Parent ?? style, flow, frame);
                }
                else
                {
                    Children(element, style, flow, frame);
                }

                break;
        }

        currentLink = outerLink;
        Close(flow);
    }

    // ---- Flex properties ----------------------------------------------------------------------------------------------

    private static FlexLayout ReadFlexLayout(HtmlStyle style, double inner)
    {
        IReadOnlyList<string> flow = style.Keywords("flex-flow");
        string? direction = style.Keyword("flex-direction") ?? flow.FirstOrDefault(k => k is "row" or "row-reverse" or "column" or "column-reverse");
        string? wrap = style.Keyword("flex-wrap") ?? flow.FirstOrDefault(k => k is "nowrap" or "wrap" or "wrap-reverse");
        (double rowGap, double columnGap) = Gaps(style, inner);
        return new FlexLayout
        {
            Direction = direction switch
            {
                "row-reverse" => FlexDirection.RowReverse,
                "column" => FlexDirection.Column,
                "column-reverse" => FlexDirection.ColumnReverse,
                _ => FlexDirection.Row,
            },
            Wrap = wrap switch
            {
                "wrap" => FlexWrap.Wrap,
                "wrap-reverse" => FlexWrap.WrapReverse,
                _ => FlexWrap.NoWrap,
            },
            JustifyContent = Alignment(style.Keyword("justify-content") ?? PlaceKeyword(style, "place-content", 1)) is { } justify and not LayoutAlignment.Stretch ? justify : LayoutAlignment.Start,
            AlignItems = Alignment(style.Keyword("align-items") ?? PlaceKeyword(style, "place-items", 0)) ?? LayoutAlignment.Stretch,
            AlignContent = Alignment(style.Keyword("align-content") ?? PlaceKeyword(style, "place-content", 0)) ?? LayoutAlignment.Stretch,
            RowGap = rowGap,
            ColumnGap = columnGap,
        };
    }

    private static void ReadFlexItem(FlexItem item, HtmlStyle style, double inner)
    {
        double grow = 0, shrink = 1;
        TableWidth basis = TableWidth.Auto;
        if (style.Computed.TryGetValue("flex", out IReadOnlyList<CssComponentValue>? shorthand))
        {
            CssComponentValue[] parts = [.. shorthand.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
            string? only = parts is [CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident }] ? ident.Value.ToLowerInvariant() : null;
            switch (only)
            {
                case "none":
                    shrink = 0;
                    break;
                case "auto":
                    grow = 1;
                    break;
                case "initial" or "unset" or "inherit" or "revert":
                    break;
                default:
                    {
                        // Numbers are grow then shrink; a length, a percentage or auto is the basis. One number alone has a basis of 0.
                        List<double> numbers = [];
                        bool basisGiven = false;
                        foreach (CssComponentValue part in parts)
                        {
                            if (part is CssTokenValue { Token: { Kind: CssTokenKind.Number } number } && !(number.Number == 0 && numbers.Count == 2))
                            {
                                numbers.Add(number.Number);
                            }
                            else if (Basis(style, part, inner) is { } given)
                            {
                                basis = given;
                                basisGiven = true;
                            }
                        }

                        if (numbers.Count > 0)
                        {
                            grow = numbers[0];
                            shrink = numbers.Count > 1 ? numbers[1] : 1;
                            if (!basisGiven)
                            {
                                basis = TableWidth.Points(0);
                            }
                        }

                        break;
                    }
            }
        }

        if (Number(style, "flex-grow") is double g)
        {
            grow = g;
        }

        if (Number(style, "flex-shrink") is double s)
        {
            shrink = s;
        }

        if (style.Computed.TryGetValue("flex-basis", out IReadOnlyList<CssComponentValue>? basisValue)
            && basisValue.FirstOrDefault(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace }) is { } basisItem && Basis(style, basisItem, inner) is { } longhand)
        {
            basis = longhand;
        }

        // A basis of content size, or of auto with a width, is taken from the box; the reader adds the box's padding and borders
        // where box-sizing leaves them out, as it does for widths.
        if (basis.Unit == WidthUnit.Points && basis.Value > 0 && style.Keyword("box-sizing") != "border-box" && item.Box is { } box)
        {
            basis = TableWidth.Points(basis.Value + box.Horizontal);
        }

        item.Grow = Math.Max(0, grow);
        item.Shrink = Math.Max(0, shrink);
        item.Basis = basis;
        item.Order = Number(style, "order") is double order ? (int)Math.Clamp(order, -10000, 10000) : 0;
        item.AlignSelf = Alignment(style.Keyword("align-self"));
    }

    /// <summary>A flex basis: a length or percentage, auto or content (automatic); null when the item is not one.</summary>
    private static TableWidth? Basis(HtmlStyle style, CssComponentValue item, double inner)
    {
        if (item is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } && ident.Value.ToLowerInvariant() is "auto" or "content")
        {
            return TableWidth.Auto;
        }

        if (item is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } percent })
        {
            return TableWidth.Percent(Math.Max(0, percent.Number));
        }

        return style.Length(item, inner) is double points ? TableWidth.Points(Math.Max(0, points)) : null;
    }

    private static double? Number(HtmlStyle style, string property)
    {
        return style.Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? value)
            && value.FirstOrDefault(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace }) is CssTokenValue { Token: { Kind: CssTokenKind.Number } number }
            ? number.Number : null;
    }

    /// <summary>The row and column gaps: gap (one value for both, or row then column), row-gap and column-gap, and the grid- forms.</summary>
    private static (double Row, double Column) Gaps(HtmlStyle style, double inner)
    {
        double row = 0, column = 0;
        foreach (string shorthand in (ReadOnlySpan<string>)["grid-gap", "gap"])
        {
            if (style.Computed.TryGetValue(shorthand, out IReadOnlyList<CssComponentValue>? value))
            {
                CssComponentValue[] parts = [.. value.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
                if (parts.Length > 0)
                {
                    row = style.Length(parts[0], inner) ?? 0;
                    column = parts.Length > 1 ? style.Length(parts[1], inner) ?? 0 : row;
                }
            }
        }

        row = style.Length("row-gap", inner) ?? style.Length("grid-row-gap", inner) ?? row;
        column = style.Length("column-gap", inner) ?? style.Length("grid-column-gap", inner) ?? column;
        return (Math.Max(0, row), Math.Max(0, column));
    }

    /// <summary>The keyword at <paramref name="index"/> of a place- shorthand (align then justify), or its only one.</summary>
    private static string? PlaceKeyword(HtmlStyle style, string property, int index)
    {
        IReadOnlyList<string> keywords = style.Keywords(property);
        return keywords.Count == 0 ? null : keywords[Math.Min(index, keywords.Count - 1)];
    }

    /// <summary>A CSS box alignment keyword as the model's; null for normal and auto, which leave the choice to the container.</summary>
    private static LayoutAlignment? Alignment(string? keyword)
    {
        return keyword switch
        {
            "flex-start" or "start" or "self-start" or "left" => LayoutAlignment.Start,
            "flex-end" or "end" or "self-end" or "right" => LayoutAlignment.End,
            "center" => LayoutAlignment.Center,
            "stretch" => LayoutAlignment.Stretch,
            "space-between" => LayoutAlignment.SpaceBetween,
            "space-around" => LayoutAlignment.SpaceAround,
            "space-evenly" => LayoutAlignment.SpaceEvenly,
            "baseline" or "first" or "last" => LayoutAlignment.Baseline,
            _ => null,
        };
    }

    // ---- Grid properties ----------------------------------------------------------------------------------------------

    private GridLayout ReadGridLayout(HtmlStyle style, double inner)
    {
        (double rowGap, double columnGap) = Gaps(style, inner);
        List<string> areas = [];
        if (style.Computed.TryGetValue("grid-template-areas", out IReadOnlyList<CssComponentValue>? areaValue))
        {
            areas.AddRange(areaValue.OfType<CssTokenValue>().Where(t => t.Token.Kind == CssTokenKind.String).Select(t => t.Token.Value));
        }

        IReadOnlyList<CssComponentValue>? columns = style.Computed.TryGetValue("grid-template-columns", out IReadOnlyList<CssComponentValue>? c) ? c : null;
        IReadOnlyList<CssComponentValue>? rows = style.Computed.TryGetValue("grid-template-rows", out IReadOnlyList<CssComponentValue>? r) ? r : null;
        if ((columns is null || rows is null) && style.Computed.TryGetValue("grid-template", out IReadOnlyList<CssComponentValue>? template))
        {
            // The simple form of grid-template: rows / columns.
            List<List<CssComponentValue>> halves = SplitTopLevel(template, CssTokenKind.Delim, "/");
            if (halves.Count == 2)
            {
                rows ??= halves[0];
                columns ??= halves[1];
            }
        }

        string[] flow = [.. style.Keywords("grid-auto-flow")];
        bool byColumn = flow.Contains("column"), dense = flow.Contains("dense");
        return new GridLayout
        {
            Columns = columns is null ? [] : Tracks(style, columns, inner, columnGap),
            Rows = rows is null ? [] : Tracks(style, rows, inner, rowGap),
            AutoColumns = style.Computed.TryGetValue("grid-auto-columns", out IReadOnlyList<CssComponentValue>? autoColumns) && Tracks(style, autoColumns, inner, 0) is [var autoColumn, ..] ? autoColumn : GridTrack.Auto,
            AutoRows = style.Computed.TryGetValue("grid-auto-rows", out IReadOnlyList<CssComponentValue>? autoRows) && Tracks(style, autoRows, inner, 0) is [var autoRow, ..] ? autoRow : GridTrack.Auto,
            AutoFlow = (byColumn, dense) switch
            {
                (true, true) => GridAutoFlow.ColumnDense,
                (true, false) => GridAutoFlow.Column,
                (false, true) => GridAutoFlow.RowDense,
                _ => GridAutoFlow.Row,
            },
            Areas = areas,
            RowGap = rowGap,
            ColumnGap = columnGap,
            JustifyItems = Alignment(style.Keyword("justify-items") ?? PlaceKeyword(style, "place-items", 1)) ?? LayoutAlignment.Stretch,
            AlignItems = Alignment(style.Keyword("align-items") ?? PlaceKeyword(style, "place-items", 0)) ?? LayoutAlignment.Stretch,
            JustifyContent = Alignment(style.Keyword("justify-content") ?? PlaceKeyword(style, "place-content", 1)) ?? LayoutAlignment.Stretch,
        };
    }

    /// <summary>
    /// A track list: lengths, percentages, fr, auto, min-content, max-content, minmax(), fit-content() and repeat(), with a
    /// count or auto-fill and auto-fit (as many as fit <paramref name="inner"/>); line names are passed over.
    /// </summary>
    private List<GridTrack> Tracks(HtmlStyle style, IReadOnlyList<CssComponentValue> value, double inner, double gap)
    {
        List<GridTrack> tracks = [];
        foreach (CssComponentValue item in value)
        {
            if (tracks.Count >= 1000)
            {
                break;
            }

            if (item is CssFunction { Name: var name } function && name.Equals("repeat", StringComparison.OrdinalIgnoreCase))
            {
                List<List<CssComponentValue>> arguments = SplitTopLevel(function.Arguments, CssTokenKind.Comma);
                if (arguments.Count != 2 || arguments[0] is not [CssTokenValue { Token: var count }])
                {
                    continue;
                }

                List<GridTrack> repeated = Tracks(style, arguments[1], inner, gap);
                if (repeated.Count == 0)
                {
                    continue;
                }

                int times;
                if (count.Kind == CssTokenKind.Number)
                {
                    times = (int)Math.Clamp(count.Number, 1, 1000);
                }
                else
                {
                    // auto-fill and auto-fit: as many repetitions as fit, each track at its fixed size (or its least).
                    double each = repeated.Sum(t => Size(t.Max, inner) ?? Size(t.Min, inner) ?? 0) + (gap * repeated.Count);
                    times = each <= 0 ? 1 : Math.Clamp((int)Math.Floor((inner + gap) / each), 1, 1000);
                    if (count.Value.Equals("auto-fit", StringComparison.OrdinalIgnoreCase))
                    {
                        Warn(ConversionWarningKind.LayoutApproximated, "Grid auto-fit tracks, kept even when empty");
                    }
                }

                for (int i = 0; i < times && tracks.Count < 1000; i++)
                {
                    tracks.AddRange(repeated);
                }

                continue;
            }

            if (TrackOf(style, item, inner) is { } track)
            {
                tracks.Add(track);
            }
        }

        return tracks;
    }

    private static GridTrack? TrackOf(HtmlStyle style, CssComponentValue item, double inner)
    {
        if (item is CssFunction { Name: var name } function)
        {
            List<List<CssComponentValue>> arguments = SplitTopLevel(function.Arguments, CssTokenKind.Comma);
            if (name.Equals("minmax", StringComparison.OrdinalIgnoreCase) && arguments is [[var min], [var max]]
                && TrackSize(style, min, inner) is { Unit: not GridTrackUnit.Fraction } low && TrackSize(style, max, inner) is { } high)
            {
                return GridTrack.MinMax(low, high);
            }

            if (name.Equals("fit-content", StringComparison.OrdinalIgnoreCase) && arguments is [[var limit]] && TrackSize(style, limit, inner) is { } size)
            {
                // Grows with its content up to the limit: the limit as the greatest size, the content's as the least.
                return GridTrack.MinMax(GridTrackSize.Auto, size);
            }

            return name.Equals("calc", StringComparison.OrdinalIgnoreCase) && style.Length(item, inner) is double points ? GridTrack.Points(points) : null;
        }

        return TrackSize(style, item, inner) is { } single ? GridTrack.Of(single) : null;
    }

    private static GridTrackSize? TrackSize(HtmlStyle style, CssComponentValue item, double inner)
    {
        if (item is CssTokenValue { Token: var token })
        {
            switch (token.Kind)
            {
                case CssTokenKind.Dimension when token.Unit.Equals("fr", StringComparison.OrdinalIgnoreCase):
                    return GridTrackSize.Fraction(Math.Max(0, token.Number));
                case CssTokenKind.Percentage:
                    return GridTrackSize.Percent(Math.Max(0, token.Number));
                case CssTokenKind.Ident:
                    return token.Value.ToLowerInvariant() switch
                    {
                        "auto" => GridTrackSize.Auto,
                        "min-content" => GridTrackSize.MinContent,
                        "max-content" => GridTrackSize.MaxContent,
                        _ => null,
                    };
            }
        }

        return style.Length(item, inner) is double points ? GridTrackSize.Points(Math.Max(0, points)) : null;
    }

    private static double? Size(GridTrackSize size, double inner)
    {
        return size.Unit switch
        {
            GridTrackUnit.Points => size.Value,
            GridTrackUnit.Percent => inner * size.Value / 100,
            _ => null,
        };
    }

    /// <summary>
    /// An item's place: grid-area (a name, or row-start / column-start / row-end / column-end), grid-row and grid-column
    /// (start / end, with spans), and their -start and -end longhands.
    /// </summary>
    private void ReadGridPlacement(GridItem item, HtmlStyle style, int explicitColumns, int explicitRows)
    {
        List<List<CssComponentValue>> area = style.Computed.TryGetValue("grid-area", out IReadOnlyList<CssComponentValue>? areaValue) ? SplitTopLevel(areaValue, CssTokenKind.Delim, "/") : [];
        if (area is [[CssTokenValue { Token: { Kind: CssTokenKind.Ident } name }]] && !name.Value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            item.Area = name.Value;
            return;
        }

        List<CssComponentValue>? Part(int index) => index < area.Count ? area[index] : null;
        (int? row, int rowSpan) = Lines(style, "grid-row", Part(0), Part(2), explicitRows);
        (int? column, int columnSpan) = Lines(style, "grid-column", Part(1), Part(3), explicitColumns);
        item.Row = row;
        item.RowSpan = rowSpan;
        item.Column = column;
        item.ColumnSpan = columnSpan;
    }

    /// <summary>One axis of an item's place: its start line (null for automatic) and how many tracks it spans.</summary>
    private (int? Start, int Span) Lines(HtmlStyle style, string property, List<CssComponentValue>? areaStart, List<CssComponentValue>? areaEnd, int explicitTracks)
    {
        List<CssComponentValue>? start = areaStart, end = areaEnd;
        if (style.Computed.TryGetValue(property, out IReadOnlyList<CssComponentValue>? shorthand))
        {
            List<List<CssComponentValue>> halves = SplitTopLevel(shorthand, CssTokenKind.Delim, "/");
            start = halves[0];
            end = halves.Count > 1 ? halves[1] : null;
        }

        if (style.Computed.TryGetValue(property + "-start", out IReadOnlyList<CssComponentValue>? startValue))
        {
            start = [.. startValue.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
        }

        if (style.Computed.TryGetValue(property + "-end", out IReadOnlyList<CssComponentValue>? endValue))
        {
            end = [.. endValue.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
        }

        (int? startLine, int? startSpan) = Line(start);
        (int? endLine, int? endSpan) = Line(end);
        int Resolve(int line) => line > 0 ? line : Math.Max(1, explicitTracks + 2 + line);
        if (startLine is int s && endLine is int e)
        {
            int from = Resolve(s), to = Resolve(e);
            return (Math.Min(from, to), Math.Clamp(Math.Abs(to - from), 1, 1000));
        }

        if (startLine is int only)
        {
            return (only, Math.Clamp(endSpan ?? 1, 1, 1000));
        }

        if (endLine is int last)
        {
            int span = Math.Clamp(startSpan ?? 1, 1, 1000);
            return (Math.Max(1, Resolve(last) - span), span);
        }

        return (null, Math.Clamp(startSpan ?? endSpan ?? 1, 1, 1000));

        (int? Line, int? Span) Line(List<CssComponentValue>? parts)
        {
            if (parts is null)
            {
                return (null, null);
            }

            bool span = false;
            int? number = null;
            foreach (CssComponentValue part in parts)
            {
                switch (part)
                {
                    case CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } when ident.Value.Equals("span", StringComparison.OrdinalIgnoreCase):
                        span = true;
                        break;
                    case CssTokenValue { Token: { Kind: CssTokenKind.Number, IsInteger: true } value } when value.Number != 0:
                        number = (int)Math.Clamp(value.Number, -1000, 1000);
                        break;
                    case CssTokenValue { Token: { Kind: CssTokenKind.Ident } named } when !named.Value.Equals("auto", StringComparison.OrdinalIgnoreCase):
                        Warn(ConversionWarningKind.LayoutApproximated, "Named grid lines, placed automatically");
                        break;
                }
            }

            return span ? (null, number ?? 1) : (number, null);
        }
    }
}
