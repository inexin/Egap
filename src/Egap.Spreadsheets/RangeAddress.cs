using System.Diagnostics.CodeAnalysis;

namespace Egap.Spreadsheets;

/// <summary>
/// A rectangle of cells on a sheet, from its top-left cell to its bottom-right cell, written in A1 style as <c>A1:D20</c>.
/// A single cell is written as <c>B4</c>, whole columns as <c>A:C</c> and whole rows as <c>2:5</c>.
/// </summary>
public readonly record struct RangeAddress : ISpanFormattable
{
    /// <summary>Creates a range from two corners in any order; the range is the rectangle they span.</summary>
    public RangeAddress(CellAddress first, CellAddress last)
    {
        First = new CellAddress(Math.Min(first.Row, last.Row), Math.Min(first.Column, last.Column));
        Last = new CellAddress(Math.Max(first.Row, last.Row), Math.Max(first.Column, last.Column));
    }

    /// <summary>Creates a range from zero-based corner rows and columns, in any order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A row or column is off the sheet.</exception>
    public RangeAddress(int firstRow, int firstColumn, int lastRow, int lastColumn)
        : this(new CellAddress(firstRow, firstColumn), new CellAddress(lastRow, lastColumn))
    {
    }

    /// <summary>The top-left cell.</summary>
    public CellAddress First { get; }

    /// <summary>The bottom-right cell.</summary>
    public CellAddress Last { get; }

    /// <summary>The number of rows the range spans.</summary>
    public int RowCount => Last.Row - First.Row + 1;

    /// <summary>The number of columns the range spans.</summary>
    public int ColumnCount => Last.Column - First.Column + 1;

    /// <summary>The number of cells the range spans; a whole sheet holds over 17 billion.</summary>
    public long CellCount => (long)RowCount * ColumnCount;

    /// <summary>Whether the range spans every row of its columns, as <c>A:C</c> does.</summary>
    public bool IsWholeColumns => First.Row == 0 && Last.Row == CellAddress.MaxRows - 1;

    /// <summary>Whether the range spans every column of its rows, as <c>2:5</c> does.</summary>
    public bool IsWholeRows => First.Column == 0 && Last.Column == CellAddress.MaxColumns - 1;

    /// <summary>Whether the cell at <paramref name="address"/> is in the range.</summary>
    public bool Contains(CellAddress address) =>
        address.Row >= First.Row && address.Row <= Last.Row && address.Column >= First.Column && address.Column <= Last.Column;

    /// <summary>Whether every cell of <paramref name="other"/> is in this range.</summary>
    public bool Contains(RangeAddress other) => Contains(other.First) && Contains(other.Last);

    /// <summary>Whether the two ranges share at least one cell.</summary>
    public bool Intersects(RangeAddress other) =>
        First.Row <= other.Last.Row && other.First.Row <= Last.Row
        && First.Column <= other.Last.Column && other.First.Column <= Last.Column;

    /// <summary>
    /// Reads an A1 range such as <c>A1:D20</c>, <c>B4</c>, <c>A:C</c> or <c>2:5</c>; dollar signs are allowed and ignored,
    /// and the corners may come in any order.
    /// </summary>
    /// <exception cref="FormatException">The text is not a range on an Excel sheet.</exception>
    public static RangeAddress Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.AsSpan());
    }

    /// <summary>Reads an A1 range; see <see cref="Parse(string)"/>.</summary>
    /// <exception cref="FormatException">The text is not a range on an Excel sheet.</exception>
    public static RangeAddress Parse(ReadOnlySpan<char> text)
    {
        return TryParse(text, out RangeAddress range)
            ? range
            : throw new FormatException($"'{text}' is not a range such as A1:D20, A:C or 2:5.");
    }

    /// <summary>Reads an A1 range, or returns false when the text is not one.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out RangeAddress range)
    {
        if (text is null)
        {
            range = default;
            return false;
        }

        return TryParse(text.AsSpan(), out range);
    }

    /// <summary>Reads an A1 range, or returns false when the text is not one.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out RangeAddress range)
    {
        range = default;
        int colon = text.IndexOf(':');
        if (colon < 0)
        {
            if (!CellAddress.TryParse(text, out CellAddress cell))
            {
                return false;
            }

            range = new RangeAddress(cell, cell);
            return true;
        }

        ReadOnlySpan<char> left = text[..colon];
        ReadOnlySpan<char> right = text[(colon + 1)..];
        if (CellAddress.TryParse(left, out CellAddress a) && CellAddress.TryParse(right, out CellAddress b))
        {
            range = new RangeAddress(a, b);
            return true;
        }

        if (TryParseColumn(left, out int c1) && TryParseColumn(right, out int c2))
        {
            range = new RangeAddress(0, c1, CellAddress.MaxRows - 1, c2);
            return true;
        }

        if (CellAddress.TryParseRowNumber(left.TrimStart('$'), out int r1) && CellAddress.TryParseRowNumber(right.TrimStart('$'), out int r2))
        {
            range = new RangeAddress(r1 - 1, 0, r2 - 1, CellAddress.MaxColumns - 1);
            return true;
        }

        return false;
    }

    /// <summary>The range in A1 style: <c>B4</c> for one cell, <c>A:C</c> for whole columns, <c>2:5</c> for whole rows, else <c>A1:D20</c>.</summary>
    public override string ToString()
    {
        Span<char> text = stackalloc char[24];
        TryFormat(text, out int written, default, null);
        return new string(text[..written]);
    }

    /// <summary>The range in A1 style; <paramref name="format"/> and <paramref name="formatProvider"/> are not used.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Writes the range in A1 style without allocating; see <see cref="ToString()"/>.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        charsWritten = 0;
        if (First == Last)
        {
            return First.TryFormat(destination, out charsWritten, default, null);
        }

        // Whole rows win over whole columns only when the range is not the whole sheet, which Excel writes as 1:1048576.
        if (IsWholeRows)
        {
            return TryFormatPair(destination, First.Row + 1, Last.Row + 1, out charsWritten);
        }

        if (IsWholeColumns)
        {
            Span<char> name = stackalloc char[3];
            int a = CellAddress.WriteColumnName(First.Column, name);
            if (!name[..a].TryCopyTo(destination) || destination.Length < a + 1)
            {
                return false;
            }

            destination[a] = ':';
            int b = CellAddress.WriteColumnName(Last.Column, name);
            if (!name[..b].TryCopyTo(destination[(a + 1)..]))
            {
                return false;
            }

            charsWritten = a + 1 + b;
            return true;
        }

        if (!First.TryFormat(destination, out int left, default, null) || destination.Length <= left)
        {
            return false;
        }

        destination[left] = ':';
        if (!Last.TryFormat(destination[(left + 1)..], out int right, default, null))
        {
            return false;
        }

        charsWritten = left + 1 + right;
        return true;
    }

    private static bool TryFormatPair(Span<char> destination, int first, int last, out int charsWritten)
    {
        charsWritten = 0;
        if (!first.TryFormat(destination, out int a, default, System.Globalization.CultureInfo.InvariantCulture) || destination.Length <= a)
        {
            return false;
        }

        destination[a] = ':';
        if (!last.TryFormat(destination[(a + 1)..], out int b, default, System.Globalization.CultureInfo.InvariantCulture))
        {
            return false;
        }

        charsWritten = a + 1 + b;
        return true;
    }

    private static bool TryParseColumn(ReadOnlySpan<char> text, out int column)
    {
        column = 0;
        text = text.TrimStart('$');
        if (text.IsEmpty || text.Length > 3)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }

            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        column--;
        return column < CellAddress.MaxColumns;
    }
}
