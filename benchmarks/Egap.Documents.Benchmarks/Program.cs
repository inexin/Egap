using BenchmarkDotNet.Running;

// Run all:      dotnet run -c Release --project benchmarks/Egap.Documents.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egap.Documents.Benchmarks -- --filter "*Markdown*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
