using System.Text;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// A shape's text (<c>p:txBody</c>): its paragraphs and their runs. The body's and paragraphs' properties, bullets and
/// styles are kept as they are.
/// </summary>
public sealed class TextBody
{
    private readonly List<TextParagraph> paragraphs = [];

    internal TextBody(SlideShape shape, XElement element)
    {
        Shape = shape;
        Element = element;
        Read();
    }

    /// <summary>The shape the text is in: for a table cell's text, the table's frame.</summary>
    public SlideShape Shape { get; }

    /// <summary>The paragraphs, in order.</summary>
    public IReadOnlyList<TextParagraph> Paragraphs => paragraphs;

    /// <summary>
    /// The text of every paragraph, each ended by <see cref="ControlChar.ParagraphBreak"/> but the last, with line breaks as
    /// <see cref="ControlChar.LineBreak"/>, as Egap.Documents' text is.
    /// </summary>
    /// <remarks>
    /// Setting it replaces the paragraphs: one for each part between paragraph breaks, with <c>a:br</c> for line breaks. The
    /// new paragraphs take the first paragraph's properties and the first run's formatting, so a title keeps its look.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The text set is null.</exception>
    public string Text
    {
        get => string.Join(ControlChar.ParagraphBreak, paragraphs.Select(p => p.Text));
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == Text)
            {
                return;
            }

            XElement? first = Element.Element(PptxNames.A + "p");
            XElement? properties = first?.Element(PptxNames.A + "pPr");
            XElement? format = first?.Elements().FirstOrDefault(e => e.Name == PptxNames.A + "r" || e.Name == PptxNames.A + "fld")?.Element(PptxNames.A + "rPr")
                ?? first?.Element(PptxNames.A + "endParaRPr");
            List<XElement> made = [.. NewParagraphs(value, properties, format)];
            Element.Elements(PptxNames.A + "p").Remove();
            Element.Add(made);
            Read();
            Shape.Changed();
        }
    }

    internal XElement Element { get; }

    internal void Changed() => Shape.Changed();

    /// <summary>A new text body (<c>p:txBody</c>, or <c>a:txBody</c> in a table cell) holding <paramref name="text"/>.</summary>
    internal static XElement NewElement(string text, XName? name = null, XElement? bodyProperties = null)
    {
        return new XElement(name ?? PptxNames.P + "txBody", bodyProperties ?? new XElement(PptxNames.A + "bodyPr"), new XElement(PptxNames.A + "lstStyle"),
            NewParagraphs(text, null, null));
    }

    /// <summary>Paragraphs for <paramref name="text"/>, each with a copy of the paragraph properties and run formatting given.</summary>
    internal static IEnumerable<XElement> NewParagraphs(string text, XElement? properties, XElement? format)
    {
        foreach (string paragraph in text.Split(ControlChar.ParagraphBreak))
        {
            XElement p = new(PptxNames.A + "p");
            if (properties is not null)
            {
                p.Add(new XElement(properties));
            }

            string[] lines = paragraph.Split(ControlChar.LineBreak);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    p.Add(new XElement(PptxNames.A + "br", RunProperties(format)));
                }

                if (lines[i].Length > 0)
                {
                    p.Add(new XElement(PptxNames.A + "r", RunProperties(format), new XElement(PptxNames.A + "t", lines[i])));
                }
            }

            XElement end = RunProperties(format);
            end.Name = PptxNames.A + "endParaRPr";
            p.Add(end);
            yield return p;
        }
    }

    private static XElement RunProperties(XElement? format)
    {
        XElement properties = format is null ? new XElement(PptxNames.A + "rPr", new XAttribute("lang", "en-US")) : new XElement(format);
        properties.Name = PptxNames.A + "rPr";
        properties.Attribute("dirty")?.Remove();
        return properties;
    }

    private void Read()
    {
        paragraphs.Clear();
        foreach (XElement paragraph in Element.Elements(PptxNames.A + "p"))
        {
            paragraphs.Add(new TextParagraph(this, paragraph));
        }
    }
}

/// <summary>A paragraph of a shape's text (<c>a:p</c>): its runs and line breaks, and its outline level.</summary>
public sealed class TextParagraph
{
    private readonly List<TextRun> runs = [];

    internal TextParagraph(TextBody body, XElement element)
    {
        Body = body;
        Element = element;
        foreach (XElement child in element.Elements())
        {
            if (child.Name == PptxNames.A + "r" || child.Name == PptxNames.A + "fld")
            {
                runs.Add(new TextRun(this, child));
            }
        }
    }

    /// <summary>The text the paragraph is in.</summary>
    public TextBody Body { get; }

    /// <summary>The runs of text and the fields (slide number, date), in order; line breaks are between them.</summary>
    public IReadOnlyList<TextRun> Runs => runs;

    /// <summary>The paragraph's outline level (<c>a:pPr/@lvl</c>), from 0 for the first level to 8.</summary>
    public int Level => (int)Math.Clamp(PptxNames.Long(Element.Element(PptxNames.A + "pPr"), "lvl") ?? 0, 0, 8);

    /// <summary>The paragraph's text, its line breaks (<c>a:br</c>) as <see cref="ControlChar.LineBreak"/>.</summary>
    public string Text
    {
        get
        {
            StringBuilder text = new();
            foreach (XElement child in Element.Elements())
            {
                if (child.Name == PptxNames.A + "br")
                {
                    text.Append(ControlChar.LineBreak);
                }
                else if (child.Name == PptxNames.A + "r" || child.Name == PptxNames.A + "fld")
                {
                    text.Append((string?)child.Element(PptxNames.A + "t"));
                }
            }

            return text.ToString();
        }
    }

    internal XElement Element { get; }
}

/// <summary>A run of text with one formatting (<c>a:r</c>), or a field PowerPoint fills in, such as the slide number (<c>a:fld</c>).</summary>
public sealed class TextRun
{
    internal TextRun(TextParagraph paragraph, XElement element)
    {
        Paragraph = paragraph;
        Element = element;
    }

    /// <summary>The paragraph the run is in.</summary>
    public TextParagraph Paragraph { get; }

    /// <summary>
    /// The run's text (<c>a:t</c>). Setting it keeps the run's formatting. A field's text is what it showed when the file was
    /// saved; PowerPoint fills it in again.
    /// </summary>
    /// <exception cref="ArgumentNullException">The text set is null.</exception>
    public string Text
    {
        get => (string?)Element.Element(PptxNames.A + "t") ?? "";
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == Text)
            {
                return;
            }

            XElement? text = Element.Element(PptxNames.A + "t");
            if (text is null)
            {
                Element.Add(new XElement(PptxNames.A + "t", value));
            }
            else
            {
                text.Value = value;
            }

            Paragraph.Body.Changed();
        }
    }

    /// <summary>Whether the run is a field (<c>a:fld</c>).</summary>
    public bool IsField => Element.Name == PptxNames.A + "fld";

    /// <summary>The field's type (<c>a:fld/@type</c>), such as <c>slidenum</c> or <c>datetime1</c>; null for a plain run.</summary>
    public string? FieldType => IsField ? (string?)Element.Attribute("type") : null;

    internal XElement Element { get; }
}
