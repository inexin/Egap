namespace Egap.Documents.Tests;

/// <summary>The plain-text writer (Phase 6 Build Plan task 8c).</summary>
public sealed class TextFormatTests
{

    [Fact]
    public void FieldCodesDeletionsAndHiddenTextAreLeftOut()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Page ");
        paragraph.Inlines.Add(new FieldStart(document));
        paragraph.AddRun(" PAGE ");
        paragraph.Inlines.Add(new FieldSeparator(document));
        paragraph.AddRun("3");
        paragraph.Inlines.Add(new FieldEnd(document));
        paragraph.AddRun(" kept");
        paragraph.AddRun(" gone").Revision = new Revision(RevisionKind.Deletion, "me");
        paragraph.AddRun(" hidden").Format.Hidden = true;

        Assert.Equal("Page 3 kept\n", TextFormat.ToText(document));
    }

    [Fact]
    public void HeadersFootersAndNotesAreOptions()
    {
        Document document = new();
        Section section = document.AddSection();
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Head");
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderFirst).AddParagraph("Head");
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.FooterPrimary).AddParagraph("Foot");
        section.Body.AddParagraph("Body");

        Assert.Equal("Body\n", TextFormat.ToText(document));
        Assert.Equal("Head\r\nBody\r\nFoot\r\n", TextFormat.ToText(document, new TextSaveOptions { IncludeHeadersAndFooters = true, NewLine = "\r\n" }));
    }

    [Fact]
    public void DocumentSaveWritesTxtAsPlainText()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-text-{Guid.NewGuid():N}.txt");
        try
        {
            Document document = new();
            document.AddSection().Body.AddParagraph("Plain **not markdown**");

            document.Save(path);

            Assert.Equal("Plain **not markdown**\n", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
