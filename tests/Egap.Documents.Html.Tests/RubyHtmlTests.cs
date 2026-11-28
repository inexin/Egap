namespace Egap.Documents.Html.Tests;

/// <summary>HTML's ruby, CSS emphasis marks and text-combine-upright, read and written; EPUB through the same path.</summary>
public sealed class RubyHtmlTests
{
    [Fact]
    public void RubyElementsBecomeRubies()
    {
        Document document = HtmlFormat.Parse(
            "<p>See <ruby>漢<rp>(</rp><rt>kan</rt><rp>)</rp>字<rt style=\"ruby-position: under; font-family: Mincho\">ji</rt></ruby> and <ruby><rb>東京</rb><rt>とうきょう</rt></ruby>.</p>");

        Ruby[] rubies = [.. document.Descendants<Ruby>()];
        Assert.Equal(["kan", "ji", "とうきょう"], rubies.Select(r => r.Text));
        Assert.Equal(["漢", "字", "東京"], rubies.Select(r => r.GetText()));
        Assert.Equal(RubyPosition.Under, rubies[1].Position);
        Assert.Equal("Mincho", rubies[1].TextFormat.FontAscii);
        Assert.Null(rubies[0].TextFormat.FontAscii);
        Assert.Equal(6, rubies[0].FontSize);
        Assert.Equal("See 漢字 and 東京.", document.Descendants<Paragraph>().Single().GetText());
    }

    [Fact]
    public void EmphasisAndCombineUprightAreRead()
    {
        Document document = HtmlFormat.Parse(
            "<p><span style=\"text-emphasis: filled sesame\">a</span><span style=\"text-emphasis-style: dot; text-emphasis-position: under right\">b</span>"
            + "<span style=\"-webkit-text-emphasis: open circle\">c</span><span style=\"text-combine-upright: all\">12</span><span>d</span></p>");

        Run[] runs = [.. document.Descendants<Run>()];
        Assert.Equal([EmphasisMark.Comma, EmphasisMark.UnderDot, EmphasisMark.Circle, null, null], runs.Select(r => r.Format.Emphasis));
        Assert.True(runs[3].Format.CombineUpright);
        Assert.Null(runs[4].Format.CombineUpright);
    }

    [Fact]
    public void RubiesAndMarksRoundTrip()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("See ");
        Ruby ruby = new(document, "とうきょう") { FontSize = 5, Alignment = RubyAlignment.DistributeSpace };
        ruby.Inlines.Add(new Run(document, "東京"));
        paragraph.Inlines.Add(ruby);
        Run marked = paragraph.AddRun("!");
        marked.Format.Emphasis = EmphasisMark.Dot;
        marked.Format.CombineUpright = true;

        string html = HtmlFormat.ToHtml(document);

        Assert.True(html.Contains("<ruby style=\"ruby-align: space-around;\">", StringComparison.Ordinal), html);
        Assert.True(html.Contains("<rp>(</rp><rt style=\"font-size: 5pt;\">とうきょう</rt><rp>)</rp></ruby>", StringComparison.Ordinal), html);
        Assert.Contains("text-emphasis: filled dot", html, StringComparison.Ordinal);
        Assert.Contains("text-combine-upright: all", html, StringComparison.Ordinal);

        Document again = HtmlFormat.Parse(html);
        Ruby read = Assert.Single(again.Descendants<Ruby>());
        Assert.Equal("とうきょう", read.Text);
        Assert.Equal("東京", read.GetText());
        Assert.Equal(RubyAlignment.DistributeSpace, read.Alignment);
        Assert.Equal(5, read.FontSize);
        Run last = again.Descendants<Run>().Last();
        Assert.Equal(EmphasisMark.Dot, last.Format.Emphasis);
        Assert.True(last.Format.CombineUpright);
        Assert.Equal("See 東京!", again.Descendants<Paragraph>().Single().GetText());
    }

    [Fact]
    public void EpubKeepsRuby()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        Ruby ruby = new(document, "かん");
        ruby.Inlines.Add(new Run(document, "漢"));
        paragraph.Inlines.Add(ruby);

        using MemoryStream stream = new();
        EpubFormat.Save(document, stream);
        stream.Position = 0;

        Ruby read = Assert.Single(EpubFormat.Load(stream).Descendants<Ruby>());
        Assert.Equal("かん", read.Text);
        Assert.Equal("漢", read.GetText());
    }
}
