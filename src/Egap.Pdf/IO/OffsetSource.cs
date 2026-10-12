namespace Egap.Pdf.IO;

/// <summary>A view of a source that starts <c>shift</c> bytes in, for files whose offsets count from a header that is not at the start.</summary>
internal sealed class OffsetSource(PdfSource inner, long shift) : PdfSource
{
    public PdfSource Inner => inner;

    public override long Length => inner.Length - shift;

    public override bool TryGetChunk(long position, out ArraySegment<byte> chunk, out long chunkStart)
    {
        if (position < 0 || !inner.TryGetChunk(position + shift, out chunk, out long start))
        {
            chunk = default;
            chunkStart = 0;
            return false;
        }

        // Cut off any part of the chunk before the view starts.
        if (start < shift)
        {
            int skip = (int)(shift - start);
            chunk = chunk.Slice(skip);
            start = shift;
        }

        chunkStart = start - shift;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A source passed on without its ownership: disposing it leaves the inner source open.</summary>
internal sealed class UnownedSource(PdfSource inner) : PdfSource
{
    public override long Length => inner.Length;

    public override bool TryGetChunk(long position, out ArraySegment<byte> chunk, out long chunkStart)
    {
        return inner.TryGetChunk(position, out chunk, out chunkStart);
    }
}
