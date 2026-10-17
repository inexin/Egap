namespace Egap.Pdf.Comparing;

/// <summary>
/// A shortest edit script between two sequences (E. W. Myers, "An O(ND) Difference Algorithm and Its Variations", 1986):
/// the linear-space version that finds the middle snake of each part and recurses on both sides, after the common head and
/// tail are cut off. Items are compared as integers (words interned to ids), so the work is O((N + M) D) with D edits.
/// </summary>
internal static class WordDiff
{
    /// <summary>The kinds of step in an edit script.</summary>
    internal enum Step
    {
        Equal,
        Delete,
        Insert,
    }

    /// <summary>The edit script from <paramref name="a"/> to <paramref name="b"/>: each step and how many items it covers.</summary>
    public static List<(Step Step, int Count)> Diff(int[] a, int[] b)
    {
        List<(Step, int)> script = [];
        Recurse(a, 0, a.Length, b, 0, b.Length, script, new int[2 * (a.Length + b.Length + 2)], new int[2 * (a.Length + b.Length + 2)]);
        return Merge(script);
    }

    private static void Recurse(int[] a, int a0, int a1, int[] b, int b0, int b1, List<(Step, int)> script, int[] forward, int[] backward)
    {
        // The common head and tail are equal steps.
        int head = 0;
        while (a0 + head < a1 && b0 + head < b1 && a[a0 + head] == b[b0 + head])
        {
            head++;
        }

        int tail = 0;
        while (a1 - tail > a0 + head && b1 - tail > b0 + head && a[a1 - tail - 1] == b[b1 - tail - 1])
        {
            tail++;
        }

        if (head > 0)
        {
            script.Add((Step.Equal, head));
        }

        a0 += head;
        b0 += head;
        a1 -= tail;
        b1 -= tail;
        if (a0 == a1)
        {
            if (b1 > b0)
            {
                script.Add((Step.Insert, b1 - b0));
            }
        }
        else if (b0 == b1)
        {
            script.Add((Step.Delete, a1 - a0));
        }
        else
        {
            (int x, int y) = MiddleSnake(a, a0, a1, b, b0, b1, forward, backward);
            Recurse(a, a0, x, b, b0, y, script, forward, backward);
            Recurse(a, x, a1, b, y, b1, script, forward, backward);
        }

        if (tail > 0)
        {
            script.Add((Step.Equal, tail));
        }
    }

    /// <summary>
    /// A point the shortest edit path passes through, near its middle, found by running the greedy search from both ends at
    /// once until the paths overlap.
    /// </summary>
    private static (int X, int Y) MiddleSnake(int[] a, int a0, int a1, int[] b, int b0, int b1, int[] forward, int[] backward)
    {
        int n = a1 - a0, m = b1 - b0, delta = n - m, max = (n + m + 1) / 2, offset = max + 1;
        bool odd = (delta & 1) != 0;
        Array.Fill(forward, -1, 0, (2 * max) + 3);
        Array.Fill(backward, -1, 0, (2 * max) + 3);
        forward[offset + 1] = 0;
        backward[offset + 1] = 0;
        for (int d = 0; d <= max; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && forward[offset + k - 1] < forward[offset + k + 1]) ? forward[offset + k + 1] : forward[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[a0 + x] == b[b0 + y])
                {
                    x++;
                    y++;
                }

                forward[offset + k] = x;
                int c = delta - k;
                if (odd && c >= -(d - 1) && c <= d - 1 && x + backward[offset + c] >= n)
                {
                    return (a0 + x, b0 + y);
                }
            }

            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && backward[offset + k - 1] < backward[offset + k + 1]) ? backward[offset + k + 1] : backward[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[a1 - x - 1] == b[b1 - y - 1])
                {
                    x++;
                    y++;
                }

                backward[offset + k] = x;
                int c = delta - k;
                if (!odd && c >= -d && c <= d && x + forward[offset + c] >= n)
                {
                    return (a1 - x, b1 - y);
                }
            }
        }

        // Not reached: a path of at most n + m edits always exists.
        return (a0 + (n / 2), b0 + (m / 2));
    }

    private static List<(Step, int)> Merge(List<(Step Step, int Count)> script)
    {
        List<(Step, int)> merged = [];
        foreach ((Step step, int count) in script)
        {
            if (count == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1].Item1 == step)
            {
                merged[^1] = (step, merged[^1].Item2 + count);
            }
            else
            {
                merged.Add((step, count));
            }
        }

        return merged;
    }
}
