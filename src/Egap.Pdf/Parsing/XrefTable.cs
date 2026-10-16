namespace Egap.Pdf.Parsing;

/// <summary>Where an object lives, from the cross-reference (ISO 32000-2, 7.5.4 and 7.5.8).</summary>
internal enum XrefEntryKind : byte
{
    Free,

    /// <summary>At a byte offset in the file.</summary>
    InFile,

    /// <summary>At an index inside an object stream.</summary>
    InObjectStream,
}

/// <summary>One cross-reference entry.</summary>
/// <param name="Kind">Where the object lives.</param>
/// <param name="Offset">The byte offset (in file), or the object stream's number (in an object stream).</param>
/// <param name="Generation">The generation (in file, or the next generation for a free entry), or the index in the object stream.</param>
internal readonly record struct XrefEntry(XrefEntryKind Kind, long Offset, int Generation)
{
    public static XrefEntry FreeEntry(int generation) => new(XrefEntryKind.Free, 0, generation);
}

/// <summary>One cross-reference section as found in the file: a table or a stream, with its trailer dictionary.</summary>
/// <param name="Offset">Where the section starts.</param>
/// <param name="IsStream">Whether it is a cross-reference stream.</param>
/// <param name="Trailer">The trailer dictionary (for a stream, the stream's dictionary).</param>
/// <param name="IsHybrid">Whether it is a hybrid file's <c>/XRefStm</c> stream, part of the table section before it rather
/// than a section of its own in the <c>/Prev</c> chain.</param>
internal sealed record XrefSection(long Offset, bool IsStream, PdfDictionary Trailer, bool IsHybrid = false);

/// <summary>
/// The merged cross-reference of a file: for each object number, the newest entry, and the trailer with the newest value
/// of each key. <see cref="Sections"/> lists the sections newest first, as read.
/// </summary>
internal sealed class XrefTable
{
    private readonly Dictionary<int, XrefEntry> entries = [];

    public PdfDictionary Trailer { get; } = new();

    public List<XrefSection> Sections { get; } = [];

    public int Count => entries.Count;

    public IEnumerable<KeyValuePair<int, XrefEntry>> Entries => entries;

    public bool TryGet(int objectNumber, out XrefEntry entry)
    {
        return entries.TryGetValue(objectNumber, out entry);
    }

    /// <summary>Adds an entry unless a newer one for the object is already there. Returns whether it was added.</summary>
    public bool AddIfAbsent(int objectNumber, XrefEntry entry)
    {
        return entries.TryAdd(objectNumber, entry);
    }

    /// <summary>Replaces an entry; used when a hybrid file's stream fills in what its table lists as free.</summary>
    public void Set(int objectNumber, XrefEntry entry)
    {
        entries[objectNumber] = entry;
    }
}
