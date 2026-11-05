using System.Globalization;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// Frames and shapes (ODF 1.3 Part 3, 10): pictures, text boxes, simple shapes, and embedded objects (formulas, charts, OLE
/// objects) as islands drawn as their replacement picture.
/// </summary>
internal sealed partial class OdtReader
{
    private int drawingCount;

    /// <summary>The inline a drawing element becomes, or null when there is nothing to show.</summary>
    private Inline? ReadDrawing(XElement element, InlineContext? context)
    {
        if (++drawingCount > MaxEntries * 10)
        {
            throw new DocumentLimitException($"The document has more than {MaxEntries * 10} drawings.");
        }

        Enter();
        try
        {
            switch (element.Name.LocalName)
            {
                case "frame":
                    return ReadFrame(element);
                case "a":
                    // A linked frame: the frame, with the link kept on a picture as its source.
                    foreach (XElement child in element.Elements())
                    {
                        if (ReadDrawing(child, context) is { } inner)
                        {
                            return inner;
                        }
                    }

                    return null;
                case "rect" or "ellipse" or "circle" or "line" or "custom-shape":
                    return ReadShape(element);
                case "g" or "polygon" or "polyline" or "path" or "connector" or "measure" or "regular-polygon" or "caption" or "control":
                    Warn(ConversionWarningKind.DrawingDropped, "Shapes other than rectangles, ellipses, lines and text boxes are kept, not drawn.");
                    return KeptShape(element, element.Name.LocalName == "g" ? IslandKind.ShapeGroup : IslandKind.Other);
                default:
                    return null;
            }
        }
        finally
        {
            depth--;
        }
    }

    private Inline? ReadFrame(XElement frame)
    {
        double width = Size(frame.Attr(SvgNs, "width") ?? frame.Attr(FoNs, "min-width"));
        double height = Size(frame.Attr(SvgNs, "height") ?? frame.Attr(FoNs, "min-height"));
        OdfStyle? style = Find("graphic", frame.Attr(DrawNs, "style-name"));
        FloatingPlacement? floating = Placement(frame, style);
        string? title = frame.Element(SvgNs + "title")?.Value;
        string? description = frame.Element(SvgNs + "desc")?.Value;

        if ((frame.Element(DrawNs + "object") ?? frame.Element(DrawNs + "object-ole")) is { } embedded)
        {
            List<XElement> images = [.. frame.Elements(DrawNs + "image")];
            ImageData? replacement = images.Select(Image).FirstOrDefault(i => i is not null);
            IslandKind kind = embedded.Name.LocalName == "object-ole" ? IslandKind.OleObject : ObjectKind(embedded);
            return new Island(document, kind, OdtExtension.Format, frame.ToString(SaveOptions.DisableFormatting))
            {
                Fallback = replacement,
                Width = width,
                Height = height,
                Floating = floating,
            };
        }

        if (frame.Element(DrawNs + "text-box") is { } box)
        {
            Shape shape = new(document, ShapeKind.Rectangle, width, height)
            {
                Floating = floating,
                Title = title,
                Description = description,
            };
            ApplyGraphic(style, shape, frame: true);
            shape.FitToText = box.Attr(FoNs, "min-height") is not null || frame.Attr(FoNs, "min-height") is not null;
            ReadStory(box, shape.AddTextBox().Blocks);
            return shape;
        }

        List<ImageData> pictures = [.. frame.Elements(DrawNs + "image").Select(Image).Where(i => i is not null).Select(i => i!)];
        if (pictures.Count > 0)
        {
            Picture picture = new(document, pictures[0], width, height)
            {
                Floating = floating,
                Title = title,
                Description = description,
            };
            if (pictures.Count > 1)
            {
                // LibreOffice stores an SVG picture with a PNG beside it for readers that do not draw SVG.
                picture.Fallback = pictures[1];
            }

            return picture;
        }

        if (frame.Elements(DrawNs + "image").Any())
        {
            Warn(ConversionWarningKind.PictureDropped, "A picture the package does not hold (a link or a missing file) is left out.");
        }

        return null;
    }

    /// <summary>
    /// Reads a frame that holds only a table, as LibreOffice writes Word's floating tables, as a floating table before the
    /// paragraph it is anchored to; false for any other frame.
    /// </summary>
    private bool ReadFloatingTable(XElement frame, InlineContext context)
    {
        if (frame.Name != DrawNs + "frame" || frame.Attr(TextNs, "anchor-type") is "as-char" or "page" || frame.Element(DrawNs + "text-box") is not { } box)
        {
            return false;
        }

        List<XElement> content = [.. box.Elements().Where(e => !(e.Name == TextNs + "p" && !e.HasElements && string.IsNullOrWhiteSpace(e.Value)))];
        if (content.Count != 1 || content[0].Name != TableNs + "table" || Placement(frame, Find("graphic", frame.Attr(DrawNs, "style-name"))) is not { } placement)
        {
            return false;
        }

        NodeCollection<Block> blocks = context.Blocks.Blocks;
        int count = blocks.Count;
        (bool pageBreak, List<XElement>? before) = (pending.PageBreak, pending.Before);
        (pending.PageBreak, pending.Before) = (false, null);
        try
        {
            ReadTable(content[0], context.Blocks);
        }
        finally
        {
            (pending.PageBreak, pending.Before) = (pageBreak, before);
        }

        if (context.Blocks.Blocks != blocks || blocks.Count != count + 1 || blocks[^1] is not Table table)
        {
            return true;
        }

        table.Format.Position = new TablePosition
        {
            HorizontalAnchor = placement.HorizontalAnchor switch
            {
                HorizontalAnchor.Page => HorizontalAnchor.Page,
                HorizontalAnchor.Margin or HorizontalAnchor.LeftMargin or HorizontalAnchor.RightMargin => HorizontalAnchor.Margin,
                _ => HorizontalAnchor.Column,
            },
            HorizontalAlignment = placement.HorizontalAlignment,
            HorizontalOffset = placement.HorizontalOffset,
            VerticalAnchor = placement.VerticalAnchor is VerticalAnchor.Page or VerticalAnchor.Margin ? placement.VerticalAnchor : VerticalAnchor.Paragraph,
            VerticalAlignment = placement.VerticalAlignment,
            VerticalOffset = placement.VerticalOffset,
            DistanceLeft = placement.DistanceLeft,
            DistanceRight = placement.DistanceRight,
            DistanceTop = placement.DistanceTop,
            DistanceBottom = placement.DistanceBottom,
        };
        int anchor = blocks.IndexOf(context.Paragraph);
        if (anchor >= 0)
        {
            blocks.RemoveAt(count);
            blocks.Insert(anchor, table);
        }

        return true;
    }

    private IslandKind ObjectKind(XElement embedded)
    {
        string path = OdfPackage.NormalizePath(embedded.Attr(XLinkNs, "href") ?? "");
        string type = package.Manifest.GetValueOrDefault(path) ?? package.Manifest.GetValueOrDefault(path + "/") ?? "";
        if (embedded.Element(MathMLNs + "math") is not null || type.EndsWith(".formula", StringComparison.Ordinal))
        {
            return IslandKind.Math;
        }

        return type.EndsWith(".chart", StringComparison.Ordinal) || embedded.Element(OfficeNs + "document")?.Attr(OfficeNs, "mimetype")?.EndsWith(".chart", StringComparison.Ordinal) == true
            ? IslandKind.Chart
            : IslandKind.OleObject;
    }

    /// <summary>The picture a <c>draw:image</c> holds, from the package or inline as base64, or null.</summary>
    private ImageData? Image(XElement image)
    {
        byte[]? bytes = null;
        if (image.Element(OfficeNs + "binary-data") is { } data)
        {
            try
            {
                bytes = Convert.FromBase64String(data.Value);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        else if (image.Attr(XLinkNs, "href") is { } href && !href.Contains("://", StringComparison.Ordinal))
        {
            bytes = package.GetFile(href);
        }

        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        string type = ImageData.GuessContentType(bytes);
        if (type == "application/octet-stream" && image.Attr(DrawNs, "mime-type") is { Length: > 0 } named)
        {
            type = named;
        }

        return new ImageData(bytes, type);
    }

    private Inline? ReadShape(XElement element)
    {
        double width;
        double height;
        if (element.Name.LocalName == "line")
        {
            double x1 = OdfUnits.Length(element.Attr(SvgNs, "x1")) ?? 0, x2 = OdfUnits.Length(element.Attr(SvgNs, "x2")) ?? 0;
            double y1 = OdfUnits.Length(element.Attr(SvgNs, "y1")) ?? 0, y2 = OdfUnits.Length(element.Attr(SvgNs, "y2")) ?? 0;
            width = Math.Max(1, Math.Abs(x2 - x1));
            height = Math.Max(1, Math.Abs(y2 - y1));
        }
        else
        {
            width = Size(element.Attr(SvgNs, "width") ?? (element.Attr(SvgNs, "r") is { } r ? (2 * (OdfUnits.Length(r) ?? 0)).ToString(CultureInfo.InvariantCulture) + "pt" : null));
            height = Size(element.Attr(SvgNs, "height") ?? (element.Attr(SvgNs, "r") is { } r2 ? (2 * (OdfUnits.Length(r2) ?? 0)).ToString(CultureInfo.InvariantCulture) + "pt" : null));
        }

        ShapeKind? kind = element.Name.LocalName switch
        {
            "rect" => OdfUnits.Length(element.Attr(DrawNs, "corner-radius")) is > 0 ? ShapeKind.RoundedRectangle : ShapeKind.Rectangle,
            "ellipse" or "circle" => ShapeKind.Ellipse,
            "line" => ShapeKind.Line,
            _ => element.Element(DrawNs + "enhanced-geometry")?.Attr(DrawNs, "type") switch
            {
                "rectangle" or "mso-spt1" or "frame" => ShapeKind.Rectangle,
                "round-rectangle" or "mso-spt2" => ShapeKind.RoundedRectangle,
                "ellipse" or "mso-spt3" => ShapeKind.Ellipse,
                "isosceles-triangle" or "mso-spt5" => ShapeKind.Triangle,
                "right-arrow" or "mso-spt13" => ShapeKind.RightArrow,
                _ => null,
            },
        };

        if (kind is null)
        {
            Warn(ConversionWarningKind.DrawingDropped, "Shapes other than rectangles, ellipses, lines and text boxes are kept, not drawn.");
            return KeptShape(element, IslandKind.Other);
        }

        OdfStyle? style = Find("graphic", element.Attr(DrawNs, "style-name"));
        Shape shape = new(document, kind.Value, width, height)
        {
            Floating = Placement(element, style),
            Title = element.Element(SvgNs + "title")?.Value,
            Description = element.Element(SvgNs + "desc")?.Value,
        };
        if (kind == ShapeKind.RoundedRectangle && OdfUnits.Length(element.Attr(DrawNs, "corner-radius")) is > 0 and var radius)
        {
            shape.CornerRadius = radius;
        }

        if (Rotation(element.Attr(DrawNs, "transform")) is { } degrees)
        {
            shape.Rotation = degrees;
        }

        ApplyGraphic(style, shape, frame: false);
        if (element.Elements(TextNs + "p").Any() || element.Elements(TextNs + "list").Any())
        {
            ReadStory(element, shape.AddTextBox().Blocks);
        }

        return shape;
    }

    private Island KeptShape(XElement element, IslandKind kind)
    {
        return new Island(document, kind, OdtExtension.Format, element.ToString(SaveOptions.DisableFormatting))
        {
            Width = Size(element.Attr(SvgNs, "width")),
            Height = Size(element.Attr(SvgNs, "height")),
            Floating = Placement(element, Find("graphic", element.Attr(DrawNs, "style-name"))),
        };
    }

    private static double Size(string? value)
    {
        return OdfUnits.Length(value) is > 0 and var size ? Math.Min(size, 22_000) : 1;
    }

    // draw:transform "rotate (r) translate (x y)": r in radians counter-clockwise; the model turns clockwise in degrees.
    private static double? Rotation(string? transform)
    {
        int at = transform?.IndexOf("rotate", StringComparison.Ordinal) ?? -1;
        if (transform is null || at < 0)
        {
            return null;
        }

        int open = transform.IndexOf('(', at);
        int close = open < 0 ? -1 : transform.IndexOf(')', open);
        if (close < 0 || !double.TryParse(transform.AsSpan(open + 1, close - open - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double radians)
            || !double.IsFinite(radians))
        {
            return null;
        }

        double degrees = -radians * 180 / Math.PI % 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    /// <summary>Fill and outline from a graphic style (ODF 1.3 Part 3, 20.1), following its parents and the default.</summary>
    private void ApplyGraphic(OdfStyle? style, Shape shape, bool frame)
    {
        string? fill = Inherited(style, "graphic", "graphic-properties", DrawNs, "fill");
        string? fillColor = Inherited(style, "graphic", "graphic-properties", DrawNs, "fill-color");
        string? background = Inherited(style, "graphic", "graphic-properties", FoNs, "background-color");
        if (frame)
        {
            // A text frame shows its background colour, and a border as paragraphs give one.
            shape.Fill = fill == "solid" && OdfUnits.Color(fillColor) is { } solid ? DocumentColor.FromRgb(solid)
                : OdfUnits.Color(background) is { } back ? DocumentColor.FromRgb(back) : null;
            shape.Outline = OdfUnits.Border(Inherited(style, "graphic", "graphic-properties", FoNs, "border")) is { IsVisible: true } border ? border : null;
            return;
        }

        // A drawing shape is filled and outlined unless its style says not, in LibreOffice's default blue.
        shape.Fill = fill == "none" ? null : OdfUnits.Color(fillColor) is { } color ? DocumentColor.FromRgb(color) : DocumentColor.FromHex("729FCF");
        string? stroke = Inherited(style, "graphic", "graphic-properties", DrawNs, "stroke");
        if (stroke == "none")
        {
            shape.Outline = null;
            return;
        }

        double strokeWidth = OdfUnits.Length(Inherited(style, "graphic", "graphic-properties", SvgNs, "stroke-width")) is > 0 and var w ? w : 0.75;
        DocumentColor strokeColor = OdfUnits.Color(Inherited(style, "graphic", "graphic-properties", SvgNs, "stroke-color")) is { } c
            ? DocumentColor.FromRgb(c)
            : DocumentColor.FromHex("3465A4");
        shape.Outline = new Border(stroke == "dash" ? BorderStyle.Dashed : BorderStyle.Single, strokeWidth, strokeColor);
    }

    /// <summary>Where a frame or shape is placed (ODF 1.3 Part 3, 20.290 and on), or null for one anchored as a character.</summary>
    private FloatingPlacement? Placement(XElement element, OdfStyle? style)
    {
        string anchor = element.Attr(TextNs, "anchor-type") ?? "paragraph";
        if (anchor == "as-char")
        {
            return null;
        }

        string? Graphic(XNamespace ns, string name) => Inherited(style, "graphic", "graphic-properties", ns, name);
        string horizontalRel = Graphic(StyleNs, "horizontal-rel") ?? (anchor == "page" ? "page" : "paragraph");
        string horizontalPos = Graphic(StyleNs, "horizontal-pos") ?? "from-left";
        string verticalRel = Graphic(StyleNs, "vertical-rel") ?? (anchor == "page" ? "page" : "paragraph");
        string verticalPos = Graphic(StyleNs, "vertical-pos") ?? "from-top";
        double x = OdfUnits.Length(element.Attr(SvgNs, "x")) ?? 0;
        double y = OdfUnits.Length(element.Attr(SvgNs, "y")) ?? 0;
        if (element.Name == DrawNs + "line")
        {
            // A line has its two ends, not a position: it sits at their top left corner.
            x = Math.Min(OdfUnits.Length(element.Attr(SvgNs, "x1")) ?? 0, OdfUnits.Length(element.Attr(SvgNs, "x2")) ?? 0);
            y = Math.Min(OdfUnits.Length(element.Attr(SvgNs, "y1")) ?? 0, OdfUnits.Length(element.Attr(SvgNs, "y2")) ?? 0);
        }

        if (element.Attr(DrawNs, "transform") is { } transform && Translation(transform) is { } moved)
        {
            (x, y) = moved;
        }

        string wrap = Graphic(StyleNs, "wrap") ?? "none";
        bool contour = Graphic(StyleNs, "wrap-contour") == "true";
        (WrapMode mode, WrapSide side) = wrap switch
        {
            "left" => (WrapMode.Square, WrapSide.Left),
            "right" => (WrapMode.Square, WrapSide.Right),
            "parallel" => (contour ? WrapMode.Tight : WrapMode.Square, WrapSide.Both),
            "dynamic" or "biggest" => (contour ? WrapMode.Tight : WrapMode.Square, WrapSide.Largest),
            "run-through" => (Graphic(StyleNs, "run-through") == "background" ? WrapMode.BehindText : WrapMode.InFrontOfText, WrapSide.Both),
            _ => (WrapMode.TopAndBottom, WrapSide.Both),
        };

        return new FloatingPlacement
        {
            HorizontalAnchor = horizontalRel switch
            {
                "page" => HorizontalAnchor.Page,
                "page-content" => HorizontalAnchor.Margin,
                "page-start-margin" or "paragraph-start-margin" => HorizontalAnchor.LeftMargin,
                "page-end-margin" or "paragraph-end-margin" => HorizontalAnchor.RightMargin,
                "char" => HorizontalAnchor.Character,
                _ => HorizontalAnchor.Column,
            },
            HorizontalAlignment = horizontalPos switch
            {
                "left" => FloatingAlignment.Start,
                "center" => FloatingAlignment.Center,
                "right" => FloatingAlignment.End,
                "inside" => FloatingAlignment.Inside,
                "outside" => FloatingAlignment.Outside,
                _ => FloatingAlignment.None,
            },
            HorizontalOffset = x,
            VerticalAnchor = verticalRel switch
            {
                "page" => VerticalAnchor.Page,
                "page-content" => VerticalAnchor.Margin,
                "char" or "line" or "baseline" or "text" => VerticalAnchor.Line,
                _ => VerticalAnchor.Paragraph,
            },
            VerticalAlignment = verticalPos switch
            {
                "top" => FloatingAlignment.Start,
                "middle" => FloatingAlignment.Center,
                "bottom" => FloatingAlignment.End,
                _ => FloatingAlignment.None,
            },
            VerticalOffset = y,
            Wrap = mode,
            WrapSide = side,
            DistanceTop = Math.Max(0, OdfUnits.Length(Graphic(FoNs, "margin-top")) ?? 0),
            DistanceBottom = Math.Max(0, OdfUnits.Length(Graphic(FoNs, "margin-bottom")) ?? 0),
            DistanceLeft = Math.Max(0, OdfUnits.Length(Graphic(FoNs, "margin-left")) ?? 0),
            DistanceRight = Math.Max(0, OdfUnits.Length(Graphic(FoNs, "margin-right")) ?? 0),
            ZOrder = OdfUnits.Int(element.Attr(DrawNs, "z-index")) ?? 0,
        };
    }

    private static (double X, double Y)? Translation(string transform)
    {
        int at = transform.IndexOf("translate", StringComparison.Ordinal);
        int open = at < 0 ? -1 : transform.IndexOf('(', at);
        int close = open < 0 ? -1 : transform.IndexOf(')', open);
        if (close < 0)
        {
            return null;
        }

        string[] parts = transform[(open + 1)..close].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && OdfUnits.Length(parts[0]) is { } x && OdfUnits.Length(parts[1]) is { } y ? (x, y) : null;
    }
}
