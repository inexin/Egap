using System.Buffers.Binary;

using Egap.Documents.Doc;
using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>
/// Justified lines before breaks as Word lays them out: a manual line break or display equation stretches the line before
/// it unless <see cref="WordCompatibility.DoNotExpandShiftReturn"/> is set, a page or column break below compatibility mode
/// 15 only; no-break spaces keep their width.
/// </summary>
public class JustifyLineBreaksTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    private const double TextWidth = 468;
    private const string Short = "In olden times when wishing still helped one,";
    private const string Long = "there lived a king whose daughters were all beautiful, but the youngest was so beautiful that the sun itself, which has seen so much, was astonished whenever it shone in her face.";

    [Fact]
    public void NewDocumentsExpandShiftReturn()
    {
        Assert.False(new Document().Settings.WordCompatibility.DoNotExpandShiftReturn);
        Assert.True(new Document().Settings.WordCompatibility.JustifiesLikeWord);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualLineBreak(bool doNotExpand)
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.AddRun(Short);
        paragraph.AddBreak();
        paragraph.AddRun(Long);
        document.Settings.WordCompatibility.DoNotExpandShiftReturn = doNotExpand;

        Document read = RoundTrip(document);
        Assert.Equal(doNotExpand, read.Settings.WordCompatibility.DoNotExpandShiftReturn);
        IReadOnlyList<LineBox> lines = Lines(read.Layout(Isolated), 0);

        Assert.True(lines[0].Line.HardBreak);
        AssertStretched(!doNotExpand, lines[0]);
        AssertStretched(false, lines[^1]);
    }

    [Theory]
    [InlineData(15, BreakKind.Page, false)]
    [InlineData(14, BreakKind.Page, true)]
    [InlineData(12, BreakKind.Page, true)]
    [InlineData(11, BreakKind.Page, true)]
    [InlineData(15, BreakKind.Column, false)]
    [InlineData(14, BreakKind.Column, true)]
    public void PageAndColumnBreaksFollowTheMode(int mode, BreakKind kind, bool stretched)
    {
        foreach (bool doNotExpand in new[] { false, true })
        {
            Document document = Justified(out Paragraph paragraph);
            paragraph.AddRun(Short);
            paragraph.AddBreak(kind);
            paragraph.AddRun(Long);
            document.Settings.CompatibilityMode = mode;
            document.Settings.WordCompatibility.DoNotExpandShiftReturn = doNotExpand;

            DocumentLayout layout = RoundTrip(document).Layout(Isolated);

            // The break sends the rest to the next page; the line before it keeps the paragraph's first page.
            Assert.Equal(2, layout.Pages.Count);
            AssertStretched(stretched, Lines(layout, 0)[0]);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DisplayEquationsBreakLikeManualLineBreaks(bool doNotExpand, bool wordBreaks)
    {
        // Word writes a w:br on each side of a display equation in a paragraph's text; it then breaks no more lines than
        // without them.
        string br = wordBreaks ? "<w:r><w:br/></w:r>" : "";
        Document document = OfficeMathTests.LoadDocx(
            $"<w:p><w:pPr><w:jc w:val='both'/></w:pPr><w:r><w:t>{Short}</w:t></w:r>{br}<m:oMathPara><m:oMath><m:r><m:t>E=mc</m:t></m:r></m:oMath></m:oMathPara>{br}<w:r><w:t>{Long}</w:t></w:r></w:p>");
        document.Settings.WordCompatibility.DoNotExpandShiftReturn = doNotExpand;

        ParagraphBox box = (ParagraphBox)document.Layout(Isolated).FindBoxes(document.Sections[0].Body.Paragraphs.First()).First(b => b.Box is ParagraphBox).Box;
        double width = box.Lines[2].Line.Width;

        Assert.Equal(4, box.Lines.Count);
        AssertStretched(!doNotExpand, box.Lines[0], width);
    }

    [Fact]
    public void DistributeStretchesEveryLine()
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.Format.Alignment = ParagraphAlignment.Distribute;
        paragraph.AddRun(Short);
        paragraph.AddBreak();
        paragraph.AddRun("the end of it");
        document.Settings.WordCompatibility.DoNotExpandShiftReturn = true;

        IReadOnlyList<LineBox> lines = Lines(RoundTrip(document).Layout(Isolated), 0);

        AssertStretched(true, lines[0]);
        AssertStretched(true, lines[^1]);
    }

    [Fact]
    public void NoBreakSpacesKeepTheirWidth()
    {
        // Every word joined by a no-break space but one: Word stretches only the one space.
        Document document = Justified(out Paragraph paragraph);
        string nbsp = ((char)0xA0).ToString();
        paragraph.AddRun(string.Join(nbsp, "In olden times when wishing".Split(' ')) + " " + string.Join(nbsp, "still helped one".Split(' ')));
        paragraph.AddBreak();
        paragraph.AddRun(Long);

        Document read = RoundTrip(document);
        DocumentLayout layout = read.Layout(Isolated);
        Paragraph first = read.Sections[0].Body.Paragraphs.First();
        ParagraphContent content = Bridge(read).Build(first);
        Assert.False(content.Text.Style.StretchNoBreakSpaces);
        Assert.True(content.Text.Style.JustifyLineBreaks);

        // The line still reaches the margin, all its stretch in the one space: the words joined by no-break spaces sit as set.
        LineBox line = Lines(layout, 0)[0];
        AssertStretched(true, line);
        Egami.Fonts.Layout.TextLine natural = Egami.Fonts.Layout.Paragraph.Create(content.Text.RichText, content.Text.Style with { Align = Egami.Fonts.Layout.TextAlign.Start })
            .Layout((float)TextWidth).Lines[0];
        int space = content.Text.RichText.Text.IndexOf(' ', StringComparison.Ordinal);
        Assert.Equal(XOf(natural, space - 1), XOf(line.Line, space - 1), 0.01);
        Assert.Equal(XOf(natural, space + 1) + TextWidth - natural.Width, XOf(line.Line, space + 1), 0.01);
    }

    [Fact]
    public void OnlySpacesAfterTheLastTabStretch()
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.AddRun("Name");
        paragraph.AddTab();
        paragraph.AddRun("first second");
        paragraph.AddBreak();
        paragraph.AddRun(Long);

        Document read = RoundTrip(document);
        DocumentLayout layout = read.Layout(Isolated);
        Run text = read.Sections[0].Body.Paragraphs.First().Runs.ElementAt(1);

        AssertStretched(true, Lines(layout, 0)[0]);
        Assert.Equal(72 + 36, LayoutInteraction.TextRects(layout, text).First().Bounds.X, 0.5);
    }

    [Theory]
    [InlineData(ParagraphAlignment.Justify, false, true)]
    [InlineData(ParagraphAlignment.Justify, true, false)]
    [InlineData(ParagraphAlignment.Left, false, false)]
    public void BreakBeforeTheParagraphMarkShowsAnEmptyLine(ParagraphAlignment alignment, bool doNotExpand, bool stretched)
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.Format.Alignment = alignment;
        paragraph.AddRun(Long);
        paragraph.AddBreak();
        document.Settings.WordCompatibility.DoNotExpandShiftReturn = doNotExpand;
        document.Sections[0].Body.AddParagraph("next");

        DocumentLayout layout = RoundTrip(document).Layout(Isolated);
        IReadOnlyList<LineBox> lines = Lines(layout, 0);

        // Word: the long text's lines, then the empty line after the break.
        Assert.True(lines[^2].Line.HardBreak);
        Assert.Equal(lines[^1].Line.Start, lines[^1].Line.End - 1);
        AssertStretched(stretched, lines[^2]);
        Assert.True(lines[^1].Bounds.Height > 5);
    }

    [Fact]
    public void BridgeKeepsLineFeedsOutsideJustifiedWordParagraphs()
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.Format.Alignment = ParagraphAlignment.Left;
        paragraph.AddRun("a");
        paragraph.AddBreak();
        paragraph.AddRun("b");
        paragraph.AddBreak(BreakKind.Page);
        paragraph.AddRun("c");

        ParagraphContent content = Bridge(document).Build(paragraph);

        Assert.Equal("a\nb\nc", content.Text.RichText.Text);
        Assert.False(content.Text.Style.JustifyLineBreaks);
        Assert.True(content.Text.Style.StretchNoBreakSpaces);
    }

    [Theory]
    [InlineData(false, 15, "a\nb\u2029c")]
    [InlineData(true, 15, "a\u2029b\u2029c")]
    [InlineData(false, 14, "a\nb\nc")]
    [InlineData(true, 14, "a\u2029b\nc")]
    public void BridgeMarksBreaksThatEndLikeAParagraph(bool doNotExpand, int mode, string expected)
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.AddRun("a");
        paragraph.AddBreak();
        paragraph.AddRun("b");
        paragraph.AddBreak(BreakKind.Page);
        paragraph.AddRun("c");
        document.Settings.CompatibilityMode = mode;
        document.Settings.WordCompatibility.DoNotExpandShiftReturn = doNotExpand;

        ParagraphContent content = Bridge(document).Build(paragraph);

        Assert.Equal(expected, content.Text.RichText.Text);
        Assert.Equal((4, BreakKind.Page), content.Breaks.Single());
    }

    [Fact]
    public void OtherFormatsJustifyAsTheirApplications()
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.AddRun("a");
        paragraph.AddBreak();
        document.Settings.WordCompatibility.JustifiesLikeWord = false;

        ParagraphContent content = Bridge(document).Build(paragraph);

        Assert.Equal("a\n", content.Text.RichText.Text);
        Assert.False(content.Text.Style.JustifyLineBreaks);
        Assert.True(content.Text.Style.StretchNoBreakSpaces);

        // A copy keeps it.
        Assert.False(((Document)document.Clone()).Settings.WordCompatibility.JustifiesLikeWord);
    }

    [Fact]
    public void OdtDocumentsJustifyAsLibreOffice()
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.AddRun("a");
        using MemoryStream stream = new();
        OdtFormat.Save(document, stream);
        stream.Position = 0;

        Assert.False(OdtFormat.Load(stream).Settings.WordCompatibility.JustifiesLikeWord);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RtfKeepsTheSetting(bool doNotExpand)
    {
        Document document = Justified(out Paragraph paragraph);
        paragraph.AddRun("a");
        document.Settings.WordCompatibility.DoNotExpandShiftReturn = doNotExpand;
        using MemoryStream stream = new();
        RtfFormat.Save(document, stream);
        string rtf = System.Text.Encoding.ASCII.GetString(stream.ToArray());
        stream.Position = 0;

        // RTF says the opposite: \expshrtn expands, and a file without it has the setting, as Word reads it.
        Assert.Equal(!doNotExpand, rtf.Contains("\\expshrtn", StringComparison.Ordinal));
        Assert.Equal(doNotExpand, RtfFormat.Load(stream).Settings.WordCompatibility.DoNotExpandShiftReturn);
        Assert.False(RtfFormat.Parse(@"{\rtf1\ansi\expshrtn x}").Settings.WordCompatibility.DoNotExpandShiftReturn);
        Assert.True(RtfFormat.Parse(@"{\rtf1\ansi x}").Settings.WordCompatibility.DoNotExpandShiftReturn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DocReadsFExpShRtn(bool on)
    {
        // Copts60 at 8 in the DOP: fExpShRtn is bit 13, which Word 16 sets when the document lacks doNotExpandShiftReturn.
        DocBuilder builder = new() { Dop = new byte[84] };
        BinaryPrimitives.WriteUInt16LittleEndian(builder.Dop.AsSpan(8), (ushort)(on ? 0x2000 : 0));
        builder.Run("x").Paragraph();

        Document document = DocReader.Read(builder.Build(), new DocumentLimits(), out _);

        Assert.Equal(!on, document.Settings.WordCompatibility.DoNotExpandShiftReturn);
    }

    private static Document Justified(out Paragraph paragraph)
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.PageWidth = 612;
        section.PageSetup.MarginLeft = 72;
        section.PageSetup.MarginRight = 72;
        section.PageSetup.MarginTop = 72;
        paragraph = section.Body.AddParagraph();
        paragraph.Format.Alignment = ParagraphAlignment.Justify;
        return document;
    }

    private static ParagraphBridge Bridge(Document document) => new(new FontProvider(document, new FontSettings { UseSystemFonts = false }), document);

    private static IReadOnlyList<LineBox> Lines(DocumentLayout layout, int page)
    {
        Paragraph paragraph = layout.Document.Sections[0].Body.Paragraphs.First();
        return ((ParagraphBox)layout.FindBoxes(paragraph).First(b => b.Page == layout.Pages[page] && b.Box is ParagraphBox).Box).Lines;
    }

    /// <summary>The pen x of the glyph for the character at <paramref name="index"/> on a line.</summary>
    private static float XOf(Egami.Fonts.Layout.TextLine line, int index)
    {
        foreach (Egami.Fonts.Layout.LayoutRun run in line.Runs)
        {
            if (run.Glyphs is Egami.Fonts.GlyphRun glyphs)
            {
                for (int g = 0; g < glyphs.Count; g++)
                {
                    if (glyphs.Clusters[g] == index)
                    {
                        return glyphs.Positions[g].X;
                    }
                }
            }
        }

        throw new InvalidOperationException($"No glyph for {index}.");
    }

    private static void AssertStretched(bool stretched, LineBox line, double width = TextWidth)
    {
        if (stretched)
        {
            Assert.Equal(width, line.Line.Width, 0.5);
        }
        else
        {
            Assert.True(line.Line.Width < width - 20, $"the line is {line.Line.Width} wide of {width}");
        }
    }

    private static Document RoundTrip(Document document)
    {
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        stream.Position = 0;
        return DocxFormat.Load(stream);
    }
}
