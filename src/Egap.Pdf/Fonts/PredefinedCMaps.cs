using System.Collections.Concurrent;
using System.IO.Compression;

namespace Egap.Pdf.Fonts;

/// <summary>
/// Adobe's predefined CMaps (cmap-resources 20231115, BSD-3-Clause) and the CID-to-Unicode tables of the CJK character
/// collections, embedded in the compact form tools/pack-cmaps.py writes (Phase 3 design Q9). Each is inflated the first
/// time a file names it.
/// </summary>
internal static class PredefinedCMaps
{
    private static readonly Lazy<(byte[] Data, Dictionary<string, (int Offset, int Length)> Index, int Start)> Archive = new(Load);
    private static readonly ConcurrentDictionary<string, CMap?> CMaps = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (int Cid, int Length, int CodePoint)[]?> Unicode = new(StringComparer.Ordinal);

    /// <summary>Whether Egap has a predefined CMap by this name.</summary>
    public static bool Has(string name)
    {
        return Archive.Value.Index.ContainsKey(name);
    }

    /// <summary>A predefined CMap with the CMaps it uses folded in, or null for a name Adobe does not define.</summary>
    public static CMap? Get(string name)
    {
        return CMaps.GetOrAdd(name, n => Build(n, 0));
    }

    /// <summary>The Unicode code point a character collection's CID stands for (Japan1, GB1, CNS1, Korea1, KR), or null.</summary>
    public static int? CidToUnicode(string ordering, int cid)
    {
        (int Cid, int Length, int CodePoint)[]? runs = Unicode.GetOrAdd(ordering, ReadUnicode);
        if (runs is null)
        {
            return null;
        }

        int low = 0, high = runs.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            (int start, int length, int cp) = runs[mid];
            if (cid < start)
            {
                high = mid - 1;
            }
            else if (cid >= start + length)
            {
                low = mid + 1;
            }
            else
            {
                return cp + (cid - start);
            }
        }

        return null;
    }

    private static CMap? Build(string name, int depth)
    {
        if (depth > 8 || Entry(name) is not byte[] packed)
        {
            return null;
        }

        CMap map = CMap.FromPacked(packed, out string? use);
        if (use is not null && Build(use, depth + 1) is CMap parent)
        {
            map.Include(parent);
        }

        return map;
    }

    private static (int, int, int)[]? ReadUnicode(string ordering)
    {
        if (Entry("ToUnicode/" + ordering) is not byte[] packed || packed.Length == 0 || packed[0] != 1)
        {
            return null;
        }

        int at = 1;
        int count = (int)Varint(packed, ref at);
        (int, int, int)[] runs = new (int, int, int)[count];
        int cidEnd = -1, cpEnd = 0;
        for (int i = 0; i < count; i++)
        {
            int cid = cidEnd + 1 + (int)Varint(packed, ref at);
            int length = (int)Varint(packed, ref at) + 1;
            int cp = cpEnd + (int)Zigzag(Varint(packed, ref at));
            runs[i] = (cid, length, cp);
            cidEnd = cid + length - 1;
            cpEnd = cp + length - 1;
        }

        return runs;
    }

    private static byte[]? Entry(string name)
    {
        (byte[] data, Dictionary<string, (int Offset, int Length)> index, int start) = Archive.Value;
        if (!index.TryGetValue(name, out (int Offset, int Length) entry))
        {
            return null;
        }

        using ZLibStream inflate = new(new MemoryStream(data, start + entry.Offset, entry.Length), CompressionMode.Decompress);
        using MemoryStream raw = new();
        inflate.CopyTo(raw);
        return raw.ToArray();
    }

    private static (byte[], Dictionary<string, (int, int)>, int) Load()
    {
        using Stream stream = typeof(PredefinedCMaps).Assembly.GetManifestResourceStream("Egap.Pdf.Fonts.cmaps.bin")
            ?? throw new InvalidOperationException("The predefined CMaps are missing from Egap.Pdf.");
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        int at = 4;
        _ = Varint(data, ref at);
        int count = (int)Varint(data, ref at);
        Dictionary<string, (int, int)> index = new(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int length = (int)Varint(data, ref at);
            string name = System.Text.Encoding.UTF8.GetString(data, at, length);
            at += length;
            int offset = (int)Varint(data, ref at);
            int size = (int)Varint(data, ref at);
            index[name] = (offset, size);
        }

        return (data, index, at);
    }

    internal static long Varint(byte[] data, ref int at)
    {
        long value = 0;
        int shift = 0;
        while (at < data.Length)
        {
            byte b = data[at++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return value;
    }

    internal static long Zigzag(long value)
    {
        return (value >> 1) ^ -(value & 1);
    }
}
