using Microsoft.Win32.SafeHandles;

namespace Egap.Pdf.IO;

/// <summary>A source over a file opened by path, read with positional reads through the block cache.</summary>
internal sealed class FileSource(SafeFileHandle handle, int blockSize = BlockCache.DefaultBlockSize, int capacity = BlockCache.DefaultBlockCount)
    : BlockCache(RandomAccess.GetLength(handle), blockSize, capacity)
{
    protected override int ReadBlock(long position, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer[total..], position + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            handle.Dispose();
        }

        base.Dispose(disposing);
    }
}
