using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// Pictures, shapes and text boxes (ODF 1.3 Part 3, 10), and markup kept from the ODT a document was read from, with the
/// automatic styles and package files it refers to.
/// </summary>
internal sealed partial class OdtWriter
{
    private static readonly HashSet<string> Declared = [OfficeNs.NamespaceName, StyleNs.NamespaceName, TextNs.NamespaceName, TableNs.NamespaceName, DrawNs.NamespaceName,
        FoNs.NamespaceName, XLinkNs.NamespaceName, DcNs.NamespaceName, MetaNs.NamespaceName, NumberNs.NamespaceName, SvgNs.NamespaceName, ConfigNs.NamespaceName,
        LoExtNs.NamespaceName];

    private readonly Dictionary<(AutoStyles, string), string> keptStyles = [];
    private int frameCount;

    private void WriteDrawing(Inline inline, ParagraphWriter writer)
    {
        switch (inline)
        {
            case Picture picture:
                writer.Add(PictureFrame(picture.Image, picture.Fallback, picture.Width, picture.Height, picture.Floating, picture.Title, picture.Description, picture.Crop), content: true);
                break;
            case Shape shape:
                writer.Add(ShapeElement(shape), content: true);
                break;
            case Island island:
                if (island.Format == OdtExtension.Format && options.KeepUnknownMarkup && Parse(island.Markup) is { } kept)
                {
                    writer.Add(Keep(kept), content: true);
                }
                else if (island.Fallback is { } fallback)
                {
                    warnings.Add(ConversionWarningKind.DrawingDropped, "Equations, charts and objects from another format were written as their pictures.");
                    writer.Add(PictureFrame(fallback, null, island.Width, island.Height, island.Floating, null, null, default), content: true);
                }
                else
                {
                    warnings.Add(ConversionWarningKind.DrawingDropped, "Objects from another format without a picture were left out.");
                }

                break;
        }
    }

    private static XElement? Parse(string markup)
    {
        try
        {
            return XElement.Parse(markup);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // ---- Pictures ----

    private XElement PictureFrame(ImageData image, ImageData? fallback, double width, double height, FloatingPlacement? floating, string? title, string? description,
        (double Left, double Top, double Right, double Bottom) crop)
    {
        XElement graphic = new(StyleNs + "graphic-properties");
        Place(graphic, floating);
        if (crop != default)
        {
            (double w, double h) = ImageSize.Intrinsic(image.Bytes);
            if (w > 0 && h > 0)
            {
                // fo:clip cuts the picture's own size, top, right, bottom and left (ODF 1.3 Part 3, 20.179).
                graphic.Add(new XAttribute(FoNs + "clip", $"rect({Pt(crop.Top * h)}, {Pt(crop.Right * w)}, {Pt(crop.Bottom * h)}, {Pt(crop.Left * w)})"));
            }
        }

        XElement frame = Frame(Auto("graphic", null, null, graphic), "Image", width, height, floating);
        frame.Add(Image(image));
        if (fallback is not null)
        {
            frame.Add(Image(fallback));
        }

        AddTitle(frame, title, description);
        return frame;
    }

    private XElement Frame(string style, string name, double width, double height, FloatingPlacement? floating)
    {
        XElement frame = new(DrawNs + "frame", new XAttribute(DrawNs + "style-name", style), new XAttribute(DrawNs + "name", name + Int(++frameCount)),
            new XAttribute(TextNs + "anchor-type", Anchor(floating)));
        if (floating is not null)
        {
            frame.Add(new XAttribute(SvgNs + "x", Pt(floating.HorizontalOffset)), new XAttribute(SvgNs + "y", Pt(floating.VerticalOffset)));
        }

        frame.Add(new XAttribute(SvgNs + "width", Pt(Math.Max(width, 0.05))), new XAttribute(SvgNs + "height", Pt(Math.Max(height, 0.05))));
        if (floating is not null)
        {
            frame.Add(new XAttribute(DrawNs + "z-index", Int(Math.Clamp(floating.ZOrder, 0, int.MaxValue))));
        }

        return frame;
    }

    private static void AddTitle(XElement element, string? title, string? description)
    {
        if (title is { Length: > 0 })
        {
            element.Add(new XElement(SvgNs + "title", Clean(title)));
        }

        if (description is { Length: > 0 })
        {
            element.Add(new XElement(SvgNs + "desc", Clean(description)));
        }
    }

    /// <summary>A <c>draw:image</c> for a picture stored in the package under Pictures/, named by its content.</summary>
    private XElement Image(ImageData image)
    {
        byte[] bytes = image.Bytes.ToArray();
        string path = "Pictures/" + Convert.ToHexStringLower(SHA256.HashData(bytes).AsSpan(0, 16)) + Extension(image.ContentType);
        files[path] = (bytes, image.ContentType);
        XElement element = new(DrawNs + "image", new XAttribute(XLinkNs + "href", path), new XAttribute(XLinkNs + "type", "simple"),
            new XAttribute(XLinkNs + "show", "embed"), new XAttribute(XLinkNs + "actuate", "onLoad"));
        element.Add(new XAttribute((Odf13 ? DrawNs : LoExtNs) + "mime-type", image.ContentType));
        return element;
    }

    private static string Extension(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/svg+xml" => ".svg",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        "image/webp" => ".webp",
        "image/x-emf" or "image/emf" => ".emf",
        "image/x-wmf" or "image/wmf" => ".wmf",
        _ => ".bin",
    };

    private static string Anchor(FloatingPlacement? floating)
    {
        return floating is null ? "as-char"
            : floating.HorizontalAnchor == HorizontalAnchor.Character || floating.VerticalAnchor == VerticalAnchor.Line ? "char" : "paragraph";
    }

    /// <summary>Where a frame or shape goes and how text wraps around it, as its graphic style holds it.</summary>
    private static void Place(XElement graphic, FloatingPlacement? floating)
    {
        if (floating is null)
        {
            graphic.Add(new XAttribute(StyleNs + "vertical-pos", "top"), new XAttribute(StyleNs + "vertical-rel", "baseline"));
            return;
        }

        string horizontalRel = floating.HorizontalAnchor switch
        {
            HorizontalAnchor.Page => "page",
            HorizontalAnchor.Margin => "page-content",
            HorizontalAnchor.LeftMargin or HorizontalAnchor.InsideMargin => "page-start-margin",
            HorizontalAnchor.RightMargin or HorizontalAnchor.OutsideMargin => "page-end-margin",
            HorizontalAnchor.Character => "char",
            _ => "paragraph",
        };
        string horizontalPos = floating.HorizontalAlignment switch
        {
            FloatingAlignment.Start => "left",
            FloatingAlignment.Center => "center",
            FloatingAlignment.End => "right",
            FloatingAlignment.Inside => "inside",
            FloatingAlignment.Outside => "outside",
            _ => "from-left",
        };
        string verticalRel = floating.VerticalAnchor switch
        {
            VerticalAnchor.Page or VerticalAnchor.TopMargin or VerticalAnchor.BottomMargin or VerticalAnchor.InsideMargin or VerticalAnchor.OutsideMargin => "page",
            VerticalAnchor.Margin => "page-content",
            VerticalAnchor.Line => "line",
            _ => "paragraph",
        };
        string verticalPos = floating.VerticalAlignment switch
        {
            FloatingAlignment.Start => "top",
            FloatingAlignment.Center => "middle",
            FloatingAlignment.End => "bottom",
            _ => "from-top",
        };
        bool contour = floating.Wrap is WrapMode.Tight or WrapMode.Through;
        string wrap = floating.Wrap switch
        {
            WrapMode.TopAndBottom => "none",
            WrapMode.BehindText or WrapMode.InFrontOfText => "run-through",
            _ => floating.WrapSide switch
            {
                WrapSide.Left => "left",
                WrapSide.Right => "right",
                WrapSide.Largest => "dynamic",
                _ => "parallel",
            },
        };
        graphic.Add(new XAttribute(StyleNs + "wrap", wrap));
        if (wrap == "run-through")
        {
            graphic.Add(new XAttribute(StyleNs + "run-through", floating.Wrap == WrapMode.BehindText ? "background" : "foreground"));
        }
        else if (contour)
        {
            graphic.Add(new XAttribute(StyleNs + "wrap-contour", "true"), new XAttribute(StyleNs + "wrap-contour-mode", "outside"));
        }

        graphic.Add(new XAttribute(StyleNs + "horizontal-pos", horizontalPos), new XAttribute(StyleNs + "horizontal-rel", horizontalRel),
            new XAttribute(StyleNs + "vertical-pos", verticalPos), new XAttribute(StyleNs + "vertical-rel", verticalRel),
            new XAttribute(FoNs + "margin-top", Pt(floating.DistanceTop)), new XAttribute(FoNs + "margin-bottom", Pt(floating.DistanceBottom)),
            new XAttribute(FoNs + "margin-left", Pt(floating.DistanceLeft)), new XAttribute(FoNs + "margin-right", Pt(floating.DistanceRight)));
    }

    // ---- Shapes ----

    private XElement ShapeElement(Shape shape)
    {
        if (shape.TextBox is { } box && shape.Kind is ShapeKind.Rectangle or ShapeKind.RoundedRectangle or ShapeKind.Other && shape.Rotation == 0)
        {
            return TextFrame(shape, box);
        }

        XElement graphic = new(StyleNs + "graphic-properties");
        Place(graphic, shape.Floating);
        if (shape.Fill is { } fill && Color(fill) is { } fillColor)
        {
            graphic.Add(new XAttribute(DrawNs + "fill", "solid"), new XAttribute(DrawNs + "fill-color", fillColor));
        }
        else
        {
            graphic.Add(new XAttribute(DrawNs + "fill", "none"));
        }

        if (shape.Outline is { IsVisible: true } outline)
        {
            graphic.Add(new XAttribute(DrawNs + "stroke", outline.Style is BorderStyle.Dashed or BorderStyle.DashSmallGap or BorderStyle.DotDash or BorderStyle.DotDotDash ? "dash" : "solid"),
                new XAttribute(SvgNs + "stroke-width", Pt(outline.Width)), new XAttribute(SvgNs + "stroke-color", Color(outline.Color) ?? "#000000"));
        }
        else
        {
            graphic.Add(new XAttribute(DrawNs + "stroke", "none"));
        }

        if (shape.Kind == ShapeKind.Other)
        {
            warnings.Add(ConversionWarningKind.LayoutApproximated, "Shapes OpenDocument has no preset for were written as rectangles.");
        }

        string style = Auto("graphic", null, null, graphic);
        FloatingPlacement? floating = shape.Floating;
        double x = floating?.HorizontalOffset ?? 0;
        double y = floating?.VerticalOffset ?? 0;
        double w = Math.Max(shape.Width, 0.05);
        double h = Math.Max(shape.Height, 0.05);
        string name = shape.Kind switch
        {
            ShapeKind.Ellipse => "ellipse",
            ShapeKind.Line => "line",
            ShapeKind.Triangle or ShapeKind.RightArrow => "custom-shape",
            _ => "rect",
        };
        XElement element = new(DrawNs + name, new XAttribute(DrawNs + "style-name", style), new XAttribute(DrawNs + "name", "Shape" + Int(++frameCount)),
            new XAttribute(TextNs + "anchor-type", Anchor(floating)));
        if (floating is not null)
        {
            element.Add(new XAttribute(DrawNs + "z-index", Int(Math.Clamp(floating.ZOrder, 0, int.MaxValue))));
        }

        if (name == "line")
        {
            element.Add(new XAttribute(SvgNs + "x1", Pt(x)), new XAttribute(SvgNs + "y1", Pt(y)), new XAttribute(SvgNs + "x2", Pt(x + shape.Width)),
                new XAttribute(SvgNs + "y2", Pt(y + shape.Height)));
        }
        else
        {
            element.Add(new XAttribute(SvgNs + "width", Pt(w)), new XAttribute(SvgNs + "height", Pt(h)));
            if (shape.Rotation != 0)
            {
                // ODF turns about the shape's corner, counter-clockwise in radians; the model turns clockwise about its centre.
                double r = -shape.Rotation * Math.PI / 180;
                double cx = w / 2, cy = h / 2;
                double rx = (cx * Math.Cos(r)) + (cy * Math.Sin(r));
                double ry = (-cx * Math.Sin(r)) + (cy * Math.Cos(r));
                element.Add(new XAttribute(DrawNs + "transform", string.Create(CultureInfo.InvariantCulture,
                    $"rotate ({r:0.######}) translate ({Pt(x + cx - rx)} {Pt(y + cy - ry)})")));
            }
            else
            {
                element.Add(new XAttribute(SvgNs + "x", Pt(x)), new XAttribute(SvgNs + "y", Pt(y)));
            }

            if (shape.Kind == ShapeKind.RoundedRectangle)
            {
                element.Add(new XAttribute(DrawNs + "corner-radius", Pt(shape.CornerRadius > 0 ? shape.CornerRadius : Math.Min(w, h) / 6)));
            }
        }

        AddTitle(element, shape.Title, shape.Description);
        if (shape.TextBox is { } text)
        {
            WriteStory(text.Blocks, element);
        }

        if (name == "custom-shape")
        {
            // The preset's outline in a 21600 square, as LibreOffice's presets give it.
            (string type, string path) = shape.Kind == ShapeKind.Triangle
                ? ("isosceles-triangle", "M 10800 0 L 21600 21600 0 21600 Z N")
                : ("right-arrow", "M 0 5400 L 16200 5400 16200 0 21600 10800 16200 21600 16200 16200 0 16200 Z N");
            element.Add(new XElement(DrawNs + "enhanced-geometry", new XAttribute(SvgNs + "viewBox", "0 0 21600 21600"),
                new XAttribute(DrawNs + "type", type), new XAttribute(DrawNs + "enhanced-path", path)));
        }

        return element;
    }

    /// <summary>A text box as a text frame (ODF 1.3 Part 3, 10.4.3): its background, border and insets in the frame's style.</summary>
    private XElement TextFrame(Shape shape, TextBoxStory box)
    {
        XElement graphic = new(StyleNs + "graphic-properties");
        Place(graphic, shape.Floating);
        if (shape.Fill is { } fill && Color(fill) is { } color)
        {
            graphic.Add(new XAttribute(FoNs + "background-color", color), new XAttribute(DrawNs + "fill", "solid"), new XAttribute(DrawNs + "fill-color", color));
        }
        else
        {
            graphic.Add(new XAttribute(FoNs + "background-color", "transparent"), new XAttribute(DrawNs + "fill", "none"));
        }

        graphic.Add(new XAttribute(FoNs + "border", shape.Outline is { IsVisible: true } outline ? BorderValue(outline) : "none"));
        (double left, double top, double right, double bottom) = shape.TextInsets ?? (7.2, 3.6, 7.2, 3.6);
        graphic.Add(new XAttribute(FoNs + "padding-top", Pt(top)), new XAttribute(FoNs + "padding-bottom", Pt(bottom)),
            new XAttribute(FoNs + "padding-left", Pt(left)), new XAttribute(FoNs + "padding-right", Pt(right)));
        XElement frame = Frame(Auto("graphic", null, null, graphic), "Frame", shape.Width, shape.Height, shape.Floating);
        XElement text = new(DrawNs + "text-box");
        if (shape.FitToText)
        {
            text.Add(new XAttribute(FoNs + "min-height", Pt(Math.Max(shape.Height, 0.05))));
        }

        WriteStory(box.Blocks, text);
        frame.Add(text);
        AddTitle(frame, shape.Title, shape.Description);
        return frame;
    }

    // ---- Kept markup ----

    /// <summary>
    /// A copy of markup kept from the source ODT, its automatic styles copied into the file it goes in under new names, and the
    /// package files it links to (pictures, embedded objects) copied over.
    /// </summary>
    private XElement Keep(XElement element)
    {
        XElement copy = new(element);
        if (source is null)
        {
            return copy;
        }

        // The namespaces it uses keep the prefixes the source gave them.
        foreach (XNamespace ns in copy.DescendantsAndSelf().SelectMany(e => e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.Namespace).Append(e.Name.Namespace)).Distinct())
        {
            if (ns != XNamespace.None && ns != XmlNs && !Declared.Contains(ns.NamespaceName) && copy.GetPrefixOfNamespace(ns) is null
                && source.Content.GetPrefixOfNamespace(ns) is { } prefix)
            {
                copy.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
            }
        }

        foreach (XElement e in copy.DescendantsAndSelf())
        {
            foreach (XAttribute attribute in e.Attributes())
            {
                if (attribute.Name.LocalName.EndsWith("style-name", StringComparison.Ordinal) && attribute.Name.LocalName != "parent-style-name")
                {
                    if (KeptStyle(attribute.Value, 0) is { } renamed)
                    {
                        attribute.Value = renamed;
                    }
                }
                else if (attribute.Name == XLinkNs + "href")
                {
                    CopyFile(attribute.Value);
                }
            }
        }

        return copy;
    }

    /// <summary>The new name of a source automatic style, copied into the current file; null when the name is no automatic style.</summary>
    private string? KeptStyle(string name, int depth)
    {
        if (keptStyles.TryGetValue((scope, name), out string? known))
        {
            return known;
        }

        XElement? styles = (scope == masterStyles ? source!.Styles : source!.Content)?.Element(OfficeNs + "automatic-styles");
        XElement? style = styles?.Elements().FirstOrDefault(s => s.Attr(StyleNs, "name") == name);
        if (style is null || depth > 4)
        {
            return null;
        }

        XElement copy = new(style);
        foreach (XElement e in copy.DescendantsAndSelf())
        {
            foreach (XAttribute attribute in e.Attributes())
            {
                if (attribute.Name.LocalName is "data-style-name" or "list-style-name" && KeptStyle(attribute.Value, depth + 1) is { } inner)
                {
                    attribute.Value = inner;
                }
                else if (attribute.Name == XLinkNs + "href")
                {
                    CopyFile(attribute.Value);
                }
            }
        }

        copy.SetAttributeValue(StyleNs + "name", "");
        string renamed = scope.Add(copy, copy.Attr(StyleNs, "family") ?? copy.Name.LocalName);
        keptStyles[(scope, name)] = renamed;
        return renamed;
    }

    /// <summary>Copies a package file or sub-document a kept element links to, with its manifest entry.</summary>
    private void CopyFile(string href)
    {
        if (href.Length == 0 || href[0] == '#' || href.Contains(':', StringComparison.Ordinal))
        {
            return;
        }

        string path = OdfPackage.NormalizePath(href);
        if (files.ContainsKey(path) || directories.ContainsKey(path))
        {
            return;
        }

        if (source!.GetFile(path) is { } data)
        {
            files[path] = (data, source.Manifest.GetValueOrDefault(path) ?? ImageData.GuessContentType(data));
            return;
        }

        if (source.Manifest.TryGetValue(path + "/", out string? type))
        {
            directories[path] = type;
            foreach (string name in source.FileNames.Where(f => f.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
            {
                if (source.GetFile(name) is { } inner)
                {
                    files[name] = (inner, source.Manifest.GetValueOrDefault(name) ?? "application/octet-stream");
                }
            }
        }
    }
}
