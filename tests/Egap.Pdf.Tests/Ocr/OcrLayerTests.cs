using System.Text;

namespace Egap.Pdf.Tests.Ocr;

/// <summary>The OCR text layer (Phase 5 task 20): hOCR and ALTO read, words drawn invisibly where OCR found them.</summary>
public sealed class OcrLayerTests
{
    /// <summary>Tesseract-style hOCR for a 1700 by 2200 pixel scan (a letter page at 200 dpi): two lines.</summary>
    private const string Hocr = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">
        <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="en" lang="en">
         <head><title></title><meta name="ocr-system" content="tesseract 5.3.0" /></head>
         <body>
          <div class='ocr_page' id='page_1' title='image "scan.png"; bbox 0 0 1700 2200; ppageno 0'>
           <div class='ocr_carea' id='block_1_1' title="bbox 200 200 1500 400">
            <p class='ocr_par' id='par_1_1' lang='eng' title="bbox 200 200 1500 400">
             <span class='ocr_line' id='line_1_1' title="bbox 200 200 1500 260; baseline 0 -10; x_size 60">
              <span class='ocrx_word' id='word_1_1' title='bbox 200 200 520 260; x_wconf 96'>Quarterly</span>
              <span class='ocrx_word' id='word_1_2' title='bbox 550 200 800 260; x_wconf 95'>report</span>
             </span>
             <span class='ocr_line' id='line_1_2' title="bbox 200 300 1500 350">
              <span class='ocrx_word' id='word_1_3' title='bbox 200 300 400 350; x_wconf 93'>Sales</span>
              <span class='ocrx_word' id='word_1_4' title='bbox 420 300 620 350; x_wconf 91'>&amp; costs</span>
             </span>
            </p>
           </div>
          </div>
         </body>
        </html>
        """;

    /// <summary>ALTO v4 for a 2480 by 3508 pixel scan (A4 at 300 dpi): one line.</summary>
    private const string Alto = """
        <?xml version="1.0" encoding="UTF-8"?>
        <alto xmlns="http://www.loc.gov/standards/alto/ns-v4#">
          <Description><MeasurementUnit>pixel</MeasurementUnit></Description>
          <Layout>
            <Page ID="p1" WIDTH="2480" HEIGHT="3508" PHYSICAL_IMG_NR="1">
              <PrintSpace>
                <TextBlock ID="b1">
                  <TextLine ID="l1" HPOS="300" VPOS="400" WIDTH="1200" HEIGHT="80" LANG="nb">
                    <String ID="s1" CONTENT="Årsrapport" HPOS="300" VPOS="400" WIDTH="600" HEIGHT="80"/>
                    <SP WIDTH="40"/>
                    <String ID="s2" CONTENT="2026" HPOS="940" VPOS="400" WIDTH="300" HEIGHT="80"/>
                  </TextLine>
                </TextBlock>
              </PrintSpace>
            </Page>
          </Layout>
        </alto>
        """;

    private static PdfDocument Scan(PdfRectangle box)
    {
        PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank(box);
        return pdf;
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void HocrWordsAreSearchableWhereTheScanShowsThem()
    {
        using PdfDocument pdf = Scan(new PdfRectangle(0, 0, 612, 792));
        Assert.Equal(4, PdfOcrLayer.AddHocr(pdf.Pages[0], Bytes(Hocr)));
        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument back = PdfDocument.Open(saved.ToArray());
        string text = back.Pages[0].ExtractText();
        Assert.Contains("Quarterly report", text, StringComparison.Ordinal);
        Assert.Contains("Sales & costs", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Quarterly", StringComparison.Ordinal) < text.IndexOf("Sales", StringComparison.Ordinal));

        // "Quarterly" spans pixels 200 to 520 across 1700: 72 to 187.2 points; its top is 200 px down: 720 points up.
        PdfTextGlyph q = back.Pages[0].GetText().Glyphs.First(g => g.Text == "Q");
        Assert.Equal(72, q.Box.Left, 1);
        Assert.Equal(792 - (200 * 792.0 / 2200), q.Box.Top, 1);
        PdfTextGlyph y = back.Pages[0].GetText().Glyphs.First(g => g.Text == "y");
        Assert.True(Math.Abs(y.Box.Right - (520 * 612.0 / 1700)) < 1, $"right edge {y.Box.Right}");

        // Invisible: render mode 3, and nothing on the rendered page.
        string content = Encoding.Latin1.GetString(back.Pages[0].GetContent().ToBytes());
        Assert.Contains("3 Tr", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AltoWordsAreRead()
    {
        IReadOnlyList<PdfOcrLine> lines = PdfOcrLayer.ReadAlto(Bytes(Alto), new PdfRectangle(0, 0, 595, 842));
        PdfOcrLine line = Assert.Single(lines);
        Assert.Equal(["Årsrapport", "2026"], line.Words.Select(w => w.Text));
        Assert.Equal("nb", line.Words[0].Language);
        Assert.Equal(300 * 595.0 / 2480, line.Words[0].Box.Left, 3);
        Assert.Equal(842 - (480 * 842.0 / 3508), line.Words[0].Box.Bottom, 3);

        using PdfDocument pdf = Scan(new PdfRectangle(0, 0, 595, 842));
        PdfOcrLayer.AddAlto(pdf.Pages[0], Bytes(Alto));
        Assert.Contains("Årsrapport 2026", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASearchablePdfAIsOnePass()
    {
        using PdfDocument pdf = Scan(new PdfRectangle(0, 0, 612, 792));
        PdfOcrLayer.AddHocr(pdf.Pages[0], Bytes(Hocr));
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2u);
        Assert.True(report.Succeeded, report.ToString());
    }

    [Fact]
    public void BrokenFilesAreRefused()
    {
        using PdfDocument pdf = Scan(new PdfRectangle(0, 0, 612, 792));
        Assert.Throws<PdfException>(() => PdfOcrLayer.AddHocr(pdf.Pages[0], Bytes("<html><body><p>no page</p></body></html>")));
        Assert.Throws<PdfException>(() => PdfOcrLayer.AddHocr(pdf.Pages[0], Bytes("<html><body")));
        Assert.Throws<PdfException>(() => PdfOcrLayer.AddAlto(pdf.Pages[0], Bytes("<alto><Layout/></alto>")));
    }
}
