namespace Egap.Spreadsheets.Storage;

/// <summary>A formula's text, without its leading <c>=</c>, and the value it last gave.</summary>
internal sealed class FormulaEntry(string text, CellValue result)
{
    public string Text = text;
    public CellValue Result = result;
}

/// <summary>A sheet's formulas; a formula cell's record holds its index. Freed slots are used again.</summary>
internal sealed class FormulaTable
{
    private readonly List<FormulaEntry?> entries = [];
    private readonly Stack<int> free = [];

    /// <summary>How many formulas the table holds.</summary>
    public int Count => entries.Count - free.Count;

    public FormulaEntry this[int index] => entries[index]!;

    public int Add(string text, CellValue result)
    {
        FormulaEntry entry = new(text, result);
        if (free.TryPop(out int index))
        {
            entries[index] = entry;
            return index;
        }

        entries.Add(entry);
        return entries.Count - 1;
    }

    public void Remove(int index)
    {
        entries[index] = null;
        free.Push(index);
    }
}
