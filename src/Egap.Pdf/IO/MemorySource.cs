using System.Runtime.InteropServices;

namespace Egap.Pdf.IO;

/// <summary>A source over bytes in memory: the whole file is one chunk.</summary>
internal sealed class MemorySource : PdfSource
{
    private readonly ArraySegment<byte> bytes;

    public MemorySource(ReadOnlyMemory<byte> memory)
    {
        bytes = MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) ? segment : new ArraySegment<byte>(memory.ToArray());
    }

    public override long Length => bytes.Count;

    public override bool TryGetChunk(long position, out ArraySegment<byte> chunk, out long chunkStart)
    {
        chunk = bytes;
        chunkStart = 0;
        return position >= 0 && position < bytes.Count;
    }
}
