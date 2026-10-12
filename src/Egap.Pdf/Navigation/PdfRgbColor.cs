namespace Egap.Pdf;

/// <summary>A colour in DeviceRGB, each component from 0 to 1, as outline items and annotations give one.</summary>
/// <param name="Red">The red component, from 0 to 1.</param>
/// <param name="Green">The green component, from 0 to 1.</param>
/// <param name="Blue">The blue component, from 0 to 1.</param>
public readonly record struct PdfRgbColor(double Red, double Green, double Blue)
{
    /// <summary>Black, the colour of an outline item without one.</summary>
    public static readonly PdfRgbColor Black = new(0, 0, 0);

    /// <summary>The colour as a PDF array <c>[r g b]</c>.</summary>
    public PdfArray ToArray()
    {
        return [new PdfReal(Clamp(Red)), new PdfReal(Clamp(Green)), new PdfReal(Clamp(Blue))];
    }

    /// <summary>A colour from an array of three numbers, each clamped to 0 to 1; null when the array is not one.</summary>
    public static PdfRgbColor? FromArray(PdfArray? array)
    {
        return array is { Count: 3 } && array.GetNumber(0) is double r && array.GetNumber(1) is double g && array.GetNumber(2) is double b
            ? new PdfRgbColor(Clamp(r), Clamp(g), Clamp(b))
            : null;
    }

    private static double Clamp(double value)
    {
        return double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
    }
}
