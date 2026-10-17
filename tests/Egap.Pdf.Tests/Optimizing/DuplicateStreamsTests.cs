using Egap.Pdf.Optimizing;

namespace Egap.Pdf.Tests.Optimizing;

/// <summary>Duplicate stream merging: Phase 7 task 6.</summary>
public sealed class DuplicateStreamsTests
{
    [Fact]
    public void MergesStreamsThatDecodeTheSameWhateverTheirFilters()
    {
        using PdfDocument pdf = PdfDocument.Create();
        byte[] pixels = [.. Enumerable.Range(0, 64 * 64).Select(i => (byte)(i * 7))];
        PdfStream flate = Gray(64, 64);
        flate.SetDecodedBytes(pixels);
        PdfStream raw = Gray(64, 64);
        raw.SetDecodedBytes(pixels, compress: false);
        PdfStream other = Gray(64, 64);
        other.SetDecodedBytes([.. pixels.Reverse()]);
        PdfReference a = pdf.Objects.Add(flate), b = pdf.Objects.Add(raw), c = pdf.Objects.Add(other);
        foreach (PdfReference image in new[] { a, b, c })
        {
            PdfPage page = pdf.Pages.AddBlank();
            page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = image } };
            page.SetContent(PdfContent.Parse("q 64 0 0 64 0 0 cm /Im Do Q"u8));
        }

        (int merged, long bytes) = DuplicateStreams.Run(pdf);

        // The raw image, and two of the three pages' identical content streams.
        Assert.Equal(3, merged);
        Assert.True(bytes > 64 * 64);
        using MemoryStream saved = new();
        pdf.Save(saved, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = PdfCompression.Compact });
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        PdfStream[] images = [.. reopened.Pages.Select(p => p.GetImages()[0].Image.Stream!)];
        Assert.Same(images[0], images[1]);
        Assert.NotSame(images[0], images[2]);
        Assert.Equal(pixels, images[1].GetDecodedBytes());
    }

    [Fact]
    public void KeepsStreamsWithOtherDictionariesOrCodecParametersApart()
    {
        using PdfDocument pdf = PdfDocument.Create();
        byte[] pixels = new byte[256];
        PdfStream gray = Gray(16, 16);
        gray.SetDecodedBytes(pixels);
        PdfStream interpolated = Gray(16, 16);
        interpolated.Dictionary["Interpolate"] = PdfBoolean.Get(true);
        interpolated.SetDecodedBytes(pixels);
        PdfStream g4 = Gray(16, 16), g3 = Gray(16, 16);
        foreach ((PdfStream fax, int k) in new[] { (g4, -1), (g3, 0) })
        {
            fax.Dictionary["Filter"] = PdfName.Get("CCITTFaxDecode");
            fax.Dictionary["DecodeParms"] = new PdfDictionary { ["K"] = new PdfInteger(k), ["Columns"] = new PdfInteger(16) };
            fax.SetEncodedBytes([1, 2, 3, 4]);
        }

        foreach (PdfStream stream in new[] { gray, interpolated, g4, g3 })
        {
            pdf.Objects.Add(stream);
        }

        Assert.Equal(0, DuplicateStreams.Run(pdf).Merged);
    }

    private static PdfStream Gray(int width, int height) => new(new PdfDictionary
    {
        ["Type"] = PdfName.Get("XObject"),
        ["Subtype"] = PdfName.Get("Image"),
        ["Width"] = new PdfInteger(width),
        ["Height"] = new PdfInteger(height),
        ["BitsPerComponent"] = new PdfInteger(8),
        ["ColorSpace"] = PdfName.Get("DeviceGray"),
    });
}
