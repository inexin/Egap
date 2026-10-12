using Microsoft.Win32.SafeHandles;

namespace Egap.Pdf.IO;

/// <summary>
/// Random access over the bytes of a PDF file. Reads are served in chunks: a memory source has one chunk, a file or stream
/// source serves blocks from a small cache, so lazy loading never reads the whole file.
/// </summary>
/// <remarks>All members are safe to call from several threads at once.</remarks>
internal abstract class PdfSource : IDisposable
{
    /// <summary>The number of bytes in the source.</summary>
    public abstract long Length { get; }

    /// <summary>
    /// Gets the chunk holding <paramref name="position"/>. Returns <see langword="false"/> when the position is at or past
    /// the end.
    /// </summary>
    /// <param name="position">The byte position wanted.</param>
    /// <param name="chunk">The chunk; its first byte is at <paramref name="chunkStart"/> in the source.</param>
    /// <param name="chunkStart">The position of the chunk's first byte.</param>
    public abstract bool TryGetChunk(long position, out ArraySegment<byte> chunk, out long chunkStart);

    /// <summary>Copies bytes starting at <paramref name="position"/> into <paramref name="destination"/>.</summary>
    /// <returns>The number of bytes copied, less than the destination's length only at the end of the source.</returns>
    public int Read(long position, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length && TryGetChunk(position + total, out ArraySegment<byte> chunk, out long start))
        {
            int offset = (int)(position + total - start);
            int count = Math.Min(chunk.Count - offset, destination.Length - total);
            chunk.AsSpan(offset, count).CopyTo(destination[total..]);
            total += count;
        }

        return total;
    }

    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="position"/>, fewer at the end of the source.</summary>
    public byte[] ReadBytes(long position, int count)
    {
        long available = Math.Max(0, Length - position);
        byte[] bytes = new byte[(int)Math.Min(count, available)];
        Read(position, bytes);
        return bytes;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the underlying file or stream when the source owns it.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>A source over bytes already in memory.</summary>
    public static PdfSource FromMemory(ReadOnlyMemory<byte> bytes)
    {
        return new MemorySource(bytes);
    }

    /// <summary>Opens a file for reading. The file stays open, shared for reading, until the source is disposed.</summary>
    public static PdfSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        return new FileSource(handle, path);
    }

    /// <summary>
    /// A source over a stream. A seekable stream is read in place, so it must stay open and unchanged while the source is
    /// used; a non-seekable stream is copied to memory first.
    /// </summary>
    /// <param name="stream">The stream, positioned anywhere: the file is taken to start at position 0.</param>
    /// <param name="leaveOpen">Whether the stream stays open when the source is disposed (or, for a non-seekable stream, after
    /// it has been copied).</param>
    public static PdfSource FromStream(Stream stream, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        if (stream.CanSeek)
        {
            return new StreamSource(stream, leaveOpen);
        }

        MemoryStream copy = new();
        stream.CopyTo(copy);
        if (!leaveOpen)
        {
            stream.Dispose();
        }

        return new MemorySource(copy.GetBuffer().AsMemory(0, (int)copy.Length));
    }
}
