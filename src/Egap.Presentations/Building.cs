using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>Adding, removing and moving slides: the parts, relationships and slide list entries they need.</summary>
public sealed partial class Presentation
{
    /// <summary>The placeholder types a new slide leaves to its layout and master: they show only when the slide turns them on.</summary>
    private static readonly string[] HeaderFooterTypes = ["dt", "ftr", "sldNum", "hdr"];

    internal Slide InsertSlide(int index, SlideLayout layout)
    {
        string name = Package.UniqueName("/ppt/slides/slide", ".xml");
        PackagePart part = Package.AddXmlPart(name, DefaultTemplate.Ct + "slide+xml", DefaultTemplate.Slide());
        Package.AddRelationship(name, PptxNames.SlideLayoutRelationship, layout.PartName);
        string rid = Package.AddRelationship(main.Part.Name, PptxNames.SlideRelationship, name);

        XElement list = main.Root.Element(PptxNames.P + "sldIdLst") ?? AddList();
        uint id = Math.Max(255, list.Elements(PptxNames.P + "sldId").Select(e => PptxNames.UInt(e, "id") ?? 0).DefaultIfEmpty(0u).Max()) + 1;
        XElement entry = new(PptxNames.P + "sldId", new XAttribute("id", PptxNames.Text(id)), new XAttribute(PptxNames.R + "id", rid));
        if (index < Slides.Count)
        {
            Slides[index].IdElement.AddBeforeSelf(entry);
        }
        else if (Slides.Count > 0)
        {
            Slides[^1].IdElement.AddAfterSelf(entry);
        }
        else
        {
            list.Add(entry);
        }

        main.MarkChanged();
        Slide slide = new(this, part.Xml!, layout, entry);
        CopyPlaceholders(slide, layout);
        Slides.List.Insert(index, slide);
        Sections.SlideAdded(slide, index);
        return slide;

        XElement AddList()
        {
            XElement made = new(PptxNames.P + "sldIdLst");
            InsertInOrder(made);
            return made;
        }
    }

    internal void RemoveSlide(Slide slide)
    {
        string? rid = (string?)slide.IdElement.Attribute(PptxNames.R + "id");
        slide.IdElement.Remove();
        if (rid is not null)
        {
            // Custom shows list slides by the same relationship.
            foreach (XElement shown in main.Root.Descendants(PptxNames.P + "sld").Where(e => (string?)e.Attribute(PptxNames.R + "id") == rid).ToList())
            {
                shown.Remove();
            }

            Package.RemoveRelationship(main.Part.Name, rid);
        }

        main.MarkChanged();
        Slides.List.Remove(slide);
        Sections.SlideRemoved(slide);
        List<string> related = [.. Package.Relationships(slide.PartName).Where(r => !r.External).Select(r => r.TargetName)];
        Package.RemovePart(slide.PartName);
        Package.RemoveUnreferenced(related);
    }

    internal void MoveSlide(Slide slide, int index)
    {
        Slides.List.Remove(slide);
        slide.IdElement.Remove();
        if (index < Slides.Count)
        {
            Slides[index].IdElement.AddBeforeSelf(slide.IdElement);
        }
        else if (Slides.Count > 0)
        {
            Slides[^1].IdElement.AddAfterSelf(slide.IdElement);
        }
        else
        {
            main.Root.Element(PptxNames.P + "sldIdLst")!.Add(slide.IdElement);
        }

        Slides.List.Insert(index, slide);
        main.MarkChanged();
    }

    /// <summary>
    /// Gives a new slide an empty placeholder for each of its layout's, as PowerPoint does, so that it shows the layout's
    /// prompts; the date, footer, slide number and header stay on the layout.
    /// </summary>
    private static void CopyPlaceholders(Slide slide, SlideLayout layout)
    {
        XElement tree = slide.ShapeTree;
        uint id = 2;
        foreach (SlideShape shape in layout.Shapes)
        {
            XElement? ph = shape.PlaceholderElement;
            if (shape is not AutoShape || ph is null || HeaderFooterTypes.Contains((string?)ph.Attribute("type")))
            {
                continue;
            }

            XElement copy = new(ph.Name, ph.Attributes().Where(a => a.Name.LocalName is "type" or "orient" or "sz" or "idx"));
            tree.Add(new XElement(PptxNames.P + "sp",
                new XElement(PptxNames.P + "nvSpPr",
                    new XElement(PptxNames.P + "cNvPr", new XAttribute("id", PptxNames.Text(id++)), new XAttribute("name", shape.Name)),
                    new XElement(PptxNames.P + "cNvSpPr", new XElement(PptxNames.A + "spLocks", new XAttribute("noGrp", "1"))),
                    new XElement(PptxNames.P + "nvPr", copy)),
                new XElement(PptxNames.P + "spPr"),
                TextBody.NewElement("")));
        }

        slide.Xml.MarkChanged();
    }
}
