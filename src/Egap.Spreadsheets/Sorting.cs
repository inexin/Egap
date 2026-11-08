namespace Egap.Spreadsheets;

/// <summary>Which way <see cref="CellRange.Sort(SortOptions, SortKey[])"/> reorders a range.</summary>
public enum SortOrientation
{
    /// <summary>Rows move up and down, ordered by the values in key columns. Excel's default.</summary>
    Rows,

    /// <summary>Columns move left and right, ordered by the values in key rows, as Excel's "Sort left to right".</summary>
    Columns,
}

/// <summary>One key of a sort: a column of the range (a row, when columns are sorted) and its direction.</summary>
/// <param name="Index">
/// The key's column, counted from zero at the range's first column; when columns are sorted, the key's row, counted from zero
/// at the range's first row.
/// </param>
/// <param name="Descending">Whether the key sorts from largest to smallest.</param>
public readonly record struct SortKey(int Index, bool Descending = false);

/// <summary>How <see cref="CellRange.Sort(SortOptions, SortKey[])"/> sorts, as the options of Excel's Sort dialog.</summary>
public sealed class SortOptions
{
    /// <summary>Whether the range's first row (first column, when columns are sorted) is a header that stays in place. Default false.</summary>
    public bool HasHeader { get; init; }

    /// <summary>Whether text sorts with lowercase before uppercase instead of ignoring case. Default false.</summary>
    public bool MatchCase { get; init; }

    /// <summary>Whether rows or columns move. Default <see cref="SortOrientation.Rows"/>.</summary>
    public SortOrientation Orientation { get; init; }
}
