using System.IO.Compression;
using System.Text;

using Egap.Documents.Odf;

namespace Egap.Documents.Tests;

/// <summary>The ODT reader: packages and detection, styles, lists, tables, frames, notes, fields, changes and page styles.</summary>
public sealed class OdtReaderTests
{
    private const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" "
        + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" "
        + "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" "
        + "xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
        + "xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" "
        + "xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\" "
        + "xmlns:loext=\"urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0\"";

    // A 1 x 1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private sealed record Parts(
        string Body,
        string Automatic = "",
        string Styles = "",
        string StylesAutomatic = "",
        string Masters = "",
        string Meta = "",
        string Settings = "",
        string MediaType = "application/vnd.oasis.opendocument.text",
        string ManifestExtra = "",
        Dictionary<string, byte[]>? Files = null);

    private static byte[] Package(Parts parts)
    {
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
            {
                using Stream stream = zip.CreateEntry(name, level).Open();
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
            }

            Add("mimetype", parts.MediaType, CompressionLevel.NoCompression);
            Add("META-INF/manifest.xml", "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">"
                + $"<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"{parts.MediaType}\"/>"
                + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" + parts.ManifestExtra + "</manifest:manifest>");
            Add("content.xml", $"<office:document-content {Namespaces} office:version=\"1.3\"><office:automatic-styles>{parts.Automatic}</office:automatic-styles>"
                + $"<office:body><office:text>{parts.Body}</office:text></office:body></office:document-content>");
            Add("styles.xml", $"<office:document-styles {Namespaces} office:version=\"1.3\"><office:styles>{parts.Styles}</office:styles>"
                + $"<office:automatic-styles>{parts.StylesAutomatic}</office:automatic-styles><office:master-styles>{parts.Masters}</office:master-styles></office:document-styles>");
            if (parts.Meta.Length > 0)
            {
                Add("meta.xml", $"<office:document-meta {Namespaces}><office:meta>{parts.Meta}</office:meta></office:document-meta>");
            }

            if (parts.Settings.Length > 0)
            {
                Add("settings.xml", $"<office:document-settings {Namespaces}><office:settings>{parts.Settings}</office:settings></office:document-settings>");
            }

            foreach ((string name, byte[] data) in parts.Files ?? [])
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return output.ToArray();
    }

    private static Document Read(Parts parts, out IReadOnlyList<ConversionWarning> warnings)
    {
        return OdtFormat.Load(new MemoryStream(Package(parts)), out warnings);
    }

    private static Document Read(Parts parts) => Read(parts, out _);

    private static Document Read(string body, string automatic = "", string styles = "") => Read(new Parts(body, automatic, styles));

    private static List<Paragraph> Paragraphs(Document document) => [.. document.Sections[0].Body.Blocks.OfType<Paragraph>()];

    private static List<Run> Runs(Document document) => [.. document.Descendants<Run>()];

    [Fact]
    public void IdentifyFindsOdtOttAndFlatFiles()
    {
        Assert.Equal(DocumentFormatKind.Odt, Document.Identify(new MemoryStream(Package(new Parts("<text:p/>")))));
        Assert.Equal(DocumentFormatKind.Odt, Document.Identify(new MemoryStream(Package(new Parts("<text:p/>", MediaType: "application/vnd.oasis.opendocument.text-template")))));
        string flat = $"<?xml version=\"1.0\"?>\n<office:document {Namespaces} office:mimetype=\"application/vnd.oasis.opendocument.text\"><office:body><office:text><text:p>Flat</text:p></office:text></office:body></office:document>";
        Assert.Equal(DocumentFormatKind.Odt, Document.Identify(new MemoryStream(Encoding.UTF8.GetBytes(flat))));
        Assert.Equal("Flat", OdtFormat.Load(new MemoryStream(Encoding.UTF8.GetBytes(flat))).GetText().Trim());

        // A spreadsheet is an OpenDocument package too, but not text: still a ZIP the DOCX reader refuses.
        Assert.Equal(DocumentFormatKind.Docx, Document.Identify(new MemoryStream(Package(new Parts("", MediaType: "application/vnd.oasis.opendocument.spreadsheet")))));
        Assert.Throws<DocumentException>(() => OdtFormat.Load(new MemoryStream(Package(new Parts("", MediaType: "application/vnd.oasis.opendocument.spreadsheet")))));
    }

    [Fact]
    public void DocumentLoadReadsAnOdtFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-odt-{Guid.NewGuid():N}.odt");
        File.WriteAllBytes(path, Package(new Parts("<text:p>Hello</text:p>")));
        try
        {
            Assert.Equal("Hello", Document.Load(path).GetText().Trim());
            Assert.Equal("Hello", Document.Load(path, new DocumentLoadOptions()).GetText().Trim());
            Assert.Equal(path, OdtFormat.Load(path).FileName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EncryptedAndBrokenFilesAreDocumentErrors()
    {
        string encrypted = "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"><manifest:encryption-data/></manifest:file-entry>";
        Assert.Throws<DocumentException>(() => Read(new Parts("<text:p/>", ManifestExtra: encrypted)));
        Assert.Throws<DocumentException>(() => OdtFormat.Load(new MemoryStream("<office:document/>"u8.ToArray())));
        Assert.Throws<DocumentException>(() => Read(new Parts("<text:p>unclosed")));
    }

    [Fact]
    public void DeepNestingIsStoppedByTheLimit()
    {
        string body = string.Concat(Enumerable.Repeat("<text:span>", 400)) + "x" + string.Concat(Enumerable.Repeat("</text:span>", 400));
        Assert.Throws<DocumentLimitException>(() => Read("<text:p>" + body + "</text:p>"));
    }

    [Fact]
    public void WhiteSpaceCollapsesAndSpacesTabsAndBreaksAreKept()
    {
        Document document = Read("<text:p>\n   Hello    <text:s text:c=\"2\"/>world<text:tab/>tab<text:line-break/>next </text:p>");
        Paragraph paragraph = Paragraphs(document)[0];
        Assert.Equal("Hello   world\ttab\vnext ", paragraph.GetText());
        Assert.Contains(paragraph.Inlines, i => i is Tab);
        Assert.Contains(paragraph.Inlines, i => i is Break { Kind: BreakKind.Line });
    }

    [Fact]
    public void CommonStylesBecomeStylesAndAutomaticStylesDirectFormatting()
    {
        string styles = """
            <style:default-style style:family="paragraph"><style:paragraph-properties style:tab-stop-distance="1.25cm"/><style:text-properties fo:font-size="11pt" fo:language="en" fo:country="GB"/></style:default-style>
            <style:style style:name="Standard" style:family="paragraph" style:class="text"/>
            <style:style style:name="Heading_20_1" style:display-name="Heading 1" style:family="paragraph" style:parent-style-name="Standard" style:next-style-name="Standard" style:default-outline-level="1">
              <style:paragraph-properties fo:margin-top="12pt" fo:margin-bottom="6pt" fo:keep-with-next="always"/>
              <style:text-properties fo:font-size="150%" fo:font-weight="bold"/>
            </style:style>
            <style:style style:name="Strong_20_Emphasis" style:display-name="Strong Emphasis" style:family="text"><style:text-properties fo:font-weight="bold"/></style:style>
            <style:style style:name="My_20_Note" style:display-name="My Note" style:family="paragraph" style:parent-style-name="Standard"><style:paragraph-properties fo:text-align="center"/></style:style>
            """;
        string automatic = """
            <style:style style:name="P1" style:family="paragraph" style:parent-style-name="My_20_Note"><style:paragraph-properties fo:margin-left="1in"/><style:text-properties fo:color="#C00000"/></style:style>
            <style:style style:name="T1" style:family="text"><style:text-properties fo:font-style="italic" style:text-underline-style="solid" style:text-underline-type="double" style:text-underline-width="auto" style:text-underline-color="font-color"/></style:style>
            """;
        Document document = Read("<text:h text:style-name=\"Heading_20_1\" text:outline-level=\"1\">Title</text:h><text:p text:style-name=\"P1\">Plain <text:span text:style-name=\"T1\">styled</text:span> <text:span text:style-name=\"Strong_20_Emphasis\">strong</text:span></text:p>", automatic, styles);

        Assert.Equal(11, document.Styles.DefaultRunFormat.Size);
        Assert.Equal("en-GB", document.Styles.DefaultRunFormat.Language);
        Assert.Equal(35.43, document.Settings.DefaultTabStop, 2);
        ParagraphStyle heading = Assert.IsType<ParagraphStyle>(document.Styles["Heading1"]);
        Assert.Equal("heading 1", heading.Name);
        Assert.Equal(16.5, heading.RunFormat.Size);
        Assert.True(heading.RunFormat.Bold);
        Assert.Equal(0, heading.ParagraphFormat.OutlineLevel);
        Assert.Equal(document.Styles.DefaultParagraphStyle, heading.BasedOn);
        Assert.Equal(document.Styles.DefaultParagraphStyle, heading.Next);
        Assert.Equal("Normal", document.Styles.DefaultParagraphStyle!.Id);

        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Same(heading, paragraphs[0].Style);
        Assert.Equal("My Note", paragraphs[1].Style!.Name);
        Assert.Equal(72, paragraphs[1].Format.LeftIndent);
        Assert.Null(paragraphs[1].Format.Alignment);
        Run plain = paragraphs[1].Runs.First();
        Assert.Equal(DocumentColor.FromHex("C00000"), plain.Format.Color);
        Run styled = paragraphs[1].Runs.Single(r => r.Text == "styled");
        Assert.True(styled.Format.Italic);
        Assert.Equal(UnderlineStyle.Double, styled.Format.Underline);
        Assert.Equal(DocumentColor.FromHex("C00000"), styled.Format.Color);
        Run strong = paragraphs[1].Runs.Single(r => r.Text == "strong");
        Assert.Equal("Strong", strong.Style!.Id);
    }

    [Fact]
    public void TextPropertiesMapOntoRunFormatting()
    {
        string automatic = """
            <style:style style:name="T1" style:family="text"><style:text-properties style:font-name="Body" fo:font-size="14pt" style:text-line-through-style="solid" fo:text-transform="uppercase" fo:font-variant="small-caps" style:text-position="super 58%" fo:background-color="#FFFF00" fo:letter-spacing="1pt" style:text-scale="80%" style:font-relief="embossed" style:text-outline="true" fo:text-shadow="1pt 1pt" text:display="none" style:font-weight-complex="bold" style:language-complex="ar" style:country-complex="EG"/></style:style>
            <style:style style:name="T2" style:family="text"><style:text-properties style:text-position="-20% 100%" style:text-line-through-style="solid" style:text-line-through-type="double" style:text-underline-style="wave" style:text-underline-type="single"/></style:style>
            """;
        Parts parts = new("<text:p><text:span text:style-name=\"T1\">a</text:span><text:span text:style-name=\"T2\">b</text:span></text:p>", automatic)
        {
            Styles = "<style:default-style style:family=\"paragraph\"><style:text-properties fo:font-size=\"10pt\"/></style:default-style>",
        };
        string decls = "<office:font-face-decls><style:font-face style:name=\"Body\" svg:font-family=\"'Liberation Serif'\"/></office:font-face-decls>";
        byte[] bytes = Package(parts);
        Document document = OdtFormat.Load(new MemoryStream(WithFontFaces(bytes, decls)));

        RunFormat a = Runs(document)[0].Format;
        Assert.Equal("Liberation Serif", a.FontAscii);
        Assert.Equal(14, a.Size);
        Assert.True(a.Strikethrough);
        Assert.True(a.AllCaps);
        Assert.True(a.SmallCaps);
        Assert.Equal(VerticalTextPosition.Superscript, a.VerticalPosition);
        Assert.Equal(DocumentColor.FromHex("FFFF00"), a.Shading!.Fill);
        Assert.Equal(1, a.Spacing);
        Assert.Equal(80, a.Scale);
        Assert.True(a.Emboss);
        Assert.True(a.Outline);
        Assert.True(a.Shadow);
        Assert.True(a.Hidden);
        Assert.True(a.BoldComplexScript);
        Assert.Equal("ar-EG", a.LanguageBidi);

        RunFormat b = Runs(document)[1].Format;
        Assert.Equal(-2, b.Position);
        Assert.True(b.DoubleStrikethrough);
        Assert.Equal(UnderlineStyle.Wave, b.Underline);
    }

    private static byte[] WithFontFaces(byte[] package, string declarations)
    {
        using MemoryStream copy = new();
        copy.Write(package);
        using (ZipArchive zip = new(copy, ZipArchiveMode.Update, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.GetEntry("content.xml")!;
            string content;
            using (StreamReader reader = new(entry.Open()))
            {
                content = reader.ReadToEnd();
            }

            entry.Delete();
            using StreamWriter writer = new(zip.CreateEntry("content.xml").Open());
            writer.Write(content.Replace("<office:automatic-styles>", declarations + "<office:automatic-styles>", StringComparison.Ordinal));
        }

        return copy.ToArray();
    }

    [Fact]
    public void ParagraphPropertiesMapOntoParagraphFormatting()
    {
        string automatic = """
            <style:style style:name="P1" style:family="paragraph"><style:paragraph-properties fo:text-align="justify" fo:margin-left="0.5in" fo:margin-right="1cm" fo:text-indent="-0.25in" fo:margin-top="0.1in" fo:margin-bottom="4pt" fo:line-height="150%" fo:keep-together="always" fo:break-before="page" fo:widows="0" fo:orphans="0" fo:background-color="#EEEEEE" fo:border="0.5pt solid #FF0000" fo:padding="2pt" style:writing-mode="rl-tb" style:contextual-spacing="true">
              <style:tab-stops><style:tab-stop style:position="1in" style:type="right" style:leader-style="dotted" style:leader-text="."/><style:tab-stop style:position="2in" style:type="char" style:char="."/></style:tab-stops>
            </style:paragraph-properties></style:style>
            <style:style style:name="P2" style:family="paragraph"><style:paragraph-properties fo:line-height="18pt"/></style:style>
            <style:style style:name="P3" style:family="paragraph"><style:paragraph-properties style:line-height-at-least="14pt" fo:break-after="page"/></style:style>
            """;
        Document document = Read("<text:p text:style-name=\"P1\">a</text:p><text:p text:style-name=\"P2\">b</text:p><text:p text:style-name=\"P3\">c</text:p><text:p>d</text:p>", automatic);
        List<Paragraph> paragraphs = Paragraphs(document);
        ParagraphFormat f = paragraphs[0].Format;
        Assert.Equal(ParagraphAlignment.Justify, f.Alignment);
        Assert.Equal(36, f.LeftIndent);
        Assert.Equal(28.35, f.RightIndent!.Value, 2);
        Assert.Equal(-18, f.FirstLineIndent);
        Assert.Equal(7.2, f.SpaceBefore!.Value, 6);
        Assert.Equal(4, f.SpaceAfter);
        Assert.Equal(1.5, f.LineSpacing);
        Assert.Equal(LineSpacingRule.Multiple, f.LineSpacingRule);
        Assert.True(f.KeepTogether);
        Assert.True(f.PageBreakBefore);
        Assert.False(f.WidowControl);
        Assert.Equal(DocumentColor.FromHex("EEEEEE"), f.Shading!.Fill);
        Assert.Equal(new Border(BorderStyle.Single, 0.5, DocumentColor.FromHex("FF0000"), 2), f.BorderTop);
        Assert.True(f.Bidi);
        Assert.True(f.ContextualSpacing);

        // Tab stops count from the paragraph's indent in ODF, from the margin in the model.
        Assert.Equal([new TabStop(108, TabAlignment.Right, TabLeader.Dot), new TabStop(180, TabAlignment.Decimal)], f.TabStops!);

        Assert.Equal(LineSpacingRule.Exactly, paragraphs[1].Format.LineSpacingRule);
        Assert.Equal(18, paragraphs[1].Format.LineSpacing);
        Assert.Equal(LineSpacingRule.AtLeast, paragraphs[2].Format.LineSpacingRule);
        Assert.True(paragraphs[3].Format.PageBreakBefore);
    }

    [Fact]
    public void ListsBecomeListsWithTheirLevelsAndRestarts()
    {
        string automatic = """
            <text:list-style style:name="L1">
              <text:list-level-style-number text:level="1" style:num-format="1" style:num-suffix="."><style:list-level-properties text:list-level-position-and-space-mode="label-alignment"><style:list-level-label-alignment text:label-followed-by="listtab" text:list-tab-stop-position="0.5in" fo:text-indent="-0.25in" fo:margin-left="0.5in"/></style:list-level-properties></text:list-level-style-number>
              <text:list-level-style-number text:level="2" style:num-format="a" style:num-prefix="(" style:num-suffix=")" text:display-levels="2" text:start-value="3"><style:list-level-properties text:space-before="0.5in" text:min-label-width="0.25in"/></text:list-level-style-number>
            </text:list-style>
            <text:list-style style:name="L2"><text:list-level-style-bullet text:level="1" text:bullet-char="•"><style:text-properties style:font-name="Symbol"/></text:list-level-style-bullet></text:list-style>
            """;
        string body = """
            <text:list text:style-name="L1" xml:id="list1">
              <text:list-item><text:p>one</text:p><text:p>more of one</text:p>
                <text:list><text:list-item><text:p>nested</text:p></text:list-item></text:list>
              </text:list-item>
              <text:list-item><text:p>two</text:p></text:list-item>
            </text:list>
            <text:p>between</text:p>
            <text:list text:style-name="L1" text:continue-list="list1"><text:list-item><text:p>three</text:p></text:list-item></text:list>
            <text:list text:style-name="L1"><text:list-item><text:p>restart</text:p></text:list-item></text:list>
            <text:list text:style-name="L2"><text:list-header><text:p>header</text:p></text:list-header><text:list-item><text:p>bullet</text:p></text:list-item></text:list>
            <text:list text:style-name="L1"><text:list-item text:start-value="7"><text:p>seven</text:p></text:list-item></text:list>
            """;
        Document document = Read(body, automatic);
        Dictionary<string, Paragraph> p = Paragraphs(document).ToDictionary(x => x.GetText().Trim());

        List first = p["one"].Format.List!;
        ListLevel level0 = first.Definition.Levels[0];
        Assert.Equal(NumberStyle.Decimal, level0.NumberStyle);
        Assert.Equal("%1.", level0.LevelText);
        Assert.Equal(36, level0.ParagraphFormat.LeftIndent);
        Assert.Equal(-18, level0.ParagraphFormat.FirstLineIndent);
        ListLevel level1 = first.Definition.Levels[1];
        Assert.Equal("(%1.%2)", level1.LevelText);
        Assert.Equal(NumberStyle.LowerLetter, level1.NumberStyle);
        Assert.Equal(3, level1.StartAt);
        Assert.Equal(54, level1.ParagraphFormat.LeftIndent);

        Assert.Equal(List.None, p["more of one"].Format.List);
        Assert.Equal(36, p["more of one"].Format.LeftIndent);
        Assert.Same(first, p["nested"].Format.List);
        Assert.Equal(1, p["nested"].Format.ListLevel);
        Assert.Same(first, p["two"].Format.List);
        Assert.Null(p["between"].Format.List);
        Assert.Same(first, p["three"].Format.List);
        Assert.NotSame(first, p["restart"].Format.List);
        Assert.Same(first.Definition, p["restart"].Format.List!.Definition);
        Assert.Equal(7, p["seven"].Format.List!.Overrides[0].StartAt);

        Assert.Equal("1.", p["one"].ListLabel!.Text);
        Assert.Equal("2.", p["two"].ListLabel!.Text);
        Assert.Equal("3.", p["three"].ListLabel!.Text);
        Assert.Equal("1.", p["restart"].ListLabel!.Text);
        Assert.Equal("7.", p["seven"].ListLabel!.Text);

        ListLevel bullet = p["bullet"].Format.List!.Definition.Levels[0];
        Assert.Equal(NumberStyle.Bullet, bullet.NumberStyle);
        Assert.Equal("•", bullet.LevelText);
        Assert.Equal("Symbol", bullet.RunFormat.FontAscii);
        Assert.Equal(List.None, p["header"].Format.List);
    }

    [Fact]
    public void HeadingsAreNumberedByTheOutlineStyle()
    {
        string styles = """
            <style:style style:name="Heading_20_2" style:display-name="Heading 2" style:family="paragraph" style:default-outline-level="2"/>
            <text:outline-style style:name="Outline">
              <text:outline-level-style text:level="1" style:num-format="1" style:num-suffix="."/>
              <text:outline-level-style text:level="2" style:num-format="1" text:display-levels="2"/>
            </text:outline-style>
            """;
        Document document = Read("<text:h text:outline-level=\"1\">First</text:h><text:h text:style-name=\"Heading_20_2\" text:outline-level=\"2\">Sub</text:h><text:h text:outline-level=\"1\" text:is-list-header=\"true\">Plain</text:h>", "", styles);
        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Equal("1.", paragraphs[0].ListLabel!.Text);
        Assert.Equal(0, paragraphs[0].Format.OutlineLevel);
        Assert.Equal("1.1", paragraphs[1].ListLabel!.Text);
        Assert.Null(paragraphs[2].ListLabel);
    }

    [Fact]
    public void TablesKeepColumnsHeaderRowsSpansAndCellStyles()
    {
        string automatic = """
            <style:style style:name="Tab1" style:family="table"><style:table-properties style:width="6in" table:align="center"/></style:style>
            <style:style style:name="Tab1.A" style:family="table-column"><style:table-column-properties style:column-width="2in"/></style:style>
            <style:style style:name="Tab1.R" style:family="table-row"><style:table-row-properties style:min-row-height="0.5in" fo:keep-together="always"/></style:style>
            <style:style style:name="Tab1.C" style:family="table-cell"><style:table-cell-properties fo:background-color="#DDDDDD" fo:border="0.5pt solid #000000" fo:padding="0.1in" style:vertical-align="middle"/></style:style>
            """;
        string body = """
            <table:table table:name="T" table:style-name="Tab1">
              <table:table-column table:style-name="Tab1.A" table:number-columns-repeated="3"/>
              <table:table-header-rows><table:table-row><table:table-cell table:number-columns-spanned="2"><text:p>wide</text:p></table:table-cell><table:covered-table-cell/><table:table-cell><text:p>c</text:p></table:table-cell></table:table-row></table:table-header-rows>
              <table:table-row table:style-name="Tab1.R"><table:table-cell table:style-name="Tab1.C" table:number-rows-spanned="2"><text:p>tall</text:p></table:table-cell><table:table-cell><text:p>b1</text:p></table:table-cell><table:table-cell/></table:table-row>
              <table:table-row><table:covered-table-cell/><table:table-cell><text:p>b2</text:p></table:table-cell><table:table-cell><table:table><table:table-column/><table:table-row><table:table-cell><text:p>inner</text:p></table:table-cell></table:table-row></table:table></table:table-cell></table:table-row>
            </table:table>
            """;
        Document document = Read(body, automatic);
        Table table = Assert.IsType<Table>(document.Sections[0].Body.Blocks[0]);
        Assert.Equal([144, 144, 144], table.Grid!);
        Assert.Equal(TableWidth.Points(432), table.Format.Width);
        Assert.Equal(TableAlignment.Center, table.Format.Alignment);
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].Format.IsHeader);
        Assert.Equal(2, table.Rows[0].Cells.Count);
        Assert.Equal(2, table.Rows[0].Cells[0].Format.GridSpan);
        Assert.Equal(TableWidth.Points(288), table.Rows[0].Cells[0].Format.Width);

        Row row = table.Rows[1];
        Assert.Equal(36, row.Format.Height);
        Assert.Equal(RowHeightRule.AtLeast, row.Format.HeightRule);
        Assert.True(row.Format.CantSplit);
        Cell tall = row.Cells[0];
        Assert.Equal(VerticalMerge.Restart, tall.Format.VerticalMerge);
        Assert.Equal(DocumentColor.FromHex("DDDDDD"), tall.Format.Shading!.Fill);
        Assert.Equal(7.2, tall.Format.MarginLeft!.Value, 6);
        Assert.Equal(VerticalAlignment.Center, tall.Format.VerticalAlignment);
        Assert.Equal(BorderStyle.Single, tall.Format.BorderTop!.Style);
        Assert.Single(row.Cells[2].Blocks);

        Assert.Equal(VerticalMerge.Continue, table.Rows[2].Cells[0].Format.VerticalMerge);
        Assert.Equal("b2", table.Rows[2].Cells[1].GetText().Trim());
        Assert.IsType<Table>(table.Rows[2].Cells[2].Blocks[0]);
        Assert.IsType<Paragraph>(table.Rows[2].Cells[2].Blocks[^1]);
    }

    [Fact]
    public void AFrameHoldingOnlyATableIsAFloatingTable()
    {
        string body = "<text:p>Anchor<draw:frame text:anchor-type=\"paragraph\" svg:x=\"1in\" svg:y=\"0.5in\" svg:width=\"3in\"><draw:text-box>"
            + "<table:table><table:table-column/><table:table-row><table:table-cell><text:p>Cell</text:p></table:table-cell></table:table-row></table:table><text:p/></draw:text-box></draw:frame></text:p>";
        List<Block> blocks = [.. Read(body).Sections[0].Body.Blocks];
        Table table = Assert.IsType<Table>(blocks[0]);
        Assert.Equal(72, table.Format.Position!.HorizontalOffset);
        Assert.Equal(36, table.Format.Position.VerticalOffset);
        Assert.Equal(VerticalAnchor.Paragraph, table.Format.Position.VerticalAnchor);
        Assert.Equal("Anchor", Assert.IsType<Paragraph>(blocks[1]).GetText().Trim());
    }

    [Fact]
    public void APageBreakBeforeATableEndsTheParagraphBefore()
    {
        string automatic = "<style:style style:name=\"Tab1\" style:family=\"table\"><style:table-properties fo:break-before=\"page\"/></style:style>";
        Document document = Read("<text:p>First</text:p><table:table table:style-name=\"Tab1\"><table:table-column/><table:table-row><table:table-cell><text:p>A1</text:p></table:table-cell></table:table-row></table:table>", automatic);
        Paragraph first = Assert.IsType<Paragraph>(document.Sections[0].Body.Blocks[0]);
        Assert.IsType<Break>(first.Inlines[^1]);
        Assert.Equal(BreakKind.Page, ((Break)first.Inlines[^1]).Kind);
        Assert.Equal(2, document.Layout().Pages.Count);
    }

    [Fact]
    public void NotesCommentsBookmarksAndLinksAreRead()
    {
        string body = """
            <text:p>See<text:note text:id="ftn1" text:note-class="footnote"><text:note-citation>1</text:note-citation><text:note-body><text:p>The note.</text:p></text:note-body></text:note>
            and<text:note text:id="edn1" text:note-class="endnote"><text:note-citation text:label="*">*</text:note-citation><text:note-body><text:p>End.</text:p></text:note-body></text:note>
            <office:annotation office:name="c1" loext:resolved="true"><dc:creator>Ann</dc:creator><dc:date>2026-01-02T03:04:05</dc:date><meta:creator-initials>A</meta:creator-initials><text:p>Why?</text:p></office:annotation>commented<office:annotation-end office:name="c1"/>
            <office:annotation><dc:creator>Bo</dc:creator><text:p>Point</text:p></office:annotation>
            <text:bookmark-start text:name="mark"/>marked<text:bookmark-end text:name="mark"/><text:bookmark text:name="here"/>
            <text:a xlink:type="simple" xlink:href="https://example.com/" office:target-frame-name="_blank" office:title="Tip">site</text:a> <text:a xlink:href="#mark">back</text:a>
            ref <text:note-ref text:note-class="footnote" text:reference-format="text" text:ref-name="ftn1">1</text:note-ref></text:p>
            """;
        Document document = Read(body);
        Paragraph paragraph = Paragraphs(document)[0];

        Footnote[] notes = [.. paragraph.Inlines.OfType<Footnote>()];
        Assert.Equal(NoteKind.Footnote, notes[0].Kind);
        Assert.Equal("The note.", notes[0].Story.GetText().Trim());
        Assert.Equal(NoteKind.Endnote, notes[1].Kind);
        Assert.Equal("*", notes[1].CustomMark);

        Comment[] comments = [.. paragraph.Inlines.OfType<Comment>()];
        Assert.Equal("Ann", comments[0].Author);
        Assert.Equal("A", comments[0].Initials);
        Assert.True(comments[0].Done);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), comments[0].Date);
        Assert.Equal("Why?", comments[0].Story.GetText().Trim());
        Assert.Contains(paragraph.Inlines, i => i is CommentRangeStart);
        Assert.Contains(paragraph.Inlines, i => i is CommentRangeEnd);
        Assert.Equal("Bo", comments[1].Author);

        string[] bookmarks = [.. paragraph.Inlines.OfType<BookmarkStart>().Select(b => b.Name)];
        Assert.Equal(["ftn1", "mark", "here"], bookmarks);
        Hyperlink[] links = [.. paragraph.Inlines.OfType<Hyperlink>()];
        Assert.Equal("https://example.com/", links[0].Address);
        Assert.Equal("_blank", links[0].Target);
        Assert.Equal("Tip", links[0].Tooltip);
        Assert.Equal("site", links[0].GetText());
        Assert.Equal("mark", links[1].Anchor);

        // Links do not nest; the inner one's text joins the outer link.
        Hyperlink outer = Assert.Single(Paragraphs(Read("<text:p><text:a xlink:href=\"https://a/\">a <text:a xlink:href=\"https://b/\">b</text:a></text:a></text:p>"))[0].Inlines.OfType<Hyperlink>());
        Assert.Equal("a b", outer.GetText());
        Assert.Contains(document.GetFields(), f => f.Code.Trim() == "NOTEREF ftn1 \\h");
    }

    [Fact]
    public void FieldsBecomeWordFields()
    {
        string body = """
            <text:p>Page <text:page-number text:select-page="current" style:num-format="i">iv</text:page-number> of <text:page-count>9</text:page-count>,
            <text:date text:fixed="true" text:date-value="2026-11-04">4 Nov</text:date> <text:title>T</text:title> <text:file-name text:display="full">f.odt</text:file-name>
            <text:sequence text:ref-name="refFigure0" text:name="Figure" text:formula="ooow:Figure+1" style:num-format="1">1</text:sequence>
            <text:bookmark-ref text:reference-format="page" text:ref-name="mark">3</text:bookmark-ref> <text:chapter text:display="number" text:outline-level="2">2.1</text:chapter>
            <text:variable-get text:name="v">value</text:variable-get> <text:user-defined text:name="Client">ACME</text:user-defined></text:p>
            """;
        Document document = Read(body);
        string[] codes = [.. document.GetFields().Select(f => f.Code.Trim())];
        Assert.Equal(
            ["PAGE \\* roman", "NUMPAGES", "DATE", "TITLE", "FILENAME \\p", "SEQ Figure \\* ARABIC", "PAGEREF mark \\h", "STYLEREF 2 \\n", "DOCPROPERTY Client"],
            codes);
        Assert.Equal("iv", document.GetFields().First().Result);
        Assert.True(document.GetFields().Single(f => f.Code.Trim() == "DATE").Start.IsLocked);
        Assert.Contains("value", Paragraphs(document)[0].GetText(), StringComparison.Ordinal);
        Assert.Contains(Paragraphs(document)[0].Inlines, i => i is BookmarkStart { Name: "refFigure0" });
    }

    [Fact]
    public void TrackedChangesBecomeRevisions()
    {
        string body = """
            <text:tracked-changes text:track-changes="true">
              <text:changed-region text:id="ct1"><text:insertion><office:change-info><dc:creator>Ann</dc:creator><dc:date>2026-03-04T05:06:07</dc:date></office:change-info></text:insertion></text:changed-region>
              <text:changed-region text:id="ct2"><text:deletion><office:change-info><dc:creator>Bo</dc:creator></office:change-info><text:p>gone</text:p><text:p>also</text:p></text:deletion></text:changed-region>
              <text:changed-region text:id="ct3"><text:format-change><office:change-info><dc:creator>Cy</dc:creator></office:change-info></text:format-change></text:changed-region>
            </text:tracked-changes>
            <text:p>Keep <text:change-start text:change-id="ct1"/>new<text:change-end text:change-id="ct1"/> and <text:change text:change-id="ct2"/>rest<text:change-start text:change-id="ct3"/>x<text:change-end text:change-id="ct3"/></text:p>
            """;
        Document document = Read(new Parts(body), out IReadOnlyList<ConversionWarning> warnings);
        Assert.True(document.Settings.TrackRevisions);
        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Equal(2, paragraphs.Count);
        Run inserted = paragraphs[0].Runs.Single(r => r.Text == "new");
        Assert.Equal(new Revision(RevisionKind.Insertion, "Ann", new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero)), inserted.Revision);
        Run gone = paragraphs[0].Runs.Single(r => r.Text == "gone");
        Assert.Equal(RevisionKind.Deletion, gone.Revision!.Kind);
        Assert.Equal("Bo", gone.Revision.Author);
        Assert.Equal(RevisionKind.Deletion, paragraphs[0].MarkRevision!.Kind);
        Assert.Equal("also", paragraphs[1].Runs.First().Text);
        Assert.Equal(RevisionKind.Deletion, paragraphs[1].Runs.First().Revision!.Kind);
        Assert.Null(paragraphs[1].Runs.Single(r => r.Text == "restx").Revision);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.ChangesAccepted);
    }

    [Fact]
    public void MasterPagesBecomeSectionsWithHeadersAndFooters()
    {
        string stylesAutomatic = """
            <style:page-layout style:name="pm1"><style:page-layout-properties fo:page-width="21cm" fo:page-height="29.7cm" style:print-orientation="portrait" fo:margin-top="2cm" fo:margin-bottom="2cm" fo:margin-left="2.5cm" fo:margin-right="2.5cm" style:num-format="1"/>
              <style:header-style><style:header-footer-properties fo:min-height="1cm" fo:margin-bottom="0.5cm"/></style:header-style><style:footer-style/></style:page-layout>
            <style:page-layout style:name="pm2"><style:page-layout-properties fo:page-width="11in" fo:page-height="8.5in" style:print-orientation="landscape" fo:margin="1in"><style:columns fo:column-count="2" fo:column-gap="0.5in"/></style:page-layout-properties></style:page-layout>
            <style:style style:name="MP1" style:family="paragraph"><style:paragraph-properties fo:text-align="center"/></style:style>
            """;
        string masters = """
            <style:master-page style:name="First_20_Page" style:display-name="First Page" style:page-layout-name="pm1" style:next-style-name="Standard"><style:header><text:p text:style-name="MP1">First header</text:p></style:header></style:master-page>
            <style:master-page style:name="Standard" style:page-layout-name="pm1"><style:header><text:p text:style-name="MP1">Header <text:page-number>1</text:page-number></text:p></style:header><style:header-left><text:p>Left</text:p></style:header-left><style:footer><text:p>Footer</text:p></style:footer></style:master-page>
            <style:master-page style:name="Landscape" style:page-layout-name="pm2"/>
            """;
        string automatic = """
            <style:style style:name="P1" style:family="paragraph" style:master-page-name="First_20_Page"/>
            <style:style style:name="P2" style:family="paragraph" style:master-page-name="Landscape"><style:paragraph-properties style:page-number="5"/></style:style>
            <style:style style:name="Sect1" style:family="section"><style:section-properties><style:columns fo:column-count="3" fo:column-gap="12pt"/></style:section-properties></style:style>
            """;
        string body = "<text:p text:style-name=\"P1\">Title page</text:p><text:p>Body</text:p><text:p text:style-name=\"P2\">Wide</text:p>"
            + "<text:section text:name=\"S\" text:style-name=\"Sect1\"><text:p>In columns</text:p></text:section><text:p>After</text:p>";
        Document document = Read(new Parts(body, automatic, StylesAutomatic: stylesAutomatic, Masters: masters));

        Assert.Equal(4, document.Sections.Count);
        Section first = document.Sections[0];
        Assert.Equal(595.28, first.PageSetup.PageWidth!.Value, 2);
        Assert.Equal(70.87, first.PageSetup.MarginLeft!.Value, 2);
        Assert.Equal(56.69, first.PageSetup.HeaderDistance!.Value, 2);
        Assert.Equal(56.69 + 28.35, first.PageSetup.MarginTop!.Value, 1);
        Assert.True(first.PageSetup.DifferentFirstPage);
        Assert.Equal("First header", first.HeadersFooters[HeaderFooterKind.HeaderFirst]!.GetText().Trim());
        Assert.StartsWith("Header", first.HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText().Trim(), StringComparison.Ordinal);
        Assert.Equal(ParagraphAlignment.Center, first.HeadersFooters[HeaderFooterKind.HeaderPrimary]!.Paragraphs.First().Format.Alignment);
        Assert.Equal("Left", first.HeadersFooters[HeaderFooterKind.HeaderEven]!.GetText().Trim());
        Assert.Equal("Footer", first.HeadersFooters[HeaderFooterKind.FooterPrimary]!.GetText().Trim());
        Assert.True(document.Settings.EvenAndOddHeaders);
        Assert.Equal(["Title page", "Body"], first.Body.Paragraphs.Select(p => p.GetText().Trim()));

        Section wide = document.Sections[1];
        Assert.Equal(PageOrientation.Landscape, wide.PageSetup.Orientation);
        Assert.Equal(792, wide.PageSetup.PageWidth);
        Assert.Equal(2, wide.PageSetup.ColumnCount);
        Assert.Equal(5, wide.PageSetup.PageNumberStart);
        Assert.Equal(SectionStart.NewPage, wide.PageSetup.SectionStart);
        Assert.Equal("", wide.HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText().Trim());

        Section columns = document.Sections[2];
        Assert.Equal(SectionStart.Continuous, columns.PageSetup.SectionStart);
        Assert.Equal(3, columns.PageSetup.ColumnCount);
        Assert.Equal("In columns", columns.Body.GetText().Trim());
        Section after = document.Sections[3];
        Assert.Equal(2, after.PageSetup.ColumnCount);
        Assert.Equal("After", after.Body.GetText().Trim());
    }

    [Fact]
    public void FramesBecomePicturesTextBoxesShapesAndIslands()
    {
        string automatic = """
            <style:style style:name="fr1" style:family="graphic"><style:graphic-properties style:wrap="parallel" style:horizontal-pos="center" style:horizontal-rel="paragraph" style:vertical-pos="from-top" style:vertical-rel="paragraph" fo:margin-left="0.1in" fo:margin-right="0.1in"/></style:style>
            <style:style style:name="gr1" style:family="graphic"><style:graphic-properties draw:fill="solid" draw:fill-color="#FF0000" svg:stroke-color="#00FF00" svg:stroke-width="2pt" style:wrap="run-through" style:run-through="background"/></style:style>
            """;
        string body = """
            <text:p>Inline <draw:frame draw:name="Image1" text:anchor-type="as-char" svg:width="1in" svg:height="0.5in"><draw:image xlink:href="Pictures/a.png"/><svg:title>Alt</svg:title><svg:desc>Described</svg:desc></draw:frame>
            float <draw:frame draw:style-name="fr1" text:anchor-type="paragraph" svg:y="0.25in" svg:width="2in" svg:height="1in" draw:z-index="3"><draw:image xlink:href="Pictures/a.png"/></draw:frame>
            box <draw:frame text:anchor-type="as-char" svg:width="2in" fo:min-height="1in"><draw:text-box><text:p>In the box</text:p></draw:text-box></draw:frame>
            shape <draw:custom-shape draw:style-name="gr1" text:anchor-type="paragraph" svg:width="1in" svg:height="1in" svg:x="1in" svg:y="0in"><text:p>Shape text</text:p><draw:enhanced-geometry draw:type="ellipse"/></draw:custom-shape>
            object <draw:frame text:anchor-type="as-char" svg:width="1in" svg:height="1in"><draw:object xlink:href="./Object 1"/><draw:image xlink:href="./ObjectReplacements/Object 1"/></draw:frame>
            group <draw:g text:anchor-type="as-char"><draw:rect svg:width="1in" svg:height="1in"/></draw:g>
            missing <draw:frame text:anchor-type="as-char" svg:width="1in" svg:height="1in"><draw:image xlink:href="Pictures/none.png"/></draw:frame></text:p>
            <draw:frame text:anchor-type="page" text:anchor-page-number="1" svg:x="1in" svg:y="1in" svg:width="1in" svg:height="1in"><draw:image xlink:href="Pictures/a.png"/></draw:frame>
            <text:p>Next</text:p>
            """;
        Dictionary<string, byte[]> files = new() { ["Pictures/a.png"] = Png, ["ObjectReplacements/Object 1"] = Png, ["Object 1/content.xml"] = "<math/>"u8.ToArray() };
        string manifest = "<manifest:file-entry manifest:full-path=\"Object 1/\" manifest:media-type=\"application/vnd.oasis.opendocument.formula\"/>";
        Document document = Read(new Parts(body, automatic, ManifestExtra: manifest, Files: files), out IReadOnlyList<ConversionWarning> warnings);
        List<Paragraph> paragraphs = Paragraphs(document);
        Paragraph paragraph = paragraphs[0];

        Picture[] pictures = [.. paragraph.Inlines.OfType<Picture>()];
        Assert.Equal(72, pictures[0].Width);
        Assert.Equal(36, pictures[0].Height);
        Assert.Null(pictures[0].Floating);
        Assert.Equal("Alt", pictures[0].Title);
        Assert.Equal("Described", pictures[0].Description);
        Assert.Equal("image/png", pictures[0].Image.ContentType);
        FloatingPlacement floating = pictures[1].Floating!;
        Assert.Equal(FloatingAlignment.Center, floating.HorizontalAlignment);
        Assert.Equal(HorizontalAnchor.Column, floating.HorizontalAnchor);
        Assert.Equal(VerticalAnchor.Paragraph, floating.VerticalAnchor);
        Assert.Equal(18, floating.VerticalOffset);
        Assert.Equal(WrapMode.Square, floating.Wrap);
        Assert.Equal(7.2, floating.DistanceLeft, 6);
        Assert.Equal(3, floating.ZOrder);

        Shape[] shapes = [.. paragraph.Inlines.OfType<Shape>()];
        Assert.Equal("In the box", shapes[0].TextBox!.GetText().Trim());
        Assert.True(shapes[0].FitToText);
        Assert.Equal(ShapeKind.Ellipse, shapes[1].Kind);
        Assert.Equal(DocumentColor.FromHex("FF0000"), shapes[1].Fill);
        Assert.Equal(2, shapes[1].Outline!.Width);
        Assert.Equal(WrapMode.BehindText, shapes[1].Floating!.Wrap);
        Assert.Equal(72, shapes[1].Floating!.HorizontalOffset);
        Assert.Equal("Shape text", shapes[1].TextBox!.GetText().Trim());

        Island[] islands = [.. paragraph.Inlines.OfType<Island>()];
        Assert.Equal(IslandKind.Math, islands[0].Kind);
        Assert.Equal("odt", islands[0].Format);
        Assert.NotNull(islands[0].Fallback);
        Assert.Equal(IslandKind.ShapeGroup, islands[1].Kind);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.PictureDropped);

        // A frame anchored to the page goes into the next paragraph.
        Picture page = Assert.IsType<Picture>(paragraphs[1].Inlines[0]);
        Assert.Equal(HorizontalAnchor.Page, page.Floating!.HorizontalAnchor);
        Assert.Equal(72, page.Floating.HorizontalOffset);
    }

    [Fact]
    public void FlatFilesHoldPicturesAsBase64()
    {
        string flat = $"<office:document {Namespaces} office:version=\"1.3\" office:mimetype=\"application/vnd.oasis.opendocument.text\">"
            + "<office:styles><style:style style:name=\"Standard\" style:family=\"paragraph\"/></office:styles>"
            + "<office:automatic-styles><style:style style:name=\"P1\" style:family=\"paragraph\"><style:text-properties fo:font-weight=\"bold\"/></style:style></office:automatic-styles>"
            + "<office:body><office:text><text:p text:style-name=\"P1\">Bold<draw:frame text:anchor-type=\"as-char\" svg:width=\"1in\" svg:height=\"1in\"><draw:image><office:binary-data>"
            + Convert.ToBase64String(Png) + "</office:binary-data></draw:image></draw:frame></text:p></office:text></office:body></office:document>";
        Document document = OdtFormat.Load(new MemoryStream(Encoding.UTF8.GetBytes(flat)));
        Paragraph paragraph = Paragraphs(document)[0];
        Assert.True(paragraph.Runs.First().Format.Bold);
        Assert.Equal(Png, paragraph.Inlines.OfType<Picture>().Single().Image.Bytes.ToArray());
    }

    [Fact]
    public void MetadataIsRead()
    {
        string meta = """
            <dc:title>The title</dc:title><dc:subject>Subject</dc:subject><dc:description>About</dc:description><meta:initial-creator>Ann</meta:initial-creator><dc:creator>Bo</dc:creator>
            <meta:creation-date>2025-05-06T07:08:09</meta:creation-date><dc:date>2026-01-01T00:00:00Z</dc:date><meta:keyword>one</meta:keyword><meta:keyword>two</meta:keyword>
            <meta:user-defined meta:name="Client" meta:value-type="string">ACME</meta:user-defined>
            """;
        Document document = Read(new Parts("<text:p/>", Meta: meta));
        DocumentProperties properties = document.Properties;
        Assert.Equal("The title", properties.Title);
        Assert.Equal("Subject", properties.Subject);
        Assert.Equal("About", properties.Comments);
        Assert.Equal("Ann", properties.Author);
        Assert.Equal("Bo", properties.LastSavedBy);
        Assert.Equal(new DateTimeOffset(2025, 5, 6, 7, 8, 9, TimeSpan.Zero), properties.Created);
        Assert.Equal("one, two", properties.Keywords);
        Assert.Equal("ACME", properties.GetCustom("Client"));
    }

    [Fact]
    public void TabStopsCountFromTheMarginWhenTheSettingSaysSo()
    {
        string settings = """<config:config-item-set config:name="ooo:configuration-settings"><config:config-item config:name="TabsRelativeToIndent" config:type="boolean">false</config:config-item></config:config-item-set>""";
        string automatic = """<style:style style:name="P1" style:family="paragraph"><style:paragraph-properties fo:margin-left="1in"><style:tab-stops><style:tab-stop style:position="2in"/></style:tab-stops></style:paragraph-properties></style:style>""";
        Document document = Read(new Parts("<text:p text:style-name=\"P1\">x</text:p>", automatic, Settings: settings));
        Assert.Equal(144, Paragraphs(document)[0].Format.TabStops![0].Position);
    }

    [Fact]
    public void IndexesAreTocFieldsAndUnknownMarkupIsKept()
    {
        string body = """
            <text:table-of-content text:name="Contents"><text:table-of-content-source text:outline-level="3"/><text:index-body>
              <text:index-title text:name="Title"><text:p>Contents</text:p></text:index-title>
              <text:p><text:a xlink:href="#a">Intro<text:tab/>1</text:a></text:p><text:p>Two<text:tab/>2</text:p>
            </text:index-body></text:table-of-content>
            <text:p>Text<text:toc-mark text:string-value="entry"/>more<text:unknown-thing/></text:p>
            <text:sequence-decls><text:sequence-decl text:display-outline-level="0" text:name="Figure"/></text:sequence-decls>
            """;
        Document document = Read(body);
        Field toc = Assert.Single(document.GetFields());
        Assert.Equal("TOC \\o \"1-3\" \\h \\z \\u", toc.Code.Trim());
        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Equal("Contents", paragraphs[0].GetText().Trim());
        Assert.IsType<FieldStart>(paragraphs[1].Inlines[0]);
        Assert.IsType<FieldEnd>(paragraphs[2].Inlines[^1]);

        OdtExtension kept = paragraphs[3].Extensions.Get<OdtExtension>(OdtExtension.Format)!;
        Assert.Equal(["toc-mark", "unknown-thing"], kept.Inline!.Select(i => i.Element.Name.LocalName));
        Assert.Equal(1, kept.Inline![0].Index);
        OdtExtension document1 = document.Extensions.Get<OdtExtension>(OdtExtension.Format)!;
        Assert.Equal("sequence-decls", Assert.Single(document1.Declarations!).Name.LocalName);
        Assert.NotNull(document1.Package);
    }
}
