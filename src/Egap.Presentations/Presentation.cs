using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// A PowerPoint presentation: its slides, the slide layouts they follow and the slide masters those belong to. A loaded
/// presentation keeps its file whole, so a save writes back every part and every element the model does not hold, as it was,
/// and only the parts whose content the model changed are written again.
/// </summary>
public sealed class Presentation
{
    private const string MainContentType = "application/vnd.openxmlformats-officedocument.presentationml.";
    private const string MacroContentType = "application/vnd.ms-powerpoint.";

    private readonly XmlPart main;
    private readonly List<SlideMaster> masters = [];

    private Presentation(PptxPackage package, XmlPart main)
    {
        Package = package;
        this.main = main;
        Format = FormatOf(main.Part.ContentType);
        Slides = new SlideCollection();
    }

    internal PptxPackage Package { get; }

    /// <summary>Whether the file was written in ISO/IEC 29500 Strict namespaces; a save keeps them.</summary>
    internal bool IsStrict => main.IsStrict;

    /// <summary>The kind of file the presentation was loaded from; a save writes the same kind.</summary>
    public PresentationFormat Format { get; }

    /// <summary>
    /// The size of every slide, in EMUs with points alongside (<c>p:sldSz</c>). Setting it changes the size the slides are
    /// shown and printed at; their shapes keep their positions.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is shorter than 1 inch (914,400 EMUs) or longer than 56 inches (51,206,400 EMUs), as PowerPoint allows.</exception>
    public SlideSize SlideSize
    {
        get => ReadSize(main.Root.Element(PptxNames.P + "sldSz")) ?? new SlideSize(12_192_000, 6_858_000);
        set
        {
            if (value.Width is < 914_400 or > 51_206_400 || value.Height is < 914_400 or > 51_206_400)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Each side of a slide must be from 914,400 to 51,206,400 EMUs (1 to 56 inches).");
            }

            if (value == SlideSize)
            {
                return;
            }

            XElement? size = main.Root.Element(PptxNames.P + "sldSz");
            if (size is null)
            {
                size = new XElement(PptxNames.P + "sldSz");
                XElement? before = main.Root.Element(PptxNames.P + "notesSz");
                if (before is not null)
                {
                    before.AddBeforeSelf(size);
                }
                else
                {
                    main.Root.Add(size);
                }
            }

            size.SetAttributeValue("cx", PptxNames.Text(value.Width));
            size.SetAttributeValue("cy", PptxNames.Text(value.Height));

            // The named size (screen4x3, A4 and the rest) no longer holds.
            size.Attribute("type")?.Remove();
            main.MarkChanged();
        }
    }

    /// <summary>The size of the notes pages, in EMUs with points alongside (<c>p:notesSz</c>).</summary>
    public SlideSize NotesSize => ReadSize(main.Root.Element(PptxNames.P + "notesSz")) ?? new SlideSize(6_858_000, 9_144_000);

    /// <summary>The slide masters, in the order the presentation lists them, each with its layouts and theme.</summary>
    public IReadOnlyList<SlideMaster> SlideMasters => masters;

    /// <summary>The slides, in the order they are shown.</summary>
    public SlideCollection Slides { get; }

    /// <summary>
    /// The name of the notes master's part, such as <c>/ppt/notesMasters/notesMaster1.xml</c>, or null when the presentation
    /// has none. The notes master and the notes pages are kept as they are and written back with the file.
    /// </summary>
    public string? NotesMasterPartName { get; private set; }

    /// <summary>Loads a PowerPoint file: PPTX, PPTM, POTX, POTM, PPSX or PPSM, transitional or Strict.</summary>
    /// <exception cref="ArgumentException">The path is null or empty.</exception>
    /// <exception cref="DocumentException">The file is not a presentation Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file goes past one of <see cref="PresentationLoadOptions.Limits"/>.</exception>
    public static Presentation Load(string path, PresentationLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream, options);
    }

    /// <summary>Loads a PowerPoint file from a stream, which is left open.</summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="DocumentException">The stream does not hold a presentation Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file goes past one of <see cref="PresentationLoadOptions.Limits"/>.</exception>
    public static Presentation Load(Stream stream, PresentationLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            copy.Position = 0;
            return Load(copy, options);
        }

        PptxPackage package = PptxPackage.Read(stream, options?.Limits ?? new DocumentLimits());
        OpcPart part = package.Opc.GetRelatedPart(package.Opc.Relationships, OpcRelationshipTypes.OfficeDocument)
            ?? throw new DocumentException("The package has no main part: it is not a PowerPoint file.");
        XmlPart main = package.Xml(part);
        if (main.Root.Name != PptxNames.P + "presentation")
        {
            throw new DocumentException($"The main part {part.Name} holds a {main.Root.Name.LocalName} element, not a presentation: the file is not a PowerPoint file.");
        }

        Presentation presentation = new(package, main);
        presentation.Read();
        return presentation;
    }

    /// <summary>
    /// Saves the presentation to a file. The file is the same kind as the one loaded (<see cref="Format"/>), whatever the
    /// extension: a PPTM saved as <c>.pptx</c> still holds its macros, and PowerPoint will not open it under that name.
    /// </summary>
    /// <exception cref="ArgumentException">The path is null or empty.</exception>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using MemoryStream buffer = new();
        Package.Save(buffer);
        using FileStream stream = File.Create(path);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    /// <summary>Saves the presentation to a stream, which is left open.</summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Package.Save(stream);
    }

    private void Read()
    {
        OpcPart part = main.Part;
        Dictionary<string, SlideLayout> layouts = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement id in main.Root.Element(PptxNames.P + "sldMasterIdLst")?.Elements(PptxNames.P + "sldMasterId") ?? [])
        {
            if (Related(part, id, PptxNames.SlideMasterRelationship) is not { } masterPart)
            {
                continue;
            }

            SlideMaster master = new(this, Package.Xml(masterPart));
            masters.Add(master);
            foreach (XElement layoutId in master.Xml.Root.Element(PptxNames.P + "sldLayoutIdLst")?.Elements(PptxNames.P + "sldLayoutId") ?? [])
            {
                if (Related(masterPart, layoutId, PptxNames.SlideLayoutRelationship) is { } layoutPart && !layouts.ContainsKey(layoutPart.Name))
                {
                    SlideLayout layout = new(this, Package.Xml(layoutPart), master);
                    layouts[layoutPart.Name] = layout;
                    master.LayoutList.Add(layout);
                }
            }
        }

        foreach (XElement id in main.Root.Element(PptxNames.P + "notesMasterIdLst")?.Elements(PptxNames.P + "notesMasterId") ?? [])
        {
            if (Related(part, id, PptxNames.NotesMasterRelationship) is { } notesMaster)
            {
                NotesMasterPartName = notesMaster.Name;
                break;
            }
        }

        foreach (XElement id in main.Root.Element(PptxNames.P + "sldIdLst")?.Elements(PptxNames.P + "sldId") ?? [])
        {
            if (Related(part, id, PptxNames.SlideRelationship) is not { } slidePart)
            {
                continue;
            }

            XmlPart xml = Package.Xml(slidePart);
            OpcPart? layoutPart = slidePart.RelatedPart(PptxNames.SlideLayoutRelationship);
            SlideLayout? layout = layoutPart is null ? null : layouts.GetValueOrDefault(layoutPart.Name);
            Slides.List.Add(new Slide(this, xml, layout, PptxNames.UInt(id, "id") ?? 0, slidePart.RelatedPart(PptxNames.NotesSlideRelationship)?.Name));
        }
    }

    /// <summary>The part an id list entry's <c>r:id</c> points at, when the relationship has the type expected.</summary>
    private OpcPart? Related(OpcPart source, XElement id, string type)
    {
        if ((string?)id.Attribute(PptxNames.R + "id") is not { } rid || source.GetRelationship(rid) is not { External: false } relationship
            || !OpcRelationshipTypes.Same(relationship.Type, type))
        {
            return null;
        }

        return Package.Opc.GetPart(relationship.TargetName);
    }

    private static SlideSize? ReadSize(XElement? element)
    {
        return PptxNames.Long(element, "cx") is long cx && PptxNames.Long(element, "cy") is long cy ? new SlideSize(cx, cy) : null;
    }

    private static PresentationFormat FormatOf(string contentType) => contentType switch
    {
        MainContentType + "template.main+xml" => PresentationFormat.Potx,
        MainContentType + "slideshow.main+xml" => PresentationFormat.Ppsx,
        MacroContentType + "presentation.macroEnabled.main+xml" => PresentationFormat.Pptm,
        MacroContentType + "template.macroEnabled.main+xml" => PresentationFormat.Potm,
        MacroContentType + "slideshow.macroEnabled.main+xml" => PresentationFormat.Ppsm,
        _ => PresentationFormat.Pptx,
    };
}

/// <summary>The kinds of PowerPoint file: presentations, templates and shows, with or without macros.</summary>
public enum PresentationFormat
{
    /// <summary>A presentation (.pptx).</summary>
    Pptx,

    /// <summary>A presentation with macros (.pptm).</summary>
    Pptm,

    /// <summary>A template (.potx).</summary>
    Potx,

    /// <summary>A template with macros (.potm).</summary>
    Potm,

    /// <summary>A show, which PowerPoint opens straight into the slide show (.ppsx).</summary>
    Ppsx,

    /// <summary>A show with macros (.ppsm).</summary>
    Ppsm,
}

/// <summary>How a presentation is loaded.</summary>
public sealed class PresentationLoadOptions
{
    /// <summary>The limits the reader keeps to, against files made to use up memory; the defaults suit any real presentation.</summary>
    public DocumentLimits Limits { get; init; } = new();
}

/// <summary>The size of a slide or notes page, in EMUs (English Metric Units: 914,400 to the inch, 12,700 to the point).</summary>
/// <param name="Width">The width in EMUs.</param>
/// <param name="Height">The height in EMUs.</param>
public readonly record struct SlideSize(long Width, long Height)
{
    /// <summary>The width in points.</summary>
    public double WidthPoints => Width / (double)ShapeTransform.EmusPerPoint;

    /// <summary>The height in points.</summary>
    public double HeightPoints => Height / (double)ShapeTransform.EmusPerPoint;

    /// <summary>A size given in points, rounded to whole EMUs.</summary>
    public static SlideSize FromPoints(double width, double height) => new(ShapeTransform.ToEmus(width), ShapeTransform.ToEmus(height));
}
