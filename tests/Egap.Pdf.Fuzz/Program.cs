using Egap.Pdf;

using SharpFuzz;

// Instrumented Egap code must not run before Fuzzer.OutOfProcess.Run sets up the shared AFL++ coverage map, so everything
// that touches Egap.Pdf runs inside the fuzzed delegate.
//
// Fuzz targets, one per reader. Each must handle any input or fail with Egap's own exception type: any other exception is a
// crash, and AFL++ reports hangs. The only target so far is "noop", which reads the input and touches the assembly; real
// targets come with the parsers.
//
//   dotnet run --project tests/Egap.Pdf.Fuzz -- noop            run one input from stdin (used by AFL++ through SharpFuzz)
//   dotnet run --project tests/Egap.Pdf.Fuzz -- noop file.pdf   run one file, to reproduce a crash
if (args is [string target, string file])
{
    using FileStream input = File.OpenRead(file);
    Target(target)(input);
    return 0;
}

if (args is [string stdinTarget])
{
    Fuzzer.OutOfProcess.Run(Target(stdinTarget));
    return 0;
}

Console.Error.WriteLine("Usage: Egap.Pdf.Fuzz <target> [file]. Targets: noop.");
return 2;

static Action<Stream> Target(string name)
{
    return name switch
    {
        "noop" => Noop,
        _ => throw new ArgumentException($"Unknown fuzz target '{name}'.", nameof(name)),
    };
}

static void Noop(Stream input)
{
    input.CopyTo(Stream.Null);
    _ = EgapPdfInfo.Name;
}
