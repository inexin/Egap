using Egami;
using Egami.Colors;
using Egami.Pixels;

using Egap.Pdf.Filters;
using Egap.Pdf.Images;
using Egap.Pdf.IO;

namespace Egap.Pdf;

/// <summary>How <see cref="PdfImage.ToRaster(PdfImageOptions?)"/> decodes an image (Phase 3 design Q11 and Q12).</summary>
public sealed record PdfImageOptions
{
    /// <summary>The default options: masks applied, colour converted to sRGB, CMYK through the built-in conversion.</summary>
    public static PdfImageOptions Default { get; } = new();

    /// <summary>
    /// Gets whether the image's soft mask, explicit mask or colour key mask becomes the raster's alpha. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool ApplyMasks { get; init; } = true;

    /// <summary>
    /// Gets whether colour is converted to sRGB. With <see langword="false"/>, gray, RGB and CMYK images (ICCBased ones by
    /// their component count, Indexed ones by their base) keep their components as stored: a CMYK image gives CMYK pixels,
    /// without its mask, as Egami has no CMYK pixel with alpha. Other spaces are converted either way. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool ConvertColor { get; init; } = true;

    /// <summary>
    /// Gets the profile DeviceCMYK images are converted through, such as a press profile; <see langword="null"/> (the
    /// default) for the built-in conversion. ICCBased images use their own profiles.
    /// </summary>
    public ColorProfile? CmykProfile { get; init; }

    /// <summary>
    /// For rendering (Phase 4 design Q14): JPEG and JPEG 2000 images decode at the smallest native reduction at least this
    /// size, so a small drawing of a large scan decodes a fraction of its pixels; null decodes at full size.
    /// </summary>
    internal Egami.Size? MinimumSize { get; init; }

    /// <summary>For rendering: the document's colour context, with its output intent, intent and caches (Phase 4 design Q7).</summary>
    internal ColorContext? Colors { get; init; }
}

/// <summary>
/// An image a page draws (8.9): an image XObject or an inline image (Phase 3 design Q11). It gives the image's stored
/// bytes and filters, so a JPEG can be saved as it is, and decodes to an Egami raster with its colour space, decode array
/// and masks applied.
/// </summary>
public sealed class PdfImage
{
    private static readonly Dictionary<string, string> InlineKeys = new(StringComparer.Ordinal)
    {
        ["BPC"] = "BitsPerComponent",
        ["CS"] = "ColorSpace",
        ["D"] = "Decode",
        ["DP"] = "DecodeParms",
        ["F"] = "Filter",
        ["H"] = "Height",
        ["IM"] = "ImageMask",
        ["I"] = "Interpolate",
        ["W"] = "Width",
        ["L"] = "Length",
    };

    private static readonly Dictionary<string, string> InlineFilters = new(StringComparer.Ordinal)
    {
        ["AHx"] = "ASCIIHexDecode",
        ["A85"] = "ASCII85Decode",
        ["LZW"] = "LZWDecode",
        ["Fl"] = "FlateDecode",
        ["RL"] = "RunLengthDecode",
        ["CCF"] = "CCITTFaxDecode",
        ["DCT"] = "DCTDecode",
    };

    private readonly PdfInlineImage? inline;

    internal PdfImage(PdfStream stream, PdfDictionary? resources, PdfDocument? document = null)
    {
        Stream = stream;
        Parameters = stream.Dictionary;
        Resources = resources;
        Document = document;
    }

    internal PdfImage(PdfInlineImage inline, PdfDictionary? resources)
    {
        this.inline = inline;
        Parameters = Expand(inline.Parameters);
        Resources = resources;
    }

    /// <summary>The image XObject's stream, or <see langword="null"/> for an inline image.</summary>
    public PdfStream? Stream { get; }

    /// <summary>Whether the image is inline in a content stream (<c>BI</c> ... <c>EI</c>).</summary>
    public bool IsInline => inline is not null;

    /// <summary>The width in samples.</summary>
    public int Width => (int)(Parameters.GetInt("Width") ?? 0);

    /// <summary>The height in samples.</summary>
    public int Height => (int)(Parameters.GetInt("Height") ?? 0);

    /// <summary>Bits per colour component: 1, 2, 4, 8 or 16; 0 when the codec says (JPEG 2000).</summary>
    public int BitsPerComponent => IsStencilMask ? 1 : (int)(Parameters.GetInt("BitsPerComponent") ?? 0);

    /// <summary>Whether the image is a stencil mask (<c>/ImageMask true</c>), painting the current colour where it is 0.</summary>
    public bool IsStencilMask => Parameters.GetBool("ImageMask") == true;

    /// <summary>Whether the image asks to be smoothed when drawn larger than its pixels (<c>/Interpolate</c>).</summary>
    internal bool Interpolate => Parameters.GetBool("Interpolate") == true;

    /// <summary>The rendering intent the image asks for (<c>/Intent</c>), or null.</summary>
    internal PdfName? Intent => Parameters.GetName("Intent");

    /// <summary>The colour space as written: a name or an array; <see langword="null"/> for stencil masks and some JPEG 2000 images.</summary>
    public PdfObject? ColorSpace => Parameters.Get("ColorSpace")?.Resolve();

    /// <summary>The filters, in the order they are decoded, inline abbreviations written out.</summary>
    public IReadOnlyList<PdfName> Filters => FilterChain.Filters(Parameters);

    /// <summary>
    /// The image codec its data is stored in (<c>DCTDecode</c> for JPEG, <c>JPXDecode</c>, <c>JBIG2Decode</c>,
    /// <c>CCITTFaxDecode</c>), or <see langword="null"/> for plain samples.
    /// </summary>
    public PdfName? ImageCodec => Filters.FirstOrDefault(FilterChain.IsImageFilter);

    /// <summary>The image's dictionary with inline abbreviations written out.</summary>
    internal PdfDictionary Parameters { get; }

    /// <summary>The resources named colour spaces are looked up in.</summary>
    internal PdfDictionary? Resources { get; }

    /// <summary>The document the image belongs to, for replacing it.</summary>
    internal PdfDocument? Document { get; }

    /// <summary>The bytes as stored, still encoded with every filter: a DCTDecode image's are a JPEG file.</summary>
    public byte[] GetRawBytes()
    {
        return Stream?.GetEncodedBytes() ?? inline!.Data.ToArray();
    }

    /// <summary>
    /// The data with its filters undone up to the image codec, as <see cref="PdfStream.GetDecodedBytes"/> gives them: a
    /// JPEG file for DCTDecode after, say, ASCII85, else the samples.
    /// </summary>
    /// <exception cref="PdfException">A filter is not supported or its data is damaged.</exception>
    public byte[] GetDecodedBytes()
    {
        return DecodeFilters().Data;
    }

    /// <summary>
    /// Decodes the image to a raster: sRGB as <see cref="Rgba8"/> (<see cref="Rgba16"/> for 16-bit images) with the mask
    /// as alpha by default; see <see cref="PdfImageOptions"/> for keeping colour or leaving masks off. The caller owns it.
    /// </summary>
    /// <exception cref="PdfException">The image cannot be decoded: an unsupported filter, colour space or damaged data.</exception>
    public Raster ToRaster(PdfImageOptions? options = null)
    {
        return ImageDecoder.Decode(this, options ?? PdfImageOptions.Default);
    }

    /// <summary>Decodes the image and converts it to <typeparamref name="TPixel"/>. The caller owns the raster.</summary>
    /// <exception cref="PdfException">The image cannot be decoded.</exception>
    public Raster<TPixel> ToRaster<TPixel>(PdfImageOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster raster = ToRaster(options);
        if (raster is Raster<TPixel> same)
        {
            return same;
        }

        using (raster)
        {
            return raster.As<TPixel>();
        }
    }

    /// <summary>
    /// Replaces the image with a raster, in place (Phase 3 design, image replacement): the XObject keeps its object number,
    /// so every page that draws it shows the new image, and an incremental save writes only the new stream (and its soft mask).
    /// The raster's alpha becomes a soft mask; its RGB or gray ICC profile is kept. Width, height, colour space, filters
    /// and masks are rewritten; other entries (such as <c>/Interpolate</c> or optional content) stay.
    /// </summary>
    /// <param name="image">The new image.</param>
    /// <param name="encoding">Flate (lossless, the default) or JPEG at a quality.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The image is inline, or was not reached through a document's pages.</exception>
    public void Replace(Raster image, PdfImageEncoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        PdfDocument document = Replaceable();
        encoding ??= PdfImageEncoding.Flate;
        PdfStream encoded = encoding.Quality is int quality ? ImageEncoder.EncodeJpeg(document, image, quality) : ImageEncoder.Encode(document, image);
        Take(encoded);
    }

    /// <summary>
    /// Replaces the image with JPEG bytes, stored as they are with <c>/DCTDecode</c>: gray, RGB or CMYK as the JPEG's frame
    /// says (CMYK from Adobe software inverted, as its marker says). See <see cref="Replace(Raster, PdfImageEncoding?)"/>.
    /// </summary>
    /// <param name="jpeg">A baseline or progressive JPEG file.</param>
    /// <exception cref="ArgumentException">The bytes are not a JPEG Egami can read.</exception>
    /// <exception cref="InvalidOperationException">The image is inline, or was not reached through a document's pages.</exception>
    public void ReplaceWithJpeg(ReadOnlySpan<byte> jpeg)
    {
        Replaceable();
        Take(ImageEncoder.JpegStream(jpeg));
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{(IsInline ? "Inline image" : "Image")} {Width}x{Height}{(ImageCodec is PdfName codec ? " " + codec.Value : string.Empty)}";
    }

    private PdfDocument Replaceable()
    {
        if (Stream is null)
        {
            throw new InvalidOperationException("An inline image is part of its content stream and cannot be replaced in place.");
        }

        return Document ?? throw new InvalidOperationException("The image was not reached through a document's pages.");
    }

    /// <summary>Puts a newly encoded image's dictionary entries and data into this XObject's stream.</summary>
    private void Take(PdfStream encoded)
    {
        PdfDictionary target = Stream!.Dictionary;
        foreach (string key in (string[])["Filter", "DecodeParms", "Decode", "ColorSpace", "BitsPerComponent", "Width", "Height", "ImageMask", "Mask", "SMask", "SMaskInData", "DL", "Alternates", "Matte"])
        {
            target.Remove(key);
        }

        foreach (KeyValuePair<PdfName, PdfObject> entry in encoded.Dictionary.Entries.ToList())
        {
            if (entry.Key.Value is not ("Type" or "Subtype" or "Length"))
            {
                encoded.Dictionary.Remove(entry.Key.Value);
                target[entry.Key.Value] = entry.Value;
            }
        }

        Stream.SetEncodedBytes(encoded.GetEncodedBytes());
    }

    internal (byte[] Data, PdfName? Codec) DecodeFilters()
    {
        if (Stream is not null)
        {
            return (Stream.GetDecodedBytes(), Stream.UndecodedFilter);
        }

        byte[] encoded = inline!.Data.ToArray();
        return FilterChain.Decode(Parameters, encoded, new DecodeContext(PdfReadOptions.Default, new WarningLog(strict: false), encoded.Length, null));
    }

    /// <summary>An inline image's dictionary with full keys and filter names.</summary>
    private static PdfDictionary Expand(PdfDictionary parameters)
    {
        PdfDictionary full = [];
        foreach (KeyValuePair<PdfName, PdfObject> entry in parameters.Entries)
        {
            string key = InlineKeys.GetValueOrDefault(entry.Key.Value, entry.Key.Value);
            PdfObject value = entry.Value;
            if (key == "Filter")
            {
                value = value switch
                {
                    PdfName name => PdfName.Get(InlineFilters.GetValueOrDefault(name.Value, name.Value)),
                    PdfArray array => new PdfArray(array.Select(f => f is PdfName n ? PdfName.Get(InlineFilters.GetValueOrDefault(n.Value, n.Value)) : f)),
                    _ => value,
                };
            }

            full[key] = value is PdfArray or PdfDictionary ? Copy(value) : value;
        }

        return full;
    }

    private static PdfObject Copy(PdfObject value)
    {
        return value switch
        {
            PdfArray array => new PdfArray(array.Select(Copy)),
            PdfDictionary dictionary => dictionary.Clone(),
            _ => value,
        };
    }
}

/// <summary>How <see cref="PdfImage.Replace(Raster, PdfImageEncoding?)"/> stores a new image.</summary>
public sealed class PdfImageEncoding
{
    private PdfImageEncoding(int? quality)
    {
        Quality = quality;
    }

    /// <summary>Lossless: Flate with the PNG Up predictor, 8 or 16 bits as the raster has.</summary>
    public static PdfImageEncoding Flate { get; } = new(null);

    /// <summary>The JPEG quality, 1 to 100; null for Flate.</summary>
    public int? Quality { get; }

    /// <summary>JPEG (DCTDecode) at a quality from 1 to 100; alpha still goes into a Flate soft mask.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is outside 1 to 100.</exception>
    public static PdfImageEncoding Jpeg(int quality = 85)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        return new PdfImageEncoding(quality);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Quality is int q ? $"JPEG {q}" : "Flate";
    }
}

/// <summary>An image where a page draws it: the image and the matrix that maps its unit square into the page's default space.</summary>
/// <param name="Image">The image.</param>
/// <param name="Matrix">The transformation of the image's unit square to default user space when it is drawn.</param>
/// <param name="Name">The XObject's resource name, or <see langword="null"/> for an inline image.</param>
public sealed record PdfPlacedImage(PdfImage Image, PdfMatrix Matrix, string? Name);
