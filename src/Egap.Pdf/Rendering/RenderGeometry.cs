using System.Numerics;

using Egami.Drawing;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Where a page lands in a render (design Q3): the chosen box scaled by the user unit and the resolution, turned by the
/// page's /Rotate plus the extra rotation, and cut to the region. The output is the box's size in pixels rounded up, as
/// PDFium and pdf.js size their bitmaps.
/// </summary>
internal readonly record struct RenderGeometry(int Width, int Height, Matrix3x2 PageToDevice, double PageWidth, double PageHeight)
{
    /// <summary>The largest side of an output, in pixels; larger needs tiles through the region.</summary>
    private const double MaxSide = 1 << 20;

    public static RenderGeometry Of(PdfPage page, PdfRenderOptions options)
    {
        PdfRectangle box = BoxOf(page, options.Box);
        int rotation = (page.Rotate + options.Rotation) % 360;
        double unit = page.UserUnit;
        double width = box.Width * unit, height = box.Height * unit;
        bool turned = rotation is 90 or 270;
        double shownWidth = turned ? height : width, shownHeight = turned ? width : height;

        double scale = options.Dpi / 72;
        if (options.FitWidth is not null || options.FitHeight is not null)
        {
            scale = Math.Min(
                options.FitWidth is int w ? w / shownWidth : double.PositiveInfinity,
                options.FitHeight is int h ? h / shownHeight : double.PositiveInfinity);
        }

        int pixelsWide = Pixels(shownWidth * scale, options.FitWidth);
        int pixelsHigh = Pixels(shownHeight * scale, options.FitHeight);

        // User space to an unturned page in pixels, y down, then turned clockwise inside the output.
        double s = scale * unit;
        Matrix3x2 matrix = new((float)s, 0, 0, (float)-s, (float)(-box.Left * s), (float)(box.Top * s));
        double unturnedWidth = width * scale, unturnedHeight = height * scale;
        matrix *= rotation switch
        {
            90 => new Matrix3x2(0, 1, -1, 0, (float)unturnedHeight, 0),
            180 => new Matrix3x2(-1, 0, 0, -1, (float)unturnedWidth, (float)unturnedHeight),
            270 => new Matrix3x2(0, -1, 1, 0, 0, (float)unturnedWidth),
            _ => Matrix3x2.Identity,
        };

        if (options.Region is RectF region)
        {
            Egami.Rect pixels = region.RoundOut();
            if (pixels.Width <= 0 || pixels.Height <= 0)
            {
                throw new ArgumentException("The region is empty.", nameof(options));
            }

            matrix *= Matrix3x2.CreateTranslation(-pixels.X, -pixels.Y);
            pixelsWide = pixels.Width;
            pixelsHigh = pixels.Height;
        }

        return new RenderGeometry(pixelsWide, pixelsHigh, matrix, shownWidth, shownHeight);
    }

    /// <summary>The box a render shows; each is already clipped to the media box by <see cref="PdfPage"/>.</summary>
    public static PdfRectangle BoxOf(PdfPage page, PdfPageBox box)
    {
        return box switch
        {
            PdfPageBox.MediaBox => page.MediaBox,
            PdfPageBox.BleedBox => page.BleedBox,
            PdfPageBox.TrimBox => page.TrimBox,
            PdfPageBox.ArtBox => page.ArtBox,
            _ => page.CropBox,
        };
    }

    private static int Pixels(double size, int? fit)
    {
        // A size a hair over a whole number from rounding stays that number; a fitted side never passes its limit.
        double rounded = Math.Ceiling(size - 1e-6);
        if (fit is int limit)
        {
            rounded = Math.Min(rounded, limit);
        }

        // A page whose boxes or user unit make it miles wide is a limit the file passes, not a bad argument.
        return rounded is >= 1 and <= MaxSide ? (int)rounded : rounded < 1 || double.IsNaN(rounded) ? 1
            : throw new PdfLimitException(nameof(PdfRenderOptions.MaxOutputPixels), $"A render {size:0} pixels across is too large; render it in tiles through the region.");
    }
}
