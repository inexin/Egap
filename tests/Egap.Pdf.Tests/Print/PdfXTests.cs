using Egami.Colors;

namespace Egap.Pdf.Tests.Print;

/// <summary>PDF/X checks and conversion: output intent, boxes, identification, colours, transparency and the rest.</summary>
public sealed class PdfXTests
{
    private static PdfDocument Open(PrintSamples.PrintFile file) => PdfDocument.Open(new MemoryStream(file.Bytes));

    [Fact]
    public void TheAffinityCaseConvertsToPdfX4AndThenPasses()
    {
        using PdfDocument pdf = Open(PrintSamples.Affinity());

        PdfAReport check = PdfXConverter.Check(pdf, PdfXConformance.PdfX4, TestContext.Current.CancellationToken);
        Assert.False(check.Succeeded);
        Assert.Contains(check.Issues, i => i.Clause == "ColorSpace" && i.Fixable);
        Assert.Contains(check.Issues, i => i.Clause == "Trapped" && i.Fixable);
        Assert.Contains(check.Issues, i => i.Clause == "Identification" && i.Fixable);
        Assert.DoesNotContain(check.Issues, i => i.Clause is "OutputIntent" or "TrimBox" or "BleedBox");

        PdfAReport converted = PdfXConverter.Convert(pdf, PdfXConformance.PdfX4, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(converted.Succeeded, converted.ToString());
        Assert.StartsWith("PDF/X-4 (ISO 15930-7): met", converted.ToString(), StringComparison.Ordinal);

        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        Assert.Equal("1.6", reopened.Version);
        Assert.True(PdfXConverter.Check(reopened, PdfXConformance.PdfX4, TestContext.Current.CancellationToken).Succeeded);
        Assert.Equal("False", reopened.Metadata.Info!.GetName("Trapped")!.Value);
        Assert.Contains("PDF/X-4", System.Text.Encoding.UTF8.GetString(reopened.Catalog.Get<PdfStream>("Metadata")!.GetDecodedBytes()), StringComparison.Ordinal);
        PdfPreflightReport preflight = PdfPreflight.Run(reopened, PdfPrintProfile.PdfX4 with { MinImageDpi = null }, TestContext.Current.CancellationToken);
        Assert.True(preflight.Passed, preflight.ToString());
    }

    [Fact]
    public void PdfX1aRefusesTransparencyAndLeavesTheFileAsItWas()
    {
        using PdfDocument pdf = Open(PrintSamples.Transparency());
        byte[] before = Save(pdf);

        PdfAReport report = PdfXConverter.Convert(pdf, PdfXConformance.PdfX1a, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        PdfAIssue transparency = Assert.Single(report.Issues, i => i.Clause == "Transparency");
        Assert.False(transparency.Fixable);
        Assert.Contains("flattening", transparency.Description, StringComparison.Ordinal);
        Assert.Equal(before, Save(pdf));
    }

    [Fact]
    public void RgbConvertsToPdfX1aWithDeviceColoursOnly()
    {
        using PdfDocument pdf = Open(PrintSamples.Mixed());

        PdfAReport report = PdfXConverter.Convert(pdf, PdfXConformance.PdfX1a, new PdfXConvertOptions { OutputCondition = PdfOutputCondition.Fogra39 }, TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, report.ToString());
        byte[] saved = Save(pdf);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(saved, 0, 8), StringComparison.Ordinal);
        using PdfDocument reopened = PdfDocument.Open(saved);
        Assert.Equal("PDF/X-1a:2003", reopened.Metadata.Info!.GetText(PdfName.Get("GTS_PDFXVersion")));
        Assert.DoesNotContain(reopened.Pages[1].GetContent(), o => o.Operator is "rg" or "RG");
        Assert.True(PdfXConverter.Check(reopened, PdfXConformance.PdfX1a, TestContext.Current.CancellationToken).Succeeded);
    }

    [Fact]
    public void BoxesComeFromTheCropBoxAndTheBleed()
    {
        using PdfDocument pdf = Open(PrintSamples.FontsAndRules());

        PdfAReport report = PdfXConverter.Convert(pdf, PdfXConformance.PdfX4, new PdfXConvertOptions { Bleed = 3, AllowFontSubstitution = true }, TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, report.ToString());
        PdfPage page = pdf.Pages[0];
        Assert.Equal(new PdfRectangle(0, 0, 612, 792), page.TrimBox);
        Assert.Equal(new PdfRectangle(0, 0, 612, 792), page.BleedBox);
        Assert.Contains(report.Fixes, i => i.Clause == "Fonts");
    }

    [Fact]
    public void AnOutputConditionWithoutAProfileCannotBeEmbedded()
    {
        using PdfDocument pdf = Open(PrintSamples.Mixed());

        PdfAReport report = PdfXConverter.Convert(pdf, PdfXConformance.PdfX4, new PdfXConvertOptions { OutputCondition = PdfOutputCondition.Fogra51 }, TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Issues, i => i.Clause == "OutputIntent" && !i.Fixable);
        Assert.NotNull(PdfOutputCondition.Find("fogra51"));
        Assert.Same(ColorProfile.Fogra39, PdfOutputCondition.Fogra39.Profile);
    }

    [Fact]
    public void CheckOnlyVersionsAreNotConverted()
    {
        using PdfDocument pdf = Open(PrintSamples.Affinity());

        PdfAReport report = PdfXConverter.Convert(pdf, PdfXConformance.PdfX5g, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Issues, i => i.Clause == "Conversion");
        Assert.Contains(PdfXConverter.Check(pdf, PdfXConformance.PdfX4p, TestContext.Current.CancellationToken).Issues, i => i.Description.Contains("DestOutputProfileRef", StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptsTransferFunctionsAndPrintedAnnotationsAreFixed()
    {
        using PdfDocument pdf = Open(PrintSamples.Affinity());
        pdf.Catalog["Names"] = new PdfDictionary { ["JavaScript"] = new PdfDictionary { ["Names"] = new PdfArray() } };
        PdfPage page = pdf.Pages[0];
        page.Resources!["ExtGState"] = new PdfDictionary { ["G0"] = new PdfDictionary { ["TR"] = new PdfDictionary { ["FunctionType"] = new PdfInteger(2), ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) }, ["N"] = new PdfInteger(1) } } };
        PdfAnnotation note = page.Annotations.Add(PdfAnnotation.FromDictionary(new PdfDictionary { ["Type"] = PdfName.Get("Annot"), ["Subtype"] = PdfName.Get("Square"), ["F"] = new PdfInteger(4) }));
        note.Rectangle = new PdfRectangle(100, 100, 200, 200);
        note.UpdateAppearance();

        PdfAReport report = PdfXConverter.Convert(pdf, PdfXConformance.PdfX4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, report.ToString());
        Assert.Contains(report.Fixes, i => i.Clause == "Actions");
        Assert.Contains(report.Fixes, i => i.Clause == "TransferFunction");
        Assert.Contains(report.Fixes, i => i.Clause == "Annotations");
        Assert.Empty(page.Annotations);
    }

    [Fact]
    public void PdfX6IsWrittenAsPdf2AndNColorVersionsNeedAnNColorIntent()
    {
        using PdfDocument pdf = Open(PrintSamples.Affinity());

        PdfAReport x6 = PdfXConverter.Convert(pdf, PdfXConformance.PdfX6, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(x6.Succeeded, x6.ToString());
        byte[] saved = Save(pdf);
        Assert.StartsWith("%PDF-2.0", System.Text.Encoding.ASCII.GetString(saved, 0, 8), StringComparison.Ordinal);

        using PdfDocument cmyk = Open(PrintSamples.Affinity());
        PdfAReport n = PdfXConverter.Check(cmyk, PdfXConformance.PdfX5n, TestContext.Current.CancellationToken);
        Assert.Contains(n.Issues, i => i.Clause == "OutputIntent" && i.Description.Contains("CMYK", StringComparison.Ordinal) && !i.Fixable);
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Deterministic = true, Mode = PdfSaveMode.Rewrite });
        return output.ToArray();
    }
}
