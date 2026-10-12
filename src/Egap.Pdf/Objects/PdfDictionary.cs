using System.Collections;

using Egap.Pdf.Objects;

namespace Egap.Pdf;

/// <summary>
/// A PDF dictionary: names mapped to objects, kept in the order they were added so a rewrite keeps the file's order.
/// </summary>
/// <remarks>
/// The indexer and <see cref="Entries"/> give values as stored, references included. <see cref="Get(PdfName)"/> and the
/// typed getters follow references, loading the target on first use. Changing a dictionary marks the indirect object it
/// belongs to as changed, so an incremental save writes it.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Named after the PDF object type (design Q1).")]
public sealed class PdfDictionary : PdfObject, IContainer, IEnumerable<KeyValuePair<PdfName, PdfObject>>
{
    private readonly OrderedDictionary<PdfName, PdfObject> entries = [];

    /// <summary>An empty dictionary.</summary>
    public PdfDictionary()
    {
    }

    object? IContainer.Parent { get; set; }

    /// <summary>The number of entries.</summary>
    public int Count => entries.Count;

    /// <summary>The keys, in order.</summary>
    public IEnumerable<PdfName> Keys => entries.Keys;

    /// <summary>The entries as stored, in order; references are not followed.</summary>
    public IEnumerable<KeyValuePair<PdfName, PdfObject>> Entries => entries;

    /// <summary>
    /// The value stored for a key, not following references; <see langword="null"/> when there is none. Setting
    /// <see langword="null"/> removes the entry.
    /// </summary>
    public PdfObject? this[PdfName key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            return entries.GetValueOrDefault(key);
        }

        set
        {
            if (value is null)
            {
                Remove(key);
            }
            else
            {
                Set(key, value);
            }
        }
    }

    /// <summary>Sets a key's value, replacing any value it had.</summary>
    public void Set(PdfName key, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (entries.TryGetValue(key, out PdfObject? old))
        {
            if (ReferenceEquals(old, value))
            {
                return;
            }

            Containment.Adopt(this, value);
            Containment.Release(old);
        }
        else
        {
            Containment.Adopt(this, value);
        }

        entries[key] = value;
        Containment.Changed(this);
    }

    /// <summary>Adds an entry; throws if the key is already there. With the indexer, this allows collection initializers.</summary>
    public void Add(PdfName key, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (entries.ContainsKey(key))
        {
            throw new ArgumentException($"The dictionary already has the key /{key.Value}.", nameof(key));
        }

        Set(key, value);
    }

    /// <summary>Removes a key.</summary>
    /// <returns>Whether the key was there.</returns>
    public bool Remove(PdfName key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!entries.Remove(key, out PdfObject? old))
        {
            return false;
        }

        Containment.Release(old);
        Containment.Changed(this);
        return true;
    }

    /// <summary>Whether the dictionary has the key.</summary>
    public bool ContainsKey(PdfName key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return entries.ContainsKey(key);
    }

    /// <summary>The value for a key with references followed; <see langword="null"/> when there is none.</summary>
    public PdfObject? Get(PdfName key)
    {
        return this[key]?.Resolve();
    }

    /// <summary>
    /// The value for a key with references followed, if it is a <typeparamref name="T"/>; otherwise <see langword="null"/>.
    /// </summary>
    public T? Get<T>(PdfName key)
        where T : PdfObject
    {
        return Get(key) as T;
    }

    /// <summary>A number, integer or real, as a <see cref="double"/>; <see langword="null"/> when the value is not a number.</summary>
    public double? GetNumber(PdfName key)
    {
        return ObjectValues.Number(Get(key));
    }

    /// <summary>
    /// An integer; a real is truncated toward zero, as other readers do. <see langword="null"/> when the value is not a number
    /// or a real is out of range.
    /// </summary>
    public long? GetInt(PdfName key)
    {
        return ObjectValues.Int(Get(key));
    }

    /// <summary>A boolean, or <see langword="null"/>.</summary>
    public bool? GetBool(PdfName key)
    {
        return Get<PdfBoolean>(key)?.Value;
    }

    /// <summary>A name, or <see langword="null"/>.</summary>
    public PdfName? GetName(PdfName key)
    {
        return Get<PdfName>(key);
    }

    /// <summary>A string decoded as text (see <see cref="PdfString.ToText"/>), or <see langword="null"/>.</summary>
    public string? GetText(PdfName key)
    {
        return Get<PdfString>(key)?.ToText();
    }

    /// <summary>
    /// A deep copy: arrays and dictionaries inside are copied too, while references stay references to the same objects.
    /// The copy belongs nowhere, so it can be put anywhere.
    /// </summary>
    public PdfDictionary Clone()
    {
        PdfDictionary copy = new();
        foreach ((PdfName key, PdfObject value) in entries)
        {
            copy.entries[key] = ObjectValues.CloneInto(copy, value);
        }

        return copy;
    }

    /// <summary>Replaces a scalar value in place while loading (as when decrypting), without change tracking.</summary>
    internal void ReplaceParsed(PdfName key, PdfObject value)
    {
        entries[key] = value;
    }

    /// <summary>Removes an entry as a repair made while reading, without change tracking.</summary>
    internal void RemoveParsed(PdfName key)
    {
        if (entries.Remove(key, out PdfObject? old))
        {
            Containment.Release(old);
        }
    }

    /// <summary>Adds an entry while parsing, without change tracking; the last of duplicate keys wins.</summary>
    internal void SetParsed(PdfName key, PdfObject value)
    {
        if (entries.Remove(key, out PdfObject? old))
        {
            Containment.Release(old);
        }

        Containment.Adopt(this, value);
        entries[key] = value;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<PdfName, PdfObject>> GetEnumerator()
    {
        return entries.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
