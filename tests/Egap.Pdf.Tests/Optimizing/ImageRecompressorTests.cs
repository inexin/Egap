using Egami;
using Egami.Pixels;

using Egap.Pdf.Optimizing;

namespace Egap.Pdf.Tests.Optimizing;

/// <summary>The optimizer's downsampling and re-encoding: Phase 7 task 3.</summary>
public sealed class ImageRecompressorTests
{
    private static readonly ImageSettings Ebook = new() { TargetResolution = 150, JpegQuality = 75 };

    [Fact]
    public void DownsamplesAPhotoAboveTheThresholdAndWritesJpeg()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream stream = Image(600, 600, 3, Photo(600, 600, 3));
        stream.Dictionary["Interpolate"] = PdfBoolean.Get(true);
        PdfReference reference = Draw(pdf, stream, 144);
        using Raster<Rgba8> before = new PdfImage(stream, null).ToRaster<Rgba8>();
        long oldBytes = stream.EncodedLength;

        ImageChange change = Assert.IsType<ImageChange>(ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook));

        Assert.Equal((300, 300, "JPEG"), (change.NewWidth, change.NewHeight, change.Encoding));
        Assert.Equal(300, change.OldResolution!.Value, 6);
        Assert.Equal(150, change.NewResolution!.Value, 6);
        Assert.Equal(oldBytes, change.OldBytes);
        Assert.True(change.NewBytes < oldBytes * 0.95);
        PdfStream after = (PdfStream)pdf.Objects[reference];
        Assert.Same(stream, after);
        Assert.Equal("DCTDecode", after.Dictionary.GetName("Filter")!.Value);
        Assert.Equal("DeviceRGB", after.Dictionary.GetName("ColorSpace")!.Value);
        Assert.True(after.Dictionary.GetBool("Interpolate"));
        Assert.Equal("Image", after.Dictionary.GetName("Subtype")!.Value);

        // The smaller image still shows the same picture.
        using Raster<Rgba8> shown = new PdfImage(after, null).ToRaster<Rgba8>();
        Assert.InRange(MeanDifference(before, shown, 2), 0, 8);
    }

    [Fact]
    public void LeavesImagesAtOrBelowOneAndAHalfTimesTheTarget()
    {
        using PdfDocument pdf = PdfDocument.Create();
        // 450 samples across 144 points: 225 dpi, exactly 1.5 times 150.
        PdfStream stream = Image(450, 450, 3, Photo(450, 450, 3));
        Draw(pdf, stream, 144);
        SurveyedImage surveyed = Survey(pdf);

        Assert.Equal((450, 450), ImageRecompressor.TargetSize(surveyed, Ebook));
        ImageChange? change = ImageRecompressor.Optimize(pdf, surveyed, Ebook);

        // Not resampled; a Flate photo may still become JPEG at its size.
        Assert.True(change is null || (change.NewWidth, change.NewHeight, change.Encoding) == (450, 450, "JPEG"));
    }

    [Fact]
    public void FlatColourStaysLosslessAndKeepsItsColourSpace()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream profile = new(new PdfDictionary { ["N"] = new PdfInteger(1), ["Alternate"] = PdfName.Get("DeviceGray") });
        profile.SetDecodedBytes(new byte[200]);
        PdfArray space = [PdfName.Get("ICCBased"), pdf.Objects.Add(profile)];
        PdfStream stream = Image(800, 800, 1, Bars(800, 800));
        stream.Dictionary["ColorSpace"] = space;
        stream.SetDecodedBytes(Bars(800, 800), compress: false);
        Draw(pdf, stream, 72);

        ImageChange change = Assert.IsType<ImageChange>(ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook));

        Assert.Equal((150, 150, "Flate"), (change.NewWidth, change.NewHeight, change.Encoding));
        Assert.Same(space, stream.Dictionary.Get("ColorSpace"));
        Assert.Equal(12, stream.Dictionary.Get<PdfDictionary>("DecodeParms")!.GetInt("Predictor"));
    }

    [Fact]
    public void ResamplesTheSoftMaskWithItsImage()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream mask = Image(600, 600, 1, Bars(600, 600));
        PdfReference oldMask = pdf.Objects.Add(mask);
        PdfStream stream = Image(600, 600, 3, Photo(600, 600, 3));
        stream.Dictionary["SMask"] = oldMask;
        Draw(pdf, stream, 144);

        Assert.NotNull(ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook));

        PdfStream soft = stream.Dictionary.Get<PdfStream>("SMask")!;
        Assert.NotSame(mask, soft);
        Assert.Equal((300, 300), (soft.Dictionary.GetInt("Width"), soft.Dictionary.GetInt("Height")));
        Assert.Equal("DeviceGray", soft.Dictionary.GetName("ColorSpace")!.Value);
        using Raster<Rgba8> shown = new PdfImage(stream, null).ToRaster<Rgba8>();
        Assert.Equal(300, shown.Width);
    }

    [Fact]
    public void CmykWithADecodeArrayIsWrittenAsFlate()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream stream = Image(600, 600, 4, Photo(600, 600, 4));
        stream.Dictionary["ColorSpace"] = PdfName.Get("DeviceCMYK");
        PdfArray decode = [new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0)];
        stream.Dictionary["Decode"] = decode;
        stream.SetDecodedBytes(Photo(600, 600, 4), compress: false);
        Draw(pdf, stream, 144);

        ImageChange change = Assert.IsType<ImageChange>(ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook));

        Assert.Equal("Flate", change.Encoding);
        Assert.Same(decode, stream.Dictionary.Get("Decode"));
    }

    [Fact]
    public void LeavesWhatItCannotResampleAsStored()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream keyed = Image(600, 600, 3, Photo(600, 600, 3));
        keyed.Dictionary["Mask"] = new PdfArray { new PdfInteger(0), new PdfInteger(10), new PdfInteger(0), new PdfInteger(10), new PdfInteger(0), new PdfInteger(10) };
        Draw(pdf, keyed, 72);
        Assert.Null(ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook));

        using PdfDocument indexed = PdfDocument.Create();
        PdfStream palette = Image(600, 600, 1, Photo(600, 600, 1));
        palette.Dictionary["ColorSpace"] = new PdfArray { PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), new PdfInteger(255), PdfString.FromBytes(new byte[768], hex: true) };
        Draw(indexed, palette, 72);
        Assert.Null(ImageRecompressor.Optimize(indexed, Survey(indexed), Ebook));
    }

    [Fact]
    public void LosslessSettingsChangeNothing()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream stream = Image(600, 600, 3, Photo(600, 600, 3));
        Draw(pdf, stream, 72);

        Assert.Null(ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook with { Lossy = false }));
        Assert.Null(ImageRecompressor.Optimize(pdf, Survey(pdf), new ImageSettings { Lossy = false }));
    }

    [Fact]
    public void KeepsTheOriginalWhenTheSavingIsBelowFivePercent()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream stream = Image(600, 600, 3, Photo(600, 600, 3));
        Draw(pdf, stream, 144);
        byte[] original = stream.GetEncodedBytes();

        Assert.Null(ImageRecompressor.Optimize(pdf, Survey(pdf), Ebook with { MinSaving = 0.995 }));
        Assert.Equal(original, stream.GetEncodedBytes());
    }

    private static SurveyedImage Survey(PdfDocument pdf) => Assert.Single(ImageSurvey.Run(pdf));

    private static PdfReference Draw(PdfDocument pdf, PdfStream image, int points)
    {
        PdfReference reference = pdf.Objects.Add(image);
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = reference } };
        page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes($"q {points} 0 0 {points} 0 0 cm /Im Do Q")));
        return reference;
    }

    private static PdfStream Image(int width, int height, int components, byte[] samples)
    {
        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(width),
            ["Height"] = new PdfInteger(height),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["ColorSpace"] = PdfName.Get(components switch { 1 => "DeviceGray", 3 => "DeviceRGB", _ => "DeviceCMYK" }),
        });
        image.SetDecodedBytes(samples);
        return image;
    }

    /// <summary>Smooth gradients with a little noise: a photo by the survey's measure, which Flate cannot shrink much.</summary>
    private static byte[] Photo(int width, int height, int components)
    {
        Random random = new(width + components);
        byte[] bytes = new byte[width * height * components];
        for (int y = 0, i = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < components; c++, i++)
                {
                    double v = (128 + (100 * Math.Sin((x + (c * 40)) / 37.0) * Math.Cos(y / 53.0))) + random.Next(-12, 13);
                    bytes[i] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }

        return bytes;
    }

    private static byte[] Bars(int width, int height)
    {
        byte[] bytes = new byte[width * height];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % width / (width / 8) * 30);
        }

        return bytes;
    }

    /// <summary>The mean channel difference of two pictures, the larger sampled down to the smaller's size.</summary>
    private static double MeanDifference(Raster<Rgba8> large, Raster<Rgba8> small, int factor)
    {
        double sum = 0;
        int n = 0;
        for (int y = 0; y < small.Height; y++)
        {
            for (int x = 0; x < small.Width; x++)
            {
                Rgba8 a = large.GetPixel(Math.Min(large.Width - 1, (x * factor) + (factor / 2)), Math.Min(large.Height - 1, (y * factor) + (factor / 2)));
                Rgba8 b = small.GetPixel(x, y);
                sum += (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3.0;
                n++;
            }
        }

        return sum / n;
    }
}
