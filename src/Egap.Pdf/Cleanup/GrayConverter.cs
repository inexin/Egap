using Egap.Pdf.Images;

namespace Egap.Pdf.Cleanup;

/// <summary>
/// Grayscale conversion (Phase 7 design 7.5 and Q14, Build Plan task 17): every colour the document paints becomes
/// DeviceGray, the luma of its sRGB colour (Rec. 601 weights), and vectors stay vectors, through the colour conversion pass
/// with a gray target (<see cref="ColorRewriter"/>, <see cref="GrayTarget"/>).
/// </summary>
internal static class GrayConverter
{
    /// <summary>Converts the whole document; what could not be converted, as warnings.</summary>
    public static IReadOnlyList<PdfWarning> Run(PdfDocument document, CancellationToken cancellationToken = default)
    {
        return ColorRewriter.Run(document, GrayTarget.Instance, cancellationToken).Warnings;
    }

    /// <summary>The gray a colour shows: the luma of its sRGB value, 0 to 1.</summary>
    internal static double Luma(ImageColorSpace space, ReadOnlySpan<double> components)
    {
        // An operator with too few operands is short of components: the missing ones are 0.
        Span<double> padded = stackalloc double[Math.Max(space.Components, components.Length)];
        padded.Clear();
        components.CopyTo(padded);
        Span<double> rgb = stackalloc double[3];
        space.ToRgb(padded, rgb);
        return Math.Clamp((0.299 * rgb[0]) + (0.587 * rgb[1]) + (0.114 * rgb[2]), 0, 1);
    }
}
