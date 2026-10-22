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

        // A picture drawn on every page (a logo or a barcode in a header) is parsed and drawn once at each size: each document's
        // bytes keep the recording, which a PDF writes once as a form and shows on every page.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Drawn> Recorded = [];

        public void Draw(ReadOnlyMemory<byte> bytes, Canvas canvas, RectF rect)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment) && segment.Offset == 0
                && segment.Count == segment.Array!.Length && rect.Width > 0 && rect.Height > 0)
            {
                Drawn drawn = Recorded.GetValue(segment.Array, array => new Drawn(Load(array)));
                canvas.DrawPicture(drawn.At(rect.Width, rect.Height), System.Numerics.Matrix3x2.CreateTranslation(rect.X, rect.Y));
                return;
            }

            Load(bytes.ToArray()).Draw(canvas, rect);
        }

        /// <summary>A parsed SVG with its drawing at the last size asked for.</summary>
        private sealed class Drawn(SvgDocument document)
        {
            private (float Width, float Height, Egami.Drawing.Picture Picture)? last;

            public Egami.Drawing.Picture At(float width, float height)
            {
                lock (this)
                {
                    if (last is not { } known || known.Width != width || known.Height != height)
                    {
                        PictureRecorder recorder = new(new RectF(0, 0, width, height));
                        document.Draw(recorder, new RectF(0, 0, width, height));
                        known = (width, height, recorder.Finish());
                        last = known;
                    }

                    return known.Picture;
                }
            }
        }

        private static SvgDocument Load(byte[] bytes)
        {
            using MemoryStream stream = new(bytes, writable: false);
            return SvgDocument.Load(stream);
        }
    }
}
