using System.Xml.Linq;

using Egami.Drawing;

using Egap.Presentations.Pptx;

namespace Egap.Presentations.Drawing;

/// <summary>
/// Draws a picture fill (<c>a:blipFill</c>, <c>p:blipFill</c>) over a rectangle: the picture cropped by its source rectangle
/// (<c>a:srcRect</c>), then stretched into the fill rectangle (<c>a:stretch/a:fillRect</c>) or tiled (<c>a:tile</c>), at the
/// blip's transparency (<c>a:alphaModFix</c>). The caller clips to the shape.
/// </summary>
internal static class PictureFill
{
    private static readonly XNamespace A = PptxNames.A;

    /// <summary>Pixels a picture without a resolution of its own is taken to have per inch, as Office takes them.</summary>
    private const double PixelsPerInch = 96;

    public static void Draw(Canvas canvas, RectF rect, XElement blipFill, Func<string, Picture?> resolve)
    {
        XElement? blip = blipFill.Element(A + "blip");
        if ((string?)blip?.Attribute(PptxNames.R + "embed") is not { } id || resolve(id) is not { } picture || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        double alpha = (PptxNames.Long(blip!.Element(A + "alphaModFix"), "amt") ?? 100000) / 100000.0;
        if (alpha <= 0)
        {
            return;
        }

        int saved = alpha < 1 ? canvas.SaveLayer((float)alpha) : canvas.Save();
        try
        {
            if (blipFill.Element(A + "tile") is { } tile)
            {
                Tile(canvas, rect, tile, picture);
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

                picture.Draw(canvas, whole);
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

    private static double Fraction(XElement? element, string name) => (PptxNames.Long(element, name) ?? 0) / 100000.0;

    /// <summary>The picture repeated at its own size, scaled by <c>sx</c> and <c>sy</c>, from the corner <c>algn</c> names, moved by <c>tx</c> and <c>ty</c>.</summary>
    private static void Tile(Canvas canvas, RectF rect, XElement tile, Picture picture)
    {
        if (picture.PixelSize is not { } pixels)
        {
            // A vector picture has no size of its own to repeat at: it fills the box.
            picture.Draw(canvas, rect);
            return;
        }

        double sx = (PptxNames.Long(tile, "sx") ?? 100000) / 100000.0;
        double sy = (PptxNames.Long(tile, "sy") ?? 100000) / 100000.0;
        float width = (float)(Math.Abs(pixels.Width * 72 / PixelsPerInch * sx));
        float height = (float)(Math.Abs(pixels.Height * 72 / PixelsPerInch * sy));
        if (width < 0.5f || height < 0.5f)
        {
            return;
        }

        string align = (string?)tile.Attribute("algn") ?? "tl";
        float x0 = rect.X + (float)((PptxNames.Long(tile, "tx") ?? 0) / (double)ShapeTransform.EmusPerPoint);
        float y0 = rect.Y + (float)((PptxNames.Long(tile, "ty") ?? 0) / (double)ShapeTransform.EmusPerPoint);
        if (align.Contains('r', StringComparison.Ordinal))
        {
            x0 += rect.Width - width;
        }
        else if (align is "t" or "b" or "ctr")
        {
            x0 += (rect.Width - width) / 2;
        }

        if (align.StartsWith('b'))
        {
            y0 += rect.Height - height;
        }
        else if (align is "l" or "r" or "ctr")
        {
            y0 += (rect.Height - height) / 2;
        }

        // Back the start up to the box's corner, so tiles cover it whatever the offset.
        x0 -= MathF.Ceiling((x0 - rect.X) / width) * width;
        y0 -= MathF.Ceiling((y0 - rect.Y) / height) * height;
        int columns = (int)Math.Min(4096, Math.Ceiling((rect.Right - x0) / width));
        int rows = (int)Math.Min(4096, Math.Ceiling((rect.Bottom - y0) / height));
        if ((long)columns * rows > 20000)
        {
            return;
        }

        int saved = canvas.Save();
        canvas.ClipRect(rect);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                picture.Draw(canvas, new RectF(x0 + (column * width), y0 + (row * height), width, height));
            }
        }

        canvas.RestoreToCount(saved);
    }
}
