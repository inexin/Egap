using Egami;
using Egami.Formats.Jpeg;
using Egami.Pixels;

using Egap.Pdf.Optimizing;

namespace Egap.Pdf.Tests.Optimizing;

/// <summary>Existing JPEG images in the optimizer: Phase 7 task 5.</summary>
public sealed class JpegOptimizerTests
{
    private static readonly ImageSettings Ebook = new() { TargetResolution = 150, JpegQuality = 75 };

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(98)]
    public void EstimatesTheQualityFromTheQuantisationTables(int quality)
    {
        Assert.InRange(JpegOptimizer.Quality(Jpeg(64, 64, quality))!.Value, quality - 2, quality + 2);
    }

    [Fact]
    public void ReencodesAJpegClearlyAboveThePresetsQuality()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream image = ImageEncoderStream(Jpeg(300, 300, 95));
        Draw(pdf, image, 144);
        long before = image.EncodedLength;

        ImageChange change = Assert.IsType<ImageChange>(ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook));

        Assert.Equal(("JPEG", 300), (change.Encoding, change.NewWidth));
        Assert.True(image.EncodedLength < before * 0.95);
        Assert.InRange(JpegOptimizer.Quality(image.GetEncodedBytes())!.Value, 73, 77);
    }

    [Fact]
    public void OnlyOptimisesTheHuffmanTablesOfAJpegNearThePresetsQuality()
    {
        using PdfDocument pdf = PdfDocument.Create();
        byte[] original = Jpeg(300, 300, 80);
        PdfStream image = ImageEncoderStream(original);
        Draw(pdf, image, 144);
        using Raster<Rgba8> before = new PdfImage(image, null).ToRaster<Rgba8>();

        ImageChange? change = ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook);

        Assert.Equal("JPEG (Huffman optimized)", change?.Encoding);
        Assert.True(image.EncodedLength < original.Length);
        using Raster<Rgba8> after = new PdfImage(image, null).ToRaster<Rgba8>();
        for (int y = 0; y < 300; y++)
        {
            Assert.True(before.Row(y).SequenceEqual(after.Row(y)), $"row {y}");
        }
    }

    [Fact]
    public void LosslessOnlyOptimisesHuffmanTables()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream image = ImageEncoderStream(Jpeg(300, 300, 98));
        Draw(pdf, image, 72);

        Assert.Equal("JPEG (Huffman optimized)", ImageRecompressor.Optimize(pdf, Survey(pdf), new ImageSettings { Lossy = false })?.Encoding);
        Assert.InRange(JpegOptimizer.Quality(image.GetEncodedBytes())!.Value, 96, 100);
    }

    [Fact]
    public void ADownsampledJpegIsNotWrittenAboveItsOwnQuality()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream image = ImageEncoderStream(Jpeg(600, 600, 50));
        Draw(pdf, image, 144);

        Assert.Equal(300, ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook)!.NewWidth);
        Assert.InRange(JpegOptimizer.Quality(image.GetEncodedBytes())!.Value, 48, 52);
    }

    private static SurveyedImage Survey(PdfDocument pdf) => Assert.Single(ImageSurvey.Run(pdf));

    private static void Draw(PdfDocument pdf, PdfStream image, int points)
    {
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = pdf.Objects.Add(image) } };
        page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes($"q {points} 0 0 {points} 0 0 cm /Im Do Q")));
    }

    private static PdfStream ImageEncoderStream(byte[] jpeg) => Egap.Pdf.Images.ImageEncoder.JpegStream(jpeg);

    private static byte[] Jpeg(int width, int height, int quality)
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        Random random = new(quality);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                double v = 128 + (90 * Math.Sin(x / 23.0) * Math.Cos(y / 31.0));
                row[x] = new Rgb8((byte)Math.Clamp(v + random.Next(-10, 11), 0, 255), (byte)Math.Clamp(255 - v, 0, 255), (byte)(x * 255 / width));
            }
        }

        using MemoryStream jpeg = new();
        raster.Save(jpeg, JpegFormat.Info, new JpegWriterOptions { Quality = quality });
        return jpeg.ToArray();
    }
}
