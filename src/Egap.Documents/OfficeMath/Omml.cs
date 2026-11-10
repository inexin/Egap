using System.Xml.Linq;

using Egap.Documents.Docx;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// Reads Office Math Markup (ECMA-376 Part 1 §22.1) into the math tree and writes the tree back. What the tree does not model
/// is kept as XML, so writing what was read gives the same markup.
/// </summary>
internal static class Omml
{
    /// <summary>Nesting deeper than this is kept as XML rather than read, so hostile files cannot exhaust the stack.</summary>
    public const int MaxDepth = 64;

    private static readonly XNamespace M = Ns.M;
    private static readonly XName Val = Ns.M + "val";

    private static readonly Dictionary<string, MathKind> Kinds = new(StringComparer.Ordinal)
    {
        ["acc"] = MathKind.Accent,
        ["bar"] = MathKind.Bar,
        ["box"] = MathKind.Box,
        ["borderBox"] = MathKind.BorderBox,
        ["d"] = MathKind.Delimiter,
        ["eqArr"] = MathKind.EquationArray,
        ["f"] = MathKind.Fraction,
        ["func"] = MathKind.Function,
        ["groupChr"] = MathKind.GroupCharacter,
        ["limLow"] = MathKind.LowerLimit,
        ["limUpp"] = MathKind.UpperLimit,
        ["m"] = MathKind.Matrix,
        ["nary"] = MathKind.Nary,
        ["phant"] = MathKind.Phantom,
        ["rad"] = MathKind.Radical,
        ["sPre"] = MathKind.PreSubSuperscript,
        ["sSub"] = MathKind.Subscript,
        ["sSup"] = MathKind.Superscript,
        ["sSubSup"] = MathKind.SubSuperscript,
    };

    private static readonly Dictionary<MathKind, string> Names = Kinds.ToDictionary(k => k.Value, k => k.Key);

    /// <summary>The arguments each object may have; any other child is kept as XML in the argument list's place.</summary>
    private static readonly HashSet<string> ArgumentNames = ["e", "num", "den", "sub", "sup", "deg", "lim", "fName"];

    /// <summary>The element name of <paramref name="kind"/> (<c>f</c> for a fraction).</summary>
    public static string NameOf(MathKind kind) => Names[kind];

    /// <summary>
    /// Reads an <c>m:oMathPara</c> or <c>m:oMath</c> element; any other element comes back as <see cref="MathOther"/>.
    /// </summary>
    public static MathNode Read(XElement element)
    {
        if (element.Name == M + "oMathPara")
        {
            MathParagraph paragraph = new();
            foreach (XElement child in element.Elements())
            {
                if (child.Name == M + "oMathParaPr")
                {
                    paragraph.Properties = ReadProperties(child);
                }
                else if (child.Name == M + "oMath")
                {
                    paragraph.Items.Add(new MathZone(ReadRow(child, 1)));
                }
                else
                {
                    paragraph.Items.Add(new MathOther(new XElement(child)));
                }
            }

            return paragraph;
        }

        return element.Name == M + "oMath" ? new MathZone(ReadRow(element, 1)) : new MathOther(new XElement(element));
    }

    /// <summary>Writes a node read by <see cref="Read"/> back as OMML.</summary>
    public static XElement Write(MathNode node)
    {
        switch (node)
        {
            case MathParagraph paragraph:
                XElement para = new(M + "oMathPara");
                if (paragraph.Properties is { } paraProperties)
                {
                    para.Add(WriteProperties("oMathParaPr", paraProperties));
                }

                para.Add(paragraph.Items.Select(Write));
                return para;
            case MathZone zone:
                return WriteRow("oMath", zone.Content);
            case MathOther other:
                return new XElement(other.Element);
            case MathRun run:
                return WriteRun(run);
            case MathObject obj:
                return WriteObject(obj);
            default:
                throw new ArgumentException("Not a math node that has its own element.", nameof(node));
        }
    }

    private static MathRow ReadRow(XElement element, int depth)
    {
        MathRow row = new();
        foreach (XElement child in element.Elements())
        {
            if (child.Name == M + "argPr")
            {
                row.Properties = ReadProperties(child);
                continue;
            }

            row.Items.Add(ReadNode(child, depth));
        }

        return row;
    }

    private static MathNode ReadNode(XElement element, int depth)
    {
        if (element.Name.Namespace != M || depth > MaxDepth)
        {
            return new MathOther(new XElement(element));
        }

        if (element.Name.LocalName == "r")
        {
            return ReadRun(element);
        }

        if (!Kinds.TryGetValue(element.Name.LocalName, out MathKind kind))
        {
            return new MathOther(new XElement(element));
        }

        MathObject obj = new(kind);
        string propertiesName = element.Name.LocalName + "Pr";
        foreach (XElement child in element.Elements())
        {
            string local = child.Name.LocalName;
            if (child.Name.Namespace == M && local == propertiesName && obj.Properties is null && obj.Arguments.Count == 0 && obj.Rows.Count == 0)
            {
                obj.Properties = ReadProperties(child);
            }
            else if (kind == MathKind.Matrix && child.Name == M + "mr")
            {
                obj.Rows.Add([.. child.Elements(M + "e").Select(e => ReadRow(e, depth + 1))]);
            }
            else if (child.Name.Namespace == M && ArgumentNames.Contains(local) && kind != MathKind.Matrix)
            {
                obj.Arguments.Add((local, ReadRow(child, depth + 1)));
            }
            else
            {
                // Something the object does not hold in the schema: kept as an argument of its own name, so it is written back.
                obj.Arguments.Add(("#" + local, new MathRow([new MathOther(new XElement(child))])));
            }
        }

        // A matrix row with anything but cells is kept whole.
        if (kind == MathKind.Matrix && element.Elements(M + "mr").Any(r => r.Elements().Any(e => e.Name != M + "e")))
        {
            return new MathOther(new XElement(element));
        }

        return obj;
    }

    private static MathRun ReadRun(XElement element)
    {
        MathRun run = new();
        foreach (XElement child in element.Elements())
        {
            if (child.Name == M + "rPr" && run.Properties is null && run.Content.Count == 0)
            {
                run.Properties = ReadProperties(child);
            }
            else if (child.Name == M + "t")
            {
                run.Content.Add(child.Value);
                run.Preserve.Add(child.Attribute(XNamespace.Xml + "space")?.Value == "preserve");
            }
            else if (run.Content.Count == 0 && child.Name.Namespace != M && child.Name.LocalName == "rPr")
            {
                run.WordProperties.Add(new XElement(child));
            }
            else
            {
                run.Content.Add(new XElement(child));
            }
        }

        return run;
    }

    /// <summary>Reads a properties element: <c>m:x m:val="…"</c> children as pairs, anything else as XML.</summary>
    public static MathProperties ReadProperties(XElement element)
    {
        MathProperties properties = new();
        foreach (XElement child in element.Elements())
        {
            bool simple = child.Name.Namespace == M && !child.HasElements && child.Value.Length == 0
                && child.Attributes().All(a => a.Name == Val);
            properties.Items.Add(simple ? new MathProperty(child.Name.LocalName, child.Attribute(Val)?.Value) : new XElement(child));
        }

        return properties;
    }

    private static XElement WriteProperties(string name, MathProperties properties)
    {
        XElement element = new(M + name);
        foreach (object item in properties.Items)
        {
            element.Add(item switch
            {
                MathProperty { Value: { } value } p => new XElement(M + p.Name, new XAttribute(Val, value)),
                MathProperty p => new XElement(M + p.Name),
                XElement e => new XElement(e),
                _ => null,
            });
        }

        return element;
    }

    private static XElement WriteRow(string name, MathRow row)
    {
        XElement element = new(M + name);
        if (row.Properties is { } properties)
        {
            element.Add(WriteProperties("argPr", properties));
        }

        element.Add(row.Items.Select(Write));
        return element;
    }

    private static XElement WriteRun(MathRun run)
    {
        XElement element = new(M + "r");
        if (run.Properties is { } properties)
        {
            element.Add(WriteProperties("rPr", properties));
        }

        element.Add(run.WordProperties.Select(e => new XElement(e)));
        int text = 0;
        foreach (object item in run.Content)
        {
            if (item is string value)
            {
                XElement t = new(M + "t", value);
                if (text < run.Preserve.Count && run.Preserve[text])
                {
                    t.Add(new XAttribute(XNamespace.Xml + "space", "preserve"));
                }

                text++;
                element.Add(t);
            }
            else if (item is XElement other)
            {
                element.Add(new XElement(other));
            }
        }

        return element;
    }

    private static XElement WriteObject(MathObject obj)
    {
        string name = Names[obj.Kind];
        XElement element = new(M + name);
        if (obj.Properties is { } properties)
        {
            element.Add(WriteProperties(name + "Pr", properties));
        }

        foreach (List<MathRow> row in obj.Rows)
        {
            element.Add(new XElement(M + "mr", row.Select(cell => WriteRow("e", cell))));
        }

        foreach ((string argument, MathRow row) in obj.Arguments)
        {
            if (argument.StartsWith('#'))
            {
                element.Add(row.Items.OfType<MathOther>().Select(o => new XElement(o.Element)));
            }
            else
            {
                element.Add(WriteRow(argument, row));
            }
        }

        return element;
    }
}
