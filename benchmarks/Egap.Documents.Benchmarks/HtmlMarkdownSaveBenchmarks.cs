using BenchmarkDotNet.Attributes;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// Writing HTML and Markdown, at three sizes, from the HTML sample's document: as it is, and with every fourth run marked
/// inserted and every seventh deleted (<see cref="HtmlTracked"/>, <see cref="MarkdownTracked"/>), written in the default
/// view, as if accepted.
/// </summary>
[MemoryDiagnoser]
public class HtmlMarkdownSaveBenchmarks
{
    private Document plain = new();
    private Document tracked = new();

    [ParamsSource(nameof(Sizes))]
    public string Size { get; set; } = "Small";

    public static IEnumerable<string> Sizes => Samples.Sizes;

    [GlobalSetup]
    public void Setup()
    {
        plain = HtmlFormat.Parse(Samples.Html(Samples.Sections(Size)));
        tracked = plain.Clone();
        int index = 0;
        foreach (Run run in tracked.Sections.SelectMany(s => s.Body.Descendants<Run>()).ToList())
        {
            if (index % 4 == 0)
            {
                run.Revision = new Revision(RevisionKind.Insertion, "Bench");
            }
            else if (index % 7 == 0)
            {
                run.Revision = new Revision(RevisionKind.Deletion, "Bench");
            }

            index++;
        }
    }

    [Benchmark]
    public int Html() => plain.ToHtml().Length;

    [Benchmark]
    public int HtmlTracked() => tracked.ToHtml().Length;

    [Benchmark]
    public int Markdown() => plain.ToMarkdown().Length;

    [Benchmark]
    public int MarkdownTracked() => tracked.ToMarkdown().Length;
}
