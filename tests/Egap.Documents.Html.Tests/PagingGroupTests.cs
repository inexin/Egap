using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>Paging options on groups of blocks.</summary>
public sealed class PagingGroupTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

    [Fact]
    public void TheBuilderMakesGroupsAndHtmlBreakInsideAvoidKeepsBoxesTogether()
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        builder.Writeln("Before");
        ContentControl group = builder.StartGroup();
        group.Paging = new GroupPaging { KeepTogether = true };
        builder.Writeln("Inside one");
        builder.Write("Inside two");
        Assert.Same(group, builder.EndGroup());
        builder.Write("After");

        Assert.Equal(["Inside one", "Inside two"], group.Blocks.OfType<Paragraph>().Select(p => p.GetText().Trim()));
        Assert.Equal("After", document.Sections[0].Body.Blocks.OfType<Paragraph>().Last().GetText().Trim());
        Assert.Throws<InvalidOperationException>(builder.EndGroup);

        Document html = HtmlFormat.Parse("<p>a</p><div style='break-inside: avoid'><p>b</p><p>c</p></div>");
        ContentControl kept = Assert.Single(html.Descendants().OfType<ContentControl>());
        Assert.True(kept.Paging!.KeepTogether);
        Assert.Equal(2, kept.Blocks.Count);
        Assert.True(kept.Clone().Paging!.KeepTogether);
    }

    private static (Document, ContentControl) FillerThenGroup(int fillers, int groupParagraphs, GroupPaging paging)
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.PageWidth = 400;
        section.PageSetup.PageHeight = 400;
        section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 36;
        for (int i = 0; i < fillers; i++)
        {
            section.Body.AddParagraph("Filler line " + i);
        }

        ContentControl group = new(document) { Paging = paging };
        for (int i = 0; i < groupParagraphs; i++)
        {
            group.AddParagraph("Group line " + i);
        }

        section.Body.Blocks.Add(group);
        return (document, group);
    }
}
