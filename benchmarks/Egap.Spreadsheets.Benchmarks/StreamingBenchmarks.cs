using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Streaming 100,000 rows of three cells (text, a date and a number) through <see cref="SheetWriter"/> and
/// <see cref="SheetReader"/>, as XLSX and as CSV: the baselines for the streaming API.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs instance methods.")]
public class StreamingBenchmarks
{
    private const int Rows = 100_000;

    private byte[] xlsx = [];
    private byte[] csv = [];

    [GlobalSetup]
    public void Setup()
    {
        xlsx = Write(SheetWriter.Create);
        csv = Write(stream => SheetWriter.CreateCsv(stream));
    }

    [Benchmark(Description = "SheetWriter XLSX 100k rows")]
    public int WriteXlsx() => Write(SheetWriter.Create).Length;

    [Benchmark(Description = "SheetReader XLSX 100k rows")]
    public double ReadXlsx() => Read(SheetReader.Open(new MemoryStream(xlsx, writable: false)));

    [Benchmark(Description = "SheetWriter CSV 100k rows")]
    public int WriteCsv() => Write(stream => SheetWriter.CreateCsv(stream)).Length;

    [Benchmark(Description = "SheetReader CSV 100k rows")]
    public double ReadCsv() => Read(SheetReader.OpenCsv(new MemoryStream(csv, writable: false)));

    private static byte[] Write(Func<Stream, SheetWriter> create)
    {
        using MemoryStream stream = new();
        using (SheetWriter writer = create(stream))
        {
            writer.SetColumnFormat(1, "yyyy-mm-dd");
            for (int i = 0; i < Rows; i++)
            {
                writer.WriteRow("order " + (i % 1000), 45000 + (i % 365), i * 0.25);
            }
        }

        return stream.ToArray();
    }

    private static double Read(SheetReader reader)
    {
        using (reader)
        {
            double sum = 0;
            while (reader.Read())
            {
                sum += reader[2].Number;
            }

            return sum;
        }
    }
}
