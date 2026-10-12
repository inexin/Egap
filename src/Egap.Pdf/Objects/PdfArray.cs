using System.Collections;

using Egap.Pdf.Objects;

namespace Egap.Pdf;

/// <summary>A PDF array.</summary>
/// <remarks>
/// The indexer gives items as stored, references included; <see cref="Get(int)"/> and the typed getters follow references.
/// Changing an array marks the indirect object it belongs to as changed. Items cannot be <see langword="null"/>: use
/// <see cref="PdfNull.Instance"/>.
/// </remarks>
public sealed class PdfArray : PdfObject, IContainer, IList<PdfObject>
{
    private readonly List<PdfObject> items;

    /// <summary>An empty array.</summary>
    public PdfArray()
    {
        items = [];
    }

    /// <summary>An array holding the given items.</summary>
    public PdfArray(IEnumerable<PdfObject> items)
        : this()
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (PdfObject item in items)
        {
            Add(item);
        }
    }

    object? IContainer.Parent { get; set; }

    /// <summary>The number of items.</summary>
    public int Count => items.Count;

    bool ICollection<PdfObject>.IsReadOnly => false;

    /// <summary>The item as stored, not following references.</summary>
    public PdfObject this[int index]
    {
        get => items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            PdfObject old = items[index];
            if (ReferenceEquals(old, value))
            {
                return;
            }

            Containment.Adopt(this, value);
            Containment.Release(old);
            items[index] = value;
            Containment.Changed(this);
        }
    }

    /// <summary>The item with references followed.</summary>
    public PdfObject Get(int index)
    {
        return items[index].Resolve();
    }

    /// <summary>The item with references followed, if it is a <typeparamref name="T"/>; otherwise <see langword="null"/>.</summary>
    public T? Get<T>(int index)
        where T : PdfObject
    {
        return Get(index) as T;
    }

    /// <summary>A number, integer or real, as a <see cref="double"/>; <see langword="null"/> when the item is not a number.</summary>
    public double? GetNumber(int index)
    {
        return ObjectValues.Number(Get(index));
    }

    /// <summary>An integer; a real is truncated toward zero. <see langword="null"/> when the item is not a number.</summary>
    public long? GetInt(int index)
    {
        return ObjectValues.Int(Get(index));
    }

    /// <summary>A boolean, or <see langword="null"/>.</summary>
    public bool? GetBool(int index)
    {
        return Get<PdfBoolean>(index)?.Value;
    }

    /// <summary>A name, or <see langword="null"/>.</summary>
    public PdfName? GetName(int index)
    {
        return Get<PdfName>(index);
    }

    /// <summary>A string decoded as text (see <see cref="PdfString.ToText"/>), or <see langword="null"/>.</summary>
    public string? GetText(int index)
    {
        return Get<PdfString>(index)?.ToText();
    }

    /// <summary>Adds an item at the end.</summary>
    public void Add(PdfObject item)
    {
        Insert(items.Count, item);
    }

    /// <summary>Inserts an item.</summary>
    public void Insert(int index, PdfObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)items.Count, nameof(index));
        Containment.Adopt(this, item);
        items.Insert(index, item);
        Containment.Changed(this);
    }

    /// <summary>Removes the item at an index.</summary>
    public void RemoveAt(int index)
    {
        PdfObject old = items[index];
        items.RemoveAt(index);
        Containment.Release(old);
        Containment.Changed(this);
    }

    /// <summary>Removes the first item equal to <paramref name="item"/>.</summary>
    public bool Remove(PdfObject item)
    {
        int index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    /// <summary>Removes every item.</summary>
    public void Clear()
    {
        if (items.Count == 0)
        {
            return;
        }

        foreach (PdfObject item in items)
        {
            Containment.Release(item);
        }

        items.Clear();
        Containment.Changed(this);
    }

    /// <summary>The index of the first item equal to <paramref name="item"/>, or -1.</summary>
    public int IndexOf(PdfObject item)
    {
        return items.IndexOf(item);
    }

    /// <summary>Whether an item equal to <paramref name="item"/> is in the array.</summary>
    public bool Contains(PdfObject item)
    {
        return items.Contains(item);
    }

    /// <inheritdoc/>
    public void CopyTo(PdfObject[] array, int arrayIndex)
    {
        items.CopyTo(array, arrayIndex);
    }

    /// <summary>A deep copy: arrays and dictionaries inside are copied too, references stay references.</summary>
    public PdfArray Clone()
    {
        PdfArray copy = new();
        foreach (PdfObject item in items)
        {
            copy.items.Add(ObjectValues.CloneInto(copy, item));
        }

        return copy;
    }

    /// <summary>Replaces a scalar item while loading (as when decrypting), without change tracking.</summary>
    internal void ReplaceParsed(int index, PdfObject item)
    {
        items[index] = item;
    }

    /// <summary>Adds an item while parsing, without change tracking.</summary>
    internal void AddParsed(PdfObject item)
    {
        Containment.Adopt(this, item);
        items.Add(item);
    }

    /// <inheritdoc/>
    public IEnumerator<PdfObject> GetEnumerator()
    {
        return items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
