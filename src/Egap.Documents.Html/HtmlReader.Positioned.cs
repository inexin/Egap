namespace Egap.Documents.Html;

/// <summary>
/// Absolutely positioned and fixed boxes as floating text boxes: a rectangle, rounded where the box is, with the box's
/// background, border, padding and shadow, placed by its offsets in front of the text (behind it for a negative z-index).
/// A box with no positioned ancestor is placed on the page's body area as browsers print it; a fixed box goes in the header
/// so it shows on every page; a box inside a positioned ancestor is placed from that ancestor's first paragraph.
/// </summary>
internal sealed partial class HtmlReader
{
    private void Positioned(HtmlElement element, HtmlStyle style, Flow flow, Frame frame)
    {
        Section section = document.Sections[^1];
        double areaWidth = (section.PageSetup.PageWidth ?? 612) - (section.PageSetup.MarginLeft ?? 0) - (section.PageSetup.MarginRight ?? 0);
        double areaHeight = (section.PageSetup.PageHeight ?? 792) - (section.PageSetup.MarginTop ?? 0) - (section.PageSetup.MarginBottom ?? 0);
        bool isFixed = style.Keyword("position") == "fixed";
        Frame? ancestor = isFixed ? null : PositionedAncestor(frame);
        double containing = ancestor is null ? areaWidth : Math.Max(1, flow.Width - ancestor.Left - ancestor.Right);

        BoxStyle box = ReadBox(style, containing);
        double? Offset(string side) => style.Keyword(side) == "auto" ? null : style.Length(side, side is "top" or "bottom" ? areaHeight : containing);
        double? left = Offset("left"), right = Offset("right"), top = Offset("top"), bottom = Offset("bottom");
        double width = box.Width.Unit switch
        {
            WidthUnit.Points => box.Width.Value,
            WidthUnit.Percent => containing * box.Width.Value / 100,
            _ => left is double l && right is double r ? Math.Max(1, containing - l - r) : 0,
        };
        double height = style.Length("height", areaHeight) is double h && h > 0 ? h + (style.Keyword("box-sizing") != "border-box" ? box.Vertical : 0) : 0;

        Shape shape = new(document, box.CornerRadius.IsSquare ? ShapeKind.Rectangle : ShapeKind.RoundedRectangle, width, height)
        {
            Fill = box.Background,
            Outline = box.BorderTop ?? box.BorderLeft,
            CornerRadius = box.CornerRadius.IsSquare ? 0 : box.CornerRadius.TopLeft,
            Shadow = box.Shadow,
            TextInsets = (box.InsetLeft, box.InsetTop, box.InsetRight, box.InsetBottom),
            FitToText = height == 0 || width == 0,
        };
        if (!shape.FitToText && style.Length("min-height", areaHeight) is double least && least > height)
        {
            shape.Height = least;
        }

        // Placed by the offsets it has: from the start edge, from the end edge (where the size is known), or where it would
        // have been in the flow.
        HorizontalAnchor horizontalAnchor = ancestor is null ? HorizontalAnchor.Margin : HorizontalAnchor.Column;
        double baseLeft = ancestor?.Left ?? 0;
        (FloatingAlignment hAlign, double hOffset) = (left, right) switch
        {
            (double l, _) => (FloatingAlignment.None, baseLeft + l + box.MarginLeft),
            (null, double r) when width > 0 => (FloatingAlignment.None, baseLeft + containing - r - width - box.MarginRight),
            (null, double r) => (r == 0 && ancestor is null ? FloatingAlignment.End : FloatingAlignment.None, ancestor is null ? 0 : baseLeft + containing - r),
            _ => (FloatingAlignment.None, ancestor is null ? frame.Left : frame.Left),
        };
        VerticalAnchor verticalAnchor = ancestor is null && (top is not null || bottom is not null) ? VerticalAnchor.Margin : VerticalAnchor.Paragraph;
        (FloatingAlignment vAlign, double vOffset) = (top, bottom) switch
        {
            (double t, _) => (FloatingAlignment.None, t + box.MarginTop),
            (null, double b) when height > 0 && ancestor is null => (FloatingAlignment.None, areaHeight - b - height - box.MarginBottom),
            (null, double b) when ancestor is null => (b == 0 ? FloatingAlignment.End : FloatingAlignment.None, areaHeight - b),
            _ => (FloatingAlignment.None, 0),
        };
        if ((right is double rr && rr != 0 && width == 0) || (bottom is double bb && bb != 0 && height == 0) || ancestor is not null)
        {
            Warn(ConversionWarningKind.LayoutApproximated, "Positioned boxes placed from their parent's first line, or from an edge without their size");
        }

        int z = style.Text("z-index") is { } index && int.TryParse(index.Trim(), out int zIndex) ? zIndex : 0;
        shape.Floating = new FloatingPlacement
        {
            HorizontalAnchor = horizontalAnchor,
            HorizontalAlignment = hAlign,
            HorizontalOffset = hOffset,
            VerticalAnchor = verticalAnchor,
            VerticalAlignment = vAlign,
            VerticalOffset = vOffset,
            Wrap = z < 0 ? WrapMode.BehindText : WrapMode.InFrontOfText,
            DistanceLeft = 0,
            DistanceRight = 0,
            ZOrder = z,
        };

        // Its content, read as a story of its own: the text box's.
        TextBoxStory story = shape.AddTextBox();
        Flow inner = new(story, Math.Max(1, (width > 0 ? width : containing) - box.Horizontal));
        Frame innerFrame = new(element, style, null) { HeadingLevel = element.LocalName is ['h', >= '1' and <= '6'] ? element.LocalName[1] - '0' : null };
        string? outerLink = currentLink;
        if (element.LocalName == "img")
        {
            Image(element, style, inner, innerFrame);
        }
        else
        {
            Children(element, style, inner, innerFrame);
        }

        currentLink = outerLink;
        Close(inner);
        FinishFlow(inner);

        if (isFixed)
        {
            // A fixed box shows on every page: the header holds it, in a paragraph too small to move the body.
            HeaderFooter header = section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary);
            Paragraph holder = header.Blocks.OfType<Paragraph>().FirstOrDefault() ?? header.AddParagraph();
            holder.Format.LineSpacingRule = LineSpacingRule.Exactly;
            holder.Format.LineSpacing = 0.1;
            holder.Format.SpaceBefore = 0;
            holder.Format.SpaceAfter = 0;
            holder.Inlines.Add(shape);
            return;
        }

        if (ancestor is { Paragraphs: [Paragraph first, ..] })
        {
            first.Inlines.Insert(0, shape);
            return;
        }

        // Placed on the body area, the box belongs on the first page, as the browser's initial containing block is there.
        if (verticalAnchor == VerticalAnchor.Margin && section.Body.Descendants<Paragraph>().FirstOrDefault() is { } opening)
        {
            opening.Inlines.Insert(0, shape);
            return;
        }

        flow.PendingFloats.Add(shape);
    }

    /// <summary>The nearest enclosing box with a position other than static: the containing block of an absolute box.</summary>
    private static Frame? PositionedAncestor(Frame frame)
    {
        for (Frame? f = frame; f is not null; f = f.Parent)
        {
            if (f.Style.Keyword("position") is "relative" or "absolute" or "fixed" or "sticky" && f.Element.LocalName is not ("html" or "body"))
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>Puts the floats that wait for a paragraph at the start of <paramref name="paragraph"/>.</summary>
    private static void AnchorPendingFloats(Flow flow, Paragraph paragraph)
    {
        for (int i = flow.PendingFloats.Count - 1; i >= 0; i--)
        {
            paragraph.Inlines.Insert(0, flow.PendingFloats[i]);
        }

        flow.PendingFloats.Clear();
    }

    private static bool IsPositioned(HtmlStyle style)
    {
        return style.Keyword("position") is "absolute" or "fixed";
    }
}
