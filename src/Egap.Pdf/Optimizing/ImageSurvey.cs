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
    public static IReadOnlyList<SurveyedImage> Run(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Dictionary<object, SurveyedImage> images = new(ReferenceEqualityComparer.Instance);
        List<SurveyedImage> order = [];
        for (int index = 0; index < document.Pages.Count; index++)
        {
            PdfPage page = document.Pages[index];
            double unit = page.UserUnit;
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

            foreach (PdfAnnotation annotation in page.Annotations)
            {
                Appearances(images, order, annotation, index, unit, document);
            }
        }

        foreach (SurveyedImage image in order)
        {
            image.Kind = Classify(image.Image);
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
        if (resources?.Get<PdfDictionary>("XObject") is not PdfDictionary xobjects || depth > 16 || !seen.Add(resources))
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
