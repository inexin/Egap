using System.Runtime.CompilerServices;

using Egami.Colors;

namespace Egap.Pdf.Images;

/// <summary>
/// How colour spaces turn into sRGB (Phase 4 design Q7): the profiles device spaces stand for (an output intent's, or a
/// CMYK profile the caller gives), the rendering intent with black point compensation for relative colorimetric as Acrobat
/// does, and the ICC profiles and transforms read so far, shared by every colour space made with this context.
/// </summary>
internal sealed class ColorContext
{
    private readonly Shared shared;

    public ColorContext()
    {
        shared = new Shared();
    }

    private ColorContext(ColorContext other, RenderingIntent intent)
    {
        shared = other.shared;
        Gray = other.Gray;
        Rgb = other.Rgb;
        Cmyk = other.Cmyk;
        Intent = intent;
    }

    /// <summary>The profile DeviceGray stands for; null for gray as it is.</summary>
    public ColorProfile? Gray { get; init; }

    /// <summary>The profile DeviceRGB stands for; null for sRGB.</summary>
    public ColorProfile? Rgb { get; init; }

    /// <summary>The profile DeviceCMYK stands for; null for the fitted coated press conversion (Phase 3 design Q12).</summary>
    public ColorProfile? Cmyk { get; init; }

    public RenderingIntent Intent { get; private init; } = RenderingIntent.RelativeColorimetric;

    /// <summary>The same profiles and caches with another rendering intent.</summary>
    public ColorContext WithIntent(RenderingIntent intent)
    {
        return intent == Intent ? this : new ColorContext(this, intent);
    }

    /// <summary>The rendering intent a PDF name gives (8.6.5.8); relative colorimetric for an unknown one, as the spec says.</summary>
    public static RenderingIntent IntentOf(PdfName? name)
    {
        return name?.Value switch
        {
            "Perceptual" => RenderingIntent.Perceptual,
            "Saturation" => RenderingIntent.Saturation,
            "AbsoluteColorimetric" => RenderingIntent.AbsoluteColorimetric,
            _ => RenderingIntent.RelativeColorimetric,
        };
    }

    /// <summary>An ICCBased stream's profile, read once; null when Egami cannot read it.</summary>
    public ColorProfile? Profile(PdfStream stream)
    {
        return shared.Profiles.GetValue(stream, s =>
        {
            try
            {
                return ColorProfile.FromIcc(s.GetDecodedBytes());
            }
            catch (Exception e) when (e is Egami.EgamiException or ArgumentException or InvalidDataException or PdfException)
            {
                return null;
            }
        });
    }

    /// <summary>The transform from a profile to sRGB at this context's intent, made once per profile and intent.</summary>
    public ColorTransform Transform(ColorProfile profile)
    {
        lock (shared.Transforms)
        {
            if (!shared.Transforms.TryGetValue((profile, Intent), out ColorTransform? transform))
            {
                transform = ColorTransform.Create(profile, ColorProfile.Srgb, new ColorTransformOptions
                {
                    Intent = Intent,
                    BlackPointCompensation = Intent == RenderingIntent.RelativeColorimetric,
                });
                shared.Transforms[(profile, Intent)] = transform;
            }

            return transform;
        }
    }

    /// <summary>
    /// The context a document's output intent gives (14.11.5): its destination profile stands for the device space with as
    /// many components. Without one, or when <paramref name="useOutputIntent"/> is false, device spaces are as they are.
    /// </summary>
    public static ColorContext ForDocument(PdfDocument document, bool useOutputIntent)
    {
        if (!useOutputIntent)
        {
            return new ColorContext();
        }

        ColorContext probe = new();
        foreach (PdfObject item in document.Catalog.Get<PdfArray>("OutputIntents") ?? [])
        {
            if (item.Resolve() is PdfDictionary intent && intent.Get("DestOutputProfile")?.Resolve() is PdfStream stream && probe.Profile(stream) is ColorProfile profile)
            {
                return (stream.Dictionary.GetInt("N") ?? Components(profile)) switch
                {
                    1 => new ColorContext { Gray = profile },
                    4 => new ColorContext { Cmyk = profile },
                    _ => new ColorContext { Rgb = profile },
                };
            }
        }

        return new ColorContext();
    }

    private static int Components(ColorProfile profile)
    {
        return profile.Model switch
        {
            ColorModel.Gray => 1,
            ColorModel.Cmyk => 4,
            _ => 3,
        };
    }

    private sealed class Shared
    {
        public ConditionalWeakTable<PdfStream, ColorProfile?> Profiles { get; } = [];

        public Dictionary<(ColorProfile, RenderingIntent), ColorTransform> Transforms { get; } = [];
    }
}
