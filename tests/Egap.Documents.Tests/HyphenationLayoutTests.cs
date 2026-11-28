using Egami.Fonts.Layout;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests;

/// <summary>Automatic hyphenation and line filling in layout (Hyphenation Build Plan task 12).</summary>
public class HyphenationLayoutTests
{
    private static readonly FontSettings Fonts = new() { UseSystemFonts = false };

    private const string Words = "Characteristically, international organizations communicate responsibilities "
        + "comprehensively, and representatives understand extraordinarily complicated administrative considerations "
        + "independently of institutionalized bureaucratic interpretations.";

    /// <summary>A document whose paragraphs, in <paramref name="language"/>, are a narrow column of long words.</summary>
    private static (Document Document, Paragraph First, Paragraph Second) Sample(bool hyphenate, string? language = "en-US")
    {
        Document document = new();
        document.Settings.AutoHyphenation = hyphenate;
        document.Settings.HyphenationZone = 0;
        document.Styles.DefaultRunFormat.Language = language;
        Body body = document.AddSection().Body;
        Paragraph first = body.AddParagraph(Words);
        first.Format.RightIndent = 280;
        Paragraph second = body.AddParagraph(Words);
        second.Format.RightIndent = 280;
        return (document, first, second);
    }

    private static List<LineBox> Lines(DocumentLayout layout, Paragraph paragraph)
    {
        return [.. layout.FindBoxes(paragraph).Select(b => b.Box).OfType<LineBox>()];
    }

    /// <summary>The lines that end inside a word, as only an automatic hyphen ends them.</summary>
    private static int Hyphenated(DocumentLayout layout, Paragraph paragraph)
    {
        return Lines(layout, paragraph).Count(l =>
        {
            string text = l.Content.Text.Text;
            int end = l.Line.Start + l.Line.Length;
            return end < text.Length && char.IsLetter(text[end - 1]) && char.IsLetter(text[end]);
        });
    }

    [Fact]
    public void JustifiedLinesThatShrinkIgnoreTheZone()
    {
        // Word 2013 and later hyphenate justified lines whatever room they leave; ragged lines keep to the zone.
        int Count(ParagraphAlignment alignment, bool shrink)
        {
            Document document = new();
            document.Settings.AutoHyphenation = true;
            document.Settings.HyphenationZone = 36;
            document.Settings.WordCompatibility.ShrinkJustifiedSpaces = shrink;
            document.Styles.DefaultRunFormat.Language = "en-US";
            Body body = document.AddSection().Body;
            List<Paragraph> paragraphs = [];
            for (double indent = 200; indent < 320; indent += 4)
            {
                Paragraph paragraph = body.AddParagraph(Words);
                paragraph.Format.RightIndent = indent;
                paragraph.Format.Alignment = alignment;
                paragraphs.Add(paragraph);
            }

            DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = Fonts });
            return paragraphs.Sum(p => Hyphenated(layout, p));
        }

        Assert.True(Count(ParagraphAlignment.Justify, shrink: true) > Count(ParagraphAlignment.Justify, shrink: false));
        Assert.Equal(Count(ParagraphAlignment.Left, shrink: false), Count(ParagraphAlignment.Left, shrink: true));
    }

    [Fact]
    public void WordsAreHyphenatedOnlyWhereTheSettingsAskForIt()
    {
        (Document off, Paragraph offFirst, _) = Sample(hyphenate: false);
        Assert.Equal(0, Hyphenated(off.Layout(new LayoutOptions { Fonts = Fonts }), offFirst));

        (Document on, Paragraph onFirst, Paragraph onSecond) = Sample(hyphenate: true);
        onSecond.Format.SuppressAutoHyphens = true;
        DocumentLayout layout = on.Layout(new LayoutOptions { Fonts = Fonts });
        Assert.True(Hyphenated(layout, onFirst) > 0);
        Assert.Equal(0, Hyphenated(layout, onSecond));
        Assert.Empty(layout.Warnings);
        Assert.Equal(Words, string.Concat(Lines(layout, onFirst).Select(l => l.Text)));

        // No dictionaries: only optional hyphens break words.
        DocumentLayout none = on.Layout(new LayoutOptions { Fonts = Fonts, Hyphenation = null });
        Assert.Equal(0, Hyphenated(none, onFirst));
    }

    [Fact]
    public void SettingsChangesReachTheNextLayout()
    {
        (Document document, Paragraph first, _) = Sample(hyphenate: true);
        LayoutOptions options = new() { Fonts = Fonts };
        Assert.True(Hyphenated(document.Layout(options), first) > 0);

        document.Settings.AutoHyphenation = false;
        Assert.Equal(0, Hyphenated(document.Layout(options), first));

        document.Settings.AutoHyphenation = true;
        document.Settings.ConsecutiveHyphenLimit = 1;
        List<LineBox> lines = Lines(document.Layout(options), first);
        for (int i = 1; i < lines.Count; i++)
        {
            Assert.False(EndsInWord(lines[i - 1]) && EndsInWord(lines[i]), "Two lines in a row end with a hyphen.");
        }

        static bool EndsInWord(LineBox line)
        {
            string text = line.Content.Text.Text;
            int end = line.Line.Start + line.Line.Length;
            return end < text.Length && char.IsLetter(text[end - 1]) && char.IsLetter(text[end]);
        }
    }

    [Fact]
    public void ALanguageWithoutADictionaryIsWarnedOfOnce()
    {
        (Document document, Paragraph first, _) = Sample(hyphenate: true, language: "tlh");
        List<LayoutWarning> heard = [];
        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = Fonts, OnWarning = heard.Add });

        Assert.Equal(0, Hyphenated(layout, first));
        LayoutWarning warning = Assert.Single(layout.Warnings);
        Assert.Equal(LayoutWarningKind.HyphenationDictionaryMissing, warning.Kind);
        Assert.Contains("tlh", warning.Message, StringComparison.Ordinal);
        Assert.Same(first, warning.Node);
        Assert.Equal(0, warning.PageIndex);
        Assert.Single(heard);

        // Text with no language is not hyphenated, and not warned of; nor is a language when hyphenation is off.
        Assert.Empty(Sample(hyphenate: true, language: null).Document.Layout(new LayoutOptions { Fonts = Fonts }).Warnings);
        Assert.Empty(Sample(hyphenate: false, language: "tlh").Document.Layout(new LayoutOptions { Fonts = Fonts }).Warnings);
    }

    [Fact]
    public void AtLeastThreeLettersGoToTheNextLine()
    {
        // German's patterns allow a last syllable of two letters ("Anla-ge"); Word carries three at least.
        Document document = new();
        document.Settings.AutoHyphenation = true;
        document.Settings.HyphenationZone = 0;
        document.Styles.DefaultRunFormat.Language = "de-DE";
        Body body = document.AddSection().Body;
        List<Paragraph> paragraphs = [];
        for (double indent = 250; indent < 420; indent += 3)
        {
            Paragraph paragraph = body.AddParagraph("Belüftungsanlage Sanierungsvorschläge Versicherungsbeiträge Zugluftmessanlage "
                + "Förderanträge Verwaltungsgebäude Bewässerungsanlage Lieferverträge Haushaltsbeiträge Ausstellungsgelände");
            paragraph.Format.RightIndent = indent;
            paragraphs.Add(paragraph);
        }

        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = Fonts });
        int hyphenated = 0;
        foreach (Paragraph paragraph in paragraphs)
        {
            List<LineBox> lines = Lines(layout, paragraph);
            for (int i = 0; i + 1 < lines.Count; i++)
            {
                string end = lines[i].Text, next = lines[i + 1].Text;
                if (end.Length > 0 && char.IsLetter(end[^1]) && next.Length > 0 && char.IsLetter(next[0]))
                {
                    hyphenated++;
                    Assert.True(next.TakeWhile(char.IsLetter).Count() >= 3, $"'{end}' / '{next}' carries fewer than three letters.");
                }
            }
        }

        Assert.True(hyphenated > 0);
    }

    [Fact]
    public void AnAddedDictionaryIsUsed()
    {
        HyphenationDictionaries dictionaries = new(HyphenationDictionaries.BuiltIn);
        Assert.True(HyphenationDictionaries.BuiltIn.TryGet("en-US", out Hyphenator? english));
        dictionaries.Add("tlh", english);
        (Document document, Paragraph first, _) = Sample(hyphenate: true, language: "tlh");
        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = Fonts, Hyphenation = dictionaries });
        Assert.True(Hyphenated(layout, first) > 0);
        Assert.Empty(layout.Warnings);
    }

    [Fact]
    public void OptimalFillingKeepsTheTextAndTheWidths()
    {
        (Document document, Paragraph first, _) = Sample(hyphenate: true);
        first.Format.FirstLineIndent = 36;
        first.Format.Alignment = ParagraphAlignment.Justify;
        DocumentLayout greedy = document.Layout(new LayoutOptions { Fonts = Fonts });
        DocumentLayout optimal = document.Layout(new LayoutOptions { Fonts = Fonts, LineFill = LineFill.Optimal });

        Assert.Equal(LineFill.Optimal, Lines(optimal, first)[0].Content.Text.Style.LineFill);
        Assert.Equal(LineFill.Greedy, Lines(greedy, first)[0].Content.Text.Style.LineFill);
        Assert.Equal(Words, string.Concat(Lines(optimal, first).Select(l => l.Text)));
        double column = Lines(greedy, first)[1].Bounds.Width;
        foreach (LineBox line in Lines(optimal, first))
        {
            Assert.True(line.Line.Width <= line.Bounds.Width + 0.5, $"A line is {line.Line.Width} wide in {line.Bounds.Width}.");
            Assert.True(line.Bounds.Width <= column + 0.5);
        }
    }

    [Fact]
    public void PdfTextReadsAHyphenatedWordWhole()
    {
        (Document document, Paragraph first, _) = Sample(hyphenate: true);
        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = Fonts });
        Assert.True(Hyphenated(layout, first) > 0);

        using MemoryStream stream = new();
        document.SaveAsPdf(stream, new PdfExportOptions { Layout = new LayoutOptions { Fonts = Fonts } });
        using PdfDocument pdf = PdfDocument.Open(stream.ToArray());
        string text = pdf.Pages[0].ExtractText();

        // Each automatic hyphen reads as a soft hyphen, which joins the word's two parts; no hyphen-minus shows in the text.
        Assert.Contains("­", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-", text, StringComparison.Ordinal);
        string joined = text.Replace("­\r\n", "", StringComparison.Ordinal).Replace("­\n", "", StringComparison.Ordinal);
        foreach (string word in Words.Split(' '))
        {
            Assert.Contains(word, joined, StringComparison.Ordinal);
        }
    }
}
