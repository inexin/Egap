namespace Egap.Pdf;

/// <summary>A text annotation (12.5.6.4): a sticky note shown as an icon, its text in a popup.</summary>
public sealed class PdfTextAnnotation : PdfMarkupAnnotation
{
    internal PdfTextAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new note at a rectangle, usually the icon's size, with optional text.</summary>
    public PdfTextAnnotation(PdfRectangle rectangle, string? contents = null)
        : base("Text", rectangle)
    {
        Contents = contents;
    }

    /// <summary>Whether the note starts open (<c>/Open</c>).</summary>
    public bool IsOpen
    {
        get => Dictionary.GetBool("Open") ?? false;
        set => Dictionary["Open"] = value ? PdfBoolean.True : null;
    }

    /// <summary>
    /// The icon's name (<c>/Name</c>): <c>Comment</c>, <c>Key</c>, <c>Note</c>, <c>Help</c>, <c>NewParagraph</c>,
    /// <c>Paragraph</c>, <c>Insert</c> or another a viewer knows; <c>Note</c> when missing.
    /// </summary>
    public string Icon
    {
        get => Dictionary.GetName("Name")?.Value ?? "Note";
        set
        {
            Dictionary["Name"] = PdfName.Get(value);
            Changed();
        }
    }

    /// <summary>The review state (<c>/State</c>, PDF 1.5), such as <c>Accepted</c> or <c>Completed</c>, on a state reply.</summary>
    public string? State => Dictionary.GetText("State");

    /// <summary>The state model <see cref="State"/> belongs to (<c>/StateModel</c>): <c>Marked</c> or <c>Review</c>.</summary>
    public string? StateModel => Dictionary.GetText("StateModel");
}

/// <summary>A link annotation (12.5.6.5): an area of the page that goes to a destination or runs an action.</summary>
public sealed class PdfLinkAnnotation : PdfAnnotation
{
    internal PdfLinkAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new link over a rectangle to a destination in this document, with no border.</summary>
    public PdfLinkAnnotation(PdfRectangle rectangle, PdfDestination destination)
        : base("Link", rectangle)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Dictionary["Border"] = new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]);
        Destination = destination;
    }

    /// <summary>A new link over a rectangle that runs an action, such as a <see cref="PdfUriAction"/>, with no border.</summary>
    public PdfLinkAnnotation(PdfRectangle rectangle, PdfAction action)
        : base("Link", rectangle)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dictionary["Border"] = new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]);
        Action = action;
    }

    /// <summary>
    /// Where the link goes: its <c>/Dest</c>, or the destination of its go-to action, named destinations looked up. Setting
    /// one replaces any action.
    /// </summary>
    public PdfDestination? Destination
    {
        get
        {
            if (Dictionary["Dest"] is PdfObject dest)
            {
                return Document is not null ? Document.NamedDestinations.Resolve(dest) : PdfDestination.FromArray(dest.Resolve() as PdfArray);
            }

            return Action is PdfGoToAction goTo ? (Document is not null ? goTo.ResolveDestination(Document) : goTo.Destination) : null;
        }

        set
        {
            Dictionary["Dest"] = value?.ToArray();
            if (value is not null)
            {
                Dictionary.Remove("A");
            }
        }
    }

    /// <summary>The action the link runs (<c>/A</c>), or <see langword="null"/>. Setting one replaces any destination.</summary>
    public PdfAction? Action
    {
        get => Dictionary.Get<PdfDictionary>("A") is PdfDictionary action ? PdfAction.FromDictionary(action) : null;
        set
        {
            Dictionary["A"] = value?.Dictionary;
            if (value is not null)
            {
                Dictionary.Remove("Dest");
            }
        }
    }

    /// <summary>How the link looks while pressed (<c>/H</c>); inverted when missing.</summary>
    public PdfHighlightMode HighlightMode
    {
        get => Highlight.Read(Dictionary);
        set => Highlight.Write(Dictionary, value);
    }

    /// <summary>
    /// The quadrilaterals that make up the link (<c>/QuadPoints</c>, PDF 1.6), four points each, for a link that spans
    /// lines; empty when the link is just its rectangle.
    /// </summary>
    public IReadOnlyList<PdfPoint> QuadPoints
    {
        get => ReadPoints("QuadPoints");
        set => WritePoints("QuadPoints", value);
    }
}

/// <summary>A free text annotation (12.5.6.6): text drawn straight on the page, in a box or with a callout line.</summary>
public sealed class PdfFreeTextAnnotation : PdfMarkupAnnotation
{
    internal PdfFreeTextAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>
    /// A new free text annotation in a rectangle. The default appearance names the font by its resource name in the
    /// form's default resources, its size and the text colour.
    /// </summary>
    public PdfFreeTextAnnotation(PdfRectangle rectangle, string contents, string defaultAppearance = "/Helv 12 Tf 0 g")
        : base("FreeText", rectangle)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(defaultAppearance);
        Contents = contents;
        DefaultAppearance = defaultAppearance;
    }

    /// <summary>The operators that set the text's font, size and colour (<c>/DA</c>), such as <c>/Helv 12 Tf 0 g</c>.</summary>
    public string? DefaultAppearance
    {
        get => Dictionary.GetText("DA");
        set
        {
            Dictionary["DA"] = value is null ? null : PdfString.FromText(value);
            Changed();
        }
    }

    /// <summary>How the text is aligned (<c>/Q</c>); left when missing.</summary>
    public PdfTextAlignment Alignment
    {
        get => Dictionary.GetInt("Q") switch
        {
            1 => PdfTextAlignment.Center,
            2 => PdfTextAlignment.Right,
            _ => PdfTextAlignment.Left,
        };
        set
        {
            Dictionary["Q"] = value == PdfTextAlignment.Left ? null : new PdfInteger((int)value);
            Changed();
        }
    }

    /// <summary>The default style of the rich text (<c>/DS</c>, a CSS declaration list).</summary>
    public string? DefaultStyle
    {
        get => Dictionary.GetText("DS");
        set => Dictionary["DS"] = value is null ? null : PdfString.FromText(value);
    }

    /// <summary>The callout line's two or three points (<c>/CL</c>, PDF 1.6), from the point it marks to the box.</summary>
    public IReadOnlyList<PdfPoint> CalloutLine
    {
        get => ReadPoints("CL");
        set => WritePoints("CL", value);
    }

    /// <summary>The ending at the start of the callout line (<c>/LE</c>).</summary>
    public PdfLineEnding CalloutEnding
    {
        get => Ending(Dictionary.GetName("LE"));
        set => Dictionary["LE"] = PdfName.Get(value.ToString());
    }
}

/// <summary>A line annotation (12.5.6.7): a straight line with optional endings and caption.</summary>
public sealed class PdfLineAnnotation : PdfMarkupAnnotation
{
    internal PdfLineAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new line between two points; its rectangle is their bounds with a margin.</summary>
    public PdfLineAnnotation(PdfPoint start, PdfPoint end)
        : base("Line", Bounds([start, end]))
    {
        WritePoints("L", [start, end]);
    }

    /// <summary>Where the line starts (<c>/L</c>); the origin when missing.</summary>
    public PdfPoint Start
    {
        get => ReadPoints("L") is { Count: 2 } line ? line[0] : default;
        set
        {
            WritePoints("L", [value, End]);
            Changed();
        }
    }

    /// <summary>Where the line ends (<c>/L</c>); the origin when missing.</summary>
    public PdfPoint End
    {
        get => ReadPoints("L") is { Count: 2 } line ? line[1] : default;
        set
        {
            WritePoints("L", [Start, value]);
            Changed();
        }
    }

    /// <summary>The ending at the start (the first name of <c>/LE</c>).</summary>
    public PdfLineEnding StartEnding
    {
        get => EndingAt(0);
        set
        {
            Dictionary["LE"] = new PdfArray([PdfName.Get(value.ToString()), PdfName.Get(EndEnding.ToString())]);
            Changed();
        }
    }

    /// <summary>The ending at the end (the second name of <c>/LE</c>).</summary>
    public PdfLineEnding EndEnding
    {
        get => EndingAt(1);
        set
        {
            Dictionary["LE"] = new PdfArray([PdfName.Get(StartEnding.ToString()), PdfName.Get(value.ToString())]);
            Changed();
        }
    }

    /// <summary>The colour the endings are filled with (<c>/IC</c>); <see langword="null"/> for none.</summary>
    public PdfRgbColor? InteriorColor
    {
        get => ReadColor("IC");
        set
        {
            Dictionary["IC"] = value?.ToArray();
            Changed();
        }
    }

    /// <summary>Whether <see cref="PdfAnnotation.Contents"/> is shown as a caption on the line (<c>/Cap</c>, PDF 1.6).</summary>
    public bool ShowsCaption
    {
        get => Dictionary.GetBool("Cap") ?? false;
        set => Dictionary["Cap"] = value ? PdfBoolean.True : null;
    }
}

/// <summary>A square annotation (12.5.6.8): a rectangle drawn inside the annotation's rectangle.</summary>
public sealed class PdfSquareAnnotation : PdfMarkupAnnotation
{
    internal PdfSquareAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new rectangle filling a rectangle.</summary>
    public PdfSquareAnnotation(PdfRectangle rectangle)
        : base("Square", rectangle)
    {
    }

    /// <summary>The fill colour (<c>/IC</c>); <see langword="null"/> for no fill.</summary>
    public PdfRgbColor? InteriorColor
    {
        get => ReadColor("IC");
        set
        {
            Dictionary["IC"] = value?.ToArray();
            Changed();
        }
    }
}

/// <summary>A circle annotation (12.5.6.8): an ellipse drawn inside the annotation's rectangle.</summary>
public sealed class PdfCircleAnnotation : PdfMarkupAnnotation
{
    internal PdfCircleAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new ellipse filling a rectangle.</summary>
    public PdfCircleAnnotation(PdfRectangle rectangle)
        : base("Circle", rectangle)
    {
    }

    /// <summary>The fill colour (<c>/IC</c>); <see langword="null"/> for no fill.</summary>
    public PdfRgbColor? InteriorColor
    {
        get => ReadColor("IC");
        set
        {
            Dictionary["IC"] = value?.ToArray();
            Changed();
        }
    }
}

/// <summary>A polygon annotation (12.5.6.9): a closed shape through its vertices.</summary>
public sealed class PdfPolygonAnnotation : PdfMarkupAnnotation
{
    internal PdfPolygonAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new polygon through vertices; its rectangle is their bounds with a margin.</summary>
    public PdfPolygonAnnotation(IReadOnlyList<PdfPoint> vertices)
        : base("Polygon", Bounds(vertices))
    {
        Vertices = vertices;
    }

    /// <summary>The vertices (<c>/Vertices</c>).</summary>
    public IReadOnlyList<PdfPoint> Vertices
    {
        get => ReadPoints("Vertices");
        set
        {
            WritePoints("Vertices", value);
            Changed();
        }
    }

    /// <summary>The fill colour (<c>/IC</c>); <see langword="null"/> for no fill.</summary>
    public PdfRgbColor? InteriorColor
    {
        get => ReadColor("IC");
        set
        {
            Dictionary["IC"] = value?.ToArray();
            Changed();
        }
    }
}

/// <summary>A polyline annotation (12.5.6.9): an open line through its vertices, with optional endings.</summary>
public sealed class PdfPolyLineAnnotation : PdfMarkupAnnotation
{
    internal PdfPolyLineAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new polyline through vertices; its rectangle is their bounds with a margin.</summary>
    public PdfPolyLineAnnotation(IReadOnlyList<PdfPoint> vertices)
        : base("PolyLine", Bounds(vertices))
    {
        Vertices = vertices;
    }

    /// <summary>The vertices (<c>/Vertices</c>).</summary>
    public IReadOnlyList<PdfPoint> Vertices
    {
        get => ReadPoints("Vertices");
        set
        {
            WritePoints("Vertices", value);
            Changed();
        }
    }

    /// <summary>The ending at the first vertex (the first name of <c>/LE</c>).</summary>
    public PdfLineEnding StartEnding
    {
        get => EndingAt(0);
        set
        {
            Dictionary["LE"] = new PdfArray([PdfName.Get(value.ToString()), PdfName.Get(EndEnding.ToString())]);
            Changed();
        }
    }

    /// <summary>The ending at the last vertex (the second name of <c>/LE</c>).</summary>
    public PdfLineEnding EndEnding
    {
        get => EndingAt(1);
        set
        {
            Dictionary["LE"] = new PdfArray([PdfName.Get(StartEnding.ToString()), PdfName.Get(value.ToString())]);
            Changed();
        }
    }

    /// <summary>The colour the endings are filled with (<c>/IC</c>); <see langword="null"/> for none.</summary>
    public PdfRgbColor? InteriorColor
    {
        get => ReadColor("IC");
        set
        {
            Dictionary["IC"] = value?.ToArray();
            Changed();
        }
    }
}

/// <summary>
/// A text markup annotation (12.5.6.10): highlight, underline, squiggly underline or strikeout over text, given as
/// quadrilaterals.
/// </summary>
public abstract class PdfTextMarkupAnnotation : PdfMarkupAnnotation
{
    private protected PdfTextMarkupAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    private protected PdfTextMarkupAnnotation(string subtype, IReadOnlyList<PdfPoint> quadPoints)
        : base(subtype, Bounds(Quads(quadPoints), 0))
    {
        QuadPoints = quadPoints;
    }

    /// <summary>
    /// The quadrilaterals marked (<c>/QuadPoints</c>), four points each, in the order Acrobat writes them: upper left, upper
    /// right, lower left, lower right.
    /// </summary>
    public IReadOnlyList<PdfPoint> QuadPoints
    {
        get => ReadPoints("QuadPoints");
        set
        {
            WritePoints("QuadPoints", Quads(value));
            Changed();
        }
    }

    /// <summary>The four corners of a rectangle as one quadrilateral, upper left first, as Acrobat orders them.</summary>
    public static IReadOnlyList<PdfPoint> Quad(PdfRectangle area)
    {
        PdfRectangle r = area.Normalize();
        return [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom)];
    }

    private static IReadOnlyList<PdfPoint> Quads(IReadOnlyList<PdfPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0 || points.Count % 4 != 0)
        {
            throw new ArgumentException("Quadrilaterals take four points each.", nameof(points));
        }

        return points;
    }
}

/// <summary>A highlight annotation (12.5.6.10): text marked as with a highlighter.</summary>
public sealed class PdfHighlightAnnotation : PdfTextMarkupAnnotation
{
    internal PdfHighlightAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new highlight over quadrilaterals, four points each; see <see cref="PdfTextMarkupAnnotation.Quad"/>.</summary>
    public PdfHighlightAnnotation(IReadOnlyList<PdfPoint> quadPoints)
        : base("Highlight", quadPoints)
    {
    }
}

/// <summary>An underline annotation (12.5.6.10).</summary>
public sealed class PdfUnderlineAnnotation : PdfTextMarkupAnnotation
{
    internal PdfUnderlineAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new underline under quadrilaterals, four points each; see <see cref="PdfTextMarkupAnnotation.Quad"/>.</summary>
    public PdfUnderlineAnnotation(IReadOnlyList<PdfPoint> quadPoints)
        : base("Underline", quadPoints)
    {
    }
}

/// <summary>A squiggly underline annotation (12.5.6.10, PDF 1.4).</summary>
public sealed class PdfSquigglyAnnotation : PdfTextMarkupAnnotation
{
    internal PdfSquigglyAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new squiggly underline under quadrilaterals, four points each; see <see cref="PdfTextMarkupAnnotation.Quad"/>.</summary>
    public PdfSquigglyAnnotation(IReadOnlyList<PdfPoint> quadPoints)
        : base("Squiggly", quadPoints)
    {
    }
}

/// <summary>A strikeout annotation (12.5.6.10).</summary>
public sealed class PdfStrikeOutAnnotation : PdfTextMarkupAnnotation
{
    internal PdfStrikeOutAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new strikeout through quadrilaterals, four points each; see <see cref="PdfTextMarkupAnnotation.Quad"/>.</summary>
    public PdfStrikeOutAnnotation(IReadOnlyList<PdfPoint> quadPoints)
        : base("StrikeOut", quadPoints)
    {
    }
}

/// <summary>A caret annotation (12.5.6.11, PDF 1.5): marks where text is to be inserted.</summary>
public sealed class PdfCaretAnnotation : PdfMarkupAnnotation
{
    internal PdfCaretAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new caret in a rectangle.</summary>
    public PdfCaretAnnotation(PdfRectangle rectangle)
        : base("Caret", rectangle)
    {
    }

    /// <summary>Whether a paragraph symbol goes with the caret (<c>/Sy /P</c>).</summary>
    public bool ShowsParagraphSymbol
    {
        get => Dictionary.GetName("Sy")?.Value == "P";
        set => Dictionary["Sy"] = value ? PdfName.Get("P") : null;
    }
}

/// <summary>A rubber stamp annotation (12.5.6.12): a stamp such as Approved or Draft.</summary>
public sealed class PdfStampAnnotation : PdfMarkupAnnotation
{
    internal PdfStampAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new stamp in a rectangle with one of the standard icons, such as <c>Approved</c>.</summary>
    public PdfStampAnnotation(PdfRectangle rectangle, string icon = "Draft")
        : base("Stamp", rectangle)
    {
        Icon = icon;
    }

    /// <summary>
    /// The stamp's name (<c>/Name</c>): <c>Approved</c>, <c>Experimental</c>, <c>NotApproved</c>, <c>AsIs</c>,
    /// <c>Expired</c>, <c>NotForPublicRelease</c>, <c>Confidential</c>, <c>Final</c>, <c>Sold</c>, <c>Departmental</c>,
    /// <c>ForComment</c>, <c>TopSecret</c>, <c>Draft</c>, <c>ForPublicRelease</c> or another; <c>Draft</c> when missing.
    /// </summary>
    public string Icon
    {
        get => Dictionary.GetName("Name")?.Value ?? "Draft";
        set
        {
            Dictionary["Name"] = PdfName.Get(value);
            Changed();
        }
    }
}

/// <summary>An ink annotation (12.5.6.13): freehand strokes.</summary>
public sealed class PdfInkAnnotation : PdfMarkupAnnotation
{
    internal PdfInkAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new ink annotation of strokes; its rectangle is their bounds with a margin.</summary>
    public PdfInkAnnotation(IReadOnlyList<IReadOnlyList<PdfPoint>> strokes)
        : base("Ink", Bounds(strokes?.SelectMany(s => s) ?? throw new ArgumentNullException(nameof(strokes))))
    {
        Strokes = strokes;
    }

    /// <summary>The strokes (<c>/InkList</c>), each a list of points; entries that are not arrays are left out.</summary>
    public IReadOnlyList<IReadOnlyList<PdfPoint>> Strokes
    {
        get => Dictionary.Get<PdfArray>("InkList") is PdfArray list
            ? [.. Enumerable.Range(0, list.Count).Select(list.Get<PdfArray>).OfType<PdfArray>().Select(PdfPoint.FromArray)]
            : [];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Dictionary["InkList"] = new PdfArray(value.Select(stroke => (PdfObject)PdfPoint.ToArray(stroke)));
            Changed();
        }
    }
}

/// <summary>A popup annotation (12.5.6.14): the window that shows a markup annotation's text.</summary>
public sealed class PdfPopupAnnotation : PdfAnnotation
{
    internal PdfPopupAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>The annotation whose text the popup shows (<c>/Parent</c>).</summary>
    public PdfAnnotation? Parent => Linked("Parent");

    /// <summary>Whether the popup starts open (<c>/Open</c>).</summary>
    public bool IsOpen
    {
        get => Dictionary.GetBool("Open") ?? false;
        set => Dictionary["Open"] = value ? PdfBoolean.True : null;
    }
}

/// <summary>A file attachment annotation (12.5.6.15): a file embedded at a point on the page, shown as an icon.</summary>
public sealed class PdfFileAttachmentAnnotation : PdfMarkupAnnotation
{
    internal PdfFileAttachmentAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>
    /// The attached file (<c>/FS</c>), named by its file name; <see langword="null"/> when the file specification is a plain
    /// string or missing. Add one with <see cref="PdfAnnotationCollection.AddFileAttachment"/>.
    /// </summary>
    public PdfAttachment? Attachment
    {
        get
        {
            if (Dictionary.Get<PdfDictionary>("FS") is not PdfDictionary spec)
            {
                return null;
            }

            string name = spec.GetText("UF") ?? spec.GetText("F") ?? "";
            return new PdfAttachment(name, spec, Dictionary["FS"] as PdfReference);
        }
    }

    /// <summary>
    /// The icon's name (<c>/Name</c>): <c>Graph</c>, <c>PushPin</c>, <c>Paperclip</c>, <c>Tag</c> or another;
    /// <c>PushPin</c> when missing.
    /// </summary>
    public string Icon
    {
        get => Dictionary.GetName("Name")?.Value ?? "PushPin";
        set => Dictionary["Name"] = PdfName.Get(value);
    }
}

/// <summary>
/// A redaction annotation (12.5.6.23, PDF 1.7): marks content to be removed. Applying redactions changes page content,
/// which is layer 3; Phase 2 reads and writes the annotation only.
/// </summary>
public sealed class PdfRedactAnnotation : PdfMarkupAnnotation
{
    internal PdfRedactAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>A new redaction of a rectangle.</summary>
    public PdfRedactAnnotation(PdfRectangle rectangle)
        : base("Redact", rectangle)
    {
    }

    /// <summary>The quadrilaterals to remove (<c>/QuadPoints</c>), four points each; empty when it is the rectangle.</summary>
    public IReadOnlyList<PdfPoint> QuadPoints
    {
        get => ReadPoints("QuadPoints");
        set => WritePoints("QuadPoints", value);
    }

    /// <summary>The colour that fills the area once redacted (<c>/IC</c>); <see langword="null"/> for none.</summary>
    public PdfRgbColor? InteriorColor
    {
        get => ReadColor("IC");
        set => Dictionary["IC"] = value?.ToArray();
    }

    /// <summary>The text drawn over the area once redacted (<c>/OverlayText</c>).</summary>
    public string? OverlayText
    {
        get => Dictionary.GetText("OverlayText");
        set => Dictionary["OverlayText"] = value is null ? null : PdfString.FromText(value);
    }
}

/// <summary>
/// A widget annotation (12.5.6.19): how a form field appears on a page. The field it belongs to comes with form reading.
/// </summary>
public sealed class PdfWidgetAnnotation : PdfAnnotation
{
    internal PdfWidgetAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
        : base(dictionary, reference, document)
    {
    }

    /// <summary>How the widget looks while pressed (<c>/H</c>); inverted when missing.</summary>
    public PdfHighlightMode HighlightMode
    {
        get => Highlight.Read(Dictionary);
        set => Highlight.Write(Dictionary, value);
    }

    /// <summary>The action the widget runs when activated (<c>/A</c>), or <see langword="null"/>.</summary>
    public PdfAction? Action
    {
        get => Dictionary.Get<PdfDictionary>("A") is PdfDictionary action ? PdfAction.FromDictionary(action) : null;
        set => Dictionary["A"] = value?.Dictionary;
    }
}

/// <summary>How a link or widget looks while pressed (12.5.6.5, table 176).</summary>
public enum PdfHighlightMode
{
    /// <summary>No change, <c>/N</c>.</summary>
    None,

    /// <summary>The area's colours inverted, <c>/I</c>.</summary>
    Invert,

    /// <summary>The border inverted, <c>/O</c>.</summary>
    Outline,

    /// <summary>The area appears pushed in, <c>/P</c>; a widget shows its down appearance.</summary>
    Push,

    /// <summary>The widget's down appearance, <c>/T</c> (toggle), for widgets only.</summary>
    Toggle,
}

/// <summary>How text is aligned in a free text annotation or a field (<c>/Q</c>).</summary>
public enum PdfTextAlignment
{
    /// <summary>Left aligned.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right aligned.</summary>
    Right,
}

/// <summary>Reads and writes <c>/H</c>.</summary>
internal static class Highlight
{
    public static PdfHighlightMode Read(PdfDictionary dictionary)
    {
        return dictionary.GetName("H")?.Value switch
        {
            "N" => PdfHighlightMode.None,
            "O" => PdfHighlightMode.Outline,
            "P" => PdfHighlightMode.Push,
            "T" => PdfHighlightMode.Toggle,
            _ => PdfHighlightMode.Invert,
        };
    }

    public static void Write(PdfDictionary dictionary, PdfHighlightMode mode)
    {
        dictionary["H"] = mode switch
        {
            PdfHighlightMode.None => PdfName.Get("N"),
            PdfHighlightMode.Outline => PdfName.Get("O"),
            PdfHighlightMode.Push => PdfName.Get("P"),
            PdfHighlightMode.Toggle => PdfName.Get("T"),
            _ => null,
        };
    }
}
