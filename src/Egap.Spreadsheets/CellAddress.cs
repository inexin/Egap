using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Egap.Spreadsheets;

/// <summary>
/// Where a cell is on a sheet: a zero-based row and column, written in A1 style as Excel shows it (<c>B4</c> is row 3,
/// column 1). Rows go up to 1,048,575 and columns up to 16,383 (<c>XFD</c>), the size of an Excel sheet.
/// </summary>
public readonly record struct CellAddress : ISpanFormattable
{
    /// <summary>The number of rows on a sheet, 1,048,576.</summary>
    public const int MaxRows = 1 << 20;

    /// <summary>The number of columns on a sheet, 16,384 (A to XFD).</summary>
    public const int MaxColumns = 1 << 14;

    /// <summary>Creates an address from a zero-based row and column.</summary>
    /// <param name="row">The row, from 0 to 1,048,575.</param>
    /// <param name="column">The column, from 0 (A) to 16,383 (XFD).</param>
    /// <exception cref="ArgumentOutOfRangeException">The row or column is off the sheet.</exception>
    public CellAddress(int row, int column)
    {
        CheckRow(row);
        CheckColumn(column);
        Row = row;
        Column = column;
    }

    /// <summary>The zero-based row: 0 is row 1.</summary>
    public int Row { get; }

    /// <summary>The zero-based column: 0 is column A.</summary>
    public int Column { get; }

    /// <summary>Reads an A1 reference such as <c>B4</c>, <c>$B$4</c> or <c>xfd1048576</c>; dollar signs are allowed and ignored.</summary>
    /// <exception cref="FormatException">The text is not a cell reference on an Excel sheet.</exception>
    public static CellAddress Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.AsSpan());
    }

    /// <summary>Reads an A1 reference such as <c>B4</c>; see <see cref="Parse(string)"/>.</summary>
    /// <exception cref="FormatException">The text is not a cell reference on an Excel sheet.</exception>
    public static CellAddress Parse(ReadOnlySpan<char> text)
    {
        return TryParse(text, out CellAddress address)
            ? address
            : throw new FormatException($"'{text}' is not a cell reference such as B4.");
    }

    /// <summary>Reads an A1 reference such as <c>B4</c>, or returns false when the text is not one.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out CellAddress address)
    {
        if (text is null)
        {
            address = default;
            return false;
        }

        return TryParse(text.AsSpan(), out address);
    }

    /// <summary>Reads an A1 reference such as <c>B4</c>, or returns false when the text is not one.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out CellAddress address)
    {
        address = default;
        int i = 0;
        if (i < text.Length && text[i] == '$')
        {
            i++;
        }

        int column = 0;
        int letters = 0;
        while (i < text.Length && char.IsAsciiLetter(text[i]))
        {
            if (++letters > 3)
            {
                return false;
            }

            column = column * 26 + (char.ToUpperInvariant(text[i]) - 'A' + 1);
            i++;
        }

        if (letters == 0 || column > MaxColumns)
        {
            return false;
        }

        if (i < text.Length && text[i] == '$')
        {
            i++;
        }

        if (!TryParseRowNumber(text[i..], out int row))
        {
            return false;
        }

        address = new CellAddress(row - 1, column - 1);
        return true;
    }

    /// <summary>The A1 name of a zero-based column: 0 is <c>A</c>, 25 <c>Z</c>, 26 <c>AA</c> and 16,383 <c>XFD</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The column is off the sheet.</exception>
    public static string ColumnName(int column)
    {
        CheckColumn(column);
        Span<char> name = stackalloc char[3];
        int length = WriteColumnName(column, name);
        return new string(name[..length]);
    }

    /// <summary>The zero-based column an A1 column name stands for: <c>A</c> is 0 and <c>XFD</c> 16,383. Case is ignored.</summary>
    /// <exception cref="FormatException">The text is not a column name on an Excel sheet.</exception>
    public static int ColumnIndex(ReadOnlySpan<char> name)
    {
        int column = 0;
        if (name.IsEmpty || name.Length > 3)
        {
            throw new FormatException($"'{name}' is not a column name such as B or XFD.");
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetter(c))
            {
                throw new FormatException($"'{name}' is not a column name such as B or XFD.");
            }

            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return column <= MaxColumns ? column - 1 : throw new FormatException($"'{name}' is past the last column, XFD.");
    }

    /// <summary>The address in A1 style, such as <c>B4</c>.</summary>
    public override string ToString()
    {
        Span<char> text = stackalloc char[10];
        TryFormat(text, out int written, default, null);
        return new string(text[..written]);
    }

    /// <summary>The address in A1 style; <paramref name="format"/> and <paramref name="formatProvider"/> are not used.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Writes the address in A1 style, such as <c>B4</c>, without allocating.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        Span<char> name = stackalloc char[3];
        int letters = WriteColumnName(Column, name);
        if (destination.Length < letters || !name[..letters].TryCopyTo(destination)
            || !(Row + 1).TryFormat(destination[letters..], out int digits, default, CultureInfo.InvariantCulture))
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = letters + digits;
        return true;
    }

    internal static bool TryParseRowNumber(ReadOnlySpan<char> text, out int row)
    {
        row = 0;
        if (text.IsEmpty || text.Length > 7 || text[0] == '0')
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            row = row * 10 + (c - '0');
        }

        return row <= MaxRows;
    }

    internal static int WriteColumnName(int column, Span<char> name)
    {
        int n = column + 1;
        int length = n > 702 ? 3 : n > 26 ? 2 : 1;
        for (int i = length - 1; i >= 0; i--)
        {
            n--;
            name[i] = (char)('A' + n % 26);
            n /= 26;
        }

        return length;
    }

    internal static void CheckRow(int row)
    {
        if ((uint)row >= MaxRows)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, "A row is from 0 to 1,048,575.");
        }
    }

    internal static void CheckColumn(int column)
    {
        if ((uint)column >= MaxColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, "A column is from 0 (A) to 16,383 (XFD).");
        }
    }
}
