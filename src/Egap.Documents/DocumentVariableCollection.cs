using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Egap.Documents;

/// <summary>
/// A document's variables (Word's document variables): text values by name, which DOCVARIABLE fields show. Names compare
/// without regard to case, as in Word, and keep the order they were added in.
/// </summary>
public sealed class DocumentVariableCollection : IReadOnlyDictionary<string, string>
{
    private readonly Document document;
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> order = [];

    internal DocumentVariableCollection(Document document)
    {
        this.document = document;
    }

    /// <summary>How many variables the document has.</summary>
    public int Count => values.Count;

    /// <summary>The names, in the order the variables were added.</summary>
    public IEnumerable<string> Keys => order;

    /// <summary>The values, in the order the variables were added.</summary>
    public IEnumerable<string> Values => order.Select(n => values[n]);

    /// <summary>
    /// A variable's value; setting it adds the variable or changes its value. Getting a name the document has no
    /// variable of throws <see cref="KeyNotFoundException"/>; <see cref="TryGetValue"/> does not.
    /// </summary>
    public string this[string name]
    {
        get => values[name];
        set => Set(name, value);
    }

    /// <summary>Whether the document has a variable named <paramref name="key"/>.</summary>
    public bool ContainsKey(string key)
    {
        return values.ContainsKey(key);
    }

    /// <summary>A variable's value, false when there is none of that name.</summary>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
    {
        return values.TryGetValue(key, out value);
    }

    /// <summary>Adds a variable or changes its value.</summary>
    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        if (values.TryGetValue(name, out string? old))
        {
            if (old == value)
            {
                return;
            }
        }
        else
        {
            order.Add(name);
        }

        values[name] = value;
        Changed(name);
    }

    /// <summary>Removes a variable; false when there was none of that name.</summary>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!values.Remove(name))
        {
            return false;
        }

        order.RemoveAt(order.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
        Changed(name);
        return true;
    }

    /// <summary>Removes every variable.</summary>
    public void Clear()
    {
        if (values.Count == 0)
        {
            return;
        }

        values.Clear();
        order.Clear();
        Changed(null);
    }

    /// <summary>The variables as name and value pairs, in the order they were added.</summary>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        foreach (string name in order)
        {
            yield return new KeyValuePair<string, string>(name, values[name]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>The same names (by case-insensitive comparison) with the same values in the same order.</summary>
    internal bool SameAs(IReadOnlyList<(string Name, string Value)> other)
    {
        if (other.Count != order.Count)
        {
            return false;
        }

        for (int i = 0; i < order.Count; i++)
        {
            if (!string.Equals(order[i], other[i].Name, StringComparison.Ordinal) || values[order[i]] != other[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    internal void CopyFrom(DocumentVariableCollection source)
    {
        values.Clear();
        order.Clear();
        foreach (string name in source.order)
        {
            order.Add(name);
            values[name] = source.values[name];
        }
    }

    private void Changed(string? name)
    {
        document.NotifyChanged(document, DocumentChangeKind.PropertyChanged, document, property: name is null ? "Variables" : "Variables." + name);
    }
}
