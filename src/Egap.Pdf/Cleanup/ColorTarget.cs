using Egami;
using Egami.Colors;
using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Cleanup;

/// <summary>
/// What a <see cref="ColorRewriter"/> converts colours into: a device space of one, three or four components, which source
/// spaces it converts, how one colour converts, how a whole image converts, and what happens to spot colours and black.
/// </summary>
internal abstract class ColorTarget
{
    /// <summary>The target's component count: 1, 3 or 4.</summary>
    public abstract int Components { get; }

    /// <summary>The device space colours are written in.</summary>
    public PdfName Space => PdfName.Get(Components switch { 1 => "DeviceGray", 3 => "DeviceRGB", _ => "DeviceCMYK" });

    /// <summary>The abbreviation inline images use for <see cref="Space"/>.</summary>
    public PdfName InlineSpace => PdfName.Get(Components switch { 1 => "G", 3 => "RGB", _ => "CMYK" });

    /// <summary>The operator that sets a fill (or with <paramref name="fill"/> false, a stroke) colour in <see cref="Space"/>.</summary>
    public string Operator(bool fill) => Components switch { 1 => fill ? "g" : "G", 3 => fill ? "rg" : "RG", _ => fill ? "k" : "K" };

    /// <summary>Whether colour in the space is rewritten; a space that is not keeps its colours as they are.</summary>
    public abstract bool Converts(ImageColorSpace space);

    /// <summary>Whether a Separation or DeviceN space that is kept has its alternate rewritten into the target.</summary>
    public virtual bool RetargetsAlternates => false;

    /// <summary>Converts one colour in the space into the target's components, each 0 to 1.</summary>
    public abstract void Convert(ImageColorSpace space, ReadOnlySpan<double> components, Span<double> target);

    /// <summary>
    /// Converts an image's pixels into the target: its samples, <see cref="Components"/> per pixel, at 8 bits; null when it
    /// cannot be read.
    /// </summary>
    public abstract byte[]? ConvertImage(PdfImage image, ImageColorSpace space, PdfImageOptions options, out int width, out int height);

    /// <summary>
    /// For black and gray text and thin rules: the target colour a neutral colour prints as on black ink alone, or null to
    /// convert it like any other colour (design Q7).
    /// </summary>
    public virtual double[]? PureBlack(ImageColorSpace space, ReadOnlySpan<double> components) => null;

    /// <summary>The widest stroke, in points, that counts as a thin rule for <see cref="PureBlack"/>.</summary>
    public virtual double ThinRule => 0;

    /// <summary>Converts one colour into a new array.</summary>
    public double[] Convert(ImageColorSpace space, ReadOnlySpan<double> components)
    {
        double[] target = new double[Components];
        Span<double> padded = stackalloc double[Math.Max(space.Components, Math.Max(components.Length, 1))];
        padded.Clear();
        components.CopyTo(padded);
        Convert(space, padded[..Math.Max(space.Components, 1)], target);
        return target;
    }
}

/// <summary>Phase 7's grayscale target: every colour becomes the luma of its sRGB colour (Rec. 601 weights).</summary>
internal sealed class GrayTarget : ColorTarget
{
    public static GrayTarget Instance { get; } = new();

    public override int Components => 1;

    public override bool Converts(ImageColorSpace space) => !(space.Family == ColorFamily.Gray && space.Components == 1);

    public override void Convert(ImageColorSpace space, ReadOnlySpan<double> components, Span<double> target)
    {
        target[0] = GrayConverter.Luma(space, components);
    }

    public override byte[]? ConvertImage(PdfImage image, ImageColorSpace space, PdfImageOptions options, out int width, out int height)
    {
        using Raster<Rgba8> raster = image.ToRaster<Rgba8>(options);
        (width, height) = (raster.Width, raster.Height);
        byte[] gray = new byte[raster.Width * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < raster.Width; x++)
            {
                Rgba8 p = row[x];
                gray[(y * raster.Width) + x] = (byte)Math.Round((0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B));
            }
        }

        return gray;
    }
}

/// <summary>
/// An ICC target (Phase 8 design Q6, Q7): RGB, calibrated, Lab and ICC-based colours go through their profiles (sRGB for
/// those without one) into the target profile at the rendering intent. DeviceGray is kept, as it prints on black ink; CMYK
/// is kept unless <see cref="RetargetCmyk"/> converts it from <see cref="SourceCmyk"/>; ICC-based CMYK in the target's own
/// profile keeps its numbers. Spot colours follow <see cref="Spots"/>, and with them kept their alternates are rewritten
/// into the target. Neutral colours of text and thin rules go on black ink alone when <see cref="BlackText"/> is set.
/// </summary>
internal sealed class IccTarget : ColorTarget
{
    private readonly ColorProfile target;
    private readonly ColorTransformOptions transformOptions;
    private readonly Dictionary<ColorProfile, ColorTransform> transforms = new(ReferenceEqualityComparer.Instance);
    private readonly int components;

    public IccTarget(ColorProfile target, RenderingIntent intent, bool blackPointCompensation)
    {
        this.target = target;
        components = target.Model switch
        {
            ColorModel.Gray => 1,
            ColorModel.Cmyk => 4,
            ColorModel.Rgb => 3,
            _ => throw new ArgumentException("Colour conversion targets a gray, RGB or CMYK profile.", nameof(target)),
        };
        transformOptions = new ColorTransformOptions { Intent = intent, BlackPointCompensation = blackPointCompensation };
    }

    public override int Components => components;

    /// <summary>How spot colours are treated.</summary>
    public PdfSpotConversion Spots { get; init; }

    /// <summary>The spot inks kept under <see cref="PdfSpotConversion.ConvertAllButNamed"/>.</summary>
    public IReadOnlySet<string> KeptSpots { get; init; } = new HashSet<string>();

    /// <summary>Whether DeviceCMYK and ICC-based CMYK in another profile are converted from <see cref="SourceCmyk"/> into the target.</summary>
    public bool RetargetCmyk { get; init; }

    /// <summary>The profile DeviceCMYK is in when it is converted.</summary>
    public ColorProfile SourceCmyk { get; init; } = ColorProfile.Fogra39;

    /// <summary>How black and gray text and thin rules convert.</summary>
    public PdfBlackText BlackText { get; init; }

    /// <summary>The widest rule, in points, that black handling treats as thin.</summary>
    public double ThinRuleWidth { get; init; } = 0.5;

    public override double ThinRule => ThinRuleWidth;

    public override bool RetargetsAlternates => true;

    public override bool Converts(ImageColorSpace space)
    {
        return space.Family switch
        {
            ColorFamily.Gray => !space.IsDevice && components != 1,
            ColorFamily.Rgb or ColorFamily.Lab => components != 3 || !space.IsDevice,
            ColorFamily.Cmyk when components == 4 => RetargetCmyk || (!space.IsDevice && space.Profile is ColorProfile own && !own.IsEquivalentTo(target)) || (!space.IsDevice && space.Profile is null),
            ColorFamily.Cmyk => true,
            ColorFamily.Indexed => space is ImageColorSpace.IndexedSpace indexed && Converts(indexed.Base),
            ColorFamily.Separation => ConvertsSpot(space),
            _ => true,
        };
    }

    private bool ConvertsSpot(ImageColorSpace space)
    {
        IReadOnlyList<string> names = space.Colorants ?? [];

        // A space of process colorants only, or All, is CMYK by another name: it follows CMYK.
        bool process = names.All(n => n is "Cyan" or "Magenta" or "Yellow" or "Black" or "All" or "None");
        if (process && components == 4)
        {
            return RetargetCmyk;
        }

        return Spots switch
        {
            PdfSpotConversion.Convert => true,
            PdfSpotConversion.ConvertAllButNamed => !names.All(n => n is "None" or "All" || KeptSpots.Contains(n)),
            _ => false,
        };
    }

    public override void Convert(ImageColorSpace space, ReadOnlySpan<double> c, Span<double> output)
    {
        switch (space.Family)
        {
            case ColorFamily.Indexed when space is ImageColorSpace.IndexedSpace indexed:
                Span<double> baseColor = stackalloc double[indexed.Base.Components];
                indexed.BaseColor(c[0], baseColor);
                Convert(indexed.Base, baseColor, output);
                return;
            case ColorFamily.Separation when space.Alternate is ImageColorSpace alternate:
                Span<double> alt = stackalloc double[alternate.Components];
                space.ToAlternate(c, alt);
                Convert(alternate, alt, output);
                return;
            case ColorFamily.Cmyk when space.IsDevice || space.Profile is null:
                Through(SourceCmyk, c, output);
                return;
            case ColorFamily.Gray or ColorFamily.Rgb or ColorFamily.Cmyk when space.Profile is ColorProfile profile:
                Through(profile, c, output);
                return;
            case ColorFamily.Rgb when space.IsDevice:
                Through(ColorProfile.Srgb, c, output);
                return;
            default:
                // CalGray, CalRGB and Lab have no Egami profile: through their sRGB colour.
                Span<double> rgb = stackalloc double[3];
                space.ToRgb(c, rgb);
                Through(ColorProfile.Srgb, rgb, output);
                return;
        }
    }

    /// <summary>One colour through a profile into the target.</summary>
    private void Through(ColorProfile source, ReadOnlySpan<double> c, Span<double> output)
    {
        ColorTransform transform = Transform(source);
        Span<float> from = stackalloc float[Math.Max(transform.InputChannels, 1)];
        Span<float> to = stackalloc float[Math.Max(transform.OutputChannels, 1)];
        for (int i = 0; i < from.Length; i++)
        {
            from[i] = i < c.Length && double.IsFinite(c[i]) ? (float)Math.Clamp(c[i], 0, 1) : 0;
        }

        transform.Apply(from, to);
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = i < to.Length ? Math.Clamp(to[i], 0, 1) : 0;
        }
    }

    private ColorTransform Transform(ColorProfile source)
    {
        lock (transforms)
        {
            if (!transforms.TryGetValue(source, out ColorTransform? transform))
            {
                transform = ColorTransform.Create(source, target, transformOptions);
                transforms[source] = transform;
            }

            return transform;
        }
    }

    public override byte[]? ConvertImage(PdfImage image, ImageColorSpace space, PdfImageOptions options, out int width, out int height)
    {
        // Gray, RGB and CMYK with a profile convert from their own samples through it; everything else from its sRGB pixels.
        ColorProfile? source = space.Family switch
        {
            ColorFamily.Cmyk when space.IsDevice || space.Profile is null => SourceCmyk,
            ColorFamily.Gray or ColorFamily.Rgb or ColorFamily.Cmyk when space.Profile is ColorProfile profile => profile,
            ColorFamily.Rgb when space.IsDevice => ColorProfile.Srgb,
            _ => null,
        };
        Raster raster = source is not null && !options.ApplyMasks ? image.ToRaster(options with { ConvertColor = false }) : image.ToRaster<Rgba8>(options);
        using (raster)
        {
            (width, height) = (raster.Width, raster.Height);
            ColorTransform transform = Transform(raster is Raster<Rgba8> ? ColorProfile.Srgb : source!);
            int n = components;
            byte[] output = new byte[raster.Width * raster.Height * n];
            float[] from = new float[raster.Width * transform.InputChannels], to = new float[raster.Width * transform.OutputChannels];
            for (int y = 0; y < raster.Height; y++)
            {
                Row(raster, y, from, transform.InputChannels);
                transform.Apply(from, to);
                int at = y * raster.Width * n;
                for (int i = 0; i < raster.Width * n; i++)
                {
                    output[at + i] = (byte)Math.Round(Math.Clamp(to[i], 0, 1) * 255);
                }
            }

            return output;
        }
    }

    /// <summary>A raster row as components 0 to 1, alpha left out.</summary>
    private static void Row(Raster raster, int y, float[] row, int channels)
    {
        const float Byte = 1 / 255f, Word = 1 / 65535f;
        int at = 0;
        switch (raster)
        {
            case Raster<Rgba8> rgba:
                foreach (Rgba8 p in rgba.Row(y))
                {
                    row[at++] = p.R * Byte;
                    row[at++] = p.G * Byte;
                    row[at++] = p.B * Byte;
                }

                break;
            case Raster<Rgb8> rgb:
                foreach (Rgb8 p in rgb.Row(y))
                {
                    row[at++] = p.R * Byte;
                    row[at++] = p.G * Byte;
                    row[at++] = p.B * Byte;
                }

                break;
            case Raster<Gray8> gray:
                foreach (Gray8 p in gray.Row(y))
                {
                    row[at++] = p.L * Byte;
                }

                break;
            case Raster<Cmyk8> cmyk:
                foreach (Cmyk8 p in cmyk.Row(y))
                {
                    row[at++] = p.C * Byte;
                    row[at++] = p.M * Byte;
                    row[at++] = p.Y * Byte;
                    row[at++] = p.K * Byte;
                }

                break;
            case Raster<Rgb16> rgb16:
                foreach (Rgb16 p in rgb16.Row(y))
                {
                    row[at++] = p.R * Word;
                    row[at++] = p.G * Word;
                    row[at++] = p.B * Word;
                }

                break;
            case Raster<Gray16> gray16:
                foreach (Gray16 p in gray16.Row(y))
                {
                    row[at++] = p.L * Word;
                }

                break;
            case Raster<Cmyk16> cmyk16:
                foreach (Cmyk16 p in cmyk16.Row(y))
                {
                    row[at++] = p.C * Word;
                    row[at++] = p.M * Word;
                    row[at++] = p.Y * Word;
                    row[at++] = p.K * Word;
                }

                break;
            default:
                throw new InvalidDataException($"An image decoded to {raster.GetType().Name}, which conversion does not read.");
        }

        if (at != raster.Width * channels)
        {
            throw new InvalidDataException("An image's samples do not match its profile's channels.");
        }
    }

    public override double[]? PureBlack(ImageColorSpace space, ReadOnlySpan<double> c)
    {
        if (BlackText != PdfBlackText.PureBlack || components != 4)
        {
            return null;
        }

        double? gray = space.Family switch
        {
            ColorFamily.Gray => c[0],
            ColorFamily.Rgb when Math.Abs(c[0] - c[1]) < 0.004 && Math.Abs(c[1] - c[2]) < 0.004 => (c[0] + c[1] + c[2]) / 3,
            ColorFamily.Lab when Math.Abs(c[1]) < 0.5 && Math.Abs(c[2]) < 0.5 => c[0] / 100,
            ColorFamily.Cmyk when c[0] < 0.004 && c[1] < 0.004 && c[2] < 0.004 => 1 - c[3],
            _ => null,
        };
        return gray is double g ? [0, 0, 0, Math.Clamp(1 - g, 0, 1)] : null;
    }
}
