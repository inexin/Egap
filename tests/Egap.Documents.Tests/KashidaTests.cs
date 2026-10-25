using Egami.Fonts;
using Egami.Fonts.Layout;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

using KashidaLevel = Egap.Documents.KashidaLevel;

namespace Egap.Documents.Tests;

/// <summary>Kashida justification in Egap (Egami 2.8.3 Build Plan task 12): Word's kashida alignments and PDF text.</summary>
public class KashidaTests
{
    private const char Tatweel = 'ـ';

    private static readonly string Arabic = string.Join(' ', Enumerable.Repeat("تعلم اللغة العربية يفتح أبواب الأدب والعلوم والتاريخ للطلاب في كل مكان", 6));

    private static LayoutOptions Options()
    {
        FontCollection fonts = new();
        fonts.AddFile(Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSansArabic-Regular.ttf"));
        return new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false, Collections = [fonts] } };
    }

    private static Document ArabicDocument(KashidaLevel? kashida)
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.Format.Bidi = true;
        paragraph.Format.Alignment = ParagraphAlignment.Justify;
        paragraph.Format.Kashida = kashida;
        Run run = paragraph.AddRun(Arabic);
        run.Format.FontName = "Noto Sans Arabic";
        run.Format.FontComplexScript = "Noto Sans Arabic";
        return document;
    }

    /// <summary>The tatweel glyphs layout put in: glyphs of U+0640 whose character is not one.</summary>
    private static int Inserted(DocumentLayout layout)
    {
        int count = 0;
        foreach (LineBox line in layout.Pages.SelectMany(page => page.BodyLines))
        {
            foreach (LayoutRun run in line.Line.Runs)
            {
                if (run.Glyphs is not GlyphRun glyphs)
                {
                    continue;
                }

                ushort tatweel = glyphs.Font.Typeface.GetGlyph(Tatweel);
                for (int g = 0; g < glyphs.Count; g++)
                {
                    if (glyphs.Glyphs[g] == tatweel && glyphs.Clusters[g] < Arabic.Length && Arabic[glyphs.Clusters[g]] != Tatweel)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    [Theory]
    [InlineData("lowKashida", KashidaLevel.Low)]
    [InlineData("mediumKashida", KashidaLevel.Medium)]
    [InlineData("highKashida", KashidaLevel.High)]
    [InlineData("both", KashidaLevel.None)]
    public void DocxKashidaAlignmentsReadAndWrite(string jc, KashidaLevel level)
    {
        Document document = ArabicDocument(level);
        using MemoryStream docx = new();
        document.SaveAsDocx(docx);
        Assert.Contains($"w:val=\"{jc}\"", DocxPart(docx.ToArray(), "word/document.xml"), StringComparison.Ordinal);

        Paragraph read = Document.Load(new MemoryStream(docx.ToArray())).Descendants<Paragraph>().Single();
        Assert.Equal(ParagraphAlignment.Justify, read.Format.Alignment);
        Assert.Equal(level, read.Format.Kashida);
        Assert.Equal(level, read.GetEffectiveFormat().Kashida);
    }

    [Fact]
    public void AParagraphsPlainJustifyTurnsOffItsStylesKashida()
    {
        Document document = ArabicDocument(null);
        ParagraphStyle style = document.Styles.AddParagraphStyle("ArabicBody");
        style.ParagraphFormat.Alignment = ParagraphAlignment.Justify;
        style.ParagraphFormat.Kashida = KashidaLevel.High;
        Paragraph paragraph = document.Descendants<Paragraph>().Single();
        paragraph.Style = style;
        Assert.Equal(KashidaLevel.High, paragraph.GetEffectiveFormat().Kashida);

        paragraph.Format.Kashida = KashidaLevel.None;
        Assert.Equal(KashidaLevel.None, paragraph.GetEffectiveFormat().Kashida);
    }

    [Fact]
    public void KashidaParagraphsStretchWordsAndOthersDoNot()
    {
        int none = Inserted(ArabicDocument(KashidaLevel.None).Layout(Options()));
        int low = Inserted(ArabicDocument(KashidaLevel.Low).Layout(Options()));
        int high = Inserted(ArabicDocument(KashidaLevel.High).Layout(Options()));

        Assert.Equal(0, none);
        Assert.True(low > 0, "low kashida inserts tatweels");
        Assert.True(high >= low, $"high {high}, low {low}");

        // Every justified line still fills the column.
        DocumentLayout layout = ArabicDocument(KashidaLevel.High).Layout(Options());
        List<LineBox> lines = [.. layout.Pages[0].BodyLines];
        Assert.True(lines.Count > 2);
        foreach (LineBox line in lines.SkipLast(1))
        {
            Assert.Equal(lines[0].Line.Width, line.Line.Width, 1);
        }
    }

    [Fact]
    public void PdfTextHasTheWordsWithoutTheInsertedTatweels()
    {
        using MemoryStream stream = new();
        ArabicDocument(KashidaLevel.High).SaveAsPdf(stream, new PdfExportOptions { Layout = Options() });
        using PdfDocument pdf = PdfDocument.Open(stream.ToArray());

        string text = string.Join(' ', pdf.Pages[0].ExtractText().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain(Tatweel, text);
        Assert.Equal(Arabic.Split(' ').Order(StringComparer.Ordinal), text.Split(' ').Order(StringComparer.Ordinal));
    }

    private static string DocxPart(byte[] docx, string name)
    {
        using System.IO.Compression.ZipArchive zip = new(new MemoryStream(docx));
        using StreamReader reader = new(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
