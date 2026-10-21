using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>HTML read into the model through the CSS cascade (Phase 6 Build Plan task 25).</summary>
public class HtmlReaderTests
{
    private const string Page = """
        <!DOCTYPE html>
        <html lang="en"><head><title>T</title>
        <style>
          @page { size: A4; margin: 2cm; }
          body { font-family: Georgia, serif; font-size: 11pt; }
          .note { color: #c00; border: 1px solid black; padding: 4pt; background: #eee; }
          h1 { page-break-before: always; }
        </style></head>
        <body>
          <h1 id="top">Title</h1>
          <p>Plain <b>bold</b> <i>italic</i>   and <a href="https://example.com/">a link</a>.</p>
          <p class="note">Boxed</p>
          <ul><li>one<ul><li>nested</li></ul></li><li>two</li></ul>
          <ol start="3"><li>three</li></ol>
          <table border="1"><tr><td colspan="2">wide</td><td rowspan="2">tall</td></tr><tr><td>a</td><td>b</td></tr></table>
          <pre>line one
          line two</pre>
          <div style="display:flex"><span>flex</span></div>
        </body></html>
        """;

    [Fact]
    public void BlocksInlinesAndPageSetup()
    {
        Document document = HtmlFormat.Parse(Page, out IReadOnlyList<ConversionWarning> warnings);
        Section section = document.Sections[0];
        Paragraph[] paragraphs = [.. section.Body.Paragraphs];

        Assert.Equal(595.28, section.PageSetup.PageWidth!.Value, 1);
        Assert.Equal(56.69, section.PageSetup.MarginLeft!.Value, 1);

        Paragraph title = paragraphs[0];
        Assert.Equal("Title", title.GetText().Trim('\r'));
        Assert.Equal(0, title.GetEffectiveFormat().OutlineLevel);
        Assert.True(title.Format.PageBreakBefore);
        Assert.Contains(title.Inlines, i => i is BookmarkStart { Name: "top" });
        Assert.Equal(22, title.Runs.Single().GetEffectiveFormat().Size);

        Paragraph plain = paragraphs[1];
        Assert.Equal("Plain bold italic and a link.", string.Concat(plain.Descendants<Run>().Select(r => r.Text)));
        Assert.True(plain.Runs.Single(r => r.Text == "bold").GetEffectiveFormat().Bold);
        Assert.Equal("Georgia", plain.Runs.First().Format.FontAscii);
        Assert.Equal("https://example.com/", plain.Inlines.OfType<Hyperlink>().Single().Address);

        Paragraph boxed = paragraphs[2];
        Assert.Equal(DocumentColor.FromHex("CC0000"), boxed.Runs.Single().Format.Color);
        Assert.Equal(0.75, boxed.Format.BorderTop!.Width, 2);
        Assert.Equal(4, boxed.Format.BorderTop.Space, 2);
        Assert.NotNull(boxed.Format.Shading);

        Paragraph[] items = [.. paragraphs.Where(p => p.ListLabel is not null)];
        Assert.Equal(["one", "nested", "two", "three"], items.Select(p => p.GetText().Trim('\r')).ToArray());
        Assert.Equal(1, items[1].Format.ListLevel);
        Assert.Equal("3.", items[3].ListLabel!.Text);

        Table table = section.Body.Tables.Single();
        Assert.Equal(2, table.Rows[0].Cells[0].Format.GridSpan);
        Assert.Equal(VerticalMerge.Restart, table.Rows[0].Cells[1].Format.VerticalMerge);
        Assert.Equal(VerticalMerge.Continue, table.Rows[1].Cells[2].Format.VerticalMerge);
        Assert.NotNull(table.Rows[1].Cells[0].Format.BorderTop);

        Paragraph pre = paragraphs.Single(p => p.GetText().StartsWith("line one", StringComparison.Ordinal));
        Assert.Contains(pre.Inlines, i => i is Break);
        Assert.Equal("Courier New", pre.Runs.First().Format.FontAscii);

        // A flex container is laid out as one, with nothing approximated.
        Assert.Equal("flex", Assert.Single(section.Body.Blocks.OfType<FlexContainer>()).Items.Single().GetText().Trim());
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.LayoutApproximated);
    }

    [Fact]
    public void PicturesFromDataUrlsOrTheResolver()
    {
        byte[] png;
        using (MemoryStream stream = new())
        {
            new Document().Layout(new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false } }).Pages[0].Render(8).Save(stream, "png");
            png = stream.ToArray();
        }

        string html = $"""<p><img src="data:image/png;base64,{Convert.ToBase64String(png)}" width="40" alt="inline"><img src="pic.png" style="float:right; width: 30pt"><img src="missing.png" alt="gone"></p>""";
        Uri? asked = null;
        Document document = HtmlFormat.Parse(html, out IReadOnlyList<ConversionWarning> warnings, new HtmlLoadOptions
        {
            BaseUri = new Uri("https://example.com/docs/"),
            ResourceResolver = uri =>
            {
                asked ??= uri;
                return uri.AbsolutePath.EndsWith("pic.png", StringComparison.Ordinal) ? png : null;
            },
        });

        Picture[] pictures = [.. document.Descendants<Picture>()];
        Assert.Equal(2, pictures.Length);
        Assert.Equal(30, pictures[0].Width, 1);
        Assert.Equal("image/png", pictures[0].Image.ContentType);
        Assert.Equal(30, pictures[1].Width, 1);
        Assert.Equal(FloatingAlignment.End, pictures[1].Floating!.HorizontalAlignment);
        Assert.Equal(new Uri("https://example.com/docs/pic.png"), asked);
        Assert.Contains("gone", document.GetText(), StringComparison.Ordinal);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.ResourceNotLoaded);
    }

    [Fact]
    public void NothingIsFetchedWithoutAResolver()
    {
        Document document = HtmlFormat.Parse("<link rel=stylesheet href=\"https://example.com/x.css\"><p style=\"color: blue\">x <img src=\"https://example.com/i.png\"></p>");

        Assert.Empty(document.Descendants<Picture>());
        Assert.Equal(DocumentColor.FromHex("0000FF"), document.Descendants<Run>().First().Format.Color);
    }

    [Fact]
    public void TheDocumentLaysOut()
    {
        Document document = HtmlFormat.Parse(Page);

        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false } });

        Assert.True(layout.Pages.Count >= 1);
        Assert.Contains(layout.Pages.SelectMany(p => p.BodyLines), l => l.Text.Contains("nested", StringComparison.Ordinal));
    }
}
