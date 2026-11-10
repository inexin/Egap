using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Egap.Documents.DrawingML;

/// <summary>
/// DrawingML fills and lines (ECMA-376 part 1, 20.1.8): which fill a shape property list or line holds, a gradient's stops and angle,
/// a pattern's parts, and a preset dash's lengths.
/// </summary>
internal static class DrawingFills
{
    private static readonly string[] Names = ["noFill", "solidFill", "gradFill", "blipFill", "pattFill", "grpFill"];

    /// <summary>Whether <paramref name="element"/> is one of the six fill elements.</summary>
    public static bool IsFill(XElement element) => element.Name.Namespace == DrawingNs.A && Array.IndexOf(Names, element.Name.LocalName) >= 0;

    /// <summary>The local name of the fill <paramref name="holder"/> holds, such as <c>solidFill</c>, or null for none.</summary>
    public static string? Kind(XElement? holder) => holder?.Elements().FirstOrDefault(IsFill)?.Name.LocalName;

    /// <summary>A gradient fill's stops in file order: each one's position from 0 to 1 and the <c>a:gs</c> element holding its colour.</summary>
    public static IEnumerable<(double Position, XElement Stop)> Stops(XElement? gradient)
    {
        foreach (XElement stop in gradient?.Element(DrawingNs.A + "gsLst")?.Elements(DrawingNs.A + "gs") ?? [])
        {
            yield return ((DrawingNs.Double((string?)stop.Attribute("pos")) ?? 0) / 100000, stop);
        }
    }

    /// <summary>A linear gradient's angle in degrees clockwise from left to right, or null.</summary>
    public static double? Angle(XElement? gradient) =>
        DrawingNs.Double((string?)gradient?.Element(DrawingNs.A + "lin")?.Attribute("ang")) / 60000;

    /// <summary>The two ends of a linear gradient at <paramref name="degrees"/> that just covers a box, through its centre.</summary>
    public static (Vector2 Start, Vector2 End) LinearEnds(float x, float y, float width, float height, double degrees)
    {
        double angle = degrees * Math.PI / 180;
        Vector2 center = new(x + (width / 2), y + (height / 2));
        float reach = (float)((Math.Abs(Math.Cos(angle)) * width / 2) + (Math.Abs(Math.Sin(angle)) * height / 2));
        Vector2 along = new((float)Math.Cos(angle) * reach, (float)Math.Sin(angle) * reach);
        return (center - along, center + along);
    }

    /// <summary>
    /// How much of a preset pattern is ink, so a hatch can come out at a page's size as its two inks' blend: the percentage of a
    /// <c>pct</c> pattern, about a third for the others.
    /// </summary>
    public static double PatternInk(string? name)
    {
        if (name is { Length: > 3 } && name.StartsWith("pct", StringComparison.Ordinal) && int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int percent))
        {
            return Math.Clamp(percent / 100.0, 0, 1);
        }

        return 0.35;
    }

    /// <summary>The dash and gap lengths of a preset dash (<c>a:prstDash</c>) for a line <paramref name="w"/> wide; null for a solid line or one it does not know.</summary>
    public static float[]? Dash(string? preset, float w) => preset switch
    {
        "dot" => [w, w * 3],
        "sysDot" => [w, w],
        "dash" => [w * 4, w * 3],
        "sysDash" => [w * 3, w],
        "lgDash" => [w * 8, w * 3],
        "dashDot" => [w * 4, w * 3, w, w * 3],
        "sysDashDot" => [w * 3, w, w, w],
        "lgDashDot" => [w * 8, w * 3, w, w * 3],
        "lgDashDotDot" => [w * 8, w * 3, w, w * 3, w, w * 3],
        "sysDashDotDot" => [w * 3, w, w, w, w, w],
        _ => null,
    };
}
