using System.Globalization;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.DrawingML;
using Egap.Presentations.Pptx;

namespace Egap.Presentations.Drawing;

/// <summary>What fields show on the page drawn: its slide number, or null on a layout or master, and the date.</summary>
internal sealed record FieldValues(int? SlideNumber, DateTime Date);

/// <summary>
/// A shape's text with everything it inherits, in the order PowerPoint looks a setting up (ECMA-376 Part 1, 19.3.1 and 21.1.2):
/// the paragraph's own properties; the list styles of the shape's text body, its layout placeholder's and its master
/// placeholder's; the shape style's font reference; the master's title, body or other text style; and the presentation's
/// default text style. Body properties follow the text bodies alone.
/// </summary>
internal sealed class TextFrame
{
    private static readonly XNamespace A = PptxNames.A;

    private readonly XElement?[]?[] levels = new XElement?[]?[9];
    private readonly XElement? fontReference;

    public TextFrame(ShapeLook look, XElement? masterStyle, XElement? defaultStyle, Theme theme, SlideFonts fonts, FieldValues fields)
    {
        Bodies = look.TextBodies;
        MasterStyle = masterStyle;
        DefaultStyle = defaultStyle;
        Theme = theme;
        Fonts = fonts;
        Fields = fields;
        BodyProperties = [.. Bodies.Select(b => b.Element(A + "bodyPr")).OfType<XElement>()];
        fontReference = FontReference(look.Style?.Element(A + "fontRef"));
    }

    /// <summary>The picture a bullet's <c>a:blip</c> names by relationship id, from the part that holds the element; null when there is none.</summary>
    public Func<XElement, string, BlipImage?>? Pictures { get; init; }

    /// <summary>The text bodies, the shape's own first.</summary>
    public List<XElement> Bodies { get; }

    /// <summary>The master's text style for the shape's kind of placeholder, or null.</summary>
    public XElement? MasterStyle { get; }

    /// <summary>The presentation's default text style (<c>p:defaultTextStyle</c>), or null.</summary>
    public XElement? DefaultStyle { get; }

    public Theme Theme { get; }

    public SlideFonts Fonts { get; }

    public FieldValues Fields { get; }

    /// <summary>The body properties (<c>a:bodyPr</c>) down the chain, the shape's own first.</summary>
    public List<XElement> BodyProperties { get; }

    /// <summary>Whether the text stays level when the shape turns (<c>upright</c>).</summary>
    public bool Upright => BodyAttribute("upright") is "1" or "true";

    /// <summary>The paragraphs: the shape's own text body's.</summary>
    public IEnumerable<XElement> Paragraphs => Bodies.Count > 0 ? Bodies[0].Elements(A + "p") : [];

    /// <summary>A body property attribute, the first the chain sets.</summary>
    public string? BodyAttribute(string name)
    {
        foreach (XElement properties in BodyProperties)
        {
            if ((string?)properties.Attribute(name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>A body property attribute as a number, the first the chain sets.</summary>
    public double? BodyNumber(string name) =>
        double.TryParse(BodyAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    /// <summary>The autofit setting (<c>a:normAutofit</c>, <c>a:spAutoFit</c> or <c>a:noAutofit</c>), the first the chain has.</summary>
    public XElement? Autofit
    {
        get
        {
            foreach (XElement properties in BodyProperties)
            {
                if (properties.Elements().FirstOrDefault(e => e.Name.LocalName is "normAutofit" or "spAutoFit" or "noAutofit" && e.Name.Namespace == A) is { } fit)
                {
                    return fit;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The paragraph property elements a paragraph at <paramref name="level"/> (0 to 8) looks its settings up in, its own
    /// <paramref name="own"/> first; each element's <c>a:defRPr</c> gives the run defaults at that step.
    /// </summary>
    public XElement?[] Chain(XElement? own, int level)
    {
        XElement?[] inherited = levels[level] ??= Inherited(level);
        XElement?[] chain = new XElement?[inherited.Length + 1];
        chain[0] = own;
        inherited.CopyTo(chain, 1);
        return chain;
    }

    private XElement?[] Inherited(int level)
    {
        XName name = A + $"lvl{level + 1}pPr";
        List<XElement?> chain = [];
        foreach (XElement body in Bodies)
        {
            XElement? list = body.Element(A + "lstStyle");
            chain.Add(list?.Element(name));
            chain.Add(list?.Element(A + "defPPr"));
        }

        chain.Add(fontReference);
        chain.Add(MasterStyle?.Element(name));
        chain.Add(MasterStyle?.Element(A + "defPPr"));
        chain.Add(DefaultStyle?.Element(name));
        chain.Add(DefaultStyle?.Element(A + "defPPr"));
        return [.. chain.Where(e => e is not null)];
    }

    /// <summary>
    /// A shape style's font reference (<c>a:fontRef</c>) as run defaults: the theme's major or minor fonts and the
    /// reference's colour, ranked below the shape's list styles and above the master's text styles.
    /// </summary>
    private static XElement? FontReference(XElement? reference)
    {
        if (reference is null)
        {
            return null;
        }

        XElement defaults = new(A + "defRPr");
        string? kind = (string?)reference.Attribute("idx") switch
        {
            "major" => "mj",
            "minor" => "mn",
            _ => null,
        };
        if (kind is not null)
        {
            defaults.Add(
                new XElement(A + "latin", new XAttribute("typeface", $"+{kind}-lt")),
                new XElement(A + "ea", new XAttribute("typeface", $"+{kind}-ea")),
                new XElement(A + "cs", new XAttribute("typeface", $"+{kind}-cs")));
        }

        if (reference.Elements().FirstOrDefault() is { } color)
        {
            defaults.AddFirst(new XElement(A + "solidFill", new XElement(color)));
        }

        return new XElement(A + "pPr", defaults);
    }
}
