using System.IO.Compression;
using System.Xml.Linq;

using Egap.Documents;

namespace Egap.Presentations.Tests;

/// <summary>Decks made from code: the default template, adding, removing and moving slides, adding shapes, and placeholder inheritance.</summary>
public sealed class BuildingTests
{
    private static readonly byte[] Png = Convert.FromBase64String(PptxBuilder.PngBase64);

    [Fact]
    public void ANewPresentationHasOneMasterWithTheElevenLayouts()
    {
        Presentation presentation = Presentation.Create();

        Assert.Equal(PresentationFormat.Pptx, presentation.Format);
        Assert.Equal(new SlideSize(12_192_000, 6_858_000), presentation.SlideSize);
        Assert.Empty(presentation.Slides);
        SlideMaster master = Assert.Single(presentation.SlideMasters);
        Assert.Equal(
            [SlideLayoutType.Title, SlideLayoutType.TitleAndContent, SlideLayoutType.SectionHeader, SlideLayoutType.TwoObjects, SlideLayoutType.TwoTextAndTwoObjects,
                SlideLayoutType.TitleOnly, SlideLayoutType.Blank, SlideLayoutType.ObjectAndCaption, SlideLayoutType.PictureAndCaption, SlideLayoutType.VerticalText,
                SlideLayoutType.VerticalTitleAndText],
            master.Layouts.Select(l => l.LayoutType));
        Assert.Equal("Title Slide", master.Layouts[0].Name);
        Assert.Equal("Egap", master.Theme!.Name);
        Assert.Null(presentation.NotesMasterPartName);
        Assert.Empty(presentation.Sections);
    }

    [Fact]
    public void ANewPresentationSurvivesASaveAndLoad()
    {
        Presentation presentation = Reload(Presentation.Create());

        Assert.Equal(11, presentation.SlideMasters[0].Layouts.Count);
        Assert.Equal(new SlideSize(12_192_000, 6_858_000), presentation.SlideSize);
        Assert.All(presentation.SlideMasters[0].Layouts, l => Assert.Same(presentation.SlideMasters[0], l.Master));
    }

    [Fact]
    public void TheSavedPackageListsEveryPartsContentType()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].Layouts[1]);
        slide.Shapes.AddPicture(Png);
        slide.NotesText = "Notes";

        using MemoryStream stream = new();
        presentation.Save(stream);
        using ZipArchive zip = new(new MemoryStream(stream.ToArray()));
        XElement types = XDocument.Load(zip.GetEntry("[Content_Types].xml")!.Open()).Root!;
        HashSet<string> defaults = [.. types.Elements().Where(e => e.Name.LocalName == "Default").Select(e => (string)e.Attribute("Extension")!)];
        HashSet<string> overrides = [.. types.Elements().Where(e => e.Name.LocalName == "Override").Select(e => (string)e.Attribute("PartName")!)];
        foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.FullName != "[Content_Types].xml"))
        {
            string extension = Path.GetExtension(entry.FullName).TrimStart('.');
            Assert.True(overrides.Contains("/" + entry.FullName) || defaults.Contains(extension), entry.FullName);
        }

        Assert.Contains("png", defaults);
        Assert.Contains("/ppt/slides/slide1.xml", overrides);
        Assert.Contains("/ppt/notesSlides/notesSlide1.xml", overrides);
    }

    [Fact]
    public void AddedSlidesTakeTheLayoutsPlaceholdersButNotTheFooters()
    {
        Presentation presentation = Presentation.Create();
        SlideLayout comparison = presentation.SlideMasters[0].GetLayout(SlideLayoutType.TwoTextAndTwoObjects)!;

        Slide slide = presentation.Slides.Add(comparison);

        Assert.Same(comparison, slide.Layout);
        Assert.Equal(256u, slide.SlideId);
        Assert.Equal(
            [(PlaceholderType.Title, 0u), (PlaceholderType.Body, 1u), (PlaceholderType.Content, 2u), (PlaceholderType.Body, 3u), (PlaceholderType.Content, 4u)],
            slide.Shapes.Select(s => (s.Placeholder!.Type, s.Placeholder.Index)));
        Assert.All(slide.Shapes, s => Assert.Null(s.Transform));
        Assert.All(slide.Shapes, s => Assert.Equal("", ((AutoShape)s).TextBody!.Text));
        Assert.Equal(slide.Shapes.Count, slide.Shapes.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void SlidesAreInsertedRemovedAndMoved()
    {
        Presentation presentation = Presentation.Create();
        SlideLayout layout = presentation.SlideMasters[0].GetLayout(SlideLayoutType.TitleOnly)!;
        Slide a = presentation.Slides.Add(layout);
        Slide c = presentation.Slides.Add(layout);
        Slide b = presentation.Slides.Insert(1, layout);
        Slide d = presentation.Slides.Insert(0, layout);
        SetTitle(a, "A");
        SetTitle(b, "B");
        SetTitle(c, "C");
        SetTitle(d, "D");

        Assert.Equal([d, a, b, c], presentation.Slides);
        Assert.Equal([259u, 256u, 258u, 257u], presentation.Slides.Select(s => s.SlideId));

        presentation.Slides.Move(d, 3);
        Assert.Equal([a, b, c, d], presentation.Slides);
        Assert.True(presentation.Slides.Remove(b));
        Assert.False(presentation.Slides.Remove(b));
        presentation.Slides.RemoveAt(0);

        Presentation reloaded = Reload(presentation);
        Assert.Equal(["C", "D"], reloaded.Slides.Select(s => ((AutoShape)s.Shapes[0]).TextBody!.Text));
        Assert.Equal([257u, 259u], reloaded.Slides.Select(s => s.SlideId));
        Assert.Equal(-1, presentation.Slides.IndexOf(b));
    }

    [Fact]
    public void ARemovedSlidesPartsGoWithIt()
    {
        Presentation presentation = Presentation.Create();
        SlideLayout layout = presentation.SlideMasters[0].Layouts[6];
        Slide keep = presentation.Slides.Add(layout);
        Slide drop = presentation.Slides.Add(layout);
        keep.Shapes.AddPicture(Png);
        drop.Shapes.AddPicture(Png);
        drop.Shapes.AddPicture(Png.Concat(new byte[] { 0 }).ToArray());
        drop.NotesText = "Gone with the slide";
        drop.Comments.Add(presentation.CommentAuthors.Add("Ada"), "Gone too");

        presentation.Slides.Remove(drop);

        string[] parts = PartNames(presentation);
        Assert.DoesNotContain("ppt/slides/slide2.xml", parts);
        Assert.DoesNotContain("ppt/notesSlides/notesSlide1.xml", parts);
        Assert.DoesNotContain(parts, p => p.StartsWith("ppt/comments/", StringComparison.Ordinal));
        Assert.Contains("ppt/media/image1.png", parts);
        Assert.DoesNotContain("ppt/media/image2.png", parts);
        Assert.Contains("ppt/notesMasters/notesMaster1.xml", parts);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Presentation presentation = Presentation.Create();
        Presentation other = Presentation.Create();
        SlideLayout layout = presentation.SlideMasters[0].Layouts[0];
        Slide slide = presentation.Slides.Add(layout);

        Assert.Throws<ArgumentException>(() => presentation.Slides.Add(other.SlideMasters[0].Layouts[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => presentation.Slides.Insert(3, layout));
        Assert.Throws<ArgumentOutOfRangeException>(() => presentation.Slides.Move(slide, 1));
        Assert.Throws<ArgumentException>(() => other.Slides.Move(slide, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => slide.Shapes.AddShape("rect", new ShapeTransform(0, 0, -1, 1)));
        Assert.Throws<ArgumentException>(() => slide.Shapes.AddShape("", new ShapeTransform(0, 0, 1, 1)));
        Assert.Throws<ArgumentException>(() => slide.Shapes.AddPicture(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void ShapesAreAddedWithIdsUniqueOnTheSlide()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].GetLayout(SlideLayoutType.TitleOnly)!);

        AutoShape box = slide.Shapes.AddShape("roundRect", ShapeTransform.FromPoints(10, 20, 100, 50), "Box");
        AutoShape text = slide.Shapes.AddTextBox(ShapeTransform.FromPoints(10, 100, 200, 30), "Line one\vline two\rSecond");
        GroupShape group = slide.Shapes.AddGroup(ShapeTransform.FromPoints(300, 20, 100, 100));
        AutoShape inner = group.Shapes.AddShape("ellipse", ShapeTransform.FromPoints(300, 20, 50, 50));
        ConnectorShape line = slide.Shapes.AddConnector(ShapeTransform.FromPoints(110, 45, 190, 0));
        line.Start = new ShapeConnection(box.Id, 3);
        line.End = new ShapeConnection(inner.Id, 1);
        PictureShape picture = slide.Shapes.AddPicture(Png);

        Assert.Equal([2u, 3u, 4u, 5u, 6u, 7u], new[] { slide.Shapes[0].Id, box.Id, text.Id, group.Id, inner.Id, line.Id });
        Assert.Equal(8u, picture.Id);

        Slide reloaded = Reload(presentation).Slides[0];
        SlideShapeCollection shapes = reloaded.Shapes;
        Assert.Equal(6, shapes.Count);
        AutoShape readBox = Assert.IsType<AutoShape>(shapes[1]);
        Assert.Equal("roundRect", readBox.PresetGeometry);
        Assert.Equal("Box", readBox.TextBody!.Text);
        Assert.Equal(ShapeTransform.FromPoints(10, 20, 100, 50), readBox.Transform);
        AutoShape readText = Assert.IsType<AutoShape>(shapes[2]);
        Assert.True(readText.IsTextBox);
        Assert.Equal("Line one\vline two\rSecond", readText.TextBody!.Text);
        Assert.Equal(2, readText.TextBody.Paragraphs.Count);
        GroupShape readGroup = Assert.IsType<GroupShape>(shapes[3]);
        Assert.Equal("ellipse", ((AutoShape)Assert.Single(readGroup.Shapes)).PresetGeometry);
        Assert.Equal(ShapeTransform.FromPoints(300, 20, 100, 100), readGroup.ChildArea);
        ConnectorShape readLine = Assert.IsType<ConnectorShape>(shapes[4]);
        Assert.Equal(new ShapeConnection(3, 3), readLine.Start);
        Assert.Equal(new ShapeConnection(6, 1), readLine.End);
        PictureShape readPicture = Assert.IsType<PictureShape>(shapes[5]);
        Assert.Equal("image/png", readPicture.ImageContentType);
        Assert.Equal(Png, readPicture.GetImageData());
        Assert.Equal(new ShapeTransform(0, 0, 9525, 9525), readPicture.Transform);
    }

    [Fact]
    public void TheSameImageIsStoredOnce()
    {
        Presentation presentation = Presentation.Create();
        Slide first = presentation.Slides.Add(presentation.SlideMasters[0].Layouts[6]);
        Slide second = presentation.Slides.Add(presentation.SlideMasters[0].Layouts[6]);

        PictureShape a = first.Shapes.AddPicture(Png);
        PictureShape b = second.Shapes.AddPicture(Png);

        Assert.Equal("/ppt/media/image1.png", a.ImagePartName);
        Assert.Equal(a.ImagePartName, b.ImagePartName);
        Assert.Single(PartNames(presentation), p => p.StartsWith("ppt/media/", StringComparison.Ordinal));
    }

    [Fact]
    public void ConnectorEndsAreDetached()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].Layouts[6]);
        ConnectorShape line = slide.Shapes.AddConnector(new ShapeTransform(0, 0, 100, 100), "bentConnector3");
        line.End = new ShapeConnection(5, 2);
        line.Start = new ShapeConnection(4, 0);

        line.Start = null;

        Assert.Null(line.Start);
        Assert.Equal(new ShapeConnection(5, 2), line.End);
        Assert.Equal("bentConnector3", line.PresetGeometry);
    }

    [Fact]
    public void ShapesAreRemoved()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].GetLayout(SlideLayoutType.Title)!);
        slide.Shapes.AddShape("rect", new ShapeTransform(0, 0, 10, 10));
        Slide other = presentation.Slides.Add(presentation.SlideMasters[0].GetLayout(SlideLayoutType.Title)!);

        Assert.True(slide.Shapes.Remove(slide.Shapes[0]));
        Assert.False(slide.Shapes.Remove(other.Shapes[0]));

        SlideShapeCollection shapes = Reload(presentation).Slides[0].Shapes;
        Assert.Equal(2, shapes.Count);
        Assert.Equal(PlaceholderType.Subtitle, shapes[0].Placeholder!.Type);
    }

    [Fact]
    public void SettingTextKeepsTheFirstRunsFormatting()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(PptxBuilder.Rectangle(2, "Box", 0, 0, 100, 100, "Old"));
        Presentation presentation = PresentationTests.Load(builder);
        AutoShape box = (AutoShape)presentation.Slides[0].Shapes[0];
        box.TextBody!.Paragraphs[0].Runs[0].Element.Element(Pptx.PptxNames.A + "rPr")!.SetAttributeValue("b", "1");

        box.TextBody.Text = "New\rLines";

        Assert.Equal("New\rLines", box.TextBody.Text);
        Assert.All(box.TextBody.Paragraphs, p => Assert.Equal("1", (string?)p.Runs[0].Element.Element(Pptx.PptxNames.A + "rPr")!.Attribute("b")));
        Assert.Equal("New\rLines", ((AutoShape)Reload(presentation).Slides[0].Shapes[0]).TextBody!.Text);
    }

    [Fact]
    public void PlaceholdersInheritTheirPositionFromTheLayoutAndMaster()
    {
        Presentation presentation = Presentation.Create();
        SlideMaster master = presentation.SlideMasters[0];
        Slide content = presentation.Slides.Add(master.GetLayout(SlideLayoutType.TitleAndContent)!);
        Slide title = presentation.Slides.Add(master.GetLayout(SlideLayoutType.Title)!);

        SlideShape contentTitle = content.Shapes[0];
        SlideShape layoutTitle = contentTitle.InheritedPlaceholder!;
        Assert.Same(content.Layout, layoutTitle.Owner);
        Assert.Null(layoutTitle.Transform);
        SlideShape masterTitle = layoutTitle.InheritedPlaceholder!;
        Assert.Same(master, masterTitle.Owner);
        Assert.Null(masterTitle.InheritedPlaceholder);
        Assert.Equal(masterTitle.Transform, contentTitle.EffectiveTransform);

        // The content placeholder (obj, idx 1) maps to the master's body.
        Assert.Equal(PlaceholderType.Body, content.Shapes[1].InheritedPlaceholder!.InheritedPlaceholder!.Placeholder!.Type);

        // The title slide's layout places its own centred title and subtitle.
        SlideShape centred = title.Shapes[0];
        Assert.Equal(PlaceholderType.CenteredTitle, centred.Placeholder!.Type);
        Assert.Equal(centred.InheritedPlaceholder!.Transform, centred.EffectiveTransform);
        Assert.Equal(PlaceholderType.Title, centred.InheritedPlaceholder.InheritedPlaceholder!.Placeholder!.Type);

        // A slide's own position wins.
        content.Shapes[0].Transform = new ShapeTransform(1, 2, 3, 4);
        Assert.Equal(new ShapeTransform(1, 2, 3, 4), content.Shapes[0].EffectiveTransform);
        Assert.Null(content.Shapes.AddShape("rect", new ShapeTransform(0, 0, 1, 1)).InheritedPlaceholder);
    }

    [Fact]
    public void ASlidePlaceholderMatchesByIndexBeforeType()
    {
        PptxBuilder builder = new();
        builder.Layouts[0] = ("obj", "Two bodies",
            PptxBuilder.Placeholder(2, "Left", "body", 1, 100, 100, 100, 100, "")
            + PptxBuilder.Placeholder(3, "Right", "body", 2, 500, 100, 100, 100, ""));
        builder.Slides.Add(PptxBuilder.Placeholder(2, "Mine", "body", 2, null, null, null, null, "Text"));

        SlideShape shape = PresentationTests.Load(builder).Slides[0].Shapes[0];

        Assert.Equal("Right", shape.InheritedPlaceholder!.Name);
        Assert.Equal(new ShapeTransform(500, 100, 100, 100), shape.EffectiveTransform);
    }

    [Fact]
    public void AStrictPresentationGetsStrictPartsForNewSlides()
    {
        PptxBuilder builder = new() { Strict = true };
        Presentation presentation = PresentationTests.Load(builder);
        presentation.Slides.Add(presentation.SlideMasters[0].Layouts[0]).Shapes.AddShape("rect", new ShapeTransform(0, 0, 10, 10), "S");

        using MemoryStream stream = new();
        presentation.Save(stream);
        using ZipArchive zip = new(new MemoryStream(stream.ToArray()));
        string slide = new StreamReader(zip.Entries.Last(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)).Open()).ReadToEnd();
        Assert.Contains("http://purl.oclc.org/ooxml/presentationml/main", slide, StringComparison.Ordinal);
        Assert.DoesNotContain("http://schemas.openxmlformats.org/presentationml/2006/main", slide, StringComparison.Ordinal);
        Assert.Equal(2, Presentation.Load(new MemoryStream(stream.ToArray())).Slides.Count);
    }

    internal static Presentation Reload(Presentation presentation)
    {
        using MemoryStream stream = new();
        presentation.Save(stream);
        return Presentation.Load(new MemoryStream(stream.ToArray()));
    }

    internal static string[] PartNames(Presentation presentation)
    {
        using MemoryStream stream = new();
        presentation.Save(stream);
        using ZipArchive zip = new(new MemoryStream(stream.ToArray()));
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    private static void SetTitle(Slide slide, string text) => ((AutoShape)slide.Shapes[0]).TextBody!.Text = text;
}
