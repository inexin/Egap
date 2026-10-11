using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

namespace Egap.Pdf.Benchmarks;

/// <summary>Opening a document and reading its catalog: only the cross-reference and the catalog should be read.</summary>
[MemoryDiagnoser]
public class OpenBenchmarks
{
    private byte[] file = [];

    [Params(10, 10_000)]
    public int Objects { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        StringBuilder pdf = new("%PDF-1.7\n");
        List<int> offsets = [];
        for (int i = 1; i <= Objects; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i} 0 obj\n{(i == 1 ? "<< /Type /Catalog >>" : $"<< /Index {i} /Name /Object /Array [1 2 3] >>")}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {Objects + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {Objects + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        file = Encoding.ASCII.GetBytes(pdf.ToString());
    }

    [Benchmark]
    public int OpenAndReadCatalog()
    {
        using PdfDocument pdf = PdfDocument.Open(file);
        return pdf.Catalog.Count;
    }

    [Benchmark]
    public int OpenAndLoadEverything()
    {
        using PdfDocument pdf = PdfDocument.Open(file);
        int count = 0;
        foreach (PdfReference reference in pdf.Objects)
        {
            count += pdf.Objects[reference] is PdfDictionary ? 1 : 0;
        }

        return count;
    }
}
