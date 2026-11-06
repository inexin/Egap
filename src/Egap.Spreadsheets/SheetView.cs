namespace Egap.Spreadsheets;

/// <summary>How a sheet shows on screen: gridlines, headings, zoom, direction, frozen panes and whether its tab is selected.</summary>
public sealed class SheetView
{
    internal SheetView()
    {
    }

    /// <summary>Whether gridlines show; true by default.</summary>
    public bool ShowGridLines { get; set; } = true;

    /// <summary>Whether row and column headings show; true by default.</summary>
    public bool ShowHeadings { get; set; } = true;

    /// <summary>Whether zero values show as 0 rather than blank; true by default.</summary>
    public bool ShowZeros { get; set; } = true;

    /// <summary>Whether cells show their formulas rather than their results, and values unformatted, in columns of twice the width; it prints so too.</summary>
    public bool ShowFormulas { get; set; }

    /// <summary>Whether the sheet runs right to left, column A at the right.</summary>
    public bool RightToLeft { get; set; }

    /// <summary>Whether the sheet's tab is selected.</summary>
    public bool Selected { get; set; }

    /// <summary>The zoom in per cent, from 10 to 400; 100 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The zoom is outside 10 to 400.</exception>
    public int ZoomScale
    {
        get;
        set => field = value is >= 10 and <= 400 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A zoom is from 10 to 400 per cent.");
    } = 100;

    /// <summary>How many rows at the top stay in place while the sheet scrolls; 0 for none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative or past the sheet.</exception>
    public int FrozenRows
    {
        get;
        set => field = value is >= 0 and < CellAddress.MaxRows ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>How many columns at the left stay in place while the sheet scrolls; 0 for none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative or past the sheet.</exception>
    public int FrozenColumns
    {
        get;
        set => field = value is >= 0 and < CellAddress.MaxColumns ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>The frozen counts as read, to tell whether they changed.</summary>
    internal (int Rows, int Columns) SourceFrozen { get; set; }

    /// <summary>Freezes the rows above and the columns left of a cell, as Excel's Freeze Panes does with that cell selected.</summary>
    public void FreezePanes(CellAddress topLeftScrolling)
    {
        FrozenRows = topLeftScrolling.Row;
        FrozenColumns = topLeftScrolling.Column;
    }
}
