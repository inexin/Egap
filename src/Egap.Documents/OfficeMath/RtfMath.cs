using System.Globalization;
using System.Text;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Documents.Rtf;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// Turns RTF math (RTF 1.9.1, "Math": <c>\mmath</c> and its <c>\m</c> control words, which spell the OMML elements) into OMML,
/// so it reads through <see cref="Omml"/> onto the same tree. A group opened by <c>\mname</c> is the element <c>m:name</c>; a
/// property group's text is its <c>m:val</c> (<c>{\mpos top}</c>); a run is <c>{\mr\mscrN\mstyN text}</c>; the math
/// properties of the header give their values as parameters (<c>\mdefJc4</c>). Character formatting round runs, and the
/// picture Word stores for older readers (<c>\mmathPict</c>), are left out.
/// </summary>
internal static class RtfMath
{
    private static readonly XNamespace M = Ns.M;
    private static readonly XName Val = Ns.M + "val";

    /// <summary>The elements that hold other elements rather than a value: zones, objects, their arguments and property sets.</summary>
    private static readonly HashSet<string> Containers =
    [
        "oMathPara", "oMathParaPr", "oMath", "acc", "accPr", "bar", "barPr", "box", "boxPr", "borderBox", "borderBoxPr", "d", "dPr",
        "eqArr", "eqArrPr", "f", "fPr", "func", "funcPr", "groupChr", "groupChrPr", "limLow", "limLowPr", "limUpp", "limUppPr", "m",
        "mPr", "mcs", "mc", "mcPr", "mr", "nary", "naryPr", "phant", "phantPr", "rad", "radPr", "sPre", "sPrePr", "sSub", "sSubPr",
        "sSup", "sSupPr", "sSubSup", "sSubSupPr", "e", "num", "den", "sub", "sup", "deg", "lim", "fName", "argPr", "ctrlPr", "mathPr",
    ];

    /// <summary>The elements whose content is math: text straight inside them is a run.</summary>
    private static readonly HashSet<string> Arguments = ["oMath", "e", "num", "den", "sub", "sup", "deg", "lim", "fName"];

    /// <summary>RTF's numbers for the values of math properties given as parameters, by property (the order of ECMA-376's lists).</summary>
    private static readonly Dictionary<string, string[]> Enumerations = new(StringComparer.Ordinal)
    {
        ["sty"] = ["p", "b", "i", "bi"],
        ["scr"] = ["roman", "script", "fraktur", "double-struck", "sans-serif", "monospace"],
        ["jc"] = ["", "centerGroup", "center", "left", "right"],
        ["defJc"] = ["", "centerGroup", "center", "left", "right"],
        ["brkBin"] = ["before", "after", "repeat"],
        ["brkBinSub"] = ["--", "+-", "-+"],
        ["intLim"] = ["subSup", "undOvr"],
        ["naryLim"] = ["subSup", "undOvr"],
    };

    /// <summary>Nesting deeper than this is left out, so hostile files cannot exhaust the stack.</summary>
    private const int MaxDepth = 128;

    /// <summary>
    /// The OMML for an RTF math group (the island markup <c>{\mmath …}</c>): its <c>m:oMathPara</c> or <c>m:oMath</c>, or null
    /// when it holds neither.
    /// </summary>
    public static XElement? ToOmml(string markup)
    {
        XElement holder = new(M + "holder");
        Read(new RtfTokenizer(Encoding.Latin1.GetBytes(markup)), holder, null);
        return holder.Descendants().FirstOrDefault(e => e.Name == M + "oMathPara" || e.Name == M + "oMath") is { } math ? new XElement(math) : null;
    }

    /// <summary>
    /// The header's math properties (<c>{\mmathPr …}</c>) as an <c>m:mathPr</c> element, with <c>m:mathFont</c> named
    /// through <paramref name="fontName"/> (RTF gives the font table index).
    /// </summary>
    public static XElement MathProperties(string markup, Func<int, string?> fontName)
    {
        XElement holder = new(M + "holder");
        Read(new RtfTokenizer(Encoding.Latin1.GetBytes(markup)), holder, fontName);
        return holder.Element(M + "mathPr") is { } properties ? new XElement(properties) : new XElement(M + "mathPr");
    }

    /// <summary>Reads tokens into <paramref name="parent"/> until the group that holds them ends.</summary>
    private static void Read(RtfTokenizer tokens, XElement parent, Func<int, string?>? fontName)
    {
        // Each open group: the element it writes into (null when it is skipped), and whether it is a run or a property value.
        Stack<Frame> stack = new();
        Frame current = new(parent, Kind.Transparent);
        int skipDepth = 0;
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            if (kind == RtfTokenKind.End)
            {
                return;
            }

            if (skipDepth > 0)
            {
                skipDepth += kind == RtfTokenKind.GroupStart ? 1 : kind == RtfTokenKind.GroupEnd ? -1 : 0;
                continue;
            }

            switch (kind)
            {
                case RtfTokenKind.GroupStart:
                    stack.Push(current);
                    current = current with { Opened = false, Kind = current.Kind is Kind.Value or Kind.Run ? current.Kind : Kind.Transparent, Uc = current.Uc, Skip = 0 };
                    if (stack.Count > MaxDepth)
                    {
                        current = stack.Pop();
                        skipDepth = 1;
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Close(current);
                    if (stack.Count == 0)
                    {
                        return;
                    }

                    current = stack.Pop();
                    break;
                case RtfTokenKind.ControlSymbol:
                    if (tokens.Symbol == '*')
                    {
                        continue;
                    }

                    if (tokens.Symbol is '\\' or '{' or '}')
                    {
                        AddText(current, tokens.Symbol.ToString());
                    }
                    else if (tokens.Symbol == '~')
                    {
                        AddText(current, "\u00A0");
                    }

                    break;
                case RtfTokenKind.Hex:
                    if (current.Skip > 0)
                    {
                        current.Skip--;
                    }
                    else
                    {
                        AddText(current, Windows1252(tokens.Byte));
                    }

                    break;
                case RtfTokenKind.Text:
                    string text = Encoding.Latin1.GetString(tokens.Text);
                    if (current.Skip > 0)
                    {
                        int skipped = Math.Min(current.Skip, text.Length);
                        current.Skip -= skipped;
                        text = text[skipped..];
                    }

                    AddText(current, text);
                    break;
                case RtfTokenKind.ControlWord:
                    string word = tokens.Word;
                    if (word == "u" && tokens.HasParameter)
                    {
                        int code = tokens.Parameter < 0 ? tokens.Parameter + 65536 : tokens.Parameter;
                        AddText(current, ((char)code).ToString());
                        current.Skip = current.Uc;
                        break;
                    }

                    if (word == "uc" && tokens.HasParameter)
                    {
                        current.Uc = Math.Max(0, tokens.Parameter);
                        break;
                    }

                    if (word is "par" or "line")
                    {
                        break;
                    }

                    if (word.Length < 2 || word[0] != 'm')
                    {
                        // Character formatting round a run, or another destination: ignored, its text with it outside runs.
                        break;
                    }

                    string name = word[1..];
                    if (name is "mathPict" || (name == "math" && current.Opened))
                    {
                        skipDepth = 1;
                        Close(current);
                        current = stack.Pop();
                        break;
                    }

                    if (name == "math")
                    {
                        break;
                    }

                    if (!current.Opened && current.Kind is Kind.Transparent && !tokens.HasParameter)
                    {
                        // The control word that opens this group: the element it is.
                        XElement element = new(M + name);
                        Target(stack)?.Add(element);
                        current = current with
                        {
                            Opened = true,
                            Element = element,
                            Kind = name == "r" ? Kind.Run : Containers.Contains(name) ? Kind.Container : Kind.Value,
                        };
                        break;
                    }

                    // A property given as a parameter (\mstyN in a run, \mjc3 in paragraph properties, \mdefJc4 in the header).
                    if (current.Element is { } owner && current.Kind is Kind.Run or Kind.Container)
                    {
                        AddParameter(owner, current.Kind == Kind.Run, name, tokens.HasParameter ? tokens.Parameter : null, fontName);
                    }

                    break;
            }
        }
    }

    private static XElement? Target(Stack<Frame> stack)
    {
        foreach (Frame frame in stack)
        {
            if (frame.Element is { } element && frame.Kind is Kind.Container or Kind.Transparent)
            {
                return element;
            }

            if (frame.Kind is Kind.Run or Kind.Value)
            {
                return null;
            }
        }

        return null;
    }

    private static void AddParameter(XElement owner, bool run, string name, int? parameter, Func<int, string?>? fontName)
    {
        string? value = parameter is not int number ? null
            : name == "mathFont" ? fontName?.Invoke(number)
            : Enumerations.TryGetValue(name, out string[]? values) ? (number >= 0 && number < values.Length ? values[number] : null)
            : name is "nor" or "lit" or "aln" or "dispDef" or "smallFrac" ? (number == 0 ? "0" : null)
            : number.ToString(CultureInfo.InvariantCulture);
        if (parameter is not null && value is null && name is not ("nor" or "lit" or "aln" or "dispDef" or "smallFrac"))
        {
            return;
        }

        XElement property = new(M + name);
        if (name == "brk")
        {
            if (parameter is int at)
            {
                property.Add(new XAttribute(M + "alnAt", at.ToString(CultureInfo.InvariantCulture)));
            }
        }
        else if (value is not null)
        {
            property.Add(new XAttribute(Val, value));
        }

        if (run)
        {
            XElement properties = owner.Element(M + "rPr") ?? AddFirst(owner, new XElement(M + "rPr"));
            properties.Add(property);
        }
        else
        {
            owner.Add(property);
        }
    }

    private static XElement AddFirst(XElement owner, XElement child)
    {
        owner.AddFirst(child);
        return child;
    }

    private static void AddText(Frame frame, string text)
    {
        if (text.Length == 0 || frame.Element is not { } element)
        {
            return;
        }

        if (frame.Kind == Kind.Run)
        {
            XElement? t = element.Element(M + "t");
            if (t is null)
            {
                t = new XElement(M + "t");
                element.Add(t);
            }

            t.Value += text;
        }
        else if (frame.Kind is Kind.Transparent or Kind.Container && Arguments.Contains(element.Name.LocalName) && !string.IsNullOrWhiteSpace(text))
        {
            // Text straight in an argument, outside any run (Word writes some cells so): a run with no properties of its own.
            if (element.LastNode is not XElement { Name.LocalName: "r" } bare || bare.Annotation<BareRun>() is null)
            {
                bare = new XElement(M + "r", new XElement(M + "t"));
                bare.AddAnnotation(BareRun.Instance);
                element.Add(bare);
            }

            bare.Element(M + "t")!.Value += text;
        }
        else if (frame.Kind == Kind.Value)
        {
            string value = (string?)element.Attribute(Val) + text;

            // RTF spells a matrix's and an equation array's bottom alignment as the top/bot pair's "bot".
            element.SetAttributeValue(Val, element.Name.LocalName == "baseJc" && value == "bot" ? "bottom" : value);
        }
    }

    private static void Close(Frame frame)
    {
        if (frame.Kind == Kind.Run && frame.Element?.Element(M + "t") is { } t && (t.Value.StartsWith(' ') || t.Value.EndsWith(' ')))
        {
            t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        }
    }

    private static string Windows1252(byte value) =>
        value is >= 0x80 and < 0xA0 ? CodePagesEncodingProvider.Instance.GetEncoding(1252)?.GetString([value]) ?? "?" : ((char)value).ToString();

    /// <summary>Marks a run made for text outside <c>\mr</c>, so following text joins it.</summary>
    private sealed class BareRun
    {
        public static readonly BareRun Instance = new();
    }

    private enum Kind
    {
        /// <summary>A group with no math word of its own, such as character formatting: its groups write into the element above.</summary>
        Transparent,

        /// <summary>An element that holds other elements.</summary>
        Container,

        /// <summary>A math run: parameters are its properties, text is its <c>m:t</c>.</summary>
        Run,

        /// <summary>A property whose text is its value.</summary>
        Value,
    }

    private sealed record Frame(XElement? Element, Kind Kind)
    {
        public bool Opened { get; init; }

        public int Uc { get; set; } = 1;

        public int Skip { get; set; }
    }
}
