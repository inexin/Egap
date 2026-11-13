using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// Reads MathML (MathML Core, W3C 2023, and the MathML 3 presentation elements ODF formulas and HTML pages use) into the math
/// tree: tokens into runs, and fractions, radicals, scripts, under- and overscripts, tables and fences into the matching Office
/// Math objects, so an ODT or HTML equation lays out and exports like a Word one. Content MathML and annotations are left out.
/// </summary>
internal static class MathMLReader
{
    private const int MaxDepth = 64;

    private static readonly XNamespace Ns = MathMLWriter.Ns;

    /// <summary>
    /// The equation of a <c>math</c> element: a display paragraph for <c>display="block"</c> (or MathML 2's <c>mode="display"</c>),
    /// an inline zone otherwise.
    /// </summary>
    public static MathNode Read(XElement math)
    {
        MathRow row = Row(math.Elements(), 1);
        bool display = (string?)math.Attribute("display") == "block" || (string?)math.Attribute("mode") == "display";
        if (!display)
        {
            return new MathZone(row);
        }

        MathParagraph paragraph = new();
        paragraph.Items.Add(new MathZone(row));
        return paragraph;
    }

    /// <summary>The first MathML <c>math</c> element at or under <paramref name="element"/>; null when there is none.</summary>
    public static XElement? Find(XElement element) =>
        element.Name == Ns + "math" ? element : element.Descendants(Ns + "math").FirstOrDefault();

    /// <summary>A row of MathML elements; an n-ary operator takes the element after it as its operand, a function name too.</summary>
    private static MathRow Row(IEnumerable<XElement> elements, int depth)
    {
        MathRow row = new();
        if (depth > MaxDepth)
        {
            return row;
        }

        List<XElement> items = [.. elements.Where(e => e.Name.Namespace == Ns)];
        for (int i = 0; i < items.Count; i++)
        {
            XElement element = items[i];
            if (Nary(element, depth) is { } nary)
            {
                // The operand is the next element, up to a relation or another operator.
                if (i + 1 < items.Count && !IsOperatorToken(items[i + 1]))
                {
                    nary.Arguments.Add(("e", Arg(items[++i], depth)));
                }
                else
                {
                    nary.Arguments.Add(("e", new MathRow()));
                }

                row.Items.Add(nary);
                continue;
            }

            if (i + 2 < items.Count && IsApply(items[i + 1]))
            {
                MathObject function = new(MathKind.Function);
                function.Arguments.Add(("fName", Arg(element, depth)));
                function.Arguments.Add(("e", Arg(items[i + 2], depth)));
                row.Items.Add(function);
                i += 2;
                continue;
            }

            if (IsApply(element) || IsInvisible(element))
            {
                continue;
            }

            // An opening fence and its matching closing fence in the same row hold a delimiter.
            if (Closing(items, i) is int end && Fenced(items[i..(end + 1)], depth) is { } delimiter)
            {
                row.Items.Add(delimiter);
                i = end;
                continue;
            }

            Add(row, Node(element, depth));
        }

        return row;
    }

    private static void Add(MathRow row, MathNode? node)
    {
        switch (node)
        {
            case null:
                break;
            case MathRow inner:
                row.Items.AddRange(inner.Items);
                break;
            default:
                row.Items.Add(node);
                break;
        }
    }

    /// <summary>One element as an argument row.</summary>
    private static MathRow Arg(XElement? element, int depth)
    {
        MathRow row = new();
        if (element is not null && depth <= MaxDepth)
        {
            Add(row, Node(element, depth));
        }

        return row;
    }

    private static MathNode? Node(XElement element, int depth)
    {
        depth++;
        if (depth > MaxDepth)
        {
            return null;
        }

        List<XElement> children = [.. element.Elements().Where(e => e.Name.Namespace == Ns)];
        XElement? Child(int i) => i < children.Count ? children[i] : null;
        MathRow A(int i) => Arg(Child(i), depth);

        switch (element.Name.LocalName)
        {
            case "mi":
                return Identifier(element);
            case "mn":
                return Token(Text(element), Style(element, null));
            case "mo":
                return Token(Text(element).Replace('-', '\u2212'), Style(element, null));
            case "mtext":
                return Text(element) is { Length: > 0 } text ? Normal(text) : null;
            case "ms":
                return Normal("\"" + Text(element) + "\"");
            case "mspace":
                return Space(element);
            case "mrow" or "mstyle" or "mpadded" or "merror" or "math":
                return (MathNode?)Fenced(children, depth) ?? Row(children, depth);
            case "semantics" or "maction":
                return Child(0) is { } first ? Node(first, depth) : null;
            case "mfrac":
                {
                    MathObject fraction = Object(MathKind.Fraction, ("num", A(0)), ("den", A(1)));
                    if (IsZero((string?)element.Attribute("linethickness")))
                    {
                        fraction.Properties = Properties(("type", "noBar"));
                    }
                    else if ((string?)element.Attribute("bevelled") == "true")
                    {
                        fraction.Properties = Properties(("type", "skw"));
                    }

                    return fraction;
                }

            case "msqrt":
                {
                    MathObject radical = Object(MathKind.Radical, ("deg", new MathRow()), ("e", Row(children, depth)));
                    radical.Properties = Properties(("degHide", "1"));
                    return radical;
                }

            case "mroot":
                return Object(MathKind.Radical, ("deg", A(1)), ("e", A(0)));
            case "msub":
                return Object(MathKind.Subscript, ("e", A(0)), ("sub", A(1)));
            case "msup":
                return Object(MathKind.Superscript, ("e", A(0)), ("sup", A(1)));
            case "msubsup":
                return Object(MathKind.SubSuperscript, ("e", A(0)), ("sub", A(1)), ("sup", A(2)));
            case "mmultiscripts":
                return Multiscripts(children, depth);
            case "munder":
                return Under(element, Child(0), Child(1), depth);
            case "mover":
                return Over(element, Child(0), Child(1), depth);
            case "munderover":
                {
                    MathObject lower = Object(MathKind.LowerLimit, ("e", A(0)), ("lim", A(1)));
                    return Object(MathKind.UpperLimit, ("e", new MathRow([lower])), ("lim", A(2)));
                }

            case "mphantom":
                {
                    MathObject phantom = Object(MathKind.Phantom, ("e", Row(children, depth)));
                    phantom.Properties = Properties(("show", "0"));
                    return phantom;
                }

            case "menclose":
                return Object(MathKind.BorderBox, ("e", Row(children, depth)));
            case "mfenced":
                return MFenced(element, children, depth);
            case "mtable":
                return Table(children, depth);
            default:
                return null;
        }
    }

    private static MathObject Object(MathKind kind, params (string Name, MathRow Row)[] arguments)
    {
        MathObject obj = new(kind);
        obj.Arguments.AddRange(arguments);
        return obj;
    }

    private static MathProperties Properties(params (string Name, string? Value)[] items)
    {
        MathProperties properties = new();
        foreach ((string name, string? value) in items)
        {
            properties.Items.Add(new MathProperty(name, value));
        }

        return properties;
    }

    private static string Text(XElement element)
    {
        string text = element.Value.Trim();
        return text.Length > 1024 ? text[..1024] : text;
    }

    private static MathRun? Token(string text, MathProperties? properties)
    {
        if (text.Length == 0)
        {
            return null;
        }

        MathRun run = new() { Properties = properties };
        run.Content.Add(text);
        run.Preserve.Add(false);
        return run;
    }

    private static MathRun Normal(string text)
    {
        MathRun run = Token(text, Properties(("nor", null)))!;
        run.Preserve[0] = true;
        return run;
    }

    /// <summary>
    /// An identifier: one letter is italic, and a longer name (<c>sin</c>, <c>max</c>) upright, unless <c>mathvariant</c> says
    /// otherwise.
    /// </summary>
    private static MathRun? Identifier(XElement element)
    {
        string text = Text(element);
        bool single = text.Length > 0 && Rune.GetRuneAt(text, 0).Utf16SequenceLength == text.Length;
        return Token(text, Style(element, single ? null : "p"));
    }

    /// <summary>The <c>sty</c> and <c>scr</c> of a token's <c>mathvariant</c>; <paramref name="fallback"/> when it has none.</summary>
    private static MathProperties? Style(XElement element, string? fallback)
    {
        string? variant = (string?)element.Attribute("mathvariant");
        if ((string?)element.Attribute("fontweight") == "bold" && variant is null)
        {
            variant = fallback == "p" ? "bold" : "bold-italic";
        }

        (string? style, string? script) = variant switch
        {
            null => (fallback, null),
            "normal" => ("p", null),
            "bold" => ("b", null),
            "italic" => ("i", null),
            "bold-italic" => ("bi", null),
            "double-struck" => ("p", "double-struck"),
            "script" => ("p", "script"),
            "bold-script" => ("b", "script"),
            "fraktur" => ("p", "fraktur"),
            "bold-fraktur" => ("b", "fraktur"),
            "sans-serif" => ("p", "sans-serif"),
            "bold-sans-serif" => ("b", "sans-serif"),
            "sans-serif-italic" => ("i", "sans-serif"),
            "sans-serif-bold-italic" => ("bi", "sans-serif"),
            "monospace" => ("p", "monospace"),
            _ => (fallback, null),
        };
        if (style is null && script is null)
        {
            return null;
        }

        MathProperties properties = new();
        if (script is not null)
        {
            properties.Items.Add(new MathProperty("scr", script));
        }

        if (style is not null)
        {
            properties.Items.Add(new MathProperty("sty", style));
        }

        return properties;
    }

    /// <summary>An <c>mspace</c> of at least a third of an em as a space; narrower ones are left out.</summary>
    private static MathRun? Space(XElement element)
    {
        string width = ((string?)element.Attribute("width") ?? "").Trim();
        double em = width.EndsWith("em", StringComparison.Ordinal) && double.TryParse(width[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
        if (em < 0.3)
        {
            return null;
        }

        MathRun run = new();
        run.Content.Add(new string(' ', Math.Clamp((int)Math.Round(em / 0.33), 1, 12)));
        run.Preserve.Add(true);
        return run;
    }

    private static bool IsZero(string? thickness)
    {
        if (thickness is null)
        {
            return false;
        }

        string number = thickness.Trim().TrimEnd('a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z', '%');
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value == 0;
    }

    private static string? OperatorText(XElement? element) => element?.Name == Ns + "mo" ? Text(element) : null;

    private static bool IsOperatorToken(XElement element) =>
        OperatorText(element) is { Length: > 0 } text && MathAlphabets.Classify(char.ConvertToUtf32(text, 0)) is MathClass.Relation or MathClass.Binary or MathClass.Close or MathClass.Punctuation;

    private static bool IsApply(XElement element) => OperatorText(element) == "\u2061";

    private static bool IsInvisible(XElement element) => OperatorText(element) is "\u2062" or "\u2063" or "\u2064" or "";

    /// <summary>Whether a character is an n-ary operator (sums, products, integrals, big unions …).</summary>
    private static bool IsNaryChar(string? text) =>
        text is { Length: > 0 } && char.ConvertToUtf32(text, 0) is var c && text.Length == char.ConvertFromUtf32(c).Length
        && (MathAlphabets.IsIntegral(c) || c is >= 0x220F and <= 0x2211 || c is >= 0x22C0 and <= 0x22C3 || c is >= 0x2A00 and <= 0x2A0A);

    /// <summary>
    /// An n-ary operator: a bare <c>mo</c> (∑, ∫ …), or one with scripts (<c>msubsup</c>, <c>munderover</c> …); null for
    /// anything else.
    /// </summary>
    private static MathObject? Nary(XElement element, int depth)
    {
        string name = element.Name.LocalName;
        XElement? op = name == "mo" ? element : name is "msub" or "msup" or "msubsup" or "munder" or "mover" or "munderover" ? element.Elements().FirstOrDefault() : null;
        string? chr = OperatorText(op);
        if (!IsNaryChar(chr))
        {
            return null;
        }

        List<XElement> children = [.. element.Elements().Where(e => e.Name.Namespace == Ns)];
        MathRow Script(int i) => i < children.Count ? Arg(children[i], depth + 1) : new MathRow();
        (MathRow sub, MathRow sup) = name switch
        {
            "msub" or "munder" => (Script(1), new MathRow()),
            "msup" or "mover" => (new MathRow(), Script(1)),
            "msubsup" or "munderover" => (Script(1), Script(2)),
            _ => (new MathRow(), new MathRow()),
        };

        MathObject nary = new(MathKind.Nary);
        MathProperties properties = Properties(("chr", chr));
        if (name != "mo")
        {
            properties.Items.Add(new MathProperty("limLoc", name is "munder" or "mover" or "munderover" ? "undOvr" : "subSup"));
        }

        if (sub.Items.Count == 0)
        {
            properties.Items.Add(new MathProperty("subHide", "1"));
        }

        if (sup.Items.Count == 0)
        {
            properties.Items.Add(new MathProperty("supHide", "1"));
        }

        nary.Properties = properties;
        nary.Arguments.Add(("sub", sub));
        nary.Arguments.Add(("sup", sup));
        return nary;
    }

    private static MathObject Under(XElement element, XElement? baseElement, XElement? under, int depth)
    {
        MathRow e = Arg(baseElement, depth);
        string? chr = OperatorText(under);
        if (chr is "_" or "\u0332" or "\u00AF" && (string?)element.Attribute("accentunder") != "false")
        {
            MathObject bar = Object(MathKind.Bar, ("e", e));
            bar.Properties = Properties(("pos", "bot"));
            return bar;
        }

        if (chr is { Length: 1 } && IsStretchyGroup(chr[0]))
        {
            MathObject group = Object(MathKind.GroupCharacter, ("e", e));
            group.Properties = Properties(("chr", chr), ("pos", "bot"), ("vertJc", "top"));
            return group;
        }

        return Object(MathKind.LowerLimit, ("e", e), ("lim", Arg(under, depth)));
    }

    private static MathObject Over(XElement element, XElement? baseElement, XElement? over, int depth)
    {
        MathRow e = Arg(baseElement, depth);
        string? chr = OperatorText(over);
        if (chr is "\u203E" or "\u00AF" or "\u0305" or "_")
        {
            MathObject bar = Object(MathKind.Bar, ("e", e));
            bar.Properties = Properties(("pos", "top"));
            return bar;
        }

        if (chr is { Length: 1 } && IsStretchyGroup(chr[0]))
        {
            MathObject group = Object(MathKind.GroupCharacter, ("e", e));
            group.Properties = Properties(("chr", chr), ("pos", "top"), ("vertJc", "bot"));
            return group;
        }

        if (chr is { Length: 1 } && (string?)element.Attribute("accent") != "false" && IsAccent(chr[0]))
        {
            MathObject accent = Object(MathKind.Accent, ("e", e));
            accent.Properties = Properties(("chr", Combining(chr[0])));
            return accent;
        }

        return Object(MathKind.UpperLimit, ("e", e), ("lim", Arg(over, depth)));
    }

    private static bool IsStretchyGroup(char c) => c is >= '\u23B4' and <= '\u23B5' or >= '\u23DC' and <= '\u23E1' or '\u2190' or '\u2192' or '\u2194' or '\u21D0' or '\u21D2' or '\u21D4';

    private static bool IsAccent(char c) => c is >= '\u0300' and <= '\u036F' or '^' or '~' or '\u02C6' or '\u02C7' or '\u02D8' or '\u02D9' or '\u02DC' or '\u00A8' or '\u00B4' or '`' or '\u2192' or '\u20D7';

    /// <summary>The combining form Office Math's accents use for a spacing accent character.</summary>
    private static string Combining(char c) => c switch
    {
        '^' or '\u02C6' => "\u0302",
        '~' or '\u02DC' => "\u0303",
        '\u02C7' => "\u030C",
        '\u02D8' => "\u0306",
        '\u02D9' => "\u0307",
        '\u00A8' => "\u0308",
        '\u00B4' => "\u0301",
        '`' => "\u0300",
        '\u2192' => "\u20D7",
        _ => c.ToString(),
    };

    /// <summary>
    /// A row that is an opening fence, content and a closing fence (<c>( … )</c>, <c>[ … )</c>, <c>| … |</c>): a delimiter,
    /// with separators (<c>,</c> marked <c>separator</c> or <c>|</c>) splitting its arguments; null for any other row.
    /// </summary>
    private static MathObject? Fenced(List<XElement> children, int depth)
    {
        if (children.Count < 2)
        {
            return null;
        }

        string? open = OperatorText(children[0]);
        string? close = OperatorText(children[^1]);
        if (open is null || close is null || !IsFence(children[0], open, opening: true) || !IsFence(children[^1], close, opening: false))
        {
            return null;
        }

        // The fences must pair: nothing between them may close the first early.
        int level = 0;
        for (int i = 1; i < children.Count - 1; i++)
        {
            if (OperatorText(children[i]) is { } inner && IsFence(children[i], inner, opening: true) && inner != "|")
            {
                level++;
            }
            else if (OperatorText(children[i]) is { } closing && IsFence(children[i], closing, opening: false) && closing != "|" && --level < 0)
            {
                return null;
            }
        }

        List<List<XElement>> parts = [[]];
        string? separator = null;
        foreach (XElement child in children.Skip(1).Take(children.Count - 2))
        {
            if ((string?)child.Attribute("separator") == "true" && OperatorText(child) is { } sep)
            {
                separator ??= sep;
                parts.Add([]);
                continue;
            }

            parts[^1].Add(child);
        }

        MathObject delimiter = new(MathKind.Delimiter);
        delimiter.Arguments.AddRange(parts.Select(p => ("e", Row(p, depth))));
        MathProperties properties = Properties(("begChr", open), ("endChr", close));
        if (separator is not null)
        {
            properties.Items.Add(new MathProperty("sepChr", separator));
        }

        delimiter.Properties = properties;
        return delimiter;
    }

    /// <summary>The index of the fence that closes the opening fence at <paramref name="i"/>; null when it is no such fence or none closes it.</summary>
    private static int? Closing(List<XElement> items, int i)
    {
        if (OperatorText(items[i]) is not { } open || open is "|" or "\u2016" || !IsFence(items[i], open, opening: true))
        {
            return null;
        }

        int level = 0;
        for (int j = i + 1; j < items.Count; j++)
        {
            if (OperatorText(items[j]) is not { } text || text is "|" or "\u2016")
            {
                continue;
            }

            if (IsFence(items[j], text, opening: true))
            {
                level++;
            }
            else if (IsFence(items[j], text, opening: false) && level-- == 0)
            {
                return j;
            }
        }

        return null;
    }

    private static bool IsFence(XElement element, string text, bool opening)
    {
        if ((string?)element.Attribute("fence") == "false" || text.Length != 1)
        {
            return false;
        }

        return opening
            ? text[0] is '(' or '[' or '{' or '|' or '\u2016' or '\u2308' or '\u230A' or '\u27E8' or '\u27E6' or '\u2329'
            : text[0] is ')' or ']' or '}' or '|' or '\u2016' or '\u2309' or '\u230B' or '\u27E9' or '\u27E7' or '\u232A';
    }

    /// <summary>MathML 2's <c>mfenced</c>: a delimiter with its <c>open</c>, <c>close</c> and <c>separators</c>.</summary>
    private static MathObject MFenced(XElement element, List<XElement> children, int depth)
    {
        string open = (string?)element.Attribute("open") ?? "(";
        string close = (string?)element.Attribute("close") ?? ")";
        string separators = ((string?)element.Attribute("separators") ?? ",").Replace(" ", "", StringComparison.Ordinal);
        MathObject delimiter = new(MathKind.Delimiter);
        delimiter.Arguments.AddRange(children.Select(c => ("e", Arg(c, depth))));
        delimiter.Properties = Properties(("begChr", open), ("endChr", close), ("sepChr", separators.Length > 0 ? separators[..1] : ""));
        return delimiter;
    }

    /// <summary><c>mmultiscripts</c>: scripts before the base as a pre-script object, after it as sub- and superscripts.</summary>
    private static MathNode Multiscripts(List<XElement> children, int depth)
    {
        if (children.Count == 0)
        {
            return new MathRow();
        }

        int pre = children.FindIndex(c => c.Name.LocalName == "mprescripts");
        int end = pre < 0 ? children.Count : pre;
        List<XElement> post = end > 1 ? children[1..end] : [];
        List<XElement> prescripts = pre < 0 ? [] : children[(pre + 1)..];
        MathRow Script(List<XElement> list, int i) => i < list.Count && list[i].Name.LocalName != "none" ? Arg(list[i], depth) : new MathRow();

        // An mprescripts first leaves no base.
        MathRow e = pre == 0 ? new MathRow() : Arg(children[0], depth);
        MathNode node = new MathRow(e.Items);
        if (post.Count > 0)
        {
            node = Object(MathKind.SubSuperscript, ("e", e), ("sub", Script(post, 0)), ("sup", Script(post, 1)));
        }

        if (prescripts.Count > 0)
        {
            node = Object(MathKind.PreSubSuperscript, ("sub", Script(prescripts, 0)), ("sup", Script(prescripts, 1)), ("e", node is MathRow row ? row : new MathRow([node])));
        }

        return node;
    }

    /// <summary>An <c>mtable</c>: one column is an equation array, more a matrix.</summary>
    private static MathObject Table(List<XElement> children, int depth)
    {
        List<List<MathRow>> rows = [];
        foreach (XElement tr in children.Where(c => c.Name.LocalName is "mtr" or "mlabeledtr").Take(1000))
        {
            IEnumerable<XElement> cells = tr.Elements(Ns + "mtd");
            if (tr.Name.LocalName == "mlabeledtr")
            {
                cells = cells.Skip(1);
            }

            rows.Add([.. cells.Take(100).Select(td => Row(td.Elements(), depth + 1))]);
        }

        int columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (columns <= 1)
        {
            MathObject array = new(MathKind.EquationArray);
            array.Arguments.AddRange(rows.Select(r => ("e", r.Count > 0 ? r[0] : new MathRow())));
            return array;
        }

        MathObject matrix = new(MathKind.Matrix);
        foreach (List<MathRow> row in rows)
        {
            while (row.Count < columns)
            {
                row.Add(new MathRow());
            }

            matrix.Rows.Add(row);
        }

        return matrix;
    }
}
