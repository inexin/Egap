using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Paging options on groups of blocks.</summary>
public sealed class PagingGroupTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

    [Fact]
    public void AKeptTogetherGroupMovesToTheNextPageWhole()
    {
        (Document document, ContentControl group) = FillerThenGroup(fillers: 12, groupParagraphs: 4, new GroupPaging { KeepTogether = true });

        DocumentLayout layout = document.Layout(Isolated);

        int[] pages = [.. group.Blocks.Select(b => layout.PageOf(b)!.Index)];
        Assert.All(pages, p => Assert.Equal(pages[0], p));
        Assert.Equal(1, pages[0]);

        // Without the option the group starts on the first page and runs on.
        group.Paging = null;
        DocumentLayout plain = document.Layout(Isolated);
        Assert.Equal(0, plain.PageOf(group.Blocks[0])!.Index);
        Assert.Equal(1, plain.PageOf(group.Blocks[^1])!.Index);
    }

    [Fact]
    public void EnsureSpaceStartsTheGroupOnTheNextPageWhenTooLittleIsLeft()
    {
        (Document document, ContentControl group) = FillerThenGroup(fillers: 12, groupParagraphs: 1, new GroupPaging { EnsureSpace = 200 });

        Assert.Equal(1, document.Layout(Isolated).PageOf(group.Blocks[0])!.Index);

        group.Paging = new GroupPaging { EnsureSpace = 10 };
        Assert.Equal(0, document.Layout(Isolated).PageOf(group.Blocks[0])!.Index);
    }

    [Fact]
    public void StopPagingDropsWhatWouldGoOnLaterPages()
    {
        (Document document, ContentControl group) = FillerThenGroup(fillers: 10, groupParagraphs: 30, new GroupPaging { StopPaging = true });

        DocumentLayout layout = document.Layout(Isolated);

        Assert.Single(layout.Pages);
        Assert.NotNull(layout.PageOf(group.Blocks[0]));
        Assert.Null(layout.PageOf(group.Blocks[^1]));
    }

    [Fact]
    public void ShowEntireTooTallForAPageFailsWhenStrict()
    {
        (Document document, _) = FillerThenGroup(fillers: 0, groupParagraphs: 60, new GroupPaging { ShowEntire = true });

        LayoutException e = Assert.Throws<LayoutException>(() => document.Layout(new LayoutOptions { Strict = true, Fonts = FontSettings.Portable }));

        Assert.Equal(LayoutProblem.ContentOverflow, e.Problem);
        Assert.IsType<ContentControl>(e.Node);
        Assert.True(document.Layout(Isolated).Pages.Count > 1);
    }

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
