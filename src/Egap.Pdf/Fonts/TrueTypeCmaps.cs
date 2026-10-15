using System.Buffers.Binary;

namespace Egap.Pdf.Fonts;

/// <summary>
/// The character maps of a TrueType font a simple PDF font reaches its glyphs through (9.6.5.4): the Windows symbol map
/// (3,0), the Windows Unicode map (3,1) or else a Unicode-platform map (0,*), and the Macintosh Roman map (1,0), each read whole from the <c>cmap</c> table as
/// Egami.Fonts only answers Unicode lookups. Formats 0, 4, 6 and 12 are read; a subtable that does not fit is skipped.
/// </summary>
internal sealed class TrueTypeCmaps
{
    /// <summary>The most mappings read from one subtable, against huge format 12 groups.</summary>
    private const int MaxMappings = 1 << 18;

    private TrueTypeCmaps()
    {
    }

    /// <summary>The (3,0) map, codes to glyphs, or null.</summary>
    public Dictionary<int, ushort>? Symbol { get; private set; }

    /// <summary>The (3,1) or (3,10) map, Unicode to glyphs, or else a (0,*) map; null when the font has neither.</summary>
    public Dictionary<int, ushort>? Unicode { get; private set; }

    /// <summary>The (1,0) map, Mac Roman codes to glyphs, or null.</summary>
    public Dictionary<int, ushort>? MacRoman { get; private set; }

    public static TrueTypeCmaps Read(ReadOnlySpan<byte> cmap)
    {
        TrueTypeCmaps maps = new();
        if (cmap.Length < 4)
        {
            return maps;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(cmap[2..]);
        Dictionary<int, ushort>? platformUnicode = null;
        for (int i = 0; i < count && 4 + (i * 8) + 8 <= cmap.Length; i++)
        {
            ReadOnlySpan<byte> record = cmap.Slice(4 + (i * 8), 8);
            int platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            int encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
            if (offset >= cmap.Length)
            {
                continue;
            }

            Dictionary<int, ushort>? map = (platform, encoding) switch
            {
                (3, 0) when maps.Symbol is null => Subtable(cmap[(int)offset..]),
                (3, 1 or 10) when maps.Unicode is null || encoding == 10 => Subtable(cmap[(int)offset..]),
                (1, 0) when maps.MacRoman is null => Subtable(cmap[(int)offset..]),
                (0, _) when platformUnicode is null => Subtable(cmap[(int)offset..]),
                _ => null,
            };
            if (map is null)
            {
                continue;
            }

            switch (platform, encoding)
            {
                case (3, 0):
                    maps.Symbol = map;
                    break;
                case (3, _):
                    maps.Unicode = map;
                    break;
                case (0, _):
                    platformUnicode = map;
                    break;
                default:
                    maps.MacRoman = map;
                    break;
            }
        }

        // A Unicode-platform map stands in for (3,1) when the font has none, as pdf.js reads it.
        maps.Unicode ??= platformUnicode;
        return maps;
    }

    private static Dictionary<int, ushort>? Subtable(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            return null;
        }

        Dictionary<int, ushort> map = [];
        switch (BinaryPrimitives.ReadUInt16BigEndian(data))
        {
            case 0 when data.Length >= 262:
                for (int code = 0; code < 256; code++)
                {
                    if (data[6 + code] != 0)
                    {
                        map[code] = data[6 + code];
                    }
                }

                break;
            case 4:
                ReadFormat4(data, map);
                break;
            case 6 when data.Length >= 10:
                int first = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
                int count = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
                for (int i = 0; i < count && 10 + (i * 2) + 2 <= data.Length; i++)
                {
                    ushort glyph = BinaryPrimitives.ReadUInt16BigEndian(data[(10 + (i * 2))..]);
                    if (glyph != 0)
                    {
                        map[first + i] = glyph;
                    }
                }

                break;
            case 12 when data.Length >= 16:
                uint groups = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
                for (uint g = 0; g < groups && 16 + ((g + 1) * 12) <= data.Length && map.Count < MaxMappings; g++)
                {
                    ReadOnlySpan<byte> group = data.Slice((int)(16 + (g * 12)), 12);
                    uint start = BinaryPrimitives.ReadUInt32BigEndian(group), end = BinaryPrimitives.ReadUInt32BigEndian(group[4..]);
                    uint glyph = BinaryPrimitives.ReadUInt32BigEndian(group[8..]);
                    for (uint c = start; c <= end && c <= 0x10FFFF && map.Count < MaxMappings; c++)
                    {
                        map[(int)c] = (ushort)(glyph + (c - start));
                    }
                }

                break;
            default:
                return null;
        }

        return map;
    }

    private static void ReadFormat4(ReadOnlySpan<byte> data, Dictionary<int, ushort> map)
    {
        if (data.Length < 14)
        {
            return;
        }

        int segments = BinaryPrimitives.ReadUInt16BigEndian(data[6..]) / 2;
        int ends = 14, starts = ends + (segments * 2) + 2, deltas = starts + (segments * 2), offsets = deltas + (segments * 2);
        if (offsets + (segments * 2) > data.Length)
        {
            return;
        }

        for (int s = 0; s < segments; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(data[(ends + (s * 2))..]);
            int start = BinaryPrimitives.ReadUInt16BigEndian(data[(starts + (s * 2))..]);
            int delta = BinaryPrimitives.ReadInt16BigEndian(data[(deltas + (s * 2))..]);
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(data[(offsets + (s * 2))..]);
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    int at = offsets + (s * 2) + rangeOffset + ((c - start) * 2);
                    if (at + 2 > data.Length)
                    {
                        break;
                    }

                    glyph = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                    glyph = glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
                }

                if (glyph != 0)
                {
                    map[c] = (ushort)glyph;
                }
            }
        }
    }
}
