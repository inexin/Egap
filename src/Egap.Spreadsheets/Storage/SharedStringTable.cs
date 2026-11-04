namespace Egap.Spreadsheets.Storage;

/// <summary>
/// The workbook's text values, each kept once, as SpreadsheetML's shared string table keeps them; cells hold an index.
/// Strings no cell uses any more stay in the table; saving writes only the ones in use.
/// </summary>
internal sealed class SharedStringTable
{
    private readonly List<string> strings = [];
    private readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);
    private Dictionary<int, System.Xml.Linq.XElement>? rich;

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

    /// <summary>
    /// Adds a string read from a file at the next index, so the file's indexes stay. <paramref name="raw"/> is the file's
    /// <c>si</c> element for text with runs of formatting or phonetic guides, written back as it was; plain text is shared
    /// with later adds.
    /// </summary>
    public int AddRead(string text, System.Xml.Linq.XElement? raw)
    {
        int index = strings.Count;
        strings.Add(text);
        if (raw is null)
        {
            indexes.TryAdd(text, index);
        }
        else
        {
            (rich ??= [])[index] = raw;
        }

        return index;
    }

    /// <summary>The file's <c>si</c> element for a string with formatting runs, or null for plain text.</summary>
    public System.Xml.Linq.XElement? Rich(int index) => rich?.GetValueOrDefault(index);
}
