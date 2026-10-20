namespace Egap.Documents.Markdown.Tests;

/// <summary>What the reader adds on top of Markdig, and how it handles what Markdig refuses.</summary>
public sealed class MarkdigReaderTests
{
    [Theory]
    [InlineData("Write to foo@bar.baz today", "mailto:foo@bar.baz")]
    [InlineData("hello+xyz@mail.example.", "mailto:hello+xyz@mail.example")]
    [InlineData("www.google.com/search?q=(business))+ok", "http://www.google.com/search?q=(business))+ok")]
    public void GitHubAutolinksAreLinks(string markdown, string address)
    {
        Document document = MarkdownFormat.Parse(markdown);

        Assert.Equal(address, Assert.Single(document.Descendants<Hyperlink>()).Address);
    }

    [Theory]
    [InlineData("a.b-c_d@a.b_")]
    [InlineData("hello@mail+xyz")]
    public void NotQuiteAddressesStayText(string markdown)
    {
        Document document = MarkdownFormat.Parse(markdown);

        Assert.Empty(document.Descendants<Hyperlink>());
        Assert.Equal(markdown, document.GetText().TrimEnd('\r'));
    }

    [Fact]
    public void MarkdownNestedTooDeeplyIsReadAsPlainText()
    {
        string markdown = new string('>', 1_000) + " quote";

        Document document = MarkdownFormat.Parse(markdown, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Contains("quote", document.GetText(), StringComparison.Ordinal);
        Assert.Equal(ConversionWarningKind.FormattingDropped, Assert.Single(warnings).Kind);
    }

    [Fact]
    public void AnItemsFirstBlockGoesOnTheMarkerLine()
    {
        Document document = MarkdownFormat.Parse("1. ```\n   code\n   ```\n\n   after\n");

        string written = MarkdownFormat.ToMarkdown(document);

        Assert.StartsWith("1. ```", written, StringComparison.Ordinal);
        Assert.Equal(written, MarkdownFormat.ToMarkdown(MarkdownFormat.Parse(written)));
    }

    [Fact]
    public void UnclosedBracketsKeepWhatFollowsThem()
    {
        Document document = MarkdownFormat.Parse("[not a `link](/foo`)");

        Assert.Equal("[not a link](/foo)", document.GetText().TrimEnd('\r'));
    }
}
