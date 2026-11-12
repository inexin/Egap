using System.Xml.Linq;

using Egap.Documents.Charts;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Xlsb;

/// <summary>
/// The drawing part of an XLSB sheet, which is DrawingML as in an XLSX file (ECMA-376 Part 1, 20.5): its pictures, charts and
/// shapes, made into drawings the workbook holds itself, as it holds an XLS file's, since it keeps none of the file's parts.
/// </summary>
internal static class XlsbDrawings
{
    private static readonly XNamespace Xdr = SheetObjectReader.Xdr;
    private static readonly XNamespace A = SheetObjectReader.A;

    public static void Read(Worksheet sheet, OpcPart part, OpcPackage package)
    {
        if (SafeXml.TryLoad(part)?.Root is not { } root || root.Name != Xdr + "wsDr")
        {
            return;
        }

        foreach (XElement anchor in root.Elements())
        {
            DrawingAnchorKind? kind = anchor.Name.LocalName switch
            {
                "twoCellAnchor" => DrawingAnchorKind.TwoCell,
                "oneCellAnchor" => DrawingAnchorKind.OneCell,
                "absoluteAnchor" => DrawingAnchorKind.Absolute,
                _ => null,
            };
            if (anchor.Name.Namespace != Xdr || kind is null || SheetObjectReader.ObjectOf(anchor) is not { } content)
            {
                continue;
            }

            SheetDrawing? made = content.Name.LocalName switch
            {
                "pic" => Picture(content, part, package),
                "graphicFrame" => Chart(content, part, package),
                "sp" or "grpSp" or "cxnSp" => Shape(anchor, content),
                _ => null,
            };
            if (made is null)
            {
                continue;
            }

            if (content.Descendants(Xdr + "cNvPr").FirstOrDefault() is { } properties)
            {
                made.Name = (string?)properties.Attribute("name") ?? "";
                made.Description = (string?)properties.Attribute("descr");
            }

            made.AnchorKind = kind.Value;
            if (anchor.Element(Xdr + "from") is { } from)
            {
                made.From = SheetObjectReader.Position(from);
            }

            if (anchor.Element(Xdr + "to") is { } to)
            {
                made.To = SheetObjectReader.Position(to);
            }

            if (anchor.Element(Xdr + "pos") is { } pos)
            {
                made.X = SheetObjectReader.Long(pos.Attribute("x"));
                made.Y = SheetObjectReader.Long(pos.Attribute("y"));
            }

            if (anchor.Element(Xdr + "ext") is { } ext)
            {
                made.Width = SheetObjectReader.Long(ext.Attribute("cx"));
                made.Height = SheetObjectReader.Long(ext.Attribute("cy"));
            }

            sheet.Drawings.AddRead(made);
        }
    }

    /// <summary>The first chart of a drawing part, such as a chart sheet's.</summary>
    public static Chart? FirstChart(OpcPart part, OpcPackage package)
    {
        if (SafeXml.TryLoad(part)?.Root is not { } root)
        {
            return null;
        }

        foreach (XElement frame in root.Descendants(Xdr + "graphicFrame"))
        {
            if (ChartPart(frame, part, package) is { } chartPart && Documents.Charts.Chart.Read(chartPart) is { } chart)
            {
                return chart;
            }
        }

        return null;
    }

    private static OpcPart? ChartPart(XElement frame, OpcPart part, OpcPackage package) =>
        SheetObjectReader.ChartReference(frame)?.Attribute(XlsxNames.R + "id")?.Value is { } id && part.GetRelationship(id) is { External: false } relationship
            ? package.GetPart(relationship.TargetName)
            : null;

    private static SheetDrawing? Picture(XElement content, OpcPart part, OpcPackage package)
    {
        if (content.Descendants(A + "blip").FirstOrDefault()?.Attribute(XlsxNames.R + "embed")?.Value is not { } embed
            || part.GetRelationship(embed) is not { External: false } image || package.GetPart(image.TargetName) is not { } imagePart)
        {
            return null;
        }

        SheetDrawing picture = new(SheetDrawingKind.Picture);
        picture.SetImageRead(imagePart.Data, imagePart.ContentType);
        return picture;
    }

    private static SheetDrawing? Chart(XElement content, OpcPart part, OpcPackage package)
    {
        if (ChartPart(content, part, package) is not { } chartPart || Documents.Charts.Chart.Read(chartPart) is not { } chart)
        {
            return null;
        }

        SheetDrawing made = new(SheetDrawingKind.Chart);
        made.SetChart(chart);
        return made;
    }

    /// <summary>
    /// A shape, a group or a connector the anchor's own element describes. What it names in other parts is left out, as
    /// those parts are not kept: a picture fill's or a grouped picture's image, a grouped chart, a hyperlink's target.
    /// </summary>
    private static SheetDrawing? Shape(XElement anchor, XElement content)
    {
        XElement kept = new(anchor);
        kept.Descendants(Xdr + "graphicFrame").Remove();
        foreach (XElement element in kept.DescendantsAndSelf())
        {
            foreach (XAttribute reference in element.Attributes().Where(a => a.Name.Namespace == XlsxNames.R).ToList())
            {
                if (element.Name.LocalName is "hlinkClick" or "hlinkHover" && reference.Name.LocalName == "id")
                {
                    reference.Value = "";
                }
                else
                {
                    reference.Remove();
                }
            }
        }

        SheetDrawingKind kind = content.Name.LocalName switch
        {
            "sp" => SheetDrawingKind.Shape,
            "grpSp" => SheetDrawingKind.Group,
            _ => SheetDrawingKind.Other,
        };
        SheetDrawing made = new(kind) { Source = kept };
        if (kind == SheetDrawingKind.Shape && content.Element(Xdr + "txBody") is { } body)
        {
            made.Text = string.Join("\n", body.Elements(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value))));
        }

        return made;
    }
}
