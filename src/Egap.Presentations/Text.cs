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
        foreach (XElement paragraph in element.Elements(PptxNames.A + "p"))
        {
            paragraphs.Add(new TextParagraph(this, paragraph));
        }
    }

    /// <summary>The shape the text is in.</summary>
    public SlideShape Shape { get; }

    /// <summary>The paragraphs, in order.</summary>
    public IReadOnlyList<TextParagraph> Paragraphs => paragraphs;

    /// <summary>
    /// The text of every paragraph, each ended by <see cref="ControlChar.ParagraphBreak"/> but the last, with line breaks as
    /// <see cref="ControlChar.LineBreak"/>, as Egap.Documents' text is.
    /// </summary>
    public string Text => string.Join(ControlChar.ParagraphBreak, paragraphs.Select(p => p.Text));

    internal XElement Element { get; }
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

            Paragraph.Body.Shape.Changed();
        }
    }

    /// <summary>Whether the run is a field (<c>a:fld</c>).</summary>
    public bool IsField => Element.Name == PptxNames.A + "fld";

    /// <summary>The field's type (<c>a:fld/@type</c>), such as <c>slidenum</c> or <c>datetime1</c>; null for a plain run.</summary>
    public string? FieldType => IsField ? (string?)Element.Attribute("type") : null;

    internal XElement Element { get; }
}
