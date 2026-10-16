using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Tagging;

/// <summary>Tagging while drawing, PDF/UA basics and PDF/A level a (Phase 5 task 18).</summary>
public sealed class TaggingTests
{
    private static readonly Typeface Noto = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));
    private static readonly SolidPaint Black = new(new RgbaF32(0, 0, 0, 1));

    /// <summary>A tagged page: a heading, a paragraph, a figure with alternate text, a page number as an artifact and a tagged link.</summary>
    internal static PdfDocument Accessible(PdfCreateOptions options, bool tagEverything = true, string? alt = "A red square")
    {
        PdfDocument pdf = PdfDocument.Create(options);
        pdf.Metadata.Title = "Accessible report";
        PdfStructureElement document = pdf.Structure.Add(PdfStructureType.Document);
        document.Language = "en-GB";
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        using (PdfCanvas canvas = new(page))
        {
            canvas.BeginTag(document.Add(PdfStructureType.H1));
            canvas.DrawText("Annual report", new Vector2(20, 40), new Font(Noto, 18), Black);
            canvas.EndTag();

            canvas.BeginTag(document.Add(PdfStructureType.P));
            canvas.DrawText("Everything went well.", new Vector2(20, 80), new Font(Noto, 11), Black);
            canvas.EndTag();

            PdfStructureElement figure = document.Add(PdfStructureType.Figure);
            figure.AlternateText = alt;
            canvas.BeginTag(figure);
            using (Raster<Rgb8> image = Raster.Create<Rgb8>(4, 4, new Rgb8(220, 0, 0)))
            {
                canvas.DrawImage(image, new RectF(20, 100, 40, 40));
            }

            canvas.EndTag();

            if (tagEverything)
            {
                canvas.BeginArtifact();
            }

            canvas.DrawText("1", new Vector2(190, 380), new Font(Noto, 9), Black);
            if (tagEverything)
            {
                canvas.EndTag();
            }
        }

        PdfLinkAnnotation link = new(new PdfRectangle(20, 300, 120, 320), new PdfUriAction("https://example.org")) { Contents = "Example site" };
        page.Annotations.Add(link);
        document.Add(PdfStructureType.Link).AddAnnotation(page, link);
        return pdf;
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void DrawingIsTaggedWithMarkedContentIds()
    {
        using PdfDocument pdf = Accessible(PdfCreateOptions.Default);
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        List<PdfOperation> marks = [.. saved.Pages[0].GetContent().Where(o => o.Operator is "BDC" or "BMC")];
        Assert.Equal(["H1", "P", "Figure"], marks.Where(o => o.Operator == "BDC" && o.Operands[1].Resolve() is PdfDictionary d && d.GetInt("MCID") is not null).Select(o => o.GetName(0)!.Value));
        Assert.Equal([0L, 1L, 2L], marks.Select(o => o.Operands.Count > 1 && o.Operands[1].Resolve() is PdfDictionary d ? d.GetInt("MCID") : null).OfType<long>());
        Assert.Contains(marks, o => o.Operator == "BMC" && o.GetName(0)?.Value == "Artifact");
        PdfStructureElement figure = saved.Structure.FindMarkedContent(saved.Pages[0], 2)!;
        Assert.Equal("A red square", figure.AlternateText);
        Assert.Equal(PdfStructureType.H1, saved.Structure.FindMarkedContent(saved.Pages[0], 0)!.Type);
    }

    [Fact]
    public void ATaggedDocumentPassesTheAccessibilityCheck()
    {
        using PdfDocument pdf = Accessible(new PdfCreateOptions { UniversalAccessibility = true });
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        PdfAReport report = PdfAConverter.CheckUniversalAccessibility(saved);
        Assert.True(report.Succeeded, report.ToString());
        Assert.Equal("en-GB", saved.Catalog.GetText("Lang"));
        Assert.True(saved.Catalog.Get<PdfDictionary>("ViewerPreferences")!.GetBool("DisplayDocTitle"));
        Assert.Equal("S", saved.Pages[0].Dictionary.GetName("Tabs")?.Value);
    }

    [Fact]
    public void UntaggedContentIsRefused()
    {
        using PdfDocument pdf = Accessible(new PdfCreateOptions { UniversalAccessibility = true }, tagEverything: false);
        PdfException e = Assert.Throws<PdfException>(() => Save(pdf));
        Assert.Contains("PDF/UA-1", e.Message, StringComparison.Ordinal);
        Assert.Contains("neither tagged nor marked as artifacts", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFigureNeedsAlternateText()
    {
        using PdfDocument pdf = Accessible(new PdfCreateOptions { UniversalAccessibility = true }, alt: null);
        Assert.Contains("alternate text", Assert.Throws<PdfException>(() => Save(pdf)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2a)]
    [InlineData(PdfAConformance.PdfA3a)]
    public void LevelAIsWrittenForTaggedDocuments(PdfAConformance conformance)
    {
        using PdfDocument pdf = Accessible(new PdfCreateOptions { Conformance = conformance, UniversalAccessibility = true });
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        Assert.Equal(conformance, saved.Conformance);
        Assert.True(PdfAConverter.Check(saved, conformance).Succeeded);
        Assert.True(PdfAConverter.CheckUniversalAccessibility(saved).Succeeded);
        Assert.Contains("pdfuaid", saved.Metadata.GetXmp()!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LevelANeedsATree()
    {
        using PdfDocument pdf = PdfDocument.Create(new PdfCreateOptions { Conformance = PdfAConformance.PdfA2a });
        pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        Assert.Contains("tagged", Assert.Throws<PdfException>(() => Save(pdf)).Message, StringComparison.Ordinal);
    }
}
