using System.Collections.Frozen;
using System.Globalization;
using System.Text;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Ods;

/// <summary>
/// Translates Excel's formula text into OpenFormula (ODF 1.3 Part 4) as an ODS file stores it: references in brackets
/// (<c>A1:B2</c> as <c>[.A1:.B2]</c>, <c>'My sheet'!A1</c> as <c>[$'My sheet'.A1]</c>, <c>[1]Rates!A1</c> as
/// <c>['file:///c:/rates.xlsx'#$Rates.A1]</c>), semicolons between arguments, <c>|</c> between an array's rows, <c>~</c> for
/// the union and <c>!</c> for the intersection, <c>TRUE()</c> and <c>FALSE()</c> outside arrays, structured references as
/// the ranges they name, and function names as ODF names them.
/// </summary>
internal static class OpenFormulaWriter
{
    /// <summary>Excel functions whose ODF names differ from Excel's (ODF 1.3 Part 4, 6 and its LEGACY. functions).</summary>
    private static readonly FrozenDictionary<string, string> Renamed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CEILING"] = "COM.MICROSOFT.CEILING",
        ["FLOOR"] = "COM.MICROSOFT.FLOOR",
        ["CHIDIST"] = "LEGACY.CHIDIST",
        ["CHIINV"] = "LEGACY.CHIINV",
        ["CHITEST"] = "LEGACY.CHITEST",
        ["FDIST"] = "LEGACY.FDIST",
        ["FINV"] = "LEGACY.FINV",
        ["NORMSDIST"] = "LEGACY.NORMSDIST",
        ["NORMSINV"] = "LEGACY.NORMSINV",
        ["TDIST"] = "LEGACY.TDIST",
        ["FORMULATEXT"] = "FORMULA",
        ["F.DIST"] = "FDIST",
        ["F.INV"] = "FINV",
        ["SKEW.P"] = "SKEWP",
        ["ISO.CEILING"] = "COM.MICROSOFT.ISO.CEILING",
        ["NETWORKDAYS.INTL"] = "COM.MICROSOFT.NETWORKDAYS.INTL",
        ["WORKDAY.INTL"] = "COM.MICROSOFT.WORKDAY.INTL",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Excel functions that office suites have no ODF name for, which they keep with Excel's own <c>_xlfn.</c> prefix so that
    /// the formula still reads in Excel.
    /// </summary>
    private static readonly FrozenSet<string> ExcelOnly = new[]
    {
        "ARRAYTOTEXT", "BINOM.DIST.RANGE", "BYCOL", "BYROW", "ISOMITTED", "LAMBDA", "MAKEARRAY", "MAP", "REDUCE", "SCAN",
        "TRIMRANGE", "VALUETOTEXT",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Functions newer than Excel 2007 that OpenFormula has under the same name. The other newer functions are Excel's own,
    /// which ODF names with the <c>COM.MICROSOFT.</c> prefix.
    /// </summary>
    private static readonly FrozenSet<string> OpenFormulaNames = new[]
    {
        "ACOT", "ACOTH", "ARABIC", "BASE", "BITAND", "BITLSHIFT", "BITOR", "BITRSHIFT", "BITXOR", "COMBINA", "COT", "COTH",
        "CSC", "CSCH", "DAYS", "DECIMAL", "GAMMA", "GAUSS", "IFNA", "IMCOSH", "IMCOT", "IMCSC", "IMCSCH", "IMSEC", "IMSECH",
        "IMSINH", "IMTAN", "ISFORMULA", "ISOWEEKNUM", "MUNIT", "NUMBERVALUE", "PDURATION", "PERMUTATIONA", "PHI", "RRI",
        "SEC", "SECH", "SHEET", "SHEETS", "UNICHAR", "UNICODE", "XOR",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The Excel functions newer than Excel 2007, which files store with the <c>_xlfn.</c> prefix.</summary>
    private static bool IsNewer(string name) => FutureFunctions.StoredName(name) != name || name is "FILTER" or "SORT";

    /// <summary>
    /// The OpenFormula for an Excel formula of a cell, with the <c>of:=</c> namespace; null when the text does not parse, which
    /// the caller writes in Excel's own syntax.
    /// </summary>
    /// <param name="excel">The formula as the workbook holds it, without the leading <c>=</c>.</param>
    /// <param name="sheet">The formula's sheet, for its tables.</param>
    /// <param name="cell">The formula's cell.</param>
    /// <param name="files">The file of a linked book by its number, as <c>[1]</c> numbers it; null when the book has none.</param>
    public static string? Translate(string excel, Worksheet? sheet, CellAddress cell, Func<string, string?> files)
    {
        if (FormulaParser.TryParse(excel, r1c1: false, cell) is not { } formula)
        {
            return null;
        }

        StringBuilder output = new(excel.Length + 16);
        output.Append("of:=");
        Writer writer = new(output, sheet, cell, files);
        writer.Node(formula.Root);
        output.Append(formula.Trail);
        return writer.Failed ? null : output.ToString();
    }

    /// <summary>A reference to a cell or range, as a table:cell-range-address or table:target-range-address holds it: <c>Sheet1.A1:Sheet1.B2</c>.</summary>
    public static string Address(string sheet, RangeAddress range, bool absolute = true)
    {
        StringBuilder output = new();
        string name = SheetName(sheet);
        output.Append(absolute ? "$" : "").Append(name).Append('.');
        Cell(output, range.First.Row, range.First.Column, absolute, absolute);
        if (range.First != range.Last)
        {
            output.Append(':').Append(absolute ? "$" : "").Append(name).Append('.');
            Cell(output, range.Last.Row, range.Last.Column, absolute, absolute);
        }

        return output.ToString();
    }

    /// <summary>A sheet name as OpenFormula writes it: bare when it is letters, digits and underscores, else in quotes.</summary>
    public static string SheetName(string name)
    {
        bool plain = name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_');
        return plain ? name : "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>The name ODF gives an Excel function: <see cref="Renamed"/>, or the <c>COM.MICROSOFT.</c> prefix for Excel's newer ones.</summary>
    public static string FunctionName(string written)
    {
        string plain = FormulaCompiler.FunctionName(written);
        if (!FormulaFunctions.IsBuiltIn(plain))
        {
            // A function of an add-in or a macro, which other suites keep as it was written.
            return written.StartsWith("_xludf.", StringComparison.OrdinalIgnoreCase) ? written[7..] : written;
        }

        string upper = plain.ToUpperInvariant();
        if (Renamed.TryGetValue(upper, out string? renamed))
        {
            return renamed;
        }

        if (ExcelOnly.Contains(upper))
        {
            return "_xlfn." + upper;
        }

        return IsNewer(upper) && !OpenFormulaNames.Contains(upper) ? "COM.MICROSOFT." + upper : upper;
    }

    private static void Cell(StringBuilder output, int row, int column, bool rowAbsolute, bool columnAbsolute)
    {
        Column(output, column, columnAbsolute);
        Row(output, row, rowAbsolute);
    }

    private static void Column(StringBuilder output, int column, bool absolute) =>
        output.Append(absolute ? "$" : "").Append(CellAddress.ColumnName(column));

    private static void Row(StringBuilder output, int row, bool absolute) =>
        output.Append(absolute ? "$" : "").Append((row + 1).ToString(CultureInfo.InvariantCulture));

    private sealed class Writer(StringBuilder output, Worksheet? sheet, CellAddress cell, Func<string, string?> files)
    {
        /// <summary>A part had no OpenFormula form.</summary>
        public bool Failed { get; private set; }

        public void Node(FormulaNode node)
        {
            output.Append(node.Lead);
            switch (node)
            {
                case NumberNode number:
                    output.Append(number.Text);
                    break;
                case StringNode text:
                    output.Append(text.Text);
                    break;
                case BooleanNode boolean:
                    output.Append(boolean.Value ? "TRUE()" : "FALSE()");
                    break;
                case ErrorNode error:
                    output.Append(error.Text.ToUpperInvariant());
                    break;
                case ArrayNode array:
                    Array(array);
                    break;
                case MissingNode:
                    break;
                case ReferenceNode reference:
                    Reference(reference);
                    break;
                case NameNode name:
                    Name(name);
                    break;
                case StructuredNode structured:
                    Structured(structured);
                    break;
                case InvokeNode invoke:
                    Node(invoke.Callee);
                    output.Append('(');
                    List(invoke.Arguments, ';');
                    output.Append(')');
                    break;
                case FunctionNode function:
                    output.Append(FunctionName(function.Name)).Append('(');
                    List(function.Arguments, ';');
                    output.Append(')');
                    break;
                case ParenthesesNode parentheses:
                    // Excel's union, (A1,B2), is OpenFormula's ~ operator.
                    output.Append('(');
                    List(parentheses.Items, '~');
                    output.Append(')');
                    break;
                case UnaryNode { Operator: '@' } single:
                    // The implicit intersection Excel shows on a formula of an older version is what OpenFormula does anyway.
                    Node(single.Operand);
                    break;
                case UnaryNode unary:
                    output.Append(unary.Operator);
                    Node(unary.Operand);
                    break;
                case PostfixNode { Operator: '#' } spill:
                    output.Append("COM.MICROSOFT.ANCHORARRAY(");
                    Node(spill.Operand);
                    output.Append(')');
                    break;
                case PostfixNode postfix:
                    Node(postfix.Operand);
                    output.Append(postfix.OperatorLead).Append(postfix.Operator);
                    break;
                case BinaryNode { Operator: BinaryOperator.Intersection } intersection:
                    Node(intersection.Left);
                    output.Append('!');
                    Node(intersection.Right);
                    break;
                case BinaryNode binary:
                    Node(binary.Left);
                    output.Append(binary.OperatorLead).Append(binary.OperatorText);
                    Node(binary.Right);
                    break;
                default:
                    Failed = true;
                    break;
            }
        }

        private void List(Argument[] items, char separator)
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                {
                    output.Append(separator);
                }

                Node(items[i].Node);
                output.Append(items[i].Trail);
            }
        }

        /// <summary>An inline array, <c>{1,2;3,4}</c> in Excel, <c>{1;2|3;4}</c> in OpenFormula, outside its strings.</summary>
        private void Array(ArrayNode array)
        {
            string text = array.Text;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    int end = i + 1;
                    while (end < text.Length && !(text[end] == '"' && (end + 1 >= text.Length || text[end + 1] != '"')))
                    {
                        end += text[end] == '"' ? 2 : 1;
                    }

                    end = Math.Min(end, text.Length - 1);
                    output.Append(text, i, end - i + 1);
                    i = end;
                    continue;
                }

                if (char.IsAsciiLetter(c))
                {
                    int end = i;
                    while (end < text.Length && char.IsAsciiLetter(text[end]))
                    {
                        end++;
                    }

                    string word = text[i..end];
                    // Booleans in an array stay bare, as office suites write them: TRUE() is a function call, which an array may
                    // not hold.
                    output.Append(word.ToUpperInvariant() is "TRUE" or "FALSE" ? word.ToUpperInvariant() : word);
                    i = end - 1;
                    continue;
                }

                output.Append(c switch
                {
                    ',' => ';',
                    ';' => '|',
                    _ => c,
                });
            }
        }

        private void Reference(ReferenceNode reference)
        {
            Area area = reference.Area;
            if (area.Kind == AreaKind.Error)
            {
                output.Append("#REF!");
                return;
            }

            SheetPrefix? prefix = reference.Prefix;
            string? file = null;
            // Excel's book [0] is the workbook itself.
            if (prefix?.Book is { } book && book != "0")
            {
                file = files(book);
                if (file is null || prefix.FirstSheet is null)
                {
                    output.Append("#REF!");
                    return;
                }
            }

            output.Append('[');
            if (file is not null)
            {
                output.Append('\'').Append(file.Replace("'", "''", StringComparison.Ordinal)).Append("'#");
            }

            string? first = prefix?.FirstSheet;
            string? last = prefix?.LastSheet is { } other && other != first ? other : null;
            Part(first, area.Kind, area.FirstRow, area.FirstColumn, area.FirstRowAbsolute, area.FirstColumnAbsolute);
            if (area.Kind != AreaKind.Cell)
            {
                output.Append(':');
                Part(last, area.Kind, area.LastRow, area.LastColumn, area.LastRowAbsolute, area.LastColumnAbsolute);
            }
            else if (last is not null)
            {
                output.Append(':');
                Part(last, area.Kind, area.FirstRow, area.FirstColumn, area.FirstRowAbsolute, area.FirstColumnAbsolute);
            }

            output.Append(']');
        }

        /// <summary>One end of a reference: the sheet, absolute as Excel's sheets always are, a dot, and the cell, row or column.</summary>
        private void Part(string? sheetName, AreaKind kind, int row, int column, bool rowAbsolute, bool columnAbsolute)
        {
            if (sheetName is not null)
            {
                output.Append('$').Append(SheetName(sheetName));
            }

            output.Append('.');
            switch (kind)
            {
                case AreaKind.Rows:
                    Row(output, row, rowAbsolute);
                    break;
                case AreaKind.Columns:
                    Column(output, column, columnAbsolute);
                    break;
                default:
                    OpenFormulaWriter.Cell(output, row, column, rowAbsolute, columnAbsolute);
                    break;
            }
        }

        private void Name(NameNode name)
        {
            SheetPrefix? prefix = name.Prefix;
            if (prefix is null)
            {
                output.Append(name.Name);
                return;
            }

            if (prefix.Book == "0" && prefix.FirstSheet is null)
            {
                // [0]!Rates names the workbook's own Rates.
                output.Append(name.Name);
                return;
            }

            if (prefix.Book is { } book && book != "0")
            {
                // A name of a linked book, [1]!Rates: ODF's 'file'#$$'Rates' when the book's file is known.
                if (prefix.FirstSheet is null && files(book) is { } file)
                {
                    output.Append("['").Append(file.Replace("'", "''", StringComparison.Ordinal)).Append("'#$$'")
                        .Append(name.Name.Replace("'", "''", StringComparison.Ordinal)).Append("']");
                }
                else
                {
                    output.Append(prefix.Text).Append(name.Name);
                }

                return;
            }

            // A name local to a sheet: Sheet!Rate in Excel, Sheet.Rate in OpenFormula.
            output.Append(prefix.FirstSheet is { } local ? SheetName(local) + "." : prefix.Text).Append(name.Name);
        }

        /// <summary>A structured reference as the range of the table it names (ECMA-376 Part 1, 18.17.6.1); #REF! when there is none.</summary>
        private void Structured(StructuredNode node)
        {
            SheetTable? table = node.Table is null ? sheet?.Tables.Find(cell) : sheet is null ? null : FormulaEvaluator.FindTable(sheet.Workbook, node.Table);
            if (table is null || TableRange(table, node) is not { } range)
            {
                output.Append("#REF!");
                return;
            }

            output.Append('[').Append(Address(table.Worksheet.Name, range)).Append(']');
        }

        private RangeAddress? TableRange(SheetTable table, StructuredNode node)
        {
            RangeAddress range = table.Range;
            int dataFirst = range.First.Row + (table.ShowHeaderRow ? 1 : 0);
            int dataLast = range.Last.Row - (table.ShowTotalsRow ? 1 : 0);
            TableItems items = node.Items;
            if ((items.HasFlag(TableItems.Headers) && !table.ShowHeaderRow) || (items.HasFlag(TableItems.Totals) && !table.ShowTotalsRow))
            {
                return null;
            }

            (int first, int last) = items switch
            {
                TableItems.None or TableItems.Data => (dataFirst, dataLast),
                TableItems.ThisRow => (cell.Row, cell.Row),
                TableItems.Headers => (range.First.Row, range.First.Row),
                TableItems.Totals => (range.Last.Row, range.Last.Row),
                TableItems.Headers | TableItems.Data => (range.First.Row, dataLast),
                TableItems.Data | TableItems.Totals => (dataFirst, range.Last.Row),
                _ when items.HasFlag(TableItems.All) => (range.First.Row, range.Last.Row),
                _ => (-1, -1),
            };
            if (first < 0 || last < first)
            {
                return null;
            }

            int firstColumn = range.First.Column;
            int lastColumn = range.Last.Column;
            if (node.FirstColumn is { } from)
            {
                int start = table.Columns.FindIndex(c => string.Equals(c.Name, from, StringComparison.OrdinalIgnoreCase));
                int end = node.LastColumn is { } to ? table.Columns.FindIndex(c => string.Equals(c.Name, to, StringComparison.OrdinalIgnoreCase)) : start;
                if (start < 0 || end < 0)
                {
                    return null;
                }

                (firstColumn, lastColumn) = (range.First.Column + Math.Min(start, end), range.First.Column + Math.Max(start, end));
            }

            return new RangeAddress(first, firstColumn, last, lastColumn);
        }
    }
}
