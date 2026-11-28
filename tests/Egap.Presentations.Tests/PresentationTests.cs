using System.Text;
using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents;

namespace Egap.Presentations.Tests;

/// <summary>The slide model: masters, layouts and slides and their relationships, themes and colour maps.</summary>
public sealed class PresentationTests
{
    [Fact]
    public void MastersLayoutsAndSlidesAreLinked()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(PptxBuilder.Placeholder(2, "Title 1", "ctrTitle", null, null, null, null, null, "First"));
        builder.Slides.Add(PptxBuilder.Placeholder(2, "Title 1", "title", null, null, null, null, null, "Second"));
        builder.SlideLayouts.AddRange([0, 1]);

        Presentation presentation = Load(builder);

        SlideMaster master = Assert.Single(presentation.SlideMasters);
        Assert.Equal("/ppt/slideMasters/slideMaster1.xml", master.PartName);
        Assert.Same(presentation, master.Presentation);
        Assert.Equal(["Title Slide", "Title and Content"], master.Layouts.Select(l => l.Name));
        Assert.Equal(["title", "obj"], master.Layouts.Select(l => l.LayoutType));
        Assert.All(master.Layouts, l => Assert.Same(master, l.Master));

        Assert.Equal(2, presentation.Slides.Count);
        Assert.Same(master.Layouts[0], presentation.Slides[0].Layout);
        Assert.Same(master.Layouts[1], presentation.Slides[1].Layout);
        Assert.Same(master, presentation.Slides[1].Master);
        Assert.Equal([256u, 257u], presentation.Slides.Select(s => s.SlideId));
        Assert.Same(presentation.Slides[1], presentation.Slides.FindById(257));
        Assert.Null(presentation.Slides.FindById(999));
        Assert.Equal("/ppt/slides/slide2.xml", presentation.Slides[1].PartName);
        Assert.Equal("", presentation.Slides[0].Name);
        Assert.False(presentation.Slides[0].Hidden);
        Assert.Null(presentation.Slides[0].NotesPartName);
        Assert.Null(presentation.NotesMasterPartName);
        Assert.Equal(PresentationFormat.Pptx, presentation.Format);
    }

    [Fact]
    public void SlideAndNotesSizesAreInEmusAndPoints()
    {
        Presentation presentation = Load(new PptxBuilder());

        Assert.Equal(new SlideSize(12_192_000, 6_858_000), presentation.SlideSize);
        Assert.Equal(960, presentation.SlideSize.WidthPoints);
        Assert.Equal(540, presentation.SlideSize.HeightPoints);
        Assert.Equal(new SlideSize(6_858_000, 9_144_000), presentation.NotesSize);
        Assert.Equal(new SlideSize(9_144_000, 6_858_000), SlideSize.FromPoints(720, 540));
    }

    [Fact]
    public void SettingTheSlideSizeWritesItAndDropsTheNamedSize()
    {
        PptxBuilder builder = new();
        byte[] file = builder.Build();
        file = PptxBuilder.WithEntry(file, "ppt/presentation.xml", Encoding.UTF8.GetBytes(PptxBuilder.Entry(file, "ppt/presentation.xml")
            .Replace("<p:sldSz cx=\"12192000\" cy=\"6858000\"/>", "<p:sldSz cx=\"9144000\" cy=\"6858000\" type=\"screen4x3\"/>", StringComparison.Ordinal)));
        Presentation presentation = Presentation.Load(new MemoryStream(file));

        presentation.SlideSize = SlideSize.FromPoints(960, 540);
        Presentation saved = Reload(presentation);

        Assert.Equal(new SlideSize(12_192_000, 6_858_000), saved.SlideSize);
        Assert.DoesNotContain("type=", Entry(presentation, "ppt/presentation.xml").Split("sldSz")[1].Split('>')[0], StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => presentation.SlideSize = new SlideSize(100, 6_858_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => presentation.SlideSize = new SlideSize(12_192_000, 60_000_000));
    }

    [Fact]
    public void FormatComesFromTheMainPartsContentType()
    {
        PptxBuilder builder = new() { MainContentType = "application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml" };
        Assert.Equal(PresentationFormat.Pptm, Load(builder).Format);
        builder = new() { MainContentType = "application/vnd.openxmlformats-officedocument.presentationml.template.main+xml" };
        Assert.Equal(PresentationFormat.Potx, Load(builder).Format);
        builder = new() { MainContentType = "application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml" };
        Assert.Equal(PresentationFormat.Ppsx, Load(builder).Format);
    }

    [Fact]
    public void HiddenSlidesAndNotesAreRead()
    {
        PptxBuilder builder = new() { Notes = true, SlideAttributes = " show=\"0\"" };

        Presentation presentation = Load(builder);

        Assert.True(presentation.Slides[0].Hidden);
        Assert.Equal("/ppt/notesSlides/notesSlide1.xml", presentation.Slides[0].NotesPartName);
        Assert.Equal("/ppt/notesMasters/notesMaster1.xml", presentation.NotesMasterPartName);
    }

    [Fact]
    public void TheMasterHasItsTheme()
    {
        Presentation presentation = Load(new PptxBuilder());

        PresentationTheme theme = Assert.IsType<PresentationTheme>(presentation.SlideMasters[0].Theme);
        Assert.Equal("Egap Test Theme", theme.Name);
        Assert.Equal("/ppt/theme/theme1.xml", theme.PartName);
        Assert.Equal("Georgia", theme.MajorLatinFont);
        Assert.Equal("Verdana", theme.MinorLatinFont);
        Assert.Equal("Verdana", theme.GetFont(ThemeFont.MinorLatin));
        Assert.Null(theme.GetFont(ThemeFont.MajorEastAsia));
        Assert.Equal(Rgba8.FromHex("C00000"), theme.GetColor(ThemeColor.Accent1));
        Assert.Equal(Rgba8.FromHex("000000"), theme.GetColor(ThemeColor.Dark1));
    }

    [Fact]
    public void AMissingOrDamagedThemeLeavesTheMasterWithout()
    {
        byte[] file = PptxBuilder.WithEntry(new PptxBuilder().Build(), "ppt/theme/theme1.xml", Encoding.UTF8.GetBytes("<a:theme"));

        Presentation presentation = Presentation.Load(new MemoryStream(file));

        Assert.Null(presentation.SlideMasters[0].Theme);
        Assert.Equal("<a:theme", PptxBuilder.Entry(Saved(presentation), "ppt/theme/theme1.xml"));
    }

    [Fact]
    public void ColorMapsComeFromTheMasterOrAnOverride()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(PptxBuilder.Placeholder(2, "Title 1", "ctrTitle", null, null, null, null, null, "Dark"));
        builder.Slides.Add(PptxBuilder.Placeholder(2, "Title 1", "ctrTitle", null, null, null, null, null, "Light"));
        builder.SlideTail = "<p:clrMapOvr><a:overrideClrMapping bg1=\"dk1\" tx1=\"lt1\" bg2=\"dk2\" tx2=\"lt2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/></p:clrMapOvr>";

        Presentation presentation = Load(builder);
        SlideMaster master = presentation.SlideMasters[0];
        Slide slide = presentation.Slides[0];

        Assert.Equal(ThemeColor.Light1, master.ColorMap[ColorMapSlot.Background1]);
        Assert.Equal(ThemeColor.Dark1, master.ColorMap[ColorMapSlot.Text1]);
        Assert.Null(master.Layouts[0].ColorMapOverride);
        Assert.Same(master.ColorMap, master.Layouts[0].ColorMap);
        ColorMap map = Assert.IsType<ColorMap>(slide.ColorMapOverride);
        Assert.Same(map, slide.ColorMap);
        Assert.Equal(ThemeColor.Dark1, map[ColorMapSlot.Background1]);
        Assert.Equal(ThemeColor.Light2, map[ColorMapSlot.Text2]);
        Assert.Equal(ThemeColor.Accent3, map[ColorMapSlot.Accent3]);
        Assert.Equal(Rgba8.FromHex("000000"), master.Theme!.GetColor(ColorMapSlot.Background1, slide.ColorMap));
        Assert.Equal(Rgba8.FromHex("FFFFFF"), master.Theme.GetColor(ColorMapSlot.Background1, master.ColorMap));
        Assert.Throws<ArgumentOutOfRangeException>(() => map[(ColorMapSlot)99]);
        Assert.Equal(ThemeColor.FollowedHyperlink, ColorMap.Default[ColorMapSlot.FollowedHyperlink]);
    }

    [Fact]
    public void LoadsFromAPathAndAStreamThatCannotSeek()
    {
        byte[] file = new PptxBuilder().Build();
        string path = Path.Combine(Path.GetTempPath(), $"egap-presentation-{Guid.NewGuid():N}.pptx");
        try
        {
            File.WriteAllBytes(path, file);
            Presentation fromPath = Presentation.Load(path);
            Assert.Single(fromPath.Slides);

            string copy = path + ".saved.pptx";
            fromPath.Save(copy);
            Assert.Single(Presentation.Load(copy).Slides);
            File.Delete(copy);
        }
        finally
        {
            File.Delete(path);
        }

        Presentation fromStream = Presentation.Load(new ForwardOnlyStream(file));
        Assert.Single(fromStream.Slides);
    }

    [Fact]
    public void ASlideWhosePartIsMissingIsLeftOut()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(PptxBuilder.Rectangle(2, "One", 0, 0, 10, 10, "one"));
        builder.Slides.Add(PptxBuilder.Rectangle(2, "Two", 0, 0, 10, 10, "two"));
        byte[] file = PptxBuilder.WithEntry(builder.Build(), "ppt/slides/slide1.xml", null);

        Presentation presentation = Presentation.Load(new MemoryStream(file));

        Slide slide = Assert.Single(presentation.Slides);
        Assert.Equal("Two", slide.Shapes[0].Name);
    }

    internal static Presentation Load(PptxBuilder builder) => Presentation.Load(new MemoryStream(builder.Build()));

    internal static byte[] Saved(Presentation presentation)
    {
        using MemoryStream output = new();
        presentation.Save(output);
        return output.ToArray();
    }

    internal static Presentation Reload(Presentation presentation) => Presentation.Load(new MemoryStream(Saved(presentation)));

    internal static string Entry(Presentation presentation, string name) => PptxBuilder.Entry(Saved(presentation), name);

    internal static XDocument EntryXml(Presentation presentation, string name) => XDocument.Parse(Entry(presentation, name));

    /// <summary>A stream that reads forward only, as a network stream does.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
