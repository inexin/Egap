using BenchmarkDotNet.Running;

// Run all:      dotnet run -c Release --project benchmarks/Egap.Pdf.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egap.Pdf.Benchmarks -- --filter "*Scaffold*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
