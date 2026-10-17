using Egami.Pixels;

namespace Egap.Pdf.Layout;

/// <summary>Text and fill colours as the document model takes them: device and calibrated spaces by their components, other spaces as black.</summary>
internal static class ModelColors
{
    /// <summary>The colour in 8-bit sRGB, near enough for text; null for black, which the model leaves as the automatic colour.</summary>
    public static Rgba8? Rgb(PdfColor color)
    {
        IReadOnlyList<double> c = color.Components;
        (double r, double g, double b) = color.Pattern is not null ? (0, 0, 0) : (color.Family, c.Count) switch
        {
            ("DeviceGray" or "CalGray" or "ICCBased", 1) => (c[0], c[0], c[0]),
            ("DeviceRGB" or "CalRGB" or "ICCBased", 3) => (c[0], c[1], c[2]),
            ("DeviceCMYK" or "ICCBased", 4) => ((1 - c[0]) * (1 - c[3]), (1 - c[1]) * (1 - c[3]), (1 - c[2]) * (1 - c[3])),
            _ => (0, 0, 0),
        };

        Rgba8 rgb = new(Byte(r), Byte(g), Byte(b), 255);
        return rgb.R < 8 && rgb.G < 8 && rgb.B < 8 ? null : rgb;
    }

    private static byte Byte(double value)
    {
        return (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }
}
