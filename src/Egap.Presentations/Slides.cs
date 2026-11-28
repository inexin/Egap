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
    /// The kind of layout PowerPoint uses to pick one for new content (<c>p:sldLayout/@type</c>);
    /// <see cref="SlideLayoutType.Custom"/> when the file names none or one this version does not know. The attribute is
    /// written back as it was.
    /// </summary>
    public SlideLayoutType LayoutType => (string?)Xml.Root.Attribute("type") switch
    {
        "title" => SlideLayoutType.Title,
        "tx" => SlideLayoutType.Text,
        "twoColTx" => SlideLayoutType.TwoColumnText,
        "tbl" => SlideLayoutType.Table,
        "txAndChart" => SlideLayoutType.TextAndChart,
        "chartAndTx" => SlideLayoutType.ChartAndText,
        "dgm" => SlideLayoutType.Diagram,
        "chart" => SlideLayoutType.Chart,
        "txAndClipArt" => SlideLayoutType.TextAndClipArt,
        "clipArtAndTx" => SlideLayoutType.ClipArtAndText,
        "titleOnly" => SlideLayoutType.TitleOnly,
        "blank" => SlideLayoutType.Blank,
        "txAndObj" => SlideLayoutType.TextAndObject,
        "objAndTx" => SlideLayoutType.ObjectAndText,
        "objOnly" => SlideLayoutType.ObjectOnly,
        "obj" => SlideLayoutType.TitleAndContent,
        "txAndMedia" => SlideLayoutType.TextAndMedia,
        "mediaAndTx" => SlideLayoutType.MediaAndText,
        "objOverTx" => SlideLayoutType.ObjectOverText,
        "txOverObj" => SlideLayoutType.TextOverObject,
        "txAndTwoObj" => SlideLayoutType.TextAndTwoObjects,
        "twoObjAndTx" => SlideLayoutType.TwoObjectsAndText,
        "twoObjOverTx" => SlideLayoutType.TwoObjectsOverText,
        "fourObj" => SlideLayoutType.FourObjects,
        "vertTx" => SlideLayoutType.VerticalText,
        "clipArtAndVertTx" => SlideLayoutType.ClipArtAndVerticalText,
        "vertTitleAndTx" => SlideLayoutType.VerticalTitleAndText,
        "vertTitleAndTxOverChart" => SlideLayoutType.VerticalTitleAndTextOverChart,
        "twoObj" => SlideLayoutType.TwoObjects,
        "objAndTwoObj" => SlideLayoutType.ObjectAndTwoObjects,
        "twoObjAndObj" => SlideLayoutType.TwoObjectsAndObject,
        "secHead" => SlideLayoutType.SectionHeader,
        "twoTxTwoObj" => SlideLayoutType.TwoTextAndTwoObjects,
        "objTx" => SlideLayoutType.ObjectAndCaption,
        "picTx" => SlideLayoutType.PictureAndCaption,
        _ => SlideLayoutType.Custom,
    };

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

/// <summary>
/// The slides of a presentation, in the order they are shown. A slide the presentation lists whose part is missing from
/// the file is left out; its entry is written back as it was.
/// </summary>
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

/// <summary>The kinds of slide layout PowerPoint uses to pick a layout for new content (ECMA-376 Part 1, 19.7.15, ST_SlideLayoutType).</summary>
public enum SlideLayoutType
{
    /// <summary>A title slide: a title and a subtitle (<c>title</c>).</summary>
    Title,

    /// <summary>A title and text (<c>tx</c>).</summary>
    Text,

    /// <summary>A title and two columns of text (<c>twoColTx</c>).</summary>
    TwoColumnText,

    /// <summary>A title and a table (<c>tbl</c>).</summary>
    Table,

    /// <summary>A title, text on the left and a chart on the right (<c>txAndChart</c>).</summary>
    TextAndChart,

    /// <summary>A title, a chart on the left and text on the right (<c>chartAndTx</c>).</summary>
    ChartAndText,

    /// <summary>A title and a diagram (<c>dgm</c>).</summary>
    Diagram,

    /// <summary>A title and a chart (<c>chart</c>).</summary>
    Chart,

    /// <summary>A title, text on the left and clip art on the right (<c>txAndClipArt</c>).</summary>
    TextAndClipArt,

    /// <summary>A title, clip art on the left and text on the right (<c>clipArtAndTx</c>).</summary>
    ClipArtAndText,

    /// <summary>A title only (<c>titleOnly</c>).</summary>
    TitleOnly,

    /// <summary>No placeholders (<c>blank</c>).</summary>
    Blank,

    /// <summary>A title, text on the left and an object on the right (<c>txAndObj</c>).</summary>
    TextAndObject,

    /// <summary>A title, an object on the left and text on the right (<c>objAndTx</c>).</summary>
    ObjectAndText,

    /// <summary>An object only (<c>objOnly</c>).</summary>
    ObjectOnly,

    /// <summary>A title and content: one object (<c>obj</c>).</summary>
    TitleAndContent,

    /// <summary>A title, text on the left and media on the right (<c>txAndMedia</c>).</summary>
    TextAndMedia,

    /// <summary>A title, media on the left and text on the right (<c>mediaAndTx</c>).</summary>
    MediaAndText,

    /// <summary>A title, an object above text (<c>objOverTx</c>).</summary>
    ObjectOverText,

    /// <summary>A title, text above an object (<c>txOverObj</c>).</summary>
    TextOverObject,

    /// <summary>A title, text on the left and two objects on the right (<c>txAndTwoObj</c>).</summary>
    TextAndTwoObjects,

    /// <summary>A title, two objects on the left and text on the right (<c>twoObjAndTx</c>).</summary>
    TwoObjectsAndText,

    /// <summary>A title, two objects above text (<c>twoObjOverTx</c>).</summary>
    TwoObjectsOverText,

    /// <summary>A title and four objects (<c>fourObj</c>).</summary>
    FourObjects,

    /// <summary>A title and vertical text (<c>vertTx</c>).</summary>
    VerticalText,

    /// <summary>A title, clip art and vertical text (<c>clipArtAndVertTx</c>).</summary>
    ClipArtAndVerticalText,

    /// <summary>A vertical title and vertical text (<c>vertTitleAndTx</c>).</summary>
    VerticalTitleAndText,

    /// <summary>A vertical title, and vertical text above a chart (<c>vertTitleAndTxOverChart</c>).</summary>
    VerticalTitleAndTextOverChart,

    /// <summary>A title and two objects side by side (<c>twoObj</c>).</summary>
    TwoObjects,

    /// <summary>A title, one object on the left and two on the right (<c>objAndTwoObj</c>).</summary>
    ObjectAndTwoObjects,

    /// <summary>A title, two objects on the left and one on the right (<c>twoObjAndObj</c>).</summary>
    TwoObjectsAndObject,

    /// <summary>A custom layout (<c>cust</c>): the default when the file names none, and what a type this version does not know reads as.</summary>
    Custom,

    /// <summary>A section header (<c>secHead</c>).</summary>
    SectionHeader,

    /// <summary>A title, and two objects each with text above it, for comparing (<c>twoTxTwoObj</c>).</summary>
    TwoTextAndTwoObjects,

    /// <summary>An object with a title and a caption beside it (<c>objTx</c>).</summary>
    ObjectAndCaption,

    /// <summary>A picture with a title and a caption (<c>picTx</c>).</summary>
    PictureAndCaption,
}
