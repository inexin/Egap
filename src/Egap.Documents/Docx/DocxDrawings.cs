using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Egami.Drawing;

using Egap.Documents.DrawingML;

namespace Egap.Documents.Docx;

/// <summary>
/// The DrawingML markup of a Word shape or shape group as it was read, for the shape engine to draw: a shape's while its own
/// settings are as read (a shape changed through the model draws from the model), a group's always, as groups are not edited.
/// </summary>
internal static class DocxDrawings
{
    private static readonly ConditionalWeakTable<Island, XElement> Groups = [];

    // Kept beside the nodes rather than on their DocxExtension, which every node read from a file carries.
    private static readonly ConditionalWeakTable<Node, Dictionary<string, ImageData>> Images = [];

    /// <summary>Keeps a picture a shape's or group's blip names, by relationship id, for the shape engine to draw.</summary>
    public static void KeepImage(Node node, string id, ImageData image) => Images.GetOrCreateValue(node)[id] = image;

    private static readonly ConditionalWeakTable<Island, XElement> Diagrams = [];

    /// <summary>Keeps a SmartArt island's stored drawing (<c>dsp:drawing</c>) and the pictures it names.</summary>
    public static void KeepDiagram(Island island, XElement drawing, Dictionary<string, ImageData>? images)
    {
        Diagrams.AddOrUpdate(island, drawing);
        if (images is not null)
        {
            Images.AddOrUpdate(island, images);
        }
    }

    /// <summary>The <c>wps:wsp</c> a shape was read from, while its settings are unchanged; null otherwise.</summary>
    public static XElement? ShapeMarkup(Shape shape)
    {
        if (shape.Extensions.Get<DocxExtension>(DocxExtension.Format) is not { Original: { } original, Snapshot: ShapeState state } || state != ShapeState.Of(shape))
        {
            return null;
        }

        return original.Descendants(Ns.Wps + "wsp").FirstOrDefault();
    }

    /// <summary>The <c>wpg:wgp</c> of a Word shape group island; null for any other island.</summary>
    public static XElement? GroupMarkup(Island island)
    {
        if (island.Kind != IslandKind.ShapeGroup || island.Format != DocxExtension.Format)
        {
            return null;
        }

        return Groups.GetValue(island, static i =>
        {
            try
            {
                return XElement.Parse(i.Markup).Descendants(Ns.Wpg + "wgp").FirstOrDefault() ?? new XElement("none");
            }
            catch (System.Xml.XmlException)
            {
                return new XElement("none");
            }
        }) is { Name.LocalName: "wgp" } group ? group : null;
    }

    /// <summary>The shape tree of a SmartArt island's stored drawing; null for any other island or one without a drawing.</summary>
    public static XElement? DiagramTree(Island island) =>
        island.Kind == IslandKind.SmartArt && Diagrams.TryGetValue(island, out XElement? drawing)
            ? drawing.Elements().FirstOrDefault(e => e.Name.LocalName == "spTree")
            : null;

    /// <summary>Whether a SmartArt island draws as a list of its text: it has text but no stored drawing and no picture.</summary>
    public static bool DrawsAsList(Island island) =>
        island is { Kind: IslandKind.SmartArt, Fallback: null, Text.Length: > 0 } && island.Format == DocxExtension.Format && DiagramTree(island) is null;

    /// <summary>
    /// A SmartArt island's alternative text: the description or title the file gives the drawing, else the diagram's text, its
    /// points separated by semicolons.
    /// </summary>
    public static string? AlternateText(Island island)
    {
        if (island.Format == DocxExtension.Format && island.Markup.Length > 0)
        {
            try
            {
                if (XElement.Parse(island.Markup).Descendants(Ns.Wp + "docPr").FirstOrDefault() is { } docPr
                    && ((string?)docPr.Attribute("descr") is { Length: > 0 } description ? description : (string?)docPr.Attribute("title")) is { Length: > 0 } given)
                {
                    return given;
                }
            }
            catch (System.Xml.XmlException)
            {
                // The diagram's text stands in.
            }
        }

        return island.Text.Length > 0 ? island.Text.Replace("\n", "; ", StringComparison.Ordinal) : null;
    }

    /// <summary>The paint context for a shape or group read from Word: the theme, and its pictures through <paramref name="drawImage"/>.</summary>
    public static ShapePaintContext Context(Node node, Action<Canvas, ImageData, RectF> drawImage)
    {
        Dictionary<string, ImageData>? images = Images.TryGetValue(node, out Dictionary<string, ImageData>? kept) ? kept : null;
        return new ShapePaintContext(
            node.Document.Theme,
            images is null ? null : (canvas, id, rect) =>
            {
                if (images.TryGetValue(id, out ImageData? image))
                {
                    drawImage(canvas, image, rect);
                }
            },
            Layout.PageDrawing.FontsOf(node.Document));
    }
}
