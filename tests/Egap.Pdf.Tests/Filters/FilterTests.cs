using System.IO.Compression;
using System.Text;

using Egap.Pdf.Filters;
using Egap.Pdf.IO;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Tests.Filters;

public sealed class FilterTests
{
    private static DecodeContext Context(long encodedLength = 0, PdfReadOptions? options = null, bool strict = false)
    {
        return new DecodeContext(options ?? PdfReadOptions.Default, new WarningLog(strict), encodedLength, 3);
    }

    private static byte[] Random(int count, int seed = 1)
    {
        byte[] bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Data with enough repetition to compress well.</summary>
    private static byte[] Text(int count)
    {
        StringBuilder text = new();
        Random random = new(2);
        while (text.Length < count)
        {
            text.Append(random.Next(4) switch { 0 => "BT /F1 12 Tf ", 1 => "72 712 Td ", 2 => "(Hello) Tj ", _ => "ET\n" });
        }

        return Encoding.ASCII.GetBytes(text.ToString()[..count]);
    }

    private static PdfStream Stream(PdfDictionary dictionary, byte[] encoded)
    {
        PdfStream stream = new(dictionary);
        stream.SetEncodedBytes(encoded);
        return stream;
    }

    [Fact]
    public void FlateRoundTrips()
    {
        byte[] data = Text(100_000);
        DecodeContext context = Context();

        Assert.Equal(data, FlateFilter.Decode(FlateFilter.Encode(data), context));
        Assert.Equal(0, context.Log.Count);
    }

    [Fact]
    public void FlateReadsRawDeflateWithAWarning()
    {
        byte[] data = Text(5000);
        using MemoryStream raw = new();
        using (DeflateStream deflate = new(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        DecodeContext context = Context();
        Assert.Equal(data, FlateFilter.Decode(raw.ToArray(), context));
        Assert.Equal(PdfWarningCode.DamagedFilterData, Assert.Single(context.Log.ToArray()).Code);
    }

    [Fact]
    public void FlateKeepsWhatDecodedBeforeDamage()
    {
        byte[] data = Random(200_000);
        byte[] encoded = FlateFilter.Encode(data);
        encoded[encoded.Length / 2] ^= 0xFF;
        encoded[(encoded.Length / 2) + 1] ^= 0x55;

        DecodeContext context = Context();
        byte[] decoded = FlateFilter.Decode(encoded, context);

        Assert.InRange(decoded.Length, 1, data.Length);
        Assert.True(decoded.AsSpan(0, Math.Min(decoded.Length, 50_000)).SequenceEqual(data.AsSpan(0, Math.Min(decoded.Length, 50_000))));
        Assert.Contains(context.Log.ToArray(), w => w.Code == PdfWarningCode.DamagedFilterData && w.ObjectNumber == 3);
    }

    [Fact]
    public void FlateBombHitsTheLimits()
    {
        byte[] bomb = FlateFilter.Encode(new byte[50_000_000]);

        // Flate alone compresses at most about 1,000 to 1, so a lower ratio limit shows the check.
        PdfLimitException ratio = Assert.Throws<PdfLimitException>(() => FlateFilter.Decode(bomb, Context(bomb.Length, new PdfReadOptions { MaxDecompressionRatio = 100 })));
        Assert.Equal("MaxDecompressionRatio", ratio.Limit);

        PdfReadOptions small = new() { MaxDecodedStreamBytes = 1_000_000, MaxDecompressionRatio = int.MaxValue };
        PdfLimitException size = Assert.Throws<PdfLimitException>(() => FlateFilter.Decode(bomb, Context(bomb.Length, small)));
        Assert.Equal("MaxDecodedStreamBytes", size.Limit);
    }

    [Fact]
    public void RatioLimitIgnoresSmallStreams()
    {
        byte[] zeros = FlateFilter.Encode(new byte[DecodeOutput.RatioThreshold]);

        Assert.Equal(DecodeOutput.RatioThreshold, FlateFilter.Decode(zeros, Context(zeros.Length, new PdfReadOptions { MaxDecompressionRatio = 1 })).Length);
    }

    [Fact]
    public void LzwDecodesTheSpecExample()
    {
        // ISO 32000-2, 7.4.4.2: the codes 256 45 258 258 65 259 66 257 for "-----A---B".
        byte[] encoded = [0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01];

        Assert.Equal("-----A---B"u8.ToArray(), LzwFilter.Decode(encoded, 1, Context()));
    }

    [Theory]
    [InlineData(1, 100_000)]
    [InlineData(0, 100_000)]
    [InlineData(1, 10)]
    [InlineData(1, 0)]
    public void LzwRoundTripsThroughEveryCodeWidth(int earlyChange, int size)
    {
        foreach (byte[] data in new[] { Text(size), Random(size) })
        {
            DecodeContext context = Context();
            Assert.Equal(data, LzwFilter.Decode(LzwEncoder.Encode(data, earlyChange), earlyChange, context));
            Assert.Equal(0, context.Log.Count);
        }
    }

    [Fact]
    public void LzwStopsAtAnUndefinedCode()
    {
        // 256 (clear), 65 'A', then 300, which is not defined yet.
        byte[] encoded = LzwEncoder.Pack([256, 65, 300, 257], 9);
        DecodeContext context = Context();

        Assert.Equal("A"u8.ToArray(), LzwFilter.Decode(encoded, 1, context));
        Assert.Equal(PdfWarningCode.DamagedFilterData, Assert.Single(context.Log.ToArray()).Code);
    }

    [Theory]
    [InlineData("48 65 6C\n6c 6F>", "Hello")]
    [InlineData("616>", "a`")]
    [InlineData("41424", "AB@")]
    [InlineData(">", "")]
    public void AsciiHexDecodes(string encoded, string expected)
    {
        Assert.Equal(Encoding.Latin1.GetBytes(expected), AsciiFilters.DecodeHex(Encoding.ASCII.GetBytes(encoded), Context()));
    }

    [Fact]
    public void AsciiHexSkipsBadCharacters()
    {
        DecodeContext context = Context();

        Assert.Equal("AB"u8.ToArray(), AsciiFilters.DecodeHex("41 xx 42>"u8, context));
        Assert.Equal(PdfWarningCode.DamagedFilterData, Assert.Single(context.Log.ToArray()).Code);
    }

    [Theory]
    [InlineData("9jqo^~>", "Man ")]
    [InlineData("<~9jqo^~>", "Man ")]
    [InlineData("9jqo^z~>", "Man \0\0\0\0")]
    [InlineData("9jqo\n^ 9jn~>", "Man Ma")]
    [InlineData("9jqo^9k!G", "Man Maî")]
    [InlineData("~>", "")]
    public void Ascii85Decodes(string encoded, string expected)
    {
        DecodeContext context = Context();

        Assert.Equal(Encoding.Latin1.GetBytes(expected), AsciiFilters.Decode85(Encoding.ASCII.GetBytes(encoded), context));
        Assert.Equal(0, context.Log.Count);
    }

    [Fact]
    public void Ascii85RoundTrips()
    {
        for (int size = 0; size < 40; size++)
        {
            byte[] data = Random(size, size);
            data.AsSpan(0, size / 2).Clear();
            Assert.Equal(data, AsciiFilters.Decode85(Ascii85Encoder.Encode(data), Context()));
        }
    }

    [Theory]
    [InlineData("9jqo^v~>")]
    [InlineData("9jqo^9~>")]
    [InlineData("s8W-\"~>")]
    public void Ascii85RepairsDamage(string encoded)
    {
        DecodeContext context = Context();

        AsciiFilters.Decode85(Encoding.ASCII.GetBytes(encoded), context);
        Assert.Equal(PdfWarningCode.DamagedFilterData, Assert.Single(context.Log.ToArray()).Code);
    }

    [Fact]
    public void RunLengthDecodes()
    {
        byte[] encoded = [2, (byte)'a', (byte)'b', (byte)'c', 254, (byte)'x', 0, (byte)'!', 128, (byte)'z'];

        Assert.Equal("abcxxx!"u8.ToArray(), AsciiFilters.DecodeRunLength(encoded, Context()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PngPredictorUndoesEveryRowType(int type)
    {
        const int Colors = 3;
        const int Columns = 17;
        byte[] image = Random(Colors * Columns * 9, type);
        byte[] predicted = PngEncoder.Predict(image, Colors * Columns, Colors, type);
        PdfDictionary parameters = new() { ["Predictor"] = 10 + type, ["Colors"] = Colors, ["Columns"] = Columns };

        Assert.Equal(image, Predictor.Apply(predicted, parameters, Context()));
    }

    [Fact]
    public void PngPredictorDecodesAShortLastRow()
    {
        byte[] predicted = [2, 1, 2, 3, 2, 1, 1];
        PdfDictionary parameters = new() { ["Predictor"] = 12, ["Columns"] = 3 };

        Assert.Equal(new byte[] { 1, 2, 3, 2, 3 }, Predictor.Apply(predicted, parameters, Context()));
    }

    [Theory]
    [InlineData(8, 3)]
    [InlineData(16, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(4, 1)]
    public void TiffPredictorUndoesDifferencing(int bits, int colors)
    {
        const int Columns = 10;
        int rowLength = ((colors * bits * Columns) + 7) / 8;
        byte[] image = Random(rowLength * 5, bits);
        byte[] predicted = TiffEncoder.Predict(image, rowLength, colors, bits);
        PdfDictionary parameters = new() { ["Predictor"] = 2, ["Colors"] = colors, ["BitsPerComponent"] = bits, ["Columns"] = Columns };

        Assert.Equal(image, Predictor.Apply(predicted, parameters, Context()));
    }

    [Theory]
    [InlineData(7, 1, 8, 1)]
    [InlineData(12, 0, 8, 1)]
    [InlineData(12, 1, 3, 1)]
    [InlineData(12, 1, 8, 0)]
    [InlineData(12, 1, 8, 2_000_000_000)]
    public void BadPredictorParametersLeaveTheData(int predictor, int colors, int bits, int columns)
    {
        PdfDictionary parameters = new() { ["Predictor"] = predictor, ["Colors"] = colors, ["BitsPerComponent"] = bits, ["Columns"] = columns };
        DecodeContext context = Context();

        Assert.Equal(new byte[] { 1, 2, 3 }, Predictor.Apply([1, 2, 3], parameters, context));
        Assert.Equal(PdfWarningCode.MalformedFilterParameters, Assert.Single(context.Log.ToArray()).Code);
    }

    [Fact]
    public void StreamDecodesAFilterChainWithParameters()
    {
        byte[] image = Random(4 * 20);
        byte[] flate = FlateFilter.Encode(PngEncoder.Predict(image, 4, 1, 4));
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(flate) + ">");
        PdfStream stream = Stream(
            new PdfDictionary
            {
                ["Filter"] = new PdfArray([PdfName.Get("ASCIIHexDecode"), PdfName.Get("FlateDecode")]),
                ["DecodeParms"] = new PdfArray([PdfNull.Instance, new PdfDictionary { ["Predictor"] = 15, ["Columns"] = 4 }]),
            },
            hex);

        Assert.Equal(image, stream.GetDecodedBytes());
        Assert.Equal(["ASCIIHexDecode", "FlateDecode"], stream.Filters.Select(f => f.Value));
        Assert.Null(stream.UndecodedFilter);
    }

    [Fact]
    public void ImageCodecsArePassedThrough()
    {
        PdfStream stream = Stream(new PdfDictionary { ["Filter"] = new PdfArray([PdfName.Get("A85"), PdfName.Get("DCTDecode")]) }, "9jqo^~>"u8.ToArray());

        Assert.Equal("Man "u8.ToArray(), stream.GetDecodedBytes());
        Assert.Equal("DCTDecode", stream.UndecodedFilter?.Value);
    }

    [Fact]
    public void UnfilteredStreamDecodesToItsBytes()
    {
        Assert.Equal("raw"u8.ToArray(), Stream([], "raw"u8.ToArray()).GetDecodedBytes());
    }

    [Fact]
    public void CryptFilterPassesDataThrough()
    {
        Assert.Equal("x"u8.ToArray(), Stream(new PdfDictionary { ["Filter"] = PdfName.Get("Crypt") }, "x"u8.ToArray()).GetDecodedBytes());
        PdfStream named = Stream(new PdfDictionary { ["Filter"] = PdfName.Get("Crypt"), ["DecodeParms"] = new PdfDictionary { ["Name"] = PdfName.Get("StdCF") } }, "x"u8.ToArray());

        // Decryption happens as an encrypted file's objects load, so the filter itself changes nothing.
        Assert.Equal("x"u8.ToArray(), named.GetDecodedBytes());
    }

    [Fact]
    public void UnknownFilterThrows()
    {
        PdfStream stream = Stream(new PdfDictionary { ["Filter"] = PdfName.Get("Mystery") }, [1]);

        Assert.Contains("/Mystery", Assert.Throws<PdfException>(stream.GetDecodedBytes).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetDecodedBytesCompressesAndSetsTheFilter()
    {
        byte[] data = Text(10_000);
        PdfStream stream = new(new PdfDictionary { ["DecodeParms"] = new PdfDictionary { ["Predictor"] = 12 } });

        stream.SetDecodedBytes(data);
        Assert.Equal("FlateDecode", stream.Dictionary.GetName("Filter")?.Value);
        Assert.False(stream.Dictionary.ContainsKey("DecodeParms"));
        Assert.True(stream.EncodedLength < data.Length / 4);
        Assert.Equal(data, stream.GetDecodedBytes());

        stream.SetDecodedBytes(data, compress: false);
        Assert.False(stream.Dictionary.ContainsKey("Filter"));
        Assert.Equal(data, stream.GetEncodedBytes());
    }

    [Fact]
    public void ParsedStreamDecodesWithTheFileOptionsAndLog()
    {
        byte[] flate = FlateFilter.Encode(Random(20_000));
        flate[flate.Length / 2] ^= 0xFF;
        flate[(flate.Length / 2) + 1] ^= 0x55;
        byte[] file = [.. "7 0 obj << /Filter /FlateDecode /Length "u8, .. Encoding.ASCII.GetBytes(flate.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. " >> stream\n"u8, .. flate, .. "\nendstream endobj"u8];
        WarningLog log = new(strict: false);
        ObjectParser parser = new(new Lexer(new SourceReader(PdfSource.FromMemory(file)), log), PdfReadOptions.Default, null);
        PdfStream stream = Assert.IsType<PdfStream>(parser.ParseIndirectObject(0).Value);

        Assert.InRange(stream.GetDecodedBytes().Length, 1, 19_999);
        PdfWarning warning = Assert.Single(log.ToArray());
        Assert.Equal(PdfWarningCode.DamagedFilterData, warning.Code);
        Assert.Equal(7, warning.ObjectNumber);
    }

    /// <summary>A plain LZW encoder for the tests, written from the spec's description.</summary>
    private static class LzwEncoder
    {
        public static byte[] Encode(byte[] data, int earlyChange)
        {
            List<int> codes = [];
            List<int> widths = [];
            Dictionary<(int, byte), int> table = [];
            int next = 258;

            // The decoder defines each entry one code later than the encoder, so code widths follow a model of the decoder.
            int decoderNext = 258;
            int width = 9;
            bool decoderHasPrevious = false;
            Clear();
            int current = -1;
            foreach (byte b in data)
            {
                if (current < 0)
                {
                    current = b;
                    continue;
                }

                if (table.TryGetValue((current, b), out int code))
                {
                    current = code;
                    continue;
                }

                Emit(current);
                table[(current, b)] = next++;
                if (next >= 4094)
                {
                    // Nearly full: clear the table, as encoders do.
                    Clear();
                }

                current = b;
            }

            if (current >= 0)
            {
                Emit(current);
            }

            codes.Add(257);
            widths.Add(width);
            return Pack(codes, widths);

            void Clear()
            {
                codes.Add(256);
                widths.Add(width);
                table.Clear();
                next = 258;
                decoderNext = 258;
                width = 9;
                decoderHasPrevious = false;
            }

            void Emit(int code)
            {
                codes.Add(code);
                widths.Add(width);
                if (decoderHasPrevious && decoderNext < 4096)
                {
                    decoderNext++;
                    if (decoderNext + earlyChange >= (1 << width) && width < 12)
                    {
                        width++;
                    }
                }

                decoderHasPrevious = true;
            }
        }

        public static byte[] Pack(List<int> codes, int width)
        {
            return Pack(codes, [.. Enumerable.Repeat(width, codes.Count)]);
        }

        private static byte[] Pack(List<int> codes, List<int> widths)
        {
            List<byte> bytes = [];
            long buffer = 0;
            int count = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                buffer = (buffer << widths[i]) | (uint)codes[i];
                count += widths[i];
                while (count >= 8)
                {
                    bytes.Add((byte)(buffer >> (count - 8)));
                    count -= 8;
                }
            }

            if (count > 0)
            {
                bytes.Add((byte)(buffer << (8 - count)));
            }

            return [.. bytes];
        }
    }

    private static class Ascii85Encoder
    {
        public static byte[] Encode(byte[] data)
        {
            StringBuilder text = new();
            for (int i = 0; i < data.Length; i += 4)
            {
                int count = Math.Min(4, data.Length - i);
                uint value = 0;
                for (int j = 0; j < 4; j++)
                {
                    value = (value << 8) | (j < count ? data[i + j] : 0u);
                }

                if (value == 0 && count == 4)
                {
                    text.Append('z');
                    continue;
                }

                char[] digits = new char[5];
                for (int j = 4; j >= 0; j--)
                {
                    digits[j] = (char)('!' + (value % 85));
                    value /= 85;
                }

                text.Append(digits, 0, count + 1);
            }

            return Encoding.ASCII.GetBytes(text.Append("~>").ToString());
        }
    }

    private static class PngEncoder
    {
        public static byte[] Predict(byte[] image, int rowLength, int pixelBytes, int type)
        {
            List<byte> output = [];
            byte[] previous = new byte[rowLength];
            for (int at = 0; at < image.Length; at += rowLength)
            {
                byte[] row = image.AsSpan(at, rowLength).ToArray();
                output.Add((byte)type);
                for (int i = 0; i < rowLength; i++)
                {
                    int left = i >= pixelBytes ? row[i - pixelBytes] : 0;
                    int up = previous[i];
                    int upLeft = i >= pixelBytes ? previous[i - pixelBytes] : 0;
                    int prediction = type switch
                    {
                        1 => left,
                        2 => up,
                        3 => (left + up) >> 1,
                        4 => Paeth(left, up, upLeft),
                        _ => 0,
                    };
                    output.Add((byte)(row[i] - prediction));
                }

                previous = row;
            }

            return [.. output];
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    private static class TiffEncoder
    {
        public static byte[] Predict(byte[] image, int rowLength, int colors, int bits)
        {
            byte[] output = (byte[])image.Clone();
            int components = rowLength * 8 / bits;
            int mask = (1 << bits) - 1;
            for (int at = 0; at < image.Length; at += rowLength)
            {
                for (int i = components - 1; i >= colors; i--)
                {
                    Set(output.AsSpan(at, rowLength), i, bits, (Get(image.AsSpan(at, rowLength), i, bits) - Get(image.AsSpan(at, rowLength), i - colors, bits)) & mask);
                }
            }

            return output;
        }

        private static int Get(Span<byte> row, int index, int bits)
        {
            if (bits == 16)
            {
                return (row[index * 2] << 8) | row[(index * 2) + 1];
            }

            int bit = index * bits;
            return (row[bit >> 3] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
        }

        private static void Set(Span<byte> row, int index, int bits, int value)
        {
            if (bits == 16)
            {
                row[index * 2] = (byte)(value >> 8);
                row[(index * 2) + 1] = (byte)value;
                return;
            }

            int bit = index * bits;
            int shift = 8 - bits - (bit & 7);
            int mask = ((1 << bits) - 1) << shift;
            row[bit >> 3] = (byte)((row[bit >> 3] & ~mask) | (value << shift));
        }
    }
}
