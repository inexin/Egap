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
    private ObjectScanner? scanner;
    private int? highestReference;

    public ObjectLoader(PdfSource source, XrefTable xref, PdfReadOptions options, WarningLog log)
    {
        this.source = source;
        Xref = xref;
        this.options = options;
        this.log = log;
        References = this;
    }

    public XrefTable Xref { get; }

    /// <summary>Resolves the references inside loaded objects: the loader itself, or the document's object table.</summary>
    public IObjectResolver References { get; set; }

    /// <summary>Decrypts objects loaded from the file, once the document knows it is encrypted.</summary>
    public Security.StandardSecurityHandler? Decryptor { get; set; }

    /// <summary>The encryption dictionary's object number: that object is never encrypted.</summary>
    public int? EncryptNumber { get; set; }

    /// <summary>The highest object number any reference in the file names, found by scanning it once.</summary>
    public int HighestReferenceInFile()
    {
        lock (gate)
        {
            if (highestReference is null)
            {
                // Object streams are compressed, so the references inside them are found in their decoded bytes.
                int highest = ObjectScanner.HighestReference(source, LargeStreamData());
                foreach (long streamNumber in Xref.Entries.Where(e => e.Value.Kind == XrefEntryKind.InObjectStream).Select(e => e.Value.Offset).Distinct())
                {
                    if (Xref.TryGet((int)streamNumber, out XrefEntry entry) && entry.Kind == XrefEntryKind.InFile && Load((int)streamNumber, entry.Generation) is PdfStream stream)
                    {
                        try
                        {
                            highest = Math.Max(highest, ObjectScanner.HighestReference(PdfSource.FromMemory(stream.GetDecodedBytes())));
                        }
                        catch (PdfException e) when (e is not PdfLimitException)
                        {
                        }
                    }
                }

                highestReference = highest;
            }

            return highestReference.Value;
        }
    }

    /// <summary>
    /// Where the data of the file's large streams lies, in file order: an object much longer than its neighbours is loaded, and
    /// a stream's data, which holds no references, need not be searched for them. A file with a 100 MB attachment is then not
    /// read through when the first object is added.
    /// </summary>
    private List<(long Start, long End)> LargeStreamData()
    {
        const long Large = 256 * 1024;
        long[] offsets = [.. Xref.Entries.Select(e => e.Value).Where(e => e.Kind == XrefEntryKind.InFile && e.Offset >= 0 && e.Offset < source.Length).Select(e => e.Offset).Distinct().Order()];
        List<(long Start, long End)> ranges = [];
        for (int i = 0; i < offsets.Length; i++)
        {
            long next = i + 1 < offsets.Length ? offsets[i + 1] : source.Length;
            if (next - offsets[i] < Large)
            {
                continue;
            }

            foreach ((int number, XrefEntry entry) in Xref.Entries.Where(e => e.Value.Kind == XrefEntryKind.InFile && e.Value.Offset == offsets[i]))
            {
                try
                {
                    if (Load(number, entry.Generation) is PdfStream { Data: SourceData data } && ReferenceEquals(data.Source, source) && data.Offset >= offsets[i] && data.Offset + data.Length <= next)
                    {
                        ranges.Add((data.Offset, data.Offset + data.Length));
                    }
                }
                catch (PdfException e) when (e is not PdfLimitException)
                {
                }

                break;
            }
        }

        return ranges;
    }

    public void Saw(int objectNumber)
    {
        if (!ReferenceEquals(References, this))
        {
            References.Saw(objectNumber);
        }
    }

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
                if (entry.Kind == XrefEntryKind.InFile && Decryptor is not null && objectNumber != EncryptNumber)
                {
                    value = Security.ObjectDecryption.Decrypt(value, objectNumber, entry.Generation, Decryptor);
                }

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
        string problem;
        if (entry.Offset >= source.Length)
        {
            problem = $"Object {objectNumber} is listed at {entry.Offset}, past the end of the file";
        }
        else
        {
            IndirectObject? found = TryParseAt(entry.Offset, out string? error);
            if (found is IndirectObject at && at.ObjectNumber == objectNumber && at.Generation == entry.Generation)
            {
                return at.Value;
            }

            problem = found is IndirectObject other
                ? $"The object at {entry.Offset} is {other.ObjectNumber} {other.Generation}, not {objectNumber} {entry.Generation}"
                : $"Object {objectNumber} is not at {entry.Offset} ({error})";
        }

        return Relocate(objectNumber, entry, problem);
    }

    /// <summary>
    /// Finds an object that is not at its listed offset: first within a kilobyte of it (offsets that are a few bytes off),
    /// then by scanning the whole file, as pdf.js and PDFBox do.
    /// </summary>
    private PdfObject Relocate(int objectNumber, XrefEntry entry, string problem)
    {
        byte[] header = System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"{objectNumber} {entry.Generation} obj"));
        long from = Math.Max(0, Math.Min(entry.Offset, source.Length) - 1024);
        long limit = Math.Min(source.Length, entry.Offset + 1024 + header.Length);
        for (long at = SourceSearch.IndexOf(source, header, from, limit); at >= 0; at = SourceSearch.IndexOf(source, header, at + 1, limit))
        {
            if (at > 0 && CharClass.IsDigit(source.ReadBytes(at - 1, 1)[0]))
            {
                continue;
            }

            if (TryParseAt(at, out _) is IndirectObject nearby && nearby.ObjectNumber == objectNumber && nearby.Generation == entry.Generation)
            {
                log.Add(PdfWarningCode.ObjectRelocated, $"{problem}; it was found at {at}.", at, objectNumber);
                return nearby.Value;
            }
        }

        scanner ??= ObjectScanner.Scan(source, options);
        if (scanner.Objects.TryGetValue(objectNumber, out (long Offset, int Generation) scanned) && scanned.Generation == entry.Generation
            && TryParseAt(scanned.Offset, out _) is IndirectObject found && found.ObjectNumber == objectNumber)
        {
            log.Add(PdfWarningCode.ObjectRelocated, $"{problem}; it was found at {scanned.Offset} by scanning the file.", scanned.Offset, objectNumber);
            return found.Value;
        }

        Warn($"{problem}; read as null.", objectNumber);
        return PdfNull.Instance;
    }

    private IndirectObject? TryParseAt(long offset, out string? error)
    {
        ObjectParser parser = new(new Lexer(new SourceReader(source), log), options, References);
        try
        {
            error = null;
            return parser.ParseIndirectObject(offset);
        }
        catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
        {
            error = e.Message;
            return null;
        }
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
                objects = ObjectStreamReader.Read(stream, streamNumber, options, log, References);
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
