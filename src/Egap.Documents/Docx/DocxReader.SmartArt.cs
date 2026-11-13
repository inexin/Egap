using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>SmartArt: the drawing Office stores beside a diagram's data, and the diagram's text from its data model.</summary>
internal sealed partial class DocxReader
{
    private static readonly XNamespace Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private static readonly XNamespace Dsp = "http://schemas.microsoft.com/office/drawing/2008/diagram";
    private const string DiagramDrawingType = "http://schemas.microsoft.com/office/2007/relationships/diagramDrawing";

    /// <summary>
    /// A SmartArt diagram's stored drawing (its <c>dsp:drawing</c>, with the pictures it names) and its text, one line per point of
    /// the data model; a null drawing when the file stores none (Word 2007 wrote none) or it cannot be read.
    /// </summary>
    private (XElement? Drawing, Dictionary<string, ImageData>? Images, string Text) ReadDiagram(XElement drawing)
    {
        XElement? relIds = drawing.Descendants(Dgm + "relIds").FirstOrDefault();
        OpcPart? data = (string?)relIds?.Attribute(Ns.R + "dm") is { } dm ? PartOf(currentPart, dm) : null;
        XElement? model = null;
        try
        {
            model = data?.LoadXml().Root;
        }
        catch (DocumentException)
        {
            // A diagram whose data cannot be read keeps the text its markup has.
        }

        string text = model is null ? "" : DiagramText(model);

        // The data model names the drawing through a relationship of the part the diagram sits in; Office also relates it from the data part.
        OpcPart? drawingPart = null;
        if (model?.Descendants(Dsp + "dataModelExt").FirstOrDefault() is { } extension && (string?)extension.Attribute("relId") is { Length: > 0 } relId)
        {
            drawingPart = PartOf(currentPart, relId);
        }

        drawingPart ??= data?.RelatedPart(DiagramDrawingType);
        XElement? tree = null;
        Dictionary<string, ImageData>? images = null;
        try
        {
            tree = drawingPart?.LoadXml().Root is { } root && root.Name == Dsp + "drawing" ? root : null;
        }
        catch (DocumentException)
        {
            tree = null;
        }

        if (tree is not null && drawingPart is not null)
        {
            foreach (XElement blip in tree.Descendants(Ns.A + "blip"))
            {
                if ((string?)blip.Attribute(Ns.R + "embed") is { } id && PartOf(drawingPart, id) is { } part)
                {
                    string type = part.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? part.ContentType : ImageData.GuessContentType(part.Data);
                    (images ??= [])[id] = new ImageData(part.Data, type);
                }
            }
        }

        return (tree, images, text);
    }

    private OpcPart? PartOf(OpcPart from, string relationshipId) =>
        from.GetRelationship(relationshipId) is { External: false } relationship ? package.GetPart(relationship.TargetName) : null;

    /// <summary>The text of a data model's content points (not its transitions or presentation points), one line each, in order.</summary>
    internal static string DiagramText(XElement model) =>
        string.Join('\n', DiagramPoints(model).Select(p => p.Text).Where(t => t.Length > 0));

    /// <summary>A data model's content points in order: each one's text and its depth below the document point.</summary>
    internal static List<(string Text, int Depth)> DiagramPoints(XElement model)
    {
        Dictionary<string, XElement> points = [];
        foreach (XElement point in model.Element(Dgm + "ptLst")?.Elements(Dgm + "pt") ?? [])
        {
            if ((string?)point.Attribute("modelId") is { } id)
            {
                points.TryAdd(id, point);
            }
        }

        // Parent links, ordered by srcOrd, give the outline; a model without them reads in file order.
        Dictionary<string, List<(int Order, string Child)>> children = [];
        foreach (XElement connection in model.Element(Dgm + "cxnLst")?.Elements(Dgm + "cxn") ?? [])
        {
            string type = (string?)connection.Attribute("type") ?? "parOf";
            if (type != "parOf" || (string?)connection.Attribute("srcId") is not { } source || (string?)connection.Attribute("destId") is not { } destination)
            {
                continue;
            }

            int order = int.TryParse((string?)connection.Attribute("srcOrd"), out int value) ? value : 0;
            (children.TryGetValue(source, out List<(int, string)>? list) ? list : children[source] = []).Add((order, destination));
        }

        List<(string Text, int Depth)> result = [];
        XElement? document = points.Values.FirstOrDefault(p => (string?)p.Attribute("type") == "doc");
        HashSet<string> seen = [];
        if (document is not null && children.Count > 0)
        {
            Walk((string)document.Attribute("modelId")!, -1);
        }
        else
        {
            foreach (XElement point in points.Values.Where(IsContent))
            {
                result.Add((PointText(point), 0));
            }
        }

        return result;

        void Walk(string id, int depth)
        {
            if (depth > 64 || !seen.Add(id))
            {
                return;
            }

            if (depth >= 0 && points.TryGetValue(id, out XElement? point) && IsContent(point))
            {
                result.Add((PointText(point), depth));
            }

            if (children.TryGetValue(id, out List<(int Order, string Child)>? list))
            {
                foreach ((int _, string child) in list.OrderBy(c => c.Order))
                {
                    if (points.TryGetValue(child, out XElement? next) && IsContent(next))
                    {
                        Walk(child, depth + 1);
                    }
                }
            }
        }
    }

    private static bool IsContent(XElement point) => ((string?)point.Attribute("type") ?? "node") is "node" or "asst";

    private static string PointText(XElement point) =>
        point.Element(Dgm + "t") is { } body ? DrawingML.TextBodyPainter.Text(body).Replace('\n', ' ').Trim() : "";
}
