namespace Egap.Spreadsheets.Storage;

/// <summary>
/// The workbook's text values, each kept once, as SpreadsheetML's shared string table keeps them; cells hold an index.
/// Strings no cell uses any more stay in the table; saving writes only the ones in use.
/// </summary>
internal sealed class SharedStringTable
{
    private readonly List<string> strings = [];
    private readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);

    /// <summary>How many strings the table holds.</summary>
    public int Count => strings.Count;

    /// <summary>The string at an index.</summary>
    public string this[int index] => strings[index];

    /// <summary>The index of <paramref name="text"/>, added when it is new.</summary>
    public int Add(string text)
    {
        if (!indexes.TryGetValue(text, out int index))
        {
            index = strings.Count;
            strings.Add(text);
            indexes.Add(text, index);
        }

        return index;
    }
}
