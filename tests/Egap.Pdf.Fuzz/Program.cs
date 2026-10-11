using System.Text.RegularExpressions;

using Egap.Pdf;
using Egap.Pdf.Fuzz;

using SharpFuzz;

// Instrumented Egap code must not run before Fuzzer.OutOfProcess.Run sets up the shared AFL++ coverage map, so everything
// that touches Egap.Pdf runs inside the fuzzed delegate.
//
// Fuzz targets, one per reader (see Targets.cs). A lenient read must never throw anything but a PdfLimitException; a strict
// read may throw only PdfException. Any other exception is a crash, and AFL++ reports hangs.
//
//   dotnet run --project tests/Egap.Pdf.Fuzz -- lexer            run one input from stdin (used by AFL++ through SharpFuzz)
//   dotnet run --project tests/Egap.Pdf.Fuzz -- lexer file.pdf   run one file, to reproduce a crash
//   dotnet run --project tests/Egap.Pdf.Fuzz -c Release -- mutate lexer 600 seeds-dir findings-dir
//       mutation fuzzing without AFL++ (any OS): random edits of the seeds for the given seconds; crashes and inputs that take
//       over 10 seconds are saved to the findings directory. Not coverage-guided, so a complement to AFL++, not a replacement.
//   dotnet run --project tests/Egap.Pdf.Fuzz -c Release -- replay lexer findings-dir
//       runs every file in a directory tree and counts the failures by message, to triage a run's findings
if (args is ["replay", string replayTarget, string directory])
{
    Action<Stream> replay = Targets.Get(replayTarget);
    Dictionary<string, int> failures = [];
    int files = 0;
    foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
    {
        files++;
        string? watch = Environment.GetEnvironmentVariable("EGAP_REPLAY_WATCH");
        int watched = watch is null ? 0 : Targets.Counts.Where(c => c.Key.Contains(watch, StringComparison.Ordinal)).Sum(c => c.Value);
        try
        {
            using FileStream input = File.OpenRead(path);
            replay(input);
            if (watch is not null && Targets.Counts.Where(c => c.Key.Contains(watch, StringComparison.Ordinal)).Sum(c => c.Value) > watched)
            {
                // EGAP_REPLAY_WATCH names a count to follow: list the files that added to it.
                Console.WriteLine($"watch: {path}");
            }
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

    foreach ((string key, int count) in Targets.Counts.OrderBy(c => c.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"{count,9}  {key}");
    }

    Console.WriteLine($"{files} files, {failures.Values.Sum()} failures.");
    return failures.Count == 0 ? 0 : 1;
}

if (args is ["mutate", string mutateTarget, string seconds, string seeds, string findings])
{
    return Mutator.Run(Targets.Get(mutateTarget), TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)), seeds, findings);
}

if (args is ["list-objects", string listed])
{
    // Prints "n g" for every object the cross-reference lists that loads to something other than null, for comparing with qpdf.
    foreach (string line in Targets.ListObjects(File.ReadAllBytes(listed)))
    {
        Console.WriteLine(line);
    }

    return 0;
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

Console.Error.WriteLine($"Usage: Egap.Pdf.Fuzz <target> [file] | mutate <target> <seconds> <seeds> <findings> | replay <target> <dir>. Targets: {string.Join(", ", Targets.Names)}.");
return 2;
