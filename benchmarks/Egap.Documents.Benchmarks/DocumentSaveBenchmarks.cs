using BenchmarkDotNet.Attributes;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// Writing DOCX, RTF and ODT, at three sizes, from the same three documents: the HTML sample's (<see cref="Docx"/>,
/// <see cref="Rtf"/>, <see cref="Odt"/>), the RTF sample's (<see cref="DocxOfRtf"/>, <see cref="RtfOfRtf"/>) and the ODT
/// sample's (<see cref="DocxOfOdt"/>, <see cref="OdtOfOdt"/>, which writes back the markup the reader kept).
/// </summary>
[MemoryDiagnoser]
public class DocumentSaveBenchmarks : IDisposable
{
    private readonly MemoryStream output = new();
    private Document html = new();
    private Document rtf = new();
    private Document odt = new();

    [ParamsSource(nameof(Sizes))]
    public string Size { get; set; } = "Small";

    public static IEnumerable<string> Sizes => Samples.Sizes;

    [GlobalSetup]
    public void Setup()
    {
        int sections = Samples.Sections(Size);
        html = HtmlFormat.Parse(Samples.Html(sections));
        rtf = RtfFormat.Parse(Samples.Rtf(sections));
        odt = OdtFormat.Load(new MemoryStream(Samples.Odt(sections)));
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

    [Benchmark]
    public long Odt() => SaveOdt(html);

    [Benchmark]
    public long DocxOfOdt() => SaveDocx(odt);

    [Benchmark]
    public long OdtOfOdt() => SaveOdt(odt);

    private long SaveDocx(Document document)
    {
        output.SetLength(0);
        DocxFormat.Save(document, output);
        return output.Length;
    }

    private long SaveOdt(Document document)
    {
        output.SetLength(0);
        OdtFormat.Save(document, output);
        return output.Length;
    }

    private long SaveRtf(Document document)
    {
        output.SetLength(0);
        RtfFormat.Save(document, output);
        return output.Length;
    }
}
