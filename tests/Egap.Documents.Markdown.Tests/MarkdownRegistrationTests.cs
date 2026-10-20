using System.Text;

namespace Egap.Documents.Markdown.Tests;

/// <summary>Markdown through Document.Load and Save once registered, and through the extension members.</summary>
public sealed class MarkdownRegistrationTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("egap-markdown-").FullName;

    public MarkdownRegistrationTests()
    {
        MarkdownFormat.Register();
        MarkdownFormat.Register();
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void RegisteredMarkdownLoadsAndSavesByExtensionAndContent()
    {
        string path = Path.Combine(folder, "notes.md");
        Document document = new();
        document.AddSection().Body.AddParagraph("Saved as Markdown");

        Assert.NotNull(document.Save(path));
        Assert.Contains("Saved as Markdown", Document.Load(path).GetText(), StringComparison.Ordinal);
        using MemoryStream text = new(Encoding.UTF8.GetBytes("# From Markdown"));
        Assert.Equal(DocumentFormatKind.Markdown, Document.Identify(text));
        Assert.Equal("Heading1", Document.Load(text).Sections[0].Body.Paragraphs.First().Style!.Id);
    }

    [Fact]
    public void MembersForwardToMarkdownFormat()
    {
        string path = Path.Combine(folder, "member.md");
        Document parsed = Document.ParseMarkdown("Some *emphasis*");
        parsed.SaveAsMarkdown(path);

        Assert.Equal(MarkdownFormat.ToMarkdown(parsed), parsed.ToMarkdown());
        Assert.Contains("*emphasis*", Document.LoadMarkdown(path).ToMarkdown(), StringComparison.Ordinal);
        Assert.Equal("Some emphasis", Document.ParseMarkdown("Some *emphasis*", out IReadOnlyList<ConversionWarning> warnings).GetText().TrimEnd());
        Assert.Empty(warnings);
    }
}
