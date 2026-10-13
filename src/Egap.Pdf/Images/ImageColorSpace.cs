using Egami.Colors;

namespace Egap.Pdf.Images;

/// <summary>The family of a colour space, for choosing how a decoded image keeps or converts its colour.</summary>
internal enum ColorFamily
{
    Gray,
    Rgb,
    Cmyk,
    Indexed,
    Lab,
    Separation,
}

/// <summary>
/// An image's colour space (8.6) turned into sRGB: device spaces as they are (CMYK through a press profile when one is given,
/// else the simple formula, design Q12), CIE-based spaces through their device counterparts or, for ICCBased, the alternate
/// the profile's component count implies, Lab through XYZ, and Indexed, Separation and DeviceN through their base or
/// alternate spaces and tint transforms. Components arrive decoded, each in its space's own range.
/// </summary>
internal abstract class ImageColorSpace
{
    /// <summary>How deep spaces may nest (Indexed over DeviceN over ICCBased ...) before reading stops.</summary>
    private const int MaxDepth = 8;

    public abstract int Components { get; }

    public abstract ColorFamily Family { get; }

    /// <summary>The ICC profile, for an ICCBased space whose profile Egami reads.</summary>
    public virtual ColorProfile? Profile => null;

    /// <summary>The default <c>/Decode</c> array for a sample depth (8.9.5.2).</summary>
    public virtual double[] DefaultDecode(int bits)
    {
        return [.. Enumerable.Range(0, Components).SelectMany(_ => new double[] { 0, 1 })];
    }

    /// <summary>Converts one colour to sRGB, each channel 0 to 1.</summary>
    public abstract void ToRgb(ReadOnlySpan<double> components, Span<double> rgb);

    /// <summary>Reads a colour space from its name or array, looking names up in <paramref name="resources"/>.</summary>
    public static ImageColorSpace Read(PdfObject? value, PdfDictionary? resources, ColorProfile? cmykProfile)
    {
        return Read(value, resources, cmykProfile, 0);
    }

    private static ImageColorSpace Read(PdfObject? value, PdfDictionary? resources, ColorProfile? cmykProfile, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("Colour spaces nest too deep.");
        }

        PdfObject? resolved = value?.Resolve();
        if (resolved is PdfName name)
        {
            switch (name.Value)
            {
                case "DeviceGray" or "G" or "CalGray":
                    return new GraySpace();
                case "DeviceRGB" or "RGB" or "CalRGB":
                    return new RgbSpace();
                case "DeviceCMYK" or "CMYK":
                    return new CmykSpace(cmykProfile);
                case "Pattern":
                    throw new InvalidDataException("An image cannot use the Pattern colour space.");
            }

            // A named space from the resources (inline images and some producers' XObjects).
            PdfObject? named = resources?.Get<PdfDictionary>("ColorSpace")?.Get(name.Value);
            return named is not null ? Read(named, resources, cmykProfile, depth + 1) : throw new InvalidDataException($"The colour space {name.Value} is not known.");
        }

        if (resolved is not PdfArray { Count: > 0 } array || array.GetName(0)?.Value is not string family)
        {
            throw new InvalidDataException("An image has no colour space.");
        }

        switch (family)
        {
            case "DeviceGray" or "G" or "CalGray":
                return new GraySpace();
            case "DeviceRGB" or "RGB" or "CalRGB":
                return new RgbSpace();
            case "DeviceCMYK" or "CMYK":
                return new CmykSpace(cmykProfile);
            case "Lab":
                return new LabSpace(array.Count > 1 ? array.Get<PdfDictionary>(1) : null);
            case "ICCBased":
                return IccSpace(array.Count > 1 ? array.Get(1).Resolve() as PdfStream : null, resources, cmykProfile, depth);
            case "Indexed" or "I":
                if (array.Count < 4)
                {
                    throw new InvalidDataException("An Indexed colour space has too few entries.");
                }

                ImageColorSpace baseSpace = Read(array.Get(1), resources, cmykProfile, depth + 1);
                int high = (int)Math.Clamp(array.GetInt(2) ?? 0, 0, 255);
                byte[] lookup = array.Get(3).Resolve() switch
                {
                    PdfString s => s.Bytes.ToArray(),
                    PdfStream st => st.GetDecodedBytes(),
                    _ => [],
                };
                return new IndexedSpace(baseSpace, high, lookup);
            case "Separation":
                if (array.Count < 4)
                {
                    throw new InvalidDataException("A Separation colour space has too few entries.");
                }

                return new TintSpace(1, array.GetName(1)?.Value is "None", Read(array.Get(2), resources, cmykProfile, depth + 1), PdfFunction.Read(array.Get(3)));
            case "DeviceN":
                if (array.Count < 4 || array.Get(1).Resolve() is not PdfArray names)
                {
                    throw new InvalidDataException("A DeviceN colour space has too few entries.");
                }

                return new TintSpace(names.Count, names.All(n => (n.Resolve() as PdfName)?.Value == "None"), Read(array.Get(2), resources, cmykProfile, depth + 1), PdfFunction.Read(array.Get(3)));
            case "Pattern":
                throw new InvalidDataException("An image cannot use the Pattern colour space.");
            default:
                throw new InvalidDataException($"The colour space {family} is not known.");
        }
    }

    /// <summary>An ICCBased space: through its profile when Egami reads it and it has 1, 3 or 4 components, else the alternate.</summary>
    private static ImageColorSpace IccSpace(PdfStream? stream, PdfDictionary? resources, ColorProfile? cmykProfile, int depth)
    {
        int n = (int)(stream?.Dictionary.GetInt("N") ?? 3);
        ColorProfile? profile = null;
        try
        {
            profile = stream is not null && n is 1 or 3 or 4 ? ColorProfile.FromIcc(stream.GetDecodedBytes()) : null;
        }
        catch (Exception e) when (e is Egami.EgamiException or ArgumentException or InvalidDataException or PdfException)
        {
            profile = null;
        }

        ImageColorSpace alternate = stream?.Dictionary.Get("Alternate") is PdfObject alt
            ? Read(alt, resources, cmykProfile, depth + 1)
            : n switch
            {
                1 => new GraySpace(),
                4 => new CmykSpace(cmykProfile),
                _ => new RgbSpace(),
            };
        return alternate.Components == n ? new IccBasedSpace(alternate, profile) : alternate;
    }

    private sealed class GraySpace : ImageColorSpace
    {
        public override int Components => 1;

        public override ColorFamily Family => ColorFamily.Gray;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            rgb[0] = rgb[1] = rgb[2] = c[0];
        }
    }

    private sealed class RgbSpace : ImageColorSpace
    {
        public override int Components => 3;

        public override ColorFamily Family => ColorFamily.Rgb;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            rgb[0] = c[0];
            rgb[1] = c[1];
            rgb[2] = c[2];
        }
    }

    /// <summary>DeviceCMYK: through the given press profile, one colour at a time; without one, a fit of a coated press profile (<see cref="CmykPress"/>).</summary>
    private sealed class CmykSpace(ColorProfile? profile) : ImageColorSpace
    {
        private readonly ColorTransform? transform = profile is null ? null : ColorTransform.Create(profile, ColorProfile.Srgb);

        public override int Components => 4;

        public override ColorFamily Family => ColorFamily.Cmyk;

        public override ColorProfile? Profile => profile;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            if (transform is not null)
            {
                Span<Egami.Pixels.Cmyk16> from = [new Egami.Pixels.Cmyk16(Unit16(c[0]), Unit16(c[1]), Unit16(c[2]), Unit16(c[3]))];
                Span<Egami.Pixels.Rgb16> to = stackalloc Egami.Pixels.Rgb16[1];
                transform.Apply<Egami.Pixels.Cmyk16, Egami.Pixels.Rgb16>(from, to);
                rgb[0] = to[0].R / 65535.0;
                rgb[1] = to[0].G / 65535.0;
                rgb[2] = to[0].B / 65535.0;
                return;
            }

            CmykPress.ToRgb(Math.Clamp(c[0], 0, 1), Math.Clamp(c[1], 0, 1), Math.Clamp(c[2], 0, 1), Math.Clamp(c[3], 0, 1), rgb);
        }
    }

    /// <summary>ICCBased: colour through the profile into sRGB; the alternate says the family and converts when there is no profile.</summary>
    private sealed class IccBasedSpace(ImageColorSpace alternate, ColorProfile? profile) : ImageColorSpace
    {
        private readonly ColorTransform? transform = profile is null ? null : ColorTransform.Create(profile, ColorProfile.Srgb);

        public override int Components => alternate.Components;

        public override ColorFamily Family => alternate.Family;

        public override ColorProfile? Profile => profile;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            if (transform is null)
            {
                alternate.ToRgb(c, rgb);
                return;
            }

            Span<Egami.Pixels.Rgb16> to = stackalloc Egami.Pixels.Rgb16[1];
            switch (Components)
            {
                case 1:
                    transform.Apply<Egami.Pixels.Gray16, Egami.Pixels.Rgb16>([new Egami.Pixels.Gray16(Unit16(c[0]))], to);
                    break;
                case 3:
                    transform.Apply<Egami.Pixels.Rgb16, Egami.Pixels.Rgb16>([new Egami.Pixels.Rgb16(Unit16(c[0]), Unit16(c[1]), Unit16(c[2]))], to);
                    break;
                default:
                    transform.Apply<Egami.Pixels.Cmyk16, Egami.Pixels.Rgb16>([new Egami.Pixels.Cmyk16(Unit16(c[0]), Unit16(c[1]), Unit16(c[2]), Unit16(c[3]))], to);
                    break;
            }

            rgb[0] = to[0].R / 65535.0;
            rgb[1] = to[0].G / 65535.0;
            rgb[2] = to[0].B / 65535.0;
        }
    }

    /// <summary>CIE L*a*b* (8.6.5.4) to sRGB, through XYZ under its white point adapted to D65 (Bradford).</summary>
    private sealed class LabSpace : ImageColorSpace
    {
        private readonly double[] white;
        private readonly double[] range;

        public LabSpace(PdfDictionary? parameters)
        {
            double[]? w = parameters?.Get<PdfArray>("WhitePoint") is { Count: 3 } wp ? [wp.GetNumber(0) ?? 0.9505, wp.GetNumber(1) ?? 1, wp.GetNumber(2) ?? 1.089] : null;
            white = w is not null && w[1] > 0 ? w : [0.9505, 1, 1.089];
            range = parameters?.Get<PdfArray>("Range") is { Count: 4 } r ? [r.GetNumber(0) ?? -100, r.GetNumber(1) ?? 100, r.GetNumber(2) ?? -100, r.GetNumber(3) ?? 100] : [-100, 100, -100, 100];
        }

        public override int Components => 3;

        public override ColorFamily Family => ColorFamily.Lab;

        public override double[] DefaultDecode(int bits)
        {
            return [0, 100, range[0], range[1], range[2], range[3]];
        }

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            double l = Math.Clamp(c[0], 0, 100), a = Math.Clamp(c[1], range[0], range[1]), b = Math.Clamp(c[2], range[2], range[3]);
            double m = (l + 16) / 116, ll = m + (a / 500), n = m - (b / 200);
            double x = white[0] * G(ll), y = white[1] * G(m), z = white[2] * G(n);

            // Bradford from the white point to D65, then XYZ to linear sRGB.
            (double xa, double ya, double za) = Adapt(x, y, z, white);
            double r = (3.2404542 * xa) - (1.5371385 * ya) - (0.4985314 * za);
            double g = (-0.9692660 * xa) + (1.8760108 * ya) + (0.0415560 * za);
            double bl = (0.0556434 * xa) - (0.2040259 * ya) + (1.0572252 * za);
            rgb[0] = Gamma(r);
            rgb[1] = Gamma(g);
            rgb[2] = Gamma(bl);
        }

        private static double G(double x)
        {
            return x >= 6.0 / 29 ? x * x * x : 108.0 / 841 * (x - (4.0 / 29));
        }

        private static (double X, double Y, double Z) Adapt(double x, double y, double z, double[] w)
        {
            static (double, double, double) Cone(double x, double y, double z) =>
                ((0.8951 * x) + (0.2664 * y) - (0.1614 * z), (-0.7502 * x) + (1.7135 * y) + (0.0367 * z), (0.0389 * x) - (0.0685 * y) + (1.0296 * z));

            (double sr, double sg, double sb) = Cone(w[0], w[1], w[2]);
            (double dr, double dg, double db) = Cone(0.95047, 1, 1.08883);
            (double r, double g, double b) = Cone(x, y, z);
            r *= dr / sr;
            g *= dg / sg;
            b *= db / sb;
            return (
                (0.9869929 * r) - (0.1470543 * g) + (0.1599627 * b),
                (0.4323053 * r) + (0.5183603 * g) + (0.0492912 * b),
                (-0.0085287 * r) + (0.0400428 * g) + (0.9684867 * b));
        }

        private static double Gamma(double v)
        {
            v = Math.Clamp(v, 0, 1);
            return v <= 0.0031308 ? 12.92 * v : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;
        }
    }

    /// <summary>Indexed: a sample is an index into a table of base-space colours.</summary>
    private sealed class IndexedSpace(ImageColorSpace baseSpace, int high, byte[] lookup) : ImageColorSpace
    {
        public override int Components => 1;

        public override ColorFamily Family => ColorFamily.Indexed;

        public ImageColorSpace Base => baseSpace;

        public override double[] DefaultDecode(int bits)
        {
            return [0, (1 << bits) - 1];
        }

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            int index = (int)Math.Clamp(Math.Round(c[0]), 0, high);
            int n = baseSpace.Components;
            Span<double> components = stackalloc double[n];
            double[] decode = baseSpace.DefaultDecode(8);
            for (int i = 0; i < n; i++)
            {
                int at = (index * n) + i;
                double v = at < lookup.Length ? lookup[at] / 255.0 : 0;
                components[i] = decode[2 * i] + (v * (decode[(2 * i) + 1] - decode[2 * i]));
            }

            baseSpace.ToRgb(components, rgb);
        }
    }

    /// <summary>Separation and DeviceN: tints through the tint transform into the alternate space; the None colorant paints nothing.</summary>
    private sealed class TintSpace(int components, bool none, ImageColorSpace alternate, PdfFunction transform) : ImageColorSpace
    {
        public override int Components => components;

        public override ColorFamily Family => ColorFamily.Separation;

        public bool IsNone => none;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            Span<double> alt = stackalloc double[Math.Max(alternate.Components, Math.Max(transform.Outputs, 1))];
            alt.Clear();
            transform.Evaluate(c[..Math.Min(c.Length, components)], alt);
            alternate.ToRgb(alt[..alternate.Components], rgb);
        }
    }

    private static ushort Unit16(double v)
    {
        return (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
    }
}
