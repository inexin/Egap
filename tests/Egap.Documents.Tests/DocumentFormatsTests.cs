using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Documents.Tests;

/// <summary>Loading and saving without naming the format, and Find (Phase 6 API review points 2 to 4).</summary>
public sealed class DocumentFormatsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("egap-formats-").FullName;

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
        Assert.Throws<ArgumentException>(() => Sample().Save(Path.Combine(folder, "a.pdf")));
        Assert.Throws<ArgumentException>(() => Sample().Save(Path.Combine(folder, "a.xyz")));
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
