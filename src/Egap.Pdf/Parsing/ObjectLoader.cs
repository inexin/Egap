using Egap.Pdf.IO;

namespace Egap.Pdf.Parsing;

/// <summary>
/// Loads objects on first use through the cross-reference: from their offset in the file, or from the object stream
/// that holds them. Loaded objects are cached. Thread safe.
/// </summary>
internal sealed class ObjectLoader : IObjectResolver
{
    /// <summary>How deep loads may nest, through a stream's indirect <c>/Length</c> or an object stream.</summary>
    public const int MaxLoadDepth = 32;

    private readonly PdfSource source;
    private readonly PdfReadOptions options;
    private readonly WarningLog log;
    private readonly Dictionary<int, PdfObject> cache = [];
    private readonly Dictionary<int, StoredObject?[]> objectStreams = [];
    private readonly HashSet<int> loading = [];
    private readonly Lock gate = new();
    private int depth;

    public ObjectLoader(PdfSource source, XrefTable xref, PdfReadOptions options, WarningLog log)
    {
        this.source = source;
        Xref = xref;
        this.options = options;
        this.log = log;
    }

    public XrefTable Xref { get; }

    public PdfObject Resolve(PdfReference reference)
    {
        return Load(reference.ObjectNumber, reference.Generation);
    }

    /// <summary>The object with the given number and generation, or <see cref="PdfNull"/> when there is none (7.3.10).</summary>
    public PdfObject Load(int objectNumber, int generation)
    {
        lock (gate)
        {
            if (!Xref.TryGet(objectNumber, out XrefEntry entry) || entry.Kind == XrefEntryKind.Free)
            {
                return PdfNull.Instance;
            }

            // A reference whose generation differs from the entry's refers to an object that no longer exists.
            int expected = entry.Kind == XrefEntryKind.InFile ? entry.Generation : 0;
            if (generation != expected)
            {
                return PdfNull.Instance;
            }

            if (cache.TryGetValue(objectNumber, out PdfObject? cached))
            {
                return cached;
            }

            if (loading.Contains(objectNumber) || depth >= MaxLoadDepth)
            {
                Warn($"Object {objectNumber} refers to itself while loading, or loads nest too deep; read as null.", objectNumber);
                return PdfNull.Instance;
            }

            loading.Add(objectNumber);
            depth++;
            try
            {
                PdfObject value = entry.Kind == XrefEntryKind.InFile ? LoadFromFile(objectNumber, entry) : LoadFromObjectStream(objectNumber, entry);
                cache[objectNumber] = value;
                return value;
            }
            finally
            {
                loading.Remove(objectNumber);
                depth--;
            }
        }
    }

    private PdfObject LoadFromFile(int objectNumber, XrefEntry entry)
    {
        if (entry.Offset >= source.Length)
        {
            Warn($"Object {objectNumber} is listed at {entry.Offset}, past the end of the file; read as null.", objectNumber);
            return PdfNull.Instance;
        }

        ObjectParser parser = new(new Lexer(new SourceReader(source), log), options, this);
        IndirectObject found;
        try
        {
            found = parser.ParseIndirectObject(entry.Offset);
        }
        catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
        {
            Warn($"Object {objectNumber} is not at {entry.Offset} ({e.Message}); read as null.", objectNumber);
            return PdfNull.Instance;
        }

        if (found.ObjectNumber != objectNumber || found.Generation != entry.Generation)
        {
            Warn($"The object at {entry.Offset} is {found.ObjectNumber} {found.Generation}, not {objectNumber} {entry.Generation}; read as null.", objectNumber);
            return PdfNull.Instance;
        }

        return found.Value;
    }

    private PdfObject LoadFromObjectStream(int objectNumber, XrefEntry entry)
    {
        int streamNumber = (int)entry.Offset;
        if (!objectStreams.TryGetValue(streamNumber, out StoredObject?[]? objects))
        {
            // Object streams are always in the file itself (7.5.7), which also keeps loads from nesting through them.
            if (!Xref.TryGet(streamNumber, out XrefEntry streamEntry) || streamEntry.Kind != XrefEntryKind.InFile)
            {
                Warn($"Object {objectNumber} is listed in object stream {streamNumber}, which is not an object in the file; read as null.", objectNumber);
                return PdfNull.Instance;
            }

            if (Load(streamNumber, streamEntry.Generation) is not PdfStream stream)
            {
                Warn($"Object {objectNumber} is listed in object {streamNumber}, which is not a stream; read as null.", objectNumber);
                return PdfNull.Instance;
            }

            try
            {
                objects = ObjectStreamReader.Read(stream, streamNumber, options, log, this);
            }
            catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
            {
                Warn($"Object stream {streamNumber} cannot be decoded ({e.Message}).", streamNumber);
                objects = [];
            }

            objectStreams[streamNumber] = objects;
        }

        int index = entry.Generation;
        if (index < objects.Length && objects[index] is StoredObject stored && stored.ObjectNumber == objectNumber)
        {
            return stored.Value;
        }

        Warn($"Object {objectNumber} is not at index {index} of object stream {streamNumber}; read as null.", objectNumber);
        return PdfNull.Instance;
    }

    private void Warn(string message, int objectNumber)
    {
        log.Add(PdfWarningCode.MissingObject, message, null, objectNumber);
    }
}
