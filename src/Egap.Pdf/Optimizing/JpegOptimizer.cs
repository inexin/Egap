using Egami;
using Egami.Formats.Jpeg;

namespace Egap.Pdf.Optimizing;

/// <summary>
/// Existing JPEG images (Phase 7 design Q5, Build Plan task 5). Re-encoding a JPEG at its size always loses quality, so one
/// that is not downsampled is decoded only when its quality, estimated from its quantisation tables, is clearly above the
/// preset's (10 or more points); then it is written at the preset's quality and kept when 5% smaller. Every other JPEG gets
/// Egami's lossless Huffman optimisation, which keeps every coefficient, and is kept whenever that is smaller. Downsampled
/// JPEGs are <see cref="ImageRecompressor"/>'s.
/// </summary>
internal static class JpegOptimizer
{
    /// <summary>How far above the preset's quality a JPEG must be estimated before it is re-encoded.</summary>
    internal const int ClearlyAbove = 10;

    /// <summary>Optimizes one JPEG image at its size; the change, or null when it was left as it was.</summary>
    public static ImageChange? Optimize(SurveyedImage surveyed, ImageSettings settings, Func<ImageChange?> reencode)
    {
        PdfImage image = surveyed.Image;
        if (image.Stream is not PdfStream stream || image.ImageCodec?.Value != "DCTDecode")
        {
            return null;
        }

        byte[]? optimized;
        if (settings.Prepared is { } prepared && prepared.TryGetValue(stream, out byte[]? ready))
        {
            optimized = ready;
        }
        else
        {
            byte[] jpeg;
            try
            {
                jpeg = image.DecodeFilters().Data;
            }
            catch (PdfException)
            {
                return null;
            }

            if (settings.Lossy && Quality(jpeg) is int quality && quality >= settings.JpegQuality + ClearlyAbove && reencode() is ImageChange change)
            {
                return change;
            }

            optimized = Huffman(jpeg);
        }

        long before = stream.EncodedLength;
        if (optimized is null || optimized.Length >= before)
        {
            return null;
        }

        ImageChange result = new(stream, image.Width, image.Height, image.Width, image.Height, before, optimized.Length,
            surveyed.EffectiveResolution, surveyed.EffectiveResolution, "JPEG (Huffman optimized)");
        stream.Dictionary.Remove("DecodeParms");
        stream.Dictionary["Filter"] = PdfName.Get("DCTDecode");
        stream.SetEncodedBytes(optimized);
        return result;
    }

    /// <summary>
    /// Under lossless settings, where the Huffman pass is all a JPEG kept at its size can get, runs that pass for every such
    /// JPEG at once, on as many cores as there are, ahead of <see cref="Optimize"/>: by stream, the result or null. The passes
    /// share nothing; reading the streams stays on this thread.
    /// </summary>
    public static Dictionary<PdfStream, byte[]?> Prepare(IReadOnlyList<SurveyedImage> images, ImageSettings settings)
    {
        Dictionary<PdfStream, byte[]?> prepared = new(ReferenceEqualityComparer.Instance);
        if (settings.Lossy)
        {
            return prepared;
        }

        List<(PdfStream Stream, byte[] Jpeg)> work = [];
        HashSet<PdfStream> seen = new(ReferenceEqualityComparer.Instance);
        foreach (SurveyedImage surveyed in images)
        {
            PdfImage image = surveyed.Image;
            (int width, int height) = ImageRecompressor.TargetSize(surveyed, settings);
            if (image.Stream is not PdfStream stream || image.ImageCodec?.Value != "DCTDecode" || width < image.Width || height < image.Height
                || !seen.Add(stream))
            {
                continue;
            }

            try
            {
                work.Add((stream, image.DecodeFilters().Data));
            }
            catch (PdfException)
            {
            }
        }

        byte[]?[] results = new byte[work.Count][];
        Parallel.For(0, work.Count, i => results[i] = Huffman(work[i].Jpeg));
        for (int i = 0; i < work.Count; i++)
        {
            prepared[work[i].Stream] = results[i];
        }

        return prepared;
    }

    /// <summary>The JPEG with optimal Huffman tables and its coefficients as they were; null when Egami cannot transform it.</summary>
    internal static byte[]? Huffman(byte[] jpeg)
    {
        int start = jpeg.AsSpan().IndexOf((byte[])[0xFF, 0xD8, 0xFF]);
        if (start < 0)
        {
            return null;
        }

        try
        {
            using MemoryStream source = new(jpeg, start, jpeg.Length - start, writable: false);
            using MemoryStream result = new();
            JpegTransform.Apply(source, result, new JpegTransformOptions { OptimizeHuffman = true, CopyMetadata = false });
            return result.ToArray();
        }
        catch (Exception e) when (e is EgamiException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The IJG quality (1 to 100) the JPEG's first quantisation table matches best, from how its sum compares with the
    /// standard luminance table's (ITU-T T.81 annex K, scaled as the IJG library scales it); null without a table.
    /// </summary>
    internal static int? Quality(ReadOnlySpan<byte> jpeg)
    {
        int at = jpeg.IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8]);
        if (at < 0)
        {
            return null;
        }

        at += 2;
        while (at + 4 <= jpeg.Length)
        {
            if (jpeg[at] != 0xFF)
            {
                at++;
                continue;
            }

            byte marker = jpeg[at + 1];
            if (marker is 0xFF or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += marker == 0xFF ? 1 : 2;
                continue;
            }

            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (marker == 0xDA || length < 2)
            {
                return null;
            }

            if (marker == 0xDB && at + 5 + 64 <= jpeg.Length)
            {
                int precision = jpeg[at + 4] >> 4;
                long sum = 0;
                for (int i = 0; i < 64; i++)
                {
                    sum += precision == 0 ? jpeg[at + 5 + i] : (jpeg[at + 5 + (2 * i)] << 8) | jpeg[at + 6 + (2 * i)];
                }

                double scale = 100.0 * sum / StandardLuminanceSum;
                int quality = scale <= 100 ? (int)Math.Round((200 - scale) / 2) : (int)Math.Round(5000 / scale);
                return Math.Clamp(quality, 1, 100);
            }

            at += 2 + length;
        }

        return null;
    }

    // The sum of ITU-T T.81 table K.1, the luminance quantisation table the IJG library scales by quality.
    private const long StandardLuminanceSum = 16 + 11 + 10 + 16 + 24 + 40 + 51 + 61 + 12 + 12 + 14 + 19 + 26 + 58 + 60 + 55
        + 14 + 13 + 16 + 24 + 40 + 57 + 69 + 56 + 14 + 17 + 22 + 29 + 51 + 87 + 80 + 62 + 18 + 22 + 37 + 56 + 68 + 109 + 103
        + 77 + 24 + 35 + 55 + 64 + 81 + 104 + 113 + 92 + 49 + 64 + 78 + 87 + 103 + 121 + 120 + 101 + 72 + 92 + 95 + 98 + 112
        + 100 + 103 + 99;
}
