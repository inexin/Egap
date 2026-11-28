using System.Globalization;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Presentations.Tests;

/// <summary>
/// Drawing slides: the picture's size, backgrounds, the master's and layout's shapes, fills, pictures, and the text engine's
/// placement of paragraphs, runs, bullets and fields, checked on the pixels of generated decks.
/// </summary>
public sealed class DrawingTests
{
    private const long Inch = 914400;

    private static readonly SlideRenderOptions Options = new() { Fonts = FontSettings.Portable, Date = new DateTime(2026, 11, 28, 9, 30, 0) };

    [Fact]
    public void RenderIsTheSlideAtNinetySixDotsPerInchByDefault()
    {
        Presentation presentation = Load(Box(""));
        using Raster<Rgba8> raster = presentation.Slides[0].Render(Options);
        Assert.Equal((1280, 720), (raster.Width, raster.Height));

        using Raster<Rgba8> wide = presentation.Slides[0].Render(new SlideRenderOptions { Width = 640, Fonts = FontSettings.Portable });
        Assert.Equal((640, 360), (wide.Width, wide.Height));

        using Raster<Rgba8> fine = presentation.Slides[0].Render(new SlideRenderOptions { Dpi = 144, Fonts = FontSettings.Portable });
        Assert.Equal((1920, 1080), (fine.Width, fine.Height));
    }

    [Fact]
    public void BadSizesAndANullCanvasThrow()
    {
        Slide slide = Load(Box("")).Slides[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => slide.Render(new SlideRenderOptions { Width = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => slide.Render(new SlideRenderOptions { Dpi = -1 }));
        Assert.Throws<ArgumentNullException>(() => slide.Draw(null!));
    }

    [Fact]
    public void DrawOnACanvasMatchesRender()
    {
        Slide slide = Load(Box("Hello", fill: "<a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill>")).Slides[0];
        using Raster<Rgba8> rendered = slide.Render(Options);
        using Raster<Rgba8> drawn = Raster.Create(1280, 720, new Rgba8(255, 255, 255, 255));
        using (RasterCanvas<Rgba8> canvas = new(drawn))
        {
            canvas.Scale(96f / 72, 96f / 72);
            slide.Draw(canvas, Options);
        }

        Assert.Equal(0, Differences(rendered, drawn));
    }

    [Fact]
    public void TheMasterBackgroundFollowsTheColourMap()
    {
        using Raster<Rgba8> plain = Load(Box("")).Slides[0].Render(Options);
        Assert.Equal(new Rgba8(255, 255, 255, 255), At(plain, 5, 5));

        // The slide maps bg1 to dk1, so the master's bg1 background comes out black.
        PptxBuilder builder = Builder(Box(""));
        builder.SlideTail = "<p:clrMapOvr><a:overrideClrMapping bg1=\"dk1\" tx1=\"lt1\" bg2=\"dk2\" tx2=\"lt2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/></p:clrMapOvr>";
        using Raster<Rgba8> dark = PresentationTests.Load(builder).Slides[0].Render(Options);
        Assert.Equal(new Rgba8(0, 0, 0, 255), At(dark, 5, 5));
    }

    [Fact]
    public void ASlideBackgroundWins()
    {
        PptxBuilder builder = Builder(Box(""));
        builder.Slides[0] = Box("");
        using Raster<Rgba8> raster = Render(builder, background: "<p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"00FF00\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>");
        Assert.Equal(new Rgba8(0, 255, 0, 255), At(raster, 5, 5));
    }

    [Fact]
    public void ShapesAreFilledAndOutlinedFromTheTheme()
    {
        string line = "<a:ln w=\"38100\"><a:solidFill><a:schemeClr val=\"accent5\"/></a:solidFill></a:ln>";
        using Raster<Rgba8> raster = Load(Box("", fill: "<a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill>" + line)).Slides[0].Render(Options);
        Assert.Equal(new Rgba8(0xC0, 0, 0, 255), At(raster, 288, 192));
        Assert.Equal(new Rgba8(0x5B, 0x9B, 0xD5, 255), At(raster, 96, 192));
        Assert.Equal(new Rgba8(255, 255, 255, 255), At(raster, 80, 192));
    }

    [Fact]
    public void AStyleReferenceFillsTheShape()
    {
        string style = "<p:style><a:lnRef idx=\"0\"><a:schemeClr val=\"accent6\"/></a:lnRef><a:fillRef idx=\"1\"><a:schemeClr val=\"accent6\"/></a:fillRef>"
            + "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent6\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"lt1\"/></a:fontRef></p:style>";
        using Raster<Rgba8> raster = Load(Box("", extra: style)).Slides[0].Render(Options);
        Assert.Equal(new Rgba8(0x70, 0xAD, 0x47, 255), At(raster, 288, 192));
    }

    [Fact]
    public void MasterShapesShowUnlessTheSlideHidesThem()
    {
        PptxBuilder builder = Builder(Box(""));
        builder.MasterShapes += PptxBuilder.Rectangle(9, "Band", 0, 6 * Inch, 12192000, 858000, "", extra: "").Replace("<p:spPr>", "<p:spPr>", StringComparison.Ordinal)
            .Replace("</a:prstGeom></p:spPr>", "</a:prstGeom><a:solidFill><a:srgbClr val=\"0000FF\"/></a:solidFill></p:spPr>", StringComparison.Ordinal);
        using Raster<Rgba8> shown = PresentationTests.Load(builder).Slides[0].Render(Options);
        Assert.Equal(new Rgba8(0, 0, 255, 255), At(shown, 640, 620));

        builder.SlideAttributes = " showMasterSp=\"0\"";
        using Raster<Rgba8> hidden = PresentationTests.Load(builder).Slides[0].Render(Options);
        Assert.Equal(new Rgba8(255, 255, 255, 255), At(hidden, 640, 620));

        // The master itself draws its shapes and its placeholders' prompt text.
        using Raster<Rgba8> master = PresentationTests.Load(builder).SlideMasters[0].Render(Options);
        Assert.Equal(new Rgba8(0, 0, 255, 255), At(master, 640, 620));
        Assert.NotNull(Ink(master, 80, 40, 1200, 170));
    }

    [Fact]
    public void HiddenShapesAndFramesDrawNothing()
    {
        string hidden = Box("Hidden", fill: "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>").Replace("name=\"Box\"", "name=\"Box\" hidden=\"1\"", StringComparison.Ordinal);
        string table = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"5\" name=\"Table 4\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + "<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"/></a:graphic></p:graphicFrame>";
        using Raster<Rgba8> raster = Load(hidden + table).Slides[0].Render(Options);
        Assert.Null(Ink(raster, 0, 0, 1280, 720));
    }

    [Fact]
    public void PicturesAreStretchedAndCropped()
    {
        PptxBuilder builder = new() { Image = true };
        builder.Slides.Add("<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Picture 3\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>"
            + "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>"
            + $"<p:spPr><a:xfrm><a:off x=\"{Inch}\" y=\"{Inch}\"/><a:ext cx=\"{Inch}\" cy=\"{Inch}\"/></a:xfrm><a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
        using Raster<Rgba8> raster = PresentationTests.Load(builder).Slides[0].Render(Options);
        using Raster<Rgba8> image = Raster.Load<Rgba8>(new MemoryStream(Convert.FromBase64String(PptxBuilder.PngBase64)));
        // The dot is half-transparent red, over the white slide.
        Rgba8 dot = At(image, 0, 0);
        Rgba8 drawn = At(raster, 144, 144);
        Assert.Equal(255, drawn.R);
        Assert.InRange(drawn.G, 255 - dot.A - 2, 255 - dot.A + 2);

        // The ellipse clips the corners away.
        Assert.Equal(new Rgba8(255, 255, 255, 255), At(raster, 98, 98));
    }

    [Fact]
    public void ParagraphsAlignInTheBox()
    {
        (int Left, int Top, int Right, int Bottom) left = InkOf(Paragraph("Text", "algn=\"l\""));
        (int Left, int Top, int Right, int Bottom) center = InkOf(Paragraph("Text", "algn=\"ctr\""));
        (int Left, int Top, int Right, int Bottom) right = InkOf(Paragraph("Text", "algn=\"r\""));

        // The box runs from 96 to 480 pixels across, with 0.1 inch insets.
        Assert.InRange(left.Left, 105, 112);
        Assert.InRange((center.Left + center.Right) / 2.0, 285, 291);
        Assert.InRange(right.Right, 464, 471);
        Assert.InRange((right.Right - right.Left) - (left.Right - left.Left), -1, 1);
    }

    [Fact]
    public void TheAnchorMovesTextDown()
    {
        (int Left, int Top, int Right, int Bottom) top = InkOf(Paragraph("Text", body: "anchor=\"t\""));
        (int Left, int Top, int Right, int Bottom) middle = InkOf(Paragraph("Text", body: "anchor=\"ctr\""));
        (int Left, int Top, int Right, int Bottom) bottom = InkOf(Paragraph("Text", body: "anchor=\"b\""));

        Assert.InRange(top.Top, 100, 115);
        Assert.InRange(bottom.Bottom, 270, 284);
        Assert.InRange((middle.Top + middle.Bottom) / 2.0, 186, 198);
    }

    [Fact]
    public void LineSpacingSetsThePitch()
    {
        string two = "<a:r><a:rPr lang=\"en-US\" sz=\"2000\"/><a:t>H</a:t></a:r><a:br><a:rPr lang=\"en-US\" sz=\"2000\"/></a:br><a:r><a:rPr lang=\"en-US\" sz=\"2000\"/><a:t>H</a:t></a:r>";
        List<(int Top, int Bottom)> single = Rows(Raw(two, ""));
        List<(int Top, int Bottom)> twice = Rows(Raw(two, "<a:lnSpc><a:spcPct val=\"200000\"/></a:lnSpc>"));
        List<(int Top, int Bottom)> exact = Rows(Raw(two, "<a:lnSpc><a:spcPts val=\"4000\"/></a:lnSpc>"));

        // 20 point text sets 24 point lines (32 pixels); twice that at 200 %, and 40 points exactly.
        Assert.Equal(2, single.Count);
        Assert.InRange(single[1].Top - single[0].Top, 31, 33);
        Assert.InRange(twice[1].Top - twice[0].Top, 63, 65);
        Assert.InRange(exact[1].Top - exact[0].Top, 52, 54);
    }

    [Fact]
    public void SpaceBeforeAndAfterSeparateParagraphs()
    {
        string run = "<a:r><a:rPr lang=\"en-US\" sz=\"2000\"/><a:t>H</a:t></a:r>";
        string Paragraphs(string spacing) => $"<a:p><a:pPr>{spacing}</a:pPr>{run}</a:p><a:p>{run}</a:p>";
        List<(int Top, int Bottom)> plain = Rows(Load(Box(rawBody: Paragraphs(""))));
        List<(int Top, int Bottom)> spaced = Rows(Load(Box(rawBody: Paragraphs("<a:spcAft><a:spcPts val=\"1800\"/></a:spcAft>"))));
        Assert.InRange((spaced[1].Top - spaced[0].Top) - (plain[1].Top - plain[0].Top), 23, 25);
    }

    [Fact]
    public void BulletsAndNumbersSitAtTheIndent()
    {
        string bullet = "marL=\"457200\" indent=\"-457200\"";
        (int Left, int Top, int Right, int Bottom) plain = InkOf(Paragraph("Item", bullet, inner: "<a:buNone/>"));
        (int Left, int Top, int Right, int Bottom) dotted = InkOf(Paragraph("Item", bullet, inner: "<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>"));
        (int Left, int Top, int Right, int Bottom) numbered = InkOf(Paragraph("Item", bullet, inner: "<a:buAutoNum type=\"arabicPeriod\"/>"));

        // Without a bullet the first line starts at the indent; a bullet or number sits there and pushes the text to the margin.
        Assert.InRange(plain.Left, 105, 112);
        Assert.InRange(dotted.Left, 105, 115);
        Assert.InRange(numbered.Left, 105, 115);
        Assert.InRange(dotted.Right - plain.Right, 44, 52);
        Assert.Equal(dotted.Right, numbered.Right);
    }

    [Fact]
    public void AutomaticNumbersCountTheParagraphs()
    {
        string pPr = "<a:pPr marL=\"457200\" indent=\"-457200\"><a:buAutoNum type=\"arabicPeriod\"/></a:pPr>";
        string auto = string.Concat(Enumerable.Range(0, 3).Select(_ => $"<a:p>{pPr}<a:r><a:rPr lang=\"en-US\"/><a:t>x</a:t></a:r></a:p>"));
        string manual = string.Concat(Enumerable.Range(1, 3).Select(i =>
            $"<a:p><a:pPr><a:buNone/><a:tabLst><a:tab pos=\"457200\" algn=\"l\"/></a:tabLst></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>{i}.\tx</a:t></a:r></a:p>"));
        using Raster<Rgba8> numbered = Load(Box(rawBody: auto)).Slides[0].Render(Options);
        using Raster<Rgba8> typed = Load(Box(rawBody: manual)).Slides[0].Render(Options);
        Assert.Equal(0, Differences(numbered, typed));
    }

    [Fact]
    public void FieldsShowTheSlideNumberAndDate()
    {
        static string Field(string type) => $"<a:fld id=\"{{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}}\" type=\"{type}\"><a:rPr lang=\"en-US\"/><a:t>‹#›</a:t></a:fld>";
        Slide numbered = Load(Box(rawBody: $"<a:p>{Field("slidenum")}</a:p>")).Slides[0];
        Slide typed = Load(Box(rawBody: "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>1</a:t></a:r></a:p>")).Slides[0];
        using (Raster<Rgba8> a = numbered.Render(Options))
        using (Raster<Rgba8> b = typed.Render(Options))
        {
            Assert.Equal(0, Differences(a, b));
        }

        string date = Options.Date!.Value.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        Slide dated = Load(Box(rawBody: $"<a:p>{Field("datetime1")}</a:p>")).Slides[0];
        Slide written = Load(Box(rawBody: $"<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{date}</a:t></a:r></a:p>")).Slides[0];
        using Raster<Rgba8> c = dated.Render(Options);
        using Raster<Rgba8> d = written.Render(Options);
        Assert.Equal(0, Differences(c, d));
    }

    [Fact]
    public void CapitalsMatchCapitalLetters()
    {
        using Raster<Rgba8> caps = Load(Box(rawBody: "<a:p><a:r><a:rPr lang=\"en-US\" cap=\"all\"/><a:t>Slide</a:t></a:r></a:p>")).Slides[0].Render(Options);
        using Raster<Rgba8> upper = Load(Box(rawBody: "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>SLIDE</a:t></a:r></a:p>")).Slides[0].Render(Options);
        Assert.Equal(0, Differences(caps, upper));
    }

    [Fact]
    public void RunsTakeTheirColourAndHyperlinksTheThemes()
    {
        using Raster<Rgba8> red = Load(Box(rawBody: "<a:p><a:r><a:rPr lang=\"en-US\" sz=\"4000\" b=\"1\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:rPr><a:t>IIII</a:t></a:r></a:p>")).Slides[0].Render(Options);
        Assert.Equal(new Rgba8(255, 0, 0, 255), Darkest(red));

        PptxBuilder builder = Builder(Box(rawBody: "<a:p><a:r><a:rPr lang=\"en-US\" sz=\"4000\" b=\"1\"><a:hlinkClick r:id=\"rId1\"/></a:rPr><a:t>IIII</a:t></a:r></a:p>"));
        using Raster<Rgba8> link = PresentationTests.Load(builder).Slides[0].Render(Options);
        Assert.Equal(new Rgba8(0x05, 0x63, 0xC1, 255), Darkest(link));
    }

    [Fact]
    public void ShrinkOnOverflowScalesTheFont()
    {
        string run = "<a:p><a:r><a:rPr lang=\"en-US\" sz=\"4000\"/><a:t>H</a:t></a:r></a:p>";
        (int Left, int Top, int Right, int Bottom) full = InkOf(Load(Box(rawBody: run)));
        (int Left, int Top, int Right, int Bottom) half = InkOf(Load(Box(rawBody: run, body: "<a:normAutofit fontScale=\"50000\"/>")));
        double ratio = (half.Bottom - half.Top + 1) / (double)(full.Bottom - full.Top + 1);
        Assert.InRange(ratio, 0.45, 0.56);
    }

    [Fact]
    public void ColumnsFlowTheTextOn()
    {
        string lines = string.Concat(Enumerable.Range(0, 12).Select(_ => "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>Line</a:t></a:r></a:p>"));
        using Raster<Rgba8> one = Load(Box(rawBody: lines)).Slides[0].Render(Options);
        using Raster<Rgba8> two = Load(Box(rawBody: lines, bodyAttributes: "numCol=\"2\" spcCol=\"91440\"")).Slides[0].Render(Options);
        Assert.Null(Ink(one, 300, 96, 480, 288));
        Assert.NotNull(Ink(two, 300, 96, 480, 288));
    }

    [Fact]
    public void RightToLeftParagraphsStartOnTheRight()
    {
        // A right-to-left paragraph's margin and bullet are on the right.
        string pPr = "rtl=\"1\" algn=\"r\" marL=\"457200\" indent=\"-457200\"";
        (int Left, int Top, int Right, int Bottom) plain = InkOf(Paragraph("Text", pPr, inner: "<a:buNone/>"));
        (int Left, int Top, int Right, int Bottom) dotted = InkOf(Paragraph("Text", pPr, inner: "<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>"));
        Assert.InRange(plain.Right, 464, 471);
        Assert.InRange(dotted.Right, 464, 471);
        Assert.True(dotted.Left < plain.Left - 30);
    }

    [Fact]
    public void VerticalTextRunsDownTheBox()
    {
        (int Left, int Top, int Right, int Bottom) across = InkOf(Paragraph("Vertical", body: ""));
        (int Left, int Top, int Right, int Bottom) down = InkOf(Paragraph("Vertical", body: "vert=\"vert\""));
        Assert.True(across.Right - across.Left > across.Bottom - across.Top);
        Assert.True(down.Bottom - down.Top > down.Right - down.Left);
    }

    [Fact]
    public void ARotatedShapeTurnsItsText()
    {
        string turned = Box(rawBody: "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>Turned text</a:t></a:r></a:p>").Replace("<a:xfrm>", "<a:xfrm rot=\"5400000\">", StringComparison.Ordinal);
        (int Left, int Top, int Right, int Bottom) ink = InkOf(Load(turned));
        Assert.True(ink.Bottom - ink.Top > ink.Right - ink.Left);
    }

    [Fact]
    public void LayoutsAndNotesPagesRender()
    {
        PptxBuilder builder = Builder(Box(""));
        builder.Notes = true;
        Presentation presentation = PresentationTests.Load(builder);
        using Raster<Rgba8> layout = presentation.SlideMasters[0].Layouts[0].Render(Options);
        Assert.Equal((1280, 720), (layout.Width, layout.Height));
        using Raster<Rgba8> notes = presentation.Slides[0].Notes!.Render(Options);
        Assert.Equal((720, 960), (notes.Width, notes.Height));
    }

    [Fact]
    public void RenderingTwiceGivesTheSamePicture()
    {
        Slide slide = Paragraph("Again", "algn=\"ctr\"").Slides[0];
        using Raster<Rgba8> first = slide.Render(Options);
        using Raster<Rgba8> second = slide.Render(Options);
        Assert.Equal(0, Differences(first, second));
    }

    [Theory]
    [InlineData("<a:bodyPr numCol=\"5000\" lIns=\"-99999999\" rIns=\"99999999999\"/>", "<a:p><a:r><a:rPr lang=\"en-US\" sz=\"0\"/><a:t>x</a:t></a:r></a:p>")]
    [InlineData("<a:bodyPr><a:normAutofit fontScale=\"-5\" lnSpcReduction=\"900000\"/></a:bodyPr>", "<a:p><a:pPr><a:lnSpc><a:spcPct val=\"-100000\"/></a:lnSpc></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"400000\" spc=\"-99999\"/><a:t>x</a:t></a:r></a:p>")]
    [InlineData("<a:bodyPr vert=\"wordArtVertRtl\" rot=\"nonsense\"/>", "<a:p><a:pPr lvl=\"42\" marL=\"-9999999999\"><a:buAutoNum type=\"romanUcPeriod\" startAt=\"-7\"/></a:pPr><a:r><a:t>x</a:t></a:r></a:p>")]
    [InlineData("<a:bodyPr/>", "<a:p><a:pPr><a:tabLst><a:tab pos=\"-1\"/><a:tab pos=\"99999999999\" algn=\"dec\"/></a:tabLst></a:pPr><a:fld type=\"datetime99\"/><a:r><a:t>\tx</a:t></a:r></a:p>")]
    public void OddValuesStillDraw(string bodyPr, string paragraphs)
    {
        string shape = Box(rawBody: paragraphs).Replace("<a:bodyPr  />", bodyPr, StringComparison.Ordinal);
        using Raster<Rgba8> raster = Load(shape).Slides[0].Render(Options);
        Assert.Equal(1280, raster.Width);
    }

    private static string Box(string text = "", string fill = "", string extra = "", string? rawBody = null, string body = "", string bodyAttributes = "")
    {
        string paragraphs = rawBody ?? (text.Length == 0 ? "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>" : $"<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{text}</a:t></a:r></a:p>");
        string bodyPr = body.StartsWith('<') ? $"<a:bodyPr {bodyAttributes}>{body}</a:bodyPr>" : $"<a:bodyPr {bodyAttributes} {body}/>";
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Box\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{Inch}\" y=\"{Inch}\"/><a:ext cx=\"{4 * Inch}\" cy=\"{2 * Inch}\"/></a:xfrm>"
            + $"<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{fill}</p:spPr>{extra}<p:txBody>{bodyPr}<a:lstStyle/>{paragraphs}</p:txBody></p:sp>";
    }

    private static Presentation Paragraph(string text, string pPr = "", string body = "", string inner = "") =>
        Load(Box(rawBody: $"<a:p><a:pPr {pPr}>{inner}</a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>{text}</a:t></a:r></a:p>", body: body));

    private static Presentation Raw(string runs, string spacing) => Load(Box(rawBody: $"<a:p><a:pPr>{spacing}</a:pPr>{runs}</a:p>"));

    private static PptxBuilder Builder(string shapes)
    {
        PptxBuilder builder = new();
        builder.Slides.Add(shapes);
        return builder;
    }

    private static Presentation Load(string shapes) => PresentationTests.Load(Builder(shapes));

    private static Raster<Rgba8> Render(PptxBuilder builder, string background)
    {
        byte[] package = builder.Build();
        string slide = PptxBuilder.Entry(package, "ppt/slides/slide1.xml").Replace("<p:cSld>", "<p:cSld>" + background, StringComparison.Ordinal);
        byte[] changed = PptxBuilder.WithEntry(package, "ppt/slides/slide1.xml", System.Text.Encoding.UTF8.GetBytes(slide));
        return Presentation.Load(new MemoryStream(changed)).Slides[0].Render(Options);
    }

    private static (int Left, int Top, int Right, int Bottom) InkOf(Presentation presentation)
    {
        using Raster<Rgba8> raster = presentation.Slides[0].Render(Options);
        return Ink(raster, 0, 0, raster.Width, raster.Height) ?? throw new InvalidOperationException("Nothing was drawn.");
    }

    /// <summary>The bounds of the pixels darker than light grey in the rectangle, or null when there are none.</summary>
    private static (int Left, int Top, int Right, int Bottom)? Ink(Raster<Rgba8> raster, int left, int top, int right, int bottom)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = top; y < Math.Min(bottom, raster.Height); y++)
        {
            ReadOnlySpan<Rgba8> row = raster.Row(y);
            for (int x = left; x < Math.Min(right, raster.Width); x++)
            {
                Rgba8 p = row[x];
                if (p.R + p.G + p.B < 600)
                {
                    (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                }
            }
        }

        return x1 < 0 ? null : (x0, y0, x1, y1);
    }

    /// <summary>The runs of rows with ink, top to bottom.</summary>
    private static List<(int Top, int Bottom)> Rows(Presentation presentation)
    {
        using Raster<Rgba8> raster = presentation.Slides[0].Render(Options);
        List<(int Top, int Bottom)> rows = [];
        int start = -1;
        for (int y = 0; y <= raster.Height; y++)
        {
            bool ink = y < raster.Height && Ink(raster, 0, y, raster.Width, y + 1) is not null;
            if (ink && start < 0)
            {
                start = y;
            }
            else if (!ink && start >= 0)
            {
                rows.Add((start, y - 1));
                start = -1;
            }
        }

        return rows;
    }

    private static Rgba8 Darkest(Raster<Rgba8> raster)
    {
        Rgba8 darkest = new(255, 255, 255, 255);
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                if (p.R + p.G + p.B < darkest.R + darkest.G + darkest.B)
                {
                    darkest = p;
                }
            }
        }

        return darkest;
    }

    private static Rgba8 At(Raster<Rgba8> raster, int x, int y) => raster.Row(y)[x];

    private static int Differences(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int count = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgba8> ra = a.Row(y);
            ReadOnlySpan<Rgba8> rb = b.Row(y);
            for (int x = 0; x < ra.Length; x++)
            {
                count += ra[x].Equals(rb[x]) ? 0 : 1;
            }
        }

        return count;
    }
}
