using System.Buffers.Binary;
using System.Text;

namespace Egap.Documents.Office;

/// <summary>
/// Writes a compound file ([MS-CFB]) of version 3, with 512-byte sectors: streams under 4096 bytes go into the mini stream,
/// storages are made from the '/'-separated paths, and each storage's children form a red-black tree in [MS-CFB] 2.6.4's
/// order. Enough to hold an encrypted Office package; the file's times and class ids are left zero.
/// </summary>
internal static class CompoundFileWriter
{
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int EntrySize = 128;
    private const int MiniStreamCutoff = 4096;
    private const int PerSector = SectorSize / 4;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint FatSectorMark = 0xFFFFFFFD;
    private const uint DifatSectorMark = 0xFFFFFFFC;
    private const uint NoStream = 0xFFFFFFFF;

    /// <summary>Writes the streams, each named by its path, to <paramref name="output"/>.</summary>
    public static void Write(Stream output, IReadOnlyList<(string Path, byte[] Data)> streams)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(streams);

        // The directory tree: the root, then storages and streams as their paths name them.
        List<Node> nodes = [new Node("Root Entry", CompoundFileEntryType.Root, null)];
        foreach ((string path, byte[] bytes) in streams)
        {
            Node parent = nodes[0];
            string[] names = path.Split('/');
            for (int i = 0; i < names.Length - 1; i++)
            {
                Node? storage = parent.Children.Find(c => c.Type == CompoundFileEntryType.Storage && Same(c.Name, names[i]));
                if (storage is null)
                {
                    storage = new Node(names[i], CompoundFileEntryType.Storage, null);
                    parent.Children.Add(storage);
                    nodes.Add(storage);
                }

                parent = storage;
            }

            Node stream = new(names[^1], CompoundFileEntryType.Stream, bytes);
            parent.Children.Add(stream);
            nodes.Add(stream);
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            nodes[i].Id = i;
            if (nodes[i].Name.Length is 0 or > 31)
            {
                throw new ArgumentException($"A compound file name has 1 to 31 characters; \"{nodes[i].Name}\" does not.", nameof(streams));
            }
        }

        // The mini stream: the small streams one after another, each in whole 64-byte mini sectors.
        List<uint> miniFat = [];
        using MemoryStream mini = new();
        foreach (Node node in nodes)
        {
            if (node.Type == CompoundFileEntryType.Stream && node.Data!.Length is > 0 and < MiniStreamCutoff)
            {
                int count = (node.Data.Length + MiniSectorSize - 1) / MiniSectorSize;
                node.Start = (uint)miniFat.Count;
                for (int i = 0; i < count; i++)
                {
                    miniFat.Add(i == count - 1 ? EndOfChain : (uint)(miniFat.Count + 1));
                }

                mini.Write(node.Data);
                mini.Write(new byte[(count * MiniSectorSize) - node.Data.Length]);
            }
        }

        // The regular sectors, in order: the FAT, the DIFAT, the directory, the mini FAT, the mini stream, then the large
        // streams, so a reader finds the directory in the file's first sectors.
        byte[] miniBytes = mini.ToArray();
        byte[] miniFatBytes = new byte[((miniFat.Count + PerSector - 1) / PerSector) * SectorSize];
        miniFatBytes.AsSpan().Fill(0xFF);
        for (int i = 0; i < miniFat.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(4 * i), miniFat[i]);
        }

        int perDirectorySector = SectorSize / EntrySize;
        byte[] directory = new byte[((nodes.Count + perDirectorySector - 1) / perDirectorySector) * SectorSize];
        long dataSectors = Sectors(directory.Length) + Sectors(miniFatBytes.Length) + Sectors(miniBytes.Length);
        foreach (Node node in nodes)
        {
            if (node.Type == CompoundFileEntryType.Stream && node.Data!.Length >= MiniStreamCutoff)
            {
                dataSectors += Sectors(node.Data.Length);
            }
        }

        // The FAT has to name its own sectors and the DIFAT's too.
        int fatSectors = 0, difatSectors = 0;
        while (true)
        {
            long total = dataSectors + fatSectors + difatSectors;
            int needFat = (int)((total + PerSector - 1) / PerSector);
            int needDifat = needFat > 109 ? (needFat - 109 + PerSector - 2) / (PerSector - 1) : 0;
            if (needFat == fatSectors && needDifat == difatSectors)
            {
                break;
            }

            fatSectors = needFat;
            difatSectors = needDifat;
        }

        List<uint> fat = [];
        for (int i = 0; i < fatSectors; i++)
        {
            fat.Add(FatSectorMark);
        }

        for (int i = 0; i < difatSectors; i++)
        {
            fat.Add(DifatSectorMark);
        }

        List<byte[]> data = [];
        uint Place(byte[] bytes)
        {
            int count = Sectors(bytes.Length);
            if (count == 0)
            {
                return EndOfChain;
            }

            uint start = (uint)fat.Count;
            for (int i = 0; i < count; i++)
            {
                fat.Add(i == count - 1 ? EndOfChain : (uint)(fat.Count + 1));
            }

            data.Add(bytes);
            return start;
        }

        uint directoryStart = Place(directory);
        uint miniFatStart = Place(miniFatBytes);
        nodes[0].Start = Place(miniBytes);
        nodes[0].Size = miniBytes.Length;
        foreach (Node node in nodes)
        {
            if (node.Type == CompoundFileEntryType.Stream && node.Data!.Length >= MiniStreamCutoff)
            {
                node.Start = Place(node.Data);
            }
        }

        FillDirectory(nodes, directory);

        byte[] header = new byte[SectorSize];
        Span<byte> h = header;
        ((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]).CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x18..], 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1A..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1C..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1E..], 9);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x20..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x2C..], (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x30..], directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x38..], MiniStreamCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x3C..], miniFat.Count == 0 ? EndOfChain : miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x40..], (uint)(miniFatBytes.Length / SectorSize));
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x44..], difatSectors == 0 ? EndOfChain : (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x48..], (uint)difatSectors);
        for (int i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[(0x4C + (4 * i))..], i < fatSectors ? (uint)i : FreeSector);
        }

        output.Write(header);
        byte[] sector = new byte[SectorSize];
        for (int s = 0; s < fatSectors; s++)
        {
            for (int i = 0; i < PerSector; i++)
            {
                int at = (s * PerSector) + i;
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(4 * i), at < fat.Count ? fat[at] : FreeSector);
            }

            output.Write(sector);
        }

        // Each DIFAT sector lists 127 FAT sectors after the header's 109 and ends with the next DIFAT sector.
        for (int s = 0; s < difatSectors; s++)
        {
            for (int i = 0; i < PerSector - 1; i++)
            {
                int fatIndex = 109 + (s * (PerSector - 1)) + i;
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(4 * i), fatIndex < fatSectors ? (uint)fatIndex : FreeSector);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(4 * (PerSector - 1)), s == difatSectors - 1 ? EndOfChain : (uint)(fatSectors + s + 1));
            output.Write(sector);
        }

        foreach (byte[] bytes in data)
        {
            output.Write(bytes);
            output.Write(new byte[(SectorSize - (bytes.Length % SectorSize)) % SectorSize]);
        }
    }

    private static int Sectors(long length) => (int)((length + SectorSize - 1) / SectorSize);

    // The directory sectors: every node's entry, its children's tree linked in, unused slots empty.
    private static void FillDirectory(List<Node> nodes, byte[] bytes)
    {
        foreach (Node node in nodes)
        {
            node.Children.Sort((a, b) => Compare(a.Name, b.Name));
            node.Child = Tree(node.Children, 0, node.Children.Count - 1, 0, MinNullDepth(node.Children.Count));
        }

        for (int i = 0; i < bytes.Length / EntrySize; i++)
        {
            Span<byte> entry = bytes.AsSpan(i * EntrySize, EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x44..], NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x48..], NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4C..], NoStream);
            if (i >= nodes.Count)
            {
                continue;
            }

            Node node = nodes[i];
            int written = Encoding.Unicode.GetBytes(node.Name, entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[0x40..], (ushort)(written + 2));
            entry[0x42] = (byte)node.Type;
            entry[0x43] = node.Red ? (byte)0 : (byte)1;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x44..], node.Left?.Id is int left ? (uint)left : NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x48..], node.Right?.Id is int right ? (uint)right : NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4C..], node.Child?.Id is int child ? (uint)child : NoStream);
            if (node.Type == CompoundFileEntryType.Storage)
            {
                continue;
            }

            long size = node.Type == CompoundFileEntryType.Root ? node.Size : node.Data!.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x74..], size == 0 ? EndOfChain : node.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0x78..], (ulong)size);
        }
    }

    // A balanced tree of the sorted siblings. The nodes deeper than the shallowest missing child are red, so every path
    // from the top to a missing child passes the same number of black nodes and no red node has a red parent.
    private static Node? Tree(List<Node> sorted, int low, int high, int depth, int redDepth)
    {
        if (low > high)
        {
            return null;
        }

        int middle = (low + high + 1) / 2;
        Node node = sorted[middle];
        node.Red = depth >= redDepth;
        node.Left = Tree(sorted, low, middle - 1, depth + 1, redDepth);
        node.Right = Tree(sorted, middle + 1, high, depth + 1, redDepth);
        return node;
    }

    // How deep the shallowest missing child of a balanced tree of n nodes sits: floor(log2(n + 1)).
    private static int MinNullDepth(int count)
    {
        int depth = 0;
        while ((2 << depth) - 1 <= count)
        {
            depth++;
        }

        return depth;
    }

    // [MS-CFB] 2.6.4: shorter names first, then by the upper-case form of each UTF-16 code unit.
    private static int Compare(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return a.Length.CompareTo(b.Length);
        }

        for (int i = 0; i < a.Length; i++)
        {
            int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[i]));
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    private static bool Same(string a, string b) => Compare(a, b) == 0;

    private sealed class Node(string name, CompoundFileEntryType type, byte[]? data)
    {
        public string Name { get; } = name;

        public CompoundFileEntryType Type { get; } = type;

        public byte[]? Data { get; } = data;

        public List<Node> Children { get; } = [];

        public int Id { get; set; }

        public uint Start { get; set; }

        public long Size { get; set; }

        public bool Red { get; set; }

        public Node? Left { get; set; }

        public Node? Right { get; set; }

        public Node? Child { get; set; }
    }
}
