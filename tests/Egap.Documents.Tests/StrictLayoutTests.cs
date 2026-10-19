using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Strict layout and portable fonts.</summary>
public sealed class StrictLayoutTests
{
    private static readonly LayoutOptions Strict = new() { Strict = true, Fonts = FontSettings.Portable };

    [Fact]
    public void AWordTooLongForTheColumnFailsNamingItsParagraph()
    {
        Document document = Page(out Section section);
        Paragraph paragraph = section.Body.AddParagraph(new string('W', 120));

        LayoutException e = Assert.Throws<LayoutException>(() => document.Layout(Strict));

        Assert.Equal(LayoutProblem.TextOverflow, e.Problem);
        Assert.Same(paragraph, e.Node);
        Assert.Equal(0, e.PageIndex);
        Assert.Contains("WWWW", e.Message, StringComparison.Ordinal);

        // Not strict, the same document lays out, the word running past the column as before.
        Assert.Single(document.Layout(new LayoutOptions { Fonts = FontSettings.Portable }).Pages);

        // Letting words break makes it fit.
        paragraph.Format.WordBreak = WordBreak.Anywhere;
        Assert.NotEmpty(document.Layout(Strict).Pages);
    }

    [Fact]
    public void AnUnknownFontWithNoStandInFails()
    {
        Document document = Page(out Section section);
        Run run = section.Body.AddParagraph().AddRun("Hello");
        run.Format.FontAscii = "No Such Font 2026";

        LayoutException e = Assert.Throws<LayoutException>(() => document.Layout(Strict));

        Assert.Equal(LayoutProblem.MissingFont, e.Problem);
        Assert.Contains("No Such Font 2026", e.Message, StringComparison.Ordinal);
        Assert.Same(run.Ancestors().OfType<Paragraph>().First(), e.Node);
    }

    [Fact]
    public void AFontWithAStandardStandInIsFine()
    {
        Document document = Page(out Section section);
        section.Body.AddParagraph().AddRun("Calibri text").Format.FontAscii = "Calibri";

        Assert.Single(document.Layout(Strict).Pages);
    }

    [Fact]
    public void ACharacterNoFontHasFails()
    {
        Document document = Page(out Section section);
        section.Body.AddParagraph("Linear B: \U00010000");

        LayoutException e = Assert.Throws<LayoutException>(() => document.Layout(Strict));

        Assert.Equal(LayoutProblem.MissingGlyph, e.Problem);
        Assert.Contains("U+10000", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATableWiderThanTheBodyFails()
    {
        Document document = Page(out Section section);
        Table table = section.Body.AddTable(1, 2);
        foreach (Row row in table.Rows)
        {
            foreach (Cell cell in row.Cells)
            {
                cell.Format.Width = TableWidth.Points(400);
                cell.AddParagraph("x");
            }
        }

        table.Format.Layout = TableLayoutKind.Fixed;
        table.Format.Width = TableWidth.Points(800);

        LayoutException e = Assert.Throws<LayoutException>(() => document.Layout(Strict));

        Assert.Equal(LayoutProblem.ContentOverflow, e.Problem);
        Assert.True(e.Node is Table or Row or Paragraph, e.Node?.GetType().Name);
    }

    private static Document Page(out Section section)
    {
        Document document = new();
        section = document.AddSection();
        section.PageSetup.PageWidth = 400;
        section.PageSetup.PageHeight = 400;
        section.PageSetup.MarginLeft = section.PageSetup.MarginRight = section.PageSetup.MarginTop = section.PageSetup.MarginBottom = 36;
        return document;
    }
}
