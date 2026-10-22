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

    /// <summary>
    /// Whether colour in this space marks nothing: a Separation space for the colourant None, or a DeviceN space whose
    /// colourants are all None (8.6.6.4, 8.6.6.5).
    /// </summary>
    public virtual bool PaintsNothing => false;

    /// <summary>The ICC profile, for an ICCBased space whose profile Egami reads.</summary>
    public virtual ColorProfile? Profile => null;

    /// <summary>Whether this is DeviceGray, DeviceRGB or DeviceCMYK, through an output intent's profile or not.</summary>
    public virtual bool IsDevice => false;

    /// <summary>A Separation or DeviceN space's colourant names, in component order; null for other spaces.</summary>
    public virtual IReadOnlyList<string>? Colorants => null;

    /// <summary>A Separation or DeviceN space's alternate space; null for other spaces.</summary>
    public virtual ImageColorSpace? Alternate => null;

    /// <summary>A Separation or DeviceN colour through the tint transform into <see cref="Alternate"/>'s components.</summary>
    public virtual void ToAlternate(ReadOnlySpan<double> components, Span<double> alternate)
    {
        throw new InvalidOperationException("Only Separation and DeviceN spaces have an alternate.");
    }

    /// <summary>The default <c>/Decode</c> array for a sample depth (8.9.5.2).</summary>
    public virtual double[] DefaultDecode(int bits)
    {
        return [.. Enumerable.Range(0, Components).SelectMany(_ => new double[] { 0, 1 })];
    }

    /// <summary>Converts one colour to sRGB, each channel 0 to 1.</summary>
    public abstract void ToRgb(ReadOnlySpan<double> components, Span<double> rgb);

    /// <summary>Reads a colour space from its name or array, looking names up in <paramref name="resources"/>.</summary>
    public static ImageColorSpace Read(PdfObject? value, PdfDictionary? resources, ColorContext? context)
    {
        return Read(value, resources, context, 0);
    }

    private static ImageColorSpace Read(PdfObject? value, PdfDictionary? resources, ColorContext? context, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("Colour spaces nest too deep.");
        }

        PdfObject? resolved = value?.Resolve();
        if (resolved is PdfName name)
        {
            // A device space is the resources' DefaultGray, DefaultRGB or DefaultCMYK when there is one (8.6.5.6).
            string? defaultKey = name.Value switch
            {
                "DeviceGray" or "G" => "DefaultGray",
                "DeviceRGB" or "RGB" => "DefaultRGB",
                "DeviceCMYK" or "CMYK" => "DefaultCMYK",
                _ => null,
            };
            if (defaultKey is not null && depth == 0 && resources?.Get<PdfDictionary>("ColorSpace")?.Get(defaultKey) is PdfObject replacement)
            {
                try
                {
                    ImageColorSpace chosen = Read(replacement, null, context, depth + 1);
                    if (chosen.Components == (defaultKey == "DefaultGray" ? 1 : defaultKey == "DefaultRGB" ? 3 : 4))
                    {
                        return chosen;
                    }
                }
                catch (InvalidDataException)
                {
                    // An unusable default space leaves the device space as it is.
                }
            }

            switch (name.Value)
            {
                case "DeviceGray" or "G" or "CalGray":
                    return Device(1, context);
                case "DeviceRGB" or "RGB" or "CalRGB":
                    return Device(3, context);
                case "DeviceCMYK" or "CMYK":
                    return new CmykSpace(context);
                case "Pattern":
                    throw new InvalidDataException("An image cannot use the Pattern colour space.");
            }

            // A named space from the resources (inline images and some producers' XObjects).
            PdfObject? named = resources?.Get<PdfDictionary>("ColorSpace")?.Get(name.Value);
            return named is not null ? Read(named, resources, context, depth + 1) : throw new InvalidDataException($"The colour space {name.Value} is not known.");
        }

        if (resolved is not PdfArray { Count: > 0 } array || array.GetName(0)?.Value is not string family)
        {
            throw new InvalidDataException("An image has no colour space.");
        }

        switch (family)
        {
            case "CalGray" when array.Count > 1 && array.Get<PdfDictionary>(1) is PdfDictionary gray:
                return new CalSpace(1, gray);
            case "CalRGB" when array.Count > 1 && array.Get<PdfDictionary>(1) is PdfDictionary rgb:
                return new CalSpace(3, rgb);
            case "DeviceGray" or "G" or "CalGray":
                return Device(1, context);
            case "DeviceRGB" or "RGB" or "CalRGB":
                return Device(3, context);
            case "DeviceCMYK" or "CMYK":
                return new CmykSpace(context);
            case "Lab":
                return new LabSpace(array.Count > 1 ? array.Get<PdfDictionary>(1) : null);
            case "ICCBased":
                return IccSpace(array.Count > 1 ? array.Get(1).Resolve() as PdfStream : null, resources, context, depth);
            case "Indexed" or "I":
                if (array.Count < 4)
                {
                    throw new InvalidDataException("An Indexed colour space has too few entries.");
                }

                ImageColorSpace baseSpace = Read(array.Get(1), resources, context, depth + 1);
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

                return new TintSpace([array.GetName(1)?.Value ?? ""], array.GetName(1)?.Value is "None", Read(array.Get(2), resources, context, depth + 1), PdfFunction.Read(array.Get(3)));
            case "DeviceN":
                if (array.Count < 4 || array.Get(1).Resolve() is not PdfArray names)
                {
                    throw new InvalidDataException("A DeviceN colour space has too few entries.");
                }

                return new TintSpace([.. names.Select(n => (n.Resolve() as PdfName)?.Value ?? "")], names.All(n => (n.Resolve() as PdfName)?.Value == "None"), Read(array.Get(2), resources, context, depth + 1), PdfFunction.Read(array.Get(3)));
            case "Pattern":
                throw new InvalidDataException("An image cannot use the Pattern colour space.");
            default:
                throw new InvalidDataException($"The colour space {family} is not known.");
        }
    }

    /// <summary>An ICCBased space: through its profile when Egami reads it and it has 1, 3 or 4 components, else the alternate.</summary>
    private static ImageColorSpace IccSpace(PdfStream? stream, PdfDictionary? resources, ColorContext? context, int depth)
    {
        int n = (int)(stream?.Dictionary.GetInt("N") ?? 3);
        context ??= new ColorContext();
        ColorProfile? profile = stream is not null && n is 1 or 3 or 4 ? context.Profile(stream) : null;

        ImageColorSpace alternate = stream?.Dictionary.Get("Alternate") is PdfObject alt
            ? Read(alt, resources, context, depth + 1)
            : n switch
            {
                1 => new GraySpace(),
                4 => new CmykSpace(context),
                _ => new RgbSpace(),
            };
        return alternate.Components == n ? new IccBasedSpace(alternate, profile, context) : alternate;
    }

    /// <summary>DeviceGray or DeviceRGB: through the profile the context says it stands for (an output intent's), else as it is.</summary>
    private static ImageColorSpace Device(int components, ColorContext? context)
    {
        ImageColorSpace plain = components == 1 ? new GraySpace() : new RgbSpace();
        ColorProfile? profile = components == 1 ? context?.Gray : context?.Rgb;
        return profile is null ? plain : new IccBasedSpace(plain, profile, context!, device: true);
    }

    private sealed class GraySpace : ImageColorSpace
    {
        public override bool IsDevice => true;

        public override int Components => 1;

        public override ColorFamily Family => ColorFamily.Gray;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            rgb[0] = rgb[1] = rgb[2] = c[0];
        }
    }

    private sealed class RgbSpace : ImageColorSpace
    {
        public override bool IsDevice => true;

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
    private sealed class CmykSpace(ColorContext? context) : ImageColorSpace
    {
        private readonly ColorProfile? profile = context?.Cmyk;
        private readonly ColorTransform? transform = context?.Cmyk is ColorProfile cmyk ? context.Transform(cmyk) : null;

        public override int Components => 4;

        public override bool IsDevice => true;

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
    private sealed class IccBasedSpace(ImageColorSpace alternate, ColorProfile? profile, ColorContext context, bool device = false) : ImageColorSpace
    {
        private readonly ColorTransform? transform = profile is null ? null : context.Transform(profile);

        public override bool IsDevice => device;

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
            XyzToSrgb(x, y, z, white, rgb);
        }

        /// <summary>XYZ under a white point to sRGB: Bradford to D65, then the sRGB matrix and transfer.</summary>
        public static void XyzToSrgb(double x, double y, double z, double[] white, Span<double> rgb)
        {
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

    /// <summary>
    /// CalGray and CalRGB (8.6.5.2, 8.6.5.3): each component raised to its gamma, through the matrix to XYZ under the white
    /// point, adapted to D65 and encoded as sRGB, as pdf.js converts them (Phase 4 task 19).
    /// </summary>
    private sealed class CalSpace : ImageColorSpace
    {
        private readonly int components;
        private readonly double[] white;
        private readonly double[] gamma;
        private readonly double[] matrix;

        public CalSpace(int components, PdfDictionary parameters)
        {
            this.components = components;
            double[] w = Numbers(parameters.Get<PdfArray>("WhitePoint"), 3, [0.9505, 1, 1.089]);
            white = w[1] > 0 && w[0] > 0 && w[2] > 0 ? w : [0.9505, 1, 1.089];
            gamma = Numbers(parameters.Get("Gamma")?.Resolve() switch { PdfArray g => g, PdfObject g => new PdfArray { g }, _ => null }, components, components == 1 ? [1] : [1, 1, 1]);
            matrix = components == 3 ? Numbers(parameters.Get<PdfArray>("Matrix"), 9, [1, 0, 0, 0, 1, 0, 0, 0, 1]) : [];
        }

        public override int Components => components;

        public override ColorFamily Family => components == 1 ? ColorFamily.Gray : ColorFamily.Rgb;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            double x, y, z;
            if (components == 1)
            {
                double a = Math.Pow(Math.Clamp(c[0], 0, 1), gamma[0]);
                (x, y, z) = (white[0] * a, white[1] * a, white[2] * a);
            }
            else
            {
                double a = Math.Pow(Math.Clamp(c[0], 0, 1), gamma[0]), b = Math.Pow(Math.Clamp(c[1], 0, 1), gamma[1]), cc = Math.Pow(Math.Clamp(c[2], 0, 1), gamma[2]);
                x = (matrix[0] * a) + (matrix[3] * b) + (matrix[6] * cc);
                y = (matrix[1] * a) + (matrix[4] * b) + (matrix[7] * cc);
                z = (matrix[2] * a) + (matrix[5] * b) + (matrix[8] * cc);
            }

            LabSpace.XyzToSrgb(x, y, z, white, rgb);
        }

        private static double[] Numbers(PdfArray? array, int count, double[] fallback)
        {
            if (array is null || array.Count < count)
            {
                return fallback;
            }

            double[] values = new double[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = array.GetNumber(i) is double v && double.IsFinite(v) ? v : fallback[i];
            }

            return values;
        }
    }

    /// <summary>Indexed: a sample is an index into a table of base-space colours.</summary>
    internal sealed class IndexedSpace(ImageColorSpace baseSpace, int high, byte[] lookup) : ImageColorSpace
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
            Span<double> components = stackalloc double[baseSpace.Components];
            BaseColor(c[0], components);
            baseSpace.ToRgb(components, rgb);
        }

        /// <summary>The base-space colour an index stands for, each component in the base space's range.</summary>
        public void BaseColor(double value, Span<double> components)
        {
            int index = (int)Math.Clamp(Math.Round(value), 0, high);
            int n = baseSpace.Components;
            double[] decode = baseSpace.DefaultDecode(8);
            for (int i = 0; i < n; i++)
            {
                int at = (index * n) + i;
                double v = at < lookup.Length ? lookup[at] / 255.0 : 0;
                components[i] = decode[2 * i] + (v * (decode[(2 * i) + 1] - decode[2 * i]));
            }
        }
    }

    /// <summary>Separation and DeviceN: tints through the tint transform into the alternate space; the None colorant paints nothing.</summary>
    /// <summary>
    /// Separation and DeviceN: through the tint transform to the alternate space. A one-colourant space is sampled the first
    /// time it is used into a table of sRGB colours (Phase 4 design Q8), so each pixel or fill costs a lookup.
    /// </summary>
    private sealed class TintSpace(string[] names, bool none, ImageColorSpace alternate, PdfFunction transform) : ImageColorSpace
    {
        private const int TableSize = 1024;

        private readonly int components = names.Length;

        private FunctionTable? table;

        public override int Components => components;

        public override IReadOnlyList<string> Colorants => names;

        public override ImageColorSpace Alternate => alternate;

        public override void ToAlternate(ReadOnlySpan<double> c, Span<double> alt)
        {
            Span<double> all = stackalloc double[Math.Max(alternate.Components, Math.Max(transform.Outputs, 1))];
            all.Clear();
            transform.Evaluate(c[..Math.Min(c.Length, components)], all);
            all[..alternate.Components].CopyTo(alt);
        }

        public override ColorFamily Family => ColorFamily.Separation;

        public override bool PaintsNothing => none;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            if (components == 1 && c.Length >= 1)
            {
                table ??= Table();
                table.Evaluate(c[0], rgb);
                return;
            }

            Convert(c[..Math.Min(c.Length, components)], rgb);
        }

        private void Convert(ReadOnlySpan<double> c, Span<double> rgb)
        {
            Span<double> alt = stackalloc double[Math.Max(alternate.Components, Math.Max(transform.Outputs, 1))];
            alt.Clear();
            transform.Evaluate(c, alt);
            alternate.ToRgb(alt[..alternate.Components], rgb);
        }

        private FunctionTable Table()
        {
            (double start, double end) = transform.FirstDomain;
            double[] values = new double[TableSize * 3];
            Span<double> input = stackalloc double[1];
            for (int i = 0; i < TableSize; i++)
            {
                input[0] = start + ((end - start) * i / (TableSize - 1));
                Convert(input, values.AsSpan(i * 3, 3));
            }

            return new FunctionTable(start, end, 3, values);
        }
    }

    private static ushort Unit16(double v)
    {
        return (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
    }
}
