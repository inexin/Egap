using System.Diagnostics;

namespace Egap.Documents.Tests;

/// <summary>
/// Markdown into the model (Phase 6 Build Plan task 7, design Q17). The CommonMark and GFM spec examples run in the compare
/// repo (CC BY-SA); these tests check the mapping onto the model and hostile input.
/// </summary>
public class MarkdownReaderTests
{
    [Fact]
    public void HeadingsParagraphsAndEmphasis()
    {
        Document doc = MarkdownFormat.Parse("# Title\n\nSome *italic*, **bold** and ~~gone~~ text\nacross lines.  \nNew line.\n\n## Sub");
        List<Paragraph> paragraphs = [.. doc.Sections[0].Body.Paragraphs];

        Assert.Equal("Heading1", paragraphs[0].Style!.Id);
        Assert.Equal("Title", paragraphs[0].GetText());
        Assert.Equal("Some italic, bold and gone text across lines.\vNew line.", paragraphs[1].GetText());
        Assert.True(paragraphs[1].Runs.Single(r => r.Text == "italic").Format.Italic);
        Assert.True(paragraphs[1].Runs.Single(r => r.Text == "bold").Format.Bold);
        Assert.True(paragraphs[1].Runs.Single(r => r.Text == "gone").Format.Strikethrough);
        Assert.Equal("Heading2", paragraphs[2].Style!.Id);
    }

    [Fact]
    public void CodeSpansAndBlocks()
    {
        Document doc = MarkdownFormat.Parse("Use `Parse()` here.\n\n```csharp\nvar x = 1;\nvar y = 2;\n```\n");
        List<Paragraph> paragraphs = [.. doc.Sections[0].Body.Paragraphs];

        Assert.Equal("HTMLCode", paragraphs[0].Runs.Single(r => r.Text == "Parse()").Style!.Id);
        Assert.Equal("HTMLPreformatted", paragraphs[1].Style!.Id);
        Assert.Equal("var x = 1;\vvar y = 2;", paragraphs[1].GetText());
        Assert.Equal("Consolas", paragraphs[1].Runs.First().GetEffectiveFormat().FontAscii);
    }

    [Fact]
    public void ListsBecomeWordListsWithLabels()
    {
        Document doc = MarkdownFormat.Parse("1. one\n2. two\n   - inner\n   - inner two\n3. three\n\n- [ ] todo\n- [x] done\n");
        List<Paragraph> paragraphs = [.. doc.Sections[0].Body.Paragraphs];

        Assert.Equal(["1.", "2.", "o", "o", "3."], paragraphs.Take(5).Select(p => p.ListLabel!.Text));
        Assert.Equal([0, 0, 1, 1, 0], paragraphs.Take(5).Select(p => p.ListLabel!.Level));
        Assert.NotSame(paragraphs[0].ListLabel!.List, paragraphs[5].ListLabel!.List);
        InlineContentControl box = paragraphs[6].Inlines.OfType<InlineContentControl>().Single();
        Assert.True(box.Properties.IsChecked);
        Assert.Equal(ContentControlKind.CheckBox, box.Properties.Kind);
        Assert.Equal("☒ done", paragraphs[6].GetText());
    }

    [Fact]
    public void OrderedListStartAndLooseItems()
    {
        Document doc = MarkdownFormat.Parse("7) seven\n\n8) eight\n\n   more of eight\n");
        List<Paragraph> paragraphs = [.. doc.Sections[0].Body.Paragraphs];

        Assert.Equal("7)", paragraphs[0].ListLabel!.Text);
        Assert.Equal("8)", paragraphs[1].ListLabel!.Text);
        Assert.Null(paragraphs[2].ListLabel);
        Assert.Equal(36, paragraphs[2].GetEffectiveFormat().LeftIndent);
        Assert.False(paragraphs[0].GetEffectiveFormat().ContextualSpacing);
    }

    [Fact]
    public void LinksImagesAndAutolinks()
    {
        Document doc = MarkdownFormat.Parse("[Egap](https://github.com/inexin/Egap \"repo\") and [here](#top) and www.example.com and <a@b.co>\n\n![chart *one*](chart.png \"Chart\")\n\n[ref]\n\n[ref]: /url");
        List<Paragraph> paragraphs = [.. doc.Sections[0].Body.Paragraphs];
        List<Hyperlink> links = [.. paragraphs[0].Inlines.OfType<Hyperlink>()];

        Assert.Equal("https://github.com/inexin/Egap", links[0].Address);
        Assert.Equal("repo", links[0].Tooltip);
        Assert.Equal("Hyperlink", links[0].Runs.Single().Style!.Id);
        Assert.Equal("top", links[1].Anchor);
        Assert.Equal("http://www.example.com", links[2].Address);
        Assert.Equal("mailto:a@b.co", links[3].Address);
        Picture picture = paragraphs[1].Inlines.OfType<Picture>().Single();
        Assert.Equal("chart.png", picture.SourceUrl);
        Assert.Equal("chart one", picture.Description);
        Assert.Equal("Chart", picture.Title);
        Assert.Equal("/url", paragraphs[2].Inlines.OfType<Hyperlink>().Single().Address);
    }

    [Fact]
    public void ImagesComeThroughTheResolverOnly()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];
        List<string> asked = [];
        Document doc = MarkdownFormat.Parse("![a](a.png)", new MarkdownLoadOptions { ImageResolver = url => { asked.Add(url); return ImageData.FromBytes(png); } });

        Assert.Equal(["a.png"], asked);
        Assert.Equal("image/png", doc.Descendants<Picture>().Single().Image.ContentType);
    }

    [Fact]
    public void TablesBlockQuotesAndRules()
    {
        Document doc = MarkdownFormat.Parse("| A | B |\n|:--|--:|\n| 1 | 2 |\n\n> quoted\n> > deeper\n\n---\n");
        Body body = doc.Sections[0].Body;
        Table table = body.Tables.Single();

        Assert.Equal("TableGrid", table.Style!.Id);
        Assert.True(table.Rows[0].Format.IsHeader);
        Assert.True(table.Rows[0].Cells[0].Paragraphs.Single().Runs.Single().Format.Bold);
        Assert.Equal(ParagraphAlignment.Right, table.Rows[1].Cells[1].Paragraphs.Single().Format.Alignment);
        List<Paragraph> quotes = [.. body.Paragraphs.Where(p => p.Style?.Id == "BlockQuote")];
        Assert.Equal([36.0, 72.0], quotes.Select(q => q.GetEffectiveFormat().LeftIndent));
        Assert.NotNull(body.Paragraphs.Last().Format.BorderBottom);
    }

    [Fact]
    public void FootnotesBecomeNotes()
    {
        Document doc = MarkdownFormat.Parse("Claim[^1] and again[^1].\n\n[^1]: The source, see[^1].\n");
        List<Footnote> notes = [.. doc.Descendants<Footnote>()];

        Assert.Equal(2, notes.Count);
        Assert.Equal("The source, see1.\r", notes[0].Story.GetText());
        Assert.Equal("1", notes[0].Mark);
        Assert.Equal("2", notes[1].Mark);
    }

    [Fact]
    public void FrontMatterGivesProperties()
    {
        Document doc = MarkdownFormat.Parse("---\ntitle: Annual report\nauthor: \"Lars\"\ntags: [a, b]\n---\n# Body\n");

        Assert.Equal("Annual report", doc.Properties.Title);
        Assert.Equal("Lars", doc.Properties.Author);
        Assert.Equal("a, b", doc.Properties.Keywords);
        Assert.Equal("Body", doc.Sections[0].Body.GetText().TrimEnd('\r'));
        Document unclosed = MarkdownFormat.Parse("---\n\ntext");
        Assert.Null(unclosed.Properties.Title);
        Assert.Equal(2, unclosed.Sections[0].Body.Paragraphs.Count());
    }

    [Fact]
    public void RawHtmlIsKept()
    {
        Document doc = MarkdownFormat.Parse("<div class=\"x\">\nblock\n</div>\n\nText <b>bold</b>.");

        Assert.Equal("<div class=\"x\">\nblock\n</div>", doc.Sections[0].Body.Blocks.OfType<BlockIsland>().Single().Markup);
        Assert.Equal(["<b>", "</b>"], doc.Descendants<Island>().Select(i => i.Markup));
    }

    [Theory]
    [InlineData(100_000, '>')]
    [InlineData(100_000, '*')]
    [InlineData(100_000, '[')]
    [InlineData(50_000, '`')]
    public void HostileInputStaysFast(int count, char c)
    {
        string markdown = c switch
        {
            '>' => new string('>', count) + " deep",
            '*' => string.Concat(Enumerable.Repeat("*a **b ", count / 7)) + string.Concat(Enumerable.Repeat("b** a*", count / 7)),
            '[' => new string('[', count) + "a" + new string(']', count),
            _ => string.Concat(Enumerable.Range(1, 300).Select(i => new string('`', i) + "x")),
        };
        Stopwatch watch = Stopwatch.StartNew();

        Document doc = MarkdownFormat.Parse(markdown);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed} for {c}");
        Assert.NotEmpty(doc.Sections[0].Body.GetText());
    }

    [Fact]
    public void DeepListsStopNestingAtTheLimit()
    {
        string markdown = string.Concat(Enumerable.Range(0, 300).Select(i => new string(' ', i * 2) + "- item\n"));

        Document doc = MarkdownFormat.Parse(markdown);

        Assert.True(doc.Sections[0].Body.Paragraphs.All(p => p.ListLabel is null || p.ListLabel.Level <= 8));
    }
}
