using BenchmarkDotNet.Attributes;

using Egap.Documents.Layout;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// Laying out a document into pages: the RTF sample's mix of paragraphs, lists, tables and pictures, with portable fonts, at
/// two sizes. It holds no equations, so it shows what the layout costs a document that has none.
/// </summary>
[MemoryDiagnoser]
public class DocumentLayoutBenchmarks
{
    private readonly LayoutOptions options = new() { Fonts = FontSettings.Portable };
    private Document document = null!;

    [Params("Small", "Medium")]
    public string Size { get; set; } = "Small";

    [GlobalSetup]
    public void Setup() => document = RtfFormat.Parse(Samples.Rtf(Samples.Sections(Size)));

    [Benchmark]
    public int Layout() => document.Layout(options).Pages.Count;
}
