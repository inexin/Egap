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
        Writer writer = new(output, style, formula.Origin, target.Row - formula.Origin.Row, target.Column - formula.Origin.Column);
        writer.Node(formula.Root);
        output.Append(formula.Trail);
        return output.ToString();
    }

    private readonly struct Writer(StringBuilder output, ReferenceStyle style, CellAddress origin, int rowDelta, int columnDelta)
    {
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
                case ReferenceNode reference:
                    Reference(reference);
                    break;
                case NameNode name:
                    output.Append(name.Prefix?.Text).Append(name.Name);
                    break;
                case StructuredNode structured:
                    output.Append(structured.Text);
                    break;
                case FunctionNode function:
                    output.Append(function.Name).Append('(');
                    List(function.Arguments);
                    output.Append(')');
                    break;
                case ParenthesesNode parentheses:
                    output.Append('(');
                    List(parentheses.Items);
                    output.Append(')');
                    break;
                case UnaryNode unary:
                    output.Append(unary.Operator);
                    Node(unary.Operand);
                    break;
                case PostfixNode postfix:
                    Node(postfix.Operand);
                    output.Append(postfix.OperatorLead).Append(postfix.Operator);
                    break;
                case BinaryNode binary:
                    Node(binary.Left);
                    output.Append(binary.OperatorLead).Append(binary.OperatorText);
                    Node(binary.Right);
                    break;
            }
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
            output.Append(reference.Prefix?.Text);
            if (style == ReferenceStyle.Written)
            {
                output.Append(reference.Text);
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
