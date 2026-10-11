using Egap.Pdf.Parsing;

namespace Egap.Pdf.Filters;

/// <summary>ASCIIHexDecode, ASCII85Decode and RunLengthDecode (ISO 32000-2, 7.4.2, 7.4.3 and 7.4.5).</summary>
internal static class AsciiFilters
{
    public static byte[] DecodeHex(ReadOnlySpan<byte> input, DecodeContext context)
    {
        DecodeOutput output = new(context, (input.Length / 2) + 1);
        int high = -1;
        bool warned = false;
        foreach (byte b in input)
        {
            if (b == '>')
            {
                break;
            }

            int value = CharClass.HexValue(b);
            if (value < 0)
            {
                if (!CharClass.IsWhiteSpace(b) && !warned)
                {
                    context.Warn(PdfWarningCode.DamagedFilterData, $"ASCIIHex data holds the character 0x{b:X2}, which was skipped.");
                    warned = true;
                }

                continue;
            }

            if (high < 0)
            {
                high = value;
            }
            else
            {
                output.Write((byte)((high << 4) | value));
                high = -1;
            }
        }

        // An odd final digit is taken as followed by 0.
        if (high >= 0)
        {
            output.Write((byte)(high << 4));
        }

        return output.ToArray();
    }

    public static byte[] Decode85(ReadOnlySpan<byte> input, DecodeContext context)
    {
        DecodeOutput output = new(context, (input.Length * 4 / 5) + 4);
        Span<int> group = stackalloc int[5];
        int count = 0;
        bool warned = false;
        int start = 0;
        while (start < input.Length && CharClass.IsWhiteSpace(input[start]))
        {
            start++;
        }

        if (input[start..].StartsWith("<~"u8))
        {
            // Some producers keep PostScript's opening delimiter.
            start += 2;
        }

        foreach (byte b in input[start..])
        {
            if (b == '~')
            {
                break;
            }

            if (b == 'z' && count == 0)
            {
                output.Write([0, 0, 0, 0]);
            }
            else if (b is >= (byte)'!' and <= (byte)'u')
            {
                group[count++] = b - '!';
                if (count == 5)
                {
                    WriteGroup(group, 4, output, context, ref warned);
                    count = 0;
                }
            }
            else if (!CharClass.IsWhiteSpace(b) && !warned)
            {
                context.Warn(PdfWarningCode.DamagedFilterData, $"ASCII85 data holds the character 0x{b:X2}, which was skipped.");
                warned = true;
            }
        }

        if (count == 1)
        {
            context.Warn(PdfWarningCode.DamagedFilterData, "ASCII85 data ends with a single character, which was dropped.");
        }
        else if (count > 1)
        {
            // A final partial group of n characters stands for n - 1 bytes, padded with 'u'.
            for (int i = count; i < 5; i++)
            {
                group[i] = 84;
            }

            WriteGroup(group, count - 1, output, context, ref warned);
        }

        return output.ToArray();
    }

    private static void WriteGroup(Span<int> group, int bytes, DecodeOutput output, DecodeContext context, ref bool warned)
    {
        long value = 0;
        foreach (int digit in group)
        {
            value = (value * 85) + digit;
        }

        if (value > uint.MaxValue && !warned)
        {
            context.Warn(PdfWarningCode.DamagedFilterData, "An ASCII85 group is larger than 32 bits; its low 32 bits were kept.");
            warned = true;
        }

        Span<byte> four = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        output.Write(four[..bytes]);
    }

    public static byte[] DecodeRunLength(ReadOnlySpan<byte> input, DecodeContext context)
    {
        DecodeOutput output = new(context, input.Length * 2);
        int i = 0;
        while (i < input.Length)
        {
            int length = input[i++];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                int count = Math.Min(length + 1, input.Length - i);
                output.Write(input.Slice(i, count));
                i += count;
                if (count < length + 1)
                {
                    context.Warn(PdfWarningCode.DamagedFilterData, "RunLength data ends inside a literal run.");
                }
            }
            else if (i < input.Length)
            {
                Span<byte> run = output.GetSpan(257 - length);
                if (run.Length < 257 - length)
                {
                    output.ThrowLimit();
                }

                run.Fill(input[i++]);
                output.Advance(run.Length);
            }
        }

        return output.ToArray();
    }
}
