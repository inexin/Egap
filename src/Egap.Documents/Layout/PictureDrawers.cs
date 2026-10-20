using Egami;
using Egami.Drawing;
using Egami.Svg;

namespace Egap.Documents.Layout;

/// <summary>Draws a vector picture the document stores as bytes into a rectangle on a canvas.</summary>
internal interface IPictureDrawer
{
    /// <summary>Whether this drawer handles pictures of <paramref name="contentType"/>.</summary>
    bool CanDraw(string contentType);

    /// <summary>Draws <paramref name="bytes"/> into <paramref name="rect"/>; throws when the picture is damaged.</summary>
    void Draw(ReadOnlyMemory<byte> bytes, Canvas canvas, RectF rect);
}

/// <summary>
/// The vector picture formats layout draws itself: EMF and WMF through Egami.Metafiles and SVG through Egami.Svg. Raster
/// formats are not here; they are decoded by Egami's codecs and drawn as images.
/// </summary>
internal static class PictureDrawers
{
    private static readonly IPictureDrawer[] Drawers = [new MetafileDrawer(), new SvgDrawer()];

    /// <summary>The drawer for <paramref name="contentType"/>, or <see langword="null"/> when it is a raster format.</summary>
    public static IPictureDrawer? For(string contentType)
    {
        foreach (IPictureDrawer drawer in Drawers)
        {
            if (drawer.CanDraw(contentType))
            {
                return drawer;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="e"/> is how a drawer reports a picture it cannot draw.</summary>
    public static bool IsDrawFailure(Exception e) =>
        e is NotSupportedException or InvalidDataException or ArgumentException or EgamiException or SvgException;

    // Word's EMF and WMF pictures play back through Egami.Metafiles.
    private sealed class MetafileDrawer : IPictureDrawer
    {
        public bool CanDraw(string contentType) => contentType is "image/x-emf" or "image/x-wmf" or "image/emf" or "image/wmf";

        public void Draw(ReadOnlyMemory<byte> bytes, Canvas canvas, RectF rect) => Egami.Metafiles.Metafile.Load(bytes).Draw(canvas, rect);
    }

    // SVG stays vector: drawn onto whatever canvas this is, fitted by its preserveAspectRatio.
    private sealed class SvgDrawer : IPictureDrawer
    {
        public bool CanDraw(string contentType) => contentType == "image/svg+xml";

        public void Draw(ReadOnlyMemory<byte> bytes, Canvas canvas, RectF rect)
        {
            using MemoryStream stream = new(bytes.ToArray(), writable: false);
            SvgDocument.Load(stream).Draw(canvas, rect);
        }
    }
}
