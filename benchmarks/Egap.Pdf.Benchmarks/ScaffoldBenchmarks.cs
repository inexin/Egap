using BenchmarkDotNet.Attributes;

namespace Egap.Pdf.Benchmarks;

// Scaffold placeholder that proves the benchmark project runs. Replace with real benchmarks once there is PDF code.
[MemoryDiagnoser]
public class ScaffoldBenchmarks
{
    private readonly string name = EgapPdfInfo.Name;

    [Benchmark]
    public int NameLength()
    {
        return name.Length;
    }
}
