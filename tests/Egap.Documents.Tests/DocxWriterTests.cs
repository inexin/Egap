using System.IO.Compression;
using System.Xml.Linq;

using Egap.Documents.Docx;

namespace Egap.Documents.Tests;

/// <summary>The DOCX writer and round trips (Phase 6 Build Plan task 12).</summary>
public class DocxWriterTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Fact]
    public void NewDocumentReadsBack()
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        builder.ParagraphStyle = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Heading1);
        builder.Writeln("Title");
        builder.ParagraphStyle = document.Styles.DefaultParagraphStyle;
        builder.Font.Bold = true;
        builder.Write("Bold");
        builder.Font.Bold = null;
        builder.Write(" then plain ");
        builder.InsertHyperlink("link", "https://example.com/");
        builder.InsertFootnote("A note.");
        builder.InsertTab();
        builder.InsertField("PAGE", "1");
        builder.Writeln();
        builder.StartTable();
        builder.InsertCell();
        builder.Write("A1");
        builder.InsertCell();
        builder.Write("B1");
        builder.EndRow();
        builder.EndTable();
        Paragraph listed = document.Sections[0].Body.AddParagraph("first item");
        listed.SetList(document.Lists.AddNumbered());
        builder.MoveToHeaderFooter(HeaderFooterKind.HeaderPrimary);
        builder.Write("Header text");
        document.Properties.Title = "Written";
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 1, 2, 3];
        document.Sections[0].Body.AddParagraph().AddPicture(new ImageData(png, "image/png"), 100, 50);

        Document read = RoundTrip(document);

        Body body = read.Sections[0].Body;
        Paragraph title = body.Paragraphs.First();
        Assert.Equal("Title", title.Runs.Single().Text);
        Assert.Equal(document.Styles["Heading1"]!.Name, title.Style!.Name);
        Paragraph second = body.Paragraphs.ElementAt(1);
        Assert.True(second.Runs.First().Format.Bold);
        Assert.Equal("https://example.com/", second.Inlines.OfType<Hyperlink>().Single().Address);
        Assert.Equal("A note.", second.Inlines.OfType<Footnote>().Single().Story.GetText().Trim('\r'));
        Assert.Equal(FieldKind.Page, read.GetFields().Single().Kind);
        Assert.Equal("B1", body.Tables.Single().Rows[0].Cells[1].Paragraphs.Single().Runs.Single().Text);
        Paragraph item = body.Paragraphs.Single(p => p.GetText().StartsWith("first item", StringComparison.Ordinal));
        Assert.Equal("1.", item.ListLabel!.Text);
        Assert.Equal("Header text", read.Sections[0].HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText().Trim('\r'));
        Assert.Equal("Written", read.Properties.Title);
        Picture picture = body.Descendants<Picture>().Single();
        Assert.Equal(png, picture.Image.Bytes.ToArray());
        Assert.Equal(100, picture.Width, 3);
    }

    [Fact]
    public void UnchangedFileIsWrittenAsRead()
    {
        byte[] original = Sample();
        Document document = DocxFormat.Load(new MemoryStream(original));

        byte[] saved = Save(document);

        Assert.Equal(Part(original, "word/document.xml").ToString(SaveOptions.DisableFormatting), Part(saved, "word/document.xml").ToString(SaveOptions.DisableFormatting));
        Assert.Equal(Part(original, "word/styles.xml").ToString(), Part(saved, "word/styles.xml").ToString());
    }

    [Fact]
    public void EditedParagraphKeepsWhatTheModelDoesNotName()
    {
        Document document = DocxFormat.Load(new MemoryStream(Sample()));
        Paragraph[] paragraphs = [.. document.Sections[0].Body.Paragraphs];
        paragraphs[0].Runs.First().Text = "Changed";

        XElement body = Part(Save(document), "word/document.xml").Element(W + "body")!;

        XElement edited = body.Elements(W + "p").First();
        Assert.Equal("Changed", edited.Descendants(W + "t").First().Value);
        Assert.NotNull(edited.Element(W + "pPr")!.Element(W + "kinsoku"));
        Assert.Equal("00AB", (string?)edited.Attribute(W + "rsidR"));
        Assert.Equal("right", edited.Element(W + "pPr")!.Element(W + "jc")!.Attribute(W + "val")!.Value);
        XElement untouched = body.Elements(W + "p").ElementAt(1);
        Assert.NotNull(untouched.Descendants(W + "proofErr").FirstOrDefault());
    }

    [Fact]
    public void Word2010RunPropertiesAreWrittenInSchemaOrder()
    {
        // A text effect the model keeps as it is, and a number form it reads as a font feature: Word's schema puts the
        // effects first.
        Document document = DocxFormat.Load(new MemoryStream(Sample(
            "<w:p><w:r><w:rPr><w:b/><w14:shadow w14:blurRad=\"38100\" w14:dist=\"19050\" w14:dir=\"2700000\" w14:sx=\"100000\" w14:sy=\"100000\" w14:kx=\"0\" w14:ky=\"0\" w14:algn=\"tl\"/>"
            + "<w14:numForm w14:val=\"lining\"/></w:rPr><w:t>2026</w:t></w:r></w:p>")));
        document.Sections[0].Body.Paragraphs.First().Runs.First().Text = "2027";

        XElement rPr = Part(Save(document), "word/document.xml").Descendants(W + "rPr").First();

        Assert.Equal(["b", "shadow", "numForm"], rPr.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsNameNoteSeparatorsOnlyWithNotes(bool notes)
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("x");
        if (notes)
        {
            paragraph.AddFootnote("A note.");
        }

        using ZipArchive zip = new(new MemoryStream(Save(document)));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XElement settings = XElement.Parse(System.Text.Encoding.UTF8.GetString(ReadEntry(zip.GetEntry("word/settings.xml")!)));

        // Word reports a file unreadable when its settings point at separator notes it does not have.
        Assert.Equal(notes, zip.GetEntry("word/footnotes.xml") is not null);
        Assert.Equal(notes ? 2 : 0, settings.Element(w + "footnotePr")?.Elements(w + "footnote").Count() ?? 0);
        Assert.Null(settings.Element(w + "endnotePr"));
    }

    [Fact]
    public void EmbeddedFontsAreWrittenObfuscated()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("x");
        byte[] font = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        document.EmbeddedFonts.Add(new EmbeddedFont("Fancy", Bold: false, Italic: true, font));

        byte[] saved = Save(document);

        using ZipArchive zip = new(new MemoryStream(saved));
        byte[] stored = ReadEntry(zip.Entries.Single(e => e.FullName.EndsWith(".odttf", StringComparison.Ordinal)));
        Assert.NotEqual(font, stored);
        EmbeddedFont read = Assert.Single(DocxFormat.Load(new MemoryStream(saved)).EmbeddedFonts);
        Assert.Equal(("Fancy", false, true), (read.Family, read.Bold, read.Italic));
        Assert.Equal(font, read.Data.ToArray());
    }

    [Fact]
    public void TrackedChangesAndCommentsRoundTrip()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.AddRun("kept ");
        Run inserted = paragraph.AddRun("added");
        inserted.Revision = new Revision(RevisionKind.Insertion, "Ann", new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero));
        Run deleted = paragraph.AddRun("gone");
        deleted.Revision = new Revision(RevisionKind.Deletion, "Bob");
        Comment comment = paragraph.Inlines.Add(new Comment(document, "Ann"));
        comment.AddParagraph("Note this");
        comment.AddReply("Bob", "Agreed");
        comment.Done = true;

        Document read = RoundTrip(document);

        Run[] runs = [.. read.Descendants<Run>().Where(r => r.Paragraph?.OwnerStoryIsBody() == true)];
        Assert.Equal(RevisionKind.Insertion, runs.Single(r => r.Text == "added").Revision!.Kind);
        Assert.Equal(RevisionKind.Deletion, runs.Single(r => r.Text == "gone").Revision!.Kind);
        Comment[] comments = [.. read.Descendants<Comment>()];
        Assert.Equal(2, comments.Length);
        Assert.True(comments[0].Done);
        Assert.Same(comments[0], comments[1].ParentComment);
    }

    [Theory]
    [InlineData(RevisionKind.Deletion, "del", "delText", "delInstrText")]
    [InlineData(RevisionKind.Insertion, "ins", "t", "instrText")]
    public void ATrackedHyperlinkHoldsItsTrackedChange(RevisionKind kind, string change, string text, string code)
    {
        // A tracked change holds runs only, so Word calls a w:del round a w:hyperlink (with w:t inside) corrupt.
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.AddRun("See ");
        Hyperlink link = new(document) { Address = "https://example.com/" };
        link.Inlines.Add(new Run(document, "the site"));
        link.Inlines.Add(new FieldStart(document));
        link.Inlines.Add(new Run(document, " PAGEREF x "));
        link.Inlines.Add(new FieldSeparator(document));
        link.Inlines.Add(new Run(document, "2"));
        link.Inlines.Add(new FieldEnd(document));
        link.Revision = new Revision(kind, "Ann");
        paragraph.Inlines.Add(link);

        byte[] saved = Save(document);

        XElement hyperlink = Part(saved, "word/document.xml").Descendants(W + "hyperlink").Single();
        Assert.Equal(W + "p", hyperlink.Parent!.Name);
        XElement wrapper = Assert.Single(hyperlink.Elements());
        Assert.Equal(W + change, wrapper.Name);
        Assert.Equal(["the site", "2"], wrapper.Descendants(W + text).Select(t => t.Value));
        Assert.Equal(" PAGEREF x ", wrapper.Descendants(W + code).Single().Value);

        Document read = DocxFormat.Load(new MemoryStream(saved));
        Assert.Equal(kind, read.Descendants<Hyperlink>().Single().Revision?.Kind);
        Assert.All(read.Descendants<Run>(), r => Assert.Null(r.Revision));
        read.Revisions.AcceptAll();
        Assert.Equal(kind == RevisionKind.Insertion, read.Sections[0].Body.Paragraphs.Single().GetText().Contains("the site", StringComparison.Ordinal));
        Assert.Equal(kind == RevisionKind.Insertion, read.Descendants<Hyperlink>().Any());
    }

    [Fact]
    public void ADeletedNoteKeepsItsMarkAsDeletedText()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Text");
        Footnote note = paragraph.AddFootnote("A note.");
        note.CustomMark = "*";
        note.Revision = new Revision(RevisionKind.Deletion, "Ann");

        byte[] saved = Save(document);

        XElement deletion = Part(saved, "word/document.xml").Descendants(W + "del").Single();
        Assert.Equal("*", deletion.Descendants(W + "delText").Single().Value);
        Assert.Empty(deletion.Descendants(W + "t"));
        Footnote read = DocxFormat.Load(new MemoryStream(saved)).Descendants<Footnote>().Single();
        Assert.Equal(("*", RevisionKind.Deletion), (read.CustomMark, read.Revision?.Kind));
    }

    [Fact]
    public void AMovedHyperlinkKeepsItsMoveRange()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Revision from = new(RevisionKind.MoveFrom, "Ann") { MoveName = "move1" };
        Revision to = new(RevisionKind.MoveTo, "Ann") { MoveName = "move1" };
        foreach (Revision revision in (Revision[])[from, to])
        {
            Hyperlink link = new(document) { Address = "https://example.com/" };
            link.Inlines.Add(new Run(document, "the site"));
            link.Revision = revision;
            body.AddParagraph().Inlines.Add(link);
        }

        XElement xml = Part(Save(document), "word/document.xml");

        foreach (string side in (string[])["moveFrom", "moveTo"])
        {
            XElement start = xml.Descendants(W + side + "RangeStart").Single();
            Assert.Equal(W + "hyperlink", start.ElementsAfterSelf().First().Name);
            Assert.Equal(W + side, start.ElementsAfterSelf().First().Elements().Single().Name);
            Assert.Equal(W + side + "RangeEnd", start.ElementsAfterSelf().ElementAt(1).Name);
        }
    }

    [Fact]
    public void AnIslandFromAnotherFileBringsItsParts()
    {
        // A VML group keeps its markup, which names its image by relationship id; in another file that id would name
        // something else, such as the styles, and Word calls the file corrupt.
        const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:v=\"urn:schemas-microsoft-com:vml\""
            + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        OpcPackageWriter writer = new();
        writer.AddXmlPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", XDocument.Parse(
            "<w:document " + ns + "><w:body><w:p><w:r><w:pict><v:group style=\"width:10pt;height:10pt\"><v:shape style=\"width:10pt;height:10pt\"><v:imagedata r:id=\"rId1\"/></v:shape></v:group></w:pict></w:r>"
            + "<w:r><w:pict><v:group style=\"width:10pt;height:10pt\"><v:shape style=\"width:10pt;height:10pt\"><v:imagedata r:id=\"rId9\"/></v:shape></v:group></w:pict></w:r></w:p></w:body></w:document>"));
        writer.AddPart("/word/media/image1.png", "image/png", [1, 2, 3]);
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rId1", OpcRelationshipTypes.Image, "media/image1.png");
        using MemoryStream stream = new();
        writer.Save(stream);
        Document source = DocxFormat.Load(new MemoryStream(stream.ToArray()));
        Document other = new();
        other.AddSection().Body.AddParagraph("Text");
        Document document = RoundTrip(other);
        document.Sections[0].Body.Blocks.Add(document.Import(source.Sections[0].Body.Paragraphs.First()));

        byte[] saved = Save(document);

        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace v = "urn:schemas-microsoft-com:vml";
        XNamespace rels = "http://schemas.openxmlformats.org/package/2006/relationships";
        XElement[] images = [.. Part(saved, "word/document.xml").Descendants(v + "imagedata")];
        XElement relationship = Part(saved, "word/_rels/document.xml.rels").Elements(rels + "Relationship")
            .Single(e => (string?)e.Attribute("Id") == (string?)images[0].Attribute(r + "id"));
        Assert.Equal(OpcRelationshipTypes.Image, (string?)relationship.Attribute("Type"));
        using ZipArchive zip = new(new MemoryStream(saved));
        Assert.Equal([1, 2, 3], ReadEntry(zip.GetEntry("word/" + (string)relationship.Attribute("Target")!)!));
        Assert.Null(images[1].Attribute(r + "id"));
    }

    [Fact]
    public void MacrosAreKeptOrDroppedByKind()
    {
        const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        OpcPackageWriter writer = new();
        writer.AddXmlPart("/word/document.xml", "application/vnd.ms-word.document.macroEnabled.main+xml", XDocument.Parse("<w:document " + ns + "><w:body><w:p/></w:body></w:document>"));
        writer.AddPart("/word/vbaProject.bin", "application/vnd.ms-office.vbaProject", [1, 2, 3]);
        writer.AddXmlPart("/word/vbaData.xml", "application/vnd.ms-word.vbaData+xml", XDocument.Parse("<wne:vbaSuppData xmlns:wne=\"http://schemas.microsoft.com/office/word/2006/wordml\"/>"));
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rId1", OpcRelationshipTypes.VbaProject, "vbaProject.bin");
        writer.AddRelationship("/word/vbaProject.bin", "rId1", "http://schemas.microsoft.com/office/2006/relationships/wordVbaData", "vbaData.xml");
        using MemoryStream stream = new();
        writer.Save(stream);
        Document document = DocxFormat.Load(new MemoryStream(stream.ToArray()));

        Assert.Equal(DocxKind.MacroEnabledDocument, DocxFormat.KindOf(document));
        Assert.Null(DocxFormat.KindOf(new Document()));
        byte[] kept = Save(document);
        using (ZipArchive zip = new(new MemoryStream(kept)))
        {
            Assert.NotNull(zip.GetEntry("word/vbaProject.bin"));
            Assert.NotNull(zip.GetEntry("word/vbaData.xml"));
        }

        using MemoryStream asDocx = new();
        DocxFormat.Save(document, asDocx, new DocxSaveOptions { Kind = DocxKind.Document });
        using (ZipArchive zip = new(new MemoryStream(asDocx.ToArray())))
        {
            Assert.Null(zip.GetEntry("word/vbaProject.bin"));
            Assert.Null(zip.GetEntry("word/vbaData.xml"));
            if (zip.GetEntry("word/_rels/document.xml.rels") is { } rels)
            {
                Assert.DoesNotContain("vbaProject", System.Text.Encoding.UTF8.GetString(ReadEntry(rels)), StringComparison.Ordinal);
            }
        }

        Assert.Equal(DocxKind.Document, DocxFormat.KindOf(DocxFormat.Load(new MemoryStream(asDocx.ToArray()))));
        using MemoryStream asTemplate = new();
        DocxFormat.Save(new Document(), asTemplate, new DocxSaveOptions { Kind = DocxKind.Template });
        Assert.Equal(DocxKind.Template, DocxFormat.KindOf(DocxFormat.Load(new MemoryStream(asTemplate.ToArray()))));
    }

    [Fact]
    public void HyphenationSettingsRoundTrip()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("Hyphenation");
        Assert.False(document.Settings.AutoHyphenation);
        Assert.Equal(18, document.Settings.HyphenationZone);
        XElement plain = Part(Save(document), "word/settings.xml");
        Assert.Null(plain.Element(W + "autoHyphenation"));
        Assert.Null(plain.Element(W + "hyphenationZone"));
        Assert.Null(plain.Element(W + "consecutiveHyphenLimit"));
        Assert.Null(plain.Element(W + "doNotHyphenateCaps"));

        document.Settings.AutoHyphenation = true;
        document.Settings.ConsecutiveHyphenLimit = 3;
        document.Settings.HyphenationZone = 36;
        document.Settings.HyphenateCapitals = false;
        byte[] saved = Save(document);
        XElement settings = Part(saved, "word/settings.xml");
        Assert.NotNull(settings.Element(W + "autoHyphenation"));
        Assert.Equal("3", settings.Element(W + "consecutiveHyphenLimit")?.Attribute(W + "val")?.Value);
        Assert.Equal("720", settings.Element(W + "hyphenationZone")?.Attribute(W + "val")?.Value);
        Assert.NotNull(settings.Element(W + "doNotHyphenateCaps"));

        // Schema order: defaultTabStop, then the four in ECMA-376's order.
        string[] names = [.. settings.Elements().Select(e => e.Name.LocalName)];
        int tab = Array.IndexOf(names, "defaultTabStop");
        Assert.Equal(["autoHyphenation", "consecutiveHyphenLimit", "hyphenationZone", "doNotHyphenateCaps"], names[(tab + 1)..(tab + 5)]);

        Document read = DocxFormat.Load(new MemoryStream(saved));
        Assert.True(read.Settings.AutoHyphenation);
        Assert.Equal(3, read.Settings.ConsecutiveHyphenLimit);
        Assert.Equal(36, read.Settings.HyphenationZone);
        Assert.False(read.Settings.HyphenateCapitals);

        // Turning them back off on a read document removes the elements again.
        read.Settings.AutoHyphenation = false;
        read.Settings.ConsecutiveHyphenLimit = 0;
        read.Settings.HyphenationZone = 18;
        read.Settings.HyphenateCapitals = true;
        XElement cleared = Part(Save(read), "word/settings.xml");
        Assert.Null(cleared.Element(W + "autoHyphenation"));
        Assert.Null(cleared.Element(W + "consecutiveHyphenLimit"));
        Assert.Null(cleared.Element(W + "hyphenationZone"));
        Assert.Null(cleared.Element(W + "doNotHyphenateCaps"));
    }

    private static Document RoundTrip(Document document)
    {
        return DocxFormat.Load(new MemoryStream(Save(document)));
    }

    private static byte[] Save(Document document)
    {
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        return stream.ToArray();
    }

    private static XElement Part(byte[] package, string name)
    {
        using ZipArchive zip = new(new MemoryStream(package));
        return XElement.Parse(System.Text.Encoding.UTF8.GetString(ReadEntry(zip.GetEntry(name)!)).TrimStart('﻿'));
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>A small DOCX with markup the model does not name, or with the given paragraphs.</summary>
    private static byte[] Sample(string? paragraphs = null)
    {
        const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        const string w14 = " xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\"";
        OpcPackageWriter writer = new();
        writer.AddXmlPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", XDocument.Parse(
            "<w:document " + ns + (paragraphs is null ? "" : w14) + "><w:body>"
            + (paragraphs ?? "<w:p w:rsidR=\"00AB\"><w:pPr><w:kinsoku w:val=\"0\"/><w:jc w:val=\"right\"/></w:pPr><w:r><w:t>One</w:t></w:r></w:p>"
            + "<w:p><w:proofErr w:type=\"spellStart\"/><w:r><w:t>Twoo</w:t></w:r><w:proofErr w:type=\"spellEnd\"/></w:p>")
            + "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/></w:sectPr></w:body></w:document>"));
        writer.AddXmlPart("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml", XDocument.Parse(
            "<w:styles " + ns + "><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:rsid w:val=\"00AB\"/></w:style></w:styles>"));
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rId1", OpcRelationshipTypes.Styles, "styles.xml");
        using MemoryStream stream = new();
        writer.Save(stream);
        return stream.ToArray();
    }
}

internal static class StoryExtensions
{
    public static bool OwnerStoryIsBody(this Paragraph paragraph)
    {
        return paragraph.Ancestors().OfType<Story>().FirstOrDefault() is Body;
    }
}
