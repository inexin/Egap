using System.Globalization;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// Reads Excel's formula grammar in its stored form (English function names, commas between arguments, semicolons between
/// array rows) into a <see cref="ParsedFormula"/>: A1 or R1C1 references, sheet, 3D and external prefixes, structured
/// references, array constants, the reference operators (<c>:</c>, the space of an intersection, the comma of a union in
/// parentheses), <c>@</c> and the spill operator <c>#</c>. White space is kept, so the formula prints back as written.
/// </summary>
/// <remarks>
/// Precedence, highest first, as in Excel: <c>:</c>, intersection, union; <c>-</c>, <c>+</c> and <c>@</c> before an operand;
/// <c>%</c>; <c>^</c>; <c>*</c> and <c>/</c>; <c>+</c> and <c>-</c>; <c>&amp;</c>; the comparisons. Every binary operator
/// groups to the left, so <c>2^3^2</c> is 64 and <c>-2^2</c> is 4.
/// </remarks>
internal sealed class FormulaParser
{
    /// <summary>How deeply expressions may nest before the parser refuses the formula, well above Excel's 64 levels.</summary>
    private const int MaxDepth = 256;

    /// <summary>The most arguments a function takes in Excel.</summary>
    private const int MaxArguments = 255;

    private readonly string text;
    private readonly bool r1c1;
    private readonly CellAddress origin;
    private int pos;
    private int depth;

    private FormulaParser(string text, bool r1c1, CellAddress origin)
    {
        this.text = text;
        this.r1c1 = r1c1;
        this.origin = origin;
    }

    /// <summary>Parses a formula without its leading <c>=</c>.</summary>
    /// <param name="text">The formula.</param>
    /// <param name="r1c1">Whether references are written in R1C1 style.</param>
    /// <param name="origin">The cell the formula is for; relative R1C1 references count from it.</param>
    /// <exception cref="FormulaException">The text is not a formula.</exception>
    public static ParsedFormula Parse(string text, bool r1c1, CellAddress origin)
    {
        FormulaParser parser = new(text, r1c1, origin);
        FormulaNode root = parser.Expression();
        string trail = parser.Space();
        if (parser.pos < text.Length)
        {
            throw parser.Error(text[parser.pos] == ',' ? "A comma outside a function or parentheses." : $"Unexpected '{text[parser.pos]}'.");
        }

        return new ParsedFormula(root, trail, origin);
    }

    /// <summary>Parses a formula, or gives null when it is not one.</summary>
    public static ParsedFormula? TryParse(string text, bool r1c1, CellAddress origin)
    {
        try
        {
            return Parse(text, r1c1, origin);
        }
        catch (FormulaException)
        {
            return null;
        }
    }

    private FormulaException Error(string message) => new(message, text, Math.Min(pos, text.Length));

    private char Peek(int offset = 0) => pos + offset < text.Length ? text[pos + offset] : '\0';

    private static bool IsSpace(char c) => c is ' ' or '\n' or '\r';

    private string Space()
    {
        int start = pos;
        while (pos < text.Length && IsSpace(text[pos]))
        {
            pos++;
        }

        return pos == start ? "" : text[start..pos];
    }

    private FormulaNode Expression()
    {
        if (++depth > MaxDepth)
        {
            throw Error("The formula nests too deeply.");
        }

        FormulaNode node = Comparison();
        depth--;
        return node;
    }

    private FormulaNode Comparison()
    {
        FormulaNode left = Concatenation();
        while (true)
        {
            int save = pos;
            string space = Space();
            (BinaryOperator op, string written) = Peek() switch
            {
                '=' => (BinaryOperator.Equal, "="),
                '<' when Peek(1) == '=' => (BinaryOperator.LessOrEqual, "<="),
                '<' when Peek(1) == '>' => (BinaryOperator.NotEqual, "<>"),
                '<' => (BinaryOperator.Less, "<"),
                '>' when Peek(1) == '=' => (BinaryOperator.GreaterOrEqual, ">="),
                '>' => (BinaryOperator.Greater, ">"),
                _ => ((BinaryOperator)255, ""),
            };
            if (written.Length == 0)
            {
                pos = save;
                return left;
            }

            pos += written.Length;
            left = new BinaryNode(op, written, space, left, Concatenation());
        }
    }

    private FormulaNode Concatenation()
    {
        FormulaNode left = Additive();
        while (true)
        {
            int save = pos;
            string space = Space();
            if (Peek() != '&')
            {
                pos = save;
                return left;
            }

            pos++;
            left = new BinaryNode(BinaryOperator.Concatenate, "&", space, left, Additive());
        }
    }

    private FormulaNode Additive()
    {
        FormulaNode left = Multiplicative();
        while (true)
        {
            int save = pos;
            string space = Space();
            char c = Peek();
            if (c is not ('+' or '-'))
            {
                pos = save;
                return left;
            }

            pos++;
            left = new BinaryNode(c == '+' ? BinaryOperator.Add : BinaryOperator.Subtract, c.ToString(), space, left, Multiplicative());
        }
    }

    private FormulaNode Multiplicative()
    {
        FormulaNode left = Power();
        while (true)
        {
            int save = pos;
            string space = Space();
            char c = Peek();
            if (c is not ('*' or '/'))
            {
                pos = save;
                return left;
            }

            pos++;
            left = new BinaryNode(c == '*' ? BinaryOperator.Multiply : BinaryOperator.Divide, c.ToString(), space, left, Power());
        }
    }

    private FormulaNode Power()
    {
        FormulaNode left = Percent();
        while (true)
        {
            int save = pos;
            string space = Space();
            if (Peek() != '^')
            {
                pos = save;
                return left;
            }

            pos++;
            left = new BinaryNode(BinaryOperator.Power, "^", space, left, Percent());
        }
    }

    private FormulaNode Percent()
    {
        FormulaNode node = Unary();
        while (true)
        {
            int save = pos;
            string space = Space();
            if (Peek() != '%')
            {
                pos = save;
                return node;
            }

            pos++;
            node = new PostfixNode(node, '%', space);
        }
    }

    private FormulaNode Unary()
    {
        string space = Space();
        char c = Peek();
        if (c is '-' or '+' or '@')
        {
            pos++;
            if (++depth > MaxDepth)
            {
                throw Error("The formula nests too deeply.");
            }

            FormulaNode operand = Unary();
            depth--;
            return new UnaryNode(c, operand) { Lead = space };
        }

        FormulaNode node = Intersection();
        node.Lead = space;
        return node;
    }

    private FormulaNode Intersection()
    {
        FormulaNode left = RangeOperator();
        while (true)
        {
            int save = pos;
            string space = Space();
            if (space.Length == 0 || !StartsReferenceOperand())
            {
                pos = save;
                return left;
            }

            left = new BinaryNode(BinaryOperator.Intersection, space, "", left, RangeOperator());
        }
    }

    /// <summary>Whether what follows white space is an operand an intersection can take, not an operator.</summary>
    private bool StartsReferenceOperand()
    {
        char c = Peek();
        return char.IsLetter(c) || c is '_' or '\\' or '$' or '\'' or '(' or '[' || (char.IsAsciiDigit(c) && RowRangeLength(pos) > 0);
    }

    private FormulaNode RangeOperator()
    {
        FormulaNode left = Spill();
        while (Peek() == ':')
        {
            pos++;
            left = new BinaryNode(BinaryOperator.Range, ":", "", left, Spill());
        }

        return left;
    }

    private FormulaNode Spill()
    {
        FormulaNode node = Primary();
        if (Peek() == '#' && node is ReferenceNode or NameNode or FunctionNode or StructuredNode or ParenthesesNode)
        {
            pos++;
            node = new PostfixNode(node, '#', "");
        }

        return node;
    }

    private FormulaNode Primary()
    {
        if (pos >= text.Length)
        {
            throw Error("An operand is missing at the end of the formula.");
        }

        char c = text[pos];
        switch (c)
        {
            case '(':
                return Parentheses();
            case '"':
                return StringConstant();
            case '{':
                return ArrayConstant();
            case '#':
                return ErrorConstant();
            case '\'':
                return AfterPrefix(QuotedPrefix());
            case '[':
                return Bracket();
        }

        if (char.IsAsciiDigit(c) || c == '.')
        {
            if (!r1c1 && RowRangeLength(pos) is > 0 and int length)
            {
                return RowRange(null, length);
            }

            return NumberConstant();
        }

        if (char.IsLetter(c) || c is '_' or '\\' or '$')
        {
            return Word();
        }

        throw Error(c is ')' or ',' or ';' ? "An operand is missing." : $"Unexpected '{c}'.");
    }

    private ParenthesesNode Parentheses()
    {
        pos++;
        List<Argument> items = [];
        while (true)
        {
            FormulaNode item = Expression();
            string trail = Space();
            items.Add(new Argument(item, trail));
            char c = Peek();
            pos++;
            if (c == ')')
            {
                return new ParenthesesNode([.. items]);
            }

            if (c != ',')
            {
                pos--;
                throw Error(c == '\0' ? "A closing parenthesis is missing." : $"Unexpected '{c}' in parentheses.");
            }
        }
    }

    private StringNode StringConstant()
    {
        int start = pos++;
        System.Text.StringBuilder? value = null;
        int runStart = pos;
        while (true)
        {
            if (pos >= text.Length)
            {
                pos = start;
                throw Error("A string is not closed.");
            }

            if (text[pos] == '"')
            {
                if (Peek(1) == '"')
                {
                    (value ??= new()).Append(text, runStart, pos + 1 - runStart);
                    pos += 2;
                    runStart = pos;
                    continue;
                }

                string end = text[runStart..pos];
                pos++;
                return new StringNode(text[start..pos], value is null ? end : value.Append(end).ToString());
            }

            pos++;
        }
    }

    private ErrorNode ErrorConstant()
    {
        if (MatchError(pos) is not (int length, CellError error))
        {
            throw Error("Not an error value.");
        }

        string written = text.Substring(pos, length);
        pos += length;
        return new ErrorNode(written, error);
    }

    /// <summary>The length and kind of the error value written at <paramref name="at"/>, or null when there is none.</summary>
    private (int Length, CellError Error)? MatchError(int at)
    {
        (int Length, CellError Error)? best = null;
        for (CellError error = CellError.Null; error <= CellError.Calc; error++)
        {
            string name = CellValue.ErrorText(error);
            if (string.Compare(text, at, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && at + name.Length <= text.Length && (best is null || name.Length > best.Value.Length))
            {
                best = (name.Length, error);
            }
        }

        return best;
    }

    private NumberNode NumberConstant()
    {
        int start = pos;
        int digits = 0;
        while (char.IsAsciiDigit(Peek()))
        {
            pos++;
            digits++;
        }

        if (Peek() == '.')
        {
            pos++;
            while (char.IsAsciiDigit(Peek()))
            {
                pos++;
                digits++;
            }
        }

        if (digits == 0)
        {
            pos = start;
            throw Error("Not a number.");
        }

        if (Peek() is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
        {
            pos += char.IsAsciiDigit(Peek(1)) ? 1 : 2;
            while (char.IsAsciiDigit(Peek()))
            {
                pos++;
            }
        }

        string written = text[start..pos];
        double value = double.Parse(written, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value))
        {
            pos = start;
            throw Error("The number is too large.");
        }

        return new NumberNode(written, value);
    }

    private ArrayNode ArrayConstant()
    {
        int start = pos++;
        List<CellValue> values = [];
        int columns = -1, rowColumns = 0, rows = 1;
        while (true)
        {
            Space();
            values.Add(ArrayItem());
            rowColumns++;
            Space();
            char c = Peek();
            pos++;
            if (c == ',')
            {
                continue;
            }

            if (c is ';' or '}')
            {
                if (columns >= 0 && rowColumns != columns)
                {
                    pos--;
                    throw Error("The rows of an array have different lengths.");
                }

                columns = rowColumns;
                rowColumns = 0;
                if (c == '}')
                {
                    return new ArrayNode(text[start..pos], rows, columns, [.. values]);
                }

                rows++;
                continue;
            }

            pos--;
            throw Error(c == '\0' ? "An array is not closed." : $"Unexpected '{c}' in an array.");
        }
    }

    private CellValue ArrayItem()
    {
        char c = Peek();
        if (c == '"')
        {
            return CellValue.TextUnchecked(StringConstant().Value);
        }

        if (c == '#')
        {
            return CellValue.FromError(ErrorConstant().Error);
        }

        if (c is '-' or '+' || char.IsAsciiDigit(c) || c == '.')
        {
            bool negative = c == '-';
            if (c is '-' or '+')
            {
                pos++;
            }

            double number = NumberConstant().Value;
            return CellValue.NumberUnchecked(negative && number != 0 ? -number : number);
        }

        int start = pos;
        while (char.IsLetter(Peek()))
        {
            pos++;
        }

        ReadOnlySpan<char> word = text.AsSpan(start, pos - start);
        if (word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || word.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return CellValue.FromBoolean(word.Length == 4);
        }

        pos = start;
        throw Error("An array holds only numbers, text, TRUE, FALSE and errors.");
    }

    /// <summary>Reads <c>'Sheet name'!</c>, <c>'[1]Sheet'!</c> or <c>'Sheet1:Sheet3'!</c>, the <c>!</c> included.</summary>
    private SheetPrefix QuotedPrefix()
    {
        int start = pos++;
        System.Text.StringBuilder content = new();
        while (true)
        {
            if (pos >= text.Length)
            {
                pos = start;
                throw Error("A quoted sheet name is not closed.");
            }

            char c = text[pos++];
            if (c == '\'')
            {
                if (Peek() == '\'')
                {
                    content.Append('\'');
                    pos++;
                    continue;
                }

                break;
            }

            content.Append(c);
        }

        if (Peek() != '!')
        {
            throw Error("A quoted sheet name must be followed by '!'.");
        }

        pos++;
        string inner = content.ToString();
        string? book = null;
        if (inner.StartsWith('['))
        {
            int close = inner.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw Error("A book name is not closed.");
            }

            book = inner[1..close];
            inner = inner[(close + 1)..];
        }

        if (inner.Length == 0 && book is null)
        {
            throw Error("A sheet name is empty.");
        }

        int colon = inner.IndexOf(':', StringComparison.Ordinal);
        string? first = inner.Length == 0 ? null : colon < 0 ? inner : inner[..colon];
        string? last = inner.Length == 0 ? null : colon < 0 ? inner : inner[(colon + 1)..];
        if (first is { Length: 0 } || last is { Length: 0 })
        {
            throw Error("A sheet name is empty.");
        }

        return new SheetPrefix(text[start..pos], book, first, last);
    }

    /// <summary>
    /// A <c>[</c>: an external book (<c>[1]Sheet1!A1</c>, <c>[1]!Name</c>) when a sheet name or <c>!</c> follows the
    /// closing bracket, otherwise a structured reference inside a table (<c>[@Amount]</c>).
    /// </summary>
    private FormulaNode Bracket()
    {
        int start = pos;
        int end = BracketEnd(pos);
        char next = end < text.Length ? text[end] : '\0';
        if (next == '!' || char.IsLetter(next) || next is '_' or '\\')
        {
            string book = text[(start + 1)..(end - 1)];
            if (book.Contains('[', StringComparison.Ordinal))
            {
                throw Error("A book name holds a '['.");
            }

            pos = end;
            if (next == '!')
            {
                pos++;
                return AfterPrefix(new SheetPrefix(text[start..pos], book, null, null));
            }

            string first = SheetWord();
            string last = first;
            if (Peek() == ':')
            {
                pos++;
                last = SheetWord();
            }

            if (Peek() != '!')
            {
                throw Error("A sheet name must be followed by '!'.");
            }

            pos++;
            return AfterPrefix(new SheetPrefix(text[start..pos], book, first, last));
        }

        return Structured(null, start);
    }

    private string SheetWord()
    {
        int start = pos;
        while (IsWordChar(Peek()))
        {
            pos++;
        }

        return pos > start ? text[start..pos] : throw Error("A sheet name is missing.");
    }

    /// <summary>The index after the <c>]</c> that closes the <c>[</c> at <paramref name="at"/>; a quote escapes the next character.</summary>
    private int BracketEnd(int at)
    {
        int level = 0;
        for (int i = at; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'')
            {
                i++;
            }
            else if (c == '[')
            {
                level++;
            }
            else if (c == ']' && --level == 0)
            {
                return i + 1;
            }
        }

        pos = at;
        throw Error("A '[' is not closed.");
    }

    private StructuredNode Structured(string? table, int start)
    {
        int open = pos;
        int end = BracketEnd(open);
        string inner = text[(open + 1)..(end - 1)];
        pos = open;
        (TableItems items, string? first, string? last) = TableSpecification(inner, open + 1);
        pos = end;
        return new StructuredNode(text[start..end], table, items, first, last);
    }

    /// <summary>Reads what is inside a structured reference's outer brackets.</summary>
    private (TableItems Items, string? First, string? Last) TableSpecification(string inner, int at)
    {
        if (inner.Length == 0)
        {
            return (TableItems.None, null, null);
        }

        if (inner[0] == '@')
        {
            string rest = inner[1..];
            if (rest.Length == 0)
            {
                return (TableItems.ThisRow, null, null);
            }

            if (rest[0] != '[')
            {
                return (TableItems.ThisRow, Unescape(rest, at), null);
            }

            (TableItems more, string? first, string? last) = ItemList(rest, at + 1);
            return more == TableItems.None ? (TableItems.ThisRow, first, last) : throw ErrorAt(at, "'@' takes only columns.");
        }

        if (inner[0] == '[')
        {
            return ItemList(inner, at);
        }

        if (inner[0] == '#')
        {
            return (SpecialItem(inner, at), null, null);
        }

        return (TableItems.None, Unescape(inner, at), null);
    }

    private FormulaException ErrorAt(int at, string message)
    {
        pos = at;
        return Error(message);
    }

    /// <summary>Reads <c>[#Headers],[Region]:[Amount]</c>: special items and at most one column or column range.</summary>
    private (TableItems Items, string? First, string? Last) ItemList(string list, int at)
    {
        TableItems items = TableItems.None;
        string? first = null, last = null;
        int i = 0;
        while (true)
        {
            while (i < list.Length && list[i] == ' ')
            {
                i++;
            }

            int itemStart = at + i;
            (string item, i) = Item(list, i, at);
            if (item.StartsWith('#'))
            {
                TableItems special = SpecialItem(item, itemStart + 1);
                if ((items & special) != 0)
                {
                    throw ErrorAt(itemStart, "A table item is named twice.");
                }

                items |= special;
            }
            else
            {
                if (first is not null)
                {
                    throw ErrorAt(itemStart, "A structured reference names one column or one range of columns.");
                }

                first = Unescape(item, itemStart + 1);
                if (i < list.Length && list[i] == ':')
                {
                    (string second, i) = Item(list, i + 1, at);
                    last = Unescape(second, at + i);
                }
            }

            while (i < list.Length && list[i] == ' ')
            {
                i++;
            }

            if (i == list.Length)
            {
                return (items, first, last);
            }

            if (list[i] != ',')
            {
                throw ErrorAt(at + i, "Table items are separated by commas.");
            }

            i++;
        }
    }

    /// <summary>Reads <c>[name]</c> at <paramref name="i"/>; gives the name, still escaped, and the index after it.</summary>
    private (string Item, int Next) Item(string list, int i, int at)
    {
        if (i >= list.Length || list[i] != '[')
        {
            throw ErrorAt(at + i, "A table item must be in brackets.");
        }

        for (int j = i + 1; j < list.Length; j++)
        {
            if (list[j] == '\'')
            {
                j++;
            }
            else if (list[j] == '[')
            {
                break;
            }
            else if (list[j] == ']')
            {
                return (list[(i + 1)..j], j + 1);
            }
        }

        throw ErrorAt(at + i, "A table item is not closed.");
    }

    private TableItems SpecialItem(string item, int at) => item.ToUpperInvariant() switch
    {
        "#ALL" => TableItems.All,
        "#DATA" => TableItems.Data,
        "#HEADERS" => TableItems.Headers,
        "#TOTALS" => TableItems.Totals,
        "#THIS ROW" => TableItems.ThisRow,
        _ => throw ErrorAt(at, $"'{item}' is not a table item."),
    };

    private string Unescape(string name, int at)
    {
        if (name.Length == 0)
        {
            throw ErrorAt(at, "A column name is empty.");
        }

        if (!name.Contains('\'', StringComparison.Ordinal))
        {
            return name;
        }

        System.Text.StringBuilder plain = new(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '\'' && i + 1 < name.Length)
            {
                i++;
            }

            plain.Append(name[i]);
        }

        return plain.ToString();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '\\' or '?' or '$';

    /// <summary>A word: a cell, column or row reference, a name, a function, a table, a sheet, TRUE or FALSE.</summary>
    private FormulaNode Word()
    {
        int start = pos;
        if (r1c1 && R1C1Reference(null) is { } r1c1Reference)
        {
            return r1c1Reference;
        }

        while (IsWordChar(Peek()))
        {
            pos++;
        }

        string word = text[start..pos];
        switch (Peek())
        {
            case '!':
                pos++;
                return AfterPrefix(new SheetPrefix(text[start..pos], null, word, word));
            case ':' when !word.Contains('$', StringComparison.Ordinal) && !LooksLikeR1C1(word) && !TryCell(word, out _, out _, out _, out _):
                // Sheet1:Sheet3! is a 3D prefix; A1:Sheet2!B2 is a range from a cell, as a sheet named like a cell needs quotes.
                int save = pos;
                pos++;
                int second = pos;
                while (IsWordChar(Peek()))
                {
                    pos++;
                }

                if (pos > second && Peek() == '!')
                {
                    pos++;
                    return AfterPrefix(new SheetPrefix(text[start..pos], null, word, text[second..(pos - 1)]));
                }

                pos = save;
                break;
            case '(':
                CheckName(word, start, "function");
                return Function(word, start);
            case '[':
                CheckName(word, start, "table");
                if (LooksLikeR1C1(word) || TryCell(word, out _, out _, out _, out _))
                {
                    pos = start;
                    throw Error($"'{word}' is a cell reference, not a table.");
                }

                return Structured(word, start);
        }

        if (!r1c1 && A1Reference(null, word, start) is { } reference)
        {
            return reference;
        }

        if (word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || word.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return new BooleanNode(word, word.Length == 4);
        }

        CheckNameNotReference(word, start);
        CheckName(word, start, "name");
        return new NameNode(null, word);
    }

    /// <summary>
    /// Refuses a name that has the shape of a reference: an R1C1 one (<c>RC</c>, <c>R0C1</c>) in either style, and an A1
    /// cell in R1C1 style. Excel allows no such names, and one would read as a reference once the formula is written in
    /// the other style.
    /// </summary>
    private void CheckNameNotReference(string word, int start)
    {
        if (LooksLikeR1C1(word))
        {
            pos = start;
            throw Error(r1c1 ? $"'{word}' is not a cell on the sheet." : $"'{word}' is an R1C1 reference, not a name.");
        }

        if (r1c1 && TryCell(word, out _, out _, out _, out _))
        {
            pos = start;
            throw Error($"'{word}' is an A1 reference, not a name.");
        }
    }

    /// <summary>Whether a word has the shape of an R1C1 reference (<c>RC</c>, <c>R0C1</c>, <c>C99999</c>).</summary>
    internal static bool LooksLikeR1C1(ReadOnlySpan<char> word)
    {
        int i = 0;
        bool any = false;
        if (i < word.Length && word[i] is 'R' or 'r')
        {
            any = true;
            i++;
            while (i < word.Length && char.IsAsciiDigit(word[i]))
            {
                i++;
            }
        }

        if (i < word.Length && word[i] is 'C' or 'c')
        {
            any = true;
            i++;
            while (i < word.Length && char.IsAsciiDigit(word[i]))
            {
                i++;
            }
        }

        return any && i == word.Length;
    }

    private void CheckName(string word, int start, string what)
    {
        if (word.Length == 0 || char.IsAsciiDigit(word[0]) || word[0] is '.' or '?' || word.Contains('$', StringComparison.Ordinal))
        {
            pos = start;
            throw Error($"'{word}' is not a {what}.");
        }
    }

    private FunctionNode Function(string name, int start)
    {
        pos++;
        List<Argument> arguments = [];
        int save = pos;
        string space = Space();
        if (Peek() == ')')
        {
            pos++;
            // F( ) keeps its space as a lone missing argument, which the compiler reads as no arguments.
            return new FunctionNode(name, space.Length == 0 ? [] : [new Argument(new MissingNode { Lead = space }, "")]);
        }

        pos = save;
        while (true)
        {
            save = pos;
            space = Space();
            FormulaNode argument;
            if (Peek() is ',' or ')')
            {
                argument = new MissingNode { Lead = space };
            }
            else
            {
                pos = save;
                argument = Expression();
            }

            string trail = Space();
            arguments.Add(new Argument(argument, trail));
            if (arguments.Count > MaxArguments)
            {
                throw Error($"A function takes at most {MaxArguments} arguments.");
            }

            char c = Peek();
            pos++;
            if (c == ')')
            {
                return new FunctionNode(name, [.. arguments]);
            }

            if (c != ',')
            {
                pos--;
                throw Error(c == '\0' ? $"The parentheses of {name} are not closed." : $"Unexpected '{c}' in the arguments of {name}.");
            }
        }
    }

    /// <summary>What may follow a prefix: a reference, <c>#REF!</c> or a name.</summary>
    private FormulaNode AfterPrefix(SheetPrefix prefix)
    {
        int start = pos;
        char c = Peek();
        if (c == '#')
        {
            if (MatchError(pos) is (int length, CellError.Reference))
            {
                pos += length;
                return new ReferenceNode(prefix, text[start..pos], new Area(AreaKind.Error, 0, 0, 0, 0, AreaFlags.None));
            }

            throw Error("Only #REF! may follow a sheet name.");
        }

        // A DDE item can be any text, so it is quoted: [1]!'A b'. It stays as written, quotes and all.
        if (c == '\'' && prefix.Book is not null && prefix.FirstSheet is null)
        {
            pos++;
            while (pos < text.Length && (text[pos] != '\'' || (pos + 1 < text.Length && text[pos + 1] == '\'')))
            {
                pos += text[pos] == '\'' ? 2 : 1;
            }

            if (pos >= text.Length)
            {
                throw Error("A quoted name must end with a quote.");
            }

            pos++;
            return new NameNode(prefix, text[start..pos]);
        }

        if (r1c1)
        {
            if (R1C1Reference(prefix) is { } reference)
            {
                return reference;
            }
        }
        else if (RowRangeLength(pos) is > 0 and int length)
        {
            return RowRange(prefix, length);
        }

        while (IsWordChar(Peek()))
        {
            pos++;
        }

        string word = text[start..pos];
        if (!r1c1 && A1Reference(prefix, word, start) is { } a1)
        {
            return a1;
        }

        if (Peek() == '(' && prefix.Book is not null && prefix.FirstSheet is null)
        {
            CheckName(word, start, "function");
            return Function(prefix.Text + word, start);
        }

        CheckNameNotReference(word, start);
        CheckName(word, start, "name");
        return new NameNode(prefix, word);
    }

    /// <summary>
    /// A cell (<c>$B$4</c>), a range of cells (<c>A1:B2</c>), or a range of columns (<c>A:C</c>) or rows (<c>$1:$3</c>) for a
    /// word just read; null when the word is not one.
    /// </summary>
    private ReferenceNode? A1Reference(SheetPrefix? prefix, string word, int start)
    {
        if (TryCell(word, out int row, out int column, out bool rowAbsolute, out bool columnAbsolute))
        {
            AreaFlags flags = (rowAbsolute ? AreaFlags.FirstRowAbsolute : 0) | (columnAbsolute ? AreaFlags.FirstColumnAbsolute : 0);
            if (Peek() == ':' && SecondWord() is (string second, int end)
                && TryCell(second, out int lastRow, out int lastColumn, out bool lastRowAbsolute, out bool lastColumnAbsolute))
            {
                pos = end;
                flags |= (lastRowAbsolute ? AreaFlags.LastRowAbsolute : 0) | (lastColumnAbsolute ? AreaFlags.LastColumnAbsolute : 0);
                return new ReferenceNode(prefix, text[start..pos], new Area(AreaKind.Range, row, column, lastRow, lastColumn, flags));
            }

            return new ReferenceNode(prefix, word, new Area(AreaKind.Cell, row, column, row, column, flags | (rowAbsolute ? AreaFlags.LastRowAbsolute : 0) | (columnAbsolute ? AreaFlags.LastColumnAbsolute : 0)));
        }

        if (TryColumn(word, out column, out columnAbsolute) && Peek() == ':' && SecondWord() is (string secondColumn, int columnEnd)
            && TryColumn(secondColumn, out int lastColumn2, out bool lastAbsolute))
        {
            pos = columnEnd;
            AreaFlags flags = (columnAbsolute ? AreaFlags.FirstColumnAbsolute : 0) | (lastAbsolute ? AreaFlags.LastColumnAbsolute : 0)
                | AreaFlags.FirstRowAbsolute | AreaFlags.LastRowAbsolute;
            return new ReferenceNode(prefix, text[start..pos], new Area(AreaKind.Columns, 0, column, CellAddress.MaxRows - 1, lastColumn2, flags));
        }

        if (word.StartsWith('$') && RowRangeLength(start) is > 0 and int rowLength)
        {
            pos = start;
            return RowRange(prefix, rowLength);
        }

        return null;
    }

    /// <summary>The word after the <c>:</c> at the current position, and the index after it; the position does not move.</summary>
    private (string Word, int End)? SecondWord()
    {
        int i = pos + 1;
        while (i < text.Length && IsWordChar(text[i]))
        {
            i++;
        }

        return i > pos + 1 ? (text[(pos + 1)..i], i) : null;
    }

    /// <summary>The length of a row range such as <c>1:3</c> or <c>$2:$2</c> at <paramref name="at"/>, or 0.</summary>
    private int RowRangeLength(int at)
    {
        int i = at;
        if (!RowNumber(ref i, out _, out _) || i >= text.Length || text[i] != ':')
        {
            return 0;
        }

        i++;
        if (!RowNumber(ref i, out _, out _) || (i < text.Length && (IsWordChar(text[i]) || text[i] is '(' or '[')))
        {
            return 0;
        }

        return i - at;
    }

    private bool RowNumber(ref int i, out int row, out bool absolute)
    {
        absolute = i < text.Length && text[i] == '$';
        int start = absolute ? i + 1 : i;
        int end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        if (end == start || !CellAddress.TryParseRowNumber(text.AsSpan(start, end - start), out row))
        {
            row = 0;
            return false;
        }

        row--;
        i = end;
        return true;
    }

    private ReferenceNode RowRange(SheetPrefix? prefix, int length)
    {
        int start = pos;
        int i = pos;
        RowNumber(ref i, out int first, out bool firstAbsolute);
        i++;
        RowNumber(ref i, out int last, out bool lastAbsolute);
        pos = start + length;
        AreaFlags flags = (firstAbsolute ? AreaFlags.FirstRowAbsolute : 0) | (lastAbsolute ? AreaFlags.LastRowAbsolute : 0)
            | AreaFlags.FirstColumnAbsolute | AreaFlags.LastColumnAbsolute;
        return new ReferenceNode(prefix, text[start..pos], new Area(AreaKind.Rows, first, 0, last, CellAddress.MaxColumns - 1, flags));
    }

    /// <summary>The zero-based column of one to three letters, or -1 past XFD.</summary>
    private static int ColumnIndex(ReadOnlySpan<char> letters)
    {
        int column = 0;
        foreach (char c in letters)
        {
            column = (column * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return column <= CellAddress.MaxColumns ? column - 1 : -1;
    }

    /// <summary>Reads <c>$B$4</c>-style text: an optional <c>$</c>, one to three letters, an optional <c>$</c>, a row number.</summary>
    internal static bool TryCell(ReadOnlySpan<char> word, out int row, out int column, out bool rowAbsolute, out bool columnAbsolute)
    {
        row = column = 0;
        rowAbsolute = false;
        columnAbsolute = word.Length > 0 && word[0] == '$';
        int i = columnAbsolute ? 1 : 0;
        int letters = i;
        while (i < word.Length && char.IsAsciiLetter(word[i]))
        {
            i++;
        }

        if (i == letters || i - letters > 3)
        {
            return false;
        }

        column = ColumnIndex(word[letters..i]);
        if (column < 0)
        {
            return false;
        }

        if (i < word.Length && word[i] == '$')
        {
            rowAbsolute = true;
            i++;
        }

        if (i == word.Length)
        {
            return false;
        }

        foreach (char c in word[i..])
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        if (!CellAddress.TryParseRowNumber(word[i..], out row))
        {
            return false;
        }

        row--;
        return true;
    }

    private static bool TryColumn(ReadOnlySpan<char> word, out int column, out bool absolute)
    {
        column = 0;
        absolute = word.Length > 0 && word[0] == '$';
        ReadOnlySpan<char> letters = absolute ? word[1..] : word;
        if (letters.Length is 0 or > 3)
        {
            return false;
        }

        foreach (char c in letters)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        column = ColumnIndex(letters);
        return column >= 0;
    }

    /// <summary>
    /// An R1C1 reference at the current position (<c>R1C1</c>, <c>R[-1]C</c>, <c>RC[2]</c>, rows <c>R2:R4</c>, columns
    /// <c>C[1]</c>), or null with the position unchanged when there is none.
    /// </summary>
    private ReferenceNode? R1C1Reference(SheetPrefix? prefix)
    {
        int start = pos;
        if (R1C1Part(pos) is not (int end, Area first))
        {
            return null;
        }

        Area area = first;
        if (end < text.Length && text[end] == ':' && R1C1Part(end + 1) is (int end2, Area last) && last.Kind == first.Kind)
        {
            end = end2;
            area = first with
            {
                Kind = first.Kind == AreaKind.Cell ? AreaKind.Range : first.Kind,
                LastRow = last.LastRow,
                LastColumn = last.LastColumn,
                Flags = (first.Flags & (AreaFlags.FirstRowAbsolute | AreaFlags.FirstColumnAbsolute))
                    | (last.Flags & (AreaFlags.LastRowAbsolute | AreaFlags.LastColumnAbsolute)),
            };
        }

        pos = end;
        return new ReferenceNode(prefix, text[start..pos], area);
    }

    private (int End, Area Area)? R1C1Part(int at)
    {
        int i = at;
        bool hasRow = false, hasColumn = false, rowAbsolute = false, columnAbsolute = false;
        int row = origin.Row, column = origin.Column;
        if (i < text.Length && text[i] is 'R' or 'r')
        {
            i++;
            hasRow = true;
            if (!R1C1Number(ref i, CellAddress.MaxRows, origin.Row, out row, out rowAbsolute))
            {
                return null;
            }
        }

        if (i < text.Length && text[i] is 'C' or 'c')
        {
            i++;
            hasColumn = true;
            if (!R1C1Number(ref i, CellAddress.MaxColumns, origin.Column, out column, out columnAbsolute))
            {
                return null;
            }
        }

        if (!hasRow && !hasColumn || (i < text.Length && (IsWordChar(text[i]) || text[i] is '(' or '[')))
        {
            return null;
        }

        if (hasRow && hasColumn)
        {
            AreaFlags flags = (rowAbsolute ? AreaFlags.FirstRowAbsolute | AreaFlags.LastRowAbsolute : 0)
                | (columnAbsolute ? AreaFlags.FirstColumnAbsolute | AreaFlags.LastColumnAbsolute : 0);
            return (i, new Area(AreaKind.Cell, row, column, row, column, flags));
        }

        if (hasRow)
        {
            AreaFlags flags = (rowAbsolute ? AreaFlags.FirstRowAbsolute | AreaFlags.LastRowAbsolute : 0) | AreaFlags.FirstColumnAbsolute | AreaFlags.LastColumnAbsolute;
            return (i, new Area(AreaKind.Rows, row, 0, row, CellAddress.MaxColumns - 1, flags));
        }

        AreaFlags columnFlags = (columnAbsolute ? AreaFlags.FirstColumnAbsolute | AreaFlags.LastColumnAbsolute : 0) | AreaFlags.FirstRowAbsolute | AreaFlags.LastRowAbsolute;
        return (i, new Area(AreaKind.Columns, 0, column, CellAddress.MaxRows - 1, column, columnFlags));
    }

    /// <summary>Reads the number after R or C: <c>5</c> (absolute, one-based), <c>[-2]</c> (relative) or nothing (this row or column).</summary>
    private bool R1C1Number(ref int i, int count, int from, out int value, out bool absolute)
    {
        absolute = false;
        value = from;
        if (i < text.Length && text[i] == '[')
        {
            int j = i + 1;
            bool negative = j < text.Length && text[j] == '-';
            if (j < text.Length && text[j] is '-' or '+')
            {
                j++;
            }

            int digitsStart = j;
            while (j < text.Length && char.IsAsciiDigit(text[j]))
            {
                j++;
            }

            if (j == digitsStart || j - digitsStart > 7 || j >= text.Length || text[j] != ']')
            {
                return false;
            }

            int offset = int.Parse(text.AsSpan(digitsStart, j - digitsStart), CultureInfo.InvariantCulture);
            if (offset >= count)
            {
                return false;
            }

            value = (((from + (negative ? -offset : offset)) % count) + count) % count;
            i = j + 1;
            return true;
        }

        int start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i == start)
        {
            return true;
        }

        if (i - start > 7 || !int.TryParse(text.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || number > count)
        {
            return false;
        }

        value = number - 1;
        absolute = true;
        return true;
    }
}
