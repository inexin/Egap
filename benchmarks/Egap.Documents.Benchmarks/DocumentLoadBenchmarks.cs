using System.Text;

using BenchmarkDotNet.Attributes;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// Reading DOCX and RTF, at three sizes. <see cref="Docx"/> reads the HTML sample saved as DOCX; <see cref="Rtf"/> reads the
/// same mix written as RTF; <see cref="DocxOfRtf"/> reads the RTF sample's document saved as DOCX, the same document as RTF;
/// <see cref="EncryptedDocx"/> reads the DOCX sample saved with a password, so the difference to <see cref="Docx"/> is the
/// key spins and the decryption.
/// </summary>
[MemoryDiagnoser]
public class DocumentLoadBenchmarks
{
    private byte[] docx = [];
    private byte[] rtf = [];
    private byte[] docxOfRtf = [];
    private byte[] encrypted = [];

    [ParamsSource(nameof(Sizes))]
    public string Size { get; set; } = "Small";

    public static IEnumerable<string> Sizes => Samples.Sizes;

    [GlobalSetup]
    public void Setup()
    {
        int sections = Samples.Sections(Size);
        docx = SaveDocx(HtmlFormat.Parse(Samples.Html(sections)));
        string text = Samples.Rtf(sections);
        rtf = Encoding.Latin1.GetBytes(text);
        docxOfRtf = SaveDocx(RtfFormat.Parse(text));
        using MemoryStream stream = new();
        DocxFormat.Save(DocxFormat.Load(new MemoryStream(docx)), stream, new DocxSaveOptions { Password = "benchmark" });
        encrypted = stream.ToArray();
    }

    [Benchmark]
    public int Docx()
    {
        using MemoryStream stream = new(docx, writable: false);
        return DocxFormat.Load(stream).Sections.Count;
    }

    [Benchmark]
    public int Rtf()
    {
        using MemoryStream stream = new(rtf, writable: false);
        return RtfFormat.Load(stream).Sections.Count;
    }

    [Benchmark]
    public int DocxOfRtf()
    {
        using MemoryStream stream = new(docxOfRtf, writable: false);
        return DocxFormat.Load(stream).Sections.Count;
    }

    [Benchmark]
    public int EncryptedDocx()
    {
        using MemoryStream stream = new(encrypted, writable: false);
        return DocxFormat.Load(stream, new DocxLoadOptions { Password = "benchmark" }).Sections.Count;
    }

    private static byte[] SaveDocx(Document document)
    {
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        return stream.ToArray();
    }
}
