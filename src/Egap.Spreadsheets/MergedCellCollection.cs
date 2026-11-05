using System.Collections;

namespace Egap.Spreadsheets;

/// <summary>
/// A sheet's merged cells: ranges shown as one cell, with the value and style of their top-left cell. Merged ranges do not
/// overlap.
/// </summary>
public sealed class MergedCellCollection : IReadOnlyCollection<RangeAddress>
{
    private readonly List<RangeAddress> ranges = [];

    internal MergedCellCollection()
    {
    }

    /// <summary>How many merged ranges there are.</summary>
    public int Count => ranges.Count;

    /// <summary>Merges a range of more than one cell; the other cells keep their values, which Excel hides.</summary>
    /// <exception cref="ArgumentException">The range is one cell, or it overlaps a range already merged.</exception>
    public void Add(RangeAddress range)
    {
        if (range.First == range.Last)
        {
            throw new ArgumentException("A merged range has more than one cell.", nameof(range));
        }

        foreach (RangeAddress merged in ranges)
        {
            if (merged.Intersects(range))
            {
                throw new ArgumentException($"{range} overlaps {merged}, which is merged already.", nameof(range));
            }
        }

        ranges.Add(range);
    }

    /// <summary>Unmerges a range that was merged as it is.</summary>
    /// <returns>Whether the range was merged.</returns>
    public bool Remove(RangeAddress range) => ranges.Remove(range);

    /// <summary>Unmerges every range.</summary>
    public void Clear() => ranges.Clear();

    /// <summary>The merged range that holds a cell, or null when the cell is not merged.</summary>
    public RangeAddress? Find(CellAddress cell)
    {
        foreach (RangeAddress merged in ranges)
        {
            if (merged.Contains(cell))
            {
                return merged;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public IEnumerator<RangeAddress> GetEnumerator() => ranges.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Adds a range read from a file, skipping one that is a single cell or overlaps one read before, as Excel would repair it.</summary>
    internal void AddRead(RangeAddress range)
    {
        if (range.First != range.Last && !ranges.Exists(merged => merged.Intersects(range)))
        {
            ranges.Add(range);
        }
    }
}
