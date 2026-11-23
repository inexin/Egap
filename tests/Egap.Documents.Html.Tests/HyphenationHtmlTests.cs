using Egami.Fonts.Layout;

using Egap.Documents.Layout;

using Paragraph = Egap.Documents.Paragraph;

namespace Egap.Documents.Html.Tests;

/// <summary>CSS hyphenation and text-wrap in HTML (Hyphenation Build Plan task 13).</summary>
public sealed class HyphenationHtmlTests
{
    private static readonly FontSettings Isolated = new() { UseSystemFonts = false };

    private static Paragraph[] Paragraphs(Document document) => [.. document.Descendants().OfType<Paragraph>()];

    [Fact]
    public void HyphensAutoTurnsHyphenationOnWhereItIsAsked()
    {
        Document html = HtmlFormat.Parse("""
            <html lang="en-US"><body>
            <div style="hyphens: auto"><p>One</p><p style="hyphens: manual">Two</p></div>
            <p>Three</p>
            <p style="-webkit-hyphens: auto">Four</p>
            <p style="hyphens: none">Five</p>
            </body></html>
            """);
        Paragraph[] paragraphs = Paragraphs(html);

        Assert.True(html.Settings.AutoHyphenation);
        Assert.Equal(0, html.Settings.HyphenationZone);
        Assert.Equal([false, true, true, false, true], paragraphs.Select(p => p.Format.SuppressAutoHyphens!.Value));
    }

    [Fact]
    public void WithoutHyphensAutoNothingChanges()
    {
        Document html = HtmlFormat.Parse("<p>One</p><p style='hyphens: manual'>Two</p>");
        Assert.False(html.Settings.AutoHyphenation);
        Assert.All(Paragraphs(html), p => Assert.Null(p.Format.SuppressAutoHyphens));
        Assert.DoesNotContain("hyphens", HtmlFormat.ToHtml(html, out _), StringComparison.Ordinal);
    }

    [Fact]
    public void HyphenateCharacterAndLimitsAreRead()
    {
        Document html = HtmlFormat.Parse("""
            <div style="hyphenate-character: '\2010'; hyphenate-limit-chars: 6 3 2">
              <p>One</p>
              <p style="hyphenate-limit-chars: 8 4; hyphenate-character: '='">Two</p>
              <p style="hyphenate-character: auto; hyphenate-limit-chars: auto">Three</p>
            </div>
            <p style="-webkit-hyphenate-character: '+'">Four</p>
            """);
        Paragraph[] paragraphs = Paragraphs(html);

        Assert.Equal("‐", paragraphs[0].Format.HyphenCharacter);
        Assert.Equal((6, 3, 2), (paragraphs[0].Format.HyphenationMinWordLength, paragraphs[0].Format.HyphenationMinBefore, paragraphs[0].Format.HyphenationMinAfter));
        Assert.Equal("=", paragraphs[1].Format.HyphenCharacter);
        Assert.Equal((8, 4, 4), (paragraphs[1].Format.HyphenationMinWordLength, paragraphs[1].Format.HyphenationMinBefore, paragraphs[1].Format.HyphenationMinAfter));
        Assert.Null(paragraphs[2].Format.HyphenCharacter);
        Assert.Null(paragraphs[2].Format.HyphenationMinWordLength);
        Assert.Null(paragraphs[2].Format.HyphenationMinAfter);
        Assert.Equal("+", paragraphs[3].Format.HyphenCharacter);
    }

    [Fact]
    public void TextWrapPrettyIsOptimalLineFillForTheParagraph()
    {
        Document html = HtmlFormat.Parse("""
            <p style="text-wrap: pretty">One</p>
            <h1 style="text-wrap: balance">Two</h1>
            <p style="text-wrap-style: pretty">Three</p>
            <p style="text-wrap: wrap">Four</p>
            """);
        Paragraph[] paragraphs = Paragraphs(html);

        Assert.Equal([LineFill.Optimal, LineFill.Balanced, LineFill.Optimal, null], paragraphs.Select(p => p.Format.LineFill));
    }

    [Fact]
    public void TheWriterWritesWhatTheReaderReads()
    {
        Document html = HtmlFormat.Parse("""
            <div style="hyphens: auto"><p style="text-wrap: pretty; hyphenate-character: '\2010'; hyphenate-limit-chars: 6 auto 3">One</p></div>
            <p>Two</p>
            """);
        string written = HtmlFormat.ToHtml(html, out _);
        Assert.Contains("hyphens: auto", written, StringComparison.Ordinal);
        Assert.Contains("hyphens: manual", written, StringComparison.Ordinal);
        Assert.Contains("text-wrap: pretty", written, StringComparison.Ordinal);
        Assert.Contains("hyphenate-limit-chars: 6 auto 3", written, StringComparison.Ordinal);

        Document again = HtmlFormat.Parse(written);
        Paragraph[] paragraphs = Paragraphs(again);
        Assert.True(again.Settings.AutoHyphenation);
        Assert.Equal([false, true], paragraphs.Select(p => p.Format.SuppressAutoHyphens!.Value));
        Assert.Equal(LineFill.Optimal, paragraphs[0].Format.LineFill);
        Assert.Equal("‐", paragraphs[0].Format.HyphenCharacter);
        Assert.Equal((6, null, 3), (paragraphs[0].Format.HyphenationMinWordLength, paragraphs[0].Format.HyphenationMinBefore, paragraphs[0].Format.HyphenationMinAfter));
    }

    [Fact]
    public void LayoutHyphenatesWithTheParagraphsCharacterAndFill()
    {
        const string words = "Characteristically, international organizations communicate responsibilities comprehensively, "
            + "and representatives understand extraordinarily complicated administrative considerations independently.";
        Document html = HtmlFormat.Parse($"""
            <html lang="en-US"><body style="hyphens: auto">
            <p style="margin-right: 280pt; hyphenate-character: '='">{words}</p>
            <p style="margin-right: 280pt; text-wrap: pretty; text-align: justify">{words}</p>
            <p style="margin-right: 280pt; hyphens: manual">{words}</p>
            </body></html>
            """);
        Paragraph[] paragraphs = Paragraphs(html);
        DocumentLayout layout = html.Layout(new LayoutOptions { Fonts = Isolated });

        List<LineBox> first = Lines(layout, paragraphs[0]);
        Assert.Contains(first, l => EndsInWord(l));
        Assert.Equal("=", first[0].Content.Text.Style.Hyphen);
        Assert.Equal(LineFill.Optimal, Lines(layout, paragraphs[1])[0].Content.Text.Style.LineFill);
        Assert.Equal(LineFill.Greedy, first[0].Content.Text.Style.LineFill);
        Assert.DoesNotContain(Lines(layout, paragraphs[2]), l => EndsInWord(l));

        static List<LineBox> Lines(DocumentLayout layout, Paragraph paragraph) => [.. layout.FindBoxes(paragraph).Select(b => b.Box).OfType<LineBox>()];

        static bool EndsInWord(LineBox line)
        {
            string text = line.Content.Text.Text;
            int end = line.Line.Start + line.Line.Length;
            return end < text.Length && char.IsLetter(text[end - 1]) && char.IsLetter(text[end]);
        }
    }
}
