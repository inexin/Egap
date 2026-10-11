namespace Egap.Pdf.IO;

/// <summary>
/// A source that reads fixed-size blocks on demand and keeps the most recently used ones. Subclasses supply the block reads.
/// </summary>
internal abstract class BlockCache : PdfSource
{
    /// <summary>The default block size: large enough for most objects, small enough that lazy loading reads little.</summary>
    public const int DefaultBlockSize = 32 * 1024;

    /// <summary>The default number of cached blocks (2 MiB at the default block size).</summary>
    public const int DefaultBlockCount = 64;

    private readonly int blockSize;
    private readonly int capacity;
    private readonly Dictionary<long, LinkedListNode<Block>> blocks = [];
    private readonly LinkedList<Block> recent = new();
    private readonly Lock gate = new();

    protected BlockCache(long length, int blockSize, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Length = length;
        this.blockSize = blockSize;
        this.capacity = capacity;
    }

    public override long Length { get; }

    /// <summary>The number of blocks read from the underlying file or stream so far, for tests.</summary>
    public int BlockReads { get; private set; }

    public override bool TryGetChunk(long position, out ArraySegment<byte> chunk, out long chunkStart)
    {
        if (position < 0 || position >= Length)
        {
            chunk = default;
            chunkStart = 0;
            return false;
        }

        long index = position / blockSize;
        chunkStart = index * blockSize;
        lock (gate)
        {
            if (blocks.TryGetValue(index, out LinkedListNode<Block>? node))
            {
                recent.Remove(node);
                recent.AddFirst(node);
                chunk = node.Value.Bytes;
                return true;
            }

            int count = (int)Math.Min(blockSize, Length - chunkStart);
            byte[] bytes = new byte[count];
            int read = ReadBlock(chunkStart, bytes);
            BlockReads++;
            chunk = new ArraySegment<byte>(bytes, 0, read);
            if (read == 0)
            {
                // The file shrank under us; report the end rather than spin.
                return false;
            }

            if (blocks.Count >= capacity)
            {
                LinkedListNode<Block> oldest = recent.Last!;
                recent.RemoveLast();
                blocks.Remove(oldest.Value.Index);
            }

            blocks[index] = recent.AddFirst(new Block(index, chunk));
            return true;
        }
    }

    /// <summary>Reads bytes at <paramref name="position"/>; called under the cache's lock.</summary>
    /// <returns>The number of bytes read, which is the buffer's length unless the file ended early.</returns>
    protected abstract int ReadBlock(long position, Span<byte> buffer);

    private readonly record struct Block(long Index, ArraySegment<byte> Bytes);
}
