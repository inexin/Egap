namespace Egap.Pdf.Filters;

/// <summary>LZWDecode (ISO 32000-2, 7.4.4): variable-length codes of 9 to 12 bits, most significant bit first.</summary>
internal static class LzwFilter
{
    private const int Clear = 256;
    private const int EndOfData = 257;
    private const int MaxCodes = 4096;

    /// <param name="input">The encoded bytes.</param>
    /// <param name="earlyChange">1 (the default) to widen codes one code early, 0 to widen them on time.</param>
    /// <param name="context">The limits and the warning log.</param>
    public static byte[] Decode(ReadOnlySpan<byte> input, int earlyChange, DecodeContext context)
    {
        DecodeOutput output = new(context, input.Length * 3);
        int[] prefix = new int[MaxCodes];
        byte[] suffix = new byte[MaxCodes];
        int[] lengths = new int[MaxCodes];
        byte[] sequence = new byte[MaxCodes];
        for (int i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            lengths[i] = 1;
        }

        int next = 258;
        int width = 9;
        int previous = -1;
        int bitBuffer = 0;
        int bitCount = 0;
        int position = 0;
        while (true)
        {
            while (bitCount < width && position < input.Length)
            {
                bitBuffer = (bitBuffer << 8) | input[position++];
                bitCount += 8;
            }

            if (bitCount < width)
            {
                // The data ends without an end-of-data code, which many files do.
                break;
            }

            int code = (bitBuffer >> (bitCount - width)) & ((1 << width) - 1);
            bitCount -= width;
            bitBuffer &= (1 << bitCount) - 1;
            if (code == Clear)
            {
                next = 258;
                width = 9;
                previous = -1;
                continue;
            }

            if (code == EndOfData)
            {
                break;
            }

            int first;
            if (code < next && (code < 256 || code > EndOfData))
            {
                int length = Expand(code, prefix, suffix, lengths, sequence);
                output.Write(sequence.AsSpan(0, length));
                first = sequence[0];
            }
            else if (code == next && previous >= 0)
            {
                // The code being defined: the previous sequence plus its own first byte.
                int length = Expand(previous, prefix, suffix, lengths, sequence);
                sequence[length] = sequence[0];
                output.Write(sequence.AsSpan(0, length + 1));
                first = sequence[0];
            }
            else
            {
                context.Warn(PdfWarningCode.DamagedFilterData, $"LZW data holds the undefined code {code}; the {output.Length} bytes decoded before it were kept.");
                break;
            }

            if (previous >= 0 && next < MaxCodes)
            {
                prefix[next] = previous;
                suffix[next] = (byte)first;
                lengths[next] = lengths[previous] + 1;
                next++;
                if (next + earlyChange >= (1 << width) && width < 12)
                {
                    width++;
                }
            }

            previous = code;
        }

        return output.ToArray();
    }

    /// <summary>Writes the bytes of <paramref name="code"/> into <paramref name="sequence"/>; returns their count.</summary>
    private static int Expand(int code, int[] prefix, byte[] suffix, int[] lengths, byte[] sequence)
    {
        int length = lengths[code];
        for (int i = length - 1; i >= 0; i--)
        {
            sequence[i] = suffix[code];
            code = prefix[code];
        }

        return length;
    }
}
