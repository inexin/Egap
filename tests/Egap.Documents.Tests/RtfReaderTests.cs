using System.Text;
using System.Text.RegularExpressions;

using Egap.Documents.Rtf;

namespace Egap.Documents.Tests;

public class RtfReaderTests
{
    private const string Fonts = """{\fonttbl{\f0\froman\fcharset0 Times New Roman;}{\f1\fswiss\fcharset0 Arial;}{\f2\fmodern\fcharset0 Courier New;}{\f3\fnil\fcharset204 Cyr;}{\f4\fnil\fcharset2 Symbol;}}""";

    private const string Colors = """{\colortbl;\red255\green0\blue0;\red0\green0\blue255;}""";

    private static Document Read(string body, out IReadOnlyList<ConversionWarning> warnings, string header = "")
    {
        return RtfFormat.Parse(@"{\rtf1\ansi\ansicpg1252\deff0" + Fonts + Colors + header + body + "}", out warnings);
    }

    private static Document Read(string body, string header = "") => Read(body, out _, header);

    private static List<Paragraph> Paragraphs(Document document) => [.. document.Sections[0].Body.Blocks.OfType<Paragraph>()];

    private static List<Run> Runs(Document document) => [.. document.Descendants<Run>()];

    [Fact]
    public void IdentifyFindsRtfByItsBytesAndExtension()
    {
        Assert.Equal(DocumentFormatKind.Rtf, Document.Identify(new MemoryStream(@"{\rtf1\ansi hello}"u8.ToArray())));
        Assert.Equal(DocumentFormatKind.Rtf, Document.Identify(new MemoryStream([0xEF, 0xBB, 0xBF, (byte)'\n', .. @"{\rtf1 x}"u8.ToArray()])));
        Assert.Equal(DocumentFormatKind.Rtf, Document.Identify(new MemoryStream(@"{\rtf1 x}"u8.ToArray()), "notes.txt"));
        Assert.Equal(DocumentFormatKind.Markdown, Document.Identify(new MemoryStream("{rtf}"u8.ToArray())));
    }

    [Fact]
    public void DocumentLoadReadsAnRtfFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-rtf-{Guid.NewGuid():N}.rtf");
        File.WriteAllText(path, @"{\rtf1\ansi Hello\par}", Encoding.ASCII);
        try
        {
            Document document = Document.Load(path);
            Assert.Equal("Hello", Paragraphs(document)[0].GetText());
            Assert.Equal(path, RtfFormat.Load(path).FileName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ByteOrderMarkAndWhiteSpaceBeforeTheGroupAreAllowed()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'\n', (byte)' ', (byte)'\t', .. @"{\rtf1 x}"u8.ToArray()];
        Assert.Equal("x", RtfFormat.Load(new MemoryStream(bytes)).GetText().Trim());
        string path = Path.Combine(Path.GetTempPath(), $"egap-rtf-{Guid.NewGuid():N}.rtf");
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Equal("x", Document.Load(path).GetText().Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LastParagraphWithoutParKeepsItsFormatting()
    {
        Document document = RtfFormat.Parse(@"{\rtf1\ansi\pard\qc Hello}");
        Assert.Equal(ParagraphAlignment.Center, Paragraphs(document)[0].Format.Alignment);

        const string sheet = @"{\stylesheet{\s0 Normal;}{\s1 heading 1;}{\s2 footnote text;}}";
        document = Read(@"\pard\s1 Title{\super\chftn{\footnote\pard\plain\s2\qr Note}}\par", sheet);
        Paragraph note = (Paragraph)document.Descendants<Footnote>().Single().Story.Blocks[0];
        Assert.Equal("footnote text", note.Style!.Name);
        Assert.Equal(ParagraphAlignment.Right, note.Format.Alignment);
        Assert.Equal("heading 1", Paragraphs(document)[0].Style!.Name);
        document = Read(@"\pard\s1\qc Title{\*\atnid JD}{\*\atnauthor Jane}\chatn{\*\annotation\pard\plain A remark.}\par", sheet);
        Paragraph remark = (Paragraph)document.Descendants<Comment>().Single().Story.Blocks[0];
        Assert.NotEqual("heading 1", remark.Style?.Name);
        Assert.NotEqual(ParagraphAlignment.Center, remark.Format.Alignment);
    }

    [Fact]
    public void NotRtfIsRefused()
    {
        Assert.Throws<DocumentException>(() => RtfFormat.Parse("hello"));
    }

    [Fact]
    public void CharacterFormattingIsRead()
    {
        Document document = Read(@"\f0\fs24 plain {\b bold}{\i\ul\cf1 red}{\f2\fs20 mono}{\super 2}{\strike\caps x}\par");
        List<Run> runs = Runs(document);
        Assert.Equal(["plain ", "bold", "red", "mono", "2", "x"], runs.Select(r => r.Text));
        Assert.True(runs[1].Format.Bold);
        Assert.True(runs[2].Format.Italic);
        Assert.Equal(UnderlineStyle.Single, runs[2].Format.Underline);
        Assert.Equal(DocumentColor.FromHex("FF0000"), runs[2].Format.Color);
        Assert.Equal("Courier New", runs[3].Format.FontAscii);
        Assert.Equal(10, runs[3].Format.Size);
        Assert.Equal(VerticalTextPosition.Superscript, runs[4].Format.VerticalPosition);
        Assert.True(runs[5].Format.Strikethrough);
        Assert.True(runs[5].Format.AllCaps);
        Assert.Equal("Times New Roman", document.Styles.DefaultRunFormat.FontAscii);
        Assert.Equal(12, document.Styles.DefaultRunFormat.Size);
    }

    [Fact]
    public void PlainResetsCharacterFormatting()
    {
        Document document = Read(@"\b bold \plain normal\par");
        List<Run> runs = Runs(document);
        Assert.True(runs[0].Format.Bold);
        Assert.Null(runs[1].Format.Bold);
    }

    [Fact]
    public void CodePagesUnicodeAndSpecialCharactersAreDecoded()
    {
        Document document = Read(@"{\f3 \'cf\'f0\'e8} \u8364? {\uc2\u8212\'97\'97x} caf\'e9\emdash\~\ldblquote q\rdblquote\par");
        Assert.Equal("При € —x café—" + (char)0xA0 + "“q”", Paragraphs(document)[0].GetText());
    }

    [Fact]
    public void AnsicpgSetsTheCodePageOfFontsWithoutACharacterSet()
    {
        Document document = RtfFormat.Parse(@"{\rtf1\ansi\ansicpg1251{\fonttbl{\f0\fnil Arial;}}\f0 \'c0\'e1\par}");
        Assert.Equal("Аб", Paragraphs(document)[0].GetText());
    }

    [Fact]
    public void ParagraphFormattingIsRead()
    {
        Document document = Read(@"\pard\qc\li720\ri360\fi-360\sb240\sa120\sl360\slmult1\keepn Centered\par\pard\qj\tx1440\tqr\tldot\tx5760 Tabs\tab done\par");
        List<Paragraph> paragraphs = Paragraphs(document);
        ParagraphFormat format = paragraphs[0].Format;
        Assert.Equal(ParagraphAlignment.Center, format.Alignment);
        Assert.Equal(36, format.LeftIndent);
        Assert.Equal(18, format.RightIndent);
        Assert.Equal(-18, format.FirstLineIndent);
        Assert.Equal(12, format.SpaceBefore);
        Assert.Equal(6, format.SpaceAfter);
        Assert.Equal(1.5, format.LineSpacing);
        Assert.True(format.KeepWithNext);
        Assert.Equal(ParagraphAlignment.Justify, paragraphs[1].Format.Alignment);
        Assert.Equal(2, paragraphs[1].Format.TabStops!.Count);
        Assert.Equal(TabAlignment.Right, paragraphs[1].Format.TabStops![1].Alignment);
        Assert.Equal(TabLeader.Dot, paragraphs[1].Format.TabStops![1].Leader);
        Assert.Single(paragraphs[1].Inlines.OfType<Tab>());
    }

    [Fact]
    public void StyleSheetMakesStylesWithTheirLinks()
    {
        const string sheet = """
            {\stylesheet{\ql \li0\ri0\widctlpar\f0\fs24 \snext0 Normal;}{\s1\ql \sb240\keepn\b\fs32 \sbasedon0 \snext0 \slink15 \sqformat heading 1;}{\*\cs10 \additive \ssemihidden Default Paragraph Font;}{\*\cs15 \additive \b\fs32 \sbasedon10 \slink1 Heading 1 Char;}{\s20\qc \sbasedon0 \snext20 My Own Style;}}
            """;
        Document document = Read(@"\pard\plain\s1\b\fs32 Title\par\pard\plain\s20 Body\par", sheet);
        List<Paragraph> paragraphs = Paragraphs(document);
        ParagraphStyle heading = Assert.IsType<ParagraphStyle>(paragraphs[0].Style);
        Assert.Equal("Heading1", heading.Id);
        Assert.Equal("heading 1", heading.Name);
        Assert.Equal("Normal", heading.BasedOn!.Id);
        Assert.Equal("Heading 1 Char", heading.Linked!.Name);
        Assert.True(heading.RunFormat.Bold);
        Assert.Equal(16, heading.RunFormat.Size);
        Assert.Equal("MyOwnStyle", paragraphs[1].Style!.Id);
        Assert.Equal("Normal", document.Styles.DefaultParagraphStyle!.Id);
    }

    [Fact]
    public void RepeatedStyleSheetsAndClashingNamesGetUniqueIds()
    {
        // Found by fuzzing: a second style sheet, and a character style named as the paragraph default.
        Document document = Read(@"\pard\s1 A\par", @"{\stylesheet{\s1 heading 1;}{\*\cs2 Normal;}}{\stylesheet{\s3 heading 1;}}");
        Assert.Equal("Heading1", Paragraphs(document)[0].Style!.Id);
        Assert.IsType<ParagraphStyle>(document.Styles.DefaultParagraphStyle);
        Assert.Equal(document.Styles.Count, document.Styles.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void ListTablesNumberParagraphs()
    {
        const string lists = """
            {\*\listtable{\list\listtemplateid1\listhybrid{\listlevel\levelnfc0\levelnfcn0\leveljc0\levelfollow0\levelstartat3{\leveltext\'02\'00.;}{\levelnumbers\'01;}\fi-360\li720}{\listlevel\levelnfc4\levelstartat1{\leveltext\'03(\'01);}{\levelnumbers\'02;}\fi-360\li1440}\listid100}{\list\listtemplateid2\listsimple{\listlevel\levelnfc23{\leveltext\'01\u8226 ?;}{\levelnumbers;}\f4\fi-360\li720}\listid200}}
            {\*\listoverridetable{\listoverride\listid100\listoverridecount0\ls1}{\listoverride\listid200\listoverridecount0\ls2}{\listoverride\listid100\listoverridecount1{\lfolevel\listoverridestartat\levelstartat7}\ls3}}
            """;
        Document document = Read(@"\pard\ls1\ilvl0 One\par\pard\ls1\ilvl1 Sub\par\pard\ls2 Bullet\par\pard\ls3 Seven\par", lists);
        List<Paragraph> paragraphs = Paragraphs(document);
        List list = paragraphs[0].Format.List!;
        Assert.Equal(0, paragraphs[0].Format.ListLevel);
        Assert.Equal(1, paragraphs[1].Format.ListLevel);
        Assert.Equal("%1.", list.Definition.Levels[0].LevelText);
        Assert.Equal(3, list.Definition.Levels[0].StartAt);
        Assert.Equal("(%2)", list.Definition.Levels[1].LevelText);
        Assert.Equal(NumberStyle.LowerLetter, list.Definition.Levels[1].NumberStyle);
        Assert.Equal(36, list.Definition.Levels[0].ParagraphFormat.LeftIndent);
        ListLevel bullet = paragraphs[2].Format.List!.Definition.Levels[0];
        Assert.Equal(NumberStyle.Bullet, bullet.NumberStyle);
        Assert.Equal("•", bullet.LevelText);
        Assert.Equal("Symbol", bullet.RunFormat.FontAscii);
        Assert.Same(list.Definition, paragraphs[3].Format.List!.Definition);
        Assert.Equal(7, paragraphs[3].Format.List!.Overrides[0].StartAt);
    }

    [Fact]
    public void OldPnNumberingMakesAList()
    {
        Document document = Read(@"{\pntext 1.\tab}\pard{\*\pn\pnlvlbody\pndec\pnstart1\pnindent360{\pntxta .}}\fi-360\li360 First\par{\pntext 2.\tab}\pard{\*\pn\pnlvlbody\pndec\pnstart1\pnindent360{\pntxta .}}\fi-360\li360 Second\par");
        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Equal("First", paragraphs[0].GetText());
        Assert.NotNull(paragraphs[0].Format.List);
        Assert.Same(paragraphs[0].Format.List, paragraphs[1].Format.List);
        Assert.Equal("%1.", paragraphs[0].Format.List!.Definition.Levels[0].LevelText);
    }

    [Fact]
    public void TablesAreBuiltFromRowsAndCells()
    {
        Document document = Read(@"\trowd\trgaph108\trleft-108\trautofit1\trhdr\clbrdrt\brdrs\brdrw10 \cellx2000\clvertalc\clcbpat1\cellx5000\pard\intbl A\cell B\cell\row\trowd\trgaph108\trleft-108\trrh-400\cellx2000\cellx5000\pard\intbl C\cell D\par E\cell\row\pard After\par");
        Table table = Assert.IsType<Table>(document.Sections[0].Body.Blocks[0]);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["A", "B"], table.Rows[0].Cells.Select(c => c.GetText().Trim()));
        Assert.Equal(2, table.Rows[1].Cells[1].Blocks.Count);
        Assert.True(table.Rows[0].Format.IsHeader);
        Assert.Equal(RowHeightRule.Exactly, table.Rows[1].Format.HeightRule);
        Assert.Equal(20, table.Rows[1].Format.Height);
        Assert.Equal([105.4, 150], table.Grid!.Select(w => Math.Round(w, 1)));
        Assert.Equal(TableLayoutKind.Autofit, table.Format.Layout);
        Assert.Equal(BorderStyle.Single, table.Rows[0].Cells[0].Format.BorderTop!.Style);
        Assert.Equal(VerticalAlignment.Center, table.Rows[0].Cells[1].Format.VerticalAlignment);
        Assert.Equal(DocumentColor.FromHex("FF0000"), table.Rows[0].Cells[1].Format.Shading!.Fill);
        Assert.Equal("After", Assert.IsType<Paragraph>(document.Sections[0].Body.Blocks[1]).GetText());
    }

    [Fact]
    public void RowDefinitionAfterTheCellsIsUsed()
    {
        Document document = Read(@"\pard\intbl A\cell B\cell C\cell{\trowd\trleft0\cellx1000\cellx2000\cellx4000\row}\pard After\par");
        Table table = Assert.IsType<Table>(document.Sections[0].Body.Blocks[0]);
        Assert.Equal(3, table.Rows[0].Cells.Count);
        Assert.Equal([50, 50, 100], table.Grid);
    }

    [Fact]
    public void MergedCellsSpanAndMerge()
    {
        Document document = Read(@"\trowd\clmgf\cellx1000\clmrg\cellx2000\clvmgf\cellx3000\pard\intbl wide\cell\cell top\cell\row\trowd\cellx1000\cellx2000\clvmrg\cellx3000\pard\intbl a\cell b\cell\cell\row\pard\par");
        Table table = Assert.IsType<Table>(document.Sections[0].Body.Blocks[0]);
        Assert.Equal(2, table.Rows[0].Cells.Count);
        Assert.Equal(2, table.Rows[0].Cells[0].Format.GridSpan);
        Assert.Equal(VerticalMerge.Restart, table.Rows[0].Cells[1].Format.VerticalMerge);
        Assert.Equal(VerticalMerge.Continue, table.Rows[1].Cells[2].Format.VerticalMerge);
    }

    [Fact]
    public void NestedTablesGoInTheirCells()
    {
        Document document = Read(@"\trowd\cellx3000\cellx6000\pard\intbl\itap1 outer\par\pard\intbl\itap2 in1\nestcell in2\nestcell{\*\nesttableprops\trowd\cellx1000\cellx2000\nestrow}{\nonesttables\par}\pard\intbl\itap1 \cell right\cell\row\pard\par");
        Table outer = Assert.IsType<Table>(document.Sections[0].Body.Blocks[0]);
        Cell first = outer.Rows[0].Cells[0];
        Table inner = Assert.IsType<Table>(first.Blocks[1]);
        Assert.Equal(["in1", "in2"], inner.Rows[0].Cells.Select(c => c.GetText().Trim()));
        Assert.Equal("right", outer.Rows[0].Cells[1].GetText().Trim());
        Assert.Equal(2, outer.Rows[0].Cells.Count);
    }

    [Fact]
    public void SectionsColumnsAndPageSetupAreRead()
    {
        Document document = Read(@"\paperw12240\paperh15840\margl1440\margr1440\sectd\pgnstarts1 First\par\sect\sectd\lndscpsxn\pgwsxn15840\pghsxn12240\cols2\colsx720\linebetcol\sbkpage\pgnrestart\pgnstarts5 Second\par");
        Assert.Equal(2, document.Sections.Count);
        PageSetup first = document.Sections[0].PageSetup;
        Assert.Equal(612, first.PageWidth);
        Assert.Equal(72, first.MarginLeft);
        PageSetup second = document.Sections[1].PageSetup;
        Assert.Equal(PageOrientation.Landscape, second.Orientation);
        Assert.Equal(792, second.PageWidth);
        Assert.Equal(2, second.ColumnCount);
        Assert.Equal(36, second.ColumnSpacing);
        Assert.True(second.ColumnSeparator);
        Assert.Equal(5, second.PageNumberStart);
        Assert.Equal("Second", document.Sections[1].Body.GetText().Trim());
    }

    [Fact]
    public void HeadersAndFootersAreStoriesOfTheSection()
    {
        Document document = Read(@"\sectd\titlepg{\header\pard Head {\field{\*\fldinst PAGE}{\fldrslt 1}}\par}{\footerf\pard First foot\par}\pard Body\par");
        Section section = document.Sections[0];
        Assert.Contains("Head", section.HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText());
        Assert.Equal("First foot", section.HeadersFooters[HeaderFooterKind.FooterFirst]!.GetText().Trim());
        Assert.True(section.PageSetup.DifferentFirstPage);
        Assert.Equal("Body", section.Body.GetText().Trim());
    }

    [Fact]
    public void FootnotesAndEndnotesAreRead()
    {
        Document document = Read(@"Text{\super\chftn{\footnote\pard\plain{\super\chftn} The note.\par}} more{\super\chftn{\footnote\ftnalt\pard{\super\chftn} End.\par}}{\super *}{\footnote\pard Custom.\par}\par");
        List<Footnote> notes = [.. document.Descendants<Footnote>()];
        Assert.Equal(3, notes.Count);
        Assert.Equal(NoteKind.Footnote, notes[0].Kind);
        Assert.Equal("The note.", notes[0].Story.GetText().Trim());
        Assert.Equal(NoteKind.Endnote, notes[1].Kind);
        Assert.Equal("*", notes[2].CustomMark);
        Assert.DoesNotContain("*", Paragraphs(document)[0].Inlines.OfType<Run>().Select(r => r.Text));
    }

    [Fact]
    public void FieldsBookmarksAndLinksAreRead()
    {
        Document document = Read(@"{\*\bkmkstart here}Go {\field{\*\fldinst {HYPERLINK ""https://example.com/""}}{\fldrslt {\ul\cf2 there}}}{\*\bkmkend here} {\field\fldlock{\*\fldinst DATE}{\fldrslt today}}\par");
        Paragraph paragraph = Paragraphs(document)[0];
        Field field = paragraph.Inlines.OfType<FieldStart>().First().GetField()!;
        Assert.Contains("HYPERLINK", field.Code);
        Assert.Equal("there", field.Result);
        Assert.True(paragraph.Inlines.OfType<FieldStart>().Last().IsLocked);
        Assert.Equal("here", paragraph.Inlines.OfType<BookmarkStart>().Single().Name);
        Assert.Single(paragraph.Inlines.OfType<BookmarkEnd>());
    }

    [Fact]
    public void CommentsAreReadWithTheirRanges()
    {
        Document document = Read(@"{\*\atrfstart 7}commented{\*\atrfend 7}{\*\atnid JD}{\*\atnauthor Jane Doe}\chatn{\*\annotation{\*\atnref 7}{\*\atndate 130363848}\pard\plain{\chatn} A remark.\par}\par");
        Comment comment = document.Descendants<Comment>().Single();
        Assert.Equal("Jane Doe", comment.Author);
        Assert.Equal("JD", comment.Initials);
        Assert.Equal("A remark.", comment.Story.GetText().Trim());
        Assert.NotNull(comment.Date);
        Assert.Equal(comment.Id, document.Descendants<CommentRangeStart>().Single().CommentId);
        Assert.Equal(comment.Id, document.Descendants<CommentRangeEnd>().Single().CommentId);
    }

    [Fact]
    public void RevisionsAreRead()
    {
        Document document = Read(@"Kept {\revised\revauth1\revdttm130363848 added}{\deleted\revauthdel2 gone}{\crauth1\b{\*\oldcprops\plain} bolded}\par", @"{\*\revtbl{Unknown;}{Ann;}{Bob;}}\revisions ");
        List<Run> runs = Runs(document);
        Assert.Equal(RevisionKind.Insertion, runs[1].Revision!.Kind);
        Assert.Equal("Ann", runs[1].Revision!.Author);
        Assert.NotNull(runs[1].Revision!.Date);
        Assert.Equal(RevisionKind.Deletion, runs[2].Revision!.Kind);
        Assert.Equal("Bob", runs[2].Revision!.Author);
        Assert.Equal("Ann", runs[3].FormatRevision!.Author);
        Assert.True(document.Settings.TrackRevisions);
    }

    [Fact]
    public void PngPicturesAreReadWithSizeScaleAndCrop()
    {
        string hex = Convert.ToHexString(Png());
        Document document = Read(@"{\*\shppict{\pict{\*\picprop{\sp{\sn wzDescription}{\sv A dot}}}\picw10\pich10\picwgoal1440\pichgoal720\picscalex50\picscaley100\piccropl144\pngblip " + hex + @"}}{\nonshppict{\pict\wmetafile8 0100}}\par");
        Picture picture = document.Descendants<Picture>().Single();
        Assert.Equal("image/png", picture.Image.ContentType);
        Assert.Equal(32.4, picture.Width, 3);
        Assert.Equal(36, picture.Height, 3);
        Assert.Equal(0.1, picture.Crop.Left, 3);
        Assert.Equal("A dot", picture.Description);
    }

    [Fact]
    public void BinaryPictureDataIsRead()
    {
        byte[] png = Png();
        byte[] rtf = [.. Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\pict\pngblip\picwgoal200\pichgoal200\bin" + png.Length + " "), .. png, .. "}\\par}"u8.ToArray()];
        Document document = RtfFormat.Load(new MemoryStream(rtf));
        Assert.Equal(png, document.Descendants<Picture>().Single().Image.Bytes.ToArray());
    }

    [Fact]
    public void MetafilesAndBitmapsGetTheirFileHeaders()
    {
        // A metafile header, a SetWindowOrg, a SetWindowExt of 1000 by 500, and the end record.
        byte[] wmf = [1, 0, 9, 0, 0, 3, 0x14, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0,
            5, 0, 0, 0, 0x0B, 2, 0, 0, 0, 0,
            5, 0, 0, 0, 0x0C, 2, 0xF4, 1, 0xE8, 3,
            3, 0, 0, 0, 0, 0];

        // A 1 by 1 24-bit DIB.
        byte[] dib = [40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0];
        Document document = Read(@"{\pict\wmetafile8\picw2540\pich1270\picwgoal1440\pichgoal720 " + Convert.ToHexString(wmf) + @"}{\pict\dibitmap0\picw1\pich1 " + Convert.ToHexString(dib) + @"}\par");
        List<Picture> pictures = [.. document.Descendants<Picture>()];
        Assert.Equal("image/x-wmf", pictures[0].Image.ContentType);
        byte[] metafile = pictures[0].Image.Bytes.ToArray();
        Assert.Equal(1000, BitConverter.ToInt16(metafile, 10));
        Assert.Equal(1000, BitConverter.ToUInt16(metafile, 14));
        Assert.Equal(72, pictures[0].Width);
        Assert.Equal("image/bmp", pictures[1].Image.ContentType);
        Assert.Equal(54, BitConverter.ToInt32(pictures[1].Image.Bytes.ToArray(), 10));
    }

    [Fact]
    public void UnsupportedPicturesAreDroppedWithAWarning()
    {
        Read(@"{\pict\macpict 00ff}\par", out IReadOnlyList<ConversionWarning> warnings);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.PictureDropped);
    }

    [Fact]
    public void TextBoxesAndFloatingPicturesAreRead()
    {
        string hex = Convert.ToHexString(Png());
        Document document = Read(
            @"{\shp{\*\shpinst\shpleft0\shptop0\shpright2880\shpbottom1440\shpbxcolumn\shpbypara\shpwr2\shpz1{\sp{\sn shapeType}{\sv 202}}{\sp{\sn fillColor}{\sv 255}}{\shptxt\pard Inside\par}}{\shprslt boxed}}"
            + @"{\shp{\*\shpinst\shpleft720\shptop0\shpright2160\shpbottom1440\shpbxpage\shpbypage\shpwr3\shpfblwtxt1{\sp{\sn shapeType}{\sv 75}}{\sp{\sn pib}{\sv {\pict\pngblip " + hex + @"}}}}}A\par");
        Shape shape = document.Descendants<Shape>().Single();
        Assert.Equal(144, shape.Width);
        Assert.Equal(72, shape.Height);
        Assert.Equal("Inside", shape.TextBox!.GetText().Trim());
        Assert.Equal(DocumentColor.FromHex("FF0000"), shape.Fill);
        Assert.Equal(WrapMode.Square, shape.Floating!.Wrap);
        Picture picture = document.Descendants<Picture>().Single();
        Assert.Equal(72, picture.Width);
        Assert.Equal(HorizontalAnchor.Page, picture.Floating!.HorizontalAnchor);
        Assert.Equal(36, picture.Floating.HorizontalOffset);
        Assert.Equal(WrapMode.BehindText, picture.Floating.Wrap);
        Assert.DoesNotContain("boxed", document.Sections[0].Body.GetText());
    }

    [Fact]
    public void OtherShapesAndObjectsBecomeIslandsWithTheirPictures()
    {
        string hex = Convert.ToHexString(Png());
        Document document = Read(
            @"{\shp{\*\shpinst\shpright1440\shpbottom1440{\sp{\sn shapeType}{\sv 183}}}{\shprslt{\*\do\dobxcolumn}{\pict\pngblip " + hex + @"}}}"
            + @"{\object\objemb\objw2000\objh1000{\*\objclass Excel.Sheet.12}{\*\objdata 0102}{\result{\pict\pngblip\picwgoal2000\pichgoal1000 " + hex + @"}}}\par",
            out IReadOnlyList<ConversionWarning> warnings);
        List<Island> islands = [.. document.Descendants<Island>()];
        Assert.Equal(IslandKind.Other, islands[0].Kind);
        Assert.Equal("rtf", islands[0].Format);
        Assert.StartsWith(@"{\shp", islands[0].Markup);
        Assert.NotNull(islands[0].Fallback);
        Assert.Equal(IslandKind.OleObject, islands[1].Kind);
        Assert.Equal(100, islands[1].Width);
        Assert.Equal("Excel.Sheet.12", islands[1].Text);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.DrawingDropped);
    }

    [Fact]
    public void MathIsKeptAsAnIsland()
    {
        Document document = Read(@"x {\mmath{\*\moMath{\mr\i y=2}}} z\par");
        Island island = document.Descendants<Island>().Single();
        Assert.Equal(IslandKind.Math, island.Kind);
        Assert.Contains("y=2", island.Text);
        Assert.Contains(@"\moMath", island.Markup);
    }

    [Fact]
    public void UnknownIgnorableGroupsAreKeptAsExtensionData()
    {
        Document document = Read(@"{\*\themedata 0102}A{\*\myword stuff}\par{\*\generator Riched20;}");
        RtfExtension? head = document.Extensions.Get<RtfExtension>("rtf");
        Assert.Equal(@"{\*\themedata 0102}", Assert.Single(head!.Groups!));
        RtfExtension? inline = Paragraphs(document)[0].Extensions.Get<RtfExtension>("rtf");
        Assert.Equal((1, @"{\*\myword stuff}"), Assert.Single(inline!.Inline!));
        Assert.Equal("A", Paragraphs(document)[0].GetText());
    }

    [Fact]
    public void DocumentInformationIsRead()
    {
        Document document = Read("Body\\par", @"{\info{\title The Title}{\author Ann}{\keywords a, b}{\creatim\yr2024\mo5\dy6\hr7\min8}{\*\company Acme}}{\*\userprops{\propname Project}\proptype30{\staticval Egap}}");
        Assert.Equal("The Title", document.Properties.Title);
        Assert.Equal("Ann", document.Properties.Author);
        Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 0, TimeSpan.Zero), document.Properties.Created);
        Assert.Equal("Egap", document.Properties.GetCustom("Project"));
    }

    [Fact]
    public void BrokenHexEscapesDoNotRecurse()
    {
        Document document = RtfFormat.Parse(@"{\rtf1 " + string.Concat(Enumerable.Repeat(@"\'", 50_000)) + @" end\par}");
        Assert.EndsWith("end", document.GetText().Trim());
        document = Read(@"{\*\foo \'}after\par");
        Assert.Equal("after", document.GetText().Trim());
        document = Read(@"{\*\foo \'{}x}after\par");
        Assert.Equal("after", document.GetText().Trim());
    }

    [Fact]
    public void FarApartCellEdgesMakeAFiniteGrid()
    {
        Document document = Read(@"\trowd\trleft-10000000000\cellx-1000000000\cellx1000000000\pard\intbl A\cell B\cell\row");
        Table table = document.Descendants<Table>().Single();
        Assert.All(table.Grid!, width => Assert.True(width >= 0 && double.IsFinite(width)));
        Assert.All(table.Descendants<Cell>(), cell => Assert.True(cell.Format.Width!.Value.Value > 0));
    }

    [Fact]
    public void EmptyAndNestedGroupsInHeaderTablesAreSkipped()
    {
        const string lists = """
            {\*\listtable{}{{\foo x}y}{\list\listtemplateid1{}{{\foo}}{\listlevel\levelnfc0{}{{\foo}}{\leveltext\'02\'00.;}{\levelnumbers\'01;}\li720}\listid100}}
            {\*\listoverridetable{}{{\foo}}{\listoverride\listid100\listoverridecount0{}{{\foo}}\ls1}}
            """;
        Document document = Read(@"\pard\ls1\ilvl0 One\par After\par", lists);
        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Equal("%1.", paragraphs[0].Format.List!.Definition.Levels[0].LevelText);
        Assert.Equal("After", paragraphs[1].GetText().Trim());

        document = Read("Body\\par", @"{\info{}{{\foo}}{\title The Title}}{\*\userprops{}{{\foo}}{\propname Project}{\staticval Egap}}");
        Assert.Equal("The Title", document.Properties.Title);
        Assert.Equal("Egap", document.Properties.GetCustom("Project"));
        Assert.Equal("Body", document.GetText().Trim());
    }

    /// <summary>Every destination the reader knows, and the groups read inside shapes, objects, lists and notes.</summary>
    public static TheoryData<string> DestinationWords() =>
    [
        "fonttbl", "colortbl", "stylesheet", "listtable", "listoverridetable", "revtbl", "info", "userprops", "defchp", "defpap",
        "field", "fldinst", "fldrslt", "footnote", "header", "footerf", "annotation", "atnid", "atnauthor", "atnref", "atnparent",
        "atrfstart", "bkmkstart", "pn", "pntxtb", "upr", "ud", "oldcprops", "oldpprops", "nesttableprops", "do", "pict", "shppict",
        "nonshppict", "shp", "shpgrp", "shpinst", "shptxt", "shprslt", "sp", "sn", "sv", "object", "objdata", "result", "mmath",
        "moMathPara", "list", "listlevel", "leveltext", "listoverride", "lfolevel", "listtext", "pntext", "title", "falt", "unknownword",
    ];

    [Theory]
    [MemberData(nameof(DestinationWords))]
    public void RepeatedDestinationsAreBoundedNotOverflowed(string word)
    {
        const int count = 100_000;
        string repeated = string.Concat(Enumerable.Repeat($@"\{word} ", count));
        string starred = string.Concat(Enumerable.Repeat($@"\*\{word} ", count));
        string opened = string.Concat(Enumerable.Repeat($@"{{\{word} ", count));
        foreach (string rtf in (string[])[$@"{{\rtf1{{\{word} {repeated}}}}}", $@"{{\rtf1{{\*\{word} {starred}}}}}", @"{\rtf1" + opened,
            $@"{{\rtf1{{\shp{{\*\shpinst{{\sp{{\sn a}}{{\sv {repeated}}}}}}}}}}}", $@"{{\rtf1{{\*\listtable{{\list{{\listlevel {repeated}}}}}}}}}"])
        {
            AssertOnlyItsOwnErrors(rtf);
        }
    }

    [Fact]
    public void ExtremeParametersAreNotCrashes()
    {
        // Each numeric parameter of every seed file, set in turn to extremes and to values just past them.
        string[] extremes = ["-2147483648", "2147483647", "-99999999999", "99999999999", "0", "-1"];
        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "rtf"), "*.rtf"))
        {
            string seed = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            foreach (Match match in Regex.Matches(seed, @"\\[a-z]+(-?[0-9]+)"))
            {
                Group number = match.Groups[1];
                foreach (string value in extremes)
                {
                    AssertOnlyItsOwnErrors(seed[..number.Index] + value + seed[(number.Index + number.Length)..]);
                }
            }
        }

        AssertOnlyItsOwnErrors(@"{\rtf1\trowd\trrh-2147483648\cellx100 a\cell\row}");
        AssertOnlyItsOwnErrors(@"{\rtf1{\shp{\*\shpinst\shpleft0\shpright-2147483648\shptop2147483647\shpbottom-2147483648}}}");
    }

    /// <summary>Hostile inputs of about 1-2 MB, one per pattern that could cost more than linear time or make unbounded objects.</summary>
    public static TheoryData<string> HostilePatterns() =>
    [
        "LongFontName", "ManyFonts", "ManyColours", "ManyStyles", "SameNamedStyles", "ManyLists", "ManyListOverrides",
        "LongLevelNumbers", "ManyPnLists", "ManyTabStops", "ManyBookmarks", "ManyComments", "ManyFootnotes", "ManyCells",
        "ManyCellDefinitions", "ManyRows", "ManySections", "ManyRuns", "ManyAuthors", "ManyUserProperties", "LongTitle",
        "ManyFields", "ManyShapes", "ManyKeptGroups", "ManyUnicodeFallbacks",
    ];

    [Theory]
    [Trait("Category", "Slow")]
    [MemberData(nameof(HostilePatterns))]
    public void HostileInputsReadInLinearTime(string pattern)
    {
        string rtf = Hostile(pattern, 1_500_000);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _ = RtfFormat.Parse(rtf);
        }
        catch (DocumentLimitException)
        {
        }

        // Linear work on 1.5 MB takes well under a second; a quadratic pattern takes minutes.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{pattern} took {watch.Elapsed}.");
    }

    private static string Hostile(string pattern, int size)
    {
        (string head, Func<int, string> item, string tail) = pattern switch
        {
            "LongFontName" => (@"{\rtf1{\fonttbl{\f0 ", (Func<int, string>)(_ => @"\'41"), ";}}x}"),
            "ManyFonts" => (@"{\rtf1{\fonttbl", i => $@"{{\f{i} F{i};}}", "}x}"),
            "ManyColours" => (@"{\rtf1{\colortbl;", i => $@"\red{i % 256}\green0\blue0;", "}x}"),
            "ManyStyles" => (@"{\rtf1{\stylesheet", i => $@"{{\s{i} Style {i};}}", "}x}"),
            "SameNamedStyles" => (@"{\rtf1{\stylesheet", i => $@"{{\s{i} Same;}}{{\*\cs{i + 1_000_000} Same;}}", "}x}"),
            "ManyLists" => (@"{\rtf1{\*\listtable", i => $@"{{\list{{\listlevel{{\leveltext\'02\'00.;}}{{\levelnumbers\'01;}}}}\listid{i}}}", "}x}"),
            "ManyListOverrides" => (@"{\rtf1{\*\listtable{\list{\listlevel{\leveltext\'02\'00.;}{\levelnumbers\'01;}}\listid1}}{\*\listoverridetable",
                i => $@"{{\listoverride\listid1\ls{i + 1}}}", @"}\pard\ls5 x\par}"),
            "LongLevelNumbers" => (@"{\rtf1{\*\listtable{\list{\listlevel{\leveltext\'ff" + new string('a', 255) + @";}{\levelnumbers", _ => @"\'01", @";}}\listid1}}x}"),
            "ManyPnLists" => (@"{\rtf1", i => $@"{{\pntext 1}}\pard{{\*\pn\pnlvlbody\pndec\pnstart{i}}}x\par", "}"),
            "ManyTabStops" => (@"{\rtf1\pard", i => $@"\tx{i}", " x}"),
            "ManyBookmarks" => (@"{\rtf1\pard", i => $@"{{\*\bkmkstart b{i}}}x{{\*\bkmkend b{i}}}", "}"),
            "ManyComments" => (@"{\rtf1\pard", i => $@"{{\*\atnid A}}{{\*\atnauthor A}}\chatn{{\*\annotation{{\*\atnref {i}}} c}}", "}"),
            "ManyFootnotes" => (@"{\rtf1\pard", _ => @"x{\footnote n}", "}"),
            "ManyCells" => (@"{\rtf1\trowd\cellx100\pard\intbl", _ => @"a\cell", @"\row}"),
            "ManyCellDefinitions" => (@"{\rtf1\trowd", i => $@"\cellx{i + 1}", @"\pard\intbl a\cell\row}"),
            "ManyRows" => (@"{\rtf1", _ => @"\trowd\cellx100\pard\intbl a\cell\row", "}"),
            "ManySections" => (@"{\rtf1", _ => @"x\sect", "}"),
            "ManyRuns" => (@"{\rtf1\pard", i => i % 2 == 0 ? @"\b x" : @"\b0 x", "}"),
            "ManyAuthors" => (@"{\rtf1{\*\revtbl", i => $@"{{A{i};}}", "}x}"),
            "ManyUserProperties" => (@"{\rtf1{\*\userprops", i => $@"{{\propname P{i}}}\proptype30{{\staticval v}}", "}x}"),
            "LongTitle" => (@"{\rtf1{\info{\title ", _ => @"\u1055{}", "}}x}"),
            "ManyFields" => (@"{\rtf1\pard", _ => @"{\field{\*\fldinst PAGE}{\fldrslt 1}}", "}"),
            "ManyShapes" => (@"{\rtf1\pard", _ => @"{\shp{\*\shpinst\shpleft0\shpright100{\sp{\sn fillColor}{\sv 255}}}}", "}"),
            "ManyKeptGroups" => (@"{\rtf1\pard", _ => @"{\*\unknown x}", "}"),
            "ManyUnicodeFallbacks" => (@"{\rtf1\uc5\pard", _ => @"\u1055 abcde", "}"),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
        };
        StringBuilder text = new(head);
        for (int i = 0; text.Length < size; i++)
        {
            text.Append(item(i));
        }

        return text.Append(tail).ToString();
    }

    /// <summary>Reads the RTF; the only failure allowed is the reader's own, not another exception it wrapped.</summary>
    private static void AssertOnlyItsOwnErrors(string rtf)
    {
        try
        {
            _ = RtfFormat.Parse(rtf);
        }
        catch (DocumentException e)
        {
            Assert.Null(e.InnerException);
        }
    }

    [Fact]
    public void UnicodeInDestinationTextIsKeptWhole()
    {
        // Parse writes each character past U+00FF as \uN{}: the empty group ends its fallback, in every destination.
        const string russian = "Привет";
        const string chinese = "你好世界";
        string rtf = @"{\rtf1{\fonttbl{\f0 " + chinese + @";}}{\stylesheet{\s0 Normal;}{\s1 " + russian + @";}}{\info{\title " + russian + chinese + "}}"
            + @"\pard\s1\f0{\*\bkmkstart " + russian + @"}x{\*\bkmkend " + russian + "}"
            + @"{\*\atnid A}{\*\atnauthor " + chinese + @"}\chatn{\*\annotation " + russian + @"}\par}";
        Document document = RtfFormat.Parse(rtf);
        Assert.Equal(russian + chinese, document.Properties.Title);
        Assert.Equal(russian, Paragraphs(document)[0].Style!.Name);
        Assert.Equal(chinese, Runs(document)[0].Format.FontAscii);
        Assert.Equal(russian, document.Descendants<BookmarkStart>().Single().Name);
        Comment comment = document.Descendants<Comment>().Single();
        Assert.Equal(chinese, comment.Author);
        Assert.Equal(russian, comment.Story.GetText().Trim());
    }

    /// <summary>The ways a character past U+FFFF comes in: as Parse escapes it, bare under \uc0, and as Word writes it.</summary>
    public static TheoryData<string> AstralForms() =>
    [
        "😀",
        @"\u-10179{}\u-8704{}",
        @"\uc0\u-10179\u-8704",
        @"\uc1\u-10179?\u-8704?",
        @"\uc2\u-10179\'3f\'3f\u-8704\'3f\'3f",
    ];

    [Theory]
    [MemberData(nameof(AstralForms))]
    public void CharactersPastTheBasicPlaneStayWhole(string emoji)
    {
        const string gothic = "𐍈";
        string rtf = @"{\rtf1{\fonttbl{\f0 F" + emoji + @";}}{\*\revtbl{Unknown;}{Zhang" + emoji + @";}}{\info{\title T" + emoji + gothic + @"}}"
            + @"\pard\f0 a" + emoji + @"b{\b " + gothic + @"}{\i " + emoji + @"}" + gothic
            + @"{\*\atnid A}{\*\atnauthor W" + emoji + @"}\chatn{\*\annotation c" + emoji + @"}"
            + @"{\deleted\revauthdel1 x}\par}";
        Document document = RtfFormat.Parse(rtf);
        string pair = char.ConvertFromUtf32(0x1F600);
        Assert.Equal("T" + pair + gothic, document.Properties.Title);
        Assert.Equal("F" + pair, Runs(document)[0].Format.FontAscii);
        Assert.StartsWith("a" + pair + "b" + gothic + pair + gothic, Paragraphs(document)[0].GetText(), StringComparison.Ordinal);
        Comment comment = document.Descendants<Comment>().Single();
        Assert.Equal("W" + pair, comment.Author);
        Assert.Equal("c" + pair, comment.Story.GetText().Trim());
        Assert.Equal("Zhang" + pair, document.Descendants<Run>().Single(r => r.Text == "x").Revision!.Author);
        Assert.All(document.Descendants<Run>(), run => AssertWholePairs(run.Text));
    }

    [Fact]
    public void LoneSurrogatesBecomeReplacementCharacters()
    {
        Document document = RtfFormat.Parse(@"{\rtf1{\info{\title a\u-10179{}b}}\pard x\u-10179{}{\b y}\u-8704{}z{\b \u-10179{}}\par}");
        Assert.Equal("a\uFFFDb", document.Properties.Title);
        Assert.Equal("x\uFFFDy\uFFFDz\uFFFD", Paragraphs(document)[0].GetText().TrimEnd());
        Assert.All(document.Descendants<Run>(), run => AssertWholePairs(run.Text));
    }

    private static void AssertWholePairs(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else
            {
                Assert.False(char.IsSurrogate(text[i]), $"A lone surrogate at {i} in \"{text}\".");
            }
        }
    }

    [Fact]
    public void DeepNestingIsRefused()
    {
        string rtf = @"{\rtf1 " + new string('{', 1000) + new string('}', 1000) + "}";
        Assert.Throws<DocumentLimitException>(() => RtfFormat.Parse(rtf));
    }

    [Fact]
    public void BinaryDataPastTheLimitIsRefused()
    {
        RtfLoadOptions options = new() { Limits = new DocumentLimits { MaxPartSize = 100 } };
        Assert.Throws<DocumentLimitException>(() => RtfFormat.Parse(@"{\rtf1 {\pict\pngblip\bin1000 }}", options));
    }

    [Fact]
    public void TruncatedFilesReadWhatIsThere()
    {
        Document document = RtfFormat.Parse(@"{\rtf1\ansi \trowd\cellx1000\pard\intbl A\cell B");
        Assert.Contains("A", document.Sections[0].Body.GetText());
        Assert.Contains("B", document.Sections[0].Body.GetText());
    }

    [Fact]
    public void SeedFilesRead()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "files", "rtf");
        string[] files = Directory.GetFiles(folder, "*.rtf");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            Document document = RtfFormat.Load(file);
            Assert.NotEmpty(document.Sections);
        }
    }

    /// <summary>A 1 by 1 PNG; only its signature matters to the reader.</summary>
    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89];
}
