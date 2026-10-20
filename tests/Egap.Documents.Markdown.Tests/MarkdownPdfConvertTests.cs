using Egami;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Markdown.Tests;

/// <summary>Markdown to PDF through PdfConvert, once the add-on is registered.</summary>
public sealed class MarkdownPdfConvertTests : IDisposable
{
    private static readonly PdfConvertOptions Isolated = new()
    {
        Export = new PdfExportOptions { Layout = new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false } } },
    };

    private static readonly PdfConvertOptions Portable = new()
    {
        Export = new PdfExportOptions { Layout = new LayoutOptions { Fonts = FontSettings.Portable } },
    };

    private readonly string folder = Directory.CreateTempSubdirectory("egap-markdown-convert-").FullName;

    public MarkdownPdfConvertTests()
    {
        MarkdownFormat.Register();
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void MarkdownAndFilesConvertToPdfFiles()
    {
        string md = Path.Combine(folder, "notes.md");
        File.WriteAllText(md, "# Notes\n\n| A | B |\n| - | - |\n| 1 | 2 |\n\n![logo](logo.png)\n");
        File.WriteAllBytes(Path.Combine(folder, "logo.png"), Png());
        string output = Path.Combine(folder, "notes.pdf");

        PdfConvert.FromFile(md, output, Isolated);

        using PdfDocument pdf = PdfDocument.Open(output);
        Assert.Contains("Notes", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Single(pdf.Pages[0].GetImages());

        using PdfDocument fromText = Reopen(PdfConvert.FromMarkdown("*Hello*", Isolated));
        Assert.Contains("Hello", fromText.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownTocMarkerOrTheStartTakesTheTable()
    {
        using PdfDocument marked = Reopen(PdfConvert.FromMarkdown("Intro text\n\n[TOC]\n\n# Alpha\n\nA\n\n# Beta\n\nB\n", Portable with { TableOfContents = TableOfContentsMode.WithoutPageNumbers }));
        string[] lines = [.. marked.Pages[0].GetText().Lines.Select(l => l.Text.Trim())];
        Assert.Equal("Intro text", lines[0]);
        Assert.Equal("Alpha", lines[1]);
        Assert.Equal("Beta", lines[2]);
        Assert.DoesNotContain("[TOC]", marked.Pages[0].ExtractText(), StringComparison.Ordinal);

        using PdfDocument start = Reopen(PdfConvert.FromMarkdown("Intro text\n\n# Alpha\n\nA\n", Portable with { TableOfContents = TableOfContentsMode.WithPageNumbers }));
        string[] first = [.. start.Pages[0].GetText().Lines.Select(l => l.Text.Trim())];
        Assert.StartsWith("Alpha", first[0], StringComparison.Ordinal);
        Assert.EndsWith("1", first[0], StringComparison.Ordinal);
        Assert.Equal("Intro text", first[1]);
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using (pdf)
        {
            using MemoryStream saved = new();
            pdf.Save(saved);
            return PdfDocument.Open(saved.ToArray());
        }
    }

    private static byte[] Png()
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(8, 8, new Rgb8(0, 128, 0));
        using MemoryStream png = new();
        raster.Save(png, "png");
        return png.ToArray();
    }
}
