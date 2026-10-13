using System.Runtime.CompilerServices;

namespace Egap.Pdf.Content;

/// <summary>
/// The objects a document has written for drawing, by what they were made from (Phase 3 design Q6): a raster, a picture, a
/// font face or a graphics state. Keys that are objects the caller owns, such as rasters, are held weakly, so the cache never
/// keeps a caller's pixels alive; keys that are values, such as an opacity and blend mode, are held by value.
/// </summary>
internal sealed class ResourceCache
{
    private readonly ConditionalWeakTable<object, PdfReference> byInstance = [];
    private readonly Dictionary<object, PdfReference> byValue = [];
    private readonly Lock gate = new();

    /// <summary>The object written for <paramref name="key"/> (compared by identity), written by <paramref name="create"/> the first time.</summary>
    public PdfReference ForInstance(object key, Func<PdfReference> create)
    {
        lock (gate)
        {
            if (!byInstance.TryGetValue(key, out PdfReference? reference))
            {
                reference = create();
                byInstance.Add(key, reference);
            }

            return reference;
        }
    }

    /// <summary>The object written for <paramref name="key"/> (compared by value), written by <paramref name="create"/> the first time.</summary>
    public PdfReference ForValue(object key, Func<PdfReference> create)
    {
        lock (gate)
        {
            if (!byValue.TryGetValue(key, out PdfReference? reference))
            {
                reference = create();
                byValue[key] = reference;
            }

            return reference;
        }
    }

    /// <summary>Forgets an instance's object, as when it was flushed and must not be referenced again by mistake.</summary>
    public void Forget(object key)
    {
        lock (gate)
        {
            byInstance.Remove(key);
            byValue.Remove(key);
        }
    }
}
