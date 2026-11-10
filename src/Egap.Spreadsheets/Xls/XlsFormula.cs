using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Xls;

/// <summary>A SUPBOOK record ([MS-XLS] 2.4.271): the workbook itself, another file, or the add-in functions.</summary>
internal sealed class XlsSupBook
{
    public XlsSupBookKind Kind { get; init; }

    /// <summary>The linked file, for another file.</summary>
    public string? File { get; init; }

    /// <summary>The linked file's sheet names.</summary>
    public List<string> Sheets { get; } = [];

    /// <summary>The EXTERNNAME records that follow it, in order.</summary>
    public List<string> Names { get; } = [];

    /// <summary>The number Excel's formula text gives the linked file, <c>[n]</c>; 0 when it is not one.</summary>
    public int Number { get; set; }
}

internal enum XlsSupBookKind
{
    Self,
    External,
    AddIn,
    Other,
}

/// <summary>What formulas refer to beyond the sheet: the sheets, the linked books and their sheets, and the names.</summary>
internal sealed class XlsLinks
{
    /// <summary>The names of the BOUNDSHEET records, in order; a sheet index of a formula counts in these.</summary>
    public List<string> SheetNames { get; } = [];

    public List<XlsSupBook> Books { get; } = [];

    /// <summary>The EXTERNSHEET entries: a book and its first and last sheet.</summary>
    public List<(int Book, int First, int Last)> Sheets { get; } = [];

    /// <summary>The NAME records' names, in order.</summary>
    public List<string> Names { get; } = [];
}

/// <summary>
/// Turns a BIFF8 parsed formula ([MS-XLS] 2.2.2 and 2.5.198, the Ptg tokens) into Excel's formula text, as an XLSX file holds
/// it, without the <c>=</c>.
/// </summary>
internal static class XlsFormula
{
    private const int MaxDepth = 1024;

    /// <summary>
    /// The text of a formula's tokens <paramref name="tokens"/>, with the extra data of its arrays and memory areas in
    /// <paramref name="extra"/>. A cell <paramref name="row"/>, <paramref name="column"/> is where relative references of a
    /// shared formula count from. Null for tokens it cannot read: those the file has broken, and table and structured references.
    /// </summary>
    public static string? Decode(XlsLinks links, ReadOnlySpan<byte> tokens, ReadOnlySpan<byte> extra, int row, int column, bool name = false)
    {
        try
        {
            return DecodeCore(links, tokens, extra, row, column, name);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }

    private static string? DecodeCore(XlsLinks links, ReadOnlySpan<byte> tokens, ReadOnlySpan<byte> extra, int row, int column, bool name)
    {
        Stack<string> stack = new();
        int at = 0;
        int extraAt = 0;

        // Spaces a PtgAttrSpace puts before the next token: before an operand, or before an operator's sign.
        string spaces = "";
        while (at < tokens.Length)
        {
            if (stack.Count > MaxDepth)
            {
                return null;
            }

            int ptg = tokens[at++];
            int kind = ptg < 0x20 ? ptg : (ptg & 0x1F) | 0x20;
            switch (kind)
            {
                case 0x01: // PtgExp: the shared or array formula it belongs to; read by the caller.
                case 0x02: // PtgTbl: a data table.
                    return null;
                case >= 0x03 and <= 0x11:
                    {
                        string right = stack.Pop(), left = stack.Pop();
                        stack.Push(left + spaces + Operator(kind) + right);
                        spaces = "";
                        break;
                    }

                case 0x12:
                    stack.Push(spaces + "+" + stack.Pop());
                    spaces = "";
                    break;
                case 0x13:
                    stack.Push(spaces + "-" + stack.Pop());
                    spaces = "";
                    break;
                case 0x14:
                    stack.Push(stack.Pop() + "%");
                    break;
                case 0x15:
                    stack.Push("(" + stack.Pop() + ")");
                    break;
                case 0x16:
                    stack.Push(string.Empty);
                    break;
                case 0x17:
                    {
                        string text = ShortString(tokens, ref at);
                        stack.Push("\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                        break;
                    }

                case 0x18:
                    return null; // PtgElf and PtgList: macro sheet areas and structured references.
                case 0x19:
                    {
                        byte options = tokens[at];
                        int data = BinaryPrimitives.ReadUInt16LittleEndian(tokens[(at + 1)..]);
                        at += 3;
                        if ((options & 0x04) != 0)
                        {
                            at += (data + 1) * 2; // PtgAttrChoose's jump table.
                        }

                        if ((options & 0x10) != 0)
                        {
                            stack.Push("SUM(" + stack.Pop() + ")");
                        }

                        // Spaces before the next token (0) or before the formula (6); line breaks and the spaces about
                        // parentheses stay out.
                        if ((options & 0x40) != 0 && (data & 0xFF) is 0 or 6 && spaces.Length < 256)
                        {
                            spaces += new string(' ', data >> 8);
                        }

                        continue;
                    }

                case 0x1C:
                    stack.Push(Error(tokens[at++]));
                    break;
                case 0x1D:
                    stack.Push(tokens[at++] != 0 ? "TRUE" : "FALSE");
                    break;
                case 0x1E:
                    stack.Push(BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]).ToString(CultureInfo.InvariantCulture));
                    at += 2;
                    break;
                case 0x1F:
                    stack.Push(Number(BinaryPrimitives.ReadDoubleLittleEndian(tokens[at..])));
                    at += 8;
                    break;
                case 0x20:
                    at += 7;
                    stack.Push(ArrayConstant(extra, ref extraAt));
                    break;
                case 0x21:
                    {
                        int number = BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]);
                        at += 2;
                        if (XlsFunctions.Function(number) is not { Arguments: >= 0 } function)
                        {
                            return null;
                        }

                        stack.Push(Call(function.Name, stack, function.Arguments));
                        break;
                    }

                case 0x22:
                    {
                        int count = tokens[at] & 0x7F;
                        int number = BinaryPrimitives.ReadUInt16LittleEndian(tokens[(at + 1)..]) & 0x7FFF;
                        at += 3;
                        if (number == 255)
                        {
                            // A user-defined or add-in function: the first argument is its name.
                            string call = Call(string.Empty, stack, count - 1);
                            stack.Push(XlsFunctions.CallName(stack.Pop()) + call);
                        }
                        else if (XlsFunctions.Function(number) is { } function)
                        {
                            stack.Push(Call(function.Name, stack, count));
                        }
                        else
                        {
                            return null;
                        }

                        break;
                    }

                case 0x23:
                    {
                        int index = (int)BinaryPrimitives.ReadUInt32LittleEndian(tokens[at..]) - 1;
                        at += 4;
                        stack.Push((uint)index < (uint)links.Names.Count ? links.Names[index] : "#NAME?");
                        break;
                    }

                case 0x2C or 0x2D when name:
                    // A name holds no relative-offset references; Excel reads one that does as #N/A.
                    return "#N/A";
                case 0x24:
                case 0x2C:
                    stack.Push(Cell(tokens[at..], kind == 0x2C, row, column));
                    at += 4;
                    break;
                case 0x25:
                case 0x2D:
                    stack.Push(Area(tokens[at..], kind == 0x2D, row, column));
                    at += 8;
                    break;
                case 0x26:
                    at += 6;
                    SkipMemoryArea(extra, ref extraAt);
                    break;
                case 0x27:
                case 0x28:
                    at += 6;
                    break;
                case 0x29:
                    at += 2;
                    break;
                case 0x2A:
                    at += 4;
                    stack.Push("#REF!");
                    break;
                case 0x2B:
                    at += 8;
                    stack.Push("#REF!");
                    break;
                case 0x39:
                    {
                        int sheet = BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]);
                        int index = (int)BinaryPrimitives.ReadUInt32LittleEndian(tokens[(at + 2)..]) - 1;
                        at += 6;
                        stack.Push(ExternalName(links, sheet, index));
                        break;
                    }

                case 0x3A:
                case 0x3C:
                    {
                        string? prefix = SheetPrefix(links, BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]));
                        stack.Push(prefix is null ? "#REF!" : prefix + (kind == 0x3A ? Cell(tokens[(at + 2)..], false, row, column) : "#REF!"));
                        at += 6;
                        break;
                    }

                case 0x3B:
                case 0x3D:
                    {
                        string? prefix = SheetPrefix(links, BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]));
                        stack.Push(prefix is null ? "#REF!" : prefix + (kind == 0x3B ? Area(tokens[(at + 2)..], false, row, column) : "#REF!"));
                        at += 10;
                        break;
                    }

                default:
                    return null;
            }

            if (spaces.Length > 0 && stack.Count > 0)
            {
                stack.Push(spaces + stack.Pop());
                spaces = "";
            }
        }

        return stack.Count == 1 ? stack.Pop() : null;
    }

    private static string Operator(int kind) => kind switch
    {
        0x03 => "+",
        0x04 => "-",
        0x05 => "*",
        0x06 => "/",
        0x07 => "^",
        0x08 => "&",
        0x09 => "<",
        0x0A => "<=",
        0x0B => "=",
        0x0C => ">=",
        0x0D => ">",
        0x0E => "<>",
        0x0F => " ",
        0x10 => ",",
        _ => ":",
    };

    private static string Call(string name, Stack<string> stack, int count)
    {
        if (count < 0 || count > stack.Count)
        {
            throw new InvalidOperationException("A function takes more arguments than the formula has.");
        }

        string[] arguments = new string[count];
        for (int i = count - 1; i >= 0; i--)
        {
            arguments[i] = stack.Pop();
        }

        return name + "(" + string.Join(',', arguments) + ")";
    }

    /// <summary>A cell reference ([MS-XLS] 2.5.198.84 and 2.5.198.88): row, then column with its relative flags.</summary>
    private static string Cell(ReadOnlySpan<byte> data, bool shared, int baseRow, int baseColumn)
    {
        int rowField = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int columnField = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        (int r, bool rowRelative, int c, bool columnRelative) = Location(rowField, columnField, shared, baseRow, baseColumn);
        return Address(c, columnRelative) + Row(r, rowRelative);
    }

    /// <summary>An area reference ([MS-XLS] 2.5.198.27 and 2.5.198.30); whole columns and rows as <c>A:A</c> and <c>1:1</c>.</summary>
    private static string Area(ReadOnlySpan<byte> data, bool shared, int baseRow, int baseColumn)
    {
        int firstRowField = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int lastRowField = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int firstColumnField = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int lastColumnField = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        (int r1, bool rr1, int c1, bool cr1) = Location(firstRowField, firstColumnField, shared, baseRow, baseColumn);
        (int r2, bool rr2, int c2, bool cr2) = Location(lastRowField, lastColumnField, shared, baseRow, baseColumn);
        if (r1 == 0 && r2 == 0xFFFF && !rr1 && !rr2 || !shared && firstRowField == 0 && lastRowField == 0xFFFF)
        {
            return Address(c1, cr1) + ":" + Address(c2, cr2);
        }

        if (c1 == 0 && c2 == 0xFF)
        {
            return Row(r1, rr1) + ":" + Row(r2, rr2);
        }

        return Address(c1, cr1) + Row(r1, rr1) + ":" + Address(c2, cr2) + Row(r2, rr2);
    }

    /// <summary>
    /// A row and column with their relative flags. In a shared formula a relative row or column is an offset from the cell,
    /// wrapping round the sheet's 65,536 rows and 256 columns.
    /// </summary>
    private static (int Row, bool RowRelative, int Column, bool ColumnRelative) Location(int rowField, int columnField, bool shared, int baseRow, int baseColumn)
    {
        bool rowRelative = (columnField & 0x8000) != 0;
        bool columnRelative = (columnField & 0x4000) != 0;
        int row = rowField;
        int column = columnField & 0xFF;
        if (shared)
        {
            if (rowRelative)
            {
                row = (baseRow + (short)rowField) & 0xFFFF;
            }

            if (columnRelative)
            {
                column = (baseColumn + (sbyte)(columnField & 0xFF)) & 0xFF;
            }
        }

        return (row, rowRelative, column, columnRelative);
    }

    private static string Address(int column, bool relative) => (relative ? string.Empty : "$") + CellAddress.ColumnName(column);

    private static string Row(int row, bool relative) => (relative ? string.Empty : "$") + (row + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The sheet part of a 3-D reference, with its <c>!</c>: a sheet, a range of sheets, or a sheet of a linked file; null for a sheet that is gone, which Excel writes as <c>#REF!</c> alone.</summary>
    private static string? SheetPrefix(XlsLinks links, int index)
    {
        if ((uint)index >= (uint)links.Sheets.Count)
        {
            return null;
        }

        (int bookIndex, int first, int last) = links.Sheets[index];
        XlsSupBook? book = (uint)bookIndex < (uint)links.Books.Count ? links.Books[bookIndex] : null;
        if (book is null || first < 0 || last < 0)
        {
            return null;
        }

        List<string> names = book.Kind == XlsSupBookKind.Self ? links.SheetNames : book.Sheets;
        if (first >= names.Count)
        {
            return null;
        }

        string sheets = names[first] + (last != first && last < names.Count ? ":" + names[last] : string.Empty);
        string text = book.Kind == XlsSupBookKind.External ? "[" + book.Number.ToString(CultureInfo.InvariantCulture) + "]" + sheets : sheets;
        bool plain = SheetNames.IsPlain(names[first]) && (last == first || last >= names.Count || SheetNames.IsPlain(names[last]));
        return (plain ? text : "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'") + "!";
    }

    /// <summary>A name of a linked file or an add-in function ([MS-XLS] 2.5.198.41 PtgNameX).</summary>
    private static string ExternalName(XlsLinks links, int sheet, int index)
    {
        if ((uint)sheet >= (uint)links.Sheets.Count || (uint)links.Sheets[sheet].Book >= (uint)links.Books.Count)
        {
            return "#REF!";
        }

        XlsSupBook book = links.Books[links.Sheets[sheet].Book];
        if (book.Kind == XlsSupBookKind.Self)
        {
            return (uint)index < (uint)links.Names.Count ? links.Names[index] : "#NAME?";
        }

        if ((uint)index >= (uint)book.Names.Count)
        {
            return "#REF!";
        }

        return book.Kind == XlsSupBookKind.External
            ? "[" + book.Number.ToString(CultureInfo.InvariantCulture) + "]!" + book.Names[index]
            : book.Names[index];
    }

    /// <summary>An array constant's values from the extra data ([MS-XLS] 2.5.198.103 PtgExtraArray): {1,2;3,4}.</summary>
    private static string ArrayConstant(ReadOnlySpan<byte> extra, ref int at)
    {
        int columns = extra[at] + 1;
        int rows = BinaryPrimitives.ReadUInt16LittleEndian(extra[(at + 1)..]) + 1;
        at += 3;
        StringBuilder text = new("{");
        for (int r = 0; r < rows; r++)
        {
            if (r > 0)
            {
                text.Append(';');
            }

            for (int c = 0; c < columns; c++)
            {
                if (c > 0)
                {
                    text.Append(',');
                }

                int type = extra[at++];
                switch (type)
                {
                    case 0x01:
                        text.Append(Number(BinaryPrimitives.ReadDoubleLittleEndian(extra[at..])));
                        at += 8;
                        break;
                    case 0x02:
                        {
                            int count = BinaryPrimitives.ReadUInt16LittleEndian(extra[at..]);
                            at += 2;
                            text.Append('"').Append(Characters(extra, ref at, count).Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                            break;
                        }

                    case 0x04:
                        text.Append(extra[at] != 0 ? "TRUE" : "FALSE");
                        at += 8;
                        break;
                    case 0x10:
                        text.Append(Error(extra[at]));
                        at += 8;
                        break;
                    default:
                        at += 8;
                        break;
                }
            }
        }

        return text.Append('}').ToString();
    }

    private static void SkipMemoryArea(ReadOnlySpan<byte> extra, ref int at)
    {
        if (at + 2 <= extra.Length)
        {
            at += 2 + (BinaryPrimitives.ReadUInt16LittleEndian(extra[at..]) * 8);
        }
    }

    private static string ShortString(ReadOnlySpan<byte> data, ref int at)
    {
        int count = data[at++];
        return Characters(data, ref at, count);
    }

    private static string Characters(ReadOnlySpan<byte> data, ref int at, int count)
    {
        bool wide = (data[at++] & 0x01) != 0;
        StringBuilder text = new(count);
        for (int i = 0; i < count; i++)
        {
            text.Append(wide ? (char)BinaryPrimitives.ReadUInt16LittleEndian(data[(at + (i * 2))..]) : (char)data[at + i]);
        }

        at += wide ? count * 2 : count;
        return text.ToString();
    }

    /// <summary>
    /// A number as Excel's formula text writes it: 15 significant digits, in plain decimals (<c>0.000001</c>,
    /// <c>30.4166666666667</c>), and in E notation only when it is 1E+15 or more, or smaller than 1E-20.
    /// </summary>
    internal static string Number(double value)
    {
        string text = value.ToString("G15", CultureInfo.InvariantCulture);
        int e = text.IndexOf('E', StringComparison.Ordinal);
        if (e < 0)
        {
            return text;
        }

        int exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (exponent >= 15 || exponent < -20)
        {
            return text;
        }

        bool negative = text[0] == '-';
        string digits = text[(negative ? 1 : 0)..e].Replace(".", string.Empty, StringComparison.Ordinal);
        string plain = exponent < 0
            ? "0." + new string('0', -exponent - 1) + digits
            : digits.Length > exponent + 1 ? digits[..(exponent + 1)] + "." + digits[(exponent + 1)..] : digits + new string('0', exponent + 1 - digits.Length);
        return negative ? "-" + plain : plain;
    }

    /// <summary>An error by its BIFF code ([MS-XLS] 2.5.97.2 BErr).</summary>
    internal static string Error(int code) => code switch
    {
        0x00 => "#NULL!",
        0x07 => "#DIV/0!",
        0x0F => "#VALUE!",
        0x17 => "#REF!",
        0x1D => "#NAME?",
        0x24 => "#NUM!",
        0x2A => "#N/A",
        0x2B => "#GETTING_DATA",
        _ => "#N/A",
    };

    /// <summary>An error value by its BIFF code.</summary>
    internal static CellError ErrorValue(int code) => code switch
    {
        0x00 => CellError.Null,
        0x07 => CellError.DivisionByZero,
        0x0F => CellError.Value,
        0x17 => CellError.Reference,
        0x1D => CellError.Name,
        0x24 => CellError.Number,
        0x2B => CellError.GettingData,
        _ => CellError.NotAvailable,
    };
}
