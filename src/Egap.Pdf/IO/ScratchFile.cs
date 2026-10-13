namespace Egap.Pdf.IO;

/// <summary>
/// A temporary file a document moves finished streams into (<see cref="PdfPage.Flush"/>), so generating thousands of pages does
/// not hold every page's content and images in memory. Deleted when closed.
/// </summary>
internal sealed class ScratchFile : IDisposable
{
    private readonly FileStream file = new(
        Path.Combine(Path.GetTempPath(), "egap-" + Guid.NewGuid().ToString("N") + ".tmp"),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        64 * 1024,
        FileOptions.DeleteOnClose);

    private readonly Lock gate = new();

    /// <summary>Appends bytes; returns where they start.</summary>
    public long Append(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            long at = file.Length;
            file.Position = at;
            file.Write(bytes);
            return at;
        }
    }

    /// <summary>Reads bytes written earlier.</summary>
    public byte[] Read(long offset, int length)
    {
        lock (gate)
        {
            byte[] bytes = new byte[length];
            file.Position = offset;
            file.ReadExactly(bytes);
            return bytes;
        }
    }

    public void Dispose()
    {
        file.Dispose();
    }
}

/// <summary>A stream's encoded bytes moved to the document's scratch file.</summary>
internal sealed class ScratchData(ScratchFile file, long offset, int length) : StreamData
{
    public override long Length => length;

    public override byte[] Read()
    {
        return file.Read(offset, length);
    }
}
