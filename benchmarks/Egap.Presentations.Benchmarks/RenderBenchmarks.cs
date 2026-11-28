using System.Globalization;

using BenchmarkDotNet.Attributes;

using Egami;
using Egami.Pixels;

namespace Egap.Presentations.Benchmarks;

/// <summary>
/// Drawing slides made with Egap: a title and three bullets with five shapes, at 1,280 pixels wide, the first time a deck is
/// drawn (fonts and theme read) and once the deck's render cache is warm.
/// </summary>
[MemoryDiagnoser]
public class RenderBenchmarks
{
    private static readonly SlideRenderOptions Options = new() { Width = 1280, Date = new DateTime(2026, 11, 28) };

    private Presentation deck = null!;
    private byte[] file = [];

    [GlobalSetup]
    public void Setup()
    {
        Presentation made = Presentation.Create();
        SlideMaster master = made.SlideMasters[0];
        for (int i = 0; i < 10; i++)
        {
            Slide slide = made.Slides.Add(master.GetLayout(SlideLayoutType.TitleAndContent)!);
            ((AutoShape)slide.Shapes[0]).TextBody!.Text = "Slide " + (i + 1).ToString(CultureInfo.InvariantCulture);
            ((AutoShape)slide.Shapes[1]).TextBody!.Text = "First point\rSecond point\rThird point with a longer line of text that wraps across the box";
            for (int s = 0; s < 5; s++)
            {
                slide.Shapes.AddShape("roundRect", ShapeTransform.FromPoints(60 + (s * 120), 400, 100, 60), "Box " + s.ToString(CultureInfo.InvariantCulture));
            }
        }

        using MemoryStream stream = new();
        made.Save(stream);
        file = stream.ToArray();
        deck = Presentation.Load(new MemoryStream(file, writable: false));
        using Raster<Rgba8> warm = deck.Slides[0].Render(Options);
    }

    [Benchmark(Description = "Render a slide, warm")]
    public int RenderWarm()
    {
        using Raster<Rgba8> raster = deck.Slides[1].Render(Options);
        return raster.Height;
    }

    [Benchmark(Description = "Load and render a slide, cold")]
    public int RenderCold()
    {
        Presentation loaded = Presentation.Load(new MemoryStream(file, writable: false));
        using Raster<Rgba8> raster = loaded.Slides[0].Render(Options);
        return raster.Height;
    }
}
