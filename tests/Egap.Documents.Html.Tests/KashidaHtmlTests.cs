namespace Egap.Documents.Html.Tests;

/// <summary>The HTML reader's kashida opt-in (Egami 2.8.3 Build Plan task 12): CSS has no kashida value.</summary>
public sealed class KashidaHtmlTests
{
    [Fact]
    public void JustifiedParagraphsGetTheLevelWhenAskedFor()
    {
        const string html = "<p style=\"text-align: justify\" dir=\"rtl\">نص عربي</p><p dir=\"rtl\">نص</p>";

        Paragraph[] off = [.. HtmlFormat.Parse(html).Descendants<Paragraph>()];
        Paragraph[] on = [.. HtmlFormat.Parse(html, new HtmlLoadOptions { Kashida = KashidaLevel.Medium }).Descendants<Paragraph>()];

        Assert.Equal(KashidaLevel.None, off[0].GetEffectiveFormat().Kashida);
        Assert.Equal(ParagraphAlignment.Justify, on[0].GetEffectiveFormat().Alignment);
        Assert.Equal(KashidaLevel.Medium, on[0].GetEffectiveFormat().Kashida);
        Assert.Equal(KashidaLevel.None, on[1].GetEffectiveFormat().Kashida);
    }
}
