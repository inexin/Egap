using System.Text;
using System.Xml.Linq;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// Writes a math tree as MathML Core (W3C, 2023): presentation elements only, so browsers draw it and a tagged PDF can carry it
/// as a formula's associated file.
/// </summary>
internal static class MathMLWriter
{
    /// <summary>The MathML namespace.</summary>
    public static readonly XNamespace Ns = "http://www.w3.org/1998/Math/MathML";

    private const int MaxDepth = 64;

    /// <summary>
    /// The <c>math</c> element of <paramref name="node"/>: <c>display="block"</c> for a display paragraph (its equations in an
    /// <c>mtable</c>, one row each), inline otherwise.
    /// </summary>
    public static XElement Write(MathNode node, bool display = false)
    {
        XElement math = new(Ns + "math");
        if (node is MathParagraph paragraph)
        {
            math.SetAttributeValue("display", "block");
            List<MathZone> zones = [.. paragraph.Items.OfType<MathZone>()];
            if (zones.Count == 1)
            {
                math.Add(Row(zones[0].Content, 1));
            }
            else
            {
                math.Add(new XElement(Ns + "mtable", zones.Select(z => new XElement(Ns + "mtr", new XElement(Ns + "mtd", Row(z.Content, 1))))));
            }
        }
        else
        {
            if (display)
            {
                math.SetAttributeValue("display", "block");
            }

            math.Add(node switch
            {
                MathZone zone => Row(zone.Content, 1),
                MathRow row => Row(row, 1),
                _ => new XElement(Ns + "mrow"),
            });
        }

        return math;
    }

    private static XElement Row(MathRow row, int depth)
    {
        XElement mrow = new(Ns + "mrow");
        if (depth > MaxDepth)
        {
            return mrow;
        }

        foreach (MathNode item in row.Items)
        {
            switch (item)
            {
                case MathRun run:
                    mrow.Add(Run(run));
                    break;
                case MathObject obj:
                    mrow.Add(Object(obj, depth + 1));
                    break;
                case MathZone zone:
                    mrow.Add(Row(zone.Content, depth + 1));
                    break;
            }
        }

        // A row of one element is that element.
        return mrow.Elements().Count() == 1 ? mrow.Elements().First() : mrow;
    }

    private static IEnumerable<XElement> Run(MathRun run)
    {
        string text = run.Text;
        if (run.Normal)
        {
            yield return new XElement(Ns + "mtext", text);
            yield break;
        }

        string? style = run.Style;
        string? variant = Variant(style, run.Script);
        int i = 0;
        while (i < text.Length)
        {
            Rune rune = Rune.GetRuneAt(text, i);
            int c = rune.Value;
            if (Rune.IsDigit(rune) || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                int start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    i++;
                }

                yield return new XElement(Ns + "mn", text[start..i]);
                continue;
            }

            if (Rune.IsLetter(rune))
            {
                // Upright letters run together into one identifier (a function or unit name); italic letters are one each.
                int start = i;
                i += rune.Utf16SequenceLength;
                if (style == "p")
                {
                    while (i < text.Length && Rune.IsLetter(Rune.GetRuneAt(text, i)))
                    {
                        i += Rune.GetRuneAt(text, i).Utf16SequenceLength;
                    }
                }

                string name = text[start..i];
                XElement mi = new(Ns + "mi", name);
                string? letterVariant = variant ?? (style == "p" && name.Length == rune.Utf16SequenceLength ? "normal" : null);
                if (letterVariant is not null)
                {
                    mi.SetAttributeValue("mathvariant", letterVariant);
                }

                yield return mi;
                continue;
            }

            i += rune.Utf16SequenceLength;
            if (c == ' ')
            {
                continue;
            }

            yield return new XElement(Ns + "mo", c == '-' ? char.ConvertFromUtf32(0x2212) : rune.ToString());
        }
    }

    /// <summary>The <c>mathvariant</c> of a run's style and script; null for the default (italic letters, upright others).</summary>
    private static string? Variant(string? style, string? script)
    {
        string weight = style switch
        {
            "b" => "bold",
            "bi" => "bold-italic",
            "i" => "italic",
            _ => "",
        };
        return script switch
        {
            null or "roman" => weight is "" or "italic" ? null : weight,
            "script" => style is "b" or "bi" ? "bold-script" : "script",
            "fraktur" => style is "b" or "bi" ? "bold-fraktur" : "fraktur",
            "double-struck" => "double-struck",
            "sans-serif" => weight switch
            {
                "bold" => "bold-sans-serif",
                "bold-italic" => "sans-serif-bold-italic",
                "italic" => "sans-serif-italic",
                _ => "sans-serif",
            },
            "monospace" => "monospace",
            _ => null,
        };
    }

    private static XElement Mo(string text, params object[] attributes) => new(Ns + "mo", attributes, text);

    private static XElement Object(MathObject o, int depth)
    {
        MathProperties? p = o.Properties;
        XElement A(string name) => Row(o.Argument(name), depth);
        switch (o.Kind)
        {
            case MathKind.Fraction:
                return (p?.Get("type") ?? "bar") switch
                {
                    "lin" => new XElement(Ns + "mrow", A("num"), Mo("/"), A("den")),
                    "skw" => new XElement(Ns + "mrow", A("num"), Mo(char.ConvertFromUtf32(0x2044)), A("den")),
                    "noBar" => new XElement(Ns + "mfrac", new XAttribute("linethickness", "0"), A("num"), A("den")),
                    _ => new XElement(Ns + "mfrac", A("num"), A("den")),
                };
            case MathKind.Subscript:
                return new XElement(Ns + "msub", A("e"), A("sub"));
            case MathKind.Superscript:
                return new XElement(Ns + "msup", A("e"), A("sup"));
            case MathKind.SubSuperscript:
                return new XElement(Ns + "msubsup", A("e"), A("sub"), A("sup"));
            case MathKind.PreSubSuperscript:
                return new XElement(Ns + "mmultiscripts", A("e"), new XElement(Ns + "mprescripts"), A("sub"), A("sup"));
            case MathKind.Radical:
                return !(p?.On("degHide") ?? false) && o.Argument("deg").Items.Count > 0
                    ? new XElement(Ns + "mroot", A("e"), A("deg"))
                    : new XElement(Ns + "msqrt", A("e"));
            case MathKind.Nary:
                {
                    string chr = p?.Char("chr", null) ?? char.ConvertFromUtf32(0x222B);
                    XElement op = Mo(chr, new XAttribute("largeop", "true"));
                    if (p?.On("grow") ?? false)
                    {
                        op.SetAttributeValue("stretchy", "true");
                    }

                    bool sub = !(p?.On("subHide") ?? false);
                    bool sup = !(p?.On("supHide") ?? false);
                    bool under = (p?.Get("limLoc") ?? (MathAlphabets.IsIntegral(chr.Length > 0 ? char.ConvertToUtf32(chr, 0) : 0) ? "subSup" : "undOvr")) == "undOvr";
                    XElement scripted = (sub, sup) switch
                    {
                        (true, true) => new XElement(Ns + (under ? "munderover" : "msubsup"), op, A("sub"), A("sup")),
                        (true, false) => new XElement(Ns + (under ? "munder" : "msub"), op, A("sub")),
                        (false, true) => new XElement(Ns + (under ? "mover" : "msup"), op, A("sup")),
                        _ => op,
                    };
                    return new XElement(Ns + "mrow", scripted, A("e"));
                }

            case MathKind.Delimiter:
                {
                    string open = p?.Char("begChr", "(") ?? "(";
                    string close = p?.Char("endChr", ")") ?? ")";
                    string separator = p?.Char("sepChr", "|") ?? "|";
                    XElement row = new(Ns + "mrow");
                    if (open.Length > 0)
                    {
                        row.Add(Mo(open, new XAttribute("fence", "true")));
                    }

                    bool first = true;
                    foreach (MathRow e in o.All("e"))
                    {
                        if (!first && separator.Length > 0)
                        {
                            row.Add(Mo(separator, new XAttribute("separator", "true")));
                        }

                        first = false;
                        row.Add(Row(e, depth));
                    }

                    if (close.Length > 0)
                    {
                        row.Add(Mo(close, new XAttribute("fence", "true")));
                    }

                    return row;
                }

            case MathKind.Function:
                return new XElement(Ns + "mrow", A("fName"), Mo(char.ConvertFromUtf32(0x2061)), A("e"));
            case MathKind.Accent:
                return new XElement(Ns + "mover", new XAttribute("accent", "true"), A("e"), Mo(p?.Char("chr", null) ?? char.ConvertFromUtf32(0x302)));
            case MathKind.Bar:
                return p?.Get("pos") == "top"
                    ? new XElement(Ns + "mover", new XAttribute("accent", "true"), A("e"), Mo(char.ConvertFromUtf32(0x203E)))
                    : new XElement(Ns + "munder", new XAttribute("accentunder", "true"), A("e"), Mo("_"));
            case MathKind.GroupCharacter:
                {
                    string chr = p?.Char("chr", null) ?? char.ConvertFromUtf32(0x23DF);
                    return p?.Get("pos") == "top"
                        ? new XElement(Ns + "mover", A("e"), Mo(chr))
                        : new XElement(Ns + "munder", A("e"), Mo(chr));
                }

            case MathKind.LowerLimit:
                return new XElement(Ns + "munder", A("e"), A("lim"));
            case MathKind.UpperLimit:
                return new XElement(Ns + "mover", A("e"), A("lim"));
            case MathKind.Phantom:
                return p?.On("show", absent: true) ?? true ? A("e") : new XElement(Ns + "mphantom", A("e"));
            case MathKind.EquationArray:
                return new XElement(Ns + "mtable", o.All("e").Select(e => new XElement(Ns + "mtr", new XElement(Ns + "mtd", Row(e, depth)))));
            case MathKind.Matrix:
                return new XElement(Ns + "mtable", o.Rows.Select(r => new XElement(Ns + "mtr", r.Select(c => new XElement(Ns + "mtd", Row(c, depth))))));
            default:
                return new XElement(Ns + "mrow", A("e"));
        }
    }

    /// <summary>The MathML of <paramref name="node"/> as text, with the namespace declared.</summary>
    public static string ToText(MathNode node, bool display = false) =>
        Write(node, display).ToString(SaveOptions.DisableFormatting);
}
