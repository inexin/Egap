using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Egap.Spreadsheets;

/// <summary>A workbook's worksheets in tab order; names are unique, case ignored, as in Excel.</summary>
public sealed class WorksheetCollection : IReadOnlyList<Worksheet>
{
    private static readonly System.Buffers.SearchValues<char> ForbiddenInNames = System.Buffers.SearchValues.Create(@":\/?*[]");

    private readonly Workbook workbook;
    private readonly List<Worksheet> sheets = [];

    internal WorksheetCollection(Workbook workbook)
    {
        this.workbook = workbook;
    }

    /// <summary>How many sheets there are.</summary>
    public int Count => sheets.Count;

    /// <summary>The sheet at a zero-based tab position.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no sheet at that position.</exception>
    public Worksheet this[int index] => sheets[index];

    /// <summary>The sheet with a name, case ignored.</summary>
    /// <exception cref="KeyNotFoundException">No sheet has that name.</exception>
    public Worksheet this[string name] =>
        TryGet(name, out Worksheet? sheet) ? sheet : throw new KeyNotFoundException($"The workbook has no sheet named '{name}'.");

    /// <summary>Adds a sheet at the end.</summary>
    /// <param name="name">The sheet's name, or null for the next free <c>SheetN</c>.</param>
    /// <exception cref="ArgumentException">The name is not one Excel allows, or another sheet has it.</exception>
    public Worksheet Add(string? name = null) => Insert(sheets.Count, name);

    /// <summary>Adds a sheet at a tab position.</summary>
    /// <param name="index">The position, from 0 to <see cref="Count"/>.</param>
    /// <param name="name">The sheet's name, or null for the next free <c>SheetN</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The position is past the end.</exception>
    /// <exception cref="ArgumentException">The name is not one Excel allows, or another sheet has it.</exception>
    public Worksheet Insert(int index, string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)sheets.Count, nameof(index));
        name ??= FreeName();
        CheckName(name, null);
        Worksheet sheet = new(workbook, name);
        sheets.Insert(index, sheet);
        return sheet;
    }

    /// <summary>Removes a sheet from the workbook; the sheet can no longer be used.</summary>
    /// <returns>Whether the sheet was in this workbook.</returns>
    public bool Remove(Worksheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (!sheets.Remove(sheet))
        {
            return false;
        }

        workbook.Names.RemoveLocalTo(sheet);
        sheet.Detach();
        return true;
    }

    /// <summary>Moves a sheet to another tab position.</summary>
    /// <exception cref="ArgumentException">The sheet is not in this workbook.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The position is not that of a sheet.</exception>
    public void Move(Worksheet sheet, int index)
    {
        int from = IndexOf(sheet);
        if (from < 0)
        {
            throw new ArgumentException("The sheet is not in this workbook.", nameof(sheet));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)sheets.Count, nameof(index));
        sheets.RemoveAt(from);
        sheets.Insert(index, sheet);
    }

    /// <summary>The tab position of a sheet, or -1 when it is not in this workbook.</summary>
    public int IndexOf(Worksheet sheet) => sheets.IndexOf(sheet);

    /// <summary>Whether a sheet has a name, case ignored.</summary>
    public bool Contains(string name) => TryGet(name, out _);

    /// <summary>Finds the sheet with a name, case ignored.</summary>
    public bool TryGet(string name, [NotNullWhen(true)] out Worksheet? sheet)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (Worksheet candidate in sheets)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                sheet = candidate;
                return true;
            }
        }

        sheet = null;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<Worksheet> GetEnumerator() => sheets.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Checks that a sheet may take a name: 1 to 31 characters, none of <c>: \ / ? * [ ]</c>, no apostrophe at either end, and not another sheet's.</summary>
    internal void CheckName(string name, Worksheet? renamed)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > 31 || name.AsSpan().IndexOfAny(ForbiddenInNames) >= 0 || name[0] == '\'' || name[^1] == '\'')
        {
            throw new ArgumentException(
                $"'{name}' is not a sheet name Excel allows: 1 to 31 characters, none of : \\ / ? * [ ], and no apostrophe at either end.",
                nameof(name));
        }

        if (TryGet(name, out Worksheet? other) && other != renamed)
        {
            throw new ArgumentException($"The workbook already has a sheet named '{other.Name}'.", nameof(name));
        }
    }

    private string FreeName()
    {
        for (int n = sheets.Count + 1; ; n++)
        {
            string name = "Sheet" + n.ToString(CultureInfo.InvariantCulture);
            if (!Contains(name))
            {
                return name;
            }
        }
    }
}
