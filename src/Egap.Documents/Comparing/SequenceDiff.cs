namespace Egap.Documents.Comparing;

/// <summary>What one step of an edit script does.</summary>
internal enum DiffOperation
{
    /// <summary>The items are in both sequences.</summary>
    Equal,

    /// <summary>The items are only in the first sequence.</summary>
    Delete,

    /// <summary>The items are only in the second sequence.</summary>
    Insert,
}

/// <summary>One step of an edit script: what it does and how many items it covers.</summary>
internal readonly record struct DiffStep(DiffOperation Operation, int Count);

/// <summary>
/// A shortest edit script between two sequences, after E. W. Myers, "An O(ND) Difference Algorithm and Its Variations"
/// (Algorithmica 1, 1986): the linear-space variant, which finds the middle snake of a region by searching from both ends at
/// once and then solves the two halves, with the common head and tail of each region cut off first. The work is
/// O((N + M) D) for D edits. A work limit keeps unrelated inputs fast: a region the search cannot finish within it becomes
/// one deletion and one insertion.
/// </summary>
internal static class SequenceDiff
{
    /// <summary>The default work limit, in diagonal steps of the search (about a tenth of a second).</summary>
    public const long DefaultWorkLimit = 1L << 26;

    /// <summary>The edit script that turns <paramref name="a"/> into <paramref name="b"/>.</summary>
    /// <param name="a">The first sequence.</param>
    /// <param name="b">The second sequence.</param>
    /// <param name="limitReached">Whether some region was given up as one deletion and one insertion.</param>
    /// <param name="workLimit">How many diagonal steps the search may take in all.</param>
    public static List<DiffStep> Diff<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, out bool limitReached, long workLimit = DefaultWorkLimit)
        where T : IEquatable<T>
    {
        Search<T> search = new(a.ToArray(), b.ToArray(), workLimit);
        search.Run();
        limitReached = search.LimitReached;
        return search.Script;
    }

    /// <summary>The edit script that turns <paramref name="a"/> into <paramref name="b"/>, with no work limit.</summary>
    public static List<DiffStep> Diff<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
        where T : IEquatable<T>
    {
        return Diff(a, b, out _, long.MaxValue);
    }

    private sealed class Search<T>(T[] a, T[] b, long workLimit)
        where T : IEquatable<T>
    {
        private readonly int[] forward = new int[a.Length + b.Length + 5];
        private readonly int[] backward = new int[a.Length + b.Length + 5];
        private long work;

        public List<DiffStep> Script { get; } = [];

        public bool LimitReached { get; private set; }

        public void Run()
        {
            // Regions still to solve, as an explicit stack so deep inputs cannot overflow the call stack. Each entry is a region
            // or a step already known; the halves of a split region are pushed in reverse so they come out in order.
            Stack<(int A0, int A1, int B0, int B1, DiffOperation Known, int Count)> pending = [];
            pending.Push((0, a.Length, 0, b.Length, default, -1));
            while (pending.Count > 0)
            {
                (int a0, int a1, int b0, int b1, DiffOperation known, int count) = pending.Pop();
                if (count >= 0)
                {
                    Add(known, count);
                    continue;
                }

                int head = 0;
                while (a0 + head < a1 && b0 + head < b1 && a[a0 + head].Equals(b[b0 + head]))
                {
                    head++;
                }

                int tail = 0;
                while (a1 - tail > a0 + head && b1 - tail > b0 + head && a[a1 - tail - 1].Equals(b[b1 - tail - 1]))
                {
                    tail++;
                }

                Add(DiffOperation.Equal, head);
                a0 += head;
                b0 += head;
                a1 -= tail;
                b1 -= tail;
                if (tail > 0)
                {
                    pending.Push((0, 0, 0, 0, DiffOperation.Equal, tail));
                }

                if (a0 == a1 || b0 == b1)
                {
                    Add(DiffOperation.Delete, a1 - a0);
                    Add(DiffOperation.Insert, b1 - b0);
                    continue;
                }

                if (MiddleSnake(a0, a1, b0, b1) is not (int x, int y) || (x == a0 && y == b0) || (x == a1 && y == b1))
                {
                    // Over the work limit (or a split that would not shrink the region): give the region up whole.
                    LimitReached |= work > workLimit;
                    Add(DiffOperation.Delete, a1 - a0);
                    Add(DiffOperation.Insert, b1 - b0);
                    continue;
                }

                pending.Push((x, a1, y, b1, default, -1));
                pending.Push((a0, x, b0, y, default, -1));
            }
        }

        private void Add(DiffOperation operation, int count)
        {
            if (count == 0)
            {
                return;
            }

            if (Script.Count > 0 && Script[^1].Operation == operation)
            {
                Script[^1] = new DiffStep(operation, Script[^1].Count + count);
            }
            else if (operation == DiffOperation.Delete && Script.Count > 0 && Script[^1].Operation == DiffOperation.Insert)
            {
                // Deletions go before insertions in a replaced stretch, so the script reads the same however it was split.
                if (Script.Count > 1 && Script[^2].Operation == DiffOperation.Delete)
                {
                    Script[^2] = new DiffStep(DiffOperation.Delete, Script[^2].Count + count);
                }
                else
                {
                    Script.Insert(Script.Count - 1, new DiffStep(DiffOperation.Delete, count));
                }
            }
            else
            {
                Script.Add(new DiffStep(operation, count));
            }
        }

        /// <summary>
        /// A point the shortest edit path of the region goes through, near its middle, or null when the work limit runs out.
        /// The two searches walk diagonals k = x - y; forward[k] is the furthest x reached from the start on diagonal k, and
        /// backward[k] the furthest distance walked back from the end on diagonal k of the reversed region.
        /// </summary>
        private (int X, int Y)? MiddleSnake(int a0, int a1, int b0, int b1)
        {
            int n = a1 - a0, m = b1 - b0, delta = n - m, max = (n + m + 1) / 2, offset = max + 1;
            bool odd = (delta & 1) != 0;
            Array.Fill(forward, -1, 0, (2 * max) + 3);
            Array.Fill(backward, -1, 0, (2 * max) + 3);
            forward[offset + 1] = 0;
            backward[offset + 1] = 0;
            for (int d = 0; d <= max; d++)
            {
                work += (2 * d) + 2;
                if (work > workLimit)
                {
                    return null;
                }

                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && forward[offset + k - 1] < forward[offset + k + 1]) ? forward[offset + k + 1] : forward[offset + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && a[a0 + x].Equals(b[b0 + y]))
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
                    while (x < n && y < m && a[a1 - x - 1].Equals(b[b1 - y - 1]))
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

            return null;
        }
    }
}
