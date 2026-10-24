using System.Text;

namespace Egap.Documents.Html;

/// <summary>The quoted-printable transfer encoding (RFC 2045 6.7), which the BCL has no public codec for.</summary>
internal static class QuotedPrintable
{
    private const int LineLength = 76;

    /// <summary>
    /// Decodes quoted-printable bytes: =XX octets, soft line breaks (= at a line's end) removed, white space before a line
    /// break dropped; a malformed = is kept as it is, as RFC 2045 6.7 suggests.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> encoded)
    {
        byte[] output = new byte[encoded.Length];
        int length = 0;
        int i = 0;
        while (i < encoded.Length)
        {
            byte b = encoded[i];
            if (b == (byte)'=')
            {
                if (i + 2 < encoded.Length && encoded[i + 1] == (byte)'\r' && encoded[i + 2] == (byte)'\n')
                {
                    i += 3;
                    continue;
                }

                if (i + 1 < encoded.Length && encoded[i + 1] == (byte)'\n')
                {
                    i += 2;
                    continue;
                }

                if (Hex(encoded, i + 1) is >= 0 and var octet)
                {
                    output[length++] = (byte)octet;
                    i += 3;
                    continue;
                }

                // "=" followed only by white space up to the line's end is a soft break with padding.
                int j = i + 1;
                while (j < encoded.Length && encoded[j] is (byte)' ' or (byte)'\t')
                {
                    j++;
                }

                if (j == encoded.Length || encoded[j] is (byte)'\r' or (byte)'\n')
                {
                    i = j < encoded.Length && encoded[j] == (byte)'\r' && j + 1 < encoded.Length && encoded[j + 1] == (byte)'\n' ? j + 2 : j < encoded.Length ? j + 1 : j;
                    continue;
                }

                output[length++] = b;
                i++;
                continue;
            }

            if (b is (byte)' ' or (byte)'\t')
            {
                // Trailing white space on a line was added in transport (RFC 2045 6.7 rule 3).
                int j = i;
                while (j < encoded.Length && encoded[j] is (byte)' ' or (byte)'\t')
                {
                    j++;
                }

                if (j < encoded.Length && encoded[j] is (byte)'\r' or (byte)'\n')
                {
                    i = j;
                    continue;
                }

                encoded[i..j].CopyTo(output.AsSpan(length));
                length += j - i;
                i = j;
                continue;
            }

            output[length++] = b;
            i++;
        }

        return output.AsSpan(0, length).ToArray();
    }

    private static int Hex(ReadOnlySpan<byte> bytes, int at)
    {
        if (at + 1 >= bytes.Length)
        {
            return -1;
        }

        int high = HexDigit(bytes[at]), low = HexDigit(bytes[at + 1]);
        return high < 0 || low < 0 ? -1 : (high << 4) | low;
    }

    private static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => -1,
    };

    /// <summary>
    /// Encodes text bytes as quoted-printable with CRLF line breaks: printable ASCII kept, everything else and = as =XX, line
    /// breaks in the text kept as hard breaks, lines at most 76 characters with soft breaks.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        StringBuilder output = new(bytes.Length + bytes.Length / 8);
        int column = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                continue;
            }

            if (b == (byte)'\n')
            {
                output.Append("\r\n");
                column = 0;
                continue;
            }

            bool atLineEnd = i + 1 == bytes.Length || bytes[i + 1] == (byte)'\n' || (bytes[i + 1] == (byte)'\r' && i + 2 < bytes.Length && bytes[i + 2] == (byte)'\n');
            bool literal = (b is >= 33 and <= 126 && b != (byte)'=') || (b is (byte)' ' or (byte)'\t' && !atLineEnd);
            int width = literal ? 1 : 3;
            if (column + width > LineLength - 1)
            {
                output.Append("=\r\n");
                column = 0;
            }

            if (literal)
            {
                output.Append((char)b);
            }
            else
            {
                output.Append('=').Append("0123456789ABCDEF"[b >> 4]).Append("0123456789ABCDEF"[b & 15]);
            }

            column += width;
        }

        return output.ToString();
    }
}
