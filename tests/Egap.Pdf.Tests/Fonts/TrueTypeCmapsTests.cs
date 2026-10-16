using System.Buffers.Binary;

using Egami.Fonts;

using Egap.Pdf.Fonts;

namespace Egap.Pdf.Tests.Fonts;

public sealed class TrueTypeCmapsTests
{
    [Fact]
    public void AUnicodePlatformMapStandsInForAMissingWindowsUnicodeMap()
    {
        // pdf.js issue 5501: a subset whose only map is (0,0) format 0, reached by raw codes.
        TrueTypeCmaps maps = new(Face((0, 0)));

        Assert.Null(maps.Symbol);
        Assert.Equal((ushort)7, maps.Unicode!.GetGlyph(59));
    }

    [Fact]
    public void AWindowsUnicodeMapWinsOverAUnicodePlatformMap()
    {
        TrueTypeCmaps maps = new(Face((0, 0), (3, 1)));

        Assert.Equal((ushort)8, maps.Unicode!.GetGlyph(59));
    }

    /// <summary>The test font with its cmap replaced by format 0 subtables, one per record, mapping code 59 to glyph 7, then 8.</summary>
    private static Typeface Face(params (int Platform, int Encoding)[] records)
    {
        byte[] font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));
        return Typeface.FromMemory(ReplaceTable(font, "cmap", Cmap(records)));
    }

    private static byte[] Cmap((int Platform, int Encoding)[] records)
    {
        List<byte> data = [0, 0, 0, (byte)records.Length];
        int offset = 4 + (records.Length * 8);
        foreach ((int platform, int encoding) in records)
        {
            data.AddRange([0, (byte)platform, 0, (byte)encoding, (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset]);
            offset += 262;
        }

        for (int i = 0; i < records.Length; i++)
        {
            byte[] table = new byte[262];
            table[2] = 262 >> 8;
            table[3] = 262 & 0xFF;
            table[6 + 59] = (byte)(7 + i);
            data.AddRange(table);
        }

        return [.. data];
    }

    /// <summary>An sfnt with one table's data replaced, every table laid out again on four-byte boundaries.</summary>
    private static byte[] ReplaceTable(byte[] font, string tag, byte[] replacement)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        List<(byte[] Tag, byte[] Data)> tables = [];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = font.AsSpan(12 + (i * 16), 16);
            byte[] name = record[..4].ToArray();
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            tables.Add((name, System.Text.Encoding.ASCII.GetString(name) == tag ? replacement : font.AsSpan(offset, length).ToArray()));
        }

        int at = 12 + (count * 16);
        byte[] output = new byte[at + tables.Sum(t => (t.Data.Length + 3) & ~3)];
        font.AsSpan(0, 12).CopyTo(output);
        for (int i = 0; i < count; i++)
        {
            Span<byte> record = output.AsSpan(12 + (i * 16), 16);
            tables[i].Tag.CopyTo(record);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)tables[i].Data.Length);
            tables[i].Data.CopyTo(output.AsSpan(at));
            at += (tables[i].Data.Length + 3) & ~3;
        }

        return output;
    }
}
