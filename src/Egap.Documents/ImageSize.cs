using Egami;
using Egami.Formats;

namespace Egap.Documents;

/// <summary>Picture sizes from an image's header, shared by the readers that place pictures without a stated size.</summary>
internal static class ImageSize
{
    /// <summary>Points per CSS pixel: 96 pixels an inch.</summary>
    public const double PointsPerPixel = 0.75;

    /// <summary>A picture's size in points at 96 pixels an inch, from its header; 100 by 100 pixels when Egami cannot tell.</summary>
    public static (double Width, double Height) Intrinsic(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            RasterInfo info = Raster.Probe(bytes);
            return (info.Width * PointsPerPixel, info.Height * PointsPerPixel);
        }
        catch (Exception e) when (e is EgamiException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return (100 * PointsPerPixel, 100 * PointsPerPixel);
        }
    }
}
