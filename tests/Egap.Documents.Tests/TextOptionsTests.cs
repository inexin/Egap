using Egami;
using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Word spacing, overline, OpenType features and word breaking.</summary>
public sealed class TextOptionsTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    [Fact]
    public void FeaturesParseAndCompareByValue()
    {
        FontFeatures features = FontFeatures.Parse("liga=0, tnum -kern +onum ss01=2 \"calt\"=off");

        Assert.Equal(6, features.Count);
        Assert.Equal(0, features.ValueOf("liga"));
        Assert.Equal(1, features.ValueOf("tnum"));
        Assert.Equal(0, features.ValueOf("kern"));
        Assert.Equal(2, features.ValueOf("ss01"));
        Assert.Equal(0, features.ValueOf("calt"));
        Assert.Equal("liga=0, tnum, kern=0, onum, ss01=2, calt=0", features.ToString());
        Assert.Equal(FontFeatures.Parse("liga=0, tnum"), FontFeatures.Parse("tnum=0, liga=0, tnum"));
        Assert.Equal(1, FontFeatures.Parse("liga=0").With(FontFeatures.Parse("liga")).ValueOf("liga"));
        Assert.Throws<FormatException>(() => FontFeatures.Parse("ligatures"));
        Assert.Throws<FormatException>(() => FontFeatures.Parse("liga=yes"));
    }

    [Fact]
    public void WordSpacingWidensOnlyTheSpaces()
    {
        Document plain = OneRun("one two three four", _ => { });
        Document spaced = OneRun("one two three four", f => f.WordSpacing = 10);
        Document letters = OneRun("onetwothreefour", f => f.WordSpacing = 10);
        Document lettersPlain = OneRun("onetwothreefour", _ => { });

        Assert.Equal(Width(plain) + 30, Width(spaced), 1);
        Assert.Equal(Width(lettersPlain), Width(letters), 1);
    }

    [Fact]
    public void OverlineDrawsALineAboveTheText()
    {
        using Raster<Rgba8> plain = OneRun("HHHH", f => f.Size = 30).Layout(Isolated).Pages[0].Render(72);
        using Raster<Rgba8> over = OneRun("HHHH", f => { f.Overline = true; f.Size = 30; }).Layout(Isolated).Pages[0].Render(72);

        // The overline sits above the capitals: rows that only the overlined page darkens lie above the plain page's ink.
        int Top(Raster<Rgba8> r) => Enumerable.Range(0, r.Height).First(y => Enumerable.Range(0, r.Width).Any(x => r.GetPixel(x, y).R < 128));
        int inkTop = Top(plain);
        Assert.True(Top(over) < inkTop - 2, $"no line above the text: ink from row {inkTop}, overlined from row {Top(over)}");
    }

    [Fact]
    public void BreakAllSplitsALongWord()
    {
        string word = new('W', 200);
        Assert.Equal(1, Lines(OneRun(word, _ => { })));
        Document broken = OneRun(word, _ => { });
        broken.Descendants().OfType<Paragraph>().Single().Format.WordBreak = WordBreak.BreakAll;
        Assert.True(Lines(broken) > 1);
        Document anywhere = OneRun(word, _ => { });
        anywhere.Descendants().OfType<Paragraph>().Single().Format.WordBreak = WordBreak.Anywhere;
        Assert.True(Lines(anywhere) > 1);
    }

    [Fact]
    public void DocxKeepsTheFeaturesWordHas()
    {
        Document document = OneRun("Office 2026", f => f.Features = FontFeatures.Parse("liga, clig, tnum, onum, ss03, calt=0"));

        using MemoryStream docx = new();
        DocxFormat.Save(document, docx);
        docx.Position = 0;
        Run back = DocxFormat.Load(docx).Descendants().OfType<Run>().First();

        FontFeatures features = back.Format.Features!;
        Assert.Equal(1, features.ValueOf("liga"));
        Assert.Equal(1, features.ValueOf("clig"));
        Assert.Equal(1, features.ValueOf("tnum"));
        Assert.Equal(1, features.ValueOf("onum"));
        Assert.Equal(1, features.ValueOf("ss03"));
        Assert.Equal(0, features.ValueOf("calt"));
    }

    private static Document OneRun(string text, Action<RunFormat> format)
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.PageWidth = 400;
        section.PageSetup.PageHeight = 400;
        section.PageSetup.MarginLeft = section.PageSetup.MarginRight = section.PageSetup.MarginTop = section.PageSetup.MarginBottom = 36;
        Run run = section.Body.AddParagraph().AddRun(text);
        format(run.Format);
        return document;
    }

    private static double Width(Document document)
    {
        DocumentLayout layout = document.Layout(Isolated);
        return layout.GetTextRects(document.Descendants().OfType<Run>().First()).Sum(r => r.Bounds.Width);
    }

    private static int Lines(Document document) => document.Layout(Isolated).Pages[0].BodyLines.Count();
}
