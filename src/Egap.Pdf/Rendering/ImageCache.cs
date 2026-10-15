using Egami;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Decoded images a document's renders share (design Q14), by image stream and reduction, under a byte budget with the
/// least recently used dropped first. A dropped raster is left to the garbage collector rather than disposed, as another
/// render on another thread may still be drawing it.
/// </summary>
internal sealed class ImageCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<(PdfStream Image, int Reduction), LinkedListNode<Entry>> entries = new(KeyComparer.Instance);
    private readonly LinkedList<Entry> order = [];
    private long bytes;

    /// <summary>The cached raster, or one decoded now and kept while it fits in <paramref name="budget"/>.</summary>
    public Raster GetOrDecode(PdfStream image, int reduction, long budget, Func<Raster> decode)
    {
        lock (gate)
        {
            if (entries.TryGetValue((image, reduction), out LinkedListNode<Entry>? hit))
            {
                order.Remove(hit);
                order.AddFirst(hit);
                return hit.Value.Raster;
            }
        }

        Raster raster = decode();
        long size = (long)raster.Width * raster.Height * raster.Layout.BytesPerPixel;
        if (size > budget)
        {
            return raster;
        }

        lock (gate)
        {
            if (entries.TryGetValue((image, reduction), out LinkedListNode<Entry>? raced))
            {
                return raced.Value.Raster;
            }

            while (bytes + size > budget && order.Last is LinkedListNode<Entry> oldest)
            {
                order.RemoveLast();
                entries.Remove((oldest.Value.Image, oldest.Value.Reduction));
                bytes -= oldest.Value.Bytes;
            }

            entries[(image, reduction)] = order.AddFirst(new Entry(image, reduction, raster, size));
            bytes += size;
        }

        return raster;
    }

    private sealed record Entry(PdfStream Image, int Reduction, Raster Raster, long Bytes);

    /// <summary>Streams by identity: two equal streams in a file are still two images to cache.</summary>
    private sealed class KeyComparer : IEqualityComparer<(PdfStream Image, int Reduction)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((PdfStream Image, int Reduction) x, (PdfStream Image, int Reduction) y)
        {
            return ReferenceEquals(x.Image, y.Image) && x.Reduction == y.Reduction;
        }

        public int GetHashCode((PdfStream Image, int Reduction) obj)
        {
            return HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Image), obj.Reduction);
        }
    }
}
