using System.Collections.Concurrent;
using System.Xml.Linq;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Documents.DrawingML;

/// <summary>
/// A picture a DrawingML blip names (<c>a:blip r:embed</c>): its bytes, decoded once, drawn as it is or with the blip's colour
/// changes (ECMA-376 Part 1, 20.1.8: grayscale, bi-level, duotone, colour change and replace, luminance, HSL, tint and the alpha
/// changes) worked out on its pixels and kept for the next draw with the same changes.
/// </summary>
internal sealed class BlipImage
{
    private static readonly XNamespace A = DrawingNs.A;

    private readonly string contentType;
    private readonly byte[] data;
    private readonly ConcurrentDictionary<string, Raster<Rgba8>?> adjusted = new(StringComparer.Ordinal);
    private EncodedImage? image;
    private Raster<Rgba8>? pixels;
    private bool triedImage;
    private bool triedPixels;

    public BlipImage(string contentType, byte[] data)
    {
        this.contentType = contentType;
        this.data = data;
    }

    /// <summary>The picture's size in pixels, or null when it cannot be decoded or draws as vectors.</summary>
    public (int Width, int Height)? PixelSize => Image() is { } decoded ? (decoded.Width, decoded.Height) : null;

    /// <summary>Whether the picture draws as vectors (a metafile or EPS), which takes no pixel changes.</summary>
    public bool IsVector => PictureDrawers.For(contentType) is not null;

    /// <summary>
    /// Draws the picture stretched over <paramref name="rect"/>, with the colour changes <paramref name="blip"/> holds; a picture that
    /// cannot be read leaves it empty. The blip's fixed transparency (<c>alphaModFix</c>) is the caller's.
    /// </summary>
    public void Draw(Canvas canvas, RectF rect, XElement? blip, Theme theme)
    {
        try
        {
            if (PictureDrawers.For(contentType) is { } drawer)
            {
                drawer.Draw(data, canvas, rect);
                return;
            }

            if (blip is not null && BlipAdjustments.Has(blip))
            {
                if (Adjusted(blip, theme) is { } changed)
                {
                    canvas.DrawImage(changed, rect);
                }

                return;
            }

            if (Image() is { } decoded)
            {
                canvas.DrawImage(decoded, rect);
            }
        }
        catch (Exception e) when (PictureDrawers.IsDrawFailure(e))
        {
            // A damaged picture, or one in a format Egami has no decoder for: its box stays empty.
        }
    }

    /// <summary>The picture with the blip's changes, worked out once for each set of changes.</summary>
    private Raster<Rgba8>? Adjusted(XElement blip, Theme theme)
    {
        string key = BlipAdjustments.Key(blip);
        return adjusted.GetOrAdd(key, _ => Pixels() is { } source ? BlipAdjustments.Apply(source, blip, theme) : null);
    }

    private Raster<Rgba8>? Pixels()
    {
        if (!triedPixels)
        {
            triedPixels = true;
            try
            {
                pixels = Raster.Load<Rgba8>(data, Imaging.Context);
            }
            catch (Exception e) when (PictureDrawers.IsDrawFailure(e))
            {
                pixels = null;
            }
        }

        return pixels;
    }

    private EncodedImage? Image()
    {
        if (!triedImage)
        {
            triedImage = true;
            if (PictureDrawers.For(contentType) is null)
            {
                try
                {
                    image = EncodedImage.Load(data, Imaging.Context);
                }
                catch (Exception e) when (PictureDrawers.IsDrawFailure(e))
                {
                    image = null;
                }
            }
        }

        return image;
    }
}
