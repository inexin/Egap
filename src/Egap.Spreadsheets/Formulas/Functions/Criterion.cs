using System.Globalization;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// A condition as COUNTIF, SUMIF and the other IF functions take it: a value to equal, or text that starts with a comparison
/// (<c>&gt;5</c>, <c>&lt;&gt;apple</c>, <c>=</c>). Text compares without case, and <c>*</c>, <c>?</c> and <c>~</c> are
/// wildcards when equality is asked for. A number in the text compares with numbers; an empty condition matches blank
/// cells.
/// </summary>
internal sealed class Criterion
{
    private enum Op
    {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
    }

    private enum Operand
    {
        Empty,
        Number,
        Text,
        Boolean,
        Error,
    }

    private readonly Op op;
    private readonly Operand kind;
    private readonly double number;
    private readonly string text = "";
    private readonly bool emptyText;
    private readonly bool wildcards;
    private readonly CultureInfo culture;

    private Criterion(Op op, Operand kind, double number, string text, bool emptyText, CultureInfo culture)
    {
        this.op = op;
        this.kind = kind;
        this.number = number;
        this.text = text;
        this.emptyText = emptyText;
        this.culture = culture;
        wildcards = kind == Operand.Text && Wildcard.Has(text);
    }

    /// <summary>Whether a blank cell meets the condition, so the cells nobody filled in count too.</summary>
    public bool MatchesBlank => Matches(EvalValue.Blank);

    /// <summary>Reads a condition from a function's argument.</summary>
    public static Criterion Parse(in EvalValue value, EvalContext context)
    {
        CultureInfo culture = context.Culture;
        switch (value.Kind)
        {
            case EvalKind.Number:
                return new Criterion(Op.Equal, Operand.Number, value.Number, "", false, culture);
            case EvalKind.Boolean:
                return new Criterion(Op.Equal, Operand.Boolean, value.Number, "", false, culture);
            case EvalKind.Error:
                return new Criterion(Op.Equal, Operand.Error, value.Number, "", false, culture);
            case EvalKind.Text:
                break;
            default:
                // A blank condition (an empty cell given as the condition) matches what equals 0... and blank cells do not.
                return new Criterion(Op.Equal, Operand.Number, 0, "", false, culture);
        }

        string source = value.Text;
        Op op = Op.Equal;
        int skip = 0;
        if (source.StartsWith("<=", StringComparison.Ordinal))
        {
            (op, skip) = (Op.LessOrEqual, 2);
        }
        else if (source.StartsWith(">=", StringComparison.Ordinal))
        {
            (op, skip) = (Op.GreaterOrEqual, 2);
        }
        else if (source.StartsWith("<>", StringComparison.Ordinal))
        {
            (op, skip) = (Op.NotEqual, 2);
        }
        else if (source.StartsWith('<'))
        {
            (op, skip) = (Op.Less, 1);
        }
        else if (source.StartsWith('>'))
        {
            (op, skip) = (Op.Greater, 1);
        }
        else if (source.StartsWith('='))
        {
            (op, skip) = (Op.Equal, 1);
        }

        string operand = source[skip..];
        if (operand.Length == 0)
        {
            // "" matches blank cells and empty text; "=" only blank cells; "<>" any cell that holds something.
            return new Criterion(op, Operand.Empty, 0, "", emptyText: skip == 0, culture);
        }

        if (ExcelValues.TryParse(operand, context, out double parsed))
        {
            return new Criterion(op, Operand.Number, parsed, operand, false, culture);
        }

        if (operand.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || operand.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return new Criterion(op, Operand.Boolean, operand.Length == 4 ? 1 : 0, operand, false, culture);
        }

        if (CellValue.TryParseError(operand, out CellError error))
        {
            return new Criterion(op, Operand.Error, (int)error, operand, false, culture);
        }

        return new Criterion(op, Operand.Text, 0, operand, false, culture);
    }

    /// <summary>Whether a cell's value meets the condition.</summary>
    public bool Matches(in EvalValue value)
    {
        if (kind == Operand.Empty)
        {
            bool empty = value.Kind is EvalKind.Blank or EvalKind.Missing || (emptyText && value.Kind == EvalKind.Text && value.Text.Length == 0);
            return op switch
            {
                Op.Equal => empty,
                Op.NotEqual => value.Kind is not (EvalKind.Blank or EvalKind.Missing),
                _ => false,
            };
        }

        int? order = Order(value);
        if (order is not int compared)
        {
            return op == Op.NotEqual;
        }

        return op switch
        {
            Op.Equal => compared == 0,
            Op.NotEqual => compared != 0,
            Op.Less => compared < 0,
            Op.LessOrEqual => compared <= 0,
            Op.Greater => compared > 0,
            _ => compared >= 0,
        };
    }

    /// <summary>How the value compares with the operand, or null when they are of kinds that do not compare.</summary>
    private int? Order(in EvalValue value)
    {
        switch (kind)
        {
            case Operand.Number:
                if (value.Kind == EvalKind.Number)
                {
                    return ExcelValues.CompareNumbers(value.Number, number);
                }

                if (value.Kind == EvalKind.Text && op is Op.Equal or Op.NotEqual && double.TryParse(value.Text, NumberStyles.Float, culture, out double inText))
                {
                    return ExcelValues.CompareNumbers(inText, number);
                }

                return null;
            case Operand.Boolean:
                return value.Kind == EvalKind.Boolean ? value.Number.CompareTo(number) : null;
            case Operand.Error:
                return value.Kind == EvalKind.Error ? (value.Number == number ? 0 : 1) : null;
            default:
                if (value.Kind != EvalKind.Text)
                {
                    return null;
                }

                if (op is Op.Equal or Op.NotEqual)
                {
                    return (wildcards ? Wildcard.IsMatch(text, value.Text) : string.Equals(text, value.Text, StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
                }

                return Math.Sign(culture.CompareInfo.Compare(value.Text, text, CompareOptions.IgnoreCase));
        }
    }
}

/// <summary>Excel's wildcards: <c>*</c> for any run of characters, <c>?</c> for one, and <c>~</c> before either to mean itself.</summary>
internal static class Wildcard
{
    /// <summary>Whether text has a wildcard in it.</summary>
    public static bool Has(string pattern) => pattern.AsSpan().IndexOfAny('*', '?', '~') >= 0;

    /// <summary>Whether the whole of <paramref name="text"/> matches the pattern, without case.</summary>
    public static bool IsMatch(string pattern, string text) => Match(pattern, text, 0) == text.Length;

    /// <summary>
    /// Finds the pattern in text from a start, as SEARCH does: the index of the first match and its length, or -1 when there
    /// is none.
    /// </summary>
    public static (int Index, int Length) Find(string pattern, string text, int start)
    {
        for (int i = start; i <= text.Length; i++)
        {
            int end = Match(pattern, text, i, anchored: false);
            if (end >= 0)
            {
                return (i, end - i);
            }
        }

        return (-1, 0);
    }

    /// <summary>
    /// Matches the pattern against text from a position: where the match ends (the whole text when anchored), or -1. A
    /// <c>*</c> takes as little as it can when not anchored, as SEARCH finds the shortest match.
    /// </summary>
    private static int Match(string pattern, string text, int from, bool anchored = true)
    {
        // Backtracking over the last star, the usual linear-time method for * and ?.
        int p = 0, t = from, starP = -1, starT = -1;
        while (true)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                starP = ++p;
                starT = t;
                if (!anchored && p == pattern.Length)
                {
                    return t;
                }

                continue;
            }

            if (p == pattern.Length && (!anchored || t == text.Length))
            {
                return t;
            }

            if (p < pattern.Length && t < text.Length && Same(pattern, ref p, text[t]))
            {
                t++;
                continue;
            }

            if (starP >= 0 && starT < text.Length)
            {
                p = starP;
                t = ++starT;
                continue;
            }

            return -1;
        }
    }

    /// <summary>Whether the pattern's next character matches one of the text's, stepping past it (and past a <c>~</c>).</summary>
    private static bool Same(string pattern, ref int p, char c)
    {
        char wanted = pattern[p];
        int step = 1;
        if (wanted == '~' && p + 1 < pattern.Length && pattern[p + 1] is '*' or '?' or '~')
        {
            wanted = pattern[p + 1];
            step = 2;
        }
        else if (wanted == '?')
        {
            p++;
            return true;
        }

        if (char.ToUpperInvariant(wanted) != char.ToUpperInvariant(c))
        {
            return false;
        }

        p += step;
        return true;
    }
}
