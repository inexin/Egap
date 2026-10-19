using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Path = System.IO.Path;

namespace Egap.Pdf.Tests.Redaction;

/// <summary>Redacting and replacing text by phrase.</summary>
public sealed class RedactTextTests
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "files", "fonts");

    [Fact]
    public void RedactsThePhraseAndNothingElse()
    {
        using PdfDocument pdf = Helvetica("BT /F1 12 Tf 72 700 Td (Name: Ola Nordmann, born 1980) Tj ET");
        PdfRedactAnnotation earlier = pdf.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(400, 100, 500, 120)));

        PdfRedactionResult result = pdf.RedactText("ola nordmann", new PdfFindOptions { MatchCase = false });

        Assert.Equal(1, result.Annotations);
        using PdfDocument saved = Reopen(pdf);
        string text = saved.Pages[0].ExtractText();
        Assert.DoesNotContain("Ola", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nordmann", text, StringComparison.Ordinal);
        Assert.Contains("Name:", text, StringComparison.Ordinal);
        Assert.Contains("born 1980", text, StringComparison.Ordinal);
        Assert.Single(saved.Pages[0].Annotations.OfType<PdfRedactAnnotation>());   // the earlier mark is left for its owner
        Assert.NotNull(earlier);
    }

    [Fact]
    public void MarksWithoutApplying()
    {
        using PdfDocument pdf = Helvetica("BT /F1 12 Tf 72 700 Td (ID 12345678901 and 98765432109) Tj ET");

        IReadOnlyList<PdfRedactAnnotation> marks = pdf.MarkRedactions(new Regex(@"\d{11}"));

        Assert.Equal(2, marks.Count);
        Assert.Equal(4, marks[0].QuadPoints.Count);
        Assert.Contains("12345678901", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
        pdf.ApplyRedactions();
        Assert.DoesNotContain("12345678901", Reopen(pdf).Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacesTextInTheSameFont()
    {
        using PdfDocument pdf = Helvetica("BT /F1 12 Tf 1 0 0 rg 72 700 Td (Prices for 2025, valid from 2025-01-01.) Tj ET");

        PdfReplaceResult result = pdf.ReplaceText("2025", "2026");

        Assert.Equal(2, result.Replaced.Count);
        Assert.Empty(result.Skipped);
        using PdfDocument saved = Reopen(pdf);
        string text = saved.Pages[0].ExtractText();
        Assert.DoesNotContain("2025", text, StringComparison.Ordinal);
        Assert.Equal(2, saved.FindText("2026", cancellationToken: TestContext.Current.CancellationToken).Count);
        PdfTextMatch first = saved.FindText("2026", cancellationToken: TestContext.Current.CancellationToken)[0];
        Assert.Equal(12, first.Glyphs[0].FontSize, 3);
        Assert.Equal("Helvetica", first.Glyphs[0].Font!.BaseFont);
        Assert.Equal([1.0, 0, 0], first.Glyphs[0].Color.Components);
    }

    [Fact]
    public void RegexReplacementsUseGroups()
    {
        using PdfDocument pdf = Helvetica("BT /F1 12 Tf 72 700 Td (Due 19.10.2026) Tj ET");

        pdf.ReplaceText(new Regex(@"(\d\d)\.(\d\d)\.(\d{4})"), "$3-$2-$1");

        Assert.Contains("2026-10-19", Reopen(pdf).Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvisibleTextStaysInvisible()
    {
        using PdfDocument pdf = Helvetica("BT 3 Tr /F1 12 Tf 72 700 Td (scanned 2025) Tj ET");

        pdf.ReplaceText("2025", "2026");

        using PdfDocument saved = Reopen(pdf);
        Assert.Contains("2026", saved.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.All(saved.Pages[0].GetContent().Where(o => o.Operator == "Tr"), o => Assert.Equal(3, o.GetNumber(0)));
        Assert.Equal(2, saved.Pages[0].GetContent().Count(o => o.Operator == "Tr"));
    }

    [Fact]
    public void AnEmbeddedSubsetWithoutTheGlyphsIsSkippedOrUsesTheFallback()
    {
        Typeface noto = Typeface.FromFile(Path.Combine(Fonts, "NotoSans-Latin.ttf"));
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawText("abc 111", new Vector2(72, 100), new Font(noto, 14), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        }

        using PdfDocument subset = Reopen(pdf);
        PdfReplaceResult skipped = subset.ReplaceText("111", "xyz");
        Assert.Empty(skipped.Replaced);
        PdfWarning warning = Assert.Single(skipped.Warnings);
        Assert.Equal(PdfWarningCode.TextNotReplaced, warning.Code);
        Assert.Contains("111", subset.Pages[0].ExtractText(), StringComparison.Ordinal);

        PdfReplaceResult inSubset = subset.ReplaceText("111", "cab");
        Assert.Single(inSubset.Replaced);

        PdfReplaceResult fallback = subset.ReplaceText("abc", "xyz", options: new PdfReplaceOptions { FallbackFont = noto });
        Assert.Single(fallback.Replaced);
        string text = Reopen(subset).Pages[0].ExtractText();
        Assert.Contains("xyz", text, StringComparison.Ordinal);
        Assert.Contains("cab", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACoveredImageThatCannotBeDecodedGoesWhole()
    {
        // Found by fuzzing: an image under the phrase with an unknown filter made redaction throw.
        using PdfDocument pdf = Helvetica("q 30 0 0 10 72 698 cm /Im1 Do Q BT /F1 12 Tf 72 700 Td (secret) Tj ET");
        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(2),
            ["Height"] = new PdfInteger(2),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["Filter"] = PdfName.Get("NoSuchDecode"),
        });
        image.SetEncodedBytes([1, 2, 3]);
        pdf.Pages[0].Resources!["XObject"] = new PdfDictionary { ["Im1"] = pdf.Objects.Add(image) };

        PdfRedactionResult result = pdf.RedactText("secret");

        Assert.Equal(1, result.Images);
        using PdfDocument saved = Reopen(pdf);
        Assert.Empty(saved.Pages[0].GetImages());
        Assert.DoesNotContain("secret", saved.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacesTextInAFontWrittenDirectlyInTheResources()
    {
        // Found by fuzzing: a font dictionary held directly, not by reference, could not be put in the resources again.
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary
        {
            ["Font"] = new PdfDictionary { ["F1"] = new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica"), ["Encoding"] = PdfName.Get("WinAnsiEncoding") } },
        };
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (year 2025) Tj ET")));
        using PdfDocument saved = Reopen(pdf);

        Assert.Single(saved.ReplaceText("2025", "2026").Replaced);
        Assert.Contains("2026", Reopen(saved).Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACoveredImageTooLargeToDecodeGoesWithoutBeingRead()
    {
        // Found by fuzzing: a 2 by 734 million pixel image under the phrase took half a minute to redact.
        using PdfDocument pdf = Helvetica("q 30 0 0 10 72 698 cm /Im1 Do Q BT /F1 12 Tf 72 700 Td (secret) Tj ET");
        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(2),
            ["Height"] = new PdfInteger(734_294_471),
            ["ColorSpace"] = PdfName.Get("DeviceGray"),
            ["BitsPerComponent"] = new PdfInteger(8),
        });
        image.SetEncodedBytes([1, 2, 3]);
        pdf.Pages[0].Resources!["XObject"] = new PdfDictionary { ["Im1"] = pdf.Objects.Add(image) };

        PdfRedactionResult result = pdf.RedactText("secret");

        Assert.Equal(1, result.Images);
        Assert.Empty(Reopen(pdf).Pages[0].GetImages());
    }

    private static PdfDocument Helvetica(string content)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference font = pdf.Objects.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type1"),
            ["BaseFont"] = PdfName.Get("Helvetica"),
            ["Encoding"] = PdfName.Get("WinAnsiEncoding"),
        });
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(content)));
        return Reopen(pdf);
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }
}
