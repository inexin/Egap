using System.Text;

namespace Egap.Pdf.Tests.Annotations;

/// <summary>Annotation appearances drawn by Egap (Q11, Q12): Phase 2 task 13.</summary>
public sealed class AppearanceTests
{
    private static (PdfDocument Pdf, PdfPage Page) NewPage()
    {
        PdfDocument pdf = PdfDocument.Create();
        return (pdf, pdf.Pages.AddBlank(new PdfRectangle(0, 0, 300, 300)));
    }

    private static string Content(PdfAnnotation annotation)
    {
        return Encoding.Latin1.GetString(annotation.GetAppearance()!.GetDecodedBytes());
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    public static TheoryData<string> Subtypes => ["Square", "Circle", "Line", "Polygon", "PolyLine", "Ink", "Highlight", "Underline", "Squiggly", "StrikeOut", "Caret", "Text", "FreeText", "Stamp"];

    private static PdfAnnotation Make(string subtype)
    {
        PdfRectangle box = new(50, 50, 150, 100);
        IReadOnlyList<PdfPoint> quad = PdfTextMarkupAnnotation.Quad(box);
        PdfPoint[] points = [new(50, 50), new(150, 60), new(100, 120)];
        return subtype switch
        {
            "Square" => new PdfSquareAnnotation(box) { Color = new PdfRgbColor(1, 0, 0), InteriorColor = new PdfRgbColor(0, 0, 1) },
            "Circle" => new PdfCircleAnnotation(box) { Color = new PdfRgbColor(1, 0, 0) },
            "Line" => new PdfLineAnnotation(points[0], points[1]) { Color = PdfRgbColor.Black, StartEnding = PdfLineEnding.Circle, EndEnding = PdfLineEnding.ClosedArrow },
            "Polygon" => new PdfPolygonAnnotation(points) { Color = PdfRgbColor.Black },
            "PolyLine" => new PdfPolyLineAnnotation(points) { Color = PdfRgbColor.Black, EndEnding = PdfLineEnding.OpenArrow },
            "Ink" => new PdfInkAnnotation([points]) { Color = PdfRgbColor.Black },
            "Highlight" => new PdfHighlightAnnotation(quad),
            "Underline" => new PdfUnderlineAnnotation(quad),
            "Squiggly" => new PdfSquigglyAnnotation(quad),
            "StrikeOut" => new PdfStrikeOutAnnotation(quad),
            "Caret" => new PdfCaretAnnotation(box),
            "Text" => new PdfTextAnnotation(new PdfRectangle(10, 10, 30, 30)) { Icon = "Comment" },
            "FreeText" => new PdfFreeTextAnnotation(box, "Free text that wraps onto more lines than one", "/Helv 10 Tf 0 0 1 rg"),
            "Stamp" => new PdfStampAnnotation(box, "NotApproved"),
            _ => throw new ArgumentOutOfRangeException(nameof(subtype)),
        };
    }

    [Theory]
    [MemberData(nameof(Subtypes))]
    public void AddingDrawsAnAppearanceThatSurvivesASave(string subtype)
    {
        (PdfDocument pdf, PdfPage page) = NewPage();
        using (pdf)
        {
            PdfAnnotation added = page.Annotations.Add(Make(subtype));

            PdfStream appearance = added.GetAppearance()!;
            Assert.Equal(added.Rectangle, PdfRectangle.FromArray(appearance.Dictionary.Get<PdfArray>("BBox")));
            Assert.Equal("Form", appearance.Dictionary.GetName("Subtype")?.Value);
            Assert.StartsWith("q\n", Content(added), StringComparison.Ordinal);
            Assert.EndsWith("Q\n", Content(added), StringComparison.Ordinal);

            using PdfDocument reopened = PdfDocument.Open(Save(pdf), new PdfReadOptions { Strict = true });
            Assert.Equal(Content(added), Content(reopened.Pages[0].Annotations[0]));
        }
    }

    [Fact]
    public void ShapesUseTheirColoursAndBorder()
    {
        (PdfDocument pdf, PdfPage page) = NewPage();
        using (pdf)
        {
            PdfSquareAnnotation square = page.Annotations.Add((PdfSquareAnnotation)Make("Square"));
            Assert.Equal("q\n1 w\n1 0 0 RG\n0 0 1 rg\n50.5 50.5 99 49 re\nB\nQ\n", Content(square));

            square.BorderWidth = 2;
            Assert.Equal("q\n2 w\n1 0 0 RG\n0 0 1 rg\n51 51 98 48 re\nB\nQ\n", Content(square));

            square.BorderStyle = PdfBorderStyle.Dashed;
            Assert.Contains("[3] 0 d", Content(square), StringComparison.Ordinal);

            square.InteriorColor = null;
            square.Opacity = 0.5;
            Assert.Contains("/GS0 gs", Content(square), StringComparison.Ordinal);
            Assert.EndsWith("re\nS\nQ\n", Content(square), StringComparison.Ordinal);
            Assert.Equal(0.5, square.GetAppearance()!.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("GS0")!.GetNumber("CA"));
        }
    }

    [Fact]
    public void AHighlightMultipliesItsColour()
    {
        (PdfDocument pdf, PdfPage page) = NewPage();
        using (pdf)
        {
            PdfAnnotation highlight = page.Annotations.Add(Make("Highlight"));

            Assert.Equal("Multiply", highlight.GetAppearance()!.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("GS0")!.GetName("BM")?.Value);
            Assert.Contains("1 1 0 rg", Content(highlight), StringComparison.Ordinal);
            Assert.Contains("50 50 m\n150 50 l\n150 100 l\n50 100 l\nh\nf", Content(highlight), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FreeTextIsWrappedInItsFont()
    {
        (PdfDocument pdf, PdfPage page) = NewPage();
        using (pdf)
        {
            PdfFreeTextAnnotation freeText = page.Annotations.Add((PdfFreeTextAnnotation)Make("FreeText"));
            string content = Content(freeText);

            Assert.Contains("/Helv 10 Tf", content, StringComparison.Ordinal);
            Assert.Contains("0 0 1 rg", content, StringComparison.Ordinal);
            Assert.True(content.Split('\n').Count(l => l.EndsWith(" Tj", StringComparison.Ordinal)) >= 2);
            PdfDictionary font = freeText.GetAppearance()!.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("Helv")!;
            Assert.Equal(("Helvetica", "WinAnsiEncoding"), (font.GetName("BaseFont")?.Value, font.GetName("Encoding")?.Value));

            freeText.Alignment = PdfTextAlignment.Right;
            Assert.NotEqual(content, Content(freeText));
        }
    }

    [Fact]
    public void FreeTextTheStandardFontsCannotShowKeepsItsAppearance()
    {
        (PdfDocument pdf, PdfPage page) = NewPage();
        using (pdf)
        {
            PdfFreeTextAnnotation freeText = page.Annotations.Add(new PdfFreeTextAnnotation(new PdfRectangle(0, 0, 100, 50), "Latin"));
            string before = Content(freeText);

            freeText.Contents = "\u03A9mega";

            Assert.Equal(before, Content(freeText));
            Assert.False(freeText.UpdateAppearance());
            Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.AppearanceNotDrawn);
        }
    }

    [Fact]
    public void AnAnnotationReadFromAFileIsRedrawnOnlyWhenWhatItShowsChanges()
    {
        (PdfDocument pdf, PdfPage page) = NewPage();
        using (pdf)
        {
            PdfSquareAnnotation square = page.Annotations.Add(new PdfSquareAnnotation(new PdfRectangle(0, 0, 10, 10)));
            PdfReference first = (PdfReference)square.Dictionary.Get<PdfDictionary>("AP")!["N"]!;

            square.Author = "Me";
            square.Contents = "Not shown";
            Assert.Equal(first, square.Dictionary.Get<PdfDictionary>("AP")!["N"]);

            square.Color = new PdfRgbColor(0, 1, 0);
            Assert.NotEqual(first, square.Dictionary.Get<PdfDictionary>("AP")!["N"]);
        }
    }

    [Fact]
    public void LinksAndWidgetsAreNotDrawn()
    {
        (PdfDocument pdf, PdfPage page) = NewPage();
        using (pdf)
        {
            PdfLinkAnnotation link = page.Annotations.Add(new PdfLinkAnnotation(new PdfRectangle(0, 0, 10, 10), new PdfUriAction("https://example.com")));

            Assert.Null(link.GetAppearance());
            Assert.False(link.UpdateAppearance());
            Assert.Throws<InvalidOperationException>(() => new PdfSquareAnnotation(default).UpdateAppearance());
        }
    }

    [Fact]
    public void ALineGrowsItsRectangleForItsEndings()
    {
        (PdfDocument pdf, PdfPage page) = NewPage();
        using (pdf)
        {
            PdfLineAnnotation line = page.Annotations.Add(new PdfLineAnnotation(new PdfPoint(10, 10), new PdfPoint(110, 10)) { Color = PdfRgbColor.Black });

            line.BorderWidth = 3;

            // Half the width, an ending of three widths, and 1.
            Assert.Equal(new PdfRectangle(10 - 11.5, 10 - 11.5, 110 + 11.5, 10 + 11.5), line.Rectangle);
        }
    }
}
