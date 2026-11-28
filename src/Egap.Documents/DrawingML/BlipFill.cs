using System.Xml.Linq;

using Egami.Drawing;

namespace Egap.Documents.DrawingML;

/// <summary>
/// Draws a picture fill (<c>a:blipFill</c>, ECMA-376 Part 1, 20.1.8.14) over a rectangle: the picture cropped by its source
/// rectangle (<c>a:srcRect</c>), then stretched into the fill rectangle (<c>a:stretch/a:fillRect</c>) or tiled (<c>a:tile</c>,
/// scaled, moved, aligned and mirrored), with the blip's colour changes and at its fixed transparency (<c>a:alphaModFix</c>).
/// The caller clips to the shape.
/// </summary>
internal static class BlipFill
{
    private static readonly XNamespace A = DrawingNs.A;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Pixels a picture is taken to have per inch when tiled, as Office takes them.</summary>
    private const double PixelsPerInch = 96;

    /// <summary>The relationship id of the picture a blip fill names, or null.</summary>
    public static string? Id(XElement blipFill) => (string?)blipFill.Element(A + "blip")?.Attribute(R + "embed");

    public static void Draw(Canvas canvas, RectF rect, XElement blipFill, Func<string, BlipImage?> resolve, Theme theme)
    {
        XElement? blip = blipFill.Element(A + "blip");
        if (Id(blipFill) is not { } id || resolve(id) is not { } picture || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        double alpha = (DrawingNs.Double((string?)blip!.Element(A + "alphaModFix")?.Attribute("amt")) ?? 100000) / 100000.0;
        if (alpha <= 0)
        {
            return;
        }

        int saved = alpha < 1 ? canvas.SaveLayer((float)Math.Min(1, alpha)) : canvas.Save();
        try
        {
            if (blipFill.Element(A + "tile") is { } tile)
            {
                Tile(canvas, rect, tile, picture, blip, theme);
            }
            else
            {
                // The fill rectangle insets (or, negative, outsets) the shape's box; the source rectangle crops the picture,
                // so the whole picture is drawn larger than the fill rectangle by what is cropped off.
                RectF fill = Inset(rect, blipFill.Element(A + "stretch")?.Element(A + "fillRect"));
                (double l, double t, double r, double b) = Edges(blipFill.Element(A + "srcRect"));
                double shownWidth = 1 - l - r;
                double shownHeight = 1 - t - b;
                if (shownWidth <= 1e-6 || shownHeight <= 1e-6 || fill.Width <= 0 || fill.Height <= 0)
                {
                    return;
                }

                double width = fill.Width / shownWidth;
                double height = fill.Height / shownHeight;
                RectF whole = new((float)(fill.X - (l * width)), (float)(fill.Y - (t * height)), (float)width, (float)height);
                if (l > 0 || t > 0 || r > 0 || b > 0)
                {
                    canvas.ClipRect(fill);
                }

                picture.Draw(canvas, whole, blip, theme);
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>A rectangle inset by fractions of its size, as <c>a:fillRect</c> and <c>a:srcRect</c> give them in thousandths of a percent.</summary>
    private static RectF Inset(RectF rect, XElement? edges)
    {
        (double l, double t, double r, double b) = Edges(edges);
        return new RectF((float)(rect.X + (l * rect.Width)), (float)(rect.Y + (t * rect.Height)), (float)(rect.Width * (1 - l - r)), (float)(rect.Height * (1 - t - b)));
    }

    private static (double L, double T, double R, double B) Edges(XElement? edges) =>
        (Fraction(edges, "l"), Fraction(edges, "t"), Fraction(edges, "r"), Fraction(edges, "b"));

    private static double Fraction(XElement? element, string name) => (DrawingNs.Double((string?)element?.Attribute(name)) ?? 0) / 100000.0;

    /// <summary>
    /// The picture repeated at its own size, scaled by <c>sx</c> and <c>sy</c>, from the corner <c>algn</c> names, moved by
    /// <c>tx</c> and <c>ty</c>, every other tile mirrored across as <c>flip</c> asks.
    /// </summary>
    private static void Tile(Canvas canvas, RectF rect, XElement tile, BlipImage picture, XElement blip, Theme theme)
    {
        if (picture.PixelSize is not { } pixels)
        {
            // A vector picture has no size of its own to repeat at: it fills the box.
            picture.Draw(canvas, rect, blip, theme);
            return;
        }

        double sx = (DrawingNs.Double((string?)tile.Attribute("sx")) ?? 100000) / 100000.0;
        double sy = (DrawingNs.Double((string?)tile.Attribute("sy")) ?? 100000) / 100000.0;
        float width = (float)Math.Abs(pixels.Width * 72 / PixelsPerInch * sx);
        float height = (float)Math.Abs(pixels.Height * 72 / PixelsPerInch * sy);
        if (width < 0.5f || height < 0.5f)
        {
            return;
        }

        string align = (string?)tile.Attribute("algn") ?? "tl";
        float x0 = rect.X + (float)((DrawingNs.Double((string?)tile.Attribute("tx")) ?? 0) / DrawingNs.EmuPerPoint);
        float y0 = rect.Y + (float)((DrawingNs.Double((string?)tile.Attribute("ty")) ?? 0) / DrawingNs.EmuPerPoint);
        if (align is "tr" or "r" or "br")
        {
            x0 += rect.Width - width;
        }
        else if (align is "t" or "ctr" or "b")
        {
            x0 += (rect.Width - width) / 2;
        }

        if (align is "bl" or "b" or "br")
        {
            y0 += rect.Height - height;
        }
        else if (align is "l" or "ctr" or "r")
        {
            y0 += (rect.Height - height) / 2;
        }

        // Back the start up to the box's corner, so tiles cover it whatever the offset; the anchor tile keeps its parity.
        int backX = (int)MathF.Ceiling((x0 - rect.X) / width);
        int backY = (int)MathF.Ceiling((y0 - rect.Y) / height);
        x0 -= backX * width;
        y0 -= backY * height;
        int columns = (int)Math.Min(4096, Math.Ceiling((rect.Right - x0) / width));
        int rows = (int)Math.Min(4096, Math.Ceiling((rect.Bottom - y0) / height));
        if ((long)columns * rows > 20000)
        {
            return;
        }

        string flip = (string?)tile.Attribute("flip") ?? "none";
        bool mirrorX = flip is "x" or "xy";
        bool mirrorY = flip is "y" or "xy";
        int saved = canvas.Save();
        canvas.ClipRect(rect);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                RectF cell = new(x0 + (column * width), y0 + (row * height), width, height);
                bool flipX = mirrorX && ((column - backX) & 1) != 0;
                bool flipY = mirrorY && ((row - backY) & 1) != 0;
                if (!flipX && !flipY)
                {
                    picture.Draw(canvas, cell, blip, theme);
                    continue;
                }

                int one = canvas.Save();
                canvas.Translate(cell.X + (cell.Width / 2), cell.Y + (cell.Height / 2));
                canvas.Scale(flipX ? -1 : 1, flipY ? -1 : 1);
                picture.Draw(canvas, new RectF(-cell.Width / 2, -cell.Height / 2, cell.Width, cell.Height), blip, theme);
                canvas.RestoreToCount(one);
            }
        }

        canvas.RestoreToCount(saved);
    }
}
