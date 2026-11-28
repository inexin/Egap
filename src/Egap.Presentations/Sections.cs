using System.Collections;
using System.Globalization;
using System.Xml.Linq;

using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// The sections a presentation's slides are grouped in, as PowerPoint's slide sorter shows them ([MS-PPTX] 2.3.3.25,
/// <c>p14:sectionLst</c>). Sections split the slides in the order shown: each holds a run of them, and every slide is in
/// one when there are any. Empty when the presentation has none.
/// </summary>
public sealed class SlideSectionCollection : IReadOnlyList<SlideSection>
{
    /// <summary>The extension that holds the section list ([MS-PPTX] 2.2.5).</summary>
    internal const string ExtensionUri = "{521415D9-36F7-43E2-AB2F-B90AF26B5E84}";

    private readonly Presentation presentation;
    private readonly Dictionary<XElement, SlideSection> sections = [];

    internal SlideSectionCollection(Presentation presentation)
    {
        this.presentation = presentation;
    }

    /// <summary>The number of sections.</summary>
    public int Count => Elements.Count();

    /// <summary>The section at <paramref name="index"/>, from 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no section at that position.</exception>
    public SlideSection this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            return Elements.Skip(index).Select(Wrap).FirstOrDefault() ?? throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private XElement? List => presentation.Main.Root.Element(PptxNames.P + "extLst")?.Elements(PptxNames.P + "ext")
        .FirstOrDefault(e => (string?)e.Attribute("uri") == ExtensionUri)?.Element(PptxNames.P14 + "sectionLst");

    private IEnumerable<XElement> Elements => List?.Elements(PptxNames.P14 + "section") ?? [];

    /// <summary>
    /// Adds an empty section named <paramref name="name"/> after the last. When the presentation has no sections yet, the
    /// new one holds all its slides.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    public SlideSection Add(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        XElement list = List ?? MakeList();
        bool first = !list.Elements(PptxNames.P14 + "section").Any();
        XElement section = NewSection(name, first ? presentation.Slides : []);
        list.Add(section);
        presentation.Main.MarkChanged();
        return Wrap(section);
    }

    /// <summary>
    /// Adds a section named <paramref name="name"/> that starts at <paramref name="firstSlide"/>: it takes that slide and the
    /// slides after it up to the next section. When the presentation has no sections yet, the slides before it go into a
    /// section named "Default Section", as PowerPoint makes one.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name or the slide is null.</exception>
    /// <exception cref="ArgumentException">The slide is not in this presentation.</exception>
    public SlideSection Add(string name, Slide firstSlide)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(firstSlide);
        int start = presentation.Slides.IndexOf(firstSlide);
        if (start < 0)
        {
            throw new ArgumentException("The slide is not in this presentation.", nameof(firstSlide));
        }

        if (List is null)
        {
            XElement list = MakeList();
            if (start > 0)
            {
                list.Add(NewSection("Default Section", presentation.Slides.Take(start)));
            }

            XElement made = NewSection(name, presentation.Slides.Skip(start));
            list.Add(made);
            presentation.Main.MarkChanged();
            return Wrap(made);
        }

        XElement? current = SectionElementOf(firstSlide);
        XElement section = NewSection(name, []);
        if (current is null)
        {
            List!.Add(section);
        }
        else
        {
            current.AddAfterSelf(section);
            XElement ids = section.Element(PptxNames.P14 + "sldIdLst")!;
            List<XElement> moved = [.. current.Element(PptxNames.P14 + "sldIdLst")!.Elements(PptxNames.P14 + "sldId")
                .SkipWhile(e => PptxNames.UInt(e, "id") != firstSlide.SlideId)];
            moved.ForEach(e => e.Remove());
            ids.Add(moved);
        }

        presentation.Main.MarkChanged();
        return Wrap(section);
    }

    /// <summary>
    /// Removes <paramref name="section"/>; its slides join the section before it, or the one after when it is the first.
    /// Removing the only section leaves the presentation with none.
    /// </summary>
    /// <returns>Whether the section was in the presentation.</returns>
    /// <exception cref="ArgumentNullException">The section is null.</exception>
    public bool Remove(SlideSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        XElement element = section.Element;
        if (element.Parent != List || List is not { } list)
        {
            return false;
        }

        sections.Remove(element);
        XElement? neighbour = element.ElementsBeforeSelf(PptxNames.P14 + "section").LastOrDefault();
        List<XElement> ids = [.. element.Element(PptxNames.P14 + "sldIdLst")?.Elements(PptxNames.P14 + "sldId") ?? []];
        ids.ForEach(e => e.Remove());
        if (neighbour is not null)
        {
            neighbour.Element(PptxNames.P14 + "sldIdLst")!.Add(ids);
        }
        else if (element.ElementsAfterSelf(PptxNames.P14 + "section").FirstOrDefault() is { } next)
        {
            next.Element(PptxNames.P14 + "sldIdLst")!.AddFirst(ids);
        }

        element.Remove();
        if (!list.Elements(PptxNames.P14 + "section").Any())
        {
            XElement extension = list.Parent!;
            XElement extensions = extension.Parent!;
            extension.Remove();
            if (!extensions.HasElements)
            {
                extensions.Remove();
            }
        }

        presentation.Main.MarkChanged();
        return true;
    }

    /// <summary>
    /// Moves <paramref name="slide"/> into <paramref name="section"/>, as its last slide: the slide moves in the order shown
    /// too, since sections hold runs of slides.
    /// </summary>
    /// <exception cref="ArgumentNullException">The slide or the section is null.</exception>
    /// <exception cref="ArgumentException">The slide or the section is not in this presentation.</exception>
    public void MoveSlide(Slide slide, SlideSection section)
    {
        ArgumentNullException.ThrowIfNull(slide);
        ArgumentNullException.ThrowIfNull(section);
        if (!presentation.Slides.List.Contains(slide))
        {
            throw new ArgumentException("The slide is not in this presentation.", nameof(slide));
        }

        if (section.Element.Parent is null || section.Element.Parent != List)
        {
            throw new ArgumentException("The section is not in this presentation.", nameof(section));
        }

        SlideRemoved(slide);
        int index = 0;
        foreach (XElement element in Elements)
        {
            index += SlideIds(element).Count(id => presentation.Slides.FindById(id) is not null);
            if (element == section.Element)
            {
                break;
            }
        }

        presentation.MoveSlide(slide, index);
        section.Element.Element(PptxNames.P14 + "sldIdLst")!.Add(IdEntry(slide));
        presentation.Main.MarkChanged();
    }

    /// <summary>The section <paramref name="slide"/> is in, or null when the presentation has no sections.</summary>
    public SlideSection? SectionOf(Slide slide)
    {
        ArgumentNullException.ThrowIfNull(slide);
        return SectionElementOf(slide) is { } element ? Wrap(element) : null;
    }

    /// <inheritdoc/>
    public IEnumerator<SlideSection> GetEnumerator() => Elements.Select(Wrap).ToList().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Puts a slide just added at <paramref name="index"/> into the section of the slide before it.</summary>
    internal void SlideAdded(Slide slide, int index)
    {
        if (Elements.FirstOrDefault() is not { } first)
        {
            return;
        }

        XElement entry = IdEntry(slide);
        Slide? before = index > 0 ? presentation.Slides[index - 1] : null;
        XElement? beforeEntry = before is null ? null : Entries.FirstOrDefault(e => PptxNames.UInt(e, "id") == before.SlideId);
        if (beforeEntry is not null)
        {
            beforeEntry.AddAfterSelf(entry);
        }
        else
        {
            first.Element(PptxNames.P14 + "sldIdLst")!.AddFirst(entry);
        }

        presentation.Main.MarkChanged();
    }

    /// <summary>Takes a slide out of its section.</summary>
    internal void SlideRemoved(Slide slide)
    {
        foreach (XElement entry in Entries.Where(e => PptxNames.UInt(e, "id") == slide.SlideId).ToList())
        {
            entry.Remove();
            presentation.Main.MarkChanged();
        }
    }

    internal static IEnumerable<uint> SlideIds(XElement section)
    {
        foreach (XElement entry in section.Element(PptxNames.P14 + "sldIdLst")?.Elements(PptxNames.P14 + "sldId") ?? [])
        {
            if (PptxNames.UInt(entry, "id") is uint id)
            {
                yield return id;
            }
        }
    }

    private IEnumerable<XElement> Entries => Elements.SelectMany(s => s.Element(PptxNames.P14 + "sldIdLst")?.Elements(PptxNames.P14 + "sldId") ?? []);

    private XElement? SectionElementOf(Slide slide) => Elements.FirstOrDefault(s => SlideIds(s).Contains(slide.SlideId));

    private SlideSection Wrap(XElement element)
    {
        if (!sections.TryGetValue(element, out SlideSection? section))
        {
            section = new SlideSection(presentation, element);
            sections[element] = section;
        }

        return section;
    }

    private XElement MakeList()
    {
        XElement extensions = presentation.Main.Root.Element(PptxNames.P + "extLst") ?? AddExtensions();
        XElement list = new(PptxNames.P14 + "sectionLst", new XAttribute(XNamespace.Xmlns + "p14", PptxNames.P14.NamespaceName));
        extensions.Add(new XElement(PptxNames.P + "ext", new XAttribute("uri", ExtensionUri), list));
        return list;

        XElement AddExtensions()
        {
            XElement made = new(PptxNames.P + "extLst");
            presentation.InsertInOrder(made);
            return made;
        }
    }

    private static XElement NewSection(string name, IEnumerable<Slide> slides) => new(PptxNames.P14 + "section",
        new XAttribute("name", name),
        new XAttribute("id", Guid.NewGuid().ToString("B").ToUpper(CultureInfo.InvariantCulture)),
        new XElement(PptxNames.P14 + "sldIdLst", slides.Select(IdEntry)));

    private static XElement IdEntry(Slide slide) => new(PptxNames.P14 + "sldId", new XAttribute("id", PptxNames.Text(slide.SlideId)));
}

/// <summary>A section of slides (<c>p14:section</c>): a name and a run of slides in the order shown.</summary>
public sealed class SlideSection
{
    private readonly Presentation presentation;

    internal SlideSection(Presentation presentation, XElement element)
    {
        this.presentation = presentation;
        Element = element;
    }

    /// <summary>The section's name, as the slide sorter shows it.</summary>
    /// <exception cref="ArgumentNullException">The name set is null.</exception>
    public string Name
    {
        get => (string?)Element.Attribute("name") ?? "";
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value != Name)
            {
                Element.SetAttributeValue("name", value);
                presentation.Main.MarkChanged();
            }
        }
    }

    /// <summary>The section's id, a GUID in braces, which stays the same when it is renamed.</summary>
    public string Id => (string?)Element.Attribute("id") ?? "";

    /// <summary>The section's slides, in the order shown; slides the presentation no longer has are left out.</summary>
    public IReadOnlyList<Slide> Slides => [.. SlideSectionCollection.SlideIds(Element).Select(presentation.Slides.FindById).OfType<Slide>()];

    internal XElement Element { get; }
}
