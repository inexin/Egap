using System.Globalization;
using System.Text;

namespace Egap.Documents.Html;

/// <summary>Flex and grid containers, boxed groups and positioned text boxes written as CSS layout.</summary>
internal sealed partial class HtmlWriter
{
    /// <summary>Positioned text boxes met in the paragraph being written, written after it as positioned divisions.</summary>
    private readonly List<Shape> positioned = [];

    private void WriteFlex(FlexContainer flex, int depth)
    {
        FlexLayout layout = flex.Layout;
        List<(string, string)> css = [("display", "flex")];
        if (layout.Direction != FlexDirection.Row)
        {
            css.Add(("flex-direction", layout.Direction switch { FlexDirection.RowReverse => "row-reverse", FlexDirection.Column => "column", _ => "column-reverse" }));
        }

        if (layout.Wrap != FlexWrap.NoWrap)
        {
            css.Add(("flex-wrap", layout.Wrap == FlexWrap.Wrap ? "wrap" : "wrap-reverse"));
        }

        AddAlignment(css, "justify-content", layout.JustifyContent, LayoutAlignment.Start);
        AddAlignment(css, "align-items", layout.AlignItems, LayoutAlignment.Stretch);
        AddAlignment(css, "align-content", layout.AlignContent, LayoutAlignment.Stretch);
        AddGaps(css, layout.RowGap, layout.ColumnGap);
        BoxCss(css, flex.Box);
        Open(css);
        foreach (FlexItem item in flex.Items)
        {
            List<(string, string)> itemCss = [("flex", $"{Number(item.Grow)} {Number(item.Shrink)} {Basis(item.Basis)}")];
            if (item.Order != 0)
            {
                itemCss.Add(("order", item.Order.ToString(CultureInfo.InvariantCulture)));
            }

            if (item.AlignSelf is { } align)
            {
                AddAlignment(itemCss, "align-self", align, null);
            }

            BoxCss(itemCss, item.Box);
            Open(itemCss);
            WriteBlocks(item.Blocks, depth + 1);
            body.Append("</div>\n");
        }

        body.Append("</div>\n");
    }

    private void WriteGrid(GridContainer grid, int depth)
    {
        GridLayout layout = grid.Layout;
        List<(string, string)> css = [("display", "grid")];
        if (layout.Columns.Count > 0)
        {
            css.Add(("grid-template-columns", string.Join(' ', layout.Columns.Select(Track))));
        }

        if (layout.Rows.Count > 0)
        {
            css.Add(("grid-template-rows", string.Join(' ', layout.Rows.Select(Track))));
        }

        if (layout.Areas.Count > 0)
        {
            css.Add(("grid-template-areas", string.Join(' ', layout.Areas.Select(a => "\"" + a.Replace("\"", "", StringComparison.Ordinal) + "\""))));
        }

        if (layout.AutoRows != GridTrack.Auto)
        {
            css.Add(("grid-auto-rows", Track(layout.AutoRows)));
        }

        if (layout.AutoColumns != GridTrack.Auto)
        {
            css.Add(("grid-auto-columns", Track(layout.AutoColumns)));
        }

        if (layout.AutoFlow != GridAutoFlow.Row)
        {
            css.Add(("grid-auto-flow", layout.AutoFlow switch { GridAutoFlow.Column => "column", GridAutoFlow.RowDense => "row dense", _ => "column dense" }));
        }

        AddAlignment(css, "justify-items", layout.JustifyItems, LayoutAlignment.Stretch);
        AddAlignment(css, "align-items", layout.AlignItems, LayoutAlignment.Stretch);
        AddAlignment(css, "justify-content", layout.JustifyContent, LayoutAlignment.Stretch);
        AddGaps(css, layout.RowGap, layout.ColumnGap);
        BoxCss(css, grid.Box);
        Open(css);
        foreach (GridItem item in grid.Items)
        {
            List<(string, string)> itemCss = [];
            if (item.Area is { } area)
            {
                itemCss.Add(("grid-area", area));
            }
            else
            {
                Place(itemCss, "grid-column", item.Column, item.ColumnSpan);
                Place(itemCss, "grid-row", item.Row, item.RowSpan);
            }

            if (item.JustifySelf is { } justify)
            {
                AddAlignment(itemCss, "justify-self", justify, null);
            }

            if (item.AlignSelf is { } align)
            {
                AddAlignment(itemCss, "align-self", align, null);
            }

            BoxCss(itemCss, item.Box);
            Open(itemCss);
            WriteBlocks(item.Blocks, depth + 1);
            body.Append("</div>\n");
        }

        body.Append("</div>\n");

        static void Place(List<(string, string)> css, string property, int? line, int span)
        {
            if (line is int start)
            {
                css.Add((property, start.ToString(CultureInfo.InvariantCulture) + (span > 1 ? " / span " + span.ToString(CultureInfo.InvariantCulture) : "")));
            }
            else if (span > 1)
            {
                css.Add((property, "span " + span.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    /// <summary>A group with a box: a division with the box's CSS around the group's blocks.</summary>
    private void WriteBoxed(ContentControl group, BoxStyle box, int depth)
    {
        List<(string, string)> css = [];
        BoxCss(css, box);
        if (group.Paging?.KeepTogether == true)
        {
            css.Add(("break-inside", "avoid"));
        }

        Open(css);
        WriteBlocks(group.Blocks, depth);
        body.Append("</div>\n");
    }

    /// <summary>The positioned text boxes met in the paragraph just written, as absolutely positioned divisions.</summary>
    private void WritePositioned(int depth)
    {
        if (positioned.Count == 0)
        {
            return;
        }

        List<Shape> shapes = [.. positioned];
        positioned.Clear();
        foreach (Shape shape in shapes)
        {
            FloatingPlacement placement = shape.Floating!;
            List<(string, string)> css = [("position", "absolute")];
            if (placement.HorizontalAlignment is FloatingAlignment.End or FloatingAlignment.Outside)
            {
                css.Add(("right", "0"));
            }
            else
            {
                css.Add(("left", Points(placement.HorizontalAlignment == FloatingAlignment.Center ? 0 : placement.HorizontalOffset)));
            }

            if (placement.VerticalAnchor is VerticalAnchor.Margin or VerticalAnchor.Page)
            {
                css.Add(placement.VerticalAlignment is FloatingAlignment.End or FloatingAlignment.Outside ? ("bottom", "0") : ("top", Points(placement.VerticalOffset)));
            }
            else if (placement.VerticalOffset != 0)
            {
                css.Add(("margin-top", Points(placement.VerticalOffset)));
            }

            if (shape.Width > 0)
            {
                css.Add(("width", Points(shape.Width)));
            }

            if (shape.Height > 0)
            {
                css.Add((shape.FitToText ? "min-height" : "height", Points(shape.Height)));
            }

            css.Add(("box-sizing", "border-box"));
            if (shape.TextInsets is { } insets)
            {
                css.Add(("padding", $"{Points(insets.Top)} {Points(insets.Right)} {Points(insets.Bottom)} {Points(insets.Left)}"));
            }

            if (shape.Fill is { } fill && Color(fill) is { } background)
            {
                css.Add(("background-color", background));
            }

            AddBorders(css, shape.Outline, shape.Outline, shape.Outline, shape.Outline);
            if (shape.CornerRadius > 0 || shape.Kind == ShapeKind.RoundedRectangle)
            {
                css.Add(("border-radius", Points(shape.CornerRadius > 0 ? shape.CornerRadius : Math.Min(shape.Width, shape.Height) / 6)));
            }

            if (shape.Shadow is { } shadow)
            {
                css.Add(("box-shadow", Shadow(shadow)));
            }

            if (placement.Wrap == WrapMode.BehindText)
            {
                css.Add(("z-index", "-1"));
            }

            Open(css);
            WriteBlocks(shape.TextBox!.Blocks, depth + 1);
            body.Append("</div>\n");
        }
    }

    /// <summary>Whether a shape is written as a positioned division: a floating text box.</summary>
    private static bool IsPositioned(Shape shape)
    {
        return shape is { TextBox: not null, Floating: { Wrap: WrapMode.InFrontOfText or WrapMode.BehindText } };
    }

    private void Open(List<(string, string)> css)
    {
        body.Append("<div");
        if (css.Count > 0)
        {
            body.Append(" style=\"").Append(Attribute(Declarations(css).Trim())).Append('"');
        }

        body.Append(">\n");
    }

    /// <summary>A box's CSS: margins, padding, borders, background, corners, shadow, width (borders and padding included) and least height.</summary>
    private void BoxCss(List<(string, string)> css, BoxStyle? box)
    {
        if (box is null)
        {
            return;
        }

        if (box.MarginTop != 0 || box.MarginRight != 0 || box.MarginBottom != 0 || box.MarginLeft != 0)
        {
            css.Add(("margin", $"{Points(box.MarginTop)} {Points(box.MarginRight)} {Points(box.MarginBottom)} {Points(box.MarginLeft)}"));
        }

        if (box.PaddingTop != 0 || box.PaddingRight != 0 || box.PaddingBottom != 0 || box.PaddingLeft != 0)
        {
            css.Add(("padding", $"{Points(box.PaddingTop)} {Points(box.PaddingRight)} {Points(box.PaddingBottom)} {Points(box.PaddingLeft)}"));
        }

        AddBorders(css, box.BorderTop, box.BorderRight, box.BorderBottom, box.BorderLeft);
        if (box.Background is { } fill && Color(fill) is { } background)
        {
            css.Add(("background-color", background));
        }

        if (!box.CornerRadius.IsSquare)
        {
            BoxCorners r = box.CornerRadius;
            css.Add(("border-radius", $"{Points(r.TopLeft)} {Points(r.TopRight)} {Points(r.BottomRight)} {Points(r.BottomLeft)}"));
        }

        if (box.Shadow is { } shadow)
        {
            css.Add(("box-shadow", Shadow(shadow)));
        }

        if (box.Width.Unit != WidthUnit.Auto || box.MinHeight > 0)
        {
            css.Add(("box-sizing", "border-box"));
        }

        if (box.Width.Unit == WidthUnit.Points)
        {
            css.Add(("width", Points(box.Width.Value)));
        }
        else if (box.Width.Unit == WidthUnit.Percent)
        {
            css.Add(("width", Number(box.Width.Value) + "%"));
        }

        if (box.MinHeight > 0)
        {
            css.Add(("min-height", Points(box.MinHeight)));
        }
    }

    private string Shadow(BoxShadow shadow)
    {
        Egami.Pixels.Rgba8 rgb = shadow.Color.Resolve(document.Theme, new Egami.Pixels.Rgba8(0, 0, 0, 255));
        string color = shadow.Opacity >= 1 ? $"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}" : $"rgba({rgb.R}, {rgb.G}, {rgb.B}, {Number(Math.Round(Math.Clamp(shadow.Opacity, 0, 1), 3))})";
        return $"{Points(shadow.OffsetX)} {Points(shadow.OffsetY)} {Points(shadow.Blur)} {Points(shadow.Spread)} {color}";
    }

    private static void AddAlignment(List<(string, string)> css, string property, LayoutAlignment alignment, LayoutAlignment? initial)
    {
        if (alignment == initial)
        {
            return;
        }

        css.Add((property, alignment switch
        {
            LayoutAlignment.Start => "flex-start",
            LayoutAlignment.End => "flex-end",
            LayoutAlignment.Center => "center",
            LayoutAlignment.Stretch => "stretch",
            LayoutAlignment.SpaceBetween => "space-between",
            LayoutAlignment.SpaceAround => "space-around",
            LayoutAlignment.SpaceEvenly => "space-evenly",
            _ => "baseline",
        }));
    }

    private static void AddGaps(List<(string, string)> css, double row, double column)
    {
        if (row != 0 || column != 0)
        {
            css.Add(("gap", row == column ? Points(row) : $"{Points(row)} {Points(column)}"));
        }
    }

    private static string Basis(TableWidth basis)
    {
        return basis.Unit switch
        {
            WidthUnit.Points => Points(basis.Value),
            WidthUnit.Percent => Number(basis.Value) + "%",
            _ => "auto",
        };
    }

    private static string Track(GridTrack track)
    {
        if (track.Min == track.Max || (track.Min == GridTrackSize.Auto && track.Max.Unit == GridTrackUnit.Fraction))
        {
            return Size(track.Max);
        }

        return $"minmax({Size(track.Min)}, {Size(track.Max)})";

        static string Size(GridTrackSize size) => size.Unit switch
        {
            GridTrackUnit.Points => Points(size.Value),
            GridTrackUnit.Percent => Number(size.Value) + "%",
            GridTrackUnit.Fraction => Number(size.Value) + "fr",
            GridTrackUnit.MinContent => "min-content",
            GridTrackUnit.MaxContent => "max-content",
            _ => "auto",
        };
    }

    private static string Number(double value)
    {
        return Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
