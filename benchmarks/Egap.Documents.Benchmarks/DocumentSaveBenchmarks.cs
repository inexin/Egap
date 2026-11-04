using BenchmarkDotNet.Attributes;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// Writing DOCX and RTF, at three sizes, from the same two documents: the HTML sample's (<see cref="Docx"/>,
/// <see cref="Rtf"/>) and the RTF sample's (<see cref="DocxOfRtf"/>, <see cref="RtfOfRtf"/>).
/// </summary>
[MemoryDiagnoser]
public class DocumentSaveBenchmarks : IDisposable
{
    private readonly MemoryStream output = new();
    private Document html = new();
    private Document rtf = new();

    [ParamsSource(nameof(Sizes))]
    public string Size { get; set; } = "Small";

    public static IEnumerable<string> Sizes => Samples.Sizes;

    [GlobalSetup]
    public void Setup()
    {
        int sections = Samples.Sections(Size);
        html = HtmlFormat.Parse(Samples.Html(sections));
        rtf = RtfFormat.Parse(Samples.Rtf(sections));
    }

    public void Dispose()
    {
        output.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark]
    public long Docx() => SaveDocx(html);

    [Benchmark]
    public long Rtf() => SaveRtf(html);

    [Benchmark]
    public long DocxOfRtf() => SaveDocx(rtf);

    [Benchmark]
    public long RtfOfRtf() => SaveRtf(rtf);

    private long SaveDocx(Document document)
    {
        output.SetLength(0);
        DocxFormat.Save(document, output);
        return output.Length;
    }

    private long SaveRtf(Document document)
    {
        output.SetLength(0);
        RtfFormat.Save(document, output);
        return output.Length;
    }
}
