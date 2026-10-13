using System.Buffers.Binary;
using System.Text;

namespace Egap.Pdf.Fonts;

/// <summary>
/// What PDF needs from a CFF font that Egami.Fonts keeps internal (Adobe Technical Note #5176): each glyph's name, or its
/// CID in a CID-keyed font, from the charset, and the font's built-in encoding. Only the header, the INDEXes, the Top DICT,
/// the charset and the encoding are read; a structure that does not fit its data throws <see cref="InvalidDataException"/>.
/// </summary>
internal sealed class CffInfo
{
    private const int OpCharset = 15;
    private const int OpEncoding = 16;
    private const int OpCharStrings = 17;
    private const int OpRos = 1230;

    private readonly Dictionary<string, ushort> byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ushort> byCid = [];

    private CffInfo(byte[] data)
    {
        if (data.Length < 4)
        {
            throw new InvalidDataException("The CFF data is shorter than its header.");
        }

        ReadIndex(data, data[2], out int nameEnd);
        List<(int Start, int End)> top = ReadIndex(data, nameEnd, out int topEnd);
        List<(int Start, int End)> strings = ReadIndex(data, topEnd, out _);
        if (top.Count == 0)
        {
            throw new InvalidDataException("The CFF data has no Top DICT.");
        }

        Dictionary<int, double[]> dict = ReadDict(data.AsSpan(top[0].Start, top[0].End - top[0].Start));
        IsCidKeyed = dict.ContainsKey(OpRos);
        if (!dict.TryGetValue(OpCharStrings, out double[]? charStrings) || charStrings.Length == 0)
        {
            throw new InvalidDataException("The CFF Top DICT has no CharStrings.");
        }

        GlyphCount = ReadIndex(data, (int)charStrings[0], out _).Count;
        int charsetOffset = dict.TryGetValue(OpCharset, out double[]? cs) && cs.Length > 0 ? (int)cs[0] : 0;
        ushort[] sids = ReadCharset(data, charsetOffset, GlyphCount);
        string Name(int sid)
        {
            if (CffStrings.Standard(sid) is string standard)
            {
                return standard;
            }

            int index = sid - CffStrings.StandardCount;
            return (uint)index < (uint)strings.Count ? Encoding.Latin1.GetString(data, strings[index].Start, strings[index].End - strings[index].Start) : string.Empty;
        }

        string?[] names = new string?[GlyphCount];
        for (int glyph = 0; glyph < GlyphCount; glyph++)
        {
            if (IsCidKeyed)
            {
                byCid.TryAdd(sids[glyph], (ushort)glyph);
            }
            else
            {
                names[glyph] = glyph == 0 ? ".notdef" : Name(sids[glyph]);
                byName.TryAdd(names[glyph]!, (ushort)glyph);
            }
        }

        if (!IsCidKeyed)
        {
            int encodingOffset = dict.TryGetValue(OpEncoding, out double[]? en) && en.Length > 0 ? (int)en[0] : 0;
            BuiltInEncoding = encodingOffset switch
            {
                0 => FontEncodings.Standard,
                1 => new string?[256],
                _ => ReadEncoding(data, encodingOffset, names, Name),
            };
        }
    }

    /// <summary>Whether the font is CID-keyed, so glyphs are found by CID rather than by name.</summary>
    public bool IsCidKeyed { get; }

    /// <summary>The number of glyphs.</summary>
    public int GlyphCount { get; }

    /// <summary>The glyph names of the font's own encoding by code; null for a CID-keyed font.</summary>
    public string?[]? BuiltInEncoding { get; }

    public static CffInfo Read(ReadOnlySpan<byte> data)
    {
        return new CffInfo(data.ToArray());
    }

    /// <summary>The glyph with a name, or null.</summary>
    public ushort? GlyphByName(string name)
    {
        return byName.TryGetValue(name, out ushort glyph) ? glyph : null;
    }

    /// <summary>The glyph with a CID in a CID-keyed font, or null.</summary>
    public ushort? GlyphByCid(int cid)
    {
        return byCid.TryGetValue(cid, out ushort glyph) ? glyph : null;
    }

    private static string?[] ReadEncoding(ReadOnlySpan<byte> data, int offset, string?[] names, Func<int, string> name)
    {
        string?[] encoding = new string?[256];
        Need(data, offset, 2);
        int format = data[offset];
        int at = offset + 1;
        int glyph = 1;
        switch (format & 0x7F)
        {
            case 0:
                int count = data[at++];
                Need(data, at, count);
                for (int i = 0; i < count; i++, glyph++)
                {
                    if (glyph < names.Length)
                    {
                        encoding[data[at + i]] = names[glyph];
                    }
                }

                at += count;
                break;
            case 1:
                int ranges = data[at++];
                Need(data, at, ranges * 2);
                for (int i = 0; i < ranges; i++)
                {
                    int first = data[at++];
                    int left = data[at++];
                    for (int k = 0; k <= left && first + k < 256; k++, glyph++)
                    {
                        if (glyph < names.Length)
                        {
                            encoding[first + k] = names[glyph];
                        }
                    }
                }

                break;
            default:
                throw new InvalidDataException($"The CFF encoding has format {format & 0x7F}.");
        }

        if ((format & 0x80) != 0 && at < data.Length)
        {
            // Supplements: more codes for glyphs already encoded, by SID.
            int supplements = data[at++];
            Need(data, at, supplements * 3);
            for (int i = 0; i < supplements; i++, at += 3)
            {
                encoding[data[at]] = name(BinaryPrimitives.ReadUInt16BigEndian(data[(at + 1)..]));
            }
        }

        return encoding;
    }

    private static ushort[] ReadCharset(ReadOnlySpan<byte> data, int offset, int glyphCount)
    {
        ushort[] charset = new ushort[glyphCount];
        if (offset is 0 or 1 or 2)
        {
            // Predefined: ISOAdobe gives glyph i the SID i (the expert sets are rare in PDF and read the same way).
            for (int i = 0; i < glyphCount; i++)
            {
                charset[i] = (ushort)Math.Min(i, 228);
            }

            return charset;
        }

        Need(data, offset, 1);
        int format = data[offset];
        int at = offset + 1;
        int glyph = 1;
        while (glyph < glyphCount)
        {
            switch (format)
            {
                case 0:
                    Need(data, at, 2);
                    charset[glyph++] = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                    at += 2;
                    break;
                case 1 or 2:
                    Need(data, at, format == 1 ? 3 : 4);
                    int first = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                    int left = format == 1 ? data[at + 2] : BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
                    at += format == 1 ? 3 : 4;
                    for (int k = 0; k <= left && glyph < glyphCount; k++)
                    {
                        charset[glyph++] = (ushort)(first + k);
                    }

                    break;
                default:
                    throw new InvalidDataException($"The CFF charset has format {format}.");
            }
        }

        return charset;
    }

    /// <summary>Reads a DICT's operators with their operands (two-byte operators as 1200 + the second byte).</summary>
    private static Dictionary<int, double[]> ReadDict(ReadOnlySpan<byte> data)
    {
        Dictionary<int, double[]> dict = [];
        List<double> operands = [];
        int at = 0;
        while (at < data.Length)
        {
            byte b = data[at++];
            if (b <= 21)
            {
                int op = b == 12 && at < data.Length ? 1200 + data[at++] : b;
                dict[op] = [.. operands];
                operands.Clear();
            }
            else if (b == 28)
            {
                Need(data, at, 2);
                operands.Add(BinaryPrimitives.ReadInt16BigEndian(data[at..]));
                at += 2;
            }
            else if (b == 29)
            {
                Need(data, at, 4);
                operands.Add(BinaryPrimitives.ReadInt32BigEndian(data[at..]));
                at += 4;
            }
            else if (b == 30)
            {
                // A real number: nibbles until 0xF. Its value is not needed here, only skipped.
                while (at < data.Length && (data[at] & 0x0F) != 0x0F && (data[at] >> 4) != 0x0F)
                {
                    at++;
                }

                at++;
                operands.Add(0);
            }
            else if (b is >= 32 and <= 246)
            {
                operands.Add(b - 139);
            }
            else if (b is >= 247 and <= 250)
            {
                Need(data, at, 1);
                operands.Add(((b - 247) * 256) + data[at++] + 108);
            }
            else if (b is >= 251 and <= 254)
            {
                Need(data, at, 1);
                operands.Add((-(b - 251) * 256) - data[at++] - 108);
            }
        }

        return dict;
    }

    /// <summary>An INDEX's items as ranges of the data, and where the INDEX ends.</summary>
    private static List<(int Start, int End)> ReadIndex(ReadOnlySpan<byte> data, int at, out int end)
    {
        Need(data, at, 2);
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        if (count == 0)
        {
            end = at + 2;
            return [];
        }

        Need(data, at + 2, 1);
        int size = data[at + 2];
        if (size is < 1 or > 4)
        {
            throw new InvalidDataException($"A CFF INDEX has offsets of {size} bytes.");
        }

        int offsets = at + 3;
        Need(data, offsets, (count + 1) * size);
        int baseOffset = offsets + ((count + 1) * size) - 1;

        List<(int, int)> items = new(count);
        for (int i = 0; i < count; i++)
        {
            int start = baseOffset + Offset(data, offsets, size, i), stop = baseOffset + Offset(data, offsets, size, i + 1);
            if (start < baseOffset || stop < start || stop > data.Length)
            {
                throw new InvalidDataException("A CFF INDEX item is outside the data.");
            }

            items.Add((start, stop));
        }

        end = baseOffset + Offset(data, offsets, size, count);
        return items;
    }

    private static int Offset(ReadOnlySpan<byte> data, int offsets, int size, int i)
    {
        int value = 0;
        for (int k = 0; k < size; k++)
        {
            value = (value << 8) | data[offsets + (i * size) + k];
        }

        return value;
    }

    private static void Need(ReadOnlySpan<byte> data, int at, int length)
    {
        if (at < 0 || length < 0 || at + length > data.Length)
        {
            throw new InvalidDataException("A CFF structure is outside the data.");
        }
    }
}
