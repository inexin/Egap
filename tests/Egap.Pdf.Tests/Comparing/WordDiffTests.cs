using Egap.Pdf.Comparing;

namespace Egap.Pdf.Tests.Comparing;

/// <summary>The Myers diff under compare: Phase 7 task 14.</summary>
public sealed class WordDiffTests
{
    [Fact]
    public void ScriptsAreShortestAndRebuildTheNewSequence()
    {
        Random random = new(14);
        for (int round = 0; round < 400; round++)
        {
            int[] a = [.. Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(6))];
            int[] b = random.Next(3) == 0 ? [.. Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(6))] : Edited(a, random);

            List<(WordDiff.Step Step, int Count)> script = WordDiff.Diff(a, b);

            List<int> rebuilt = [];
            int i = 0, edits = 0;
            foreach ((WordDiff.Step step, int count) in script)
            {
                switch (step)
                {
                    case WordDiff.Step.Equal:
                        rebuilt.AddRange(a.Skip(i).Take(count));
                        i += count;
                        break;
                    case WordDiff.Step.Delete:
                        i += count;
                        edits += count;
                        break;
                    default:
                        rebuilt.AddRange(b.Skip(rebuilt.Count).Take(count));
                        edits += count;
                        break;
                }
            }

            Assert.Equal(a.Length, i);
            Assert.Equal(b, rebuilt);
            Assert.Equal(a.Length + b.Length - (2 * Lcs(a, b)), edits);
        }
    }

    private static int[] Edited(int[] a, Random random)
    {
        List<int> b = [.. a];
        for (int e = random.Next(0, 6); e > 0; e--)
        {
            int at = random.Next(b.Count + 1);
            switch (random.Next(3))
            {
                case 0:
                    b.Insert(at, random.Next(6));
                    break;
                case 1 when b.Count > 0:
                    b.RemoveAt(Math.Min(at, b.Count - 1));
                    break;
                default:
                    if (b.Count > 0)
                    {
                        b[Math.Min(at, b.Count - 1)] = random.Next(6);
                    }

                    break;
            }
        }

        return [.. b];
    }

    private static int Lcs(int[] a, int[] b)
    {
        int[,] table = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[a.Length, b.Length];
    }
}
