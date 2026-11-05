using System.Globalization;

namespace Egap.Documents.Word;

/// <summary>
/// Where a Word shape is anchored, as the RTF shape instance or the DOC FSPA gives it: its rectangle in twips from the reference
/// areas, the wrap mode (0 around, 1 top and bottom, 2 square, 3 none, 4 tight, 5 through) and side (0 both, 1 left, 2 right,
/// 3 largest), whether it is behind the text, and its stacking order.
/// </summary>
internal readonly record struct WordShapeAnchor(int Left, int Top, int Right, int Bottom, HorizontalAnchor HorizontalAnchor, VerticalAnchor VerticalAnchor,
    int Wrap, int WrapSide, bool Behind, long ZOrder);

/// <summary>
/// Office Art shapes as Word stores them, from their properties by [MS-ODRAW] name (RTF's <c>\sn</c> names; the DOC reader
/// names its properties the same): the kind, the placement and the look.
/// </summary>
internal static class WordShapes
{
    /// <summary>The model's kind for an MSOSPT shape type, or null for one it has no kind for.</summary>
    public static ShapeKind? Kind(int shapeType) => shapeType switch
    {
        1 or 202 => ShapeKind.Rectangle,
        2 => ShapeKind.RoundedRectangle,
        3 => ShapeKind.Ellipse,
        5 => ShapeKind.Triangle,
        13 => ShapeKind.RightArrow,
        20 or 32 => ShapeKind.Line,
        _ => null,
    };

    /// <summary>Where a shape floats: its anchor, overridden by the position properties (posh, posrelh, posv, posrelv) where they are set.</summary>
    public static FloatingPlacement Placement(WordShapeAnchor info, IReadOnlyDictionary<string, string> properties)
    {
        HorizontalAnchor horizontalAnchor = Number(properties, "posrelh") switch
        {
            0 => HorizontalAnchor.Margin,
            1 => HorizontalAnchor.Page,
            2 => HorizontalAnchor.Column,
            3 => HorizontalAnchor.Character,
            _ => info.HorizontalAnchor,
        };
        VerticalAnchor verticalAnchor = Number(properties, "posrelv") switch
        {
            0 => VerticalAnchor.Margin,
            1 => VerticalAnchor.Page,
            2 => VerticalAnchor.Paragraph,
            3 => VerticalAnchor.Line,
            _ => info.VerticalAnchor,
        };
        FloatingAlignment horizontal = Number(properties, "posh") switch
        {
            1 => FloatingAlignment.Start,
            2 => FloatingAlignment.Center,
            3 => FloatingAlignment.End,
            4 => FloatingAlignment.Inside,
            5 => FloatingAlignment.Outside,
            _ => FloatingAlignment.None,
        };
        FloatingAlignment vertical = Number(properties, "posv") switch
        {
            1 => FloatingAlignment.Start,
            2 => FloatingAlignment.Center,
            3 => FloatingAlignment.End,
            4 => FloatingAlignment.Inside,
            5 => FloatingAlignment.Outside,
            _ => FloatingAlignment.None,
        };
        WrapMode wrap = info.Wrap switch
        {
            1 => WrapMode.TopAndBottom,
            2 => WrapMode.Square,
            4 => WrapMode.Tight,
            5 => WrapMode.Through,
            _ => info.Behind || Number(properties, "fBehindDocument") == 1 ? WrapMode.BehindText : WrapMode.InFrontOfText,
        };
        return new FloatingPlacement
        {
            HorizontalAnchor = horizontalAnchor,
            VerticalAnchor = verticalAnchor,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            HorizontalOffset = WordProperties.Twips(Math.Min(info.Left, info.Right)),
            VerticalOffset = WordProperties.Twips(Math.Min(info.Top, info.Bottom)),
            Wrap = wrap,
            WrapSide = info.WrapSide switch { 1 => WrapSide.Left, 2 => WrapSide.Right, 3 => WrapSide.Largest, _ => WrapSide.Both },
            DistanceLeft = Emu(properties, "dxWrapDistLeft", 9),
            DistanceRight = Emu(properties, "dxWrapDistRight", 9),
            DistanceTop = Emu(properties, "dyWrapDistTop", 0),
            DistanceBottom = Emu(properties, "dyWrapDistBottom", 0),
            ZOrder = info.ZOrder,
            AllowOverlap = Number(properties, "fAllowOverlap") != 0,
            LayoutInCell = Number(properties, "fLayoutInCell") != 0,
        };
    }

    /// <summary>A shape's fill, line and text box insets from its properties; the defaults are a white fill and a black line.</summary>
    public static void ApplyLook(Shape shape, IReadOnlyDictionary<string, string> properties)
    {
        if (Number(properties, "fFilled") != 0)
        {
            shape.Fill = ColorOf(properties, "fillColor") ?? DocumentColor.FromRgb(new Egami.Pixels.Rgba8(255, 255, 255, 255));
        }

        if (Number(properties, "fLine") != 0)
        {
            double width = Emu(properties, "lineWidth", 0.75);
            shape.Outline = new Border(BorderStyle.Single, width, ColorOf(properties, "lineColor") ?? DocumentColor.FromRgb(new Egami.Pixels.Rgba8(0, 0, 0, 255)));
        }

        if (shape.TextBox is not null)
        {
            shape.TextInsets = (Emu(properties, "dxTextLeft", 7.2), Emu(properties, "dyTextTop", 3.6), Emu(properties, "dxTextRight", 7.2), Emu(properties, "dyTextBottom", 3.6));
            shape.FitToText = Number(properties, "fFitShapeToText") == 1;
        }
    }

    public static int? Number(IReadOnlyDictionary<string, string> properties, string name)
    {
        return properties.TryGetValue(name, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
    }

    private static double Emu(IReadOnlyDictionary<string, string> properties, string name, double fallback)
    {
        return Number(properties, name) is int emu ? Math.Clamp(emu / 12700.0, 0, 1584) : fallback;
    }

    /// <summary>A shape colour property: BGR in the low three bytes; system and scheme colours (high byte set) are left out.</summary>
    private static DocumentColor? ColorOf(IReadOnlyDictionary<string, string> properties, string name)
    {
        if (Number(properties, name) is not int value || (value & unchecked((int)0xFF000000)) != 0)
        {
            return null;
        }

        return DocumentColor.FromRgb(new Egami.Pixels.Rgba8((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), 255));
    }
}
