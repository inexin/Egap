namespace Egap.Pdf.IO;

/// <summary>Byte pattern searches over a <see cref="PdfSource"/>, reading it in windows.</summary>
internal static class SourceSearch
{
    private const int Window = 64 * 1024;

    /// <summary>The position of the first <paramref name="pattern"/> at or after <paramref name="from"/> and before
    /// <paramref name="limit"/>, or -1.</summary>
    public static long IndexOf(PdfSource source, ReadOnlySpan<byte> pattern, long from, long limit = long.MaxValue)
    {
        limit = Math.Min(limit, source.Length);
        byte[] buffer = new byte[Window + pattern.Length];
        for (long start = Math.Max(0, from); start + pattern.Length <= limit; start += Window)
        {
            int count = source.Read(start, buffer.AsSpan(0, (int)Math.Min(buffer.Length, limit - start)));
            int found = buffer.AsSpan(0, count).IndexOf(pattern);
            if (found >= 0)
            {
                return start + found;
            }
        }

        return -1;
    }

    /// <summary>The position of the last <paramref name="pattern"/> that ends at or before <paramref name="end"/> and starts
    /// at or after <paramref name="limit"/>, or -1.</summary>
    public static long LastIndexOf(PdfSource source, ReadOnlySpan<byte> pattern, long end, long limit = 0)
    {
        end = Math.Min(end, source.Length);
        byte[] buffer = new byte[Window + pattern.Length];
        for (long stop = end; stop - pattern.Length >= limit; stop -= Window)
        {
            long start = Math.Max(limit, stop - buffer.Length);
            int count = source.Read(start, buffer.AsSpan(0, (int)(stop - start)));
            int found = buffer.AsSpan(0, count).LastIndexOf(pattern);
            if (found >= 0)
            {
                return start + found;
            }
        }

        return -1;
    }
}
