using System.Runtime.CompilerServices;

using Egami.Colors;
using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Colour spaces read once per document and colours converted to sRGB (design Q7). A space is keyed by its object, the
/// resources it was looked up in (a device space may be replaced by the resources' Default space) and the rendering intent,
/// so content that sets the same space again reuses it, profiles and transforms included.
/// </summary>
internal sealed class ColorCache(ColorContext context)
{
    private readonly ConditionalWeakTable<PdfObject, Dictionary<(PdfDictionary? Resources, RenderingIntent Intent), ImageColorSpace?>> spaces = [];
    private static readonly PdfDictionary NoResources = [];

    /// <summary>The context colour spaces are read in: the document's output intent and the shared profile cache.</summary>
    public ColorContext Context => context;

    /// <summary>
    /// The colour in sRGB with alpha 1; transparent for a colourant that marks nothing (None), null when its space cannot be
    /// read.
    /// </summary>
    public RgbaF32? ToRgb(PdfColor color, PdfDictionary? resources, RenderingIntent intent)
    {
        if (Space(color.Space, resources, intent) is not ImageColorSpace space)
        {
            return null;
        }

        if (space.PaintsNothing)
        {
            return default(RgbaF32);
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

    /// <summary>The colour space, read once for these resources and intent; null when it cannot be read.</summary>
    public ImageColorSpace? Space(PdfObject space, PdfDictionary? resources, RenderingIntent intent)
    {
        // Only a device space depends on the resources, through their Default spaces.
        PdfDictionary? key = space is PdfName ? resources : null;
        Dictionary<(PdfDictionary?, RenderingIntent), ImageColorSpace?> byUse = spaces.GetValue(space, _ => new(KeyComparer.Instance));
        lock (byUse)
        {
            if (!byUse.TryGetValue((key ?? NoResources, intent), out ImageColorSpace? found))
            {
                found = Read(space, resources, context.WithIntent(intent));
                byUse[(key ?? NoResources, intent)] = found;
            }

            return found;
        }
    }

    private static ImageColorSpace? Read(PdfObject space, PdfDictionary? resources, ColorContext context)
    {
        try
        {
            return ImageColorSpace.Read(space, resources, context);
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

    /// <summary>Resources by identity, with the intent.</summary>
    private sealed class KeyComparer : IEqualityComparer<(PdfDictionary?, RenderingIntent)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((PdfDictionary?, RenderingIntent) x, (PdfDictionary?, RenderingIntent) y)
        {
            return ReferenceEquals(x.Item1, y.Item1) && x.Item2 == y.Item2;
        }

        public int GetHashCode((PdfDictionary?, RenderingIntent) obj)
        {
            return HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Item1), obj.Item2);
        }
    }
}
