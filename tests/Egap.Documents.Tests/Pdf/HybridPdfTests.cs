using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf;

/// <summary>Hybrid PDFs: a DOCX of the document embedded as the PDF's source, and read back instead of the pages.</summary>
public sealed class HybridPdfTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    private static Document Sample()
    {
        Document document = new();
        document.Properties.Title = "Quarterly report";
        document.Properties.SetCustom("Origin", "model");
        Body body = document.AddSection().Body;
        Paragraph heading = body.AddParagraph("Quarterly report");
        heading.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Heading1);
        Run bold = body.AddParagraph("Sales rose by ").AddRun("a tenth");
        bold.Format.Bold = true;
        return document;
    }

    private static byte[] Export(Document document, PdfExportOptions options)
    {
        using MemoryStream stream = new();
        document.SaveAsPdf(stream, options with { Layout = Isolated });
        return stream.ToArray();
    }

    [Fact]
    public void TheSourceIsAnAssociatedDocxFile()
    {
        using PdfDocument pdf = PdfDocument.Open(Export(Sample(), new PdfExportOptions { EmbedSource = true }));

        PdfAttachment source = Assert.Single(pdf.Attachments);
        Assert.Equal("Quarterly report.docx", source.Name);
        Assert.Equal("Source", source.Relationship);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", source.MimeType);
        Assert.Single(pdf.Catalog.Get<PdfArray>("AF")!);
        Assert.Equal(DocumentFormatKind.Docx, Document.Identify(new MemoryStream(source.GetBytes()!)));
    }

    [Fact]
    public void LoadingAHybridPdfGivesTheDocumentBack()
    {
        List<PdfToDocumentWarning> warnings = [];
        Document back = Document.LoadPdf(new MemoryStream(Export(Sample(), new PdfExportOptions { EmbedSource = true })), new PdfToDocumentOptions { OnWarning = warnings.Add });

        Paragraph[] paragraphs = [.. back.Sections[0].Body.Paragraphs];
        Assert.Equal("model", back.Properties.GetCustom("Origin"));
        Assert.Equal("Heading1", paragraphs[0].Style!.Id);
        Assert.True(paragraphs[1].Runs.Last().Format.Bold);
        Assert.Empty(warnings);
    }

    [Fact]
    public void IgnoreAnalysesThePages()
    {
        Document back = Document.LoadPdf(new MemoryStream(Export(Sample(), new PdfExportOptions { EmbedSource = true })), new PdfToDocumentOptions { EmbeddedSource = EmbeddedSource.Ignore });

        Assert.Contains("Sales rose", string.Concat(back.Descendants<Run>().Select(r => r.Text)), StringComparison.Ordinal);
        Assert.Null(back.Properties.GetCustom("Origin"));
    }

    [Fact]
    public void APdfChangedAfterExportIsAnalysedWithAWarning()
    {
        byte[] hybrid = Export(Sample(), new PdfExportOptions { EmbedSource = true });
        using MemoryStream edited = new();
        using (PdfDocument pdf = PdfDocument.Open(hybrid))
        {
            pdf.Metadata.Title = "Edited";
            pdf.Save(edited, PdfSaveOptions.Default with { Mode = PdfSaveMode.Incremental });
        }

        List<PdfToDocumentWarning> warnings = [];
        Document back = Document.LoadPdf(new MemoryStream(edited.ToArray()), new PdfToDocumentOptions { OnWarning = warnings.Add });

        PdfToDocumentWarning warning = Assert.Single(warnings, w => w.Kind == PdfToDocumentWarningKind.EmbeddedSourceSkipped);
        Assert.Contains("changed or signed", warning.Message, StringComparison.Ordinal);
        Assert.Null(back.Properties.GetCustom("Origin"));
        Assert.Throws<DocumentException>(() => Document.LoadPdf(new MemoryStream(edited.ToArray()), new PdfToDocumentOptions { EmbeddedSource = EmbeddedSource.Require }));
    }

    [Fact]
    public void ASourceSomeoneElseAttachedIsNotTrusted()
    {
        using MemoryStream crafted = new();
        using (PdfDocument pdf = PdfDocument.Open(Export(Sample(), new PdfExportOptions())))
        {
            using MemoryStream other = new();
            Document different = new();
            different.AddSection().Body.AddParagraph("Something else entirely");
            DocxFormat.Save(different, other);
            pdf.Attachments.Add("report.docx", other.ToArray(), PdfAttachmentRelationship.Source, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            pdf.Save(crafted, PdfSaveOptions.Default with { Mode = PdfSaveMode.Rewrite });
        }

        List<PdfToDocumentWarning> warnings = [];
        Document back = Document.LoadPdf(new MemoryStream(crafted.ToArray()), new PdfToDocumentOptions { OnWarning = warnings.Add });

        Assert.Contains(warnings, w => w.Kind == PdfToDocumentWarningKind.EmbeddedSourceSkipped && w.Message.Contains("not embedded by Egap", StringComparison.Ordinal));
        Assert.DoesNotContain("Something else", string.Concat(back.Descendants<Run>().Select(r => r.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void RequireThrowsWithoutASource()
    {
        Assert.Throws<DocumentException>(() => Document.LoadPdf(new MemoryStream(Export(Sample(), new PdfExportOptions())), new PdfToDocumentOptions { EmbeddedSource = EmbeddedSource.Require }));
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1b)]
    [InlineData(PdfAConformance.PdfA2b)]
    [InlineData(PdfAConformance.PdfA2u)]
    public void PdfA1And2Refuse(PdfAConformance conformance)
    {
        ArgumentException e = Assert.Throws<ArgumentException>(() => Export(Sample(), new PdfExportOptions { EmbedSource = true, Conformance = conformance }));
        Assert.Contains("PDF/A-3", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfA3Allows()
    {
        Document document = Sample();
        document.Styles.DefaultRunFormat.Language = "en-US";
        using PdfDocument pdf = PdfDocument.Open(Export(document, new PdfExportOptions { EmbedSource = true, Conformance = PdfAConformance.PdfA3b }));

        PdfAReport report = PdfAConverter.Check(pdf, PdfAConformance.PdfA3b);
        Assert.True(report.Succeeded, string.Join("\n", report.Unmet.Select(i => i.Clause + " " + i.Description)));
    }

    [Fact]
    public void ConvertOptionsEmbedToo()
    {
        string path = Path.Combine(Path.GetTempPath(), "egap-hybrid-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "# Title\n\nText.");
        try
        {
            using PdfDocument pdf = PdfConvert.FromFile(path, new PdfConvertOptions { EmbedSource = true });
            Assert.Equal("Source", Assert.Single(pdf.Attachments).Relationship);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
