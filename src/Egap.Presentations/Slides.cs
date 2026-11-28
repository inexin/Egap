using System.Collections;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// What slides, slide layouts and slide masters have in common: a part of the file, a name, and a tree of shapes
/// (<c>p:cSld</c>).
/// </summary>
public abstract class SlideBase
{
    private SlideShapeCollection? shapes;

    private protected SlideBase(Presentation presentation, XmlPart xml)
    {
        Presentation = presentation;
        Xml = xml;
    }

    /// <summary>The presentation this belongs to.</summary>
    public Presentation Presentation { get; }

    /// <summary>The name of the part that holds it, such as <c>/ppt/slides/slide1.xml</c>.</summary>
    public string PartName => Xml.Part.Name;

    /// <summary>The name PowerPoint shows for it (<c>p:cSld/@name</c>): a layout's or master's name; usually empty for a slide.</summary>
    public string Name => (string?)CommonData?.Attribute("name") ?? "";

    /// <summary>The shapes, in the order they are drawn: the first is at the back.</summary>
    public SlideShapeCollection Shapes => shapes ??= new SlideShapeCollection(this, CommonData?.Element(PptxNames.P + "spTree"));

    /// <summary>
    /// The map from the twelve colour names text and shapes use (<c>tx1</c>, <c>bg1</c> and the rest) to the theme's colours:
    /// the master's own, or the override this slide or layout has.
    /// </summary>
    public abstract ColorMap ColorMap { get; }

    internal XmlPart Xml { get; }

    private XElement? CommonData => Xml.Root.Element(PptxNames.P + "cSld");

    /// <summary>The override of a layout or slide (<c>p:clrMapOvr</c>), or null when it follows its master.</summary>
    private protected ColorMap? ReadOverride()
    {
        return Xml.Root.Element(PptxNames.P + "clrMapOvr")?.Element(PptxNames.A + "overrideClrMapping") is { } map ? new ColorMap(map) : null;
    }
}

/// <summary>A slide: the layout it follows, its shapes, and whether the show skips it.</summary>
public sealed class Slide : SlideBase
{
    private ColorMap? colorOverride;
    private bool overrideRead;

    internal Slide(Presentation presentation, XmlPart xml, SlideLayout? layout, uint id, string? notes)
        : base(presentation, xml)
    {
        Layout = layout;
        SlideId = id;
        NotesPartName = notes;
    }

    /// <summary>The layout the slide follows, or null when the file names none the presentation's masters list.</summary>
    public SlideLayout? Layout { get; }

    /// <summary>The master of the slide's layout, or null when the slide has no layout.</summary>
    public SlideMaster? Master => Layout?.Master;

    /// <summary>The slide's id in the presentation (<c>p:sldId/@id</c>), from 256; stable while slides move.</summary>
    public uint SlideId { get; }

    /// <summary>Whether the slide show skips the slide (<c>show="0"</c>).</summary>
    public bool Hidden => !PptxNames.Bool(Xml.Root, "show", fallback: true);

    /// <summary>
    /// The name of the slide's notes page part, such as <c>/ppt/notesSlides/notesSlide1.xml</c>, or null when it has none.
    /// The notes page is kept as it is and written back with the file.
    /// </summary>
    public string? NotesPartName { get; }

    /// <summary>The slide's own colour map (<c>p:clrMapOvr</c>), or null when it follows its master's.</summary>
    public ColorMap? ColorMapOverride
    {
        get
        {
            if (!overrideRead)
            {
                colorOverride = ReadOverride();
                overrideRead = true;
            }

            return colorOverride;
        }
    }

    /// <inheritdoc/>
    public override ColorMap ColorMap => ColorMapOverride ?? Layout?.ColorMap ?? ColorMap.Default;
}

/// <summary>A slide layout: the placeholders and shapes slides that follow it start from, under one master.</summary>
public sealed class SlideLayout : SlideBase
{
    private ColorMap? colorOverride;
    private bool overrideRead;

    internal SlideLayout(Presentation presentation, XmlPart xml, SlideMaster master)
        : base(presentation, xml)
    {
        Master = master;
    }

    /// <summary>The master the layout belongs to.</summary>
    public SlideMaster Master { get; }

    /// <summary>
    /// The kind of layout PowerPoint uses to pick one for new content (<c>p:sldLayout/@type</c>), as the file spells it:
    /// <c>title</c>, <c>obj</c>, <c>twoObj</c>, <c>secHead</c>, <c>titleOnly</c>, <c>blank</c> and the rest; <c>cust</c>
    /// when the file names none.
    /// </summary>
    public string LayoutType => (string?)Xml.Root.Attribute("type") ?? "cust";

    /// <summary>The layout's own colour map (<c>p:clrMapOvr</c>), or null when it follows its master's.</summary>
    public ColorMap? ColorMapOverride
    {
        get
        {
            if (!overrideRead)
            {
                colorOverride = ReadOverride();
                overrideRead = true;
            }

            return colorOverride;
        }
    }

    /// <inheritdoc/>
    public override ColorMap ColorMap => ColorMapOverride ?? Master.ColorMap;
}

/// <summary>A slide master: the theme, colour map, text styles and shapes its layouts and their slides start from.</summary>
public sealed class SlideMaster : SlideBase
{
    private ColorMap? colorMap;
    private PresentationTheme? theme;
    private bool themeRead;

    internal SlideMaster(Presentation presentation, XmlPart xml)
        : base(presentation, xml)
    {
    }

    /// <summary>The master's layouts, in the order the master lists them.</summary>
    public IReadOnlyList<SlideLayout> Layouts => LayoutList;

    /// <summary>The master's theme: its colours and fonts, or null when the theme part is missing or damaged.</summary>
    public PresentationTheme? Theme
    {
        get
        {
            if (!themeRead)
            {
                themeRead = true;
                if (Xml.Part.RelatedPart(PptxNames.ThemeRelationship) is { } part)
                {
                    try
                    {
                        theme = PresentationTheme.Read(Presentation.Package.Xml(part).Root, part.Name);
                    }
                    catch (DocumentException)
                    {
                        // A damaged theme part is kept as it is and leaves the master without a theme.
                    }
                }
            }

            return theme;
        }
    }

    /// <inheritdoc/>
    public override ColorMap ColorMap => colorMap ??= Xml.Root.Element(PptxNames.P + "clrMap") is { } map ? new ColorMap(map) : ColorMap.Default;

    internal List<SlideLayout> LayoutList { get; } = [];
}

/// <summary>The slides of a presentation, in the order they are shown.</summary>
public sealed class SlideCollection : IReadOnlyList<Slide>
{
    internal SlideCollection()
    {
    }

    /// <summary>The number of slides.</summary>
    public int Count => List.Count;

    internal List<Slide> List { get; } = [];

    /// <summary>The slide at <paramref name="index"/>, from 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no slide at that position.</exception>
    public Slide this[int index] => List[index];

    /// <summary>The slide with the id <paramref name="slideId"/> (<see cref="Slide.SlideId"/>), or null.</summary>
    public Slide? FindById(uint slideId) => List.Find(s => s.SlideId == slideId);

    /// <inheritdoc/>
    public IEnumerator<Slide> GetEnumerator() => List.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
