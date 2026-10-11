using System.Collections;

using Egap.Pdf.IO;
using Egap.Pdf.Objects;
using Egap.Pdf.Parsing;

namespace Egap.Pdf;

/// <summary>
/// A document's indirect objects, by number. Objects load from the file the first time they are asked for; changing one,
/// anywhere in its tree, marks it as changed, so an incremental save writes only what changed.
/// </summary>
/// <remarks>Thread safe for reading; edits from several threads at once need the caller's own locking.</remarks>
public sealed class PdfObjectTable : IEnumerable<PdfReference>, IObjectResolver
{
    private readonly SortedDictionary<int, IndirectEntry> entries = [];
    private readonly ObjectLoader? loader;
    private readonly WarningLog log;
    private readonly Lock gate = new();
    private int nextNumber;

    internal PdfObjectTable(ObjectLoader? loader, WarningLog log)
    {
        this.loader = loader;
        this.log = log;
        if (loader is not null)
        {
            loader.References = this;
            foreach ((int number, XrefEntry entry) in loader.Xref.Entries)
            {
                if (entry.Kind != XrefEntryKind.Free && number > 0)
                {
                    entries[number] = new IndirectEntry(this, number, entry.Kind == XrefEntryKind.InFile ? entry.Generation : 0, entry);
                }
            }
        }

        // New objects number after the highest the file uses, free entries included, as qpdf does; /Size is not trusted.
        int highest = loader is null ? 0 : loader.Xref.Entries.Select(e => e.Key).DefaultIfEmpty(0).Max();
        nextNumber = highest == int.MaxValue ? int.MaxValue : highest + 1;
    }

    /// <summary>The number of objects, loaded or not.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Values.Count(e => e.State != EntryState.Deleted);
            }
        }
    }

    /// <summary>Whether any object was added, changed or removed since the document was opened.</summary>
    public bool IsChanged
    {
        get
        {
            lock (gate)
            {
                return entries.Values.Any(e => e.State is EntryState.Changed or EntryState.New or EntryState.Deleted);
            }
        }
    }

    /// <summary>The object a reference points to, loading it on first use; <see cref="PdfNull"/> when there is none.</summary>
    public PdfObject this[PdfReference reference]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(reference);
            return Resolve(reference);
        }
    }

    /// <summary>The object with the given number at its current generation, or <see cref="PdfNull"/> when there is none.</summary>
    public PdfObject Get(int objectNumber)
    {
        lock (gate)
        {
            return entries.TryGetValue(objectNumber, out IndirectEntry? entry) ? Value(entry) : PdfNull.Instance;
        }
    }

    /// <summary>The reference to an object number at its current generation, or <see langword="null"/> when there is none.</summary>
    public PdfReference? GetReference(int objectNumber)
    {
        lock (gate)
        {
            return entries.TryGetValue(objectNumber, out IndirectEntry? entry) && entry.State != EntryState.Deleted ? entry.Reference : null;
        }
    }

    /// <summary>Whether the reference points to an object of the document.</summary>
    public bool Contains(PdfReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (gate)
        {
            return entries.TryGetValue(reference.ObjectNumber, out IndirectEntry? entry) && entry.State != EntryState.Deleted && entry.Generation == reference.Generation;
        }
    }

    /// <summary>
    /// Adds an indirect object and returns the reference to store wherever it is used. An array, dictionary or stream must
    /// not already be in another place (use its <c>Clone()</c>).
    /// </summary>
    public PdfReference Add(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is PdfReference)
        {
            throw new ArgumentException("An indirect object cannot be a reference.", nameof(value));
        }

        lock (gate)
        {
            IndirectEntry entry = new(this, NextNumber(), 0, null) { State = EntryState.New };
            entry.Attach(value);
            entries[entry.Number] = entry;
            return entry.Reference;
        }
    }

    /// <summary>Replaces the object a reference points to. The new value must not already be in another place.</summary>
    public void Set(PdfReference reference, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(value);
        if (value is PdfReference)
        {
            throw new ArgumentException("An indirect object cannot be a reference.", nameof(value));
        }

        lock (gate)
        {
            IndirectEntry entry = Existing(reference);
            PdfObject? old = entry.Loaded ? entry.Value : null;
            if (ReferenceEquals(old, value))
            {
                return;
            }

            entry.Attach(value);
            Containment.Release(old);
            entry.MarkChanged();
        }
    }

    /// <summary>
    /// Removes an object. References to it then resolve to <see cref="PdfNull"/>; on save its number becomes free with the
    /// next generation.
    /// </summary>
    /// <returns>Whether the object was there.</returns>
    public bool Remove(PdfReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (gate)
        {
            if (!entries.TryGetValue(reference.ObjectNumber, out IndirectEntry? entry) || entry.State == EntryState.Deleted || entry.Generation != reference.Generation)
            {
                return false;
            }

            if (entry.Loaded)
            {
                Containment.Release(entry.Value);
            }

            if (entry.State == EntryState.New)
            {
                entries.Remove(entry.Number);
            }
            else
            {
                entry.State = EntryState.Deleted;
            }

            entry.Detach();
            return true;
        }
    }

    /// <summary>The references to every object, in object number order. Nothing is loaded by enumerating.</summary>
    public IEnumerator<PdfReference> GetEnumerator()
    {
        List<PdfReference> references;
        lock (gate)
        {
            references = [.. entries.Values.Where(e => e.State != EntryState.Deleted).Select(e => e.Reference)];
        }

        return references.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    PdfObject IObjectResolver.Resolve(PdfReference reference)
    {
        return Resolve(reference);
    }

    internal PdfObject Resolve(PdfReference reference)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(reference.ObjectNumber, out IndirectEntry? entry) || entry.Generation != reference.Generation)
            {
                return PdfNull.Instance;
            }

            return Value(entry);
        }
    }

    /// <summary>Every entry, for the writer.</summary>
    internal IEnumerable<IndirectEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return [.. entries.Values];
            }
        }
    }

    private PdfObject Value(IndirectEntry entry)
    {
        if (entry.State == EntryState.Deleted)
        {
            return PdfNull.Instance;
        }

        if (entry.Loaded)
        {
            return entry.Value!;
        }

        if (entry.Loading)
        {
            // Asked for while it loads, as through a stream /Length that points at its own stream.
            log.Add(PdfWarningCode.MissingObject, $"Object {entry.Number} refers to itself while loading; read as null.", null, entry.Number);
            return PdfNull.Instance;
        }

        entry.Loading = true;
        try
        {
            PdfObject value = loader!.Load(entry.Number, entry.Generation);
            entry.Attach(value);
            return value;
        }
        finally
        {
            entry.Loading = false;
        }
    }

    /// <summary>The next number after the highest; when a damaged file used up the top, the lowest unused number.</summary>
    private int NextNumber()
    {
        if (nextNumber < int.MaxValue)
        {
            return nextNumber++;
        }

        int candidate = 1;
        foreach (int number in entries.Keys)
        {
            if (number > candidate)
            {
                break;
            }

            candidate = number + 1;
        }

        return candidate < int.MaxValue ? candidate : throw new InvalidOperationException("The document has no object numbers left.");
    }

    private IndirectEntry Existing(PdfReference reference)
    {
        if (!entries.TryGetValue(reference.ObjectNumber, out IndirectEntry? entry) || entry.State == EntryState.Deleted || entry.Generation != reference.Generation)
        {
            throw new KeyNotFoundException($"The document has no object {reference}.");
        }

        if (!entry.Loaded)
        {
            Value(entry);
        }

        return entry;
    }
}

/// <summary>Where an indirect object stands since the document was opened.</summary>
internal enum EntryState
{
    /// <summary>As in the file (loaded or not).</summary>
    Unchanged,
    Changed,
    New,
    Deleted,
}

/// <summary>One indirect object: its number, generation, where it is in the file, its value once loaded, and its state.</summary>
internal sealed class IndirectEntry(PdfObjectTable table, int number, int generation, XrefEntry? source) : IObjectOwner
{
    public int Number { get; } = number;

    public int Generation { get; } = generation;

    /// <summary>Where the object is in the file; null for a new object.</summary>
    public XrefEntry? Source { get; } = source;

    public PdfReference Reference { get; } = new(number, generation, table);

    public PdfObject? Value { get; private set; }

    public bool Loaded => Value is not null;

    public bool Loading { get; set; }

    public EntryState State { get; set; }

    public void Attach(PdfObject value)
    {
        Containment.Bind(value, this);
        Value = value;
    }

    public void Detach()
    {
        Value = null;
    }

    public void MarkChanged()
    {
        if (State == EntryState.Unchanged)
        {
            State = EntryState.Changed;
        }
    }

    void IObjectOwner.OnChanged()
    {
        MarkChanged();
    }
}
