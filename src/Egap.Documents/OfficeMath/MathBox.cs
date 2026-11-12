using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// A laid-out piece of math: its width, its height above and depth below the baseline, in points, and what it draws, placed
/// from its origin on the baseline with y growing down. Boxes nest: an object's box holds its arguments' boxes.
/// </summary>
internal sealed class MathBox
{
    /// <summary>The width.</summary>
    public float Width { get; set; }

    /// <summary>The height above the baseline.</summary>
    public float Ascent { get; set; }

    /// <summary>The depth below the baseline.</summary>
    public float Descent { get; set; }

    /// <summary>How far a superscript after the box moves right (the italics correction of a slanted last glyph).</summary>
    public float Italic { get; set; }

    /// <summary>Where an accent above the box centres, from its origin; null for the middle of its width.</summary>
    public float? TopAccent { get; set; }

    /// <summary>The spacing class the box has in its row.</summary>
    public MathClass Class { get; set; }

    /// <summary>The single glyph the box is, for scripts that kern against it and accents that attach to it; 0 otherwise.</summary>
    public ushort Glyph { get; set; }

    /// <summary>The size of <see cref="Glyph"/>, in points.</summary>
    public float GlyphSize { get; set; }

    /// <summary>Whether the box is an integral sign, whose scripts hang from its top and bottom.</summary>
    public bool Integral { get; set; }

    /// <summary>Whether the box draws nothing (a phantom that hides its content).</summary>
    public bool Hidden { get; set; }

    /// <summary>Whether a display line breaks before this atom (a run with <c>m:brk</c>).</summary>
    public bool BreakBefore { get; set; }

    /// <summary>For a break: the operator (1-based) of the first line that the next line aligns under; 0 for the wrap indent.</summary>
    public int AlignAt { get; set; }

    /// <summary>Whether the box is an alignment point (an <c>&amp;</c> in an equation array), which takes no space.</summary>
    public bool Alignment { get; set; }

    /// <summary>The glyphs, rules and boxes inside, in drawing order.</summary>
    public List<MathItem> Items { get; } = [];

    /// <summary>The total height.</summary>
    public float Height => Ascent + Descent;

    /// <summary>An empty box of a width.</summary>
    public static MathBox Space(float width) => new() { Width = width };

    /// <summary>Adds <paramref name="child"/> at (<paramref name="x"/>, <paramref name="y"/>), y down from this box's baseline.</summary>
    public void Add(MathBox child, float x, float y)
    {
        Items.Add(new MathItem.Child(child, x, y));
    }

    /// <summary>Draws the box with its baseline origin at <paramref name="origin"/>.</summary>
    public void Draw(Canvas canvas, Vector2 origin, Paint paint)
    {
        if (Hidden)
        {
            return;
        }

        foreach (MathItem item in Items)
        {
            switch (item)
            {
                case MathItem.Child child:
                    child.Box.Draw(canvas, origin + new Vector2(child.X, child.Y), paint);
                    break;
                case MathItem.Glyphs glyphs:
                    canvas.DrawGlyphRun(GlyphRun.Create(new Font(glyphs.Face, glyphs.Size), glyphs.Ids, glyphs.Positions), origin, paint);
                    break;
                case MathItem.Rule rule:
                    canvas.FillRect(Snap(canvas, new RectF(origin.X + rule.X, origin.Y + rule.Y, rule.Width, rule.Height)), paint);
                    break;
                case MathItem.Line line:
                    canvas.DrawLine(origin + line.Start, origin + line.End, new Stroke(line.Thickness), paint);
                    break;
            }
        }
    }

    /// <summary>
    /// A rule on a raster at whole pixels, at least one pixel thick, as PDF viewers draw thin filled rectangles: a fraction
    /// bar or overbar is then one crisp line, not two grey ones. Vector output and turned canvases keep the rule as it is.
    /// </summary>
    private static RectF Snap(Canvas canvas, RectF rect)
    {
        Matrix3x2 m = canvas.Matrix;
        if (canvas is not RasterCanvas<Egami.Pixels.Rgba8> || m.M12 != 0 || m.M21 != 0 || m.M11 <= 0 || m.M22 <= 0)
        {
            return rect;
        }

        float left = MathF.Round(rect.Left * m.M11 + m.M31), right = MathF.Round(rect.Right * m.M11 + m.M31);
        float top = MathF.Round(rect.Top * m.M22 + m.M32), bottom = MathF.Round(rect.Bottom * m.M22 + m.M32);
        bottom = Math.Max(bottom, top + 1);
        right = Math.Max(right, left + 1);
        return new RectF((left - m.M31) / m.M11, (top - m.M32) / m.M22, (right - left) / m.M11, (bottom - top) / m.M22);
    }

    /// <summary>The boxes inside, depth first, with their offsets from this box's origin (for tests and alternative text).</summary>
    public IEnumerable<(MathBox Box, float X, float Y)> Descendants(float x = 0, float y = 0)
    {
        foreach (MathItem item in Items)
        {
            if (item is MathItem.Child child)
            {
                yield return (child.Box, x + child.X, y + child.Y);
                foreach ((MathBox Box, float X, float Y) inner in child.Box.Descendants(x + child.X, y + child.Y))
                {
                    yield return inner;
                }
            }
        }
    }
}

/// <summary>Something a <see cref="MathBox"/> draws.</summary>
internal abstract record MathItem
{
    /// <summary>A box placed in its parent.</summary>
    public sealed record Child(MathBox Box, float X, float Y) : MathItem;

    /// <summary>Glyphs of one face and size at their positions (baseline origins, y down).</summary>
    public sealed record Glyphs(Typeface Face, float Size, ushort[] Ids, Vector2[] Positions) : MathItem;

    /// <summary>A filled rectangle: fraction bars, overbars, radical rules; its top left corner and size.</summary>
    public sealed record Rule(float X, float Y, float Width, float Height) : MathItem;

    /// <summary>A stroked line: border box sides and strikes, skewed fraction slashes.</summary>
    public sealed record Line(Vector2 Start, Vector2 End, float Thickness) : MathItem;
}
