using System.Diagnostics;

namespace Egap.Documents.Fuzz;

/// <summary>
/// A small mutation fuzzer for machines without AFL++: it applies random edits to the seeds and runs the target in process.
/// Not coverage-guided, so a complement to AFL++, not a replacement.
/// </summary>
internal static class Mutator
{
    private static readonly byte[][] Tokens =
    [
        "*"u8.ToArray(), "**"u8.ToArray(), "_"u8.ToArray(), "~~"u8.ToArray(), "`"u8.ToArray(), "```"u8.ToArray(),
        "["u8.ToArray(), "]("u8.ToArray(), ")"u8.ToArray(), "!["u8.ToArray(), "<"u8.ToArray(), ">"u8.ToArray(),
        "</"u8.ToArray(), "/>"u8.ToArray(), "&amp;"u8.ToArray(), "&#"u8.ToArray(), "\\"u8.ToArray(), "\n"u8.ToArray(),
        "\n\n"u8.ToArray(), "    "u8.ToArray(), "- "u8.ToArray(), "1. "u8.ToArray(), "> "u8.ToArray(),
        "# "u8.ToArray(), "| "u8.ToArray(), "|---|"u8.ToArray(), "[^1]"u8.ToArray(), "[^1]: "u8.ToArray(),
        "<!--"u8.ToArray(), "-->"u8.ToArray(), "<w:p>"u8.ToArray(), "</w:p>"u8.ToArray(), "<w:r><w:t>"u8.ToArray(),
        "</w:t></w:r>"u8.ToArray(), "xmlns:w=\""u8.ToArray(), "\""u8.ToArray(), "'"u8.ToArray(), "="u8.ToArray(),
        "PK\u0003\u0004"u8.ToArray(), "<table>"u8.ToArray(), "<td>"u8.ToArray(), "<script>"u8.ToArray(),
        "<p>"u8.ToArray(), "&nbsp;"u8.ToArray(),
    ];

    public static int Run(Action<Stream> target, TimeSpan duration, string seedDirectory, string findingDirectory)
    {
        List<byte[]> seeds = [.. Directory.GetFiles(seedDirectory).Select(File.ReadAllBytes)];
        if (seeds.Count == 0)
        {
            seeds.Add([]);
        }

        Directory.CreateDirectory(findingDirectory);
        Random random = new();
        Stopwatch clock = Stopwatch.StartNew();
        long runs = 0;
        int crashes = 0;
        while (clock.Elapsed < duration)
        {
            byte[] input = Mutate(seeds[random.Next(seeds.Count)], seeds, random);
            Stopwatch run = Stopwatch.StartNew();
            try
            {
                target(new MemoryStream(input, writable: false));
            }
            catch (Exception e)
            {
                crashes++;
                string name = $"crash-{e.GetType().Name}-{runs}.bin";
                File.WriteAllBytes(Path.Combine(findingDirectory, name), input);
                Console.WriteLine($"{name}: {e.GetType().Name}: {e.Message}");
            }

            if (run.Elapsed > TimeSpan.FromSeconds(10))
            {
                File.WriteAllBytes(Path.Combine(findingDirectory, $"slow-{runs}.bin"), input);
                Console.WriteLine($"slow-{runs}.bin: {run.Elapsed.TotalSeconds:F1} s");
            }

            runs++;
        }

        Console.WriteLine($"{runs} runs in {clock.Elapsed.TotalSeconds:F0} s, {crashes} crashes.");
        return crashes == 0 ? 0 : 1;
    }

    private static byte[] Mutate(byte[] seed, List<byte[]> seeds, Random random)
    {
        List<byte> bytes = [.. seed];
        int edits = 1 + random.Next(8);
        for (int i = 0; i < edits; i++)
        {
            int at = bytes.Count == 0 ? 0 : random.Next(bytes.Count + 1);
            switch (random.Next(7))
            {
                case 0 when bytes.Count > 0:
                    bytes[Math.Min(at, bytes.Count - 1)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when bytes.Count > 0:
                    bytes[Math.Min(at, bytes.Count - 1)] = (byte)random.Next(256);
                    break;
                case 2 when bytes.Count > 0:
                    bytes.RemoveRange(Math.Min(at, bytes.Count - 1), Math.Min(1 + random.Next(16), bytes.Count - Math.Min(at, bytes.Count - 1)));
                    break;
                case 3:
                    bytes.InsertRange(at, Tokens[random.Next(Tokens.Length)]);
                    break;
                case 4 when bytes.Count > 0:
                    int from = random.Next(bytes.Count);
                    int count = Math.Min(1 + random.Next(64), bytes.Count - from);
                    bytes.InsertRange(at, bytes.GetRange(from, count));
                    break;
                case 5:
                    byte[] other = seeds[random.Next(seeds.Count)];
                    if (other.Length > 0)
                    {
                        int start = random.Next(other.Length);
                        bytes.InsertRange(at, other.AsSpan(start, Math.Min(1 + random.Next(256), other.Length - start)).ToArray());
                    }

                    break;
                default:
                    if (bytes.Count > 0)
                    {
                        bytes.RemoveRange(at == bytes.Count ? at - 1 : at, bytes.Count - (at == bytes.Count ? at - 1 : at));
                    }

                    break;
            }
        }

        return [.. bytes];
    }
}
