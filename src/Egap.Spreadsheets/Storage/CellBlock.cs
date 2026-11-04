namespace Egap.Spreadsheets.Storage;

/// <summary>
/// 32 rows by 32 columns of cells; a sheet makes one only where it has cells. Each row of 32 cells (512 bytes) is made when
/// its first cell is stored, so a block holding one cell costs a few hundred bytes, and a full one 16 KB and change.
/// </summary>
internal sealed class CellBlock
{
    public const int Shift = 5;
    public const int Size = 1 << Shift;
    public const int Mask = Size - 1;

    /// <summary>The block's rows, each of 32 cells or null while it has none.</summary>
    public readonly CellRecord[]?[] Rows = new CellRecord[]?[Size];

    /// <summary>How many cells in the block are not vacant.</summary>
    public int Count;
}
