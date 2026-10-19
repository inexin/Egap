using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Egap.Documents;

/// <summary>
/// One record of data for a mail merge: the value of each merge field, and the records of each repeated region. Implement
/// it to stream records from any source; <see cref="MergeData"/> makes one from dictionaries, rows or objects.
/// </summary>
public interface IMergeData
{
    /// <summary>The value of the field named <paramref name="name"/> (compared without regard to case).</summary>
    /// <param name="name">The field name, as the MERGEFIELD gives it.</param>
    /// <param name="value">The value: text, a number, a date or null for an empty field.</param>
    /// <returns>False when the record has no such field.</returns>
    bool TryGetValue(string name, out object? value);

    /// <summary>The records of the repeated region named <paramref name="name"/>, or null when the record has no such region.</summary>
    /// <param name="name">The region name, from TableStart:Name or the prefix of a table row's fields.</param>
    /// <returns>The region's records, or null.</returns>
    IEnumerable<IMergeData>? GetRegion(string name) => null;
}

/// <summary>Merge data from dictionaries, rows of values with column names, and objects.</summary>
/// <remarks>
/// A value that is itself a sequence of <see cref="IMergeData"/> or of dictionaries is a repeated region of that name. A
/// dictionary or <see cref="IMergeData"/> value is reached with a dotted name, such as <c>Customer.Name</c>.
/// </remarks>
public static class MergeData
{
    /// <summary>A record of the values in <paramref name="values"/>, by name.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public static IMergeData From(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new DictionaryRecord(values);
    }

    /// <summary>A record for each dictionary, read as it is enumerated.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is null.</exception>
    public static IEnumerable<IMergeData> From(IEnumerable<IReadOnlyDictionary<string, object?>> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.Select(From);
    }

    /// <summary>A record for each row, its values named by <paramref name="columns"/> in order, as a CSV file or a query gives them.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IEnumerable<IMergeData> From(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Count; i++)
        {
            index.TryAdd(columns[i], i);
        }

        return rows.Select(row => (IMergeData)new RowRecord(index, row));
    }

    /// <summary>A record of an object's public properties, by name.</summary>
    /// <remarks>
    /// The properties are those of <typeparamref name="T"/>, kept when the app is trimmed. A property holding a sequence of
    /// <see cref="IMergeData"/> (such as <see cref="FromObjects{T}(IEnumerable{T})"/> of its items) is a region.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static IMergeData FromObject<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ObjectRecord<T>(value);
    }

    /// <summary>A record for each object, of its public properties, read as the sequence is enumerated.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public static IEnumerable<IMergeData> FromObjects<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Select(v => FromObject(v));
    }

    /// <summary>A field's value through a dotted name: the whole name first, then the first part's record for the rest.</summary>
    internal static bool TryGetPath(IMergeData record, string name, out object? value)
    {
        if (record.TryGetValue(name, out value))
        {
            return true;
        }

        int dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && record.TryGetValue(name[..dot], out object? inner) && AsRecord(inner) is { } nested)
        {
            return TryGetPath(nested, name[(dot + 1)..], out value);
        }

        value = null;
        return false;
    }

    /// <summary>A value read as a record, when it is one.</summary>
    internal static IMergeData? AsRecord(object? value) => value switch
    {
        IMergeData record => record,
        IReadOnlyDictionary<string, object?> dictionary => new DictionaryRecord(dictionary),
        _ => null,
    };

    /// <summary>A value read as a region's records, when it is a sequence of records.</summary>
    internal static IEnumerable<IMergeData>? AsRegion(object? value) => value switch
    {
        IEnumerable<IMergeData> records => records,
        IEnumerable<IReadOnlyDictionary<string, object?>> dictionaries => dictionaries.Select(From),
        _ => null,
    };

    private sealed class DictionaryRecord(IReadOnlyDictionary<string, object?> values) : IMergeData
    {
        public bool TryGetValue(string name, out object? value)
        {
            if (values.TryGetValue(name, out value))
            {
                return true;
            }

            foreach ((string key, object? item) in values)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = item;
                    return true;
                }
            }

            return false;
        }

        public IEnumerable<IMergeData>? GetRegion(string name) => TryGetValue(name, out object? value) ? AsRegion(value) : null;
    }

    private sealed class RowRecord(Dictionary<string, int> index, IReadOnlyList<object?> row) : IMergeData
    {
        public bool TryGetValue(string name, out object? value)
        {
            if (index.TryGetValue(name, out int i) && i < row.Count)
            {
                value = row[i];
                return true;
            }

            value = null;
            return false;
        }
    }

    private sealed class ObjectRecord<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) : IMergeData
    {
        private static readonly Dictionary<string, PropertyInfo> Properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public bool TryGetValue(string name, out object? result)
        {
            if (Properties.TryGetValue(name, out PropertyInfo? property))
            {
                result = property.GetValue(value);
                return true;
            }

            result = null;
            return false;
        }

        public IEnumerable<IMergeData>? GetRegion(string name) => TryGetValue(name, out object? result) && result is not string and IEnumerable ? AsRegion(result) : null;
    }
}
