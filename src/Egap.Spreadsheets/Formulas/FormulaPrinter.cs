using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Formulas;

/// <summary>How <see cref="FormulaPrinter"/> writes references.</summary>
internal enum ReferenceStyle
{
    /// <summary>Every token as it was written, so the text is the one parsed.</summary>
    Written,

    /// <summary>A1 references made again for a target cell: relative ones move by the target's distance from the origin.</summary>
    A1,

    /// <summary>R1C1 references: relative ones as offsets from the origin.</summary>
    R1C1,
}

/// <summary>Writes a <see cref="ParsedFormula"/> back to text, as written or with its references in A1 or R1C1 style.</summary>
internal static class FormulaPrinter
{
    /// <summary>The formula's text.</summary>
    /// <param name="formula">The parsed formula.</param>
    /// <param name="style">How references are written; everything else is written as it was read.</param>
    /// <param name="target">
    /// For <see cref="ReferenceStyle.A1"/>, the cell to write the formula for: relative references move by its distance
    /// from the formula's origin, and one that moves off the sheet becomes <c>#REF!</c>. Ignored otherwise.
    /// </param>
    public static string Print(ParsedFormula formula, ReferenceStyle style, CellAddress target = default)
    {
        StringBuilder output = new();
        Writer writer = new(output, style, formula.Origin, target.Row - formula.Origin.Row, target.Column - formula.Origin.Column, Stored.No);
        writer.Node(formula.Root);
        output.Append(formula.Trail);
        return output.ToString();
    }

    /// <summary>
    /// The formula as written, but for the references <paramref name="moved"/> gives a new area for, which are written
    /// again in A1 style (<c>#REF!</c> for an area of <see cref="AreaKind.Error"/>) after their prefix as written, and the
    /// table names and structured references <paramref name="gone"/> says are gone, which are written as <c>#REF!</c>.
    /// </summary>
    public static string Print(ParsedFormula formula, Func<ReferenceNode, Area?> moved, Func<FormulaNode, bool>? gone = null)
    {
        StringBuilder output = new();
        new Writer(output, ReferenceStyle.Written, formula.Origin, 0, 0, Stored.No, null, moved, gone).Node(formula.Root);
        output.Append(formula.Trail);
        return output.ToString();
    }

    /// <summary>
    /// A formula as a person types it, as a file stores it: newer functions with their prefixes, <c>#</c> and, in a dynamic
    /// array formula, <c>@</c> as the functions that stand for them. An ordinary formula has no <c>@</c>: it is what it means.
    /// The names of LET and LAMBDA get their prefixes, and a function's name used as a value too, unless <paramref name="book"/>
    /// has it as a name. A structured reference has its table named, and its parts in full. A formula that does not parse is
    /// stored as it is.
    /// </summary>
    /// <param name="text">The formula as typed.</param>
    /// <param name="cell">Its cell.</param>
    /// <param name="dynamic">Whether it is a dynamic array formula.</param>
    /// <param name="book">The workbook, for its names and tables.</param>
    /// <param name="sheet">The formula's sheet, for the table a reference without one is in.</param>
    public static string StoredForm(string text, CellAddress cell, bool dynamic, Workbook? book = null, Worksheet? sheet = null)
    {
        bool mayNameTable = book is not null && book.Worksheets.Any(s => s.Tables.Count > 0);
        if ((!mayNameTable && !FutureFunctions.MayNeedStoring(text)) || FormulaParser.TryParse(text, r1c1: false, cell) is not { } formula)
        {
            return text;
        }

        StringBuilder output = new(text.Length + 16);
        new Writer(output, ReferenceStyle.Written, formula.Origin, 0, 0, dynamic ? Stored.Dynamic : Stored.Ordinary, new Locals(book, sheet, cell)).Node(formula.Root);
        output.Append(formula.Trail);
        return output.ToString();
    }

    /// <summary>A structured reference's items, in the order a file has them.</summary>
    private static readonly (TableItems Item, string Text)[] Items =
    [
        (TableItems.All, "#All"), (TableItems.Headers, "#Headers"), (TableItems.Data, "#Data"), (TableItems.Totals, "#Totals"), (TableItems.ThisRow, "#This Row"),
    ];

    private enum Stored : byte
    {
        No,
        Ordinary,
        Dynamic,
    }

    /// <summary>The names of the LETs and LAMBDAs around where a stored formula has got to.</summary>
    private sealed class Locals(Workbook? book, Worksheet? sheet, CellAddress cell)
    {
        private readonly List<string> known = [];

        public int Count => known.Count;

        public void Add(string name) => known.Add(name);

        public void Back(int count) => known.RemoveRange(count, known.Count - count);

        public bool Has(string name) => known.Contains(name);

        /// <summary>Whether a word that is no local stands for the function of that name.</summary>
        public bool IsFunction(string word)
        {
            string name = FormulaCompiler.FunctionName(word);
            return !word.Contains('.', StringComparison.Ordinal) && FormulaFunctions.IsBuiltIn(name)
                && (book is null || !book.Names.Any(n => string.Equals(n.Name, word, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>The table of this name, or the one around the cell when it is null.</summary>
        public SheetTable? Table(string? name)
        {
            if (name is null)
            {
                return sheet?.Tables.Find(cell);
            }

            return book is null ? null : FormulaEvaluator.FindTable(book, name);
        }
    }

    private readonly struct Writer(StringBuilder output, ReferenceStyle style, CellAddress origin, int rowDelta, int columnDelta, Stored stored, Locals? locals = null, Func<ReferenceNode, Area?>? moved = null, Func<FormulaNode, bool>? gone = null)
    {
        public void Node(FormulaNode node)
        {
            output.Append(node.Lead);
            if (gone is not null && node is StructuredNode or NameNode && gone(node))
            {
                output.Append("#REF!");
                return;
            }

            switch (node)
            {
                case NumberNode number:
                    output.Append(number.Text);
                    break;
                case StringNode text:
                    output.Append(text.Text);
                    break;
                case BooleanNode boolean:
                    output.Append(boolean.Text);
                    break;
                case ErrorNode error:
                    output.Append(error.Text);
                    break;
                case ArrayNode array:
                    output.Append(array.Text);
                    break;
                case MissingNode:
                    break;
                case ReferenceNode reference when IsLocal(reference):
                    output.Append("_xlpm.").Append(reference.Text);
                    break;
                case ReferenceNode reference:
                    Reference(reference);
                    break;
                case NameNode name when !IsLocal(name) && locals?.Table(name.Name) is { } table && !name.Name.Contains('!', StringComparison.Ordinal):
                    output.Append(table.Name).Append("[]");
                    break;
                case NameNode name when IsLocal(name):
                    output.Append(name.Name.StartsWith("_xl", StringComparison.OrdinalIgnoreCase) ? "" : "_xlpm.").Append(name.Name);
                    break;
                case NameNode { Prefix: null } name when locals?.IsFunction(name.Name) == true:
                    output.Append("_xleta.").Append(name.Name.ToUpperInvariant());
                    break;
                case NameNode name:
                    Prefix(name.Prefix);
                    output.Append(name.Name);
                    break;
                case StructuredNode structured when locals?.Table(structured.Table) is { } table:
                    Structured(structured, table);
                    break;
                case StructuredNode structured:
                    output.Append(structured.Text);
                    break;
                case InvokeNode invoke:
                    Node(invoke.Callee);
                    output.Append('(');
                    List(invoke.Arguments);
                    output.Append(')');
                    break;
                case FunctionNode function when locals is not null && FormulaCompiler.FunctionName(function.Name) is "LET" or "LAMBDA":
                    Scoped(function);
                    break;
                case FunctionNode function when locals is not null && !FormulaFunctions.IsBuiltIn(FormulaCompiler.FunctionName(function.Name)) && locals.Has(FormulaCompiler.FunctionName(function.Name)):
                    output.Append(function.Name.StartsWith("_xl", StringComparison.OrdinalIgnoreCase) ? "" : "_xlpm.").Append(function.Name).Append('(');
                    List(function.Arguments);
                    output.Append(')');
                    break;
                case FunctionNode function:
                    output.Append(stored == Stored.No ? function.Name : FutureFunctions.StoredName(function.Name)).Append('(');
                    List(function.Arguments);
                    output.Append(')');
                    break;
                case ParenthesesNode parentheses:
                    output.Append('(');
                    List(parentheses.Items);
                    output.Append(')');
                    break;
                case UnaryNode { Operator: '@' } single when stored == Stored.Ordinary:
                    Node(single.Operand);
                    break;
                case UnaryNode { Operator: '@' } single when stored == Stored.Dynamic:
                    output.Append("_xlfn.SINGLE(");
                    Node(single.Operand);
                    output.Append(')');
                    break;
                case UnaryNode unary:
                    output.Append(unary.Operator);
                    Node(unary.Operand);
                    break;
                case PostfixNode { Operator: '#' } spill when stored != Stored.No:
                    output.Append("_xlfn.ANCHORARRAY(");
                    Node(spill.Operand);
                    output.Append(')');
                    break;
                case PostfixNode postfix:
                    Node(postfix.Operand);
                    output.Append(postfix.OperatorLead).Append(postfix.Operator);
                    break;
                case BinaryNode binary:
                    if (style != ReferenceStyle.Written && binary.Operator == BinaryOperator.Range && Rightmost(binary.Left) is NameNode or NumberNode)
                    {
                        // A name or number before ':' would join what follows into one reference once references are
                        // written afresh (a name a before a column S reads as the columns A:S), so it is kept apart.
                        output.Append('(');
                        Node(binary.Left);
                        output.Append(')');
                    }
                    else
                    {
                        Node(binary.Left);
                    }

                    output.Append(binary.OperatorLead).Append(binary.OperatorText);
                    Node(binary.Right);
                    break;
            }
        }

        /// <summary>
        /// A structured reference as a file stores it: <c>Sales[]</c>, <c>Sales[#All]</c>, <c>Sales[Qty]</c>,
        /// <c>Sales[[Qty]:[Price]]</c> or <c>Sales[[#This Row],[Qty]]</c>, with the names as the table has them.
        /// </summary>
        private void Structured(StructuredNode node, SheetTable table)
        {
            output.Append(table.Name);
            bool hasColumn = node.FirstColumn is not null;
            List<string> items = [];
            foreach ((TableItems item, string text) in Items)
            {
                if (node.Items.HasFlag(item))
                {
                    items.Add(text);
                }
            }

            if (items.Count + (hasColumn ? 1 : 0) == 0)
            {
                output.Append("[]");
            }
            else if (items.Count == 1 && !hasColumn)
            {
                output.Append('[').Append(items[0]).Append(']');
            }
            else if (items.Count == 0 && node.LastColumn is null)
            {
                output.Append('[').Append(ColumnName(table, node.FirstColumn!)).Append(']');
            }
            else
            {
                output.Append('[');
                for (int i = 0; i < items.Count; i++)
                {
                    output.Append(i > 0 ? "," : "").Append('[').Append(items[i]).Append(']');
                }

                if (hasColumn)
                {
                    output.Append(items.Count > 0 ? "," : "").Append('[').Append(ColumnName(table, node.FirstColumn!)).Append(']');
                    if (node.LastColumn is { } last)
                    {
                        output.Append(":[").Append(ColumnName(table, last)).Append(']');
                    }
                }

                output.Append(']');
            }
        }

        /// <summary>A column's name as the table writes it, with what brackets give a meaning to escaped.</summary>
        private static string ColumnName(SheetTable table, string name)
        {
            string written = table.Columns.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Name ?? name;
            StringBuilder escaped = new(written.Length);
            foreach (char c in written)
            {
                if (c is '[' or ']' or '#' or '\'')
                {
                    escaped.Append('\'');
                }

                escaped.Append(c);
            }

            return escaped.ToString();
        }

        private bool IsLocal(FormulaNode word) => locals is not null && FormulaCompiler.LocalName(word).Name is { } name && locals.Has(name);

        /// <summary>A LET or LAMBDA as a file stores it, its names with their prefixes; a LET's value sees only the names before it.</summary>
        private void Scoped(FunctionNode function)
        {
            bool let = FormulaCompiler.FunctionName(function.Name) == "LET";
            int outer = locals!.Count;
            int last = function.Arguments.Length - 1;
            output.Append(FutureFunctions.StoredName(function.Name)).Append('(');
            for (int i = 0; i <= last; i++)
            {
                Argument argument = function.Arguments[i];
                bool isName = i < last && (!let || i % 2 == 0);
                if (i > 0)
                {
                    output.Append(',');
                }

                if (isName && FormulaCompiler.LocalName(argument.Node) is { Name: { } name } local)
                {
                    output.Append(argument.Node.Lead).Append(local.Optional ? "_xlop." : "_xlpm.").Append(argument.Node switch
                    {
                        StructuredNode { FirstColumn: { } column } => column,
                        ReferenceNode reference => reference.Text,
                        NameNode written => written.Name.StartsWith("_xl", StringComparison.OrdinalIgnoreCase) ? written.Name[6..] : written.Name,
                        _ => name,
                    });
                    if (!let)
                    {
                        locals.Add(name);
                    }
                }
                else
                {
                    Node(argument.Node);
                    if (let && i < last)
                    {
                        locals.Add(FormulaCompiler.LocalName(function.Arguments[i - 1].Node).Name!);
                    }
                }

                output.Append(argument.Trail);
            }

            output.Append(')');
            locals.Back(outer);
        }

        /// <summary>The last token a node writes.</summary>
        private static FormulaNode Rightmost(FormulaNode node) => node switch
        {
            BinaryNode binary => Rightmost(binary.Right),
            UnaryNode unary => Rightmost(unary.Operand),
            _ => node,
        };

        /// <summary>
        /// A sheet prefix: as written in <see cref="ReferenceStyle.Written"/>, otherwise made again and quoted when a
        /// sheet name would not read back unquoted in either style (a name such as <c>R</c> or <c>C2</c> is a reference).
        /// </summary>
        private void Prefix(SheetPrefix? prefix)
        {
            if (prefix is null)
            {
                return;
            }

            if (style == ReferenceStyle.Written || prefix.FirstSheet is null)
            {
                output.Append(prefix.Text);
                return;
            }

            string sheets = prefix.LastSheet is { } last && last != prefix.FirstSheet ? $"{prefix.FirstSheet}:{last}" : prefix.FirstSheet;
            string book = prefix.Book is null ? "" : $"[{prefix.Book}]";
            if (SheetNames.IsPlain(prefix.FirstSheet) && SheetNames.IsPlain(prefix.LastSheet ?? prefix.FirstSheet) && (prefix.Book ?? "").All(c => char.IsLetterOrDigit(c) || c is '_' or '.'))
            {
                output.Append(book).Append(sheets).Append('!');
                return;
            }

            output.Append('\'').Append((book + sheets).Replace("'", "''", StringComparison.Ordinal)).Append("'!");
        }

        private void List(Argument[] items)
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                {
                    output.Append(',');
                }

                Node(items[i].Node);
                output.Append(items[i].Trail);
            }
        }

        private void Reference(ReferenceNode reference)
        {
            Prefix(reference.Prefix);
            if (style == ReferenceStyle.Written)
            {
                if (moved?.Invoke(reference) is { } to)
                {
                    if (to.Kind == AreaKind.Error)
                    {
                        output.Append("#REF!");
                    }
                    else
                    {
                        A1(to);
                    }
                }
                else
                {
                    output.Append(reference.Text);
                }

                return;
            }

            Area area = reference.Area;
            if (area.Kind == AreaKind.Error)
            {
                output.Append("#REF!");
            }
            else if (style == ReferenceStyle.R1C1)
            {
                R1C1(area);
            }
            else
            {
                A1(area);
            }
        }

        private void A1(Area area)
        {
            int firstRow = Move(area.FirstRow, area.FirstRowAbsolute, rowDelta, CellAddress.MaxRows);
            int firstColumn = Move(area.FirstColumn, area.FirstColumnAbsolute, columnDelta, CellAddress.MaxColumns);
            int lastRow = Move(area.LastRow, area.LastRowAbsolute, rowDelta, CellAddress.MaxRows);
            int lastColumn = Move(area.LastColumn, area.LastColumnAbsolute, columnDelta, CellAddress.MaxColumns);
            if (firstRow < 0 || firstColumn < 0 || lastRow < 0 || lastColumn < 0)
            {
                output.Append("#REF!");
                return;
            }

            switch (area.Kind)
            {
                case AreaKind.Cell:
                    Column(firstColumn, area.FirstColumnAbsolute);
                    Row(firstRow, area.FirstRowAbsolute);
                    break;
                case AreaKind.Range:
                    Column(firstColumn, area.FirstColumnAbsolute);
                    Row(firstRow, area.FirstRowAbsolute);
                    output.Append(':');
                    Column(lastColumn, area.LastColumnAbsolute);
                    Row(lastRow, area.LastRowAbsolute);
                    break;
                case AreaKind.Rows:
                    Row(firstRow, area.FirstRowAbsolute);
                    output.Append(':');
                    Row(lastRow, area.LastRowAbsolute);
                    break;
                case AreaKind.Columns:
                    Column(firstColumn, area.FirstColumnAbsolute);
                    output.Append(':');
                    Column(lastColumn, area.LastColumnAbsolute);
                    break;
            }
        }

        private static int Move(int value, bool absolute, int delta, int count)
        {
            if (absolute)
            {
                return value;
            }

            int moved = value + delta;
            return moved >= 0 && moved < count ? moved : -1;
        }

        private void Column(int column, bool absolute)
        {
            if (absolute)
            {
                output.Append('$');
            }

            output.Append(CellAddress.ColumnName(column));
        }

        private void Row(int row, bool absolute)
        {
            if (absolute)
            {
                output.Append('$');
            }

            output.Append((row + 1).ToString(CultureInfo.InvariantCulture));
        }

        private void R1C1(Area area)
        {
            switch (area.Kind)
            {
                case AreaKind.Cell:
                    R1C1Row(area.FirstRow, area.FirstRowAbsolute);
                    R1C1Column(area.FirstColumn, area.FirstColumnAbsolute);
                    break;
                case AreaKind.Range:
                    R1C1Row(area.FirstRow, area.FirstRowAbsolute);
                    R1C1Column(area.FirstColumn, area.FirstColumnAbsolute);
                    output.Append(':');
                    R1C1Row(area.LastRow, area.LastRowAbsolute);
                    R1C1Column(area.LastColumn, area.LastColumnAbsolute);
                    break;
                case AreaKind.Rows:
                    R1C1Row(area.FirstRow, area.FirstRowAbsolute);
                    if (area.FirstRow != area.LastRow || area.FirstRowAbsolute != area.LastRowAbsolute)
                    {
                        output.Append(':');
                        R1C1Row(area.LastRow, area.LastRowAbsolute);
                    }

                    break;
                case AreaKind.Columns:
                    R1C1Column(area.FirstColumn, area.FirstColumnAbsolute);
                    if (area.FirstColumn != area.LastColumn || area.FirstColumnAbsolute != area.LastColumnAbsolute)
                    {
                        output.Append(':');
                        R1C1Column(area.LastColumn, area.LastColumnAbsolute);
                    }

                    break;
            }
        }

        private void R1C1Row(int row, bool absolute)
        {
            output.Append('R');
            R1C1Number(row, absolute, origin.Row, CellAddress.MaxRows);
        }

        private void R1C1Column(int column, bool absolute)
        {
            output.Append('C');
            R1C1Number(column, absolute, origin.Column, CellAddress.MaxColumns);
        }

        private void R1C1Number(int value, bool absolute, int from, int count)
        {
            if (absolute)
            {
                output.Append((value + 1).ToString(CultureInfo.InvariantCulture));
                return;
            }

            int offset = value - from;
            if (offset != 0)
            {
                output.Append('[').Append(offset.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
        }
    }
}
