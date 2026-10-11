using Egap.Pdf.IO;

namespace Egap.Pdf.Tests.IO;

public sealed class PdfSourceTests
{
    private static byte[] Bytes(int count)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)(i * 7 + (i >> 8));
        }

        return bytes;
    }

    public static TheoryData<string> Kinds => ["memory", "file", "seekable", "nonseekable"];

    private static PdfSource Open(string kind, byte[] bytes, out string? path)
    {
        path = null;
        switch (kind)
        {
            case "memory":
                return PdfSource.FromMemory(bytes);
            case "file":
                path = Path.Combine(Path.GetTempPath(), $"egap-source-{Guid.NewGuid():N}.bin");
                File.WriteAllBytes(path, bytes);
                return PdfSource.FromFile(path);
            case "seekable":
                return PdfSource.FromStream(new MemoryStream(bytes), leaveOpen: false);
            default:
                return PdfSource.FromStream(new NonSeekableStream(bytes), leaveOpen: false);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ReadsEveryByteInRandomOrder(string kind)
    {
        byte[] bytes = Bytes(200_000);
        using (PdfSource source = Open(kind, bytes, out string? path))
        {
            Assert.Equal(bytes.Length, source.Length);
            Random random = new(1);
            for (int i = 0; i < 2000; i++)
            {
                long position = random.Next(bytes.Length + 10);
                int count = random.Next(70_000);
                byte[] read = new byte[count];
                int got = source.Read(position, read);
                int expected = (int)Math.Clamp(bytes.Length - position, 0, count);
                Assert.Equal(expected, got);
                Assert.True(read.AsSpan(0, got).SequenceEqual(bytes.AsSpan((int)Math.Min(position, bytes.Length), got)));
            }

            source.Dispose();
            if (path is not null)
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ChunkReportsNothingOutsideTheSource()
    {
        using PdfSource source = PdfSource.FromMemory(Bytes(10));

        Assert.False(source.TryGetChunk(-1, out _, out _));
        Assert.False(source.TryGetChunk(10, out _, out _));
        Assert.True(source.TryGetChunk(9, out ArraySegment<byte> chunk, out long start));
        Assert.Equal(0, start);
        Assert.Equal(10, chunk.Count);
    }

    [Fact]
    public void CacheKeepsRecentBlocksAndEvictsTheOldest()
    {
        byte[] bytes = Bytes(1000);
        using StreamSource source = new(new MemoryStream(bytes), leaveOpen: false, blockSize: 100, capacity: 3);

        source.ReadBytes(0, 1);
        source.ReadBytes(150, 1);
        source.ReadBytes(250, 1);
        Assert.Equal(3, source.BlockReads);

        // Touch block 0 so block 1 is now the oldest, then load a fourth block.
        source.ReadBytes(10, 1);
        source.ReadBytes(999, 1);
        Assert.Equal(4, source.BlockReads);

        source.ReadBytes(20, 1);
        Assert.Equal(4, source.BlockReads);
        source.ReadBytes(120, 1);
        Assert.Equal(5, source.BlockReads);
    }

    [Fact]
    public void LazyReadsTouchOnlyTheBlocksNeeded()
    {
        byte[] bytes = Bytes(10 * BlockCache.DefaultBlockSize);
        using StreamSource source = new(new MemoryStream(bytes), leaveOpen: false);

        source.ReadBytes(bytes.Length - 1024, 1024);
        source.ReadBytes(5 * BlockCache.DefaultBlockSize + 10, 100);

        Assert.Equal(2, source.BlockReads);
    }

    [Fact]
    public void SeekableStreamStaysOpenWhenAsked()
    {
        MemoryStream stream = new(Bytes(10));
        PdfSource.FromStream(stream, leaveOpen: true).Dispose();

        Assert.True(stream.CanRead);

        PdfSource.FromStream(stream, leaveOpen: false).Dispose();
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void NonSeekableStreamIsCopiedAndClosed()
    {
        NonSeekableStream stream = new(Bytes(10));
        using PdfSource source = PdfSource.FromStream(stream, leaveOpen: false);

        Assert.IsType<MemorySource>(source);
        Assert.True(stream.Disposed);
        Assert.Equal(Bytes(10), source.ReadBytes(0, 10));
    }

    [Fact]
    public void FileStaysReadableByOthers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-source-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, Bytes(100));
        try
        {
            using PdfSource source = PdfSource.FromFile(path);
            Assert.Equal(Bytes(100), File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReaderWalksAcrossBlocks()
    {
        byte[] bytes = Bytes(1000);
        using StreamSource source = new(new MemoryStream(bytes), leaveOpen: false, blockSize: 64, capacity: 2);
        SourceReader reader = new(source);

        for (int i = 0; i < bytes.Length; i++)
        {
            Assert.Equal(i, reader.Position);
            Assert.Equal(bytes[i], reader.Peek());
            Assert.Equal(i + 1 < bytes.Length ? bytes[i + 1] : -1, reader.Peek(1));
            Assert.Equal(bytes[i], reader.Read());
        }

        Assert.Equal(-1, reader.Read());
        Assert.True(reader.AtEnd);

        reader.Position = 60;
        Assert.True(reader.StartsWith(bytes.AsSpan(60, 10)));
        Assert.False(reader.StartsWith(bytes.AsSpan(61, 10)));
        Assert.Equal(bytes[59], reader.Peek(-1));

        reader.Position = 5000;
        Assert.Equal(-1, reader.Peek());
        reader.Position = 3;
        Assert.Equal(bytes[3], reader.Peek());
    }

    [Fact]
    public void ReadOptionsRejectNonPositiveLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReadOptions { MaxNestingDepth = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReadOptions { MaxDecodedStreamBytes = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReadOptions { MaxDecompressionRatio = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReadOptions { MaxXrefSections = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReadOptions { MaxObjects = 0 });

        PdfReadOptions options = new() { MaxNestingDepth = 5, Password = "x", Strict = true };
        Assert.Equal(5, options.MaxNestingDepth);
        Assert.Equal(512L * 1024 * 1024, options.MaxDecodedStreamBytes);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        public override bool CanSeek => false;

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
