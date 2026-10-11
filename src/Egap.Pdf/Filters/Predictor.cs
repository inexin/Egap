namespace Egap.Pdf.Filters;

/// <summary>
/// The TIFF and PNG predictors that FlateDecode and LZWDecode may apply after decoding (ISO 32000-2, 7.4.4.4).
/// </summary>
internal static class Predictor
{
    /// <summary>Undoes the predictor named in <paramref name="parameters"/>, if any.</summary>
    public static byte[] Apply(byte[] data, PdfDictionary? parameters, DecodeContext context)
    {
        long predictor = parameters?.GetInt("Predictor") ?? 1;
        if (predictor == 1)
        {
            return data;
        }

        long colors = parameters!.GetInt("Colors") ?? 1;
        long bits = parameters.GetInt("BitsPerComponent") ?? 8;
        long columns = parameters.GetInt("Columns") ?? 1;
        if (predictor is not (2 or (>= 10 and <= 15)) || colors is < 1 or > 256 || bits is not (1 or 2 or 4 or 8 or 16) || columns < 1
            || colors * bits * columns > int.MaxValue)
        {
            context.Warn(PdfWarningCode.MalformedFilterParameters, $"Predictor {predictor} with Colors {colors}, BitsPerComponent {bits} and Columns {columns} is not valid; the data was left as decoded.");
            return data;
        }

        int rowLength = (int)(((colors * bits * columns) + 7) / 8);
        int pixelBytes = (int)Math.Max(1, colors * bits / 8);
        return predictor == 2 ? Tiff(data, rowLength, (int)colors, (int)bits) : Png(data, rowLength, pixelBytes, context);
    }

    private static byte[] Png(byte[] data, int rowLength, int pixelBytes, DecodeContext context)
    {
        // Each row is a filter type byte and rowLength bytes; a short last row is decoded as far as it goes.
        long rows = (data.Length + rowLength) / (rowLength + 1L);
        DecodeOutput output = new(context with { EncodedLength = Math.Max(context.EncodedLength, data.Length) }, (int)Math.Min(rows * rowLength, int.MaxValue));
        byte[] previous = new byte[rowLength];
        byte[] row = new byte[rowLength];
        bool warned = false;
        for (int at = 0; at < data.Length; at += rowLength + 1)
        {
            int type = data[at];
            int count = Math.Min(rowLength, data.Length - at - 1);
            if (count <= 0)
            {
                break;
            }

            Array.Clear(row);
            data.AsSpan(at + 1, count).CopyTo(row);
            switch (type)
            {
                case 0:
                    break;
                case 1:
                    for (int i = pixelBytes; i < count; i++)
                    {
                        row[i] += row[i - pixelBytes];
                    }

                    break;
                case 2:
                    for (int i = 0; i < count; i++)
                    {
                        row[i] += previous[i];
                    }

                    break;
                case 3:
                    for (int i = 0; i < count; i++)
                    {
                        int left = i >= pixelBytes ? row[i - pixelBytes] : 0;
                        row[i] += (byte)((left + previous[i]) >> 1);
                    }

                    break;
                case 4:
                    for (int i = 0; i < count; i++)
                    {
                        int left = i >= pixelBytes ? row[i - pixelBytes] : 0;
                        int upLeft = i >= pixelBytes ? previous[i - pixelBytes] : 0;
                        row[i] += Paeth(left, previous[i], upLeft);
                    }

                    break;
                default:
                    if (!warned)
                    {
                        context.Warn(PdfWarningCode.DamagedFilterData, $"A PNG predictor row has the unknown type {type}; such rows were left unpredicted.");
                        warned = true;
                    }

                    break;
            }

            output.Write(row.AsSpan(0, count));
            (previous, row) = (row, previous);
        }

        return output.ToArray();
    }

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static byte[] Tiff(byte[] data, int rowLength, int colors, int bits)
    {
        // Horizontal differencing: each component is stored as the difference from the same component of the pixel before.
        for (int start = 0; start < data.Length; start += rowLength)
        {
            Span<byte> row = data.AsSpan(start, Math.Min(rowLength, data.Length - start));
            switch (bits)
            {
                case 8:
                    for (int i = colors; i < row.Length; i++)
                    {
                        row[i] += row[i - colors];
                    }

                    break;
                case 16:
                    for (int i = colors * 2; i + 1 < row.Length; i += 2)
                    {
                        int value = ((row[i] << 8) | row[i + 1]) + ((row[i - (colors * 2)] << 8) | row[i - (colors * 2) + 1]);
                        row[i] = (byte)(value >> 8);
                        row[i + 1] = (byte)value;
                    }

                    break;
                default:
                    int mask = (1 << bits) - 1;
                    int components = row.Length * 8 / bits;
                    for (int i = colors; i < components; i++)
                    {
                        int value = (Get(row, i, bits) + Get(row, i - colors, bits)) & mask;
                        Set(row, i, bits, value);
                    }

                    break;
            }
        }

        return data;
    }

    private static int Get(Span<byte> row, int index, int bits)
    {
        int bit = index * bits;
        return (row[bit >> 3] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
    }

    private static void Set(Span<byte> row, int index, int bits, int value)
    {
        int bit = index * bits;
        int shift = 8 - bits - (bit & 7);
        int mask = ((1 << bits) - 1) << shift;
        row[bit >> 3] = (byte)((row[bit >> 3] & ~mask) | (value << shift));
    }
}
