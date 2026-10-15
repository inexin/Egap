using Egap.Pdf.Fonts;

namespace Egap.Pdf.Tests.Fonts;

public sealed class TrueTypeCmapsTests
{
    [Fact]
    public void AUnicodePlatformMapStandsInForAMissingWindowsUnicodeMap()
    {
        // pdf.js issue 5501: a subset whose only map is (0,0) format 0, reached by raw codes.
        byte[] cmap = Cmap((0, 0));
        TrueTypeCmaps maps = TrueTypeCmaps.Read(cmap);

        Assert.Null(maps.Symbol);
        Assert.Equal((ushort)7, Assert.IsType<Dictionary<int, ushort>>(maps.Unicode)[59]);
    }

    [Fact]
    public void AWindowsUnicodeMapWinsOverAUnicodePlatformMap()
    {
        byte[] cmap = Cmap((0, 0), (3, 1));
        TrueTypeCmaps maps = TrueTypeCmaps.Read(cmap);

        Assert.Equal((ushort)8, maps.Unicode![59]);
    }

    /// <summary>A cmap of format 0 subtables, one per record, mapping code 59 to glyph 7 for the first and 8 for the second.</summary>
    private static byte[] Cmap(params (int Platform, int Encoding)[] records)
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
}
