using System.Text;

using BenchmarkDotNet.Attributes;

namespace Egap.Documents.Benchmarks;

/// <summary>Reading HTML into a document, at three sizes: from a string, and from UTF-8 bytes with encoding detection.</summary>
[MemoryDiagnoser]
public class HtmlLoadBenchmarks
{
    private string html = "";
    private byte[] bytes = [];

    [ParamsSource(nameof(Sizes))]
    public string Size { get; set; } = "Small";

    public static IEnumerable<string> Sizes => Samples.Sizes;

    [GlobalSetup]
    public void Setup()
    {
        html = Samples.Html(Samples.Sections(Size));
        bytes = Encoding.UTF8.GetBytes(html);
    }

    [Benchmark]
    public int Parse() => HtmlFormat.Parse(html).Sections.Count;

    [Benchmark]
    public int LoadBytes()
    {
        using MemoryStream stream = new(bytes, writable: false);
        return HtmlFormat.Load(stream).Sections.Count;
    }
}
