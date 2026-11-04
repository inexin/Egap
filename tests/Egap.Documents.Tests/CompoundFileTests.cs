using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Office;

namespace Egap.Documents.Tests;

/// <summary>The compound file ([MS-CFB]) reader and writer: round trips, the directory tree, damage and limits.</summary>
public sealed class CompoundFileTests
{
    private static byte[] Bytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Write(params (string Path, byte[] Data)[] streams)
    {
        using MemoryStream output = new();
        CompoundFileWriter.Write(output, streams);
        return output.ToArray();
    }

    [Fact]
    public void WrittenStreamsReadBack()
    {
        (string, byte[])[] streams =
        [
            ("Small", Bytes(100, 1)),
            ("Exactly4095", Bytes(4095, 2)),
            ("Exactly4096", Bytes(4096, 3)),
            ("Large", Bytes(100_000, 4)),
            ("Empty", []),
            ("\u0006DataSpaces/Version", Bytes(76, 5)),
            ("\u0006DataSpaces/TransformInfo/StrongEncryptionTransform/\u0006Primary", Bytes(200, 6)),
        ];
        byte[] file = Write(streams);

        CompoundFile read = CompoundFile.Read(file, new DocumentLimits());

        Assert.Equal(0, file.Length % 512);
        Assert.Equal(3, read.MajorVersion);
        Assert.Empty(read.Problems);
        foreach ((string path, byte[] data) in streams)
        {
            Assert.Equal(data, read.ReadStream(path));
        }

        Assert.Equal(CompoundFileEntryType.Storage, read.Find("\u0006dataspaces/transforminfo")!.Type);
        Assert.Null(read.ReadStream("\u0006DataSpaces"));
        Assert.Null(read.Find("Missing"));
        Assert.Equal(CompoundFileEntryType.Root, read.Entries[0].Type);
        Assert.Equal(11, read.Entries.Count);
    }

    [Fact]
    public void FilesOverSevenMegabytesUseDifatSectors()
    {
        // 109 FAT sectors in the header name 109 * 128 sectors of 512 bytes, about 7.1 MB; past that the DIFAT chain lists more.
        byte[] large = Bytes(9_000_000, 7);
        byte[] file = Write(("Large", large), ("Small", Bytes(10, 8)));

        Assert.NotEqual(0xFFFFFFFE, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x44)));
        CompoundFile read = CompoundFile.Read(new MemoryStream(file), new DocumentLimits());
        Assert.Empty(read.Problems);
        Assert.Equal(large, read.ReadStream("Large"));
        Assert.Equal(Bytes(10, 8), read.ReadStream("Small"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(40)]
    public void EachStoragesChildrenAreAValidRedBlackTree(int count)
    {
        (string, byte[])[] streams = [.. Enumerable.Range(0, count).Select(i => ($"S/Stream{(i * 7) + 3}{new string('a', i % 5)}", new byte[] { (byte)i }))];
        byte[] file = Write(streams);
        int directoryStart = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30));
        CompoundFile read = CompoundFile.Read(file, new DocumentLimits());

        // The directory's sectors follow one another, as the writer lays them out.
        byte[] Entry(uint id) => file.AsSpan(512 + (directoryStart * 512) + ((int)id * 128), 128).ToArray();
        string Name(byte[] e) => Encoding.Unicode.GetString(e, 0, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(0x40)) - 2);
        uint storage = (uint)read.Find("S")!.Id;
        uint top = BinaryPrimitives.ReadUInt32LittleEndian(Entry(storage).AsSpan(0x4C));
        List<string> inOrder = [];
        HashSet<int> blackHeights = [];
        void Walk(uint id, int blacks, bool parentRed)
        {
            if (id == 0xFFFFFFFF)
            {
                blackHeights.Add(blacks);
                return;
            }

            byte[] e = Entry(id);
            bool red = e[0x43] == 0;
            Assert.False(red && parentRed, "A red node has a red parent.");
            Walk(BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0x44)), blacks + (red ? 0 : 1), red);
            inOrder.Add(Name(e));
            Walk(BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0x48)), blacks + (red ? 0 : 1), red);
        }

        Walk(top, 0, parentRed: true);

        Assert.Equal(1, Entry(top)[0x43]);
        Assert.Single(blackHeights);
        Assert.Equal(count, inOrder.Count);
        Assert.Equal(inOrder.OrderBy(n => n.Length).ThenBy(n => n.ToUpperInvariant(), StringComparer.Ordinal), inOrder);
    }

    [Fact]
    public void NotACompoundFileIsADocumentError()
    {
        Assert.Throws<DocumentException>(() => CompoundFile.Read(new byte[1024], new DocumentLimits()));
        Assert.Throws<DocumentException>(() => CompoundFile.Read(new byte[100], new DocumentLimits()));
        byte[] file = Write(("A", [1]));
        file[0x1E] = 10;                                    // a sector shift no version has
        Assert.Throws<DocumentException>(() => CompoundFile.Read(file, new DocumentLimits()));
    }

    [Fact]
    public void ALoopingChainIsCutAndReported()
    {
        byte[] large = Bytes(20_000, 9);
        byte[] file = Write(("Large", large));
        CompoundFile read = CompoundFile.Read(file, new DocumentLimits());
        uint start = read.Find("Large")!.StartSector;

        // Point the stream's second sector back at its first.
        uint fatSector = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x4C));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(512 + ((int)fatSector * 512) + ((int)(start + 1) * 4)), start);
        read = CompoundFile.Read(file, new DocumentLimits());
        byte[] bytes = read.ReadStream("Large")!;

        Assert.Equal(large.Length, bytes.Length);
        Assert.Equal(large.AsSpan(0, 1024).ToArray(), bytes[..1024]);
        Assert.All(bytes[1024..], b => Assert.Equal(0, b));
        Assert.Contains(read.Problems, p => p.Contains("breaks off", StringComparison.Ordinal));
    }

    [Fact]
    public void ADirectoryLoopIsLeftOut()
    {
        byte[] file = Write(("A", [1]), ("B", [2]), ("C", [3]));
        CompoundFile read = CompoundFile.Read(file, new DocumentLimits());
        int directoryStart = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30));
        int b = read.Find("B")!.Id;

        // B's left sibling becomes B itself.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(512 + (directoryStart * 512) + (b * 128) + 0x44), (uint)b);
        read = CompoundFile.Read(file, new DocumentLimits());

        Assert.NotNull(read.Find("B"));
        Assert.Contains(read.Problems, p => p.Contains("already seen", StringComparison.Ordinal));
    }

    [Fact]
    public void LimitsStopHugeOrDeepFiles()
    {
        byte[] many = Write([.. Enumerable.Range(0, 40).Select(i => ($"S{i}", new byte[] { 1 }))]);
        Assert.Throws<DocumentLimitException>(() => CompoundFile.Read(many, new DocumentLimits { MaxEntries = 10 }));

        byte[] deep = Write(("A/B/C/D/E/F", [1]));
        Assert.Throws<DocumentLimitException>(() => CompoundFile.Read(deep, new DocumentLimits { MaxDepth = 3 }));
        Assert.NotNull(CompoundFile.Read(deep, new DocumentLimits()).ReadStream("A/B/C/D/E/F"));

        byte[] large = Write(("Large", Bytes(10_000, 10)));
        CompoundFile read = CompoundFile.Read(large, new DocumentLimits { MaxPartSize = 5_000 });
        Assert.Throws<DocumentLimitException>(() => read.ReadStream("Large"));
    }

    [Fact]
    public void AHeaderClaimingAHugeMiniFatIsStoppedOrCutToTheFile()
    {
        byte[] file = Write(("Small", Bytes(100, 12)));

        // 0x00FFFFFF mini FAT sectors would be 8 GB: past the part size limit.
        byte[] huge = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(0x40), 0x00FFFFFF);
        CompoundFile read = CompoundFile.Read(huge, new DocumentLimits());
        Assert.Throws<DocumentLimitException>(() => read.ReadStream("Small"));

        // 500,000 sectors (256 MB) is under the limit, but the file holds a few KB: the read is cut to the file, not allocated.
        byte[] large = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(large.AsSpan(0x40), 500_000);
        read = CompoundFile.Read(large, new DocumentLimits());
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(Bytes(100, 12), read.ReadStream("Small"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
        Assert.Contains(read.Problems, p => p.Contains("the mini FAT", StringComparison.Ordinal));
    }

    [Fact]
    public void AMiniStreamLargerThanTheFileIsCutBeforeItIsAllocated()
    {
        byte[] file = Write(("Small", Bytes(100, 13)));
        int directoryStart = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(512 + (directoryStart * 512) + 0x78), 400_000_000);

        CompoundFile read = CompoundFile.Read(file, new DocumentLimits());
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(Bytes(100, 13), read.ReadStream("Small"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
        Assert.Contains(read.Problems, p => p.Contains("the mini stream", StringComparison.Ordinal));
    }

    [Fact]
    public void AStreamLargerThanTheFileIsCut()
    {
        byte[] file = Write(("Large", Bytes(10_000, 11)));
        CompoundFile read = CompoundFile.Read(file, new DocumentLimits());
        int directoryStart = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30));
        int id = read.Find("Large")!.Id;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(512 + (directoryStart * 512) + (id * 128) + 0x78), 50_000_000);

        read = CompoundFile.Read(file, new DocumentLimits());

        Assert.True(read.ReadStream("Large")!.Length < 100_000);
        Assert.NotEmpty(read.Problems);
    }
}
