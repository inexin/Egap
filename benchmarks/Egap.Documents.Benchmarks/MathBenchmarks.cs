using BenchmarkDotNet.Attributes;

using Egap.Documents.Html;
using Egap.Documents.Layout;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// Equations: <see cref="Layout"/> lays out a page of 40 Word equations (fractions, radicals, scripts, sums, delimiters and a
/// matrix, half inline and half display, one long enough to break); <see cref="ParseMathML"/> reads the same equations as an
/// HTML page of MathML.
/// </summary>
[MemoryDiagnoser]
public class MathBenchmarks
{
    private const string M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    private static readonly string[] Equations =
    [
        "<m:f><m:num><m:r><m:t>-b\u00B1</m:t></m:r><m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e><m:sSup><m:e><m:r><m:t>b</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup><m:r><m:t>-4ac</m:t></m:r></m:e></m:rad></m:num><m:den><m:r><m:t>2a</m:t></m:r></m:den></m:f>",
        "<m:nary><m:naryPr><m:chr m:val=\"\u2211\"/></m:naryPr><m:sub><m:r><m:t>i=1</m:t></m:r></m:sub><m:sup><m:r><m:t>n</m:t></m:r></m:sup><m:e><m:sSup><m:e><m:r><m:t>i</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:e></m:nary>",
        "<m:d><m:e><m:m><m:mr><m:e><m:r><m:t>a</m:t></m:r></m:e><m:e><m:r><m:t>b</m:t></m:r></m:e></m:mr><m:mr><m:e><m:r><m:t>c</m:t></m:r></m:e><m:e><m:r><m:t>d</m:t></m:r></m:e></m:mr></m:m></m:e></m:d>",
        "<m:r><m:t>" + string.Join("+", Enumerable.Range(1, 40).Select(i => "x" + i)) + "</m:t></m:r>",
    ];

    private readonly LayoutOptions options = new() { Fonts = FontSettings.Portable };
    private string html = "";

    [GlobalSetup]
    public void Setup() => html = HtmlFormat.ToHtml(Build(), out _);

    [Benchmark]
    public int Layout() => Build().Layout(options).Pages.Count;

    [Benchmark]
    public int ParseMathML() => HtmlFormat.Parse(html).Sections.Count;

    private static Document Build()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        for (int i = 0; i < 40; i++)
        {
            string content = Equations[i % Equations.Length];
            string markup = i % 2 == 0
                ? $"<m:oMath xmlns:m=\"{M}\">{content}</m:oMath>"
                : $"<m:oMathPara xmlns:m=\"{M}\"><m:oMath>{content}</m:oMath></m:oMathPara>";
            Paragraph paragraph = body.AddParagraph("Equation ");
            paragraph.Inlines.Add(new Island(document, IslandKind.Math, "docx", markup));
        }

        return document;
    }
}
