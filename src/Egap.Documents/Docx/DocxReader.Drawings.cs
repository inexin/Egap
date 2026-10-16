using System.Globalization;
using System.Xml.Linq;

using Egami.Pixels;

namespace Egap.Documents.Docx;

/// <summary>Drawings: DrawingML pictures and shapes, VML pictures and text boxes, OLE objects, and islands for the rest.</summary>
internal sealed partial class DocxReader
{
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const string GroupUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string DiagramUri = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string InkUri = "http://schemas.microsoft.com/office/drawing/2010/ink";

    private void ReadDrawing(XElement drawing, NodeCollection<Inline> inlines, InlineState state)
    {
        XElement? frame = drawing.Element(Ns.Wp + "inline") ?? drawing.Element(Ns.Wp + "anchor");
        if (frame is null)
        {
            AddIsland(IslandKind.Other, drawing, 0, 0, null);
            return;
        }

        XElement? extent = frame.Element(Ns.Wp + "extent");
        double width = Size(extent, "cx");
        double height = Size(extent, "cy");
        FloatingPlacement? floating = frame.Name.LocalName == "anchor" ? ReadAnchor(frame) : null;
        XElement? docPr = frame.Element(Ns.Wp + "docPr");
        XElement? graphicData = frame.Element(Ns.A + "graphic")?.Element(Ns.A + "graphicData");
        string uri = (string?)graphicData?.Attribute("uri") ?? "";

        switch (uri)
        {
            case PictureUri when graphicData!.Element(Ns.Pic + "pic") is { } pic:
                {
                    XElement? blipFill = pic.Element(Ns.Pic + "blipFill");
                    XElement? blip = blipFill?.Element(Ns.A + "blip");
                    Picture picture = Add(inlines, NewPicture(blip, width, height), state, drawing);
                    picture.Title = (string?)docPr?.Attribute("title");
                    picture.Description = (string?)docPr?.Attribute("descr");
                    picture.Floating = floating;
                    if (blipFill?.Element(Ns.A + "srcRect") is { } crop)
                    {
                        picture.Crop = (Fraction(crop, "l"), Fraction(crop, "t"), Fraction(crop, "r"), Fraction(crop, "b"));
                    }

                    DocxExtension.Of(picture).Snapshot = PictureState.Of(picture);

                    break;
                }

            case ShapeUri when graphicData!.Element(Ns.Wps + "wsp") is { } wsp:
                {
                    XElement? spPr = wsp.Element(Ns.Wps + "spPr");
                    Shape shape = Add(inlines, new Shape(document, ShapeKindOf(spPr), width, height), state, drawing);
                    shape.Title = (string?)docPr?.Attribute("title");
                    shape.Description = (string?)docPr?.Attribute("descr");
                    shape.Floating = floating;
                    ReadShapeProperties(spPr, shape);
                    if (wsp.Element(Ns.Wps + "txbx")?.Element(Ns.W + "txbxContent") is { } content)
                    {
                        ReadTextBox(content, shape);
                    }

                    break;
                }

            default:
                {
                    IslandKind kind = uri switch
                    {
                        GroupUri => IslandKind.ShapeGroup,
                        ChartUri => IslandKind.Chart,
                        DiagramUri => IslandKind.SmartArt,
                        InkUri => IslandKind.Ink,
                        _ => IslandKind.Other,
                    };
                    AddIsland(kind, drawing, width, height, floating);
                    break;
                }
        }

        void AddIsland(IslandKind kind, XElement element, double w, double h, FloatingPlacement? placement)
        {
            Add(inlines, new Island(document, kind, DocxExtension.Format, element.ToString(SaveOptions.DisableFormatting))
            {
                Width = w,
                Height = h,
                Floating = placement,
                Text = IslandText(element),
            }, state, element);
        }
    }

    /// <summary>The text an island holds: its Word text (text boxes in groups), else its DrawingML text (charts, diagrams).</summary>
    private static string IslandText(XElement element)
    {
        string text = string.Concat(element.Descendants(Ns.W + "t").Select(t => t.Value));
        return text.Length > 0 ? text : string.Concat(element.Descendants(Ns.A + "t").Select(t => t.Value));
    }

    private Picture NewPicture(XElement? blip, double width, double height)
    {
        ImageData image = new(ReadOnlyMemory<byte>.Empty, "application/octet-stream");
        string? link = null;
        if ((string?)blip?.Attribute(Ns.R + "embed") is { } embed && ImageOf(embed) is { } data)
        {
            image = data;
        }
        else if ((string?)blip?.Attribute(Ns.R + "link") is { } linkId && currentPart.GetRelationship(linkId) is { } relationship)
        {
            link = relationship.Target;
        }

        return new Picture(document, image, width, height) { SourceUrl = link };
    }

    private ImageData? ImageOf(string relationshipId)
    {
        if (currentPart.GetRelationship(relationshipId) is not { External: false } relationship || package.GetPart(relationship.TargetName) is not { } part)
        {
            return null;
        }

        string type = part.ContentType;
        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            type = ImageData.GuessContentType(part.Data);
        }

        return new ImageData(part.Data, type);
    }

    private void ReadTextBox(XElement content, Shape shape)
    {
        TextBoxStory story = shape.AddTextBox();
        List<Inline> outerMarks = [.. pendingMarks];
        pendingMarks.Clear();
        Paragraph? outerLast = lastParagraph;
        lastParagraph = null;
        XElement? outerRunProperties = runProperties;
        runProperties = null;
        noteDepth++;
        try
        {
            ReadBlocks(content, story.Blocks);
            FlushMarksToLast();
        }
        finally
        {
            noteDepth--;
            runProperties = outerRunProperties;
            pendingMarks.Clear();
            pendingMarks.AddRange(outerMarks);
            lastParagraph = outerLast;
        }
    }

    private static ShapeKind ShapeKindOf(XElement? spPr)
    {
        return (string?)spPr?.Element(Ns.A + "prstGeom")?.Attribute("prst") switch
        {
            "rect" => ShapeKind.Rectangle,
            "roundRect" => ShapeKind.RoundedRectangle,
            "ellipse" => ShapeKind.Ellipse,
            "line" or "straightConnector1" => ShapeKind.Line,
            "triangle" => ShapeKind.Triangle,
            "rightArrow" => ShapeKind.RightArrow,
            null when spPr?.Element(Ns.A + "custGeom") is null => ShapeKind.Rectangle,
            _ => ShapeKind.Other,
        };
    }

    private static void ReadShapeProperties(XElement? spPr, Shape shape)
    {
        if (spPr is null)
        {
            return;
        }

        if (spPr.Element(Ns.A + "xfrm") is { } xfrm && DocxUnits.Int((string?)xfrm.Attribute("rot")) is int rotation)
        {
            shape.Rotation = rotation / 60000.0;
        }

        if (spPr.Element(Ns.A + "solidFill") is { } fill && DrawingColor(fill) is { } fillColor)
        {
            shape.Fill = fillColor;
        }

        if (spPr.Element(Ns.A + "ln") is { } line)
        {
            if (line.Element(Ns.A + "noFill") is not null)
            {
                shape.Outline = null;
            }
            else
            {
                double lineWidth = Math.Clamp((DocxUnits.Int((string?)line.Attribute("w")) ?? 9525) * DocxUnits.Emu, 0.25, 12);
                DocumentColor color = line.Element(Ns.A + "solidFill") is { } lineFill && DrawingColor(lineFill) is { } c ? c : DocumentColor.Auto;
                shape.Outline = new Border(BorderStyle.Single, lineWidth, color, 0, false, false);
            }
        }
    }

    private static DocumentColor? DrawingColor(XElement fill)
    {
        XElement? color = fill.Elements().FirstOrDefault();
        return color?.Name.LocalName switch
        {
            "srgbClr" when DocxProperties.ParseHex((string?)color.Attribute("val")) is Rgba8 rgb => DocumentColor.FromRgb(rgb),
            "schemeClr" when DocxProperties.ThemeColorOf(SchemeName((string?)color.Attribute("val"))) is { } theme => DocumentColor.FromTheme(theme, 0, 0, default),
            _ => null,
        };

        static string SchemeName(string? value)
        {
            return value switch
            {
                "tx1" => "text1",
                "tx2" => "text2",
                "bg1" => "background1",
                "bg2" => "background2",
                "hlink" => "hyperlink",
                "folHlink" => "followedHyperlink",
                _ => value ?? "",
            };
        }
    }

    private static FloatingPlacement ReadAnchor(XElement anchor)
    {
        XElement? positionH = anchor.Element(Ns.Wp + "positionH");
        XElement? positionV = anchor.Element(Ns.Wp + "positionV");
        (WrapMode wrap, WrapSide side, IReadOnlyList<(double X, double Y)>? polygon) = ReadWrap(anchor);
        return new FloatingPlacement
        {
            HorizontalAnchor = (string?)positionH?.Attribute("relativeFrom") switch
            {
                "page" => HorizontalAnchor.Page,
                "margin" => HorizontalAnchor.Margin,
                "character" => HorizontalAnchor.Character,
                "leftMargin" => HorizontalAnchor.LeftMargin,
                "rightMargin" => HorizontalAnchor.RightMargin,
                "insideMargin" => HorizontalAnchor.InsideMargin,
                "outsideMargin" => HorizontalAnchor.OutsideMargin,
                _ => HorizontalAnchor.Column,
            },
            HorizontalAlignment = Alignment(positionH?.Element(Ns.Wp + "align")?.Value),
            HorizontalOffset = Offset(positionH),
            VerticalAnchor = (string?)positionV?.Attribute("relativeFrom") switch
            {
                "page" => VerticalAnchor.Page,
                "margin" => VerticalAnchor.Margin,
                "line" => VerticalAnchor.Line,
                "topMargin" => VerticalAnchor.TopMargin,
                "bottomMargin" => VerticalAnchor.BottomMargin,
                "insideMargin" => VerticalAnchor.InsideMargin,
                "outsideMargin" => VerticalAnchor.OutsideMargin,
                _ => VerticalAnchor.Paragraph,
            },
            VerticalAlignment = Alignment(positionV?.Element(Ns.Wp + "align")?.Value),
            VerticalOffset = Offset(positionV),
            Wrap = wrap,
            WrapSide = side,
            WrapPolygon = polygon,
            DistanceTop = Distance(anchor, "distT"),
            DistanceBottom = Distance(anchor, "distB"),
            DistanceLeft = Distance(anchor, "distL"),
            DistanceRight = Distance(anchor, "distR"),
            ZOrder = long.TryParse((string?)anchor.Attribute("relativeHeight"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long z) ? z : 0,
            AllowOverlap = (string?)anchor.Attribute("allowOverlap") is not ("0" or "false"),
            LayoutInCell = (string?)anchor.Attribute("layoutInCell") is not ("0" or "false"),
            Locked = (string?)anchor.Attribute("locked") is "1" or "true",
        };

        static FloatingAlignment Alignment(string? value)
        {
            return value?.Trim() switch
            {
                "left" or "top" => FloatingAlignment.Start,
                "center" => FloatingAlignment.Center,
                "right" or "bottom" => FloatingAlignment.End,
                "inside" => FloatingAlignment.Inside,
                "outside" => FloatingAlignment.Outside,
                _ => FloatingAlignment.None,
            };
        }

        static double Offset(XElement? position)
        {
            return position?.Element(Ns.Wp + "posOffset") is { } offset && long.TryParse(offset.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long emu)
                ? Math.Clamp(emu * DocxUnits.Emu, -1e6, 1e6) : 0;
        }

        static double Distance(XElement element, string name)
        {
            return long.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long emu) ? Math.Clamp(emu * DocxUnits.Emu, 0, 1e5) : 0;
        }
    }

    private static (WrapMode Wrap, WrapSide Side, IReadOnlyList<(double X, double Y)>? Polygon) ReadWrap(XElement anchor)
    {
        bool behind = (string?)anchor.Attribute("behindDoc") is "1" or "true";
        foreach (XElement e in anchor.Elements())
        {
            if (e.Name.Namespace != Ns.Wp)
            {
                continue;
            }

            WrapSide side = (string?)e.Attribute("wrapText") switch
            {
                "left" => WrapSide.Left,
                "right" => WrapSide.Right,
                "largest" => WrapSide.Largest,
                _ => WrapSide.Both,
            };
            switch (e.Name.LocalName)
            {
                case "wrapNone":
                    return (behind ? WrapMode.BehindText : WrapMode.InFrontOfText, WrapSide.Both, null);
                case "wrapSquare":
                    return (WrapMode.Square, side, null);
                case "wrapTopAndBottom":
                    return (WrapMode.TopAndBottom, WrapSide.Both, null);
                case "wrapTight" or "wrapThrough":
                    return (e.Name.LocalName == "wrapTight" ? WrapMode.Tight : WrapMode.Through, side, Polygon(e.Element(Ns.Wp + "wrapPolygon")));
            }
        }

        return (WrapMode.Square, WrapSide.Both, null);

        static IReadOnlyList<(double X, double Y)>? Polygon(XElement? polygon)
        {
            if (polygon is null)
            {
                return null;
            }

            // Points are in 21600ths of the drawing's size.
            List<(double X, double Y)> points = [];
            foreach (XElement point in polygon.Elements())
            {
                if (DocxUnits.Int((string?)point.Attribute("x")) is int x && DocxUnits.Int((string?)point.Attribute("y")) is int y)
                {
                    points.Add((x / 21600.0, y / 21600.0));
                }
            }

            return points.Count >= 3 ? points : null;
        }
    }

    /// <summary>VML in w:pict or w:object: a picture, a text box, or an island with the picture the file stores for it.</summary>
    private void ReadVml(XElement element, NodeCollection<Inline> inlines, InlineState state, IslandKind kind)
    {
        XElement? shape = element.Elements().FirstOrDefault(e => e.Name.Namespace == Ns.V && e.Name.LocalName is "shape" or "rect" or "roundrect" or "oval");
        string? style = (string?)shape?.Attribute("style");
        double width = Math.Max(0, CssLength(style, "width") ?? 0);
        double height = Math.Max(0, CssLength(style, "height") ?? 0);
        XElement? imageData = shape?.Element(Ns.V + "imagedata");
        ImageData? fallback = (string?)imageData?.Attribute(Ns.R + "id") is { } id ? ImageOf(id) : null;

        if (kind == IslandKind.Vml && shape is not null && element.Elements().Count(e => e.Name.Namespace == Ns.V && e.Name.LocalName != "shapetype") == 1)
        {
            if (fallback is not null && shape.Element(Ns.V + "textbox") is null)
            {
                Picture picture = Add(inlines, new Picture(document, fallback, width, height), state, element);
                picture.Title = (string?)shape.Attribute(Ns.O + "title") ?? (string?)imageData?.Attribute(Ns.O + "title");
                picture.Description = (string?)shape.Attribute("alt");
                DocxExtension.Of(picture).Snapshot = PictureState.Of(picture);
                return;
            }

            if (shape.Element(Ns.V + "textbox")?.Element(Ns.W + "txbxContent") is { } content)
            {
                Shape box = Add(inlines, new Shape(document, shape.Name.LocalName switch
                {
                    "oval" => ShapeKind.Ellipse,
                    "roundrect" => ShapeKind.RoundedRectangle,
                    _ => ShapeKind.Rectangle,
                }, width, height), state, element);
                ReadTextBox(content, box);
                return;
            }
        }

        Add(inlines, new Island(document, kind, DocxExtension.Format, element.ToString(SaveOptions.DisableFormatting))
        {
            Width = width,
            Height = height,
            Fallback = fallback,
            Text = string.Concat(element.Descendants(Ns.W + "t").Select(t => t.Value)),
        }, state, element);
    }

    /// <summary>A length in a VML style attribute (width:100pt;height:2in), in points.</summary>
    private static double? CssLength(string? style, string property)
    {
        if (style is null)
        {
            return null;
        }

        foreach (string declaration in style.Split(';'))
        {
            int colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || !declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = declaration[(colon + 1)..].Trim();
            if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels) && double.IsFinite(pixels))
            {
                return Math.Min(pixels * 0.75, 1e6);
            }

            return DocxUnits.Points(value, unit: 1) is double points ? Math.Min(points, 1e6) : null;
        }

        return null;
    }

    private static double Size(XElement? extent, string name)
    {
        return extent is not null && long.TryParse((string?)extent.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long emu)
            ? Math.Clamp(emu * DocxUnits.Emu, 0, 1e6) : 0;
    }

    private static double Fraction(XElement rect, string name)
    {
        return DocxUnits.Int((string?)rect.Attribute(name)) is int value ? Math.Clamp(value / 100000.0, -10, 1) : 0;
    }
}
