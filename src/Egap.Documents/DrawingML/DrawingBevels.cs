using System.Xml.Linq;

using Egami.Drawing;

namespace Egap.Documents.DrawingML;

/// <summary>
/// A shape's top bevel (<c>a:sp3d/a:bevelT</c>, ECMA-376 Part 1, 20.1.5.9 and 20.1.5.1) lit as its light rig (<c>a:scene3d/a:lightRig</c>)
/// and material (<c>prstMaterial</c>) ask, as Egami's bevel draws it. The rest of the 3D look (extrusion, contour, the camera's
/// turn) is left out: the shape draws as its flat front face, and the bottom bevel is behind it.
/// </summary>
internal static class DrawingBevels
{
    private static readonly XNamespace A = DrawingNs.A;

    /// <summary>The default bevel's width and height, 6 points.</summary>
    private const double DefaultSize = 76200;

    /// <summary>
    /// The bevel of a shape whose properties are <paramref name="spPr"/> and whose theme effect style (the parent of its effect list)
    /// is <paramref name="effectStyle"/>; the shape's own 3D settings win. Null for a shape with no top bevel.
    /// </summary>
    public static Bevel? Of(XElement? spPr, XElement? effectStyle)
    {
        XElement? sp3d = spPr?.Element(A + "sp3d") ?? effectStyle?.Element(A + "sp3d");
        if (sp3d?.Element(A + "bevelT") is not { } top)
        {
            return null;
        }

        XElement? scene = spPr?.Element(A + "scene3d") ?? effectStyle?.Element(A + "scene3d");
        float width = (float)((DrawingNs.Double((string?)top.Attribute("w")) ?? DefaultSize) / DrawingNs.EmuPerPoint);
        float height = (float)((DrawingNs.Double((string?)top.Attribute("h")) ?? DefaultSize) / DrawingNs.EmuPerPoint);
        if (!(width > 0) || !(height > 0))
        {
            return null;
        }

        return new Bevel
        {
            Profile = Profile((string?)top.Attribute("prst")),
            Width = width,
            Height = height,
            Material = Material((string?)sp3d.Attribute("prstMaterial")),
            Light = Light((string?)scene?.Element(A + "lightRig")?.Attribute("dir")),
        };
    }

    private static BevelProfile Profile(string? preset) => preset switch
    {
        "angle" => BevelProfile.Angle,
        "artDeco" => BevelProfile.ArtDeco,
        "circle" => BevelProfile.Circle,
        "convex" => BevelProfile.Convex,
        "coolSlant" => BevelProfile.CoolSlant,
        "cross" => BevelProfile.Cross,
        "divot" => BevelProfile.Divot,
        "hardEdge" => BevelProfile.HardEdge,
        "riblet" => BevelProfile.Riblet,
        "slope" => BevelProfile.Slope,
        "softRound" => BevelProfile.SoftRound,
        _ => BevelProfile.RelaxedInset,
    };

    private static BevelMaterial Material(string? preset) => preset switch
    {
        "matte" or "legacyMatte" => BevelMaterial.Matte,
        "plastic" or "legacyPlastic" => BevelMaterial.Plastic,
        "metal" or "legacyMetal" => BevelMaterial.Metal,
        "softmetal" => BevelMaterial.SoftMetal,
        "dkEdge" => BevelMaterial.DarkEdge,
        "softEdge" => BevelMaterial.SoftEdge,
        "flat" or "legacyWireframe" => BevelMaterial.Flat,
        "clear" => BevelMaterial.Clear,
        "powder" => BevelMaterial.Powder,
        "translucentPowder" => BevelMaterial.TranslucentPowder,
        _ => BevelMaterial.WarmMatte,
    };

    /// <summary>
    /// The light a rig's direction gives, fitted to PowerPoint's three-point rig on the angle bevel: its key light comes 30
    /// degrees round from the direction (from above and to the left for the top), 65 degrees up, at 0.7 of full strength.
    /// </summary>
    private static BevelLight Light(string? direction)
    {
        BevelLight from = BevelLight.FromDirection(Direction(direction));
        return new BevelLight(from.Angle + 30, 65, 0.7f);
    }

    /// <summary>The light rig's direction; a rig without one lights from the top, as Office's default rig does.</summary>
    private static LightDirection Direction(string? direction) => direction switch
    {
        "tl" => LightDirection.TopLeft,
        "tr" => LightDirection.TopRight,
        "l" => LightDirection.Left,
        "r" => LightDirection.Right,
        "bl" => LightDirection.BottomLeft,
        "b" => LightDirection.Bottom,
        "br" => LightDirection.BottomRight,
        _ => LightDirection.Top,
    };
}
