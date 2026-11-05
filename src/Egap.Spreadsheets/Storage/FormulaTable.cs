namespace Egap.Spreadsheets.Storage;

/// <summary>A formula's text, without its leading <c>=</c>, and the value it last gave.</summary>
internal sealed class FormulaEntry(string text, CellValue result)
{
    public string Text = text;
    public CellValue Result = result;

    /// <summary>
    /// The attributes of the file's <c>f</c> element: its kind (shared, array or data table), range, shared index and flags.
    /// A cell that follows a shared formula has these and an empty <see cref="Text"/>.
    /// </summary>
    public System.Xml.Linq.XAttribute[]? Attributes;

    /// <summary>The value of one of <see cref="Attributes"/>, or null.</summary>
    public string? Attribute(string name)
    {
        foreach (System.Xml.Linq.XAttribute attribute in Attributes ?? [])
        {
            if (attribute.Name.LocalName == name)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>The shared index of a shared formula, the first cell's or a follower's; null for any other formula.</summary>
    public string? SharedIndex => Attribute("t") == "shared" ? Attribute("si") : null;

    /// <summary>The range an array formula fills, for the formula in its first cell; null for any other formula.</summary>
    public RangeAddress? ArrayRange => Attribute("t") == "array" && Text.Length > 0
        ? RangeAddress.TryParse(Attribute("ref"), out RangeAddress range) ? range : null
        : null;
}

/// <summary>A sheet's formulas; a formula cell's record holds its index. Freed slots are used again.</summary>
internal sealed class FormulaTable
{
    private readonly List<FormulaEntry?> entries = [];
    private readonly Stack<int> free = [];

    /// <summary>How many formulas the table holds.</summary>
    public int Count => entries.Count - free.Count;

    public FormulaEntry this[int index] => entries[index]!;

    public int Add(string text, CellValue result, System.Xml.Linq.XAttribute[]? attributes = null)
    {
        FormulaEntry entry = new(text, result) { Attributes = attributes };
        if (free.TryPop(out int index))
        {
            entries[index] = entry;
            return index;
        }

        entries.Add(entry);
        return entries.Count - 1;
    }

    /// <summary>The formulas the table holds.</summary>
    public IEnumerable<FormulaEntry> Entries => entries.OfType<FormulaEntry>();

    /// <summary>The formulas the table holds, with the index a cell's record holds for each.</summary>
    public IEnumerable<(int Index, FormulaEntry Entry)> Indexed()
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] is { } entry)
            {
                yield return (i, entry);
            }
        }
    }

    public void Remove(int index)
    {
        entries[index] = null;
        free.Push(index);
    }
}
