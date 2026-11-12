using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Xls;

namespace Egap.Spreadsheets.Xlsb;

/// <summary>
/// Turns a BIFF12 parsed formula ([MS-XLSB] 2.2.2 and 2.5.97, the Ptg tokens) into Excel's formula text, as an XLSX file holds
/// it, without the <c>=</c>. The tokens are BIFF8's with wider rows and columns, 16-bit string lengths, and structured
/// references to tables.
/// </summary>
internal static class XlsbFormula
{
    private const int MaxDepth = 1024;
    private const int LastRow = CellAddress.MaxRows - 1;
    private const int LastColumn = CellAddress.MaxColumns - 1;

    /// <summary>
    /// The text of a formula's tokens <paramref name="tokens"/>, with the extra data of its arrays and memory areas in
    /// <paramref name="extra"/>. A cell <paramref name="row"/>, <paramref name="column"/> is where relative references of a
    /// shared formula, a conditional format or a validation count from. Null for tokens it cannot read.
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

    /// <summary>The cell a formula of only a PtgExp names: its row from the token, its column from the extra data.</summary>
    public static (int Row, int Column)? Anchor(ReadOnlySpan<byte> tokens, ReadOnlySpan<byte> extra) =>
        tokens.Length == 5 && tokens[0] == 0x01 && extra.Length >= 4
            ? ((int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(tokens[1..]), int.MaxValue), (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(extra), int.MaxValue))
            : null;

    private static string? DecodeCore(XlsLinks links, ReadOnlySpan<byte> tokens, ReadOnlySpan<byte> extra, int row, int column, bool name)
    {
        Stack<string> stack = new();
        int at = 0;
        int extraAt = 0;

        // Spaces a PtgAttrSpace puts before the next token: before an operand, or before an operator's sign.
        string spaces = "";

        // Spaces before the next parenthesis' opening and closing marks.
        string open = "", close = "";
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
                        if (kind == 0x11 && right.IndexOf('!') is > 0 and var bang && right[0] is not ('\'' or '[') && SheetNames.IsPlain(right[..bang]))
                        {
                            // Excel quotes the sheet after a range's colon, so that Sheet!A1:Sheet!B2 does not read as a sheet range.
                            right = "'" + right[..bang] + "'" + right[bang..];
                        }

                        stack.Push(left + spaces + XlsFormula.Operator(kind) + right);
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
                    stack.Push(open + "(" + stack.Pop() + close + ")");
                    open = close = "";
                    break;
                case 0x16:
                    stack.Push(string.Empty);
                    break;
                case 0x17:
                    {
                        int count = BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]);
                        string text = Characters(tokens, at + 2, count);
                        at += 2 + (count * 2);
                        stack.Push("\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                        break;
                    }

                case 0x18:
                    {
                        int extended = tokens[at++];
                        if (extended != 0x19 || List(links, tokens.Slice(at, 12)) is not { } list)
                        {
                            return null; // PtgSxName and the macro sheets' tokens; a table that is gone.
                        }

                        at += 12;
                        stack.Push(list);
                        break;
                    }

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
                            stack.Push(spaces + "SUM(" + stack.Pop() + close + ")");
                            spaces = close = "";
                        }

                        // Spaces before the next token (0), before a parenthesis' opening (2) or closing (4) mark, or before
                        // the formula (6), as Excel keeps them in an XLSX file; line breaks stay out.
                        if ((options & 0x40) != 0 && spaces.Length + open.Length + close.Length < 256)
                        {
                            switch (data & 0xFF)
                            {
                                case 0 or 6:
                                    spaces += new string(' ', data >> 8);
                                    break;
                                case 2:
                                    open += new string(' ', data >> 8);
                                    break;
                                case 4:
                                    close += new string(' ', data >> 8);
                                    break;
                            }
                        }

                        continue;
                    }

                case 0x1C:
                    stack.Push(XlsFormula.Error(tokens[at++]));
                    break;
                case 0x1D:
                    stack.Push(tokens[at++] != 0 ? "TRUE" : "FALSE");
                    break;
                case 0x1E:
                    stack.Push(BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]).ToString(CultureInfo.InvariantCulture));
                    at += 2;
                    break;
                case 0x1F:
                    stack.Push(XlsFormula.Number(BinaryPrimitives.ReadDoubleLittleEndian(tokens[at..])));
                    at += 8;
                    break;
                case 0x20:
                    at += 14;
                    stack.Push(ArrayConstant(extra, ref extraAt));
                    break;
                case 0x21:
                    {
                        int number = BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]);
                        at += 2;
                        if (XlsFunctions.Biff12Function(number) is not { Arguments: >= 0 } function)
                        {
                            return null;
                        }

                        stack.Push(Closed(XlsFormula.Call(function.Name, stack, function.Arguments), ref close));
                        break;
                    }

                case 0x22:
                    {
                        int count = tokens[at] & 0x7F;
                        int number = BinaryPrimitives.ReadUInt16LittleEndian(tokens[(at + 1)..]) & 0x7FFF;
                        at += 3;
                        if (number == 255)
                        {
                            // A user-defined, add-in or newer function: the first argument is its name.
                            string call = Closed(XlsFormula.Call(string.Empty, stack, count - 1), ref close);
                            stack.Push(UserFunction(XlsFunctions.CallName(stack.Pop())) + call);
                        }
                        else if (XlsFunctions.Biff12Function(number) is { } function)
                        {
                            stack.Push(Closed(XlsFormula.Call(function.Name, stack, count), ref close));
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
                    at += 6;
                    break;
                case 0x25:
                case 0x2D:
                    stack.Push(Area(tokens[at..], kind == 0x2D, row, column));
                    at += 12;
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
                    at += 6;
                    stack.Push("#REF!");
                    break;
                case 0x2B:
                    at += 12;
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
                        at += 8;
                        break;
                    }

                case 0x3B:
                case 0x3D:
                    {
                        string? prefix = SheetPrefix(links, BinaryPrimitives.ReadUInt16LittleEndian(tokens[at..]));
                        stack.Push(prefix is null ? "#REF!" : prefix + (kind == 0x3B ? Area(tokens[(at + 2)..], false, row, column) : "#REF!"));
                        at += 14;
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

    /// <summary>A call with the spaces a PtgAttrSpace put before its closing parenthesis, as <c>NOW( )</c>.</summary>
    private static string Closed(string call, ref string close)
    {
        if (close.Length == 0 || !call.EndsWith(')'))
        {
            return call;
        }

        string text = call[..^1] + close + ")";
        close = "";
        return text;
    }

    /// <summary>
    /// A user-defined function's name; one that is also a built-in function's, as a macro's <c>Concat</c> or an add-in's
    /// <c>xirr</c>, takes the <c>_xludf.</c> prefix Excel gives it, which keeps the two apart.
    /// </summary>
    private static string UserFunction(string name)
    {
        int bang = name.LastIndexOf('!');
        string bare = name[(bang + 1)..];
        return bare.StartsWith("_x", StringComparison.OrdinalIgnoreCase) || !FormulaFunctions.IsBuiltIn(bare.ToUpperInvariant())
            ? name
            : name[..(bang + 1)] + "_xludf." + bare;
    }

    /// <summary>A cell reference ([MS-XLSB] PtgRef): a 32-bit row, then the column with its relative flags.</summary>
    private static string Cell(ReadOnlySpan<byte> data, bool shared, int baseRow, int baseColumn)
    {
        int rowField = BinaryPrimitives.ReadInt32LittleEndian(data);
        int columnField = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        (int r, bool rowRelative, int c, bool columnRelative) = Location(rowField, columnField, shared, baseRow, baseColumn);
        return Address(c, columnRelative) + Row(r, rowRelative);
    }

    /// <summary>
    /// A name PtgNameX calls, as Excel writes it in an XLSX file: a sheet's name with the sheet in front, one of this
    /// workbook's as <c>[0]!Name</c>, an add-in's function as <c>_xll.Name</c>, a linked file's as <c>[1]!Name</c>.
    /// </summary>
    private static string ExternalName(XlsLinks links, int sheet, int index)
    {
        if ((uint)sheet >= (uint)links.Sheets.Count || (uint)links.Sheets[sheet].Book >= (uint)links.Books.Count)
        {
            return "#REF!";
        }

        (int bookIndex, int first, _) = links.Sheets[sheet];
        XlsSupBook book = links.Books[bookIndex];
        List<string> names = book.Kind == XlsSupBookKind.Self ? links.Names : book.Names;
        if ((uint)index >= (uint)names.Count)
        {
            return book.Kind == XlsSupBookKind.Self ? "#NAME?" : "#REF!";
        }

        string name = names[index];
        if (first >= 0 && book.Kind is XlsSupBookKind.Self or XlsSupBookKind.External && SheetPrefix(links, sheet) is { } prefix)
        {
            return prefix + name;
        }

        // A name of one sheet, which the formula reaches from elsewhere: Excel writes that sheet in front.
        List<int> scopes = book.Kind == XlsSupBookKind.Self ? links.NameSheets : book.NameSheets;
        int scope = index < scopes.Count ? scopes[index] : -1;
        if (book.Kind == XlsSupBookKind.Self && (uint)scope < (uint)links.SheetNames.Count)
        {
            string owner = links.SheetNames[scope];
            return (SheetNames.IsPlain(owner) ? owner : "'" + owner.Replace("'", "''", StringComparison.Ordinal) + "'") + "!" + name;
        }

        if (book.Kind == XlsSupBookKind.External && (uint)scope < (uint)book.Sheets.Count)
        {
            string text = "[" + book.Number.ToString(CultureInfo.InvariantCulture) + "]" + book.Sheets[scope];
            return (IsWord(book.Sheets[scope]) ? text : "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'") + "!" + name;
        }

        return book.Kind switch
        {
            XlsSupBookKind.Self => "[0]!" + name,
            XlsSupBookKind.AddIn => "_xll." + name,
            // A linked file's name, or a DDE or OLE link's item, quoted unless it is a plain word.
            _ when book.Number > 0 => "[" + book.Number.ToString(CultureInfo.InvariantCulture) + "]!" + (IsWord(name) ? name : "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'"),
            _ => name,
        };
    }

    /// <summary>
    /// A 3-D reference's sheet prefix: a linked file's sheet goes unquoted when its name has only letters, digits, dots and
    /// underscores, as Excel writes <c>[1]SO2!A1</c>.
    /// </summary>
    private static string? SheetPrefix(XlsLinks links, int index)
    {
        string? prefix = XlsFormula.SheetPrefix(links, index);
        if (prefix is not ['\'', '[', ..] || links.Books[links.Sheets[index].Book].Kind != XlsSupBookKind.External)
        {
            return prefix;
        }

        string inner = prefix[1..^2];
        int close = inner.IndexOf(']');
        string sheets = inner[(close + 1)..];
        return sheets.Split(':').All(IsWord) ? inner + "!" : prefix;
    }

    /// <summary>Whether a linked file's sheet name goes unquoted: a letter or underscore, then letters, digits, dots and underscores.</summary>
    private static bool IsWord(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');

    /// <summary>An area reference ([MS-XLSB] PtgArea); whole columns and rows as <c>A:A</c> and <c>1:1</c>.</summary>
    private static string Area(ReadOnlySpan<byte> data, bool shared, int baseRow, int baseColumn)
    {
        int firstRowField = BinaryPrimitives.ReadInt32LittleEndian(data);
        int lastRowField = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int firstColumnField = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        int lastColumnField = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        (int r1, bool rr1, int c1, bool cr1) = Location(firstRowField, firstColumnField, shared, baseRow, baseColumn);
        (int r2, bool rr2, int c2, bool cr2) = Location(lastRowField, lastColumnField, shared, baseRow, baseColumn);
        // Whole rows first, as Excel writes the whole sheet as 1:1048576.
        if (c1 == 0 && c2 == LastColumn && !cr1 && !cr2 || !shared && (firstColumnField & 0x3FFF) == 0 && (lastColumnField & 0x3FFF) == LastColumn)
        {
            return Row(r1, rr1) + ":" + Row(r2, rr2);
        }

        if (r1 == 0 && r2 == LastRow && !rr1 && !rr2 || !shared && firstRowField == 0 && lastRowField == LastRow)
        {
            return Address(c1, cr1) + ":" + Address(c2, cr2);
        }

        return Address(c1, cr1) + Row(r1, rr1) + ":" + Address(c2, cr2) + Row(r2, rr2);
    }

    /// <summary>
    /// A row and column with their relative flags (bit 14 the column's, bit 15 the row's). In a shared formula a relative row
    /// or column is an offset from the cell, the column's in 14 bits, wrapping round the sheet's rows and columns.
    /// </summary>
    private static (int Row, bool RowRelative, int Column, bool ColumnRelative) Location(int rowField, int columnField, bool shared, int baseRow, int baseColumn)
    {
        bool rowRelative = (columnField & 0x8000) != 0;
        bool columnRelative = (columnField & 0x4000) != 0;
        int row = rowField;
        int column = columnField & 0x3FFF;
        if (shared)
        {
            if (rowRelative)
            {
                row = (int)(((long)baseRow + rowField) & LastRow);
            }

            if (columnRelative)
            {
                int offset = (column ^ 0x2000) - 0x2000;
                column = (baseColumn + offset) & LastColumn;
            }
        }

        if ((uint)row > LastRow)
        {
            throw new InvalidDataException("A reference's row is past the sheet's last row.");
        }

        return (row, rowRelative, column, columnRelative);
    }

    private static string Address(int column, bool relative) => (relative ? string.Empty : "$") + CellAddress.ColumnName(column);

    private static string Row(int row, bool relative) => (relative ? string.Empty : "$") + (row + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A structured reference to a table ([MS-XLSB] PtgList), as Excel writes it in a file:
    /// <c>Sales[Amount]</c>, <c>Sales[[#All],[Amount]]</c>, <c>Sales[[#This Row],[Amount]]</c>, <c>Sales[[A]:[B]]</c>.
    /// </summary>
    private static string? List(XlsLinks links, ReadOnlySpan<byte> data)
    {
        int options = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        int first = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        int last = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        int columns = options & 0x03;
        int rows = (options >> 2) & 0x1F;
        if ((options & 0x3000) != 0 || !links.Tables.TryGetValue(id, out var table))
        {
            return null;
        }

        List<string> parts = [];
        foreach ((int bit, string text) in new[] { (0x01, "#All"), (0x02, "#Headers"), (0x04, "#Data"), (0x08, "#Totals"), (0x10, "#This Row") })
        {
            if ((rows & bit) != 0)
            {
                parts.Add("[" + text + "]");
            }
        }

        if (columns is 1 or 2)
        {
            if ((uint)first >= (uint)table.Columns.Length || (uint)last >= (uint)table.Columns.Length)
            {
                return null;
            }

            parts.Add(columns == 1 || first == last
                ? "[" + Escape(table.Columns[first]) + "]"
                : "[" + Escape(table.Columns[first]) + "]:[" + Escape(table.Columns[last]) + "]");
        }

        return parts.Count switch
        {
            0 => table.Name + "[]",
            1 => table.Name + parts[0],
            _ => table.Name + "[" + string.Join(',', parts) + "]",
        };
    }

    /// <summary>A column name in a structured reference: the characters Excel gives meaning there take an apostrophe.</summary>
    private static string Escape(string column)
    {
        if (column.AsSpan().IndexOfAny("[]#'") < 0)
        {
            return column;
        }

        StringBuilder text = new(column.Length + 4);
        foreach (char c in column)
        {
            if (c is '[' or ']' or '#' or '\'')
            {
                text.Append('\'');
            }

            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>An array constant's values from the extra data ([MS-XLSB] PtgExtraArray): {1,2;3,4}.</summary>
    private static string ArrayConstant(ReadOnlySpan<byte> extra, ref int at)
    {
        int rows = (int)BinaryPrimitives.ReadUInt32LittleEndian(extra[at..]);
        int columns = (int)BinaryPrimitives.ReadUInt32LittleEndian(extra[(at + 4)..]);
        at += 8;
        if (rows <= 0 || columns <= 0 || (long)rows * columns > extra.Length)
        {
            throw new InvalidDataException("An array constant is larger than its data.");
        }

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
                    case 0x00:
                        text.Append(XlsFormula.Number(BinaryPrimitives.ReadDoubleLittleEndian(extra[at..])));
                        at += 8;
                        break;
                    case 0x01:
                        {
                            int count = BinaryPrimitives.ReadUInt16LittleEndian(extra[at..]);
                            text.Append('"').Append(Characters(extra, at + 2, count).Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                            at += 2 + (count * 2);
                            break;
                        }

                    case 0x02:
                        text.Append(extra[at] != 0 ? "TRUE" : "FALSE");
                        at += 1;
                        break;
                    case 0x04:
                        text.Append(XlsFormula.Error(extra[at]));
                        at += 4;
                        break;
                    default:
                        throw new InvalidDataException("An array constant holds a value of a kind it cannot.");
                }
            }
        }

        return text.Append('}').ToString();
    }

    private static void SkipMemoryArea(ReadOnlySpan<byte> extra, ref int at)
    {
        if (at + 4 <= extra.Length)
        {
            at += 4 + ((int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(extra[at..]), (uint)extra.Length) * 16);
        }
    }

    private static string Characters(ReadOnlySpan<byte> data, int at, int count) => Encoding.Unicode.GetString(data.Slice(at, count * 2));
}
