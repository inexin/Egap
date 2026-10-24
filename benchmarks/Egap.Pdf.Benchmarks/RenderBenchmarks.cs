using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Benchmarks;

/// <summary>
/// Rendering colour-heavy pages, each from a freshly opened document so no cache carries over: DeviceCMYK fills and an
/// 8-bit DeviceCMYK image without an output intent, and fills under an ExtGState transfer function.
/// </summary>
[MemoryDiagnoser]
public class RenderBenchmarks
{
    private static readonly PdfRenderOptions Options = new() { ImageCacheLimit = 0 };

    private byte[] cmykFills = [];
    private byte[] cmykImage = [];
    private byte[] transferFills = [];

    [GlobalSetup]
    public void Setup()
    {
        cmykFills = Fills("k", null);
        transferFills = Fills("rg", new PdfDictionary
        {
            ["TR"] = new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(2),
                ["Domain"] = new PdfArray { new PdfReal(0), new PdfReal(1) },
                ["N"] = new PdfReal(2.2),
            },
        });

        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 512, 512));
        byte[] samples = new byte[512 * 512 * 4];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)((i * 37) ^ (i >> 9));
        }

        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(512),
            ["Height"] = new PdfInteger(512),
            ["ColorSpace"] = PdfName.Get("DeviceCMYK"),
            ["BitsPerComponent"] = new PdfInteger(8),
        });
        image.SetDecodedBytes(samples);
        page.Dictionary["Resources"] = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = pdf.Objects.Add(image) } };
        page.SetContent(PdfContent.Parse("512 0 0 512 0 0 cm /Im Do"u8.ToArray()));
        cmykImage = Save(pdf);
    }

    [Benchmark]
    public int CmykFills()
    {
        return Render(cmykFills);
    }

    [Benchmark]
    public int CmykImage()
    {
        return Render(cmykImage);
    }

    [Benchmark]
    public int TransferFunctionFills()
    {
        return Render(transferFills);
    }

    private static int Render(byte[] file)
    {
        using PdfDocument pdf = PdfDocument.Open(file);
        using Raster<Rgba8> raster = pdf.Pages[0].Render(Options);
        return raster.GetPixel(7, 7).R;
    }

    /// <summary>A 512-point page of 4,096 small squares in 1,024 colours, set with <paramref name="op"/> (k or rg).</summary>
    private static byte[] Fills(string op, PdfDictionary? state)
    {
        StringBuilder content = new();
        if (state is not null)
        {
            content.Append("/S gs\n");
        }

        for (int i = 0; i < 4096; i++)
        {
            int c = i % 1024;
            string color = op == "k"
                ? string.Create(CultureInfo.InvariantCulture, $"{(c & 7) / 7.0:0.###} {((c >> 3) & 7) / 7.0:0.###} {((c >> 6) & 7) / 7.0:0.###} {(c >> 9) / 1.0:0.###} k")
                : string.Create(CultureInfo.InvariantCulture, $"{(c & 15) / 15.0:0.###} {((c >> 4) & 15) / 15.0:0.###} {(c >> 8) / 3.0:0.###} rg");
            content.Append(CultureInfo.InvariantCulture, $"{color} {i % 64 * 8} {i / 64 * 8} 8 8 re f\n");
        }

        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 512, 512));
        if (state is not null)
        {
            page.Dictionary["Resources"] = new PdfDictionary { ["ExtGState"] = new PdfDictionary { ["S"] = state } };
        }

        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(content.ToString())));
        return Save(pdf);
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream stream = new();
        pdf.Save(stream);
        return stream.ToArray();
    }
}
