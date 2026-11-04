using System.Buffers.Binary;
using System.Text;

namespace Egap.Documents.Office;

/// <summary>What a compound file's directory entry is ([MS-CFB] 2.6.1).</summary>
internal enum CompoundFileEntryType
{
    /// <summary>A storage: a folder of entries.</summary>
    Storage = 1,

    /// <summary>A stream: a file of bytes.</summary>
    Stream = 2,

    /// <summary>The root storage, which also holds the mini stream.</summary>
    Root = 5,
}

/// <summary>One entry of a compound file's directory.</summary>
/// <param name="Id">Its index in the directory.</param>
/// <param name="Name">Its name.</param>
/// <param name="Path">Its storages' names and its own, '/'-separated, from the root (empty for the root).</param>
/// <param name="Type">Storage, stream or root.</param>
/// <param name="StartSector">The first sector (or mini sector, for a small stream) of its data.</param>
/// <param name="Size">Its data's length in bytes.</param>
internal sealed record CompoundFileEntry(int Id, string Name, string Path, CompoundFileEntryType Type, uint StartSector, long Size);

/// <summary>
/// A Microsoft compound file ([MS-CFB], OLE2 structured storage): a small FAT file system in one file, which holds
/// encrypted Office packages and old binary Word documents. Reads the header, the FAT, the directory and the streams from a
/// seekable source without loading the whole file. Damaged chains and directory entries are cut and reported in
/// <see cref="Problems"/>; past <see cref="DocumentLimits"/> it throws <see cref="DocumentLimitException"/>, and a header
/// that is not a compound file's throws <see cref="DocumentException"/>.
/// </summary>
/// <remarks>Ported from ClassicMac's compound file reader (the maintainer's own MIT code), with limits added.</remarks>
internal sealed class CompoundFile
{
    private const uint MaxRegularSector = 0xFFFFFFFA;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSectorMark = 0xFFFFFFFD;
    private const uint NoStream = 0xFFFFFFFF;
    private const int HeaderSize = 512;
    private const int MiniSectorSize = 64;
    private const int EntrySize = 128;
    private const uint MiniStreamCutoff = 4096;

    private static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly Stream source;
    private readonly long length;
    private readonly DocumentLimits limits;
    private readonly List<string> problems = [];
    private readonly List<CompoundFileEntry> entries = [];
    private readonly uint[] fat;
    private uint[]? miniFat;
    private byte[]? miniStream;
    private readonly uint miniFatStart;
    private readonly uint miniFatCount;
    private CompoundFileEntry root = null!;

    private CompoundFile(Stream source, DocumentLimits limits)
    {
        this.source = source;
        this.limits = limits;
        length = source.Length;
        byte[] header = new byte[HeaderSize];
        ReadAt(0, header);
        MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1A));
        int shift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E));
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1C)) != 0xFFFE
            || !((MajorVersion == 3 && shift == 9) || (MajorVersion == 4 && shift == 12))
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x20)) != 6)
        {
            throw new DocumentException("The file is not a compound file: its byte order, version or sector size is not [MS-CFB]'s.");
        }

        SectorSize = 1 << shift;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38)) != MiniStreamCutoff)
        {
            problems.Add("The header's mini stream cutoff is not 4096; 4096 is used.");
        }

        miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x3C));
        miniFatCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x40));
        fat = ReadFat(header);
        ReadDirectory(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30)));
    }

    /// <summary>3 (512-byte sectors) or 4 (4096-byte sectors).</summary>
    public int MajorVersion { get; }

    /// <summary>The sector size in bytes.</summary>
    public int SectorSize { get; }

    /// <summary>The directory's entries in tree order: the root first, each storage before its contents.</summary>
    public IReadOnlyList<CompoundFileEntry> Entries => entries;

    /// <summary>What was damaged and cut while reading, one line each.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Whether <paramref name="data"/> starts with the compound file signature ([MS-CFB] 2.2).</summary>
    public static bool IsCompoundFile(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    /// <summary>Reads a compound file's header, FAT and directory from <paramref name="data"/>.</summary>
    /// <exception cref="DocumentException">The data is no compound file.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <paramref name="limits"/>.</exception>
    public static CompoundFile Read(byte[] data, DocumentLimits limits)
    {
        return Read(new MemoryStream(data, writable: false), limits);
    }

    /// <summary>
    /// Reads a compound file's header, FAT and directory from a seekable stream, which must stay open while streams are read
    /// from it.
    /// </summary>
    /// <exception cref="DocumentException">The data is no compound file.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <paramref name="limits"/>.</exception>
    public static CompoundFile Read(Stream source, DocumentLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        if (source.Length < HeaderSize)
        {
            throw new DocumentException("The file is not a compound file: it is shorter than the header.");
        }

        Span<byte> head = stackalloc byte[8];
        source.Position = 0;
        source.ReadExactly(head);
        if (!IsCompoundFile(head))
        {
            throw new DocumentException("The file is not a compound file: no [MS-CFB] signature.");
        }

        return new CompoundFile(source, limits);
    }

    /// <summary>The entry at <paramref name="path"/> ('/'-separated, names compared ignoring case), or null.</summary>
    public CompoundFileEntry? Find(string path)
    {
        foreach (CompoundFileEntry entry in entries)
        {
            if (entry.Type != CompoundFileEntryType.Root && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The bytes of the stream at <paramref name="path"/>, or null when there is no such stream.</summary>
    /// <exception cref="DocumentLimitException">The stream is larger than <see cref="DocumentLimits.MaxPartSize"/>.</exception>
    public byte[]? ReadStream(string path)
    {
        return Find(path) is { Type: CompoundFileEntryType.Stream } entry ? ReadStream(entry) : null;
    }

    /// <summary>
    /// A stream's bytes: from the mini stream when it is smaller than the cutoff, else from sectors. A chain that ends early,
    /// loops or leaves the file is cut (reported), and the rest is zeros.
    /// </summary>
    /// <exception cref="DocumentLimitException">The stream is larger than <see cref="DocumentLimits.MaxPartSize"/>.</exception>
    public byte[] ReadStream(CompoundFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Size == 0)
        {
            return [];
        }

        if (entry.Size > limits.MaxPartSize)
        {
            throw new DocumentLimitException($"The compound file's stream \"{entry.Path}\" is {entry.Size} bytes, more than the limit of {limits.MaxPartSize}.");
        }

        if (entry.Type == CompoundFileEntryType.Stream && entry.Size < MiniStreamCutoff)
        {
            byte[] mini = MiniStream();
            return Chain(MiniFat(), entry.StartSector, entry.Size, MiniSectorSize, mini.Length, (sector, into) => CopyMini(mini, sector, into), entry.Path);
        }

        return Chain(fat, entry.StartSector, entry.Size, SectorSize, BytesInSectors, ReadSector, entry.Path);
    }

    private long SectorOffset(uint sector) => ((long)sector + 1) * SectorSize;

    // The bytes the file's sectors after the header hold: no chain of them can be longer.
    private long BytesInSectors => (length - HeaderSize + SectorSize - 1) / SectorSize * SectorSize;

    private void ReadAt(long offset, Span<byte> into)
    {
        source.Position = offset;
        source.ReadExactly(into);
    }

    // Reads a whole sector into the span; false when it is past the file.
    private bool ReadSector(uint sector, Span<byte> into)
    {
        if (sector > MaxRegularSector || SectorOffset(sector) + SectorSize > length)
        {
            return false;
        }

        ReadAt(SectorOffset(sector), into[..SectorSize]);
        return true;
    }

    private static bool CopyMini(byte[] mini, uint sector, Span<byte> into)
    {
        if (((long)sector + 1) * MiniSectorSize > mini.Length)
        {
            return false;
        }

        mini.AsSpan((int)sector * MiniSectorSize, MiniSectorSize).CopyTo(into);
        return true;
    }

    private delegate bool SectorReader(uint sector, Span<byte> into);

    // Follows a chain of (mini) sectors for size bytes; cuts it where a sector is missing, out of the table or seen before.
    // capacity is what the sectors behind the table hold (the file's sectors, or the mini stream), so a header or directory
    // entry that claims more cannot make the reader allocate more than the file can fill.
    private byte[] Chain(uint[] table, uint start, long size, int sectorSize, long capacity, SectorReader read, string what)
    {
        // A stream cannot hold more than every sector its table can name, nor more than the sectors there are.
        long most = Math.Min((long)table.Length * sectorSize, capacity);
        if (size > most)
        {
            problems.Add($"Stream \"{what}\" says it is {size} bytes, more than the file holds; it is cut to {most}.");
            size = most;
        }

        byte[] result = new byte[size];
        byte[] buffer = new byte[sectorSize];
        bool[] seen = new bool[table.Length];
        uint sector = start;
        for (long at = 0; at < size; at += sectorSize)
        {
            if (sector >= table.Length || seen[sector] || !read(sector, buffer))
            {
                problems.Add($"Stream \"{what}\" breaks off after {at} of its {size} bytes (sector {sector}); the rest is zeros.");
                break;
            }

            seen[sector] = true;
            buffer.AsSpan(0, (int)Math.Min(sectorSize, size - at)).CopyTo(result.AsSpan((int)at));
            sector = table[sector];
        }

        return result;
    }

    // The FAT: the sectors the header's 109 DIFAT entries list, then those the chain of DIFAT sectors lists ([MS-CFB] 2.5).
    private uint[] ReadFat(ReadOnlySpan<byte> header)
    {
        long count = BinaryPrimitives.ReadUInt32LittleEndian(header[0x2C..]);
        long sectorsInFile = (length - HeaderSize + SectorSize - 1) / SectorSize;
        if (count > sectorsInFile)
        {
            problems.Add($"The header says {count} FAT sectors, more than the file's {sectorsInFile} sectors.");
            count = sectorsInFile;
        }

        List<uint> sectors = [];
        for (int i = 0; i < 109 && sectors.Count < count; i++)
        {
            sectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(header[(0x4C + (4 * i))..]));
        }

        uint difat = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
        int perSector = SectorSize / 4;
        byte[] buffer = new byte[SectorSize];
        HashSet<uint> visited = [];
        while (sectors.Count < count && difat <= MaxRegularSector && visited.Add(difat) && ReadSector(difat, buffer))
        {
            for (int i = 0; i < perSector - 1 && sectors.Count < count; i++)
            {
                sectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4 * i)));
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4 * (perSector - 1)));
        }

        if (sectors.Count < count)
        {
            problems.Add($"The header says {count} FAT sectors; {sectors.Count} are listed.");
        }

        uint[] table = new uint[sectors.Count * perSector];
        for (int f = 0; f < sectors.Count; f++)
        {
            if (ReadSector(sectors[f], buffer))
            {
                for (int i = 0; i < perSector; i++)
                {
                    table[(f * perSector) + i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4 * i));
                }
            }
            else
            {
                table.AsSpan(f * perSector, perSector).Fill(EndOfChain);
            }
        }

        // Each FAT sector is marked as one in the FAT ([MS-CFB] 2.3).
        foreach (uint sector in sectors)
        {
            if (sector >= table.Length || table[sector] != FatSectorMark)
            {
                problems.Add("A sector the DIFAT lists as a FAT sector is not marked as one in the FAT.");
                break;
            }
        }

        return table;
    }

    private uint[] MiniFat()
    {
        if (miniFat is not null)
        {
            return miniFat;
        }

        if (miniFatCount == 0 || miniFatStart > MaxRegularSector)
        {
            return miniFat = [];
        }

        long size = (long)miniFatCount * SectorSize;
        if (size > limits.MaxPartSize)
        {
            throw new DocumentLimitException($"The compound file's mini FAT is {size} bytes, more than the limit of {limits.MaxPartSize}.");
        }

        byte[] bytes = Chain(fat, miniFatStart, size, SectorSize, BytesInSectors, ReadSector, "the mini FAT");
        uint[] table = new uint[bytes.Length / 4];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 * i));
        }

        return miniFat = table;
    }

    private byte[] MiniStream()
    {
        if (miniStream is not null)
        {
            return miniStream;
        }

        if (root.StartSector > MaxRegularSector || root.Size == 0)
        {
            return miniStream = [];
        }

        if (root.Size > limits.MaxPartSize)
        {
            throw new DocumentLimitException($"The compound file's mini stream is {root.Size} bytes, more than the limit of {limits.MaxPartSize}.");
        }

        return miniStream = Chain(fat, root.StartSector, root.Size, SectorSize, BytesInSectors, ReadSector, "the mini stream");
    }

    // The directory: 128-byte entries in a chain of sectors; each storage's children are a red-black tree of siblings under
    // its child pointer ([MS-CFB] 2.6). The tree is walked in the order of the sibling links, not checked for balance, with a
    // stack of its own so a degenerate tree cannot overflow the call stack.
    private void ReadDirectory(uint start)
    {
        List<byte[]> directory = [];
        bool[] seen = new bool[fat.Length];
        byte[] buffer = new byte[SectorSize];
        int perSector = SectorSize / EntrySize;
        for (uint sector = start; sector <= MaxRegularSector && sector < fat.Length && !seen[sector]; sector = fat[sector])
        {
            seen[sector] = true;
            if (!ReadSector(sector, buffer))
            {
                problems.Add($"The directory's sector {sector} is past the end of the file.");
                break;
            }

            if (directory.Count > limits.MaxEntries)
            {
                throw new DocumentLimitException($"The compound file's directory has more than {limits.MaxEntries} entries, the limit.");
            }

            for (int i = 0; i < perSector; i++)
            {
                directory.Add(buffer.AsSpan(i * EntrySize, EntrySize).ToArray());
            }
        }

        if (directory.Count == 0)
        {
            throw new DocumentException("The compound file has no directory.");
        }

        bool[] visited = new bool[directory.Count];
        visited[0] = true;
        root = Entry(directory[0], 0, "");
        if (root.Type != CompoundFileEntryType.Root)
        {
            throw new DocumentException("The compound file's first directory entry is not its root.");
        }

        entries.Add(root);

        // Each frame is a sibling tree still to walk in order: (entry id, parent path, storage depth, whether its left subtree
        // is done).
        Stack<(uint Id, string Parent, int Depth, bool LeftDone)> stack = new();
        uint first = Child(directory[0]);
        if (Valid(first))
        {
            stack.Push((first, "", 1, false));
        }

        while (stack.Count > 0)
        {
            (uint id, string parent, int depth, bool leftDone) = stack.Pop();
            byte[] raw = directory[(int)id];
            if (!leftDone)
            {
                stack.Push((id, parent, depth, true));
                uint left = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x44));
                if (Valid(left))
                {
                    stack.Push((left, parent, depth, false));
                }

                continue;
            }

            // In order: the entry, its contents (before its right siblings), then the right subtree.
            uint right = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x48));
            if (Valid(right))
            {
                stack.Push((right, parent, depth, false));
            }

            CompoundFileEntry entry = Entry(raw, (int)id, parent);
            if (entry.Type is CompoundFileEntryType.Storage or CompoundFileEntryType.Stream)
            {
                entries.Add(entry);
                if (entry.Type == CompoundFileEntryType.Storage && Valid(Child(raw)))
                {
                    if (depth + 1 > limits.MaxDepth)
                    {
                        throw new DocumentLimitException($"The compound file's storages nest deeper than the limit of {limits.MaxDepth}.");
                    }

                    stack.Push((Child(raw), entry.Path, depth + 1, false));
                }
            }
        }

        static uint Child(byte[] raw) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x4C));

        bool Valid(uint id)
        {
            if (id == NoStream)
            {
                return false;
            }

            if (id >= directory.Count || visited[id])
            {
                problems.Add($"A directory entry points to entry {id}, which is past the directory or already seen; it is left out.");
                return false;
            }

            visited[id] = true;
            return true;
        }
    }

    private CompoundFileEntry Entry(byte[] raw, int id, string parent)
    {
        int nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x40)), 2, 64);
        string name = Encoding.Unicode.GetString(raw, 0, (nameLength - 2) & ~1);
        CompoundFileEntryType type = (CompoundFileEntryType)raw[0x42];
        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x78));
        if (MajorVersion == 3)
        {
            size &= 0xFFFFFFFF;                                     // the high half is not used in version 3 ([MS-CFB] 2.6.1)
        }
        else if (size < 0)
        {
            size = long.MaxValue;
        }

        string path = parent.Length == 0 ? (type == CompoundFileEntryType.Root ? "" : name) : parent + "/" + name;
        return new CompoundFileEntry(id, name, path, type, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x74)), size);
    }
}
