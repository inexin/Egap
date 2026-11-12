using System.Text;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// Writes a math tree in UnicodeMath, the linear format of Unicode Technical Note #28 that Word types and reads back:
/// <c>x=(−b±√(b^2−4ac))/(2a)</c>. It is the equation's text for alternative text, accessibility and plain-text output.
/// </summary>
internal static class UnicodeMath
{
    private const int MaxDepth = 64;

    /// <summary>The UnicodeMath text of <paramref name="node"/>; a display paragraph's equations go one per line.</summary>
    public static string Write(MathNode node)
    {
        StringBuilder text = new();
        Node(text, node, 0);
        return text.ToString();
    }

    private static void Node(StringBuilder text, MathNode node, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (node)
        {
            case MathParagraph paragraph:
                bool first = true;
                foreach (MathNode item in paragraph.Items)
                {
                    if (item is MathZone)
                    {
                        if (!first)
                        {
                            text.Append('\n');
                        }

                        first = false;
                        Node(text, item, depth + 1);
                    }
                }

                break;
            case MathZone zone:
                Row(text, zone.Content, depth + 1);
                break;
            case MathRow row:
                Row(text, row, depth + 1);
                break;
            case MathRun run:
                Run(text, run);
                break;
            case MathObject obj:
                Object(text, obj, depth + 1);
                break;
        }
    }

    private static void Row(StringBuilder text, MathRow row, int depth)
    {
        foreach (MathNode item in row.Items)
        {
            Node(text, item, depth);
        }
    }

    private static void Run(StringBuilder text, MathRun run)
    {
        string value = run.Text;
        if (run.Normal && value.Length > 0)
        {
            text.Append('"').Append(value.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
            return;
        }

        foreach (char c in value)
        {
            text.Append(c == '-' ? '\u2212' : c);
        }
    }

    /// <summary>An argument as an operand: bare when it is one character or one object, in parentheses otherwise.</summary>
    private static void Operand(StringBuilder text, MathRow row, int depth)
    {
        StringBuilder inner = new();
        Row(inner, row, depth);
        // A word or a number (lim, 12) is one operand too: UTN #28 ends an operand at the first operator.
        bool single = row.Items.Count == 1 && row.Items[0] is MathObject
            || inner.Length == 1
            || inner.Length == 2 && char.IsSurrogatePair(inner[0], inner[1])
            || inner.Length > 0 && (inner.ToString().All(char.IsLetter) || inner.ToString().All(char.IsDigit));
        if (single)
        {
            text.Append(inner);
        }
        else
        {
            text.Append('(').Append(inner).Append(')');
        }
    }

    private static void Object(StringBuilder text, MathObject o, int depth)
    {
        MathProperties? p = o.Properties;
        switch (o.Kind)
        {
            case MathKind.Fraction:
                {
                    string type = p?.Get("type") ?? "bar";
                    Operand(text, o.Argument("num"), depth);
                    text.Append(type switch
                    {
                        "lin" => '\u2215',
                        "skw" => '\u2298',
                        "noBar" => '\u00A6',
                        _ => '/',
                    });
                    Operand(text, o.Argument("den"), depth);
                    break;
                }

            case MathKind.Subscript:
                Operand(text, o.Argument("e"), depth);
                text.Append('_');
                Operand(text, o.Argument("sub"), depth);
                break;
            case MathKind.Superscript:
                Operand(text, o.Argument("e"), depth);
                text.Append('^');
                Operand(text, o.Argument("sup"), depth);
                break;
            case MathKind.SubSuperscript:
                Operand(text, o.Argument("e"), depth);
                text.Append('_');
                Operand(text, o.Argument("sub"), depth);
                text.Append('^');
                Operand(text, o.Argument("sup"), depth);
                break;
            case MathKind.PreSubSuperscript:
                text.Append("(_");
                Operand(text, o.Argument("sub"), depth);
                text.Append('^');
                Operand(text, o.Argument("sup"), depth);
                text.Append(')');
                Operand(text, o.Argument("e"), depth);
                break;
            case MathKind.Radical:
                text.Append('\u221A');
                if (!(p?.On("degHide") ?? false) && o.Argument("deg").Items.Count > 0)
                {
                    text.Append('(');
                    Row(text, o.Argument("deg"), depth);
                    text.Append('&');
                    Row(text, o.Argument("e"), depth);
                    text.Append(')');
                }
                else
                {
                    Operand(text, o.Argument("e"), depth);
                }

                break;
            case MathKind.Nary:
                text.Append(p?.Char("chr", null) ?? "\u222B");
                if (!(p?.On("subHide") ?? false) && o.Argument("sub").Items.Count > 0)
                {
                    text.Append('_');
                    Operand(text, o.Argument("sub"), depth);
                }

                if (!(p?.On("supHide") ?? false) && o.Argument("sup").Items.Count > 0)
                {
                    text.Append('^');
                    Operand(text, o.Argument("sup"), depth);
                }

                text.Append('\u2592');
                Operand(text, o.Argument("e"), depth);
                break;
            case MathKind.Delimiter:
                {
                    string open = p?.Char("begChr", "(") ?? "(";
                    string close = p?.Char("endChr", ")") ?? ")";
                    string separator = p?.Char("sepChr", "|") ?? "|";
                    text.Append(open.Length == 0 ? "\u3016" : open);
                    bool first = true;
                    foreach (MathRow e in o.All("e"))
                    {
                        if (!first)
                        {
                            text.Append(separator == "|" ? "\u2502" : separator);
                        }

                        first = false;
                        Row(text, e, depth);
                    }

                    text.Append(close.Length == 0 ? "\u3017" : close);
                    break;
                }

            case MathKind.Function:
                Row(text, o.Argument("fName"), depth);
                text.Append('\u2061');
                Operand(text, o.Argument("e"), depth);
                break;
            case MathKind.Accent:
                Operand(text, o.Argument("e"), depth);
                text.Append(p?.Char("chr", null) ?? "\u0302");
                break;
            case MathKind.Bar:
                text.Append(p?.Get("pos") == "top" ? '\u00AF' : '\u2581');
                Operand(text, o.Argument("e"), depth);
                break;
            case MathKind.Box:
                text.Append('\u25A1');
                Operand(text, o.Argument("e"), depth);
                break;
            case MathKind.BorderBox:
                text.Append('\u25AD');
                Operand(text, o.Argument("e"), depth);
                break;
            case MathKind.GroupCharacter:
                text.Append(p?.Char("chr", null) ?? "\u23DF");
                Operand(text, o.Argument("e"), depth);
                break;
            case MathKind.LowerLimit:
                Operand(text, o.Argument("e"), depth);
                text.Append('\u252C');
                Operand(text, o.Argument("lim"), depth);
                break;
            case MathKind.UpperLimit:
                Operand(text, o.Argument("e"), depth);
                text.Append('\u2534');
                Operand(text, o.Argument("lim"), depth);
                break;
            case MathKind.Phantom:
                text.Append('\u27E1');
                Operand(text, o.Argument("e"), depth);
                break;
            case MathKind.EquationArray:
                text.Append("\u2588(");
                Rows(text, [.. o.All("e").Select(e => (IReadOnlyList<MathRow>)[e])], depth);
                text.Append(')');
                break;
            case MathKind.Matrix:
                text.Append("\u25A0(");
                Rows(text, [.. o.Rows], depth);
                text.Append(')');
                break;
        }
    }

    private static void Rows(StringBuilder text, List<IReadOnlyList<MathRow>> rows, int depth)
    {
        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0)
            {
                text.Append('@');
            }

            for (int c = 0; c < rows[r].Count; c++)
            {
                if (c > 0)
                {
                    text.Append('&');
                }

                Row(text, rows[r][c], depth);
            }
        }
    }
}
