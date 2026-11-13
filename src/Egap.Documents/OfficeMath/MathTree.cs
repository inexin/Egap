using System.Xml.Linq;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// The math objects of Office Math (ECMA-376 Part 1 §22.1.2): each has its own properties element and named arguments. Runs,
/// zones (<c>m:oMath</c>) and paragraphs (<c>m:oMathPara</c>) are their own node types.
/// </summary>
internal enum MathKind
{
    /// <summary><c>m:acc</c>: an accent over its base.</summary>
    Accent,

    /// <summary><c>m:bar</c>: a bar over or under its base.</summary>
    Bar,

    /// <summary><c>m:box</c>: its base as one unit (an operator emulator, a no-break group).</summary>
    Box,

    /// <summary><c>m:borderBox</c>: a border and strike lines round its base.</summary>
    BorderBox,

    /// <summary><c>m:d</c>: delimiters round one or more bases, with separators between them.</summary>
    Delimiter,

    /// <summary><c>m:eqArr</c>: equations stacked and aligned.</summary>
    EquationArray,

    /// <summary><c>m:f</c>: a fraction, numerator over denominator.</summary>
    Fraction,

    /// <summary><c>m:func</c>: a function name applied to its argument.</summary>
    Function,

    /// <summary><c>m:groupChr</c>: a stretched character, such as a brace, over or under its base.</summary>
    GroupCharacter,

    /// <summary><c>m:limLow</c>: a limit below its base.</summary>
    LowerLimit,

    /// <summary><c>m:limUpp</c>: a limit above its base.</summary>
    UpperLimit,

    /// <summary><c>m:m</c>: a matrix of rows of cells.</summary>
    Matrix,

    /// <summary><c>m:nary</c>: an n-ary operator (sum, integral) with its limits and operand.</summary>
    Nary,

    /// <summary><c>m:phant</c>: its base, hidden or with dimensions left out.</summary>
    Phantom,

    /// <summary><c>m:rad</c>: a radical with its degree.</summary>
    Radical,

    /// <summary><c>m:sPre</c>: a subscript and superscript before the base.</summary>
    PreSubSuperscript,

    /// <summary><c>m:sSub</c>: a subscript.</summary>
    Subscript,

    /// <summary><c>m:sSup</c>: a superscript.</summary>
    Superscript,

    /// <summary><c>m:sSubSup</c>: a subscript and a superscript.</summary>
    SubSuperscript,
}

/// <summary>A node of the math tree that every equation input (OMML, RTF <c>\mmath</c>, MathML) reads into.</summary>
internal abstract class MathNode
{
}

/// <summary>A list of nodes laid out in a row: an argument of an object (<c>m:e</c>, <c>m:num</c> …) or a zone's content.</summary>
internal sealed class MathRow : MathNode
{
    public MathRow(IEnumerable<MathNode>? items = null) => Items = [.. items ?? []];

    /// <summary>The nodes, in order.</summary>
    public List<MathNode> Items { get; }

    /// <summary>The argument's own properties (<c>m:argPr</c>, such as <c>m:argSz</c>); null when it has none.</summary>
    public MathProperties? Properties { get; set; }
}

/// <summary>
/// The properties of a math object, run or argument: simple ones as name and value (the <c>m:val</c> attribute, null when the
/// element has none, which for an on/off property means on), in file order, and anything else (<c>m:ctrlPr</c>,
/// <c>m:mcs</c>, unknown elements) kept as XML.
/// </summary>
internal sealed class MathProperties
{
    /// <summary>The simple properties and other elements, in file order: each item is a <see cref="MathProperty"/> or an XElement.</summary>
    public List<object> Items { get; } = [];

    /// <summary>The value of the property <paramref name="name"/>; null when it is absent or has no value.</summary>
    public string? Get(string name)
    {
        foreach (object item in Items)
        {
            if (item is MathProperty property && property.Name == name)
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>Whether the property <paramref name="name"/> is present.</summary>
    public bool Has(string name) => Items.Any(i => i is MathProperty property && property.Name == name);

    /// <summary>
    /// An on/off property: present without a value, or with <c>on</c>, <c>1</c> or <c>true</c>, is on; absent is
    /// <paramref name="absent"/>.
    /// </summary>
    public bool On(string name, bool absent = false)
    {
        if (!Has(name))
        {
            return absent;
        }

        return Get(name) is null or "on" or "1" or "true";
    }

    /// <summary>A character property (<c>m:chr</c>, <c>m:begChr</c> …); <paramref name="absent"/> when it is not set.</summary>
    public string? Char(string name, string? absent) => Has(name) ? Get(name) ?? "" : absent;

    /// <summary>An integer property; <paramref name="absent"/> when it is not set or not a number.</summary>
    public int Int(string name, int absent) =>
        int.TryParse(Get(name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : absent;

    /// <summary>A property element kept as XML, such as <c>m:ctrlPr</c> or <c>m:mcs</c>; null when absent.</summary>
    public XElement? Element(string localName) => Items.OfType<XElement>().FirstOrDefault(e => e.Name.LocalName == localName);
}

/// <summary>A simple math property: its element's local name and its <c>m:val</c>, null when the element has none.</summary>
internal sealed record MathProperty(string Name, string? Value);

/// <summary>
/// A math run (<c>m:r</c>): text with its math properties (<c>m:rPr</c>: <c>m:sty</c>, <c>m:scr</c>, <c>m:nor</c>,
/// <c>m:lit</c>, <c>m:brk</c>, <c>m:aln</c>) and its Word run properties (<c>w:rPr</c>), kept as XML.
/// </summary>
internal sealed class MathRun : MathNode
{
    /// <summary>The run's math properties; null when it has no <c>m:rPr</c>.</summary>
    public MathProperties? Properties { get; set; }

    /// <summary>The Word run properties (<c>w:rPr</c>, and anything else before the text), kept as XML.</summary>
    public List<XElement> WordProperties { get; } = [];

    /// <summary>The run's content in order: a string for each <c>m:t</c>, an XElement for anything else (<c>w:br</c> …).</summary>
    public List<object> Content { get; } = [];

    /// <summary>Whether an <c>m:t</c> keeps its spaces (<c>xml:space="preserve"</c>), per text piece in order.</summary>
    public List<bool> Preserve { get; } = [];

    /// <summary>The run's text, its <c>m:t</c> pieces joined.</summary>
    public string Text => string.Concat(Content.OfType<string>());

    /// <summary>The math style (<c>m:sty</c>: p, b, i, bi); null when not set.</summary>
    public string? Style => Properties?.Get("sty");

    /// <summary>The math script (<c>m:scr</c>: roman, script, fraktur, double-struck, sans-serif, monospace); null when not set.</summary>
    public string? Script => Properties?.Get("scr");

    /// <summary>Normal text (<c>m:nor</c>): set as ordinary text, not math.</summary>
    public bool Normal => Properties?.On("nor") ?? false;

    /// <summary>Literal (<c>m:lit</c>): characters are not given math meaning.</summary>
    public bool Literal => Properties?.On("lit") ?? false;
}

/// <summary>A math object (<see cref="MathKind"/>) with its properties and arguments.</summary>
internal sealed class MathObject : MathNode
{
    public MathObject(MathKind kind) => Kind = kind;

    /// <summary>Which object.</summary>
    public MathKind Kind { get; }

    /// <summary>The object's properties (<c>m:fPr</c> …); null when it has none.</summary>
    public MathProperties? Properties { get; set; }

    /// <summary>
    /// The arguments in file order, each with its element name (<c>e</c>, <c>num</c>, <c>den</c>, <c>sub</c>, <c>sup</c>,
    /// <c>deg</c>, <c>lim</c>, <c>fName</c>); a delimiter and an equation array have several <c>e</c>. A matrix holds its
    /// rows in <see cref="Rows"/> instead.
    /// </summary>
    public List<(string Name, MathRow Row)> Arguments { get; } = [];

    /// <summary>A matrix's rows (<c>m:mr</c>), each a list of cells (<c>m:e</c>); empty for other objects.</summary>
    public List<List<MathRow>> Rows { get; } = [];

    /// <summary>The first argument named <paramref name="name"/>; an empty row when there is none.</summary>
    public MathRow Argument(string name)
    {
        foreach ((string n, MathRow row) in Arguments)
        {
            if (n == name)
            {
                return row;
            }
        }

        return new MathRow();
    }

    /// <summary>Every argument named <paramref name="name"/>, in order.</summary>
    public IEnumerable<MathRow> All(string name) => Arguments.Where(a => a.Name == name).Select(a => a.Row);
}

/// <summary>An element inside math the tree does not model (a bookmark, a revision mark, a Word run), kept as XML.</summary>
internal sealed class MathOther : MathNode
{
    public MathOther(XElement element) => Element = element;

    /// <summary>The element as read.</summary>
    public XElement Element { get; }
}

/// <summary>An equation (<c>m:oMath</c>): a row of math, inline in its paragraph or one line of a display paragraph.</summary>
internal sealed class MathZone : MathNode
{
    public MathZone(MathRow content) => Content = content;

    /// <summary>The equation's content.</summary>
    public MathRow Content { get; }
}

/// <summary>A display math paragraph (<c>m:oMathPara</c>): equations on their own lines, with their justification.</summary>
internal sealed class MathParagraph : MathNode
{
    /// <summary>The paragraph's properties (<c>m:oMathParaPr</c>, with <c>m:jc</c>); null when it has none.</summary>
    public MathProperties? Properties { get; set; }

    /// <summary>The equations and anything else the paragraph holds, in order.</summary>
    public List<MathNode> Items { get; } = [];

    /// <summary>The justification (<c>m:jc</c>: left, right, center, centerGroup); null when not set.</summary>
    public string? Justification => Properties?.Get("jc");
}
