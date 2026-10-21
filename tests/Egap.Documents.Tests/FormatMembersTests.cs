using Egap.Documents.Pdf;

namespace Egap.Documents.Tests;

/// <summary>The core formats as members of Document, forwarding to the format classes.</summary>
public sealed class FormatMembersTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("egap-members-").FullName;

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private static Document Sample()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("Hello from a member");
        return document;
    }

    [Fact]
    public void DocxLoadsAndSavesThroughDocument()
    {
        string path = Path.Combine(folder, "a.docx");
        Assert.NotNull(Sample().SaveAsDocx(path));
        using MemoryStream stream = new();
        Sample().SaveAsDocx(stream);
        stream.Position = 0;

        Assert.Contains("Hello from a member", Document.LoadDocx(path).GetText(), StringComparison.Ordinal);
        Assert.Contains("Hello from a member", Document.LoadDocx(stream, out IReadOnlyList<ConversionWarning> warnings).GetText(), StringComparison.Ordinal);
        Assert.NotNull(warnings);
    }

    [Fact]
    public void TextLoadsAndSavesThroughDocument()
    {
        string path = Path.Combine(folder, "a.txt");
        Sample().SaveAsText(path);

        Assert.Equal("Hello from a member", Document.LoadText(path).ToText().TrimEnd());
        Assert.Equal(2, Document.ParseText("one\ntwo").Sections[0].Body.Paragraphs.Count());
        Assert.Equal(Sample().ToText(), TextFormat.ToText(Sample()));
    }

    [Fact]
    public void PdfLoadsThroughDocument()
    {
        string path = Path.Combine(folder, "a.pdf");
        Sample().SaveAsPdf(path);

        Document document = Document.LoadPdf(path);

        Assert.Equal(path, document.FileName);
        Assert.Contains("Hello from a member", document.GetText(), StringComparison.Ordinal);
    }
}
