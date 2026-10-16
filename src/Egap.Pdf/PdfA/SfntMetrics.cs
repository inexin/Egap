using System.Buffers.Binary;

namespace Egap.Pdf.PdfA;

/// <summary>
/// Sets glyph advances in a TrueType font file: the <c>hmtx</c> table rewritten with a full metric for every glyph and
/// <c>hhea</c>'s metric count and widest advance updated, the file reassembled with fresh checksums. A substitute font then
/// agrees with the widths the PDF gives (ISO 19005-2 6.2.11.5) and text keeps its place.
/// </summary>
internal static class SfntMetrics
{
    /// <summary>Whether the file's metrics tables can be rewritten: hhea, hmtx and maxp present and hmtx as long as they say.</summary>
    public static bool CanSetAdvances(byte[] sfnt)
    {
        try
        {
            Dictionary<string, byte[]> tables = Read(sfnt, out _);
            if (!tables.TryGetValue("hhea", out byte[]? hhea) || !tables.TryGetValue("hmtx", out byte[]? hmtx) || !tables.TryGetValue("maxp", out byte[]? maxp) || hhea.Length < 36 || maxp.Length < 6)
            {
                return false;
            }

            int glyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));
            int metrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
            return metrics > 0 && metrics <= glyphs && hmtx.Length >= (metrics * 4) + ((glyphs - metrics) * 2);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>The file with the advances of <paramref name="advances"/> (glyph to thousandths of the em); the rest unchanged.</summary>
    public static byte[] SetAdvances(byte[] sfnt, IReadOnlyDictionary<ushort, double> advances, int unitsPerEm)
    {
        Dictionary<string, byte[]> tables = Read(sfnt, out uint version);
        if (!tables.TryGetValue("hhea", out byte[]? hhea) || !tables.TryGetValue("hmtx", out byte[]? hmtx) || !tables.TryGetValue("maxp", out byte[]? maxp)
            || hhea.Length < 36 || maxp.Length < 6)
        {
            return sfnt;
        }

        int glyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));
        int metrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
        if (metrics == 0 || hmtx.Length < (metrics * 4) + ((glyphs - metrics) * 2))
        {
            return sfnt;
        }

        byte[] table = new byte[glyphs * 4];
        int widest = 0;
        for (int g = 0; g < glyphs; g++)
        {
            int advance = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(4 * Math.Min(g, metrics - 1)));
            short bearing = g < metrics
                ? BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan((4 * g) + 2))
                : BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan((4 * metrics) + (2 * (g - metrics))));
            if (advances.TryGetValue((ushort)g, out double width))
            {
                advance = (int)Math.Clamp(Math.Round(width * unitsPerEm / 1000), 0, ushort.MaxValue);
            }

            widest = Math.Max(widest, advance);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4 * g), (ushort)advance);
            BinaryPrimitives.WriteInt16BigEndian(table.AsSpan((4 * g) + 2), bearing);
        }

        byte[] header = (byte[])hhea.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)widest);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(34), (ushort)glyphs);
        tables["hhea"] = header;
        tables["hmtx"] = table;
        return Write(tables, version);
    }

    private static Dictionary<string, byte[]> Read(byte[] sfnt, out uint version)
    {
        Dictionary<string, byte[]> tables = new(StringComparer.Ordinal);
        version = BinaryPrimitives.ReadUInt32BigEndian(sfnt);
        int count = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = sfnt.AsSpan(12 + (16 * i), 16);
            string tag = System.Text.Encoding.ASCII.GetString(record[..4]);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (offset >= 0 && length >= 0 && offset <= sfnt.Length - length)
            {
                tables[tag] = sfnt.AsSpan(offset, length).ToArray();
            }
        }

        return tables;
    }

    private static byte[] Write(Dictionary<string, byte[]> tables, uint version)
    {
        string[] tags = [.. tables.Keys.Order(StringComparer.Ordinal)];
        int count = tags.Length;
        int power = 1, log = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            log++;
        }

        int offset = 12 + (16 * count);
        int size = offset + tags.Sum(t => (tables[t].Length + 3) & ~3);
        byte[] file = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(file, version);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10), (ushort)((count * 16) - (power * 16)));
        int head = -1;
        for (int i = 0; i < count; i++)
        {
            byte[] data = tables[tags[i]];
            if (tags[i] == "head" && data.Length >= 12)
            {
                data = (byte[])data.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0);
                head = offset;
            }

            Span<byte> record = file.AsSpan(12 + (16 * i), 16);
            System.Text.Encoding.ASCII.GetBytes(tags[i], record);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            data.CopyTo(file, offset);
            offset += (data.Length + 3) & ~3;
        }

        if (head >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(head + 8), unchecked(0xB1B0AFBA - Checksum(file)));
        }

        return file;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }

        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data[i..].CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }

        return sum;
    }
}
