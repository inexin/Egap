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
    public void MarkdownBecomesDocx()
    {
        Document document = MarkdownFormat.Parse("# Heading\n\nSome *emphasis* and a [link](https://example.com/).\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");

        Document read = RoundTrip(document);

        Assert.Equal(document.GetText(), read.GetText());
        Assert.Equal(2, read.Descendants<Paragraph>().Count(p => p.ListLabel is not null));
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

    /// <summary>A small DOCX with markup the model does not name.</summary>
    private static byte[] Sample()
    {
        const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        OpcPackageWriter writer = new();
        writer.AddXmlPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", XDocument.Parse(
            "<w:document " + ns + "><w:body>"
            + "<w:p w:rsidR=\"00AB\"><w:pPr><w:kinsoku w:val=\"0\"/><w:jc w:val=\"right\"/></w:pPr><w:r><w:t>One</w:t></w:r></w:p>"
            + "<w:p><w:proofErr w:type=\"spellStart\"/><w:r><w:t>Twoo</w:t></w:r><w:proofErr w:type=\"spellEnd\"/></w:p>"
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
