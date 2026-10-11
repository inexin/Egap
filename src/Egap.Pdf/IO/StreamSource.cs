namespace Egap.Pdf.IO;

/// <summary>A source over a seekable stream, read through the block cache. The stream's own position is not preserved.</summary>
internal sealed class StreamSource(Stream stream, bool leaveOpen, int blockSize = BlockCache.DefaultBlockSize, int capacity = BlockCache.DefaultBlockCount)
    : BlockCache(stream.Length, blockSize, capacity)
{
    protected override int ReadBlock(long position, Span<byte> buffer)
    {
        stream.Position = position;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            stream.Dispose();
        }

        base.Dispose(disposing);
    }
}
