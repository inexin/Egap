using System.Runtime.InteropServices;

namespace Egap.Spreadsheets.Storage;

/// <summary>What a <see cref="CellRecord"/>'s payload holds.</summary>
internal enum CellKind
{
    /// <summary>No value; with a style other than 0 the cell is a formatted blank, which Excel keeps.</summary>
    Empty,

    /// <summary>The payload is a double's bits.</summary>
    Number,

    /// <summary>The payload is an index into the workbook's shared string table.</summary>
    SharedString,

    /// <summary>The payload is 0 or 1.</summary>
    Boolean,

    /// <summary>The payload is a <see cref="CellError"/>.</summary>
    Error,

    /// <summary>The payload is an index into the sheet's formula table, which also holds the formula's last result.</summary>
    Formula,
}

/// <summary>
/// One cell as a sheet stores it, 16 bytes: an 8-byte payload whose meaning <see cref="Kind"/> gives, the index of the cell's
/// style in the workbook's style table, and the kind. The default record is an empty cell in the default style, which is no
/// cell at all.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CellRecord
{
    public long Payload;
    public int Style;
    public CellKind Kind;

    /// <summary>Whether the record stands for no cell: no value and the default style.</summary>
    public readonly bool IsVacant => Kind == CellKind.Empty && Style == 0;
}
