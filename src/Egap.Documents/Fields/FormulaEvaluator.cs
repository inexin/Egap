using System.Globalization;

namespace Egap.Documents;

/// <summary>
/// Evaluates the expression of a formula field (<c>= expression</c>, ECMA-376 Part 1 17.16.5.1 and Word's field-code
/// reference) as Word does: numbers, + - * / % ^ and the comparisons = &lt;&gt; &lt; &lt;= &gt; &gt;= (1 or 0), Word's
/// functions, bookmarks, and in a table cell the table's cells by A1 or RnCn references, ranges and LEFT, RIGHT, ABOVE and
/// BELOW. A formula that cannot be evaluated gives Word's error text, such as <c>!Zero Divide</c>.
/// </summary>
internal sealed class FormulaEvaluator
{
    private static readonly string[] Functions =
    [
        "ABS", "AND", "AVERAGE", "COUNT", "DEFINED", "FALSE", "IF", "INT", "MAX", "MIN", "MOD", "NOT", "OR", "PRODUCT", "ROUND",
        "SIGN", "SUM", "TRUE",
    ];

    private readonly Func<string, Bookmark?> bookmarks;
    private readonly Cell? cell;
    private readonly char decimalSymbol;
    private readonly char listSeparator;
    private readonly Dictionary<Table, Cell?[][]> grids = [];
    private List<Token> tokens = [];
    private int position;

    private FormulaEvaluator(Document document, Node? at, Func<string, Bookmark?>? bookmarks)
    {
        this.bookmarks = bookmarks ?? document.GetBookmark;
        cell = at?.GetAncestor<Cell>();
        decimalSymbol = document.Settings.DecimalSymbol;
        listSeparator = document.Settings.ListSeparator;
    }

    private enum TokenKind
    {
        Number,
        Name,
        Operator,
        End,
    }

    /// <summary>
    /// The value of <paramref name="expression"/> for a field at <paramref name="at"/> (its table cell gives the table
    /// references), or Word's error text. <paramref name="bookmarks"/> finds bookmarks by name (the document's own lookup
    /// when null).
    /// </summary>
    public static (double Value, string? Error) Evaluate(string expression, Document document, Node? at, Func<string, Bookmark?>? bookmarks = null)
    {
        FormulaEvaluator evaluator = new(document, at, bookmarks);
        try
        {
            Expr tree = evaluator.ParseAll(expression);
            return (evaluator.Scalar(tree), null);
        }
        catch (FormulaException error)
        {
            return (0, error.Message);
        }
    }

    /// <summary>A formula's value as Word shows it without a numeric picture: rounded to two decimals.</summary>
    public static string FormatDefault(double value, char decimalSymbol)
    {
        if (!double.IsFinite(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        string text = (rounded == 0 ? 0 : rounded).ToString("0.##", CultureInfo.InvariantCulture);
        return decimalSymbol == '.' ? text : text.Replace('.', decimalSymbol);
    }

    /// <summary>
    /// A number in a cell's or bookmark's text, as Word reads it: a sign, currency symbols, digit grouping symbols, the
    /// decimal symbol and a closing percent sign; null when the text is empty or not a number.
    /// </summary>
    public static double? ParseNumber(string text, char decimalSymbol)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return null;
        }

        char group = decimalSymbol == ',' ? '.' : ',';
        bool negative = false, digits = false, point = false, percent = false, sign = false;
        double whole = 0, scale = 1;
        double fraction = 0;
        bool parenthesis = span[0] == '(' && span[^1] == ')';
        if (parenthesis)
        {
            negative = true;
            span = span[1..^1].Trim();
        }

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c is >= '0' and <= '9')
            {
                if (percent)
                {
                    return null;
                }

                digits = true;
                if (point)
                {
                    scale /= 10;
                    fraction += (c - '0') * scale;
                }
                else
                {
                    whole = (whole * 10) + (c - '0');
                }
            }
            else if (c == decimalSymbol && !point && !percent)
            {
                point = true;
            }
            else if ((c == group || c is '\u00A0' or '\u202F' || (c == ' ' && group == '.')) && digits && !point && !percent
                && i > 0 && span[i - 1] is >= '0' and <= '9' && i + 1 < span.Length && span[i + 1] is >= '0' and <= '9')
            {
                // A grouping symbol between digits, three digits before the next one or the end of the digits.
                if (i + 4 > span.Length || !char.IsAsciiDigit(span[i + 2]) || !char.IsAsciiDigit(span[i + 3]) || (i + 4 < span.Length && char.IsAsciiDigit(span[i + 4])))
                {
                    return null;
                }
            }
            else if (c is '-' or '+' && !digits && !sign && !parenthesis)
            {
                negative = c == '-';
                sign = true;
            }
            else if (c == '%' && digits && !percent)
            {
                percent = true;
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol || (c == ' ' && !(i > 0 && char.IsAsciiDigit(span[i - 1]) && i + 1 < span.Length && char.IsAsciiDigit(span[i + 1]))))
            {
                // Currency symbols and spaces around them.
            }
            else
            {
                return null;
            }
        }

        if (!digits)
        {
            return null;
        }

        double value = whole + fraction;
        value = percent ? value / 100 : value;
        return negative ? -value : value;
    }

    private Expr ParseAll(string expression)
    {
        tokens = Tokenize(expression);
        position = 0;
        Expr tree = Comparison();
        if (Peek.Kind != TokenKind.End)
        {
            throw Syntax(Peek);
        }

        return tree;
    }

    private Token Peek => tokens[position];

    private Token Next() => tokens[position++];

    private bool IsOperator(string text)
    {
        return Peek.Kind == TokenKind.Operator && Peek.Text == text;
    }

    private Expr Comparison()
    {
        Expr left = Additive();
        while (Peek.Kind == TokenKind.Operator && Peek.Text is "=" or "<>" or "<" or "<=" or ">" or ">=")
        {
            string op = Next().Text;
            left = new Binary(op, left, Additive());
        }

        return left;
    }

    private Expr Additive()
    {
        Expr left = Multiplicative();
        while (IsOperator("+") || IsOperator("-"))
        {
            string op = Next().Text;
            left = new Binary(op, left, Multiplicative());
        }

        return left;
    }

    private Expr Multiplicative()
    {
        Expr left = Power();
        while (IsOperator("*") || IsOperator("/"))
        {
            string op = Next().Text;
            left = new Binary(op, left, Power());
        }

        return left;
    }

    private Expr Power()
    {
        // Word raises to powers from the right: 2^3^2 is 2^9.
        Expr left = Unary();
        if (IsOperator("^"))
        {
            Next();
            return new Binary("^", left, Power());
        }

        return left;
    }

    private Expr Unary()
    {
        if (IsOperator("-") || IsOperator("+"))
        {
            bool minus = Next().Text == "-";
            Expr operand = Unary();
            return minus ? new Negate(operand) : operand;
        }

        Expr primary = Primary();
        while (IsOperator("%"))
        {
            Next();
            primary = new Percent(primary);
        }

        return primary;
    }

    private Expr Primary()
    {
        Token token = Next();
        switch (token.Kind)
        {
            case TokenKind.End:
                throw new FormulaException("!Unexpected End of Formula");
            case TokenKind.Number when IsOperator(":"):
                return RangeFrom(token.Text, null);
            case TokenKind.Number:
                return new Number(token.Value);
            case TokenKind.Operator when token.Text == "(":
                Expr inner = Comparison();
                Expect(")");
                return inner;
            case TokenKind.Name:
                string upper = token.Text.ToUpperInvariant();
                if (Array.IndexOf(Functions, upper) >= 0 && (IsOperator("(") || upper is "TRUE" or "FALSE"))
                {
                    return ParseCall(upper);
                }

                if (IsOperator(":"))
                {
                    return RangeFrom(token.Text, null);
                }

                // A bookmarked table and a reference into it: "Prices B2:C4".
                if (Peek.Kind == TokenKind.Name || (Peek.Kind == TokenKind.Number && tokens[position + 1] is { Kind: TokenKind.Operator, Text: ":" }))
                {
                    Token reference = Next();
                    if (reference.Kind == TokenKind.Name && !IsOperator(":") && !IsCellName(reference.Text))
                    {
                        throw Syntax(reference);
                    }

                    return IsOperator(":") ? RangeFrom(reference.Text, token.Text) : new Reference(reference.Text, token.Text);
                }

                return new Reference(token.Text, null);
            default:
                throw Syntax(token);
        }
    }

    private Range RangeFrom(string first, string? table)
    {
        Next();
        Token last = Next();
        if (last.Kind == TokenKind.End)
        {
            throw new FormulaException("!Unexpected End of Formula");
        }

        if (last.Kind is not (TokenKind.Name or TokenKind.Number))
        {
            throw Syntax(last);
        }

        return new Range(first, last.Text, table);
    }

    private Call ParseCall(string name)
    {
        List<Expr> arguments = [];
        if (IsOperator("("))
        {
            Next();
            if (IsOperator(")"))
            {
                Next();
            }
            else
            {
                while (true)
                {
                    arguments.Add(Comparison());
                    if (IsOperator(listSeparator.ToString()))
                    {
                        Next();
                        continue;
                    }

                    Expect(")");
                    break;
                }
            }
        }

        (int min, int max) = name switch
        {
            "TRUE" or "FALSE" => (0, 0),
            "ABS" or "INT" or "NOT" or "SIGN" or "DEFINED" => (1, 1),
            "MOD" or "ROUND" => (2, 2),
            "IF" => (3, 3),
            _ => (1, int.MaxValue),
        };
        if (arguments.Count < min)
        {
            throw new FormulaException("!Syntax Error, )");
        }

        if (arguments.Count > max)
        {
            throw new FormulaException("!Syntax Error, " + listSeparator);
        }

        return new Call(name, arguments);
    }

    private void Expect(string text)
    {
        Token token = Next();
        if (token.Kind == TokenKind.End)
        {
            throw new FormulaException("!Unexpected End of Formula");
        }

        if (token.Kind != TokenKind.Operator || token.Text != text)
        {
            throw Syntax(token);
        }
    }

    private static FormulaException Syntax(Token token)
    {
        return new FormulaException("!Syntax Error, " + token.Text);
    }

    private List<Token> Tokenize(string expression)
    {
        List<Token> list = [];
        int i = 0;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c is >= '0' and <= '9' || (c == decimalSymbol && i + 1 < expression.Length && expression[i + 1] is >= '0' and <= '9'))
            {
                int start = i;
                double value = 0, scale = 1;
                bool point = false;
                while (i < expression.Length && (expression[i] is >= '0' and <= '9' || (expression[i] == decimalSymbol && !point)))
                {
                    if (expression[i] == decimalSymbol)
                    {
                        point = true;
                    }
                    else if (point)
                    {
                        scale /= 10;
                        value += (expression[i] - '0') * scale;
                    }
                    else
                    {
                        value = (value * 10) + (expression[i] - '0');
                    }

                    i++;
                }

                list.Add(new Token(TokenKind.Number, expression[start..i], value));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                {
                    i++;
                }

                list.Add(new Token(TokenKind.Name, expression[start..i], 0));
            }
            else if (c is '<' or '>' && i + 1 < expression.Length && (expression[i + 1] == '=' || (c == '<' && expression[i + 1] == '>')))
            {
                list.Add(new Token(TokenKind.Operator, expression.Substring(i, 2), 0));
                i += 2;
            }
            else
            {
                list.Add(new Token(TokenKind.Operator, c.ToString(), 0));
                i++;
            }
        }

        list.Add(new Token(TokenKind.End, "", 0));
        return list;
    }

    private double Scalar(Expr expr)
    {
        switch (expr)
        {
            case Number number:
                return number.Value;
            case Negate negate:
                return -Scalar(negate.Operand);
            case Percent percent:
                return Scalar(percent.Operand) / 100;
            case Binary binary:
                return Apply(binary.Op, Scalar(binary.Left), Scalar(binary.Right));
            case Call call:
                return Function(call);
            default:
                List<double> values = [];
                Values(expr, values, scalar: true);
                return values.Count > 0 ? values[0] : 0;
        }
    }

    private static double Apply(string op, double a, double b)
    {
        switch (op)
        {
            case "+":
                return a + b;
            case "-":
                return a - b;
            case "*":
                return a * b;
            case "/":
                return b == 0 ? throw new FormulaException("!Zero Divide") : a / b;
            case "^":
                double power = Math.Pow(a, b);
                return double.IsInfinity(power) && a == 0 ? throw new FormulaException("!Zero Divide") : power;
            case "=":
                return a == b ? 1 : 0;
            case "<>":
                return a != b ? 1 : 0;
            case "<":
                return a < b ? 1 : 0;
            case "<=":
                return a <= b ? 1 : 0;
            case ">":
                return a > b ? 1 : 0;
            default:
                return a >= b ? 1 : 0;
        }
    }

    private double Function(Call call)
    {
        List<Expr> args = call.Arguments;
        switch (call.Name)
        {
            case "TRUE":
                return 1;
            case "FALSE":
                return 0;
            case "ABS":
                return Math.Abs(Scalar(args[0]));
            case "INT":
                return Math.Truncate(Scalar(args[0]));
            case "SIGN":
                return Math.Sign(Scalar(args[0]));
            case "NOT":
                return Scalar(args[0]) == 0 ? 1 : 0;
            case "MOD":
                double dividend = Scalar(args[0]), divisor = Scalar(args[1]);
                return divisor == 0 ? throw new FormulaException("!Zero Divide") : dividend % divisor;
            case "ROUND":
                return Round(Scalar(args[0]), Scalar(args[1]));
            case "IF":
                return Scalar(args[0]) != 0 ? Scalar(args[1]) : Scalar(args[2]);
            case "DEFINED":
                try
                {
                    Scalar(args[0]);
                    return 1;
                }
                catch (FormulaException)
                {
                    return 0;
                }
        }

        List<double> values = [];
        foreach (Expr arg in args)
        {
            Values(arg, values, scalar: false);
        }

        switch (call.Name)
        {
            case "AND":
                return values.Count > 0 && values.TrueForAll(v => v != 0) ? 1 : 0;
            case "OR":
                return values.Exists(v => v != 0) ? 1 : 0;
            case "COUNT":
                return values.Count;
            case "AVERAGE":
                return values.Count == 0 ? 0 : values.Sum() / values.Count;
            case "MAX":
                return values.Count == 0 ? 0 : values.Max();
            case "MIN":
                return values.Count == 0 ? 0 : values.Min();
            case "PRODUCT":
                double product = values.Count == 0 ? 0 : 1;
                foreach (double v in values)
                {
                    product *= v;
                }

                return product;
            default:
                return values.Sum();
        }
    }

    private static double Round(double value, double places)
    {
        int digits = (int)Math.Truncate(places);
        if (digits >= 0)
        {
            return digits > 15 ? value : (double)Math.Round((decimal)value, digits, MidpointRounding.AwayFromZero);
        }

        double factor = Math.Pow(10, -digits);
        return Math.Round(value / factor, MidpointRounding.AwayFromZero) * factor;
    }

    /// <summary>
    /// Adds the numbers <paramref name="expr"/> stands for: a range's or a positional reference's numeric cells, or one
    /// value. In a function's list, empty and text cells are left out; true when it named cells.
    /// </summary>
    private bool Values(Expr expr, List<double> values, bool scalar)
    {
        switch (expr)
        {
            case Range range:
                if (range.Table is null && cell?.GetAncestor<Table>() is null)
                {
                    // Outside a table a range names no cells, as a single reference does.
                    if (scalar)
                    {
                        values.Add(0);
                    }

                    return true;
                }

                Table table = TableFor(range.Table, range.First + ":" + range.Last);
                foreach (Cell c in RangeCells(table, range.First, range.Last))
                {
                    AddCell(c, values, scalar);
                }

                return true;
            case Reference reference:
                return ReferenceValues(reference, values, scalar);
            default:
                values.Add(Scalar(expr));
                return false;
        }
    }

    private bool ReferenceValues(Reference reference, List<double> values, bool scalar)
    {
        string name = reference.Name;
        string upper = name.ToUpperInvariant();
        if (reference.Table is null && upper is "LEFT" or "RIGHT" or "ABOVE" or "BELOW")
        {
            // Outside a table there are no cells to read: the reference counts as an empty cell, as in Word.
            if (cell?.GetAncestor<Table>() is not { } here)
            {
                if (scalar)
                {
                    values.Add(0);
                }

                return true;
            }

            foreach (double v in Positional(here, upper))
            {
                values.Add(v);
            }

            return true;
        }

        if (reference.Table is not null || (cell is not null && IsCellName(name)))
        {
            Table table = TableFor(reference.Table, name);
            (int row, int column) = CellAddress(name);
            Cell?[][] grid = Grid(table);
            if (row >= grid.Length || column >= grid[row].Length || grid[row][column] is not { } found)
            {
                if (scalar)
                {
                    values.Add(0);
                }

                return true;
            }

            AddCell(found, values, scalar);
            return true;
        }

        if (bookmarks(name) is { } bookmark)
        {
            if (bookmark.End is { } end && bookmark.Start.GetAncestor<Cell>() is { } first && first.GetAncestor<Table>() is { } table)
            {
                Cell last = end.GetAncestor<Cell>() is { } endCell && endCell.GetAncestor<Table>() == table ? endCell : table.Rows[^1].Cells[^1];
                foreach (Cell c in Between(table, first, last))
                {
                    AddCell(c, values, scalar);
                }

                return true;
            }

            double? number = ParseNumber(bookmark.Text.Replace(ControlChar.ParagraphBreak, ' '), decimalSymbol);
            if (number is not null || scalar)
            {
                values.Add(number ?? 0);
            }

            return false;
        }

        if (IsCellName(name))
        {
            CellAddress(name);
            if (scalar)
            {
                values.Add(0);
            }

            return true;
        }

        throw Undefined(name);
    }

    /// <summary>Word's error for a name that is no bookmark, which it shows in capitals.</summary>
    private static FormulaException Undefined(string name)
    {
        return new FormulaException("!Undefined Bookmark, " + name.ToUpperInvariant());
    }

    private void AddCell(Cell c, List<double> values, bool scalar)
    {
        double? number = ParseNumber(CellText(c), decimalSymbol);
        if (number is not null || scalar)
        {
            values.Add(number ?? 0);
        }
    }

    /// <summary>The table a reference reads: the bookmarked table <paramref name="bookmark"/> names, or the field's own.</summary>
    private Table TableFor(string? bookmark, string reference)
    {
        if (bookmark is not null)
        {
            if (bookmarks(bookmark) is not { } found)
            {
                throw Undefined(bookmark);
            }

            return found.Start.GetAncestor<Table>() ?? throw new FormulaException("!" + reference + " Is Not In Table");
        }

        return cell?.GetAncestor<Table>() ?? throw new FormulaException("!" + reference + " Is Not In Table");
    }

    /// <summary>Numbers next to the field's cell, toward the side: empty and text cells before the first number are skipped, and the run of numbers stops at the next one.</summary>
    private List<double> Positional(Table table, string side)
    {
        Cell?[][] grid = Grid(table);
        (int row, int column) = Find(grid, cell!);
        (int dr, int dc) = side switch
        {
            "LEFT" => (0, -1),
            "RIGHT" => (0, 1),
            "ABOVE" => (-1, 0),
            _ => (1, 0),
        };
        List<double> found = [];
        Cell? previous = cell;
        for (int r = row + dr, c = column + dc; r >= 0 && r < grid.Length && c >= 0 && c < grid[r].Length; r += dr, c += dc)
        {
            if (grid[r][c] is not { } next || next == previous)
            {
                continue;
            }

            previous = next;
            double? number = ParseNumber(CellText(next), decimalSymbol);
            if (number is double value)
            {
                found.Add(value);
            }
            else if (found.Count > 0)
            {
                break;
            }
        }

        return found;
    }

    private List<Cell> RangeCells(Table table, string first, string last)
    {
        Cell?[][] grid = Grid(table);
        int columns = grid.Length == 0 ? 0 : grid.Max(r => r.Length);
        int r1, c1, r2, c2;
        if (IsRowColumnName(first) || IsRowColumnName(last))
        {
            // Word takes RnCn for single cells only.
            throw new FormulaException("!Syntax Error, :");
        }

        if (IsCellName(first) && IsCellName(last))
        {
            (r1, c1) = CellAddress(first);
            (r2, c2) = CellAddress(last);
        }
        else if (ColumnOf(first) is int a && ColumnOf(last) is int b)
        {
            (r1, c1, r2, c2) = (0, a, grid.Length - 1, b);
        }
        else if (RowOf(first) is int x && RowOf(last) is int y)
        {
            (r1, c1, r2, c2) = (x, 0, y, columns - 1);
        }
        else
        {
            throw new FormulaException("!Syntax Error, :");
        }

        return Rectangle(grid, Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
    }

    private List<Cell> Between(Table table, Cell first, Cell last)
    {
        Cell?[][] grid = Grid(table);
        (int r1, int c1) = Find(grid, first);
        (int r2, int c2) = Find(grid, last);
        return Rectangle(grid, Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
    }

    private static List<Cell> Rectangle(Cell?[][] grid, int r1, int c1, int r2, int c2)
    {
        List<Cell> cells = [];
        HashSet<Cell> seen = [];
        for (int r = Math.Max(0, r1); r <= r2 && r < grid.Length; r++)
        {
            for (int c = Math.Max(0, c1); c <= c2 && c < grid[r].Length; c++)
            {
                if (grid[r][c] is { } found && seen.Add(found))
                {
                    cells.Add(found);
                }
            }
        }

        return cells;
    }

    private static (int Row, int Column) Find(Cell?[][] grid, Cell target)
    {
        for (int r = 0; r < grid.Length; r++)
        {
            int c = Array.IndexOf(grid[r], target);
            if (c >= 0)
            {
                return (r, c);
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// The table's cells as Word counts them for references: each row's cells in order, whatever columns a cell spans, and a
    /// cell continuing a vertical merge standing for the merge's first cell (the cell above it in the same grid column).
    /// </summary>
    private Cell?[][] Grid(Table table)
    {
        if (grids.TryGetValue(table, out Cell?[][]? cached))
        {
            return cached;
        }

        Cell?[][] grid = new Cell?[table.Rows.Count][];
        List<Cell?> above = [], columns = [];
        for (int r = 0; r < grid.Length; r++)
        {
            List<Cell?> row = [];
            columns.Clear();
            foreach (Cell c in table.Rows[r].Cells)
            {
                int span = Math.Max(1, c.Format.GridSpan ?? 1);
                Cell? owner = c;
                if (c.Format.VerticalMerge == VerticalMerge.Continue && columns.Count < above.Count)
                {
                    owner = above[columns.Count] ?? c;
                }

                row.Add(owner);
                for (int i = 0; i < span; i++)
                {
                    columns.Add(owner);
                }
            }

            grid[r] = [.. row];
            (above, columns) = (columns, above);
        }

        grids[table] = grid;
        return grid;
    }

    /// <summary>A cell's visible text, its paragraphs joined by spaces.</summary>
    private static string CellText(Cell c)
    {
        string text = "";
        foreach (Block block in c.Blocks)
        {
            if (block is Paragraph paragraph)
            {
                string part = FieldUpdater.VisibleText(paragraph);
                text = text.Length == 0 ? part : part.Length == 0 ? text : text + " " + part;
            }
        }

        return text;
    }

    /// <summary>A1 (column letters, row number) or RnCn.</summary>
    private static bool IsCellName(string name)
    {
        return TryCellAddress(name, out _, out _);
    }

    private static (int Row, int Column) CellAddress(string name)
    {
        TryCellAddress(name, out int row, out int column);
        if (row < 0 || column < 0)
        {
            // Word reads A0 as the name R0C1, which no bookmark has.
            throw Undefined(string.Create(CultureInfo.InvariantCulture, $"R{row + 1}C{column + 1}"));
        }

        return (row, column);
    }

    private static bool IsRowColumnName(string name)
    {
        return name.Length >= 4 && name[0] is 'R' or 'r' && char.IsAsciiDigit(name[1]) && TryCellAddress(name, out _, out _);
    }

    /// <summary>Zero-based row and column of an A1 or RnCn name; -1 for an index of zero.</summary>
    private static bool TryCellAddress(string name, out int row, out int column)
    {
        row = column = 0;
        if (name.Length >= 4 && name[0] is 'R' or 'r')
        {
            int c = name.IndexOfAny(['C', 'c'], 1);
            if (c > 1 && int.TryParse(name.AsSpan(1, c - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int r)
                && int.TryParse(name.AsSpan(c + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int col))
            {
                (row, column) = (r - 1, col - 1);
                return true;
            }
        }

        int letters = 0;
        while (letters < name.Length && char.IsAsciiLetter(name[letters]))
        {
            letters++;
        }

        if (letters is < 1 or > 2 || letters == name.Length || !int.TryParse(name.AsSpan(letters), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return false;
        }

        column = ColumnOf(name[..letters]) ?? 0;
        row = number - 1;
        return true;
    }

    private static int? ColumnOf(string letters)
    {
        if (letters.Length is < 1 or > 2)
        {
            return null;
        }

        int column = 0;
        foreach (char c in letters)
        {
            if (!char.IsAsciiLetter(c))
            {
                return null;
            }

            column = (column * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return column - 1;
    }

    private static int? RowOf(string digits)
    {
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int row))
        {
            return null;
        }

        return row == 0 ? throw new FormulaException("!Table Index Cannot be Zero") : row - 1;
    }

    private readonly record struct Token(TokenKind Kind, string Text, double Value);

    private abstract record Expr;

    private sealed record Number(double Value) : Expr;

    private sealed record Negate(Expr Operand) : Expr;

    private sealed record Percent(Expr Operand) : Expr;

    private sealed record Binary(string Op, Expr Left, Expr Right) : Expr;

    private sealed record Call(string Name, List<Expr> Arguments) : Expr;

    private sealed record Reference(string Name, string? Table) : Expr;

    private sealed record Range(string First, string Last, string? Table) : Expr;

    private sealed class FormulaException(string message) : Exception(message);
}
