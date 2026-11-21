using Egap.Documents.Comparing;

namespace Egap.Documents.Tests.Comparing;

public sealed class SequenceDiffTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    [InlineData("", "abc")]
    [InlineData("abc", "")]
    [InlineData("abcabba", "cbabac")]
    [InlineData("kitten", "sitting")]
    [InlineData("the quick brown fox", "the quack brown box")]
    [InlineData("aaaa", "aa")]
    [InlineData("xyz", "abc")]
    public void ScriptTurnsFirstIntoSecond(string a, string b)
    {
        List<DiffStep> script = SequenceDiff.Diff<char>(a, b);

        Assert.Equal(b, Apply(a, b, script));
        Assert.Equal(EditDistance(a, b), script.Where(s => s.Operation != DiffOperation.Equal).Sum(s => s.Count));
    }

    [Fact]
    public void ScriptsAreShortestOnRandomInput()
    {
        Random random = new(7);
        for (int round = 0; round < 300; round++)
        {
            int[] a = [.. Enumerable.Range(0, random.Next(40)).Select(_ => random.Next(4))];
            int[] b = [.. Enumerable.Range(0, random.Next(40)).Select(_ => random.Next(4))];
            List<DiffStep> script = SequenceDiff.Diff<int>(a, b);

            Assert.Equal(b, Apply(a, b, script));
            Assert.Equal(EditDistance(a, b), script.Where(s => s.Operation != DiffOperation.Equal).Sum(s => s.Count));
        }
    }

    [Fact]
    public void StepsAreMergedAndDeletionsComeFirst()
    {
        List<DiffStep> script = SequenceDiff.Diff<char>("a b c", "a x c");

        Assert.Equal(
            [new(DiffOperation.Equal, 2), new(DiffOperation.Delete, 1), new(DiffOperation.Insert, 1), new(DiffOperation.Equal, 2)],
            script);
    }

    [Fact]
    public void WorkLimitGivesUpUnrelatedInputQuickly()
    {
        int[] a = [.. Enumerable.Range(0, 20_000)];
        int[] b = [.. Enumerable.Range(100_000, 20_000)];
        a[10_000] = 5;
        b[10_000] = 5;

        List<DiffStep> script = SequenceDiff.Diff<int>(a, b, out bool limit, workLimit: 100_000);

        Assert.True(limit);
        Assert.Equal(b, Apply(a, b, script));
    }

    [Fact]
    public void LongEqualInputIsCheap()
    {
        int[] a = [.. Enumerable.Range(0, 200_000)];
        int[] b = [.. a];
        b[100_000] = -1;

        List<DiffStep> script = SequenceDiff.Diff<int>(a, b, out bool limit, workLimit: 1000);

        Assert.False(limit);
        Assert.Equal(4, script.Count);
    }

    private static T[] Apply<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, List<DiffStep> script)
    {
        List<T> output = [];
        int i = 0, j = 0;
        foreach (DiffStep step in script)
        {
            switch (step.Operation)
            {
                case DiffOperation.Equal:
                    for (int k = 0; k < step.Count; k++)
                    {
                        Assert.Equal(a[i + k], b[j + k]);
                        output.Add(a[i + k]);
                    }

                    i += step.Count;
                    j += step.Count;
                    break;
                case DiffOperation.Delete:
                    i += step.Count;
                    break;
                default:
                    for (int k = 0; k < step.Count; k++)
                    {
                        output.Add(b[j + k]);
                    }

                    j += step.Count;
                    break;
            }
        }

        Assert.Equal(a.Length, i);
        Assert.Equal(b.Length, j);
        return [.. output];
    }

    private static string Apply(string a, string b, List<DiffStep> script)
    {
        return new string(Apply<char>(a, b, script));
    }

    /// <summary>The fewest insertions and deletions (no substitutions) between two sequences.</summary>
    private static int EditDistance<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
        where T : IEquatable<T>
    {
        int[,] lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                lcs[i, j] = a[i - 1].Equals(b[j - 1]) ? lcs[i - 1, j - 1] + 1 : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
            }
        }

        return a.Length + b.Length - (2 * lcs[a.Length, b.Length]);
    }

    private static int EditDistance(string a, string b)
    {
        return EditDistance<char>(a, b);
    }
}
