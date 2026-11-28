using System.Xml.Linq;

using Egap.Documents.DrawingML;
using Egap.Presentations.Pptx;

namespace Egap.Presentations.Drawing;

/// <summary>
/// What a shape draws with once its placeholder chain is followed: a slide's placeholder takes what it does not set itself
/// (its place, geometry, fill, line, effects and style) from its layout's placeholder, and that one from the master's. The text
/// bodies come in the same order, the shape's own first, for their body properties and list styles.
/// </summary>
internal sealed class ShapeLook
{
    private static readonly XNamespace A = PptxNames.A;
    private static readonly XNamespace P = PptxNames.P;

    /// <summary>The groups of shape properties a placeholder takes whole from the first shape in its chain that has them.</summary>
    private static readonly Func<XElement, bool>[] Groups =
    [
        e => e.Name == A + "xfrm",
        e => e.Name == A + "prstGeom" || e.Name == A + "custGeom",
        DrawingFills.IsFill,
        e => e.Name == A + "ln",
        e => e.Name == A + "effectLst" || e.Name == A + "effectDag",
        e => e.Name == A + "scene3d",
        e => e.Name == A + "sp3d",
    ];

    private ShapeLook(XElement properties, XElement? style, List<XElement> textBodies, string? placeholderType)
    {
        Properties = properties;
        Style = style;
        TextBodies = textBodies;
        PlaceholderType = placeholderType;
    }

    /// <summary>The shape properties (<c>p:spPr</c>), merged down the placeholder chain.</summary>
    public XElement Properties { get; }

    /// <summary>The shape's style (<c>p:style</c>) with its theme references, its own or the first in the chain.</summary>
    public XElement? Style { get; }

    /// <summary>The text bodies, the shape's own first, then its layout placeholder's and master placeholder's.</summary>
    public List<XElement> TextBodies { get; }

    /// <summary>The placeholder type (<c>title</c>, <c>body</c>, <c>dt</c>, …; <c>obj</c> for one without a type), or null for a shape that is not a placeholder.</summary>
    public string? PlaceholderType { get; }

    public static ShapeLook Of(SlideShape shape)
    {
        List<SlideShape> chain = [shape];
        SlideShape current = shape;
        while (chain.Count < 4 && current.InheritedPlaceholder is { } inherited && !chain.Contains(inherited))
        {
            chain.Add(inherited);
            current = inherited;
        }

        XElement? own = OwnProperties(shape);
        XElement properties;
        if (chain.Count == 1)
        {
            properties = own ?? new XElement(P + "spPr");
        }
        else
        {
            properties = new XElement(P + "spPr", own?.Attributes());
            foreach (Func<XElement, bool> group in Groups)
            {
                foreach (SlideShape link in chain)
                {
                    List<XElement> found = OwnProperties(link)?.Elements().Where(group).ToList() ?? [];
                    if (found.Count > 0)
                    {
                        properties.Add(found);
                        break;
                    }
                }
            }
        }

        XElement? style = chain.Select(s => s.Element.Element(P + "style")).FirstOrDefault(s => s is not null);
        List<XElement> bodies = [.. chain.Select(s => s.Element.Element(P + "txBody")).OfType<XElement>()];
        string? type = shape.PlaceholderElement is { } ph ? (string?)ph.Attribute("type") ?? "obj" : null;
        if (type is not null && chain.Count > 1 && chain[^1].PlaceholderElement is { } root)
        {
            // A slide placeholder matched by index takes the text style of the type its chain ends at, as a content
            // placeholder filled on a title-only layout still sets its text as body text.
            type = (string?)root.Attribute("type") ?? type;
        }

        return new ShapeLook(properties, style, bodies, type);
    }

    private static XElement? OwnProperties(SlideShape shape) => shape.Element.Element(P + "spPr");
}
