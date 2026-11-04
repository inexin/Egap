using BenchmarkDotNet.Running;

// Run all:      dotnet run -c Release --project benchmarks/Egap.Spreadsheets.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egap.Spreadsheets.Benchmarks -- --filter "*CellStore*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
