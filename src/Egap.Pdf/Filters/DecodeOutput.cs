namespace Egap.Pdf.Filters;

/// <summary>
/// A growing output buffer for a decoder, enforcing <see cref="PdfReadOptions.MaxDecodedStreamBytes"/> and
/// <see cref="PdfReadOptions.MaxDecompressionRatio"/> as it grows, so a decompression bomb fails early and never holds much
/// more than the limit.
/// </summary>
internal sealed class DecodeOutput
{
    /// <summary>The ratio limit applies once the output is larger than this.</summary>
    public const int RatioThreshold = 1024 * 1024;

    private readonly long maxBytes;
    private readonly long maxRatioBytes;
    private byte[] buffer;
    private int length;

    public DecodeOutput(DecodeContext context, int initialCapacity)
    {
        maxBytes = Math.Min(context.Options.MaxDecodedStreamBytes, Array.MaxLength);
        maxRatioBytes = Math.Max(RatioThreshold, context.EncodedLength * context.Options.MaxDecompressionRatio);
        buffer = new byte[Math.Clamp(initialCapacity, 16, (int)Math.Min(maxBytes, 1 << 20))];
    }

    public int Length => length;

    public Span<byte> WrittenSpan => buffer.AsSpan(0, length);

    public void Write(byte b)
    {
        if (length == buffer.Length)
        {
            Grow(length + 1);
        }

        buffer[length++] = b;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > buffer.Length - length)
        {
            Grow((long)length + bytes.Length);
        }

        bytes.CopyTo(buffer.AsSpan(length));
        length += bytes.Length;
    }

    /// <summary>
    /// Space for up to <paramref name="count"/> more bytes, to be committed with <see cref="Advance"/>. It is shorter, even
    /// empty, near a limit: a decoder with more output then calls <see cref="ThrowLimit"/>.
    /// </summary>
    public Span<byte> GetSpan(int count)
    {
        long allowed = Math.Min(maxBytes, maxRatioBytes) - length;
        count = (int)Math.Min(count, allowed);
        if (count > buffer.Length - length)
        {
            Grow((long)length + count);
        }

        return buffer.AsSpan(length, count);
    }

    /// <summary>
    /// The free space left in the buffer, doubling it first when it is full, so a decoder reading in chunks fills what it has
    /// before growing. It is empty only at a limit: a decoder with more output then calls <see cref="ThrowLimit"/>.
    /// </summary>
    public Span<byte> GetAvailableSpan()
    {
        long allowed = Math.Min(maxBytes, maxRatioBytes) - length;
        if (length == buffer.Length && allowed > 0)
        {
            Grow((long)length + 1);
        }

        return buffer.AsSpan(length, (int)Math.Min(buffer.Length - length, allowed));
    }

    /// <summary>Throws the limit the output has reached, for a decoder that has more to write.</summary>
    public void ThrowLimit()
    {
        Grow((long)length + 1);
    }

    public void Advance(int count)
    {
        length += count;
    }

    public byte[] ToArray()
    {
        return buffer.AsSpan(0, length).ToArray();
    }

    private void Grow(long needed)
    {
        if (needed > maxBytes)
        {
            throw new PdfLimitException(nameof(PdfReadOptions.MaxDecodedStreamBytes), $"A stream decodes to more than {maxBytes} bytes.");
        }

        if (needed > maxRatioBytes)
        {
            throw new PdfLimitException(nameof(PdfReadOptions.MaxDecompressionRatio), $"A stream decodes to more than {maxRatioBytes} bytes, over the decompression ratio limit.");
        }

        long capacity = Math.Min(Math.Max(needed, (long)buffer.Length * 2), Math.Min(maxBytes, maxRatioBytes));
        Array.Resize(ref buffer, (int)capacity);
    }
}
