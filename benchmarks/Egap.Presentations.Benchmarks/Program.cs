using BenchmarkDotNet.Running;

// Run all:      dotnet run -c Release --project benchmarks/Egap.Presentations.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egap.Presentations.Benchmarks -- --filter "*Pptx*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
