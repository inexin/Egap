using Egap.Documents.Html;
using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>Equations in HTML (Build Plan tasks 17 and 18): MathML read into equation islands, and equations written as MathML.</summary>
public sealed class MathHtmlTests
{
    private const string Omml = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    [Fact]
    public void MathElementsBecomeEquations()
    {
        Document document = HtmlFormat.Parse("""
            <p>Inline <math><mfrac><mi>a</mi><mi>b</mi></mfrac></math> text.</p>
            <math display="block"><msup><mi>x</mi><mn>2</mn></msup></math>
            """);
        Island[] islands = [.. document.Sections.SelectMany(s => s.Body.Paragraphs).SelectMany(p => p.Inlines.OfType<Island>())];

        Assert.Equal(2, islands.Length);
        Assert.All(islands, i => Assert.Equal(IslandKind.Math, i.Kind));
        Assert.Equal("a/b", islands[0].Text);
        Assert.Equal("x^2", islands[1].Text);
        Assert.IsType<OfficeMath.MathZone>(OfficeMath.MathIslands.Tree(islands[0]));
        Assert.IsType<OfficeMath.MathParagraph>(OfficeMath.MathIslands.Tree(islands[1]));
        Assert.Contains("a/b", document.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void MathMLEquationsLayOutAsMath()
    {
        Document document = HtmlFormat.Parse("<p>x <math><mfrac><mrow><mi>a</mi><mo>+</mo><mi>b</mi></mrow><mn>2</mn></mfrac></math> y</p>");
        PageLayout page = document.Layout(new LayoutOptions { Fonts = FontSettings.Portable }).Pages[0];
        ObjectBox equation = page.Boxes.SelectMany(b => (IEnumerable<LayoutBox>)[b, .. b.Children]).OfType<ParagraphBox>()
            .SelectMany(p => p.Children).OfType<ObjectBox>().Single();

        // A fraction stands taller than a line of text.
        Assert.True(equation.Bounds.Height > 15, $"{equation.Bounds}");
    }

    [Fact]
    public void EquationsAreWrittenAsMathML()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Area ");
        paragraph.Inlines.Add(new Island(document, IslandKind.Math, "docx",
            $"<m:oMath xmlns:m=\"{Omml}\"><m:sSup><m:e><m:r><m:t>r</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath>"));

        string html = HtmlFormat.ToHtml(document, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Contains("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><msup><mi>r</mi><mn>2</mn></msup></math>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.DrawingDropped);

        // Read back, it is the same equation.
        Island back = HtmlFormat.Parse(html).Sections[0].Body.Paragraphs.SelectMany(p => p.Inlines.OfType<Island>()).Single();
        Assert.Equal("r^2", back.Text);
    }

    [Fact]
    public void OddNamesAndCharactersInMathAreLeftOut()
    {
        Document document = HtmlFormat.Parse("<p><math><mi title=\"ab\">x</mi><m<i>y</m<i><mn>2</mn></math></p>");
        Island island = document.Sections[0].Body.Paragraphs.SelectMany(p => p.Inlines.OfType<Island>()).Single();
        Assert.Contains("<mn>2</mn>", island.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("", island.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlEquationsRoundTripAsTheirMarkup()
    {
        Document document = HtmlFormat.Parse("<p><math><mi>y</mi><mo>=</mo><mi>k</mi><mi>x</mi></math></p>");
        string html = document.ToHtml();
        Assert.Contains("<mi>k</mi>", html, StringComparison.Ordinal);
    }
}
