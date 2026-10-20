using BenchmarkDotNet.Attributes;

namespace Egap.Documents.Benchmarks;

/// <summary>Reading Markdown into a document, at three sizes.</summary>
[MemoryDiagnoser]
public class MarkdownLoadBenchmarks
{
    private string markdown = "";

    [ParamsSource(nameof(Sizes))]
    public string Size { get; set; } = "Small";

    public static IEnumerable<string> Sizes => Samples.Sizes;

    [GlobalSetup]
    public void Setup() => markdown = Samples.Markdown(Samples.Sections(Size));

    [Benchmark]
    public int Parse() => MarkdownFormat.Parse(markdown).Sections.Count;
}
