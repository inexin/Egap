using System.Text.RegularExpressions;

using Egap.Documents.Fuzz;

using SharpFuzz;

// Instrumented Egap code must not run before Fuzzer.OutOfProcess.Run sets up the shared AFL++ coverage map, so everything
// that touches Egap.Documents runs inside the fuzzed delegate.
//
//   dotnet run --project tests/Egap.Documents.Fuzz -- markdown            run one input from stdin (used by AFL++)
//   dotnet run --project tests/Egap.Documents.Fuzz -- markdown file.md    run one file, to reproduce a crash
//   dotnet run --project tests/Egap.Documents.Fuzz -c Release -- mutate markdown 600 seeds-dir findings-dir
//   dotnet run --project tests/Egap.Documents.Fuzz -c Release -- replay markdown findings-dir
if (args is ["replay", string replayTarget, string directory])
{
    Action<Stream> replay = Targets.Get(replayTarget);
    Dictionary<string, int> failures = [];
    int files = 0;
    foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
    {
        files++;
        try
        {
            using FileStream input = File.OpenRead(path);
            replay(input);
        }
        catch (Exception e)
        {
            string key = $"{e.GetType().Name}: {Regex.Replace(e.Message, "[0-9]+", "#")}";
            key = key[..Math.Min(key.Length, 160)];
            failures[key] = failures.GetValueOrDefault(key) + 1;
            if (failures[key] == 1)
            {
                Console.WriteLine($"{Path.GetFileName(path)}: {e}");
            }
        }
    }

    foreach ((string key, int count) in failures.OrderByDescending(f => f.Value))
    {
        Console.WriteLine($"{count,6}  {key}");
    }

    Console.WriteLine($"{files} files, {failures.Values.Sum()} failures.");
    return failures.Count == 0 ? 0 : 1;
}

if (args is ["mutate", string mutateTarget, string seconds, string seeds, string findings])
{
    return Mutator.Run(Targets.Get(mutateTarget), TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)), seeds, findings);
}

if (args is [string target, string file])
{
    using FileStream input = File.OpenRead(file);
    Targets.Get(target)(input);
    return 0;
}

if (args is [string stdinTarget])
{
    Fuzzer.OutOfProcess.Run(Targets.Get(stdinTarget));
    return 0;
}

Console.Error.WriteLine($"Usage: Egap.Documents.Fuzz <target> [file] | mutate <target> <seconds> <seeds> <findings> | replay <target> <dir>. Targets: {string.Join(", ", Targets.Names)}.");
return 2;
