using Egami;
using Egami.Pixels;

using Egap.Pdf.Annotations;

namespace Egap.Pdf.Optimizing;

/// <summary>
/// The optimizer's image survey (Phase 7 design 7.1 and Q3, Build Plan task 2): every image XObject and inline image the
/// document draws, listed once, with its largest placement, its effective resolution at that placement, the kind of picture
/// it holds and its masks. Images inside forms and tiling patterns are found where they are drawn; images in annotation
/// appearances are placed through the annotation's rectangle (12.5.5). An image is never downsampled below what its largest
/// placement needs, so an image whose placement cannot be known (on a page whose content cannot be read) is marked
/// <see cref="SurveyedImage.PlacementKnown"/> false.
/// </summary>
internal static class ImageSurvey
{
    /// <summary>The longest side images are decoded at to judge their kind; a large image is judged on a sample.</summary>
    internal const int SampleSize = 256;

    /// <summary>At most this many colours, in the sample, for a picture to count as flat colour.</summary>
    internal const int FlatColorLimit = 256;

    /// <summary>At least this share of neighbouring sample pairs equal for a picture to count as flat colour.</summary>
    internal const double FlatEqualShare = 0.6;

    /// <summary>Surveys every page of the document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="lossy">
    /// False when only lossless changes follow: then the only kind that matters is bilevel, and images that cannot be bilevel
    /// (by their parameters, or a first sample that is neither black nor white) are not decoded.
    /// </param>
    public static IReadOnlyList<SurveyedImage> Run(PdfDocument document, bool lossy = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        Dictionary<object, SurveyedImage> images = new(ReferenceEqualityComparer.Instance);
        List<SurveyedImage> order = [];
        for (int index = 0; index < document.Pages.Count; index++)
        {
            PdfPage page = document.Pages[index];
            double unit = page.UserUnit;
            if (!lossy)
            {
                // Lossless changes keep every image's size, so where it is drawn does not matter: the resources tell what is.
                Unplaced(images, order, page.Resources, index, depth: 0, seen: new(ReferenceEqualityComparer.Instance));
            }
            else
            {
                try
                {
                    foreach (PdfPlacedImage placed in page.GetImages())
                    {
                        Add(images, order, placed.Image, placed.Matrix, unit, index, appearance: false);
                    }
                }
                catch (PdfException e) when (e is not PdfLimitException)
                {
                    // The page's content cannot be read: whatever its resources name may be drawn at any size.
                    Unplaced(images, order, page.Resources, index, depth: 0, seen: new(ReferenceEqualityComparer.Instance));
                }
            }

            foreach (PdfAnnotation annotation in page.Annotations)
            {
                Appearances(images, order, annotation, index, unit, document);
            }
        }

        foreach (SurveyedImage image in order)
        {
            image.Kind = lossy ? Classify(image.Image) : Bilevel(image.Image) switch
            {
                true => SurveyedImageKind.Bilevel,
                false => SurveyedImageKind.Photo,
                null => Classify(image.Image),
            };
        }

        return order;
    }

    /// <summary>The kind of picture an image holds, judged on its parameters and then on a decoded sample.</summary>
    internal static SurveyedImageKind Classify(PdfImage image)
    {
        if (image.IsStencilMask || image.BitsPerComponent == 1 && Components(image) == 1
            || image.ImageCodec?.Value is "CCITTFaxDecode" or "JBIG2Decode")
        {
            return SurveyedImageKind.Bilevel;
        }

        if (image.Width <= 0 || image.Height <= 0)
        {
            return SurveyedImageKind.Unknown;
        }

        try
        {
            PdfImageOptions options = new() { ApplyMasks = false, MinimumSize = new Size(SampleSize, SampleSize) };
            using Raster<Rgb8> raster = image.ToRaster<Rgb8>(options);
            return Classify(raster);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or NotSupportedException)
        {
            return SurveyedImageKind.Unknown;
        }
    }

    /// <summary>
    /// Bilevel when every sample is black or white; flat colour when few colours and large runs of equal neighbours; a
    /// photo otherwise (design Q4).
    /// </summary>
    internal static SurveyedImageKind Classify(Raster<Rgb8> raster)
    {
        int stepX = Math.Max(1, raster.Width / SampleSize), stepY = Math.Max(1, raster.Height / SampleSize);
        HashSet<int> colours = [];
        bool bilevel = true;
        long pairs = 0, equal = 0;
        Rgb8[]? previousRow = null;
        List<Rgb8> row = [];
        for (int y = 0; y < raster.Height; y += stepY)
        {
            Span<Rgb8> pixels = raster.Row(y);
            row.Clear();
            for (int x = 0; x < raster.Width; x += stepX)
            {
                Rgb8 p = pixels[x];
                row.Add(p);
                if (colours.Count <= FlatColorLimit)
                {
                    colours.Add((p.R << 16) | (p.G << 8) | p.B);
                }

                bilevel &= p.R == p.G && p.G == p.B && p.R is 0 or 255;
                if (row.Count > 1)
                {
                    pairs++;
                    equal += row[^2] == p ? 1 : 0;
                }

                if (previousRow is not null && row.Count - 1 < previousRow.Length)
                {
                    pairs++;
                    equal += previousRow[row.Count - 1] == p ? 1 : 0;
                }
            }

            previousRow = [.. row];
        }

        if (bilevel)
        {
            return SurveyedImageKind.Bilevel;
        }

        return colours.Count <= FlatColorLimit && pairs > 0 && equal >= FlatEqualShare * pairs
            ? SurveyedImageKind.FlatColor
            : SurveyedImageKind.Photo;
    }

    /// <summary>
    /// Whether an image is bilevel, for lossless changes; null when only decoding can tell. 1-bit images, CCITT and JBIG2 are
    /// left to <see cref="Classify(PdfImage)"/>, and JPEG and JPEG 2000 never count: lossless changes keep them as they are. 8-bit gray, RGB (device, calibrated or ICC)
    /// and indexed samples stored
    /// plain or with Flate are read only up to the first that is not black or white.
    /// </summary>
    private static bool? Bilevel(PdfImage image)
    {
        if (image.IsStencilMask || image.BitsPerComponent == 1 || image.ImageCodec?.Value is "CCITTFaxDecode" or "JBIG2Decode")
        {
            return null;
        }

        if (image.ImageCodec is not null || image.BitsPerComponent != 8 || image.Width <= 0 || image.Height <= 0)
        {
            return false;
        }

        // Which stored byte values show black or white: all of them for gray and RGB, the palette's black and white for Indexed.
        bool[]? allowed = image.ColorSpace switch
        {
            PdfArray { Count: 4 } indexed when indexed.GetName(0)?.Value is "Indexed" or "I" => Palette(indexed),
            PdfObject space when GrayOrRgb(space) > 0 => null,
            _ => [],
        };
        if (allowed is { Length: 0 } || image.Parameters.Get("Decode") is not null || image.Stream is not PdfStream stream
            || stream.Dictionary.Get("DecodeParms")?.Resolve() is PdfDictionary or PdfArray)
        {
            return allowed is not { Length: 0 } || image.ColorSpace is not PdfName { Value: "DeviceCMYK" or "CMYK" } ? null : false;
        }

        IReadOnlyList<PdfName> filters = stream.Filters;
        if (filters.Count > 1 || filters.Count == 1 && filters[0].Value is not ("FlateDecode" or "Fl"))
        {
            return null;
        }

        try
        {
            using Stream encoded = new MemoryStream(stream.GetEncodedBytes(), writable: false);
            using Stream samples = filters.Count == 0 ? encoded : new System.IO.Compression.ZLibStream(encoded, System.IO.Compression.CompressionMode.Decompress);
            Span<byte> buffer = stackalloc byte[4096];
            long count = 0;
            int read;
            while ((read = samples.Read(buffer)) > 0)
            {
                count += read;
                foreach (byte b in buffer[..read])
                {
                    if (allowed is null ? b is not (0 or 255) : !allowed[b])
                    {
                        return false;
                    }
                }
            }

            int components = allowed is null ? GrayOrRgb(image.ColorSpace) : 1;
            return count >= (long)image.Width * image.Height * components ? true : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The components of a gray or RGB space, device, calibrated or ICC; 0 for any other.</summary>
    private static int GrayOrRgb(PdfObject? space) => space switch
    {
        PdfName { Value: "DeviceGray" or "G" } => 1,
        PdfName { Value: "DeviceRGB" or "RGB" } => 3,
        PdfArray { Count: 2 } cal when cal.GetName(0)?.Value == "CalGray" => 1,
        PdfArray { Count: 2 } cal when cal.GetName(0)?.Value == "CalRGB" => 3,
        PdfArray { Count: 2 } icc when icc.GetName(0)?.Value == "ICCBased" => icc.Get<PdfStream>(1)?.Dictionary.GetInt("N") switch
        {
            1 => 1,
            3 => 3,
            _ => 0,
        },
        _ => 0,
    };

    /// <summary>
    /// For each of the 256 indices, whether an Indexed palette over a gray or RGB space shows it black or white; an empty
    /// array when the palette cannot be read this way, which leaves the judgement to decoding.
    /// </summary>
    private static bool[] Palette(PdfArray indexed)
    {
        int components = GrayOrRgb(indexed.Get(1)?.Resolve());
        byte[]? table = indexed.Get(3)?.Resolve() switch
        {
            PdfString text => text.Bytes.ToArray(),
            PdfStream lookup => TryDecode(lookup),
            _ => null,
        };
        if (components == 0 || table is null)
        {
            return [];
        }

        bool[] allowed = new bool[256];
        for (int index = 0; index < 256 && (index + 1) * components <= table.Length; index++)
        {
            ReadOnlySpan<byte> entry = table.AsSpan(index * components, components);
            allowed[index] = !entry.ContainsAnyExcept((byte)0) || !entry.ContainsAnyExcept((byte)255);
        }

        return allowed;
    }

    private static byte[]? TryDecode(PdfStream stream)
    {
        try
        {
            return stream.GetDecodedBytes();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return null;
        }
    }

    private static int Components(PdfImage image)
    {
        PdfObject? space = image.ColorSpace;
        return space switch
        {
            PdfName { Value: "DeviceGray" or "G" or "CalGray" } => 1,
            PdfArray { Count: > 0 } array when array.GetName(0)?.Value is "CalGray" => 1,
            PdfArray { Count: > 1 } array when array.GetName(0)?.Value == "ICCBased" && array.Get<PdfStream>(1)?.Dictionary.GetInt("N") == 1 => 1,
            _ => 0,
        };
    }

    private static void Add(Dictionary<object, SurveyedImage> images, List<SurveyedImage> order, PdfImage image, PdfMatrix? matrix, double unit, int page, bool appearance)
    {
        object key = (object?)image.Stream ?? image.Inline!;
        if (!images.TryGetValue(key, out SurveyedImage? surveyed))
        {
            surveyed = new SurveyedImage(image);
            images.Add(key, surveyed);
            order.Add(surveyed);
        }

        surveyed.Place(matrix, unit, page, appearance);
    }

    private static void Unplaced(Dictionary<object, SurveyedImage> images, List<SurveyedImage> order, PdfDictionary? resources, int page, int depth, HashSet<PdfObject> seen)
    {
        if (resources is null || depth > 16 || !seen.Add(resources))
        {
            return;
        }

        // Tiling patterns and soft mask groups draw with resources of their own.
        foreach (KeyValuePair<PdfName, PdfObject> entry in resources.Get<PdfDictionary>("Pattern")?.Entries ?? [])
        {
            if (entry.Value.Resolve() is PdfStream pattern)
            {
                Unplaced(images, order, pattern.Dictionary.Get<PdfDictionary>("Resources"), page, depth + 1, seen);
            }
        }

        foreach (KeyValuePair<PdfName, PdfObject> entry in resources.Get<PdfDictionary>("ExtGState")?.Entries ?? [])
        {
            if (entry.Value.Resolve() is PdfDictionary state && state.Get<PdfDictionary>("SMask")?.Get<PdfStream>("G") is PdfStream group)
            {
                Unplaced(images, order, group.Dictionary.Get<PdfDictionary>("Resources"), page, depth + 1, seen);
            }
        }

        if (resources.Get<PdfDictionary>("XObject") is not PdfDictionary xobjects)
        {
            return;
        }

        foreach (KeyValuePair<PdfName, PdfObject> entry in xobjects.Entries)
        {
            if (entry.Value.Resolve() is not PdfStream stream)
            {
                continue;
            }

            string? subtype = stream.Dictionary.GetName("Subtype")?.Value;
            if (subtype == "Image")
            {
                Add(images, order, new PdfImage(stream, resources), null, 1, page, appearance: false);
            }
            else if (subtype == "Form")
            {
                Unplaced(images, order, stream.Dictionary.Get<PdfDictionary>("Resources"), page, depth + 1, seen);
            }
        }
    }

    /// <summary>The images every appearance stream of an annotation draws, placed in its rectangle.</summary>
    private static void Appearances(Dictionary<object, SurveyedImage> images, List<SurveyedImage> order, PdfAnnotation annotation, int page, double unit, PdfDocument document)
    {
        if (annotation.Dictionary.Get<PdfDictionary>("AP") is not PdfDictionary ap || annotation.Rectangle.IsEmpty)
        {
            return;
        }

        foreach (KeyValuePair<PdfName, PdfObject> kind in ap.Entries)
        {
            List<PdfStream> streams = kind.Value.Resolve() switch
            {
                PdfStream single => [single],
                PdfDictionary states => [.. states.Entries.Select(s => s.Value.Resolve()).OfType<PdfStream>()],
                _ => [],
            };
            foreach (PdfStream stream in streams)
            {
                try
                {
                    if (Flattener.Placement(stream, annotation.Rectangle) is not double[] m)
                    {
                        continue;
                    }

                    PdfMatrix placement = (PdfMatrix.FromArray(stream.Dictionary.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity)
                        * new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);
                    PdfPage.ImageCollector collector = new(document);
                    collector.Walk(PdfContent.Read(stream), stream.Dictionary.Get<PdfDictionary>("Resources"), placement, document.Options);
                    foreach (PdfPlacedImage placed in collector.Images)
                    {
                        Add(images, order, placed.Image, placed.Matrix, unit, page, appearance: true);
                    }
                }
                catch (PdfException e) when (e is not PdfLimitException)
                {
                    Unplaced(images, order, stream.Dictionary.Get<PdfDictionary>("Resources"), page, 0, new(ReferenceEqualityComparer.Instance));
                }
            }
        }
    }
}

/// <summary>What kind of picture an image holds (Phase 7 design Q4): it decides the encoding the optimizer writes.</summary>
internal enum SurveyedImageKind
{
    /// <summary>Not judged: the image could not be decoded.</summary>
    Unknown,

    /// <summary>Continuous tone: JPEG suits it.</summary>
    Photo,

    /// <summary>Few colours in flat areas, such as a chart or a screenshot: Flate suits it.</summary>
    FlatColor,

    /// <summary>Black and white only, such as a text scan: CCITT G4 suits it, and it is never resampled.</summary>
    Bilevel,
}

/// <summary>One image of the survey: where it is drawn largest and what it holds.</summary>
internal sealed class SurveyedImage(PdfImage image)
{
    /// <summary>The image; for an inline image, the first place it was found.</summary>
    public PdfImage Image { get; } = image;

    /// <summary>The kind of picture.</summary>
    public SurveyedImageKind Kind { get; internal set; }

    /// <summary>How often the image is drawn, in page content and annotation appearances.</summary>
    public int Placements { get; private set; }

    /// <summary>The 0-based indexes of the pages that draw it, in order, each once.</summary>
    public List<int> Pages { get; } = [];

    /// <summary>Whether it is drawn by an annotation appearance.</summary>
    public bool InAppearance { get; private set; }

    /// <summary>
    /// Whether every place it is drawn is known. False when it is in the resources of content that could not be read: then
    /// its resolution must be kept.
    /// </summary>
    public bool PlacementKnown { get; private set; } = true;

    /// <summary>The largest placement: the image's unit square as drawn, in points (user units applied).</summary>
    public PdfMatrix? Largest { get; private set; }

    /// <summary>The drawn width of the largest placement in points: the length of the image's x axis.</summary>
    public double PlacedWidth => Largest is PdfMatrix m ? Math.Sqrt((m.A * m.A) + (m.B * m.B)) : 0;

    /// <summary>The drawn height of the largest placement in points: the length of the image's y axis.</summary>
    public double PlacedHeight => Largest is PdfMatrix m ? Math.Sqrt((m.C * m.C) + (m.D * m.D)) : 0;

    /// <summary>Samples per inch along the image's width at its largest placement; infinity for a placement of no size.</summary>
    public double ResolutionX => PlacedWidth > 0 ? Image.Width * 72 / PlacedWidth : double.PositiveInfinity;

    /// <summary>Samples per inch along the image's height at its largest placement.</summary>
    public double ResolutionY => PlacedHeight > 0 ? Image.Height * 72 / PlacedHeight : double.PositiveInfinity;

    /// <summary>
    /// The effective resolution (design Q3): the lower of <see cref="ResolutionX"/> and <see cref="ResolutionY"/> at the
    /// largest placement, or null when the placement is not known.
    /// </summary>
    public double? EffectiveResolution => PlacementKnown && Largest is not null ? Math.Min(ResolutionX, ResolutionY) : null;

    /// <summary>The soft mask image (<c>/SMask</c>), which is resampled with the image.</summary>
    public PdfStream? SoftMask => Image.Parameters.Get("SMask")?.Resolve() as PdfStream;

    /// <summary>The explicit mask image (<c>/Mask</c> as a stream).</summary>
    public PdfStream? ExplicitMask => Image.Parameters.Get("Mask")?.Resolve() as PdfStream;

    /// <summary>The colour key mask (<c>/Mask</c> as an array of ranges), which lossy encoding would break.</summary>
    public PdfArray? ColorKeyMask => Image.Parameters.Get("Mask")?.Resolve() as PdfArray;

    internal void Place(PdfMatrix? matrix, double unit, int page, bool appearance)
    {
        Placements++;
        InAppearance |= appearance;
        if (Pages.Count == 0 || Pages[^1] != page)
        {
            if (!Pages.Contains(page))
            {
                Pages.Add(page);
            }
        }

        if (matrix is not PdfMatrix m)
        {
            PlacementKnown = false;
            return;
        }

        PdfMatrix scaled = unit == 1 ? m : m * PdfMatrix.Scale(unit, unit);
        if (Largest is not PdfMatrix largest || Math.Abs(scaled.Determinant) > Math.Abs(largest.Determinant))
        {
            Largest = scaled;
        }
    }

    public override string ToString()
    {
        return FormattableString.Invariant($"{Image.Width}x{Image.Height} {Kind} at {EffectiveResolution:0} dpi, {Placements} placements on {Pages.Count} pages");
    }
}
