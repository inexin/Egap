using System.IO.Compression;
using System.Xml.Linq;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>
/// Space between paragraphs as Word lays it out: the larger of space after and space before, automatic (HTML) spacing, and
/// Word's doNotUseHTMLParagraphAutoSpacing, which adds the two up and makes automatic spacing fixed.
/// </summary>
public class WordAutoSpacingTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Theory]
    [InlineData(false, 12)]
    [InlineData(true, 18)]
    public void SpaceBetweenParagraphsIsTheLargerUnlessTheFlagAddsThemUp(bool fixedSpacing, double expected)
    {
        Document document = new();
        Body body = Body(document);
        Paragraph first = body.AddParagraph("first");
        Paragraph second = body.AddParagraph("second");
        first.Format.SpaceAfter = 6;
        second.Format.SpaceBefore = 12;
        document.Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing = fixedSpacing;

        Assert.Equal(expected, Gap(document.Layout(Isolated), first, second), 1);
    }

    [Theory]
    [InlineData(false, 14)]
    [InlineData(true, 15)]
    public void AutomaticSpacingIgnoresTheStatedValue(bool fixedSpacing, double expected)
    {
        Document document = new();
        Body body = Body(document);
        body.AddParagraph("lead");
        Paragraph first = Auto(body.AddParagraph("first"));
        Paragraph second = Auto(body.AddParagraph("second"));
        document.Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing = fixedSpacing;

        // HTML's 14 points either way; with the flag 10 after and 5 before, added up.
        Assert.Equal(expected, Gap(document.Layout(Isolated), first, second), 1);
    }

    [Fact]
    public void AutomaticSpacingAndAnExplicitOneTakeTheLarger()
    {
        Document document = new();
        Body body = Body(document);
        body.AddParagraph("lead");
        Paragraph auto = Auto(body.AddParagraph("auto"));
        Paragraph small = body.AddParagraph("small");
        Paragraph large = body.AddParagraph("large");
        small.Format.SpaceBefore = 8;
        small.Format.SpaceAfter = 0;
        large.Format.SpaceBefore = 20;

        DocumentLayout layout = document.Layout(Isolated);

        Assert.Equal(14, Gap(layout, auto, small), 1);
        Assert.Equal(20, Gap(layout, small, large), 1);
    }

    [Fact]
    public void NoAutomaticSpaceBeforeTheFirstParagraph()
    {
        Document document = new();
        Paragraph first = Auto(Body(document).AddParagraph("first"));

        Assert.Equal(72, Top(document.Layout(Isolated), first), 1);
    }

    [Fact]
    public void NoAutomaticSpaceBetweenItemsOfOneList()
    {
        Document document = new();
        Body body = Body(document);
        body.AddParagraph("lead");
        List list = document.Lists.AddNumbered();
        List again = document.Lists.Add(list.Definition);
        List other = document.Lists.AddBulleted();
        Paragraph a = Auto(body.AddParagraph("a"));
        Paragraph b = Auto(body.AddParagraph("b"));
        Paragraph c = Auto(body.AddParagraph("c"));
        Paragraph d = Auto(body.AddParagraph("d"));
        a.SetList(list);
        b.SetList(list, 1);
        c.SetList(again);
        d.SetList(other);

        DocumentLayout layout = document.Layout(Isolated);

        Assert.Equal(0, Gap(layout, a, b), 1);
        Assert.Equal(0, Gap(layout, b, c), 1);
        Assert.Equal(14, Gap(layout, c, d), 1);
    }

    [Fact]
    public void NoAutomaticSpaceAtTheEdgesOfACell()
    {
        Document plain = CellDocument(auto: false, out _);
        Document auto = CellDocument(auto: true, out Table table);
        DocumentLayout plainLayout = plain.Layout(Isolated);
        DocumentLayout autoLayout = auto.Layout(Isolated);

        Paragraph[] plainCell = [.. plain.Sections[0].Body.Tables.Single().Rows[0].Cells[0].Paragraphs];
        Paragraph[] autoCell = [.. table.Rows[0].Cells[0].Paragraphs];
        Paragraph[] autoNext = [.. table.Rows[1].Cells[0].Paragraphs];
        Paragraph[] plainNext = [.. plain.Sections[0].Body.Tables.Single().Rows[1].Cells[0].Paragraphs];

        // The cell's first line and the next row start where they do without spacing; inside the cell, HTML's 14 points.
        Assert.Equal(Top(plainLayout, plainCell[0]), Top(autoLayout, autoCell[0]), 1);
        Assert.Equal(14, Gap(autoLayout, autoCell[0], autoCell[1]), 1);
        Assert.Equal(Top(plainLayout, plainNext[0]) + 14, Top(autoLayout, autoNext[0]), 1);
    }

    [Fact]
    public void AutomaticSpaceAfterATable()
    {
        Document document = new();
        Body body = Body(document);
        Table table = body.AddTable(1, 1);
        table.Rows[0].Cells[0].Paragraphs.Single().AddRun("cell");
        Paragraph after = Auto(body.AddParagraph("after"));

        DocumentLayout layout = document.Layout(Isolated);

        double tableBottom = layout.FindBoxes(table).First(b => b.Box is TableBox).Box.Bounds.Bottom;
        Assert.Equal(14, Top(layout, after) - tableBottom, 1);
    }

    [Fact]
    public void OdtDocumentsAddSpacingUp()
    {
        Document document = new();
        Body body = Body(document);
        body.AddParagraph("first").Format.SpaceAfter = 6;
        body.AddParagraph("second").Format.SpaceBefore = 12;
        using MemoryStream stream = new();
        OdtFormat.Save(document, stream);
        Document read = OdtFormat.Load(new MemoryStream(stream.ToArray()));
        Paragraph[] paragraphs = [.. read.Sections[0].Body.Paragraphs];

        Assert.Equal(18, Gap(read.Layout(Isolated), paragraphs[0], paragraphs[1]), 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlagGoesThroughDocx(bool on)
    {
        Document document = new();
        document.Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing = on;
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);

        XElement compat = SettingsOf(stream.ToArray()).Element(W + "compat")!;
        Assert.Equal(on, compat.Element(W + "doNotUseHTMLParagraphAutoSpacing") is not null);
        Assert.Equal(on, DocxFormat.Load(new MemoryStream(stream.ToArray())).Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing);
    }

    [Fact]
    public void FlagIsOffByDefaultAndWrittenAsACompatChild()
    {
        Assert.False(new Document().Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing);
        Document document = new();
        document.Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing = true;
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        XElement compat = SettingsOf(stream.ToArray()).Element(W + "compat")!;

        // A w:compat child comes before the compatSetting entries.
        Assert.Equal("doNotUseHTMLParagraphAutoSpacing", compat.Elements().First().Name.LocalName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlagGoesThroughRtf(bool on)
    {
        Document document = new();
        Body(document).AddParagraph("text");
        document.Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing = on;
        using MemoryStream stream = new();
        RtfFormat.Save(document, stream);
        string rtf = System.Text.Encoding.ASCII.GetString(stream.ToArray());

        Assert.Equal(!on, rtf.Contains(@"\htmautsp", StringComparison.Ordinal));
        Assert.Equal(on, RtfFormat.Load(new MemoryStream(stream.ToArray())).Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing);
    }

    [Fact]
    public void RtfWithoutTheWordAddsSpacingUp()
    {
        byte[] rtf = System.Text.Encoding.ASCII.GetBytes(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0\fs24\sa120 first\par\sb240 second\par}");

        Assert.True(RtfFormat.Load(new MemoryStream(rtf)).Settings.WordCompatibility.DoNotUseHtmlParagraphAutoSpacing);
    }

    private static Document CellDocument(bool auto, out Table table)
    {
        Document document = new();
        Body body = Body(document);
        table = body.AddTable(2, 1);
        foreach (Row row in table.Rows)
        {
            Cell cell = row.Cells[0];
            Paragraph first = cell.Paragraphs.Single();
            first.AddRun("one");
            Paragraph second = cell.AddParagraph("two");
            foreach (Paragraph paragraph in new[] { first, second })
            {
                paragraph.Format.SpaceBefore = 0;
                paragraph.Format.SpaceAfter = 0;
                if (auto)
                {
                    Auto(paragraph);
                }
            }
        }

        return document;
    }

    /// <summary>Gives a paragraph automatic space before and after over stated values of 5 points, as Word writes them.</summary>
    private static Paragraph Auto(Paragraph paragraph)
    {
        paragraph.Format.SpaceBefore = 5;
        paragraph.Format.SpaceAfter = 5;
        paragraph.Format.SpaceBeforeAuto = true;
        paragraph.Format.SpaceAfterAuto = true;
        return paragraph;
    }

    private static Body Body(Document document)
    {
        Section section = document.AddSection();
        section.PageSetup.MarginLeft = 72;
        section.PageSetup.MarginTop = 72;
        return section.Body;
    }

    private static double Gap(DocumentLayout layout, Paragraph above, Paragraph below) => Top(layout, below) - Bottom(layout, above);

    private static double Top(DocumentLayout layout, Paragraph paragraph) => Box(layout, paragraph).Lines[0].Bounds.Y;

    private static double Bottom(DocumentLayout layout, Paragraph paragraph) => Box(layout, paragraph).Lines[^1].Bounds.Bottom;

    private static ParagraphBox Box(DocumentLayout layout, Paragraph paragraph) => (ParagraphBox)layout.FindBoxes(paragraph).First(b => b.Box is ParagraphBox).Box;

    private static XElement SettingsOf(byte[] docx)
    {
        using ZipArchive zip = new(new MemoryStream(docx));
        using Stream part = zip.GetEntry("word/settings.xml")!.Open();
        return XElement.Load(part);
    }
}
