using System.Runtime.CompilerServices;

using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Colour spaces read once per document and colours converted to sRGB (design Q7). A space is keyed by its object (the
/// array, or the name with the resources it was looked up in), so content that sets the same space again reuses it.
/// </summary>
internal sealed class ColorCache
{
    private readonly ConditionalWeakTable<PdfObject, ImageColorSpace?> spaces = [];
    private readonly Dictionary<string, ImageColorSpace> device = [];

    /// <summary>The colour in sRGB with alpha 1, or null when its space cannot be read.</summary>
    public RgbaF32? ToRgb(PdfColor color, PdfDictionary? resources)
    {
        if (Space(color.Space, resources) is not ImageColorSpace space)
        {
            return null;
        }

        Span<double> components = stackalloc double[Math.Max(space.Components, 1)];
        for (int i = 0; i < space.Components; i++)
        {
            components[i] = i < color.Components.Count && double.IsFinite(color.Components[i]) ? color.Components[i] : 0;
        }

        Span<double> rgb = stackalloc double[3];
        try
        {
            space.ToRgb(components, rgb);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or PdfException)
        {
            return null;
        }

        return new RgbaF32(Unit(rgb[0]), Unit(rgb[1]), Unit(rgb[2]), 1);
    }

    private ImageColorSpace? Space(PdfObject space, PdfDictionary? resources)
    {
        if (space is PdfName name)
        {
            // The walker resolves names through the resources, so a name here is a device space or one that was missing.
            lock (device)
            {
                if (!device.TryGetValue(name.Value, out ImageColorSpace? found))
                {
                    found = Read(name, resources);
                    if (found is null)
                    {
                        return null;
                    }

                    device[name.Value] = found;
                }

                return found;
            }
        }

        return spaces.GetValue(space, s => Read(s, resources));
    }

    private static ImageColorSpace? Read(PdfObject space, PdfDictionary? resources)
    {
        try
        {
            return ImageColorSpace.Read(space, resources, cmykProfile: null);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or PdfException)
        {
            return null;
        }
    }

    private static float Unit(double value)
    {
        return double.IsFinite(value) ? (float)Math.Clamp(value, 0, 1) : 0;
    }
}
