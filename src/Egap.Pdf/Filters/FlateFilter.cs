using System.IO.Compression;

namespace Egap.Pdf.Filters;

/// <summary>
/// FlateDecode (ISO 32000-2, 7.4.4) with .NET's zlib. Data with a broken zlib header is read as raw Deflate, and data that
/// breaks off keeps what decoded before the break, with a warning, as other readers do.
/// </summary>
internal static class FlateFilter
{
    public static byte[] Decode(ReadOnlySpan<byte> input, DecodeContext context)
    {
        byte[] data = input.ToArray();
        bool header = HasZlibHeader(data);
        (DecodeOutput output, bool damaged) = Inflate(data, zlib: header, context);
        if (!header || (damaged && output.Length == 0))
        {
            // No zlib header, or nothing came out: try raw Deflate, with and without skipping a two-byte header, and keep the best.
            foreach (int skip in header ? [0, 2] : (int[])[0])
            {
                (DecodeOutput raw, bool rawDamaged) = Inflate(data.AsSpan(skip).ToArray(), zlib: false, context);
                if (raw.Length > output.Length || (raw.Length == output.Length && damaged && !rawDamaged))
                {
                    (output, damaged) = (raw, rawDamaged);
                }
            }

            if (!header)
            {
                context.Warn(PdfWarningCode.DamagedFilterData, "Flate data has no valid zlib header; it was read as raw Deflate.");
            }
        }

        if (damaged)
        {
            context.Warn(PdfWarningCode.DamagedFilterData, $"Flate data is damaged; the {output.Length} bytes decoded before the damage were kept.");
        }

        return output.ToArray();
    }

    /// <summary>Compresses with zlib (Flate) at the optimal level.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        using MemoryStream result = new();
        using (ZLibStream zlib = new(result, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return result.ToArray();
    }

    private static bool HasZlibHeader(ReadOnlySpan<byte> data)
    {
        // RFC 1950: method 8 (deflate), a window of at most 32 KiB, and a header that is a multiple of 31; no preset dictionary.
        return data.Length >= 2 && (data[0] & 0x0F) == 8 && (data[0] >> 4) <= 7 && ((data[0] << 8) | data[1]) % 31 == 0 && (data[1] & 0x20) == 0;
    }

    private static (DecodeOutput Output, bool Damaged) Inflate(byte[] data, bool zlib, DecodeContext context)
    {
        DecodeOutput output = new(context, data.Length * 4);
        using MemoryStream source = new(data, writable: false);
        using Stream inflater = zlib ? new ZLibStream(source, CompressionMode.Decompress) : new DeflateStream(source, CompressionMode.Decompress);
        try
        {
            while (true)
            {
                Span<byte> span = output.GetSpan(16 * 1024);
                if (span.IsEmpty)
                {
                    // At a limit: fine if the data ends here, otherwise the limit is exceeded.
                    Span<byte> one = stackalloc byte[1];
                    if (inflater.Read(one) > 0)
                    {
                        output.ThrowLimit();
                    }

                    return (output, false);
                }

                int read = inflater.Read(span);
                if (read == 0)
                {
                    return (output, false);
                }

                output.Advance(read);
            }
        }
        catch (InvalidDataException)
        {
            return (output, true);
        }
    }
}
