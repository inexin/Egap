using System.Text;
using System.Xml.Linq;

using Egap.Documents;

namespace Egap.Presentations.Tests;

/// <summary>Saving: unchanged parts byte for byte, changed parts with everything the model does not hold, Strict files in Strict.</summary>
public sealed class RoundTripTests
{
    private const string Extensions =
        "<p:extLst><p:ext uri=\"{BB962C8B-B14F-4D97-AF65-F5344CB8AC3E}\"><p14:creationId xmlns:p14=\"http://schemas.microsoft.com/office/powerpoint/2010/main\" val=\"1234567\"/></p:ext></p:extLst>";

    private const string Unknown =
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Kept\"><a:extLst><a:ext uri=\"{FF2B5EF4-FFF2-40B4-BE49-F238E27FC236}\"><a16:creationId xmlns:a16=\"http://schemas.microsoft.com/office/drawing/2014/main\" id=\"{00000000-0000-0000-0000-000000000001}\"/></a:ext></a:extLst></p:cNvPr>"
        + "<p:cNvSpPr/><p:nvPr userDrawn=\"1\"/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"1\" y=\"2\"/><a:ext cx=\"3\" cy=\"4\"/></a:xfrm><a:prstGeom prst=\"star5\"><a:avLst/></a:prstGeom>"
        + "<a:solidFill><a:schemeClr val=\"accent2\"><a:lumMod val=\"75000\"/></a:schemeClr></a:solidFill><a:ln w=\"12700\"><a:prstDash val=\"dash\"/></a:ln></p:spPr>"
        + "<p:style><a:lnRef idx=\"2\"><a:schemeClr val=\"accent1\"/></a:lnRef><a:fillRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:fillRef><a:effectRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"lt1\"/></a:fontRef></p:style>"
        + "<p:txBody><a:bodyPr rtlCol=\"0\" anchor=\"ctr\"/><a:lstStyle/><a:p><a:pPr algn=\"ctr\"/><a:r><a:rPr lang=\"nb-NO\" sz=\"2400\" dirty=\"0\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:rPr><a:t>  Two spaces</a:t></a:r></a:p></p:txBody></p:sp>";

    [Fact]
    public void AnUnchangedSaveKeepsEveryPartByteForByte()
    {
        byte[] file = Rich().Build();

        Presentation presentation = Presentation.Load(new MemoryStream(file));
        _ = presentation.Slides[0].Shapes[0].Name;
        _ = presentation.SlideMasters[0].Theme;
        byte[] saved = PresentationTests.Saved(presentation);

        Assert.Equal(PptxBuilder.Entries(file).Select(e => e.Name), PptxBuilder.Entries(saved).Select(e => e.Name));
        Assert.All(PptxBuilder.Entries(file).Zip(PptxBuilder.Entries(saved)), pair => Assert.Equal(pair.First.Data, pair.Second.Data));
    }

    [Fact]
    public void SettingAValueToWhatItWasChangesNothing()
    {
        byte[] file = Rich().Build();
        Presentation presentation = Presentation.Load(new MemoryStream(file));
        SlideShape shape = presentation.Slides[0].Shapes[0];

        shape.Name = shape.Name;
        shape.Transform = shape.Transform;
        ((AutoShape)shape).TextBody!.Paragraphs[0].Runs[0].Text = "  Two spaces";
        presentation.SlideSize = presentation.SlideSize;

        Assert.All(PptxBuilder.Entries(file).Zip(PptxBuilder.Entries(PresentationTests.Saved(presentation))), pair => Assert.Equal(pair.First.Data, pair.Second.Data));
    }

    [Fact]
    public void AChangedPartKeepsWhatTheModelDoesNotHold()
    {
        byte[] file = Rich().Build();
        Presentation presentation = Presentation.Load(new MemoryStream(file));
        AutoShape shape = (AutoShape)presentation.Slides[0].Shapes[0];
        XElement before = XDocument.Parse(PptxBuilder.Entry(file, "ppt/slides/slide1.xml")).Root!;

        shape.Name = "Renamed";
        shape.Description = "A star";
        shape.TextBody!.Paragraphs[0].Runs[0].Text = "Changed";
        shape.Transform = new ShapeTransform(10, 20, 30, 40, Rotation: 45, FlipHorizontal: true);
        byte[] saved = PresentationTests.Saved(presentation);

        // Only the slide's part changed.
        foreach (((string name, byte[] data), (_, byte[] again)) in PptxBuilder.Entries(file).Zip(PptxBuilder.Entries(saved)))
        {
            if (name != "ppt/slides/slide1.xml")
            {
                Assert.Equal(data, again);
            }
        }

        // The slide is the same XML but for the values set.
        XElement after = XDocument.Parse(PptxBuilder.Entry(saved, "ppt/slides/slide1.xml")).Root!;
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main", p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        XElement properties = before.Descendants(p + "cNvPr").Single(e => (string?)e.Attribute("name") == "Kept");
        properties.SetAttributeValue("name", "Renamed");
        properties.SetAttributeValue("descr", "A star");
        before.Descendants(a + "t").Single().Value = "Changed";
        XElement xfrm = before.Descendants(a + "xfrm").Last();
        xfrm.SetAttributeValue("rot", "2700000");
        xfrm.SetAttributeValue("flipH", "1");
        xfrm.Element(a + "off")!.ReplaceAttributes(new XAttribute("x", "10"), new XAttribute("y", "20"));
        xfrm.Element(a + "ext")!.ReplaceAttributes(new XAttribute("cx", "30"), new XAttribute("cy", "40"));
        Assert.True(XNode.DeepEquals(Normalized(before), Normalized(after)), after.ToString());

        Presentation reloaded = Presentation.Load(new MemoryStream(saved));
        AutoShape again2 = (AutoShape)reloaded.Slides[0].Shapes[0];
        Assert.Equal("Renamed", again2.Name);
        Assert.Equal("A star", again2.Description);
        Assert.Equal("Changed", again2.TextBody!.Text);
        Assert.Equal(new ShapeTransform(10, 20, 30, 40, 45, true, false), again2.Transform);
        Assert.Equal("star5", again2.PresetGeometry);
    }

    [Fact]
    public void UnknownPartsAndExtensionsAreWrittenBack()
    {
        PptxBuilder builder = Rich();
        Presentation presentation = Presentation.Load(new MemoryStream(builder.Build()));
        presentation.Slides[0].Shapes[0].Name = "Touched";
        presentation.SlideSize = new SlideSize(9_144_000, 6_858_000);

        byte[] saved = PresentationTests.Saved(presentation);

        Assert.Equal([1, 2, 3, 4], PptxBuilder.Entries(saved).Single(e => e.Name == "custom/egap/blob.bin").Data);
        Assert.Equal("<kept xmlns=\"http://example.com/kept\"><child a=\"1\"/></kept>", PptxBuilder.Entry(saved, "custom/egap/kept.xml"));
        string slide = PptxBuilder.Entry(saved, "ppt/slides/slide1.xml");
        Assert.Contains("p14:creationId", slide, StringComparison.Ordinal);
        Assert.Contains("a16:creationId", slide, StringComparison.Ordinal);
        Assert.Contains("<p:style>", slide, StringComparison.Ordinal);
        string main = PptxBuilder.Entry(saved, "ppt/presentation.xml");
        Assert.Contains("p15:sldGuideLst", main, StringComparison.Ordinal);
        Assert.Contains("saveSubsetFonts=\"1\"", main, StringComparison.Ordinal);
        Assert.Contains("<p:defaultTextStyle>", main, StringComparison.Ordinal);
        Assert.Contains("custom/egap/blob.bin", PptxBuilder.Entry(saved, "ppt/_rels/presentation.xml.rels"), StringComparison.Ordinal);
        Assert.Contains("/custom/egap/kept.xml", PptxBuilder.Entry(saved, "[Content_Types].xml"), StringComparison.Ordinal);
        Assert.Equal(new SlideSize(9_144_000, 6_858_000), Presentation.Load(new MemoryStream(saved)).SlideSize);
    }

    [Fact]
    public void ANewTransformGoesFirstInTheShapeProperties()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(PptxBuilder.Placeholder(2, "Title 1", "title", null, null, null, null, null, "T")
            + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Bare\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:txBody><a:bodyPr/><a:p/></p:txBody></p:sp>");
        Presentation presentation = PresentationTests.Load(builder);
        SlideShape title = presentation.Slides[0].Shapes[0];
        SlideShape bare = presentation.Slides[0].Shapes[1];

        title.Transform = ShapeTransform.FromPoints(10, 20, 300, 40);
        bare.Transform = ShapeTransform.FromPoints(1, 2, 3, 4);
        XDocument slide = PresentationTests.EntryXml(presentation, "ppt/slides/slide1.xml");

        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main", a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XElement[] shapes = [.. slide.Descendants(p + "sp")];
        Assert.Equal(a + "xfrm", shapes[0].Element(p + "spPr")!.Elements().First().Name);
        Assert.Equal([p + "nvSpPr", p + "spPr", p + "txBody"], shapes[1].Elements().Select(e => e.Name));
        Assert.Equal([a + "off", a + "ext"], shapes[1].Element(p + "spPr")!.Element(a + "xfrm")!.Elements().Select(e => e.Name));
        Assert.Equal(ShapeTransform.FromPoints(1, 2, 3, 4), PresentationTests.Reload(presentation).Slides[0].Shapes[1].Transform);

        title.Transform = null;
        Assert.Null(title.Transform);
        Assert.Null(PresentationTests.Reload(presentation).Slides[0].Shapes[0].Transform);
        Assert.Throws<ArgumentOutOfRangeException>(() => bare.Transform = new ShapeTransform(0, 0, -1, 5));
    }

    [Fact]
    public void GroupsAndFramesKeepTheirOwnTransforms()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(
            "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"2\" name=\"G\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"1\" y=\"1\"/><a:ext cx=\"2\" cy=\"2\"/><a:chOff x=\"5\" y=\"6\"/><a:chExt cx=\"7\" cy=\"8\"/></a:xfrm></p:grpSpPr></p:grpSp>"
            + "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"New\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:grpSp>"
            + "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"F\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><a:graphic><a:graphicData uri=\"http://example.com/x\"/></a:graphic></p:graphicFrame>");
        Presentation presentation = PresentationTests.Load(builder);
        GroupShape group = (GroupShape)presentation.Slides[0].Shapes[0];
        GroupShape fresh = (GroupShape)presentation.Slides[0].Shapes[1];
        GraphicFrame frame = (GraphicFrame)presentation.Slides[0].Shapes[2];

        group.Transform = new ShapeTransform(100, 200, 300, 400);
        fresh.Transform = new ShapeTransform(10, 20, 30, 40);
        frame.Transform = new ShapeTransform(1, 2, 3, 4);
        Presentation saved = PresentationTests.Reload(presentation);

        Assert.Equal(new ShapeTransform(5, 6, 7, 8), ((GroupShape)saved.Slides[0].Shapes[0]).ChildArea);
        Assert.Equal(new ShapeTransform(100, 200, 300, 400), saved.Slides[0].Shapes[0].Transform);
        Assert.Equal(new ShapeTransform(10, 20, 30, 40), ((GroupShape)saved.Slides[0].Shapes[1]).ChildArea);
        Assert.Equal(new ShapeTransform(1, 2, 3, 4), saved.Slides[0].Shapes[2].Transform);
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        Assert.Equal(p + "xfrm", PresentationTests.EntryXml(presentation, "ppt/slides/slide1.xml").Descendants(p + "graphicFrame").Single().Elements().ElementAt(1).Name);
        Assert.Throws<ArgumentNullException>(() => frame.Transform = null);
    }

    [Fact]
    public void StrictFilesAreReadAndWrittenInStrict()
    {
        PptxBuilder builder = Rich();
        builder.Strict = true;
        byte[] file = builder.Build();

        Presentation presentation = Presentation.Load(new MemoryStream(file));

        Assert.True(presentation.IsStrict);
        Assert.Equal("Title Slide", presentation.SlideMasters[0].Layouts[0].Name);
        Assert.Equal("Egap Test Theme", presentation.SlideMasters[0].Theme!.Name);
        AutoShape shape = Assert.IsType<AutoShape>(presentation.Slides[0].Shapes[0]);
        Assert.Equal("Kept", shape.Name);
        Assert.Equal("star5", shape.PresetGeometry);
        Assert.Equal(new ShapeTransform(1, 2, 3, 4), shape.Transform);
        Assert.All(PptxBuilder.Entries(file).Zip(PptxBuilder.Entries(PresentationTests.Saved(presentation))), pair => Assert.Equal(pair.First.Data, pair.Second.Data));

        shape.Name = "Strict still";
        shape.Transform = new ShapeTransform(5, 6, 7, 8);
        shape.TextBody!.Paragraphs[0].Runs[0].Text = "Edited";
        byte[] saved = PresentationTests.Saved(presentation);

        string slide = PptxBuilder.Entry(saved, "ppt/slides/slide1.xml");
        Assert.DoesNotContain("schemas.openxmlformats.org", slide, StringComparison.Ordinal);
        Assert.Contains("xmlns:p=\"http://purl.oclc.org/ooxml/presentationml/main\"", slide, StringComparison.Ordinal);
        Assert.Contains("xmlns:a=\"http://purl.oclc.org/ooxml/drawingml/main\"", slide, StringComparison.Ordinal);
        Presentation reloaded = Presentation.Load(new MemoryStream(saved));
        Assert.True(reloaded.IsStrict);
        Assert.Equal("Strict still", reloaded.Slides[0].Shapes[0].Name);
        Assert.Equal("Edited", ((AutoShape)reloaded.Slides[0].Shapes[0]).TextBody!.Text);
        Assert.Equal(new ShapeTransform(5, 6, 7, 8), reloaded.Slides[0].Shapes[0].Transform);
        Assert.False(PresentationTests.Load(new PptxBuilder()).IsStrict);
    }

    [Fact]
    public void ControlCharactersInTextAreLeftOutOfTheFile()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(PptxBuilder.Rectangle(2, "Box", 0, 0, 10, 10, "x"));
        Presentation presentation = PresentationTests.Load(builder);
        TextRun run = ((AutoShape)presentation.Slides[0].Shapes[0]).TextBody!.Paragraphs[0].Runs[0];

        run.Text = "a\u0001b";

        Assert.Equal("ab", ((AutoShape)PresentationTests.Reload(presentation).Slides[0].Shapes[0]).TextBody!.Text);
    }

    /// <summary>A deck with an unknown part of each kind, extension lists and shape XML the model does not hold.</summary>
    private static PptxBuilder Rich()
    {
        PptxBuilder builder = new()
        {
            SlideTail = "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr><p:transition spd=\"slow\"><p:fade/></p:transition>" + Extensions,
            PresentationTail = "<p:extLst><p:ext uri=\"{EFAFB233-063F-42B5-8137-9DF3F51BA10A}\"><p15:sldGuideLst xmlns:p15=\"http://schemas.microsoft.com/office/powerpoint/2012/main\"/></p:ext></p:extLst>",
            Notes = true,
        };
        builder.Slides.Add(Unknown);
        builder.Slides.Add(PptxBuilder.Rectangle(2, "Second", 0, 0, 10, 10, "two"));
        builder.UnknownParts.Add(("/custom/egap/blob.bin", "application/octet-stream", [1, 2, 3, 4]));
        builder.UnknownParts.Add(("/custom/egap/kept.xml", "application/vnd.egap.test+xml", Encoding.UTF8.GetBytes("<kept xmlns=\"http://example.com/kept\"><child a=\"1\"/></kept>")));
        return builder;
    }

    /// <summary>The element with its attributes in name order, so that a rewritten attribute's place does not count.</summary>
    private static XElement Normalized(XElement element)
    {
        return new XElement(element.Name,
            element.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal),
            element.Nodes().Select(n => n is XElement e ? Normalized(e) : n));
    }
}
