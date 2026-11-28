using System.Globalization;

using BenchmarkDotNet.Attributes;

namespace Egap.Presentations.Benchmarks;

/// <summary>
/// A 50-slide deck made with Egap (titles, bullets, five shapes and a 4x4 table a slide, notes, transitions): loaded, saved,
/// and loaded, edited and saved.
/// </summary>
[MemoryDiagnoser]
public class PptxBenchmarks
{
    private Presentation deck = null!;
    private byte[] file = [];

    [GlobalSetup]
    public void Setup()
    {
        deck = Presentation.Create();
        SlideMaster master = deck.SlideMasters[0];
        for (int i = 0; i < 50; i++)
        {
            Slide slide = deck.Slides.Add(master.GetLayout(SlideLayoutType.TitleAndContent)!);
            ((AutoShape)slide.Shapes[0]).TextBody!.Text = "Slide " + (i + 1).ToString(CultureInfo.InvariantCulture);
            ((AutoShape)slide.Shapes[1]).TextBody!.Text = "First point\rSecond point\rThird point with a longer line of text";
            for (int s = 0; s < 5; s++)
            {
                slide.Shapes.AddShape("roundRect", ShapeTransform.FromPoints(60 + (s * 120), 400, 100, 60), "Box " + s.ToString(CultureInfo.InvariantCulture));
            }

            SlideTable table = slide.Shapes.AddTable(4, 4, ShapeTransform.FromPoints(500, 120, 400, 160)).Table!;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    table.Cell(r, c).Text = string.Create(CultureInfo.InvariantCulture, $"R{r}C{c}");
                }
            }

            slide.NotesText = "Speaker notes for this slide.";
            slide.Transition = new SlideTransition(SlideTransitionType.Fade) { AdvanceAfterMilliseconds = 3000 };
        }

        using MemoryStream stream = new();
        deck.Save(stream);
        file = stream.ToArray();
    }

    [Benchmark(Description = "Load 50 slides")]
    public Presentation Load() => Presentation.Load(new MemoryStream(file, writable: false));

    [Benchmark(Description = "Save 50 slides")]
    public long Save()
    {
        using MemoryStream stream = new(file.Length);
        deck.Save(stream);
        return stream.Length;
    }

    [Benchmark(Description = "Load, edit, save 50 slides")]
    public long LoadEditSave()
    {
        Presentation loaded = Presentation.Load(new MemoryStream(file, writable: false));
        foreach (Slide slide in loaded.Slides)
        {
            ((AutoShape)slide.Shapes[0]).TextBody!.Text += " (edited)";
            slide.Shapes.Remove(slide.Shapes[2]);
        }

        loaded.Slides.Move(loaded.Slides[^1], 0);
        using MemoryStream stream = new(file.Length);
        loaded.Save(stream);
        return stream.Length;
    }
}
