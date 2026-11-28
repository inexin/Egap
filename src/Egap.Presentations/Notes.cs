using System.Xml.Linq;

using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>A slide's notes page (<c>p:notes</c>): the speaker's notes and a picture of the slide, laid out by the notes master.</summary>
public sealed class NotesSlide : SlideBase
{
    internal NotesSlide(Presentation presentation, XmlPart xml, Slide slide)
        : base(presentation, xml)
    {
        Slide = slide;
    }

    /// <summary>The slide the notes are for.</summary>
    public Slide Slide { get; }

    /// <summary>The notes master the page follows, or null when the presentation has none.</summary>
    public NotesMaster? Master => Presentation.NotesMaster;

    /// <summary>The notes text (the body placeholder's), or null when the page has no body placeholder.</summary>
    public TextBody? NotesBody => Body?.TextBody;

    /// <inheritdoc/>
    public override ColorMap ColorMap => Xml.Root.Element(PptxNames.P + "clrMapOvr")?.Element(PptxNames.A + "overrideClrMapping") is { } map
        ? new ColorMap(map)
        : Master?.ColorMap ?? ColorMap.Default;

    internal AutoShape? Body => Shapes.OfType<AutoShape>().FirstOrDefault(s => s.Placeholder?.Type == PlaceholderType.Body && s.TextBody is not null);
}

/// <summary>The notes master (<c>p:notesMaster</c>): the layout and text styles of every notes page.</summary>
public sealed class NotesMaster : SlideBase
{
    internal NotesMaster(Presentation presentation, XmlPart xml)
        : base(presentation, xml)
    {
    }

    /// <inheritdoc/>
    public override ColorMap ColorMap => Xml.Root.Element(PptxNames.P + "clrMap") is { } map ? new ColorMap(map) : ColorMap.Default;
}

/// <summary>The notes of a slide.</summary>
public sealed partial class Slide
{
    private NotesSlide? notes;

    /// <summary>The slide's notes page, or null when it has none; setting <see cref="NotesText"/> makes one.</summary>
    public NotesSlide? Notes
    {
        get
        {
            PackagePart? part = Presentation.Package.Related(PartName, PptxNames.NotesSlideRelationship);
            if (part is null)
            {
                return notes = null;
            }

            return notes is not null && notes.Xml.Part == part ? notes : notes = new NotesSlide(Presentation, Presentation.Package.Xml(part), this);
        }
    }

    /// <summary>
    /// The speaker's notes, paragraphs ended by <see cref="Documents.ControlChar.ParagraphBreak"/>; empty when the slide has none.
    /// Setting it on a slide without notes makes a notes page, and a notes master too when the presentation has none.
    /// </summary>
    /// <exception cref="ArgumentNullException">The text set is null.</exception>
    public string NotesText
    {
        get => Notes?.NotesBody?.Text ?? "";
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Notes is { } page)
            {
                if (page.NotesBody is { } body)
                {
                    body.Text = value;
                    return;
                }

                page.Shapes.Append(NotesPlaceholder(page, value));
                return;
            }

            if (value.Length > 0)
            {
                AddNotes(value);
            }
        }
    }

    private void AddNotes(string text)
    {
        PptxPackage package = Presentation.Package;
        PackagePart master = Presentation.NotesMasterPart ?? DefaultTemplate.AddNotesMaster(Presentation);
        string name = package.UniqueName("/ppt/notesSlides/notesSlide", ".xml");
        package.AddXmlPart(name, DefaultTemplate.Ct + "notesSlide+xml", DefaultTemplate.NotesSlide(text));
        package.AddRelationship(name, PptxNames.NotesMasterRelationship, master.Name);
        package.AddRelationship(name, PptxNames.SlideRelationship, PartName);
        package.AddRelationship(PartName, PptxNames.NotesSlideRelationship, name);
    }

    private static XElement NotesPlaceholder(NotesSlide page, string text)
    {
        XElement nonVisual = SlideShapeCollection.NonVisualElement("nvSpPr", "cNvSpPr", page.NextShapeId(), "Notes Placeholder");
        nonVisual.Element(PptxNames.P + "cNvSpPr")!.Add(new XElement(PptxNames.A + "spLocks", new XAttribute("noGrp", "1")));
        nonVisual.Element(PptxNames.P + "nvPr")!.Add(new XElement(PptxNames.P + "ph", new XAttribute("type", "body"), new XAttribute("idx", "1")));
        return new XElement(PptxNames.P + "sp", nonVisual, new XElement(PptxNames.P + "spPr"), TextBody.NewElement(text));
    }
}
