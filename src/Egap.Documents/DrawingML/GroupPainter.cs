using System.Xml.Linq;

using Egami.Drawing;

namespace Egap.Documents.DrawingML;

/// <summary>
/// Draws a DrawingML shape group (<c>wpg:wgp</c>, <c>a:grpSp</c>, <c>p:grpSp</c>, <c>dsp:grpSp</c>, or a SmartArt drawing's
/// <c>dsp:spTree</c>): each child shape, picture, connector and nested group in its place, through the group's child coordinate
/// space. Children are matched by local name, so Word, PowerPoint and SmartArt drawings all draw the same way.
/// </summary>
internal static class GroupPainter
{
    private static readonly XNamespace A = DrawingNs.A;

    /// <summary>
    /// Draws the group's children into <paramref name="box"/>. <paramref name="group"/> is the group element; its group shape
    /// properties (<c>grpSpPr</c>) map the children's coordinates onto the box, or, with none, the children's own extent does;
    /// <paramref name="space"/>, in EMUs, overrides both (a SmartArt drawing's coordinates are its frame's).
    /// </summary>
    public static void Draw(Canvas canvas, RectF box, XElement group, ShapePaintContext context, XElement? outerFill = null, (double X, double Y, double Width, double Height)? space = null)
    {
        XElement? properties = Child(group, "grpSpPr");
        XElement? xfrm = properties?.Element(A + "xfrm");
        XElement? fill = DrawingFills.Kind(properties) is { } kind ? properties!.Element(A + kind) : null;
        if (fill?.Name == A + "grpFill")
        {
            fill = outerFill;
        }

        space ??= ChildSpace(xfrm) ?? Extent(group);
        if (space is not { Width: > 0, Height: > 0 } child)
        {
            return;
        }

        int saved = canvas.Save();
        try
        {
            ShapePainter.Transform(canvas, box, xfrm);
            double sx = box.Width / child.Width;
            double sy = box.Height / child.Height;
            foreach (XElement element in group.Elements())
            {
                string name = element.Name.LocalName;
                if (name is not ("sp" or "wsp" or "cxnSp" or "pic" or "grpSp"))
                {
                    continue;
                }

                XElement? spPr = Child(element, name == "grpSp" ? "grpSpPr" : "spPr");
                if (Offset(spPr?.Element(A + "xfrm")) is not { } place)
                {
                    continue;
                }

                RectF rect = new(
                    (float)(box.X + ((place.X - child.X) * sx)),
                    (float)(box.Y + ((place.Y - child.Y) * sy)),
                    (float)(place.Width * sx),
                    (float)(place.Height * sy));
                switch (name)
                {
                    case "grpSp":
                        Draw(canvas, rect, element, context, fill);
                        break;
                    case "pic":
                        DrawPicture(canvas, rect, element, spPr, context);
                        break;
                    default:
                        RectF? textBox = Offset(Child(element, "txXfrm")) is { } text
                            ? new RectF((float)(box.X + ((text.X - child.X) * sx)), (float)(box.Y + ((text.Y - child.Y) * sy)), (float)(text.Width * sx), (float)(text.Height * sy))
                            : null;
                        double textRotation = (DrawingNs.Double((string?)Child(element, "txXfrm")?.Attribute("rot")) ?? 0) / 60000;
                        ShapePainter.Draw(canvas, rect, spPr, Child(element, "style"), context, fill, Child(element, "txBody"), textBox, textRotation);
                        break;
                }
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>A child element by local name, in whatever namespace the host format uses.</summary>
    internal static XElement? Child(XElement parent, string localName) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    /// <summary>An <c>a:xfrm</c>'s offset and extent, in EMUs.</summary>
    internal static (double X, double Y, double Width, double Height)? Offset(XElement? xfrm)
    {
        XElement? off = xfrm?.Element(A + "off");
        XElement? ext = xfrm?.Element(A + "ext");
        if (off is null || ext is null)
        {
            return null;
        }

        return (Number(off, "x"), Number(off, "y"), Number(ext, "cx"), Number(ext, "cy"));
    }

    private static (double X, double Y, double Width, double Height)? ChildSpace(XElement? xfrm)
    {
        XElement? off = xfrm?.Element(A + "chOff");
        XElement? ext = xfrm?.Element(A + "chExt");
        if (off is null || ext is null)
        {
            return null;
        }

        return (Number(off, "x"), Number(off, "y"), Number(ext, "cx"), Number(ext, "cy"));
    }

    /// <summary>The box round the children's own places, for a group that gives no child space (a SmartArt drawing's shape tree).</summary>
    private static (double X, double Y, double Width, double Height)? Extent(XElement group)
    {
        double left = double.MaxValue;
        double top = double.MaxValue;
        double right = double.MinValue;
        double bottom = double.MinValue;
        foreach (XElement element in group.Elements())
        {
            XElement? spPr = Child(element, element.Name.LocalName == "grpSp" ? "grpSpPr" : "spPr");
            if (Offset(spPr?.Element(A + "xfrm")) is { } place)
            {
                left = Math.Min(left, place.X);
                top = Math.Min(top, place.Y);
                right = Math.Max(right, place.X + place.Width);
                bottom = Math.Max(bottom, place.Y + place.Height);
            }
        }

        return left <= right && top <= bottom ? (left, top, right - left, bottom - top) : null;
    }

    private static void DrawPicture(Canvas canvas, RectF rect, XElement picture, XElement? spPr, ShapePaintContext context)
    {
        XElement? blipFill = Child(picture, "blipFill");
        XElement? blip = blipFill?.Element(A + "blip");
        string? id = (string?)blip?.Attribute(XName.Get("embed", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"));
        int saved = canvas.Save();
        try
        {
            ShapePainter.Transform(canvas, rect, spPr?.Element(A + "xfrm"));
            if (id is not null && context.Picture is { } draw)
            {
                ShapeGeometry geometry = ShapeGeometry.Read(spPr) ?? ShapeGeometry.Preset("rect")!;
                GeometryLayout layout = geometry.Layout(rect.Width, rect.Height, spPr?.Element(A + "prstGeom")?.Element(A + "avLst"));
                int clip = canvas.Save();
                canvas.Translate(rect.X, rect.Y);
                foreach (GeometryPath path in layout.Paths)
                {
                    canvas.ClipPath(path.Path);
                }

                draw(canvas, id, new RectF(0, 0, rect.Width, rect.Height));
                canvas.RestoreToCount(clip);
            }

            // The picture's own outline and effects, without a fill of its own.
            XElement outline = spPr is null ? new XElement(A + "spPr") : new XElement(spPr);
            outline.Elements().Where(DrawingFills.IsFill).Remove();
            outline.Element(A + "xfrm")?.Remove();
            ShapePainter.Draw(canvas, rect, outline, Child(picture, "style"), context);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    private static double Number(XElement element, string name) => DrawingNs.Double((string?)element.Attribute(name)) ?? 0;
}
