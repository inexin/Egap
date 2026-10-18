using System.Text;

namespace Egap.Pdf.Tests.Optimizing;

/// <summary>PdfDocument.Optimize with its presets, clean-up passes, guards and report: Phase 7 task 8.</summary>
public sealed class OptimizeTests
{
    [Fact]
    public void LosslessRemovesWhatNobodySeesAndWritesACompactFile()
    {
        byte[] original = Sample(withExtras: true);
        using PdfDocument pdf = PdfDocument.Open(original);
        string? modified = pdf.Metadata.ModificationDate?.ToString("O");

        PdfOptimizeReport report = pdf.Optimize(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(original.Length, report.BytesBefore);
        Assert.True(report.BytesAfter < report.BytesBefore, report.ToString());
        Assert.Equal(1, report.Savings.ThumbnailsRemoved);
        Assert.Equal(1, report.Savings.PrivateDataRemoved);
        Assert.Equal(1, report.Savings.EmptyContentRemoved);
        Assert.True(report.Savings.StreamsMerged >= 1);
        Assert.Empty(report.Skipped);
        Assert.All(report.Images, i => Assert.Equal((i.OldWidth, i.OldHeight), (i.NewWidth, i.NewHeight)));

        using MemoryStream saved = new();
        pdf.Save(saved);
        Assert.Equal(report.BytesAfter, saved.Length);
        string text = Encoding.Latin1.GetString(saved.ToArray());
        Assert.Contains("/ObjStm", text, StringComparison.Ordinal);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        Assert.Equal(2, reopened.Pages.Count);
        Assert.Null(reopened.Pages[0].Dictionary.Get("Thumb"));
        Assert.Equal(modified, reopened.Metadata.ModificationDate?.ToString("O"));
        Assert.Same(reopened.Pages[0].GetImages()[0].Image.Stream, reopened.Pages[1].GetImages()[0].Image.Stream);
    }

    [Fact]
    public void OptimizingTwiceChangesNothingMore()
    {
        using PdfDocument first = PdfDocument.Open(Sample(withExtras: true));
        first.Optimize(PdfOptimizeOptions.Ebook, cancellationToken: TestContext.Current.CancellationToken);
        using MemoryStream once = new();
        first.Save(once);

        using PdfDocument second = PdfDocument.Open(once.ToArray());
        PdfOptimizeReport again = second.Optimize(PdfOptimizeOptions.Ebook, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(again.Images);
        Assert.Equal(0, again.Savings.FontsSubset);
        Assert.InRange(again.BytesAfter, once.Length - 64, once.Length + 64);
    }

    [Fact]
    public void EbookDownsamplesAndReportsEachImage()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample(withExtras: false));

        PdfOptimizeReport report = pdf.Optimize(PdfOptimizeOptions.Ebook, cancellationToken: TestContext.Current.CancellationToken);

        PdfOptimizedImage image = Assert.Single(report.Images);
        Assert.Equal((1, 600, 300, "JPEG"), (image.Page, image.OldWidth, image.NewWidth, image.Encoding));
        Assert.Equal(300, image.OldResolution!.Value, 6);
        Assert.True(report.Savings.ImageBytes > 0);
        Assert.True(report.BytesAfter < report.BytesBefore / 2);
    }

    [Fact]
    public void LeavesASignedDocumentAloneUnlessAllowed()
    {
        byte[] signed = Signed();
        using (PdfDocument pdf = PdfDocument.Open(signed))
        {
            PdfOptimizeReport report = pdf.Optimize(PdfOptimizeOptions.Ebook, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(report.Skipped, s => s.Contains("signed", StringComparison.Ordinal));
            Assert.Equal(report.BytesBefore, report.BytesAfter);
            using MemoryStream unchanged = new();
            pdf.Save(unchanged);
            Assert.Equal(signed, unchanged.ToArray());
        }

        // Strict reading makes a save that breaks a signature unasked throw.
        using (PdfDocument pdf = PdfDocument.Open(signed, new PdfReadOptions { Strict = true }))
        {
            PdfOptimizeReport report = pdf.Optimize(PdfOptimizeOptions.Ebook with { AllowInvalidatingSignatures = true }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(report.Skipped);
            using MemoryStream rewritten = new();
            pdf.Save(rewritten);
            Assert.Equal(report.BytesAfter, rewritten.Length);
        }
    }

    [Theory]
    [InlineData("B")]
    [InlineData("A")]
    public void PdfAOneIsWrittenWithoutObjectStreamsAndKeepsItsFonts(string level)
    {
        // PDF/A-1a is found by the gate (Phase 7 task 21): its part rules apply though Conformance does not name it.
        using PdfDocument pdf = PdfDocument.Open(Sample(withExtras: true));
        pdf.Metadata.SetXmp(System.Xml.Linq.XDocument.Parse($"""
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" pdfaid:part="1" pdfaid:conformance="{level}"/>
            </rdf:RDF></x:xmpmeta>
            """));

        PdfOptimizeReport report = pdf.Optimize(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(report.Skipped, s => s.StartsWith("fonts", StringComparison.Ordinal));
        using MemoryStream saved = new();
        pdf.Save(saved);
        string text = Encoding.Latin1.GetString(saved.ToArray());
        Assert.DoesNotContain("/ObjStm", text, StringComparison.Ordinal);
        Assert.Contains("\nxref", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesOptionsOutsideTheirRange()
    {
        using PdfDocument pdf = PdfDocument.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Optimize(PdfOptimizeOptions.Ebook with { JpegQuality = 0 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Optimize(PdfOptimizeOptions.Ebook with { ImageResolution = -1 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Optimize(PdfOptimizeOptions.Ebook with { DownsampleThreshold = 0.5 }, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Two pages sharing a photo drawn at 300 dpi; with extras, a second uncompressed copy of the photo on page 2, a thumbnail,
    /// private data and an empty content stream.
    /// </summary>
    private static byte[] Sample(bool withExtras)
    {
        using PdfDocument pdf = PdfDocument.Create();
        byte[] photo = Photo(600, 600);
        PdfReference image = pdf.Objects.Add(Image(600, 600, photo, compress: true));
        PdfReference copy = withExtras ? pdf.Objects.Add(Image(600, 600, photo, compress: false)) : image;
        foreach (PdfReference drawn in new[] { image, copy })
        {
            PdfPage page = pdf.Pages.AddBlank();
            page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = drawn } };
            page.SetContent(PdfContent.Parse("q 144 0 0 144 72 72 cm /Im Do Q"u8));
        }

        if (withExtras)
        {
            PdfPage first = pdf.Pages[0];
            first.Dictionary["Thumb"] = pdf.Objects.Add(Image(16, 16, new byte[256], compress: true));
            first.Dictionary["PieceInfo"] = new PdfDictionary { ["App"] = new PdfDictionary { ["Private"] = PdfString.FromText("data") } };
            PdfStream empty = new(new PdfDictionary());
            empty.SetDecodedBytes("  \n"u8, compress: false);
            first.Dictionary["Contents"] = new PdfArray { first.Dictionary["Contents"]!, pdf.Objects.Add(empty) };
        }

        pdf.Metadata.ModificationDate = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using MemoryStream saved = new();
        pdf.Save(saved);
        return saved.ToArray();
    }

    /// <summary>The sample with a signature field holding a signature value, as the signature guard sees one.</summary>
    private static byte[] Signed()
    {
        using PdfDocument pdf = PdfDocument.Open(Sample(withExtras: false));
        PdfDictionary signature = new()
        {
            ["Type"] = PdfName.Get("Sig"),
            ["Filter"] = PdfName.Get("Adobe.PPKLite"),
            ["SubFilter"] = PdfName.Get("adbe.pkcs7.detached"),
            ["ByteRange"] = new PdfArray { new PdfInteger(0), new PdfInteger(10), new PdfInteger(20), new PdfInteger(10) },
            ["Contents"] = PdfString.FromBytes(new byte[16], hex: true),
        };
        PdfDictionary field = new() { ["FT"] = PdfName.Get("Sig"), ["T"] = PdfString.FromText("Signature1"), ["V"] = pdf.Objects.Add(signature) };
        pdf.Catalog["AcroForm"] = new PdfDictionary { ["Fields"] = new PdfArray { pdf.Objects.Add(field) }, ["SigFlags"] = new PdfInteger(3) };
        using MemoryStream saved = new();
        pdf.Save(saved);
        return saved.ToArray();
    }

    private static PdfStream Image(int width, int height, byte[] gray, bool compress)
    {
        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(width),
            ["Height"] = new PdfInteger(height),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["ColorSpace"] = PdfName.Get("DeviceGray"),
        });
        image.SetDecodedBytes(gray, compress);
        return image;
    }

    private static byte[] Photo(int width, int height)
    {
        Random random = new(7);
        byte[] bytes = new byte[width * height];
        for (int y = 0, i = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++, i++)
            {
                bytes[i] = (byte)Math.Clamp(128 + (100 * Math.Sin(x / 29.0) * Math.Cos(y / 41.0)) + random.Next(-10, 11), 0, 255);
            }
        }

        return bytes;
    }
}
