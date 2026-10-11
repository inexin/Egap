namespace Egap.Pdf.IO;

/// <summary>
/// A cursor over a <see cref="PdfSource"/> for byte-at-a-time parsing. It holds the current chunk, so reads within a chunk
/// cost an array access. Not thread safe: each parse uses its own reader.
/// </summary>
internal sealed class SourceReader
{
    private byte[] chunk = [];
    private int chunkOffset;
    private int chunkCount;
    private long chunkStart;
    private int index;

    public SourceReader(PdfSource source, long position = 0)
    {
        Source = source;
        Position = position;
    }

    public PdfSource Source { get; }

    public long Length => Source.Length;

    /// <summary>The position of the next byte. Setting it past the end is allowed; reads then return -1.</summary>
    public long Position
    {
        get => chunkStart + index;
        set
        {
            if (value >= chunkStart && value < chunkStart + chunkCount)
            {
                index = (int)(value - chunkStart);
                return;
            }

            chunk = [];
            chunkOffset = 0;
            chunkCount = 0;
            chunkStart = value;
            index = 0;
        }
    }

    public bool AtEnd => Peek() < 0;

    /// <summary>The next byte, or -1 at the end, without moving.</summary>
    public int Peek()
    {
        if (index < chunkCount || Load())
        {
            return chunk[chunkOffset + index];
        }

        return -1;
    }

    /// <summary>The byte <paramref name="ahead"/> places after the next one, or -1 past the end, without moving.</summary>
    public int Peek(int ahead)
    {
        if (ahead == 0)
        {
            return Peek();
        }

        int at = index + ahead;
        if (at >= 0 && at < chunkCount)
        {
            return chunk[chunkOffset + at];
        }

        long position = Position + ahead;
        Span<byte> one = stackalloc byte[1];
        return position >= 0 && Source.Read(position, one) == 1 ? one[0] : -1;
    }

    /// <summary>The next byte, or -1 at the end, moving past it.</summary>
    public int Read()
    {
        if (index < chunkCount || Load())
        {
            return chunk[chunkOffset + index++];
        }

        return -1;
    }

    /// <summary>Moves past the next byte; does nothing at the end.</summary>
    public void Skip()
    {
        if (index < chunkCount || Load())
        {
            index++;
        }
    }

    /// <summary>Copies the next bytes into <paramref name="destination"/> and moves past them.</summary>
    /// <returns>The number copied, fewer than asked only at the end.</returns>
    public int Read(Span<byte> destination)
    {
        int read = Source.Read(Position, destination);
        Position += read;
        return read;
    }

    /// <summary>Whether the bytes at the current position are <paramref name="text"/> (ASCII), without moving.</summary>
    public bool StartsWith(ReadOnlySpan<byte> text)
    {
        if (index + text.Length <= chunkCount || (Load() && index + text.Length <= chunkCount))
        {
            return chunk.AsSpan(chunkOffset + index, text.Length).SequenceEqual(text);
        }

        Span<byte> buffer = text.Length <= 256 ? stackalloc byte[text.Length] : new byte[text.Length];
        return Source.Read(Position, buffer) == text.Length && buffer.SequenceEqual(text);
    }

    private bool Load()
    {
        long position = Position;
        if (!Source.TryGetChunk(position, out ArraySegment<byte> segment, out long start))
        {
            return false;
        }

        chunk = segment.Array!;
        chunkOffset = segment.Offset;
        chunkCount = segment.Count;
        chunkStart = start;
        index = (int)(position - start);
        return index < chunkCount;
    }
}
