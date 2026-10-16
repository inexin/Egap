namespace Egap.Documents;

/// <summary>Something that holds a format and must hear of its changes: a node or a style.</summary>
internal interface IFormatOwner
{
    /// <summary>The document the owner belongs to.</summary>
    Document Document { get; }

    void FormatChanged(FormatBase format, string property);
}

/// <summary>
/// A set of formatting properties as Word keeps them (Phase 6 design Q5): every property is nullable, and null means "not
/// set here", so the value comes from the style, the document defaults or the application. The format of a node or style
/// reports each change to its document; a format made with <c>new</c> stands alone, for passing to the methods that copy it.
/// </summary>
public abstract class FormatBase
{
    private object?[]? values;

    private protected FormatBase()
    {
    }

    /// <summary>Whether no property is set.</summary>
    public bool IsEmpty => values is null || Array.TrueForAll(values, v => v is null);

    /// <summary>The node or style this format belongs to, or null for a format that stands alone.</summary>
    internal IFormatOwner? Owner { get; set; }

    /// <summary>How many properties this kind of format has.</summary>
    internal abstract int PropertyCount { get; }

    /// <summary>Clears every property, so all values come from styles and defaults again.</summary>
    public void ClearFormatting()
    {
        if (values is null)
        {
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is not null)
            {
                values[i] = null;
                Owner?.FormatChanged(this, PropertyName(i));
            }
        }
    }

    /// <summary>Sets every property that is set in <paramref name="source"/>, leaving the others as they are.</summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is another kind of format.</exception>
    public void Apply(FormatBase source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.GetType() != GetType())
        {
            throw new ArgumentException($"A {source.GetType().Name} cannot be applied to a {GetType().Name}.", nameof(source));
        }

        if (source.values is null)
        {
            return;
        }

        for (int i = 0; i < source.values.Length; i++)
        {
            if (source.values[i] is { } value)
            {
                SetRaw(i, value);
            }
        }
    }

    /// <summary>The value of property <paramref name="id"/>, or null.</summary>
    internal object? GetRaw(int id)
    {
        return values?[id];
    }

    /// <summary>Sets property <paramref name="id"/>, reporting the change to the owner when the value is new.</summary>
    internal void SetRaw(int id, object? value)
    {
        if (values is null)
        {
            if (value is null)
            {
                return;
            }

            values = new object?[PropertyCount];
        }

        if (Equals(values[id], value))
        {
            return;
        }

        values[id] = value;
        Owner?.FormatChanged(this, PropertyName(id));
    }

    /// <summary>Sets property <paramref name="id"/> without notifying, for clones being built.</summary>
    internal void SetRawSilently(int id, object? value)
    {
        values ??= new object?[PropertyCount];
        values[id] = value;
    }

    /// <summary>Copies every value from <paramref name="source"/> (the same kind), set or not, without notifying: for clones.</summary>
    internal void CopyRaw(FormatBase source)
    {
        values = source.values is null ? null : (object?[])source.values.Clone();
    }

    /// <summary>The name of property <paramref name="id"/>, for change notifications.</summary>
    internal abstract string PropertyName(int id);

    /// <summary>Whether <paramref name="other"/> sets the same properties to equal values (lists compared item by item).</summary>
    internal bool RawEquals(FormatBase other)
    {
        if (other.GetType() != GetType())
        {
            return false;
        }

        for (int id = 0; id < PropertyCount; id++)
        {
            object? a = GetRaw(id);
            object? b = other.GetRaw(id);
            bool same = a is System.Collections.IEnumerable listA and not string && b is System.Collections.IEnumerable listB and not string
                ? listA.Cast<object?>().SequenceEqual(listB.Cast<object?>())
                : Equals(a, b);
            if (!same)
            {
                return false;
            }
        }

        return true;
    }

    private protected T? Get<T>(int id)
        where T : struct
    {
        return values?[id] is T value ? value : null;
    }

    private protected T? GetRef<T>(int id)
        where T : class
    {
        return values?[id] as T;
    }

    private protected void SetEnum<T>(int id, T? value)
        where T : struct, Enum
    {
        if (value is { } v && !Enum.IsDefined(v))
        {
            throw new ArgumentOutOfRangeException(nameof(value), v, $"Not a {typeof(T).Name}.");
        }

        SetRaw(id, value);
    }

    private protected void SetList(int id, List? value)
    {
        if (value is not null && value != List.None && Owner is { } owner && (value.Document != owner.Document || !owner.Document.Lists.Contains(value)))
        {
            throw new ArgumentException($"List {value.Id} is not in this format's document.", nameof(value));
        }

        SetRaw(id, value);
    }

    private protected void SetLength(int id, double? value, bool allowNegative = false)
    {
        if (value is { } v && (!double.IsFinite(v) || (!allowNegative && v < 0)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), v, allowNegative ? "Must be finite." : "Must be finite and not negative.");
        }

        SetRaw(id, value);
    }
}
