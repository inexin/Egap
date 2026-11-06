using System.Runtime.CompilerServices;

namespace Egap.Spreadsheets.Storage;

/// <summary>
/// A sheet's cells in blocks of 32 by 32, made only where cells are: bands of 32 rows, each an array of blocks across the
/// columns. Both levels grow to the furthest block used, and a block makes its rows of 32 cells as cells arrive, so a sheet
/// of a few cells costs a few small arrays. A block goes again when its last cell does.
/// </summary>
internal sealed class CellStore
{
    private const int MaxBands = CellAddress.MaxRows >> CellBlock.Shift;
    private const int MaxBlocksAcross = CellAddress.MaxColumns >> CellBlock.Shift;

    private static readonly CellRecord Vacant;

    private CellBlock?[]?[] bands = [];

    /// <summary>How many blocks the store holds.</summary>
    public int BlockCount { get; private set; }

    /// <summary>How many cells are not vacant.</summary>
    public long Count { get; private set; }

    /// <summary>The record at a cell, or a vacant one; the row and column must be on the sheet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly CellRecord Get(int row, int column)
    {
        CellRecord[]? cells = BlockAt(row, column)?.Rows[row & CellBlock.Mask];
        if (cells is null)
        {
            return ref Vacant;
        }

        return ref cells[column & CellBlock.Mask];
    }

    /// <summary>Whether every cell of a stretch of one column holds a number or a boolean, read a block at a time.</summary>
    public bool OnlyNumbers(int firstRow, int lastRow, int column)
    {
        int place = column & CellBlock.Mask;
        for (int row = firstRow; row <= lastRow; row = (row | CellBlock.Mask) + 1)
        {
            if (BlockAt(row, column) is not { } block)
            {
                return false;
            }

            int end = Math.Min(lastRow, row | CellBlock.Mask);
            for (int r = row; r <= end; r++)
            {
                if (block.Rows[r & CellBlock.Mask] is not { } cells || cells[place].Kind is not (CellKind.Number or CellKind.Boolean))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Stores a record at a cell; a vacant record removes the cell, and its block when it was the block's last.</summary>
    public void Set(int row, int column, in CellRecord record)
    {
        CellBlock? block = BlockAt(row, column);
        if (block is null)
        {
            if (record.IsVacant)
            {
                return;
            }

            block = AddBlock(row, column);
        }

        ref CellRecord[]? cells = ref block.Rows[row & CellBlock.Mask];
        if (cells is null)
        {
            if (record.IsVacant)
            {
                return;
            }

            cells = new CellRecord[CellBlock.Size];
        }

        ref CellRecord slot = ref cells[column & CellBlock.Mask];
        bool was = !slot.IsVacant;
        bool now = !record.IsVacant;
        slot = record;
        if (was != now)
        {
            int change = now ? 1 : -1;
            block.Count += change;
            Count += change;
            if (block.Count == 0)
            {
                bands[row >> CellBlock.Shift]![column >> CellBlock.Shift] = null;
                BlockCount--;
            }
        }
    }

    /// <summary>The cells in a rectangle that are not vacant, row by row and left to right within a row.</summary>
    public IEnumerable<(int Row, int Column)> Occupied(RangeAddress range)
    {
        int firstRow = range.First.Row, firstColumn = range.First.Column, lastRow = range.Last.Row, lastColumn = range.Last.Column;
        int lastBand = Math.Min(lastRow >> CellBlock.Shift, bands.Length - 1);
        for (int b = firstRow >> CellBlock.Shift; b <= lastBand; b++)
        {
            // Read the band again each time: the caller may change cells between items.
            if (bands[b] is null)
            {
                continue;
            }

            int rowFrom = Math.Max(firstRow, b << CellBlock.Shift);
            int rowTo = Math.Min(lastRow, (b << CellBlock.Shift) | CellBlock.Mask);
            for (int row = rowFrom; row <= rowTo; row++)
            {
                for (int x = firstColumn >> CellBlock.Shift; x <= lastColumn >> CellBlock.Shift; x++)
                {
                    CellBlock?[]? band = bands[b];
                    if (band is null || x >= band.Length)
                    {
                        break;
                    }

                    if (band[x]?.Rows[row & CellBlock.Mask] is null)
                    {
                        continue;
                    }

                    int colFrom = Math.Max(firstColumn, x << CellBlock.Shift);
                    int colTo = Math.Min(lastColumn, (x << CellBlock.Shift) | CellBlock.Mask);
                    for (int column = colFrom; column <= colTo; column++)
                    {
                        if (!Get(row, column).IsVacant)
                        {
                            yield return (row, column);
                        }
                    }
                }
            }
        }
    }

    /// <summary>The smallest range holding every cell that is not vacant, or null when there is none.</summary>
    public RangeAddress? Bounds()
    {
        if (Count == 0)
        {
            return null;
        }

        int top = int.MaxValue, left = int.MaxValue, bottom = -1, right = -1;
        for (int b = 0; b < bands.Length; b++)
        {
            CellBlock?[]? band = bands[b];
            if (band is null)
            {
                continue;
            }

            for (int x = 0; x < band.Length; x++)
            {
                CellBlock? block = band[x];
                if (block is null)
                {
                    continue;
                }

                for (int r = 0; r < CellBlock.Size; r++)
                {
                    CellRecord[]? cells = block.Rows[r];
                    for (int c = 0; cells is not null && c < CellBlock.Size; c++)
                    {
                        if (!cells[c].IsVacant)
                        {
                            int row = (b << CellBlock.Shift) | r;
                            int column = (x << CellBlock.Shift) | c;
                            top = Math.Min(top, row);
                            bottom = Math.Max(bottom, row);
                            left = Math.Min(left, column);
                            right = Math.Max(right, column);
                        }
                    }
                }
            }
        }

        return new RangeAddress(top, left, bottom, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private CellBlock? BlockAt(int row, int column)
    {
        CellBlock?[]?[] all = bands;
        int b = row >> CellBlock.Shift;
        if ((uint)b >= (uint)all.Length)
        {
            return null;
        }

        CellBlock?[]? band = all[b];
        int x = column >> CellBlock.Shift;
        return band is not null && (uint)x < (uint)band.Length ? band[x] : null;
    }

    private CellBlock AddBlock(int row, int column)
    {
        int b = row >> CellBlock.Shift;
        int x = column >> CellBlock.Shift;
        if (b >= bands.Length)
        {
            Array.Resize(ref bands, Grow(bands.Length, b + 1, MaxBands));
        }

        ref CellBlock?[]? band = ref bands[b];
        band ??= new CellBlock?[Grow(0, x + 1, MaxBlocksAcross)];
        if (x >= band.Length)
        {
            Array.Resize(ref band, Grow(band.Length, x + 1, MaxBlocksAcross));
        }

        BlockCount++;
        return band[x] = new CellBlock();
    }

    private static int Grow(int length, int needed, int max) => Math.Min(max, Math.Max(needed, Math.Max(4, length * 2)));
}
