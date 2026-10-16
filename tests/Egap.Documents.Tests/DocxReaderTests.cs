using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents.Docx;

namespace Egap.Documents.Tests;

/// <summary>The DOCX reader (Phase 6 Build Plan tasks 10 and 11).</summary>
public class DocxReaderTests
{
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Namespaces = "xmlns:w=\"" + WNs + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\""
        + " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\""
        + " xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\""
        + " xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\""
        + " xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:v=\"urn:schemas-microsoft-com:vml\"";

    [Fact]
    public void ParagraphsRunsAndFormatting()
    {
        Document document = Load("""
            <w:p><w:pPr><w:jc w:val="center"/><w:spacing w:before="240" w:after="120" w:line="360" w:lineRule="auto"/><w:ind w:left="720" w:hanging="360"/></w:pPr>
              <w:r><w:rPr><w:b/><w:i w:val="0"/><w:sz w:val="28"/><w:color w:val="FF0000"/></w:rPr><w:t xml:space="preserve">Hello </w:t></w:r>
              <w:r><w:t>world</w:t><w:tab/><w:t>tabbed</w:t><w:br/><w:br w:type="page"/><w:noBreakHyphen/></w:r>
            </w:p>
            """);

        Paragraph paragraph = Body(document).Paragraphs.Single();
        Assert.Equal(ParagraphAlignment.Center, paragraph.Format.Alignment);
        Assert.Equal(12, paragraph.Format.SpaceBefore);
        Assert.Equal(6, paragraph.Format.SpaceAfter);
        Assert.Equal(1.5, paragraph.Format.LineSpacing);
        Assert.Equal(36, paragraph.Format.LeftIndent);
        Assert.Equal(-18, paragraph.Format.FirstLineIndent);
        Run hello = paragraph.Runs.First();
        Assert.Equal("Hello ", hello.Text);
        Assert.True(hello.Format.Bold);
        Assert.False(hello.Format.Italic);
        Assert.Equal(14, hello.Format.Size);
        Assert.Equal(DocumentColor.FromHex("FF0000"), hello.Format.Color);
        Assert.Equal(["Run", "Run", "Tab", "Run", "Break", "Break", "Run"], paragraph.Inlines.Select(i => i.GetType().Name));
        Assert.Equal(BreakKind.Page, paragraph.Inlines.OfType<Break>().Last().Kind);
        Assert.Equal("‑", paragraph.Runs.Last().Text);
    }

    [Fact]
    public void TablesWithGridSpansAndNesting()
    {
        Document document = Load("""
            <w:tbl><w:tblPr><w:tblW w:w="5000" w:type="pct"/><w:tblBorders><w:top w:val="single" w:sz="8" w:color="000000"/></w:tblBorders></w:tblPr>
              <w:tblGrid><w:gridCol w:w="2880"/><w:gridCol w:w="2880"/></w:tblGrid>
              <w:tr><w:trPr><w:tblHeader/></w:trPr><w:tc><w:tcPr><w:gridSpan w:val="2"/><w:shd w:val="clear" w:fill="D9E2F3"/></w:tcPr><w:p><w:r><w:t>Head</w:t></w:r></w:p></w:tc></w:tr>
              <w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc>
                <w:tc><w:tbl><w:tblGrid><w:gridCol w:w="1000"/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>inner</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p/></w:tc></w:tr>
            </w:tbl>
            <w:p/>
            """);

        Table table = Body(document).Tables.Single();
        Assert.Equal([144.0, 144.0], table.Grid);
        Assert.Equal(TableWidth.Percent(100), table.Format.Width);
        Assert.Equal(1, table.Format.BorderTop!.Width);
        Assert.True(table.Rows[0].Format.IsHeader);
        Assert.Equal(2, table.Rows[0].Cells[0].Format.GridSpan);
        Assert.Equal(DocumentColor.FromHex("D9E2F3"), table.Rows[0].Cells[0].Format.Shading!.Fill);
        Assert.Equal("inner", table.Rows[1].Cells[1].Tables.Single().Rows[0].Cells[0].Paragraphs.Single().GetText().TrimEnd('\r'));
    }

    [Fact]
    public void SectionsWithHeadersAndPageSetup()
    {
        Document document = Load("""
            <w:p><w:pPr><w:sectPr><w:pgSz w:w="16838" w:h="11906" w:orient="landscape"/><w:type w:val="nextPage"/></w:sectPr></w:pPr><w:r><w:t>one</w:t></w:r></w:p>
            <w:p><w:r><w:t>two</w:t></w:r></w:p>
            <w:sectPr><w:headerReference w:type="default" r:id="rIdH"/><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:bottom="1440" w:left="1800" w:right="1800" w:header="720" w:footer="720" w:gutter="0"/><w:cols w:num="2" w:space="720"/><w:titlePg/></w:sectPr>
            """,
            parts: [("word/header1.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml", "<w:hdr " + Namespaces + "><w:p><w:r><w:t>Header</w:t></w:r></w:p></w:hdr>", "header", "rIdH")]);

        Assert.Equal(2, document.Sections.Count);
        Assert.Equal(PageOrientation.Landscape, document.Sections[0].PageSetup.Orientation);
        Assert.Equal(841.9, document.Sections[0].PageSetup.PageWidth!.Value, 1);
        PageSetup second = document.Sections[1].PageSetup;
        Assert.Equal(90, second.MarginLeft);
        Assert.Equal(2, second.ColumnCount);
        Assert.True(second.DifferentFirstPage);
        Assert.Equal("Header", document.Sections[1].HeadersFooters[HeaderFooterKind.HeaderPrimary]!.Paragraphs.Single().Runs.Single().Text);
        Assert.Equal("two", document.Sections[1].Body.Paragraphs.Single().Runs.Single().Text);
    }

    [Fact]
    public void FieldsBookmarksAndHyperlinks()
    {
        Document document = Load("""
            <w:p><w:bookmarkStart w:id="0" w:name="intro"/><w:r><w:t>Intro</w:t></w:r><w:bookmarkEnd w:id="0"/>
              <w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>3</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r>
              <w:fldSimple w:instr=" REF intro \h "><w:r><w:t>Intro</w:t></w:r></w:fldSimple>
              <w:hyperlink r:id="rIdL" w:tooltip="tip"><w:r><w:t>link</w:t></w:r></w:hyperlink>
              <w:hyperlink w:anchor="intro"><w:r><w:t>inside</w:t></w:r></w:hyperlink>
            </w:p>
            """, relationships: [("rIdL", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", "https://example.com/", true)]);

        Assert.Equal("Intro", document.GetBookmark("intro")!.Text);
        Field[] fields = [.. document.GetFields()];
        Assert.Equal([FieldKind.Page, FieldKind.Ref], fields.Select(f => f.Kind));
        Assert.Equal("3", fields[0].Result);
        Hyperlink[] links = [.. Body(document).Paragraphs.Single().Inlines.OfType<Hyperlink>()];
        Assert.Equal("https://example.com/", links[0].Address);
        Assert.Equal("tip", links[0].Tooltip);
        Assert.Equal("intro", links[1].Anchor);
    }

    [Fact]
    public void StylesListsAndTheme()
    {
        const string styles = """
            <w:styles NS><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="22"/></w:rPr></w:rPrDefault></w:docDefaults>
              <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
              <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:color w:val="2F5496" w:themeColor="accent1" w:themeShade="BF"/></w:rPr></w:style>
              <w:style w:type="paragraph" w:styleId="ListBullet"><w:name w:val="List Bullet"/><w:pPr><w:numPr><w:numId w:val="1"/></w:numPr></w:pPr></w:style>
            </w:styles>
            """;
        const string numbering = """
            <w:numbering NS><w:abstractNum w:abstractNumId="7"><w:multiLevelType w:val="hybridMultilevel"/>
              <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
              <w:lvl w:ilvl="1"><w:start w:val="3"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%2)"/><w:suff w:val="space"/></w:lvl></w:abstractNum>
              <w:num w:numId="1"><w:abstractNumId w:val="7"/></w:num>
              <w:num w:numId="2"><w:abstractNumId w:val="7"/><w:lvlOverride w:ilvl="1"><w:startOverride w:val="5"/></w:lvlOverride></w:num>
            </w:numbering>
            """;
        const string theme = """
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Custom"><a:themeElements>
              <a:clrScheme name="c"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:accent1><a:srgbClr val="112233"/></a:accent1></a:clrScheme>
              <a:fontScheme name="f"><a:majorFont><a:latin typeface="Georgia"/></a:majorFont><a:minorFont><a:latin typeface="Verdana"/></a:minorFont></a:fontScheme>
            </a:themeElements></a:theme>
            """;
        Document document = Load("""
            <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Title</w:t></w:r></w:p>
            <w:p><w:pPr><w:pStyle w:val="ListBullet"/></w:pPr><w:r><w:t>bullet</w:t></w:r></w:p>
            <w:p><w:pPr><w:numPr><w:ilvl w:val="1"/><w:numId w:val="2"/></w:numPr></w:pPr><w:r><w:t>lettered</w:t></w:r></w:p>
            """, parts:
            [
                ("word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml", styles.Replace("NS", Namespaces, StringComparison.Ordinal), "styles", "rIdS"),
                ("word/numbering.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml", numbering.Replace("NS", Namespaces, StringComparison.Ordinal), "numbering", "rIdN"),
                ("word/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml", theme, "theme", "rIdT"),
            ]);

        ParagraphStyle heading = (ParagraphStyle)document.Styles["Heading1"]!;
        Assert.Equal("heading 1", heading.Name);
        Assert.Same(document.Styles["Normal"], heading.BasedOn);
        Assert.Same(document.Styles["Normal"], document.Styles.DefaultParagraphStyle);
        Assert.Equal(11, document.Styles.DefaultRunFormat.Size);
        Assert.Equal(ThemeColor.Accent1, heading.RunFormat.Color!.Value.Theme);
        Assert.Equal(1 - (0xBF / 255.0), heading.RunFormat.Color!.Value.Shade, 6);
        Assert.Equal(Rgba8.FromHex("112233"), document.Theme.Colors[ThemeColor.Accent1]);
        Assert.Equal("Georgia", document.Theme.MajorLatinFont);
        Assert.Equal("Custom", document.Theme.Name);

        Paragraph[] paragraphs = [.. Body(document).Paragraphs];
        Assert.Equal(0, paragraphs[0].GetEffectiveFormat().OutlineLevel);
        Assert.Equal("•", paragraphs[1].ListLabel!.Text);
        Assert.Equal("e)", paragraphs[2].ListLabel!.Text);
        Assert.Equal(NumberStyle.LowerLetter, document.Lists[2]!.GetLevel(1).NumberStyle);
    }

    [Fact]
    public void NotesCommentsAndRevisions()
    {
        const string footnotes = """
            <w:footnotes NS><w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
              <w:footnote w:id="1"><w:p><w:r><w:footnoteRef/></w:r><w:r><w:t xml:space="preserve"> A note.</w:t></w:r></w:p></w:footnote></w:footnotes>
            """;
        const string comments = """
            <w:comments NS><w:comment w:id="3" w:author="Ann" w:initials="A" w:date="2026-01-02T03:04:05Z"><w:p w14:paraId="00000001"><w:r><w:t>Look</w:t></w:r></w:p></w:comment>
              <w:comment w:id="4" w:author="Bob"><w:p w14:paraId="00000002"><w:r><w:t>Agreed</w:t></w:r></w:p></w:comment></w:comments>
            """;
        const string extended = """
            <w15:commentsEx NS><w15:commentEx w15:paraId="00000001" w15:done="1"/><w15:commentEx w15:paraId="00000002" w15:paraIdParent="00000001"/></w15:commentsEx>
            """;
        Document document = Load("""
            <w:p><w:commentRangeStart w:id="3"/><w:r><w:t>Body</w:t></w:r><w:commentRangeEnd w:id="3"/><w:r><w:commentReference w:id="3"/></w:r><w:r><w:commentReference w:id="4"/></w:r>
              <w:r><w:footnoteReference w:id="1"/></w:r>
              <w:ins w:id="9" w:author="Ann" w:date="2026-01-02T00:00:00Z"><w:r><w:t>added</w:t></w:r></w:ins>
              <w:del w:id="10" w:author="Bob"><w:r><w:delText>gone</w:delText></w:r></w:del>
              <w:r><w:rPr><w:b/><w:rPrChange w:id="11" w:author="Ann"><w:rPr><w:i/></w:rPr></w:rPrChange></w:rPr><w:t>changed</w:t></w:r>
            </w:p>
            """, parts:
            [
                ("word/footnotes.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml", footnotes.Replace("NS", Namespaces, StringComparison.Ordinal), "footnotes", "rIdF"),
                ("word/comments.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml", comments.Replace("NS", Namespaces, StringComparison.Ordinal), "comments", "rIdC"),
                ("word/commentsExtended.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.commentsExtended+xml", extended.Replace("NS", Namespaces, StringComparison.Ordinal), "http://schemas.microsoft.com/office/2011/relationships/commentsExtended", "rIdX"),
            ]);

        Paragraph paragraph = Body(document).Paragraphs.Single();
        Footnote note = paragraph.Inlines.OfType<Footnote>().Single();
        Assert.Equal(" A note.", note.Story.Paragraphs.Single().Runs.Single().Text);
        Comment[] comment = [.. paragraph.Inlines.OfType<Comment>()];
        Assert.Equal(3, comment[0].Id);
        Assert.Equal("Ann", comment[0].Author);
        Assert.Equal("A", comment[0].Initials);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), comment[0].Date);
        Assert.True(comment[0].Done);
        Assert.Same(comment[0], comment[1].ParentComment);
        Assert.Equal(3, paragraph.Inlines.OfType<CommentRangeStart>().Single().CommentId);
        Run[] runs = [.. paragraph.Runs];
        Assert.Equal(RevisionKind.Insertion, runs.Single(r => r.Text == "added").Revision!.Kind);
        Assert.Equal(RevisionKind.Deletion, runs.Single(r => r.Text == "gone").Revision!.Kind);
        Run changed = runs.Single(r => r.Text == "changed");
        Assert.True(((RunFormat)changed.FormatRevision!.OldFormat).Italic);
        Assert.Equal(3, document.Revisions.Count);
        Assert.True(new Comment(document, "x").Id > 4);
    }

    [Fact]
    public void ContentControlsDrawingsAndIslands()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0];
        Document document = Load("""
            <w:sdt><w:sdtPr><w:alias w:val="Box"/><w:tag w:val="t1"/><w:lock w:val="sdtContentLocked"/></w:sdtPr><w:sdtContent><w:p><w:r><w:t>in control</w:t></w:r></w:p></w:sdtContent></w:sdt>
            <w:p><w:sdt><w:sdtPr><w14:checkbox><w14:checked w14:val="1"/></w14:checkbox></w:sdtPr><w:sdtContent><w:r><w:t>X</w:t></w:r></w:sdtContent></w:sdt>
              <w:r><w:drawing><wp:inline><wp:extent cx="1270000" cy="635000"/><wp:docPr id="1" name="p" descr="alt text"/>
                <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:blipFill><a:blip r:embed="rIdI"/><a:srcRect l="10000"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>
              <w:r><mc:AlternateContent><mc:Choice Requires="wps"><w:drawing><wp:anchor behindDoc="1" relativeHeight="5"><wp:positionH relativeFrom="page"><wp:posOffset>127000</wp:posOffset></wp:positionH><wp:positionV relativeFrom="paragraph"><wp:align>top</wp:align></wp:positionV><wp:extent cx="254000" cy="254000"/><wp:wrapNone/><wp:docPr id="2" name="s"/>
                <a:graphic><a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><wps:wsp><wps:spPr><a:prstGeom prst="ellipse"/><a:solidFill><a:srgbClr val="00FF00"/></a:solidFill></wps:spPr><wps:txbx><w:txbxContent><w:p><w:r><w:t>boxed</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice>
                <mc:Fallback><w:pict><v:oval/></w:pict></mc:Fallback></mc:AlternateContent></w:r>
              <m:oMath xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><m:r><m:t>x=1</m:t></m:r></m:oMath>
            </w:p>
            """, relationships: [("rIdI", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", "media/image1.png", false)],
            binaryParts: [("word/media/image1.png", "image/png", png)]);

        ContentControl control = Body(document).Blocks.OfType<ContentControl>().Single();
        Assert.Equal("Box", control.Properties.Title);
        Assert.Equal("t1", control.Properties.Tag);
        Assert.True(control.Properties.LockContents && control.Properties.LockControl);
        Paragraph paragraph = Body(document).Paragraphs.Single();
        InlineContentControl checkbox = paragraph.Inlines.OfType<InlineContentControl>().Single();
        Assert.Equal(ContentControlKind.CheckBox, checkbox.Properties.Kind);
        Assert.True(checkbox.Properties.IsChecked);
        Picture picture = paragraph.Inlines.OfType<Picture>().Single();
        Assert.Equal(100, picture.Width);
        Assert.Equal(50, picture.Height);
        Assert.Equal("alt text", picture.Description);
        Assert.Equal("image/png", picture.Image.ContentType);
        Assert.Equal(png, picture.Image.Bytes.ToArray());
        Assert.Equal(0.1, picture.Crop.Left, 6);
        Shape shape = paragraph.Inlines.OfType<Shape>().Single();
        Assert.Equal(ShapeKind.Ellipse, shape.Kind);
        Assert.Equal(WrapMode.BehindText, shape.Floating!.Wrap);
        Assert.Equal(HorizontalAnchor.Page, shape.Floating.HorizontalAnchor);
        Assert.Equal(10, shape.Floating.HorizontalOffset, 6);
        Assert.Equal(FloatingAlignment.Start, shape.Floating.VerticalAlignment);
        Assert.Equal(5, shape.Floating.ZOrder);
        Assert.Equal(DocumentColor.FromHex("00FF00"), shape.Fill);
        Assert.Equal("boxed", shape.TextBox!.Paragraphs.Single().Runs.Single().Text);
        Island math = paragraph.Inlines.OfType<Island>().Single();
        Assert.Equal(IslandKind.Math, math.Kind);
        Assert.Equal("x=1", math.Text);
        Assert.Contains("oMath", math.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownMarkupIsKeptOnTheNode()
    {
        Document document = Load("""
            <w:p w14:paraId="12345678" w:rsidR="00AB"><w:pPr><w:kinsoku w:val="0"/><w:jc w:val="right"/></w:pPr><w:r><w:rPr><w:eastAsianLayout w:id="1"/><w:b/></w:rPr><w:t>x</w:t></w:r></w:p>
            <w:altChunk r:id="rIdA"/>
            """);

        Paragraph paragraph = Body(document).Paragraphs.Single();
        DocxExtension extension = DocxExtension.Of(paragraph);
        Assert.Equal("kinsoku", Assert.Single(extension.UnknownProperties!["pPr"]).Name.LocalName);
        Assert.Contains(extension.Attributes!, a => a.Name.LocalName == "paraId");
        Assert.Equal("eastAsianLayout", Assert.Single(DocxExtension.Of(paragraph.Runs.Single()).UnknownProperties!["rPr"]).Name.LocalName);
        Assert.Equal("docx", Assert.Single(Body(document).Blocks.OfType<BlockIsland>()).Format);
    }

    [Fact]
    public void EmbeddedFontsAreDeobfuscated()
    {
        byte[] font = new byte[64];
        for (int i = 0; i < font.Length; i++)
        {
            font[i] = (byte)i;
        }

        const string key = "{01234567-89AB-CDEF-0123-456789ABCDEF}";
        byte[] obfuscated = (byte[])font.Clone();
        Assert.True(DocxReader.Deobfuscate(obfuscated, key));
        Assert.NotEqual(font, obfuscated);
        Assert.Equal(0x00 ^ 0xEF, obfuscated[0]);
        Assert.Equal(0x0F ^ 0x01, obfuscated[15]);

        string fontTable = "<w:fonts " + Namespaces + "><w:font w:name=\"Fancy\"><w:embedBold r:id=\"rIdF1\" w:fontKey=\"" + key + "\"/></w:font></w:fonts>";
        Document document = Load("<w:p/>", parts: [("word/fontTable.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml", fontTable, "fontTable", "rIdFT")],
            binaryParts: [("word/fonts/font1.odttf", "application/vnd.openxmlformats-officedocument.obfuscatedFont", obfuscated)],
            partRelationships: [("/word/fontTable.xml", "rIdF1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/font", "fonts/font1.odttf")]);

        EmbeddedFont embedded = Assert.Single(document.EmbeddedFonts);
        Assert.Equal("Fancy", embedded.Family);
        Assert.True(embedded.Bold);
        Assert.False(embedded.Italic);
        Assert.Equal(font, embedded.Data.ToArray());
    }

    [Fact]
    public void StrictFilesReadLikeTransitionalOnes()
    {
        string body = "<w:document xmlns:w=\"http://purl.oclc.org/ooxml/wordprocessingml/main\" w:conformance=\"strict\"><w:body><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>strict</w:t></w:r></w:p></w:body></w:document>";
        OpcPackageWriter writer = new();
        writer.AddXmlPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", XDocument.Parse(body));
        writer.AddRelationship("/", "rId1", "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument", "word/document.xml");
        using MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;

        Run run = Body(DocxFormat.Load(stream)).Paragraphs.Single().Runs.Single();

        Assert.Equal("strict", run.Text);
        Assert.True(run.Format.Bold);
    }

    [Fact]
    public void NotAWordDocument()
    {
        OpcPackageWriter writer = new();
        writer.AddXmlPart("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", XDocument.Parse("<workbook/>"));
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "xl/workbook.xml");
        using MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;

        Assert.Throws<DocumentException>(() => DocxFormat.Load(stream));
    }

    private static Body Body(Document document)
    {
        return document.Sections[0].Body;
    }

    private static Document Load(
        string body,
        (string Name, string ContentType, string Xml, string RelationshipType, string Id)[]? parts = null,
        (string Id, string Type, string Target, bool External)[]? relationships = null,
        (string Name, string ContentType, byte[] Data)[]? binaryParts = null,
        (string Source, string Id, string Type, string Target)[]? partRelationships = null)
    {
        OpcPackageWriter writer = new();
        string xml = "<w:document " + Namespaces + "><w:body>" + body + "</w:body></w:document>";
        writer.AddXmlPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", XDocument.Parse(xml, LoadOptions.PreserveWhitespace));
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        foreach ((string name, string contentType, string partXml, string type, string id) in parts ?? [])
        {
            writer.AddXmlPart("/" + name, contentType, XDocument.Parse(partXml, LoadOptions.PreserveWhitespace));
            string fullType = type.StartsWith("http", StringComparison.Ordinal) ? type : "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + type;
            writer.AddRelationship("/word/document.xml", id, fullType, name["word/".Length..]);
        }

        foreach ((string id, string type, string target, bool external) in relationships ?? [])
        {
            writer.AddRelationship("/word/document.xml", id, type, target, external);
        }

        foreach ((string name, string contentType, byte[] data) in binaryParts ?? [])
        {
            writer.AddPart("/" + name, contentType, data);
        }

        foreach ((string source, string id, string type, string target) in partRelationships ?? [])
        {
            writer.AddRelationship(source, id, type, target);
        }

        using MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        return DocxFormat.Load(stream);
    }
}
