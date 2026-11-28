using Egami.Fonts;
using Egami.Fonts.Unicode;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>
/// Thai and Lao, written without spaces between words, break into lines between dictionary words (Egami.Fonts 1.6.0's
/// dictionary line breaking), not only at spaces or where a line overflows.
/// </summary>
public class DictionaryLineBreakTests
{
    // "Thai is a language written together, without spaces between words", and Lao "Lao is a language written together".
    private const string Thai = "ภาษาไทยเป็นภาษาที่เขียนติดกันโดยไม่เว้นวรรคระหว่างคำ";
    private const string Lao = "ພາສາລາວເປັນພາສາທີ່ຂຽນຕິດກັນ";

    private static LayoutOptions Options(string file)
    {
        FontCollection fonts = new();
        fonts.AddFile(Path.Combine(AppContext.BaseDirectory, "files", "fonts", file));
        return new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false, Collections = [fonts] } };
    }

    /// <summary>A narrow paragraph of <paramref name="text"/> repeated with no space, saved as DOCX and read back.</summary>
    private static (Document Document, string Text) Sample(string text, string font)
    {
        string all = string.Concat(Enumerable.Repeat(text, 6));
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.Format.RightIndent = 300;
        Run run = paragraph.AddRun(all);
        run.Format.FontName = font;
        run.Format.FontComplexScript = font;
        using MemoryStream docx = new();
        document.SaveAsDocx(docx);
        return (Document.Load(new MemoryStream(docx.ToArray())), all);
    }

    [Theory]
    [InlineData(Thai, "Noto Sans Thai", "NotoSansThai-Regular.ttf")]
    [InlineData(Lao, "Noto Sans Lao", "NotoSansLao-Regular.ttf")]
    public void TextWithoutSpacesBreaksBetweenWords(string text, string font, string file)
    {
        (Document document, string all) = Sample(text, font);
        Paragraph paragraph = document.Descendants<Paragraph>().Single();
        DocumentLayout layout = document.Layout(Options(file));
        List<LineBox> lines = [.. layout.FindBoxes(paragraph).Select(b => b.Box).OfType<LineBox>()];

        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        Assert.Equal(all, string.Concat(lines.Select(l => l.Text)));

        HashSet<int> words = [];
        foreach (TextSegment word in TextSegmenter.Words(all, null))
        {
            words.Add(word.Start + word.Length);
        }

        // Each line ends where a dictionary word ends, inside the run of letters: no space is there to break at.
        foreach (LineBox line in lines.SkipLast(1))
        {
            int end = line.Line.Start + line.Line.Length;
            Assert.Contains(end, words);
            Assert.False(char.IsWhiteSpace(all[end - 1]));
        }
    }
}
