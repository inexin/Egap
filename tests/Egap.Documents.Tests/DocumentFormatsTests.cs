using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Documents.Tests;

/// <summary>Loading and saving without naming the format, telling formats apart, and Find.</summary>
public sealed class DocumentFormatsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("egap-formats-").FullName;

    public DocumentFormatsTests()
    {
        HtmlFormat.Register();
        MarkdownFormat.Register();
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private static Document Sample()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        body.AddParagraph("Hello, world");
        body.AddParagraph("Second paragraph with world again");
        return document;
    }

    [Theory]
    [InlineData("a.docx")]
    [InlineData("a.html")]
    [InlineData("a.md")]
    [InlineData("a.txt")]
    public void SaveByExtensionAndLoadBack(string name)
    {
        string path = Path.Combine(folder, name);

        IReadOnlyList<ConversionWarning> saved = Sample().Save(path);
        Document loaded = Document.Load(path, out IReadOnlyList<ConversionWarning> read);

        Assert.NotNull(saved);
        Assert.NotNull(read);
        Assert.Equal(path, loaded.FileName);
        Assert.Contains("Second paragraph with world again", loaded.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void StreamsAreToldApartByTheirBytes()
    {
        using MemoryStream docx = new();
        DocxFormat.Save(Sample(), docx);
        docx.Position = 0;
        using MemoryStream html = new(Encoding.UTF8.GetBytes("\uFEFF  <!DOCTYPE html><p>From <b>HTML</b></p>"));
        using MemoryStream markdown = new(Encoding.UTF8.GetBytes("# From Markdown\n\n<b>inline html</b> stays text"));

        Assert.Contains("Hello, world", Document.Load(docx).GetText(), StringComparison.Ordinal);
        Assert.Contains("From HTML", Document.Load(html).GetText(), StringComparison.Ordinal);
        Assert.Equal("Heading1", Document.Load(markdown).Sections[0].Body.Paragraphs.First().Style!.Id);
    }

    [Fact]
    public void UnknownExtensionsAndPdfAreRefused()
    {
        NotSupportedException pdf = Assert.Throws<NotSupportedException>(() => Sample().Save(Path.Combine(folder, "a.pdf")));
        Assert.Contains("Egap.Pdf.Conversion", pdf.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Sample().Save(Path.Combine(folder, "a.xyz")));
        using MemoryStream binary = new([0x00, 0x01, 0x02, 0x03]);
        Assert.Throws<DocumentException>(() => Document.Load(binary));
    }

    [Theory]
    [InlineData("%PDF-1.7\n%âã", null, DocumentFormatKind.Pdf)]
    [InlineData("junk before the header %PDF-2.0\n", "scan.bin", DocumentFormatKind.Pdf)]
    [InlineData("{ \"schema_name\": \"DoclingDocument\", \"version\": \"1.3.0\" }", "doc.json", DocumentFormatKind.DoclingJson)]
    [InlineData("﻿{\"schema_name\":\"DoclingDocument\"}", null, DocumentFormatKind.DoclingJson)]
    [InlineData("{ \"name\": \"other\" }", "data.json", DocumentFormatKind.Markdown)]
    [InlineData("  <!DOCTYPE html><p>x</p>", null, DocumentFormatKind.Html)]
    [InlineData("<p>x</p>", "page.md", DocumentFormatKind.Markdown)]
    [InlineData("# Title", null, DocumentFormatKind.Markdown)]
    [InlineData("# Title", "notes.txt", DocumentFormatKind.Text)]
    [InlineData("plain", ".TXT", DocumentFormatKind.Text)]
    [InlineData("a\0b", null, DocumentFormatKind.Unknown)]
    public void IdentifyTellsFormatsApartWithoutLoading(string content, string? fileName, DocumentFormatKind expected)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));

        Assert.Equal(expected, Document.Identify(stream, fileName));
    }

    [Fact]
    public void IdentifyPutsTheStreamBackAndNeedsNoAddOn()
    {
        using MemoryStream docx = new();
        DocxFormat.Save(Sample(), docx);
        docx.Position = 0;
        Assert.Equal(DocumentFormatKind.Docx, Document.Identify(docx));
        Assert.Equal(0, docx.Position);

        string path = Path.Combine(folder, "page.html");
        File.WriteAllText(path, "<p>x</p>");
        Assert.Equal(DocumentFormatKind.Html, Document.Identify(path));
    }

    [Fact]
    public void FormatsThatNeedAnotherPackageNameIt()
    {
        string path = Path.Combine(folder, "a.pdf");
        File.WriteAllText(path, "%PDF-1.7\n");

        NotSupportedException e = Assert.Throws<NotSupportedException>(() => Document.Load(path));
        Assert.Contains("Egap.Pdf.Conversion", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextIsReadLineByLine()
    {
        Document document = TextFormat.Parse("First\tline\r\nSecond\n\nPage\fbreak\n");
        Paragraph[] paragraphs = [.. document.Sections[0].Body.Paragraphs];

        Assert.Equal(4, paragraphs.Length);
        Assert.Equal("Second", paragraphs[1].GetText().TrimEnd('\r'));
        Assert.IsType<Tab>(paragraphs[0].Inlines[1]);
        Assert.Equal("", paragraphs[2].GetText().TrimEnd('\r'));
        Assert.Equal(BreakKind.Page, Assert.IsType<Break>(paragraphs[3].Inlines[1]).Kind);
        Assert.Single(TextFormat.Parse("").Sections[0].Body.Paragraphs);
    }

    [Fact]
    public void FindReturnsMatchesAcrossRuns()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.AddRun("Wor");
        paragraph.AddRun("ld and world, ");
        paragraph.AddRun("WORLDS");

        IReadOnlyList<TextMatch> words = document.Find("world", new FindOptions { WholeWord = true });
        IReadOnlyList<TextMatch> any = document.Find(new Regex("worlds?", RegexOptions.IgnoreCase));

        Assert.Equal([(0, "World"), (10, "world")], words.Select(m => (m.Start, m.Value)));
        Assert.All(words, m => Assert.Same(paragraph, m.Paragraph));
        Assert.Equal(3, any.Count);
        Assert.Equal("WORLDS", any[^1].Value);
    }
}
